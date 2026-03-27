namespace MonitorProfileSwitcher;

internal class HotkeyDialog : Form
{
    private readonly CheckBox _ctrlCheck;
    private readonly CheckBox _altCheck;
    private readonly CheckBox _shiftCheck;
    private readonly ComboBox _keyCombo;

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

    public HotkeyDialog()
    {
        Text = "Set Hotkey";
        Size = new System.Drawing.Size(320, 130);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        _ctrlCheck = new CheckBox { Text = "Ctrl", Location = new(15, 18), Checked = true, AutoSize = true };
        _altCheck = new CheckBox { Text = "Alt", Location = new(75, 18), Checked = true, AutoSize = true };
        _shiftCheck = new CheckBox { Text = "Shift", Location = new(130, 18), AutoSize = true };

        _keyCombo = new ComboBox
        {
            Location = new(200, 17),
            Width = 90,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _keyCombo.Items.Add("(none)");
        for (int i = 0; i <= 9; i++) _keyCombo.Items.Add(i.ToString());
        for (char c = 'A'; c <= 'Z'; c++) _keyCombo.Items.Add(c.ToString());
        for (int i = 1; i <= 12; i++) _keyCombo.Items.Add($"F{i}");
        _keyCombo.SelectedIndex = 0;

        var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new(120, 55), Width = 80 };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new(205, 55), Width = 80 };

        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.AddRange([_ctrlCheck, _altCheck, _shiftCheck, _keyCombo, okButton, cancelButton]);
    }
}
