using System.Diagnostics;
using System.Net.Http.Json;
using System.ServiceProcess;

namespace PlaylistMixer.Companion.Tray;

/// <summary>
/// System-tray controller for the PlaylistMixer Companion Windows service. Shows live status and
/// offers Start/Stop/Restart plus a shortcut to view recordings. Quitting stops the service (closing
/// the tray turns off local playback). Runs unelevated; the installer grants the Users group
/// service-control rights so these actions don't need a UAC prompt.
/// </summary>
internal sealed class TrayAppContext : ApplicationContext
{
    // Must match the service name registered by the installer.
    private const string ServiceName = "PlaylistMixerCompanion";

    // Candidate loopback ports the service may bind (must match the service's range). Probed for
    // /health to learn whether a stream is currently playing.
    private static readonly int[] HealthPorts = [36400, 36401, 36402];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(700) };

    private readonly Icon _appIcon;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly System.Windows.Forms.Timer _poll;

    // Opens the external-player settings window. Single-instance: focus it if already open.
    private SettingsForm? _settings;

    public TrayAppContext()
    {
        _statusItem = new ToolStripMenuItem("Checking…") { Enabled = false };
        _startItem = new ToolStripMenuItem("Start", null, (_, _) => Control(sc => sc.Start(), "start"));
        _stopItem = new ToolStripMenuItem("Stop", null, (_, _) => Control(StopAndWait, "stop"));
        _restartItem = new ToolStripMenuItem("Restart", null, (_, _) => Control(Restart, "restart"));

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(_restartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("External Player Settings…", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("View Recordings", null, (_, _) => OpenRecordings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit()));
        menu.Opening += (_, _) => Refresh();

        _appIcon = TrayIcons.CreateAppIcon();
        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            Visible = true,
            Text = "PlaylistMixer Companion",
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenRecordings();

        _poll = new System.Windows.Forms.Timer { Interval = 3000 };
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();
        Refresh();
    }

    private static ServiceControllerStatus? QueryStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status;
        }
        catch
        {
            return null; // service not installed
        }
    }

    // async void: fired from the UI timer / menu-opening; awaits resume on the UI thread so the
    // control updates are safe.
    private async void Refresh()
    {
        var status = QueryStatus();
        // One health probe drives both the streaming status and the foreground-lock check below.
        var health = status == ServiceControllerStatus.Running ? await GetHealthAsync() : null;
        var streaming = health?.Streaming ?? false;

        // First-run default: if external playback has never been configured, auto-detect an installed
        // player and enable it (detection must run here in the user session for HKCU access). Done once
        // we can read the service's settings; respects a user who later disables it.
        if (!_autoEnableChecked && status == ServiceControllerStatus.Running)
            _autoEnableChecked = await TryAutoEnableExternalPlayerAsync();

        // Once an external player is configured, relax this user's foreground-steal lock so the
        // service-launched player can come to the front (not just flash in the taskbar). Done once
        // per tray session, and only when the feature is actually in use.
        if (!_foregroundLockApplied && health?.ExternalPlayer?.Configured == true)
            _foregroundLockApplied = ForegroundLock.EnsureDisabled();

        var (label, tip, canStart, canStop) = status switch
        {
            ServiceControllerStatus.Running when streaming
                => ("Status: Streaming", "PlaylistMixer Companion — Streaming", false, true),
            ServiceControllerStatus.Running
                => ("Status: Running", "PlaylistMixer Companion — Running", false, true),
            ServiceControllerStatus.Stopped
                => ("Status: Stopped", "PlaylistMixer Companion — Stopped", true, false),
            null => ("Status: Not installed", "PlaylistMixer Companion — Not installed", false, false),
            _ => ($"Status: {status}", $"PlaylistMixer Companion — {status}", false, false),
        };
        _statusItem.Text = label;
        _icon.Text = tip.Length > 63 ? tip[..63] : tip; // NotifyIcon.Text caps at 63 chars
        _startItem.Enabled = canStart;
        _stopItem.Enabled = canStop;
        _restartItem.Enabled = status == ServiceControllerStatus.Running;
    }

    // Set once we've relaxed the foreground lock this session, so we don't re-broadcast on every poll.
    private bool _foregroundLockApplied;
    // Set once we've evaluated the first-run external-player default, so we don't keep re-checking.
    private bool _autoEnableChecked;

    // First-run default for external playback: when nothing has ever been configured and a media
    // player is installed, auto-detect one (VLC first, then MPV/MPC-HC/PotPlayer) and enable it, so
    // external playback works out of the box. Returns true once it has definitively evaluated (settings
    // were readable), so the caller stops re-checking. A user who later disables it keeps a saved
    // name/path, so this no longer treats it as first-run and won't re-enable behind their back.
    private static async Task<bool> TryAutoEnableExternalPlayerAsync()
    {
        var got = await ExternalPlayerClient.GetAsync();
        if (got is null) return false; // service not reachable yet — retry on a later poll
        var (port, s) = got.Value;
        // "Never configured" = the None sentinel: no name and no path have ever been saved.
        if (!string.IsNullOrEmpty(s.Name) || !string.IsNullOrEmpty(s.Path)) return true;
        var player = PlayerDetector.Detect().FirstOrDefault();
        if (player is null) return true; // nothing installed to enable — don't keep probing this session
        try { await ExternalPlayerClient.SaveAsync(port, new ExternalPlayerClient.Settings(true, player.Name, player.Path)); }
        catch { return false; } // save failed — try again on the next poll
        return true;
    }

    private sealed record HealthDto(string Name, string Version, int Port, bool Streaming, string? RecordingsDir, ExternalPlayerDto? ExternalPlayer);
    private sealed record ExternalPlayerDto(bool Configured, string Name);

    // Probes the candidate ports for /health and returns the first reachable companion's payload; null
    // if none respond.
    private static async Task<HealthDto?> GetHealthAsync()
    {
        foreach (var port in HealthPorts)
        {
            try
            {
                var health = await Http.GetFromJsonAsync<HealthDto>($"http://127.0.0.1:{port}/health");
                if (health is not null) return health;
            }
            catch { /* not this port / not reachable — try the next */ }
        }
        return null;
    }

    // Runs a service-control action off the UI thread; surfaces failures (e.g. access denied) as a balloon.
    private void Control(Action<ServiceController> action, string verb)
    {
        Task.Run(() =>
        {
            try
            {
                using var sc = new ServiceController(ServiceName);
                action(sc);
            }
            catch (Exception ex)
            {
                ShowError($"Could not {verb} the service: {ex.Message}");
            }
        });
    }

    private static void StopAndWait(ServiceController sc)
    {
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
    }

    private static void Restart(ServiceController sc)
    {
        if (sc.Status != ServiceControllerStatus.Stopped)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
        }
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
    }

    // Opens the folder where the service saves recordings. The service runs as LocalSystem, so the tray
    // (running as the user) can't recompute the path — it learns the resolved dir from /health.
    private async void OpenRecordings()
    {
        var dir = (await GetHealthAsync())?.RecordingsDir;
        if (string.IsNullOrEmpty(dir))
        {
            ShowError("Couldn't locate the recordings folder. Is the companion service running?");
            return;
        }
        if (!Directory.Exists(dir))
        {
            ShowError($"No recordings yet — this folder doesn't exist:{Environment.NewLine}{dir}");
            return;
        }
        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError($"Could not open the recordings folder: {ex.Message}"); }
    }

    private void OpenSettings()
    {
        if (_settings is { IsDisposed: false }) { _settings.Activate(); return; }
        _settings = new SettingsForm();
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.Show();
    }

    // Quit handler: stop the local service first (closing the tray turns off local playback), then exit.
    // async void so the await resumes on the UI thread for ExitThread(); the stop runs off-thread.
    private async void Quit()
    {
        _poll.Stop();
        await Task.Run(StopServiceQuietly);
        ExitThread();
    }

    // Best-effort clean stop of the service (the installer grants Users stop rights, so no UAC). A
    // graceful Stop doesn't trip the service's crash-restart action; failures (not installed / access
    // denied) are swallowed so Quit always proceeds.
    private static void StopServiceQuietly()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
        }
        catch { /* not installed / access denied — quit anyway */ }
    }

    private void ShowError(string message)
    {
        _icon.BalloonTipTitle = "PlaylistMixer Companion";
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(5000);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _appIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
