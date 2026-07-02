using System.Runtime.InteropServices;

namespace PlaylistMixer.Companion.Tray;

/// <summary>
/// Relaxes Windows' foreground-steal lock for the current user so a media player launched by the
/// LocalSystem service can come to the front instead of only flashing in the taskbar. The default
/// ForegroundLockTimeout (~200s) blocks service-launched apps from taking focus. This runs from the
/// tray (which lives on the interactive desktop, so SystemParametersInfo succeeds) and persists the
/// value, scoped to the current user.
/// </summary>
internal static class ForegroundLock
{
    private const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
    private const uint SPIF_UPDATEINIFILE = 0x01; // persist to the user profile
    private const uint SPIF_SENDCHANGE = 0x02;    // broadcast so the live value updates now

    /// <summary>Sets ForegroundLockTimeout to 0 unless it already is. Best-effort; returns success.</summary>
    public static bool EnsureDisabled()
    {
        try
        {
            uint current = 1;
            if (SystemParametersInfo(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref current, 0) && current == 0)
                return true; // already disabled — nothing to change
            // For SET, the new value is passed IN pvParam (cast to a pointer); IntPtr.Zero == 0.
            return SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        }
        catch { return false; }
    }

    // GET: pvParam points to a DWORD that receives the value. SET: pvParam IS the value cast to PVOID.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint uiParam, ref uint pvParam, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint uiParam, IntPtr pvParam, uint winIni);
}
