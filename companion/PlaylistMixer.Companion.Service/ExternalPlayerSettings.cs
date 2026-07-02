using System.Text.Json;
using PlaylistMixer.Companion.Service.Platform;

namespace PlaylistMixer.Companion.Service;

/// <summary>The user's external-player choice, set from the tray and read by the service to launch.</summary>
public sealed record ExternalPlayerSettings(bool Enabled, string Name, string Path)
{
    public static ExternalPlayerSettings None { get; } = new(false, "", "");

    /// <summary>Configured = enabled AND a path that currently exists on disk. The path may be a file
    /// (a Windows .exe / Unix binary) or a directory (a macOS .app bundle).</summary>
    public bool Configured => Enabled && PathExists(Path);

    /// <summary>True if the player path points at an existing file or an existing .app bundle (a dir).</summary>
    public static bool PathExists(string? path) =>
        !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));
}

/// <summary>
/// Persists the external-player choice to &lt;AppDataDir&gt;\external-player.json (plain JSON; no secret).
/// Held in memory; rewritten on save.
/// </summary>
public sealed class ExternalPlayerStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly ILogger<ExternalPlayerStore> _log;
    private readonly object _gate = new();
    private ExternalPlayerSettings _current = ExternalPlayerSettings.None;

    public ExternalPlayerStore(IPlatform platform, ILogger<ExternalPlayerStore> log)
    {
        _log = log;
        _path = Path.Combine(platform.AppDataDir, "external-player.json");
        Load();
    }

    public ExternalPlayerSettings Current { get { lock (_gate) return _current; } }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var s = JsonSerializer.Deserialize<ExternalPlayerSettings>(File.ReadAllText(_path), Json);
                if (s is not null) _current = s with { Name = s.Name ?? "", Path = s.Path ?? "" };
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not read external-player settings; using defaults."); }
    }

    public void Save(ExternalPlayerSettings settings)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(settings, Json));
                _current = settings;
            }
            catch (Exception ex) { _log.LogError(ex, "Could not persist external-player settings."); throw; }
        }
    }
}
