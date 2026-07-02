namespace PlaylistMixer.Companion.Service.Platform;

/// <summary>
/// The OS-specific seam for the Companion service. Everything that differs between Windows (the
/// install-and-forget LocalSystem service) and macOS (a per-user LaunchAgent) lives behind this:
/// where on-disk state goes, how the pairing secret is protected, how FFmpeg is found, and how the
/// external player is launched. The rest of the service is platform-neutral and talks only to this.
///
/// Resolve once at startup via <see cref="PlatformInfo.Create"/> and register as a DI singleton.
/// </summary>
public interface IPlatform
{
    /// <summary>Per-app data directory for small state files (pairing, external-player choice).
    /// Windows: %ProgramData%\PlaylistMixer Companion. macOS: ~/Library/Application Support/PlaylistMixer Companion.</summary>
    string AppDataDir { get; }

    /// <summary>The recordings folder used when Dvr:RecordingsDir is not configured.
    /// Windows: &lt;MyVideos&gt;\PlaylistMixer Recordings (falling back to %ProgramData% under LocalSystem).
    /// macOS: ~/Movies/PlaylistMixer Recordings.</summary>
    string DefaultRecordingsDir { get; }

    /// <summary>FFmpeg path used when Ffmpeg:Path is not configured — the bundled binary next to the
    /// executable. Windows: ffmpeg.exe. macOS/Unix: ffmpeg.</summary>
    string DefaultFfmpegPath { get; }

    /// <summary>Host integration that only applies on Windows (UseWindowsService). No-op elsewhere —
    /// launchd runs the binary as a plain foreground process.</summary>
    void ConfigureHost(IHostBuilder host);

    /// <summary>Encrypt a small secret at rest. Windows: DPAPI (LocalMachine). macOS: identity —
    /// confidentiality comes from owner-only file permissions applied by <see cref="HardenFile"/>,
    /// since the LaunchAgent already runs as the owning user.</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>Reverse of <see cref="Protect"/>.</summary>
    byte[] Unprotect(byte[] data);

    /// <summary>Restrict a freshly written file to the owner. Windows: no-op (ACL inherited from the
    /// protected %ProgramData% folder). macOS/Unix: chmod 0600.</summary>
    void HardenFile(string path);

    /// <summary>Launch the configured external media player against a raw upstream URL, bringing it to
    /// the foreground in the user's session.</summary>
    void LaunchPlayer(string playerPath, string url);
}

/// <summary>Selects the concrete platform at runtime.</summary>
public static class PlatformInfo
{
    public static IPlatform Create() =>
        OperatingSystem.IsWindows() ? new WindowsPlatform() : new MacPlatform();
}
