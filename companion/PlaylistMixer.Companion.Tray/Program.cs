namespace PlaylistMixer.Companion.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Single instance: the tray autostarts at login; a second copy would just duplicate the icon.
        using var mutex = new Mutex(initiallyOwned: true, "PlaylistMixer.Companion.Tray", out var isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
        GC.KeepAlive(mutex);
    }
}
