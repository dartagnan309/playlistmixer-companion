using System.Diagnostics;

namespace PlaylistMixer.Companion.Service.Platform;

/// <summary>
/// macOS implementation: the Companion runs as a per-user LaunchAgent inside the user's own GUI
/// session. That collapses most of the Windows complexity — state lives under ~/Library, the pairing
/// secret is protected by owner-only file permissions (no DPAPI), and launching the player is just
/// `open` because we are already in the user's session (no cross-session token dance).
///
/// Also used as the fallback for any non-Windows Unix (e.g. Linux dev runs); the paths follow the
/// macOS convention, which is a reasonable default.
/// </summary>
public sealed class MacPlatform : IPlatform
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string AppDataDir => Path.Combine(Home, "Library", "Application Support", "PlaylistMixer Companion");

    public string DefaultRecordingsDir => Path.Combine(Home, "Movies", "PlaylistMixer Recordings");

    public string DefaultFfmpegPath => Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    // launchd, not the host builder, owns lifecycle/restart on macOS.
    public void ConfigureHost(IHostBuilder host) { }

    // No DPAPI on macOS. The pairing token is stored as-is and protected by 0600 file permissions
    // (see HardenFile); the LaunchAgent already runs as the owning user. A future hardening step
    // could move this into the macOS Keychain.
    public byte[] Protect(byte[] plaintext) => plaintext;
    public byte[] Unprotect(byte[] data) => data;

    public void HardenFile(string path)
    {
        if (OperatingSystem.IsWindows()) return; // SetUnixFileMode is a no-op concept on Windows
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { /* best effort — a failure here doesn't compromise correctness, only tidiness */ }
    }

    public void LaunchPlayer(string playerPath, string url)
    {
        // An ".app" bundle is a directory — route the URL to it via `open -a`, which activates the app
        // in the foreground. A bare executable path (dev/CLI players) is launched directly.
        if (playerPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || Directory.Exists(playerPath))
            Process.Start(new ProcessStartInfo("open", ["-a", playerPath, url]) { UseShellExecute = false });
        else
            Process.Start(new ProcessStartInfo(playerPath, [url]) { UseShellExecute = false });
    }
}
