namespace MonitorProfileSwitcher;

internal class CaptureDialog : Form
{
    private readonly IReadOnlyList<DisplayProfile> _existing;
    private readonly ComboBox _nameBox;
    private readonly CheckBox _ctrlCheck;
    private readonly CheckBox _altCheck;
    private readonly CheckBox _shiftCheck;
    private readonly ComboBox _keyCombo;

    public string ProfileName => _nameBox.Text.Trim();
    public HotkeyBinding? SelectedHotkey
    {
        get
        {
            if (_keyCombo.SelectedItem == null || _keyCombo.SelectedItem.ToString() == "(none)")
                return null;
            return new HotkeyBinding
            {
                Ctrl = _ctrlCheck.Checked,
                Alt = _altCheck.Checked,
                Shift = _shiftCheck.Checked,
                Key = _keyCombo.SelectedItem.ToString()!
            };
        }
    }

    /// <param name="existing">Saved profiles: offered for overwriting (keeping their hotkey) and
    /// checked against so a new hotkey cannot silently collide with another profile's.</param>
    public CaptureDialog(IReadOnlyList<DisplayProfile> existing, string title = "Capture Current Display Setup", string? initialName = null)
    {
        _existing = existing;
        Text = title;
        Size = new System.Drawing.Size(420, 240);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var nameLabel = new Label { Text = "Profile Name:", Location = new(15, 18), AutoSize = true };
        _nameBox = new ComboBox { Location = new(120, 15), Width = 265, DropDownStyle = ComboBoxStyle.DropDown };
        _nameBox.Items.AddRange(existing.Select(profile => (object)profile.Name).ToArray());

        var hotkeyLabel = new Label { Text = "Hotkey:", Location = new(15, 55), AutoSize = true };
        _ctrlCheck = new CheckBox { Text = "Ctrl", Location = new(120, 53), Checked = true, AutoSize = true };
        _altCheck = new CheckBox { Text = "Alt", Location = new(175, 53), Checked = true, AutoSize = true };
        _shiftCheck = new CheckBox { Text = "Shift", Location = new(225, 53), AutoSize = true };

        _keyCombo = new ComboBox
        {
            Location = new(295, 52),
            Width = 90,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _keyCombo.Items.Add("(none)");
        for (int i = 0; i <= 9; i++) _keyCombo.Items.Add(i.ToString());
        for (char c = 'A'; c <= 'Z'; c++) _keyCombo.Items.Add(c.ToString());
        for (int i = 1; i <= 12; i++) _keyCombo.Items.Add($"F{i}");
        _keyCombo.SelectedIndex = 0;

        // Choosing an existing profile brings its hotkey along, so overwriting keeps it.
        _nameBox.SelectedIndexChanged += (_, _) => LoadHotkeyFor(_nameBox.Text);
        _nameBox.Leave += (_, _) => LoadHotkeyFor(_nameBox.Text);

        // Preview of current monitors
        var previewLabel = new Label { Text = "Current monitors:", Location = new(15, 90), AutoSize = true };
        var preview = new Label { Location = new(15, 110), AutoSize = true, ForeColor = System.Drawing.Color.DarkGray };
        try
        {
            var snapshot = DisplayManager.CaptureCurrentConfig();
            var lines = snapshot.Monitors.Select(m =>
            {
                var primary = m.IsPrimary ? " ★" : "";
                var name = m.FriendlyName.Length > 0 ? m.FriendlyName : m.EdidId;
                return $"  {name} — {m.Resolution.Width}x{m.Resolution.Height} @ {m.RefreshRate}{primary}";
            });
            var current = lines.ToList();
            var surround = NvidiaSurroundManager.TryCaptureActive();
            if (surround != null)
                current.Insert(0, $"  NVIDIA Surround {surround.Rows}x{surround.Columns} — " +
                    $"{surround.WidthPerDisplay}x{surround.HeightPerDisplay} per display");
            preview.Text = string.Join("\n", current);
        }
        catch { preview.Text = "  (unable to read display config)"; }

        var okButton = new Button { Text = "Save", Location = new(220, 170), Width = 80 };
        okButton.Click += (_, _) => Confirm();
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new(305, 170), Width = 80 };

        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.AddRange([nameLabel, _nameBox, hotkeyLabel, _ctrlCheck, _altCheck, _shiftCheck,
            _keyCombo, previewLabel, preview, okButton, cancelButton]);

        // Grow to fit the preview of many monitors.
        Load += (_, _) =>
        {
            var bottom = preview.Bottom + 12;
            okButton.Top = cancelButton.Top = Math.Max(okButton.Top, bottom);
            ClientSize = new System.Drawing.Size(ClientSize.Width, okButton.Bottom + 14);
        };

        if (!string.IsNullOrEmpty(initialName))
        {
            _nameBox.Text = initialName;
            LoadHotkeyFor(initialName);
        }
    }

    private DisplayProfile? Existing(string name) =>
        _existing.FirstOrDefault(profile => profile.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    private void LoadHotkeyFor(string name)
    {
        var hotkey = Existing(name)?.Hotkey;
        if (hotkey == null) return;
        _ctrlCheck.Checked = hotkey.Ctrl;
        _altCheck.Checked = hotkey.Alt;
        _shiftCheck.Checked = hotkey.Shift;
        var index = _keyCombo.Items.IndexOf(hotkey.Key);
        _keyCombo.SelectedIndex = index >= 0 ? index : 0;
    }

    private void Confirm()
    {
        if (ProfileName.Length == 0)
        {
            MessageBox.Show(this, "Enter a profile name.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var hotkey = SelectedHotkey;
        var clash = hotkey == null ? null : _existing.FirstOrDefault(profile =>
            !profile.Name.Equals(ProfileName, StringComparison.OrdinalIgnoreCase) &&
            profile.Hotkey?.ToString().Equals(hotkey.ToString(), StringComparison.OrdinalIgnoreCase) == true);
        if (clash != null)
        {
            MessageBox.Show(this, $"{hotkey} is already the hotkey for '{clash.Name}'. Pick another key, or change that profile's hotkey in Manage Profiles.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (Existing(ProfileName) is { } existing &&
            MessageBox.Show(this, $"Replace the saved profile '{existing.Name}' with the current layout?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
