using System.Security.Cryptography;
using Microsoft.Extensions.Hosting.WindowsServices;
using PlaylistMixer.Companion.Service; // InteractiveProcessLauncher

namespace PlaylistMixer.Companion.Service.Platform;

/// <summary>
/// Windows implementation: the install-and-forget service runs as LocalSystem in session 0, so state
/// lives under %ProgramData%, the pairing secret is DPAPI-encrypted at LocalMachine scope, and the
/// external player is launched into the active user's desktop via <see cref="InteractiveProcessLauncher"/>.
/// </summary>
public sealed class WindowsPlatform : IPlatform
{
    public string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PlaylistMixer Companion");

    public string DefaultRecordingsDir
    {
        get
        {
            // The user's Videos folder when available, else a machine-wide, always-present location
            // (LocalSystem has no MyVideos — GetFolderPath returns "", which would yield a relative path).
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            return !string.IsNullOrWhiteSpace(videos)
                ? Path.Combine(videos, "PlaylistMixer Recordings")
                : Path.Combine(AppDataDir, "Recordings");
        }
    }

    public string DefaultFfmpegPath => Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");

    public void ConfigureHost(IHostBuilder host) => host.UseWindowsService();

    public byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.LocalMachine);
    }

    public byte[] Unprotect(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return ProtectedData.Unprotect(data, optionalEntropy: null, DataProtectionScope.LocalMachine);
    }

    // The %ProgramData%\PlaylistMixer Companion folder is already protected by inherited ACLs; no
    // per-file hardening needed (and DPAPI provides confidentiality regardless).
    public void HardenFile(string path) { }

    public void LaunchPlayer(string playerPath, string url) => InteractiveProcessLauncher.Launch(playerPath, url);
}
