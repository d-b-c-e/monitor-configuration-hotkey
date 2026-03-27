namespace MonitorProfileSwitcher;

internal class CaptureDialog : Form
{
    private readonly TextBox _nameBox;
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

    public CaptureDialog(List<string> existingNames)
    {
        Text = "Capture Current Display Setup";
        Size = new System.Drawing.Size(400, 220);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var nameLabel = new Label { Text = "Profile Name:", Location = new(15, 18), AutoSize = true };
        _nameBox = new TextBox { Location = new(120, 15), Width = 245 };

        var hotkeyLabel = new Label { Text = "Hotkey:", Location = new(15, 55), AutoSize = true };
        _ctrlCheck = new CheckBox { Text = "Ctrl", Location = new(120, 53), Checked = true, AutoSize = true };
        _altCheck = new CheckBox { Text = "Alt", Location = new(175, 53), Checked = true, AutoSize = true };
        _shiftCheck = new CheckBox { Text = "Shift", Location = new(225, 53), AutoSize = true };

        _keyCombo = new ComboBox
        {
            Location = new(295, 52),
            Width = 70,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _keyCombo.Items.Add("(none)");
        for (int i = 0; i <= 9; i++) _keyCombo.Items.Add(i.ToString());
        for (char c = 'A'; c <= 'Z'; c++) _keyCombo.Items.Add(c.ToString());
        for (int i = 1; i <= 12; i++) _keyCombo.Items.Add($"F{i}");
        _keyCombo.SelectedIndex = 0;

        // Preview of current monitors
        var previewLabel = new Label { Text = "Current monitors:", Location = new(15, 90), AutoSize = true };
        var preview = new Label { Location = new(15, 110), AutoSize = true, ForeColor = System.Drawing.Color.DarkGray };
        try
        {
            var snapshot = DisplayManager.CaptureCurrentConfig();
            var lines = snapshot.Monitors.Select(m =>
            {
                var primary = m.IsPrimary ? " ★" : "";
                return $"  {m.FriendlyName} — {m.Resolution.Width}x{m.Resolution.Height} @ {m.RefreshRate}{primary}";
            });
            preview.Text = string.Join("\n", lines);
        }
        catch { preview.Text = "  (unable to read display config)"; }

        var okButton = new Button { Text = "Capture", DialogResult = DialogResult.OK, Location = new(200, 150), Width = 80 };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new(285, 150), Width = 80 };

        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.AddRange([nameLabel, _nameBox, hotkeyLabel, _ctrlCheck, _altCheck, _shiftCheck,
            _keyCombo, previewLabel, preview, okButton, cancelButton]);
    }
}
