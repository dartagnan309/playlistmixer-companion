using Microsoft.Win32;

namespace PlaylistMixer.Companion.Tray;

/// <summary>A player the user can pick: a display name + the resolved absolute exe path.</summary>
public sealed record DetectedPlayer(string Name, string Path);

/// <summary>
/// Probes the registry + known install locations for common media players. Runs in the tray (the
/// user's interactive session), so it can read HKCU keys a LocalSystem service could not. Returns only
/// players whose exe currently exists, de-duplicated by path.
/// </summary>
public static class PlayerDetector
{
    public static IReadOnlyList<DetectedPlayer> Detect()
    {
        var found = new List<DetectedPlayer>();
        Add(found, "VLC", AppPaths("vlc.exe")
            ?? RegPath(RegistryHive.LocalMachine, @"SOFTWARE\VideoLAN\VLC", "InstallDir", "vlc.exe")
            ?? ProgramFiles(@"VideoLAN\VLC\vlc.exe"));
        Add(found, "MPV", AppPaths("mpv.exe") ?? OnPath("mpv.exe"));
        Add(found, "MPC-HC", RegValue(RegistryHive.CurrentUser, @"SOFTWARE\MPC-HC\MPC-HC", "ExePath")
            ?? RegValue(RegistryHive.LocalMachine, @"SOFTWARE\MPC-HC\MPC-HC", "ExePath")
            ?? ProgramFiles(@"MPC-HC\mpc-hc64.exe"));
        Add(found, "PotPlayer", AppPaths("PotPlayerMini64.exe") ?? AppPaths("PotPlayer64.exe")
            ?? RegValue(RegistryHive.CurrentUser, @"SOFTWARE\DAUM\PotPlayer", "ProgramPath"));
        return found;
    }

    private static void Add(List<DetectedPlayer> list, string name, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        if (list.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(new DetectedPlayer(name, path));
    }

    // HKLM\...\App Paths\<exe> default value = full path to the exe.
    private static string? AppPaths(string exe)
        => RegValue(RegistryHive.LocalMachine, $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}", null)
        ?? RegValue(RegistryHive.CurrentUser, $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}", null);

    // Reads a registry string value (null valueName = the key's default value).
    private static string? RegValue(RegistryHive hive, string subKey, string? valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // Reads a registry dir value and joins an exe onto it.
    private static string? RegPath(RegistryHive hive, string subKey, string valueName, string exe)
    {
        var dir = RegValue(hive, subKey, valueName);
        return string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir, exe);
    }

    private static string? ProgramFiles(string rel)
    {
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var p = Path.Combine(Environment.GetFolderPath(root), rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string? OnPath(string exe)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        return paths.Select(d => Path.Combine(d.Trim(), exe)).FirstOrDefault(File.Exists);
    }
}
