namespace MonitorProfileSwitcher;

internal sealed class ManageProfilesDialog : Form
{
    private readonly List<ProfileMetadataEdit> _edits;
    private readonly ListView _profileList;

    public IReadOnlyList<ProfileMetadataEdit> Edits => _edits;

    public ManageProfilesDialog(IReadOnlyList<DisplayProfile> profiles)
    {
        Text = "Manage Profiles";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Width = 760;
        Height = 440;

        _edits = profiles.Select(profile => new ProfileMetadataEdit
        {
            OriginalName = profile.Name,
            Name = profile.Name,
            DisplaySummary = profile.DisplaySummary,
            Hotkey = profile.Hotkey?.Clone(),
        }).ToList();

        Controls.Add(new Label
        {
            Text = "Select a profile to rename it, change its hotkey, or move it in the tray-menu order.",
            AutoSize = true,
            Left = 12,
            Top = 13,
        });

        _profileList = new ListView
        {
            Left = 12,
            Top = 40,
            Width = 570,
            Height = 310,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            MultiSelect = false,
        };
        _profileList.Columns.Add("Profile", 275);
        _profileList.Columns.Add("Hotkey", 135);
        _profileList.Columns.Add("Type", 140);
        _profileList.DoubleClick += (_, _) => RenameSelected();
        Controls.Add(_profileList);

        Controls.AddRange([
            CreateActionButton("Move Up", 598, 40, MoveSelectedUp),
            CreateActionButton("Move Down", 598, 76, MoveSelectedDown),
            CreateActionButton("Rename...", 598, 128, RenameSelected),
            CreateActionButton("Hotkey...", 598, 164, ChangeSelectedHotkey),
        ]);

        var saveButton = new Button { Text = "Save", Left = 566, Top = 360, Width = 80 };
        saveButton.Click += (_, _) => SaveChanges();
        Controls.Add(saveButton);

        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 652, Top = 360, Width = 80 };
        Controls.Add(cancelButton);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        RefreshList(_edits.Count > 0 ? 0 : -1);
    }

    private static Button CreateActionButton(string text, int left, int top, Action action)
    {
        var button = new Button { Text = text, Left = left, Top = top, Width = 134 };
        button.Click += (_, _) => action();
        return button;
    }

    private int SelectedIndex => _profileList.SelectedIndices.Count == 1 ? _profileList.SelectedIndices[0] : -1;

    private void RefreshList(int selectedIndex)
    {
        _profileList.BeginUpdate();
        _profileList.Items.Clear();
        foreach (var edit in _edits)
        {
            var item = new ListViewItem(edit.Name);
            item.SubItems.Add(edit.Hotkey?.ToString() ?? "(none)");
            item.SubItems.Add(edit.DisplaySummary);
            _profileList.Items.Add(item);
        }

        if (selectedIndex >= 0 && selectedIndex < _profileList.Items.Count)
        {
            _profileList.Items[selectedIndex].Selected = true;
            _profileList.Items[selectedIndex].Focused = true;
            _profileList.EnsureVisible(selectedIndex);
        }
        _profileList.EndUpdate();
    }

    private void MoveSelectedUp()
    {
        var index = SelectedIndex;
        if (index <= 0) return;
        (_edits[index - 1], _edits[index]) = (_edits[index], _edits[index - 1]);
        RefreshList(index - 1);
    }

    private void MoveSelectedDown()
    {
        var index = SelectedIndex;
        if (index < 0 || index >= _edits.Count - 1) return;
        (_edits[index + 1], _edits[index]) = (_edits[index], _edits[index + 1]);
        RefreshList(index + 1);
    }

    private void RenameSelected()
    {
        var index = SelectedIndex;
        if (index < 0) return;

        using var dialog = new RenameProfileDialog(_edits[index].Name);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _edits[index].Name = dialog.ProfileName;
        RefreshList(index);
    }

    private void ChangeSelectedHotkey()
    {
        var index = SelectedIndex;
        if (index < 0) return;

        using var dialog = new HotkeyDialog(_edits[index].Hotkey);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _edits[index].Hotkey = dialog.SelectedHotkey?.Clone();
        RefreshList(index);
    }

    private void SaveChanges()
    {
        foreach (var edit in _edits)
        {
            edit.Name = edit.Name.Trim();
            if (edit.Name.Length == 0)
            {
                MessageBox.Show(this, "Profile names cannot be blank.", "Manage Profiles", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        var duplicateName = _edits.GroupBy(edit => edit.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateName is not null)
        {
            MessageBox.Show(this, $"More than one profile is named '{duplicateName.Key}'. Profile names must be unique.",
                "Manage Profiles", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var duplicateHotkey = _edits.Where(edit => edit.Hotkey is not null)
            .GroupBy(edit => edit.Hotkey!.ToString(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateHotkey is not null)
        {
            MessageBox.Show(this, $"The hotkey {duplicateHotkey.Key} is assigned to more than one profile.",
                "Manage Profiles", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed class RenameProfileDialog : Form
    {
        private readonly TextBox _nameBox;
        public string ProfileName => _nameBox.Text.Trim();

        public RenameProfileDialog(string currentName)
        {
            Text = "Rename Profile";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Width = 430;
            Height = 150;

            Controls.Add(new Label { Text = "Profile name:", Left = 12, Top = 16, AutoSize = true });
            _nameBox = new TextBox { Text = currentName, Left = 105, Top = 12, Width = 295 };
            Controls.Add(_nameBox);

            var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 238, Top = 55, Width = 78 };
            var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 322, Top = 55, Width = 78 };
            Controls.AddRange([okButton, cancelButton]);
            AcceptButton = okButton;
            CancelButton = cancelButton;
            Shown += (_, _) => { _nameBox.Focus(); _nameBox.SelectAll(); };
        }
    }
}
