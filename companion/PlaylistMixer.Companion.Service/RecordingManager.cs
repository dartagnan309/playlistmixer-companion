using System.Text.Json;
using PlaylistMixer.Companion.Service.Platform;
using PlaylistMixer.Playback;

namespace PlaylistMixer.Companion.Service;

/// <summary>
/// Owns the persistent recordings catalog (JSON sidecars beside the .mp4 files), the FFmpeg recorder
/// processes, and a 15 s scheduler that starts due recordings, finalizes finished ones, and fails
/// missed ones. Registered as a singleton and instantiated at startup so the scheduler runs without a
/// request. Connection-limit checks run under a start-lock (the pattern <see cref="DvrManager"/> uses).
/// </summary>
public sealed class RecordingManager : IDisposable
{
    private readonly DvrOptions _opts;
    private readonly string _ffmpegPath;
    private readonly ILogger<RecordingManager> _log;
    private readonly string _dir;
    private readonly object _gate = new();                 // guards _index + _running + sidecar writes
    private readonly SemaphoreSlim _startLock = new(1, 1); // guards count-limit + start
    private readonly Dictionary<string, Recording> _index = new();
    private readonly Dictionary<string, StreamRecorder> _running = new();
    private readonly Timer _timer;

    public RecordingManager(DvrOptions opts, string ffmpegPath, IPlatform platform, ILogger<RecordingManager> log)
    {
        _opts = opts;
        _ffmpegPath = ffmpegPath;
        _log = log;
        _dir = opts.ResolveRecordingsDir(platform.DefaultRecordingsDir);

        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        if (_dir.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Dvr:RecordingsDir must be a persistent path, not under TEMP: {_dir}");
        Directory.CreateDirectory(_dir);

        LoadAndReconcile();
        _timer = new Timer(_ => SafeTick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
        _log.LogInformation("RecordingManager ready: dir={Dir} ({N} recordings)", _dir, _index.Count);
    }

    /// <summary>The resolved on-disk directory where recordings (and their JSON sidecars) live.
    /// Surfaced via /health so the tray (which runs as the user, not the service's LocalSystem
    /// account) can open the exact folder rather than guessing the path.</summary>
    public string Dir => _dir;

    public int ActiveCount { get { lock (_gate) return _index.Values.Count(r => r.Status == RecordingStatus.Recording); } }
    public int ScheduledCount { get { lock (_gate) return _index.Values.Count(r => r.Status == RecordingStatus.Scheduled); } }

    /// <summary>All recordings, newest first, with live byte sizes for in-progress entries.</summary>
    public List<Recording> List()
    {
        lock (_gate)
        {
            foreach (var r in _index.Values)
                if (r.Status == RecordingStatus.Recording && _running.TryGetValue(r.Id, out var rec))
                {
                    r.FileBytes = rec.FileBytes;
                    r.DurationSeconds = (int)Math.Round(rec.RecordedSeconds);
                }
            return _index.Values.OrderByDescending(r => r.CreatedUtc).ToList();
        }
    }

    public Recording? Get(string id) { lock (_gate) return _index.GetValueOrDefault(id); }

    /// <summary>Schedule (or immediately start) a recording. Returns (rec, null) on success or
    /// (null, reason) when an interactive immediate start hits the connection limit without force.</summary>
    public (Recording? rec, string? conflict) Create(RecordingSnapshot snap)
    {
        var rec = new Recording
        {
            Status = RecordingStatus.Scheduled,
            Title = string.IsNullOrWhiteSpace(snap.Title) ? snap.ChannelName : snap.Title,
            ChannelName = snap.ChannelName,
            ChannelLogo = snap.ChannelLogo,
            PlaylistId = snap.PlaylistId,
            PlaylistName = snap.PlaylistName,
            MaxConnections = Math.Max(1, snap.MaxConnections),
            StreamUrl = snap.StreamUrl,
            StartUtc = snap.StartUtc == default ? DateTime.UtcNow : snap.StartUtc.ToUniversalTime(),
            StopUtc = snap.StopUtc?.ToUniversalTime(),
            PrePadSec = Math.Max(0, snap.PrePadSec ?? _opts.DefaultPrePadSeconds),
            PostPadSec = Math.Max(0, snap.PostPadSec ?? _opts.DefaultPostPadSeconds),
            Immediate = snap.Immediate,
            CreatedUtc = DateTime.UtcNow,
        };
        rec.FilePath = Path.Combine(_dir, rec.Id + ".mp4");

        if (snap.Immediate)
        {
            _startLock.Wait();
            try
            {
                if (!snap.Force && !LimitAllows(rec.PlaylistId, rec.MaxConnections))
                    return (null, $"This provider allows {rec.MaxConnections} simultaneous connection(s); recording would exceed it.");
                lock (_gate) { _index[rec.Id] = rec; Save(rec); }
                StartLocked(rec);
            }
            finally { _startLock.Release(); }
        }
        else
        {
            lock (_gate) { _index[rec.Id] = rec; Save(rec); }
        }
        return (rec, null);
    }

    /// <summary>Cancel a scheduled recording or stop+finalize an active one (file kept). No-op if finished.</summary>
    public bool Stop(string id)
    {
        StreamRecorder? recorder = null;
        lock (_gate)
        {
            if (!_index.TryGetValue(id, out var rec)) return false;
            switch (rec.Status)
            {
                case RecordingStatus.Scheduled:
                    _index.Remove(id);
                    TryDeleteSidecar(id);
                    return true;
                case RecordingStatus.Recording:
                    _running.Remove(id, out recorder);
                    rec.Status = RecordingStatus.Completed;
                    Save(rec);
                    break;
                default:
                    return true; // completed/failed — nothing to stop
            }
        }
        var seconds = recorder is null ? 0 : (int)Math.Round(recorder.RecordedSeconds);
        recorder?.Dispose(); // kills FFmpeg; fragmented MP4 stays playable
        lock (_gate)
        {
            if (_index.TryGetValue(id, out var rec))
            {
                rec.FileBytes = SafeLen(rec.FilePath);
                if (recorder is not null) rec.DurationSeconds = seconds;
                Save(rec);
            }
        }
        return true;
    }

    /// <summary>Stop (if active), then remove the entry and its media file. Idempotent.</summary>
    public bool Delete(string id)
    {
        Stop(id);
        Recording? rec;
        lock (_gate) { _index.TryGetValue(id, out rec); _index.Remove(id); }
        try { if (rec is not null && File.Exists(rec.FilePath)) File.Delete(rec.FilePath); } catch { }
        TryDeleteSidecar(id);
        return true;
    }

    // ── internals ──────────────────────────────────────────────────────────────────────────────

    private bool LimitAllows(string playlistId, int max)
    {
        lock (_gate)
            return _index.Values.Count(r => r.Status == RecordingStatus.Recording && r.PlaylistId == playlistId) < Math.Max(1, max);
    }

    /// <summary>Must be called holding _startLock. Sets the entry to recording (or failed).</summary>
    private void StartLocked(Recording rec)
    {
        int? duration = null;
        if (rec.StopUtc is { } stop)
        {
            var end = stop.AddSeconds(rec.PostPadSec);
            var secs = (int)Math.Ceiling((end - DateTime.UtcNow).TotalSeconds);
            if (secs <= 0) { Fail(rec, "window already passed"); return; }
            duration = secs;
        }
        var recorder = new StreamRecorder(rec.StreamUrl, rec.FilePath, _ffmpegPath, duration, _log);
        try { recorder.Start(); }
        catch (Exception ex)
        {
            recorder.Dispose();
            Fail(rec, "FFmpeg failed to start: " + Trunc(ex.Message));
            return;
        }
        lock (_gate) { rec.Status = RecordingStatus.Recording; Save(rec); _running[rec.Id] = recorder; }
    }

    private void Fail(Recording rec, string reason)
    {
        lock (_gate)
        {
            rec.Status = RecordingStatus.Failed;
            rec.ErrorReason = reason;
            Save(rec);
            try { if (File.Exists(rec.FilePath) && new FileInfo(rec.FilePath).Length == 0) File.Delete(rec.FilePath); } catch { }
        }
    }

    private void SafeTick()
    {
        try { Tick(); }
        catch (Exception ex) { _log.LogError(ex, "Recording scheduler tick failed"); }
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;

        // (a) Finalize running recordings whose FFmpeg exited (bounded -t done, or upstream ended).
        List<KeyValuePair<string, StreamRecorder>> running;
        lock (_gate) running = _running.ToList();
        foreach (var (id, recorder) in running)
            if (recorder.HasExited) Finalize(id, recorder);

        // (b) Fail scheduled recordings whose padded window is fully in the past (missed while offline).
        // (c) Start due scheduled recordings (under the start-lock, respecting the limit; retry next tick).
        List<Recording> scheduled;
        lock (_gate) scheduled = _index.Values.Where(r => r.Status == RecordingStatus.Scheduled).ToList();
        foreach (var rec in scheduled)
        {
            var windowEnd = rec.StopUtc?.AddSeconds(rec.PostPadSec);
            if (windowEnd is { } end && end <= now) { Fail(rec, "missed (companion was offline)"); continue; }

            var effectiveStart = rec.Immediate ? rec.StartUtc : rec.StartUtc.AddSeconds(-rec.PrePadSec);
            var due = effectiveStart <= now && (windowEnd is null || now < windowEnd);
            if (!due) continue;

            _startLock.Wait();
            try
            {
                var current = Get(rec.Id);
                if (current is null || current.Status != RecordingStatus.Scheduled) continue;
                if (!LimitAllows(current.PlaylistId, current.MaxConnections)) continue; // retry next tick
                StartLocked(current);
            }
            finally { _startLock.Release(); }
        }
    }

    private void Finalize(string id, StreamRecorder recorder)
    {
        var seconds = (int)Math.Round(recorder.RecordedSeconds);
        lock (_gate)
        {
            _running.Remove(id);
            if (_index.TryGetValue(id, out var rec) && rec.Status == RecordingStatus.Recording)
            {
                var bytes = recorder.FileBytes;
                if (bytes > 0) { rec.Status = RecordingStatus.Completed; rec.FileBytes = bytes; rec.DurationSeconds = seconds; }
                else { rec.Status = RecordingStatus.Failed; rec.ErrorReason = Trunc(recorder.StderrTail) ?? "no output"; }
                Save(rec);
            }
        }
        recorder.Dispose();
    }

    private void LoadAndReconcile()
    {
        // Scan the current dir, plus a legacy location: a build running under LocalSystem with no Videos
        // folder wrote recordings to a RELATIVE "PlaylistMixer Recordings" (resolved against the service's
        // working directory). Adopt anything stranded there so a prior version's recordings survive the fix.
        var dirs = new List<string> { _dir };
        try
        {
            var legacy = Path.GetFullPath("PlaylistMixer Recordings");
            if (!string.Equals(legacy, _dir, StringComparison.OrdinalIgnoreCase) && Directory.Exists(legacy))
                dirs.Add(legacy);
        }
        catch { /* ignore an unresolvable legacy path */ }

        foreach (var dir in dirs)
        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var rec = JsonSerializer.Deserialize<Recording>(File.ReadAllText(path), RecordingJson.Options);
                if (rec is null || _index.ContainsKey(rec.Id)) continue;
                // Normalize any relative path written by an older build to an absolute one.
                rec.FilePath = Path.GetFullPath(rec.FilePath);
                if (rec.Status == RecordingStatus.Recording)
                {
                    var ok = SafeLen(rec.FilePath) > 0;
                    rec.Status = ok ? RecordingStatus.Completed : RecordingStatus.Failed;
                    rec.FileBytes = SafeLen(rec.FilePath);
                    if (!ok) rec.ErrorReason = "interrupted (companion restarted)";
                }
                else if (rec.Status == RecordingStatus.Scheduled && rec.StopUtc is { } stop
                         && stop.AddSeconds(rec.PostPadSec) <= DateTime.UtcNow)
                {
                    rec.Status = RecordingStatus.Failed;
                    rec.ErrorReason = "missed (companion was offline)";
                }
                _index[rec.Id] = rec;
                Save(rec); // re-home the sidecar into the current dir (with the normalized path)
            }
            catch (Exception ex) { _log.LogWarning(ex, "Skipping unreadable recording sidecar {Path}", path); }
        }
    }

    private void Save(Recording r)
        => File.WriteAllText(Path.Combine(_dir, r.Id + ".json"), JsonSerializer.Serialize(r, RecordingJson.Options));

    private void TryDeleteSidecar(string id)
    {
        try { var p = Path.Combine(_dir, id + ".json"); if (File.Exists(p)) File.Delete(p); } catch { }
    }

    private static long SafeLen(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }
    private static string? Trunc(string? s) => string.IsNullOrEmpty(s) ? null : (s.Length <= 1000 ? s : s[^1000..]);

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate) { foreach (var r in _running.Values) r.Dispose(); _running.Clear(); }
        _startLock.Dispose();
    }
}
