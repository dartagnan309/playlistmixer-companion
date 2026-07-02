using System.Text.Json;
using PlaylistMixer.Companion.Service.Platform;

namespace PlaylistMixer.Companion.Service;

/// <summary>The reverse-channel pairing credential + the server to dial.</summary>
public sealed record PairingSettings(string Token, string ServerUrl)
{
    public static PairingSettings None { get; } = new("", "");
    public bool Paired => !string.IsNullOrWhiteSpace(Token) && !string.IsNullOrWhiteSpace(ServerUrl);
}

/// <summary>
/// Persists the pairing credential to &lt;AppDataDir&gt;\pairing.bin, protected by the platform: DPAPI
/// (LocalMachine) on Windows, owner-only file permissions on macOS. Held in memory; rewritten on save.
/// </summary>
public sealed class PairingStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IPlatform _platform;
    private readonly string _path;
    private readonly ILogger<PairingStore> _log;
    private readonly object _gate = new();
    private PairingSettings _current = PairingSettings.None;

    public PairingStore(IPlatform platform, ILogger<PairingStore> log)
    {
        _platform = platform;
        _log = log;
        _path = Path.Combine(platform.AppDataDir, "pairing.bin");
        Load();
    }

    public PairingSettings Current { get { lock (_gate) return _current; } }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var protectedBytes = File.ReadAllBytes(_path);
            var plain = _platform.Unprotect(protectedBytes);
            var s = JsonSerializer.Deserialize<PairingSettings>(plain, Json);
            if (s is not null) _current = s;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not read pairing settings; treating as unpaired."); }
    }

    public void Save(PairingSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var plain = JsonSerializer.SerializeToUtf8Bytes(settings, Json);
            var protectedBytes = _platform.Protect(plain);
            File.WriteAllBytes(_path, protectedBytes);
            _platform.HardenFile(_path);
            _current = settings;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best effort */ }
            _current = PairingSettings.None;
        }
    }
}
