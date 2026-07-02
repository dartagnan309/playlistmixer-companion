namespace PlaylistMixer.Companion.Tray;

/// <summary>
/// Lets the user pick which external player the companion launches. Detected players populate a
/// dropdown; "Custom…" browses for any exe. Reads the current choice from the service and saves back to it.
/// </summary>
internal sealed class SettingsForm : Form
{
    private const string CustomLabel = "Custom…";
    private readonly ComboBox _combo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly TextBox _path = new() { Width = 320, ReadOnly = true };
    private readonly Button _browse = new() { Text = "Browse…", Width = 90, Enabled = false };
    private readonly CheckBox _enabled = new() { Text = "Enable external-player playback", AutoSize = true };
    private readonly Button _save = new() { Text = "Save", Width = 90 };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.Firebrick };
    private readonly List<DetectedPlayer> _detected = PlayerDetector.Detect().ToList();
    private int? _port;

    public SettingsForm()
    {
        Text = "PlaylistMixer Companion — External Player";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 220);

        var lblPlayer = new Label { Text = "Player:", AutoSize = true, Location = new Point(16, 50) };
        _combo.Location = new Point(96, 46);
        _browse.Location = new Point(96 + 324, 45);
        var lblPath = new Label { Text = "Path:", AutoSize = true, Location = new Point(16, 86) };
        _path.Location = new Point(96, 82);
        _enabled.Location = new Point(96, 120);
        _save.Location = new Point(96, 156);
        _status.Location = new Point(16, 16); _status.MaximumSize = new Size(408, 0);

        foreach (var p in _detected) _combo.Items.Add(p.Name);
        _combo.Items.Add(CustomLabel);
        _combo.SelectedIndexChanged += (_, _) => OnSelectionChanged();
        _browse.Click += (_, _) => Browse();
        _save.Click += async (_, _) => await SaveAsync();

        Controls.AddRange([_status, lblPlayer, _combo, _browse, lblPath, _path, _enabled, _save]);
        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        var got = await ExternalPlayerClient.GetAsync();
        if (got is null) { _status.Text = "Companion service not reachable — is it running?"; _save.Enabled = false; return; }
        _port = got.Value.port;
        var s = got.Value.settings;
        _enabled.Checked = s.Enabled;

        // Pre-select a detected player matching the saved path; otherwise treat the saved path as Custom.
        var match = _detected.FirstOrDefault(p => string.Equals(p.Path, s.Path, StringComparison.OrdinalIgnoreCase));
        if (match is not null) { _combo.SelectedItem = match.Name; _path.Text = match.Path; }
        else if (!string.IsNullOrWhiteSpace(s.Path)) { _combo.SelectedItem = CustomLabel; _path.Text = s.Path; }
        else if (_combo.Items.Count > 0) _combo.SelectedIndex = 0;
    }

    private void OnSelectionChanged()
    {
        var isCustom = (string?)_combo.SelectedItem == CustomLabel;
        _browse.Enabled = isCustom;
        if (!isCustom)
        {
            var p = _detected.FirstOrDefault(d => d.Name == (string?)_combo.SelectedItem);
            if (p is not null) _path.Text = p.Path;
        }
    }

    private void Browse()
    {
        using var dlg = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Select a media player" };
        if (dlg.ShowDialog(this) == DialogResult.OK) _path.Text = dlg.FileName;
    }

    private async Task SaveAsync()
    {
        _status.ForeColor = Color.Firebrick;
        if (_enabled.Checked && (string.IsNullOrWhiteSpace(_path.Text) || !File.Exists(_path.Text)))
        { _status.Text = "Pick a player whose file exists."; return; }
        if (_port is null) { _status.Text = "Companion service not reachable."; return; }

        var name = (string?)_combo.SelectedItem == CustomLabel
            ? Path.GetFileNameWithoutExtension(_path.Text)
            : (string?)_combo.SelectedItem ?? "";
        try
        {
            await ExternalPlayerClient.SaveAsync(_port.Value, new ExternalPlayerClient.Settings(_enabled.Checked, name, _path.Text));
            _status.ForeColor = Color.SeaGreen; _status.Text = "Saved.";
        }
        catch (Exception ex) { _status.Text = $"Save failed: {ex.Message}"; }
    }
}
