using System.Collections.Concurrent;
using PlaylistMixer.Playback;

namespace PlaylistMixer.Companion.Service;

/// <summary>Bound from the "Dvr" config section (see appsettings.json).</summary>
public sealed class DvrOptions
{
    public bool Enabled { get; init; } = true;
    /// <summary>How far back the user can pause/rewind. Default 10 minutes.</summary>
    public int WindowSeconds { get; init; } = 600;
    /// <summary>HLS segment length; smaller = lower latency behind live, more files.</summary>
    public int SegmentSeconds { get; init; } = 2;
    /// <summary>A session is reaped this long after its last manifest/segment fetch.</summary>
    public int IdleTimeoutSeconds { get; init; } = 30;
    /// <summary>Where capture dirs live. Empty => %TEMP%/PlaylistMixerCompanion/dvr.</summary>
    public string RootDir { get; init; } = "";

    public string ResolveRootDir() => string.IsNullOrWhiteSpace(RootDir)
        ? Path.Combine(Path.GetTempPath(), "PlaylistMixerCompanion", "dvr")
        : RootDir;

    /// <summary>Where finished/in-progress recordings are stored. Empty => <paramref name="platformDefault"/>
    /// (the user's Videos/Movies folder, per platform). MUST be persistent — never %TEMP% (that's only
    /// for the rolling live-DVR window).</summary>
    public string RecordingsDir { get; init; } = "";
    /// <summary>Seconds to start a scheduled recording early (does not apply to immediate starts).</summary>
    public int DefaultPrePadSeconds { get; init; } = 60;
    /// <summary>Seconds to keep recording past the program's stop time.</summary>
    public int DefaultPostPadSeconds { get; init; } = 180;

    /// <summary>Resolves the persistent recordings directory: the configured Dvr:RecordingsDir when set,
    /// otherwise the platform's default. Always returned as an absolute path — Results.File
    /// (PhysicalFileHttpResult) rejects non-rooted paths with a 500.</summary>
    public string ResolveRecordingsDir(string platformDefault)
    {
        var dir = !string.IsNullOrWhiteSpace(RecordingsDir)
            ? Environment.ExpandEnvironmentVariables(RecordingsDir)
            : platformDefault;
        return Path.GetFullPath(dir);
    }
}

/// <summary>One live DVR capture: a session id, its on-disk window, and the FFmpeg writer driving it.</summary>
public sealed class DvrSession : IDisposable
{
    public string Id { get; }
    public string Url { get; }
    public string Dir { get; }
    public StreamDvr Writer { get; }
    private long _lastAccessTicks;

    public DvrSession(string id, string url, string dir, StreamDvr writer)
    {
        Id = id; Url = url; Dir = dir; Writer = writer;
        Touch();
    }

    public void Touch() => Interlocked.Exchange(ref _lastAccessTicks, DateTime.UtcNow.Ticks);
    public DateTime LastAccessUtc => new(Interlocked.Read(ref _lastAccessTicks), DateTimeKind.Utc);

    public void Dispose()
    {
        Writer.Dispose();
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { }
    }
}

/// <summary>
/// Owns the live DVR captures. One session per upstream URL, so multiple players of the same channel
/// share a single FFmpeg + window. Sessions are reaped a short time after their last fetch (covers tab
/// close / channel change) — there's no explicit refcount; ongoing manifest/segment requests keep a
/// session alive. Registered as a singleton; disposed on host shutdown (kills FFmpeg, deletes dirs).
/// </summary>
public sealed class DvrManager : IDisposable
{
    private readonly DvrOptions _opts;
    private readonly string _ffmpegPath;
    private readonly string _loopbackBase;
    private readonly ILogger<DvrManager> _log;
    private readonly ConcurrentDictionary<string, DvrSession> _byUrl = new();
    private readonly ConcurrentDictionary<string, DvrSession> _byId = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly Timer _reaper;

    public DvrManager(DvrOptions opts, string ffmpegPath, string loopbackBase, ILogger<DvrManager> log)
    {
        _opts = opts;
        _ffmpegPath = ffmpegPath;
        _loopbackBase = loopbackBase.TrimEnd('/');
        _log = log;
        _reaper = new Timer(_ => Reap(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public bool Enabled => _opts.Enabled;
    public int WindowSeconds => _opts.WindowSeconds;

    /// <summary>
    /// Returns a ready session for <paramref name="url"/>, starting FFmpeg if needed and waiting for the
    /// first segment. Returns null if the capture failed to produce output in time.
    /// </summary>
    public async Task<DvrSession?> GetOrStartAsync(string url, CancellationToken ct)
    {
        if (_byUrl.TryGetValue(url, out var existing) && !existing.Writer.HasExited)
        {
            existing.Touch();
            return existing;
        }

        await _startLock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the lock (another request may have started it).
            if (_byUrl.TryGetValue(url, out existing) && !existing.Writer.HasExited)
            {
                existing.Touch();
                return existing;
            }
            // A dead session for this URL (upstream ended) — drop it and start fresh.
            if (existing is not null) Remove(existing);

            var id = Guid.NewGuid().ToString("N");
            var dir = Path.Combine(_opts.ResolveRootDir(), id);
            var segmentBaseUrl = $"{_loopbackBase}/dvr/{id}/";
            var writer = new StreamDvr(url, dir, segmentBaseUrl, _ffmpegPath,
                _opts.WindowSeconds, _opts.SegmentSeconds, _log);

            DvrSession session;
            try
            {
                writer.Start();
                if (!await writer.WaitForReadyAsync(TimeSpan.FromSeconds(12), ct))
                {
                    _log.LogWarning("DVR capture for {Url} produced no segments in time.", url);
                    writer.Dispose();
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                    return null;
                }
                session = new DvrSession(id, url, dir, writer);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to start DVR capture for {Url}", url);
                writer.Dispose();
                return null;
            }

            _byUrl[url] = session;
            _byId[id] = session;
            return session;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>Looks up an active session by id (for serving its segment/manifest files).</summary>
    public DvrSession? Find(string sessionId)
    {
        if (_byId.TryGetValue(sessionId, out var s)) { s.Touch(); return s; }
        return null;
    }

    private void Remove(DvrSession s)
    {
        _byUrl.TryRemove(new KeyValuePair<string, DvrSession>(s.Url, s));
        _byId.TryRemove(new KeyValuePair<string, DvrSession>(s.Id, s));
        s.Dispose();
    }

    private void Reap()
    {
        var idleCutoff = DateTime.UtcNow - TimeSpan.FromSeconds(_opts.IdleTimeoutSeconds);
        foreach (var s in _byId.Values)
        {
            if (s.Writer.HasExited || s.LastAccessUtc < idleCutoff)
            {
                _log.LogInformation("Reaping DVR session {Id} (exited={Exited})", s.Id, s.Writer.HasExited);
                Remove(s);
            }
        }
    }

    public void Dispose()
    {
        _reaper.Dispose();
        foreach (var s in _byId.Values) s.Dispose();
        _byId.Clear();
        _byUrl.Clear();
        _startLock.Dispose();
    }
}
