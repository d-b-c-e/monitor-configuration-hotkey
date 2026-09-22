namespace MonitorProfileSwitcher;

internal class HotkeyDialog : Form
{
    private readonly CheckBox _ctrlCheck;
    private readonly CheckBox _altCheck;
    private readonly CheckBox _shiftCheck;
    private readonly CheckBox _winCheck;
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
                Win = _winCheck.Checked,
                Key = _keyCombo.SelectedItem.ToString()!
            };
        }
    }

    public HotkeyDialog(HotkeyBinding? current = null)
    {
        Text = "Set Hotkey";
        Size = new System.Drawing.Size(390, 140);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        _ctrlCheck = new CheckBox { Text = "Ctrl", Location = new(15, 18), AutoSize = true };
        _altCheck = new CheckBox { Text = "Alt", Location = new(75, 18), AutoSize = true };
        _shiftCheck = new CheckBox { Text = "Shift", Location = new(130, 18), AutoSize = true };
        _winCheck = new CheckBox { Text = "Win", Location = new(195, 18), AutoSize = true };

        _keyCombo = new ComboBox
        {
            Location = new(255, 17),
            Width = 105,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _keyCombo.Items.Add("(none)");
        for (int i = 0; i <= 9; i++) _keyCombo.Items.Add(i.ToString());
        for (char c = 'A'; c <= 'Z'; c++) _keyCombo.Items.Add(c.ToString());
        for (int i = 1; i <= 12; i++) _keyCombo.Items.Add($"F{i}");
        if (current is not null)
        {
            _ctrlCheck.Checked = current.Ctrl;
            _altCheck.Checked = current.Alt;
            _shiftCheck.Checked = current.Shift;
            _winCheck.Checked = current.Win;
            _keyCombo.SelectedItem = current.Key;
        }
        else
        {
            _ctrlCheck.Checked = true;
            _altCheck.Checked = true;
        }

        if (_keyCombo.SelectedIndex < 0)
            _keyCombo.SelectedIndex = 0;

        var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new(195, 58), Width = 80 };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new(280, 58), Width = 80 };

        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.AddRange([_ctrlCheck, _altCheck, _shiftCheck, _winCheck, _keyCombo, okButton, cancelButton]);
    }
}
