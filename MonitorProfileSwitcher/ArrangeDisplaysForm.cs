using System.Drawing;
using MonitorProfileSwitcher.Native;

namespace MonitorProfileSwitcher;

/// <summary>
/// A replacement for Windows' Settings → Display for this app's users: arrange, enable and
/// disable every monitor, stage any number of changes and apply them in ONE operation, then
/// save the result as a profile. Remembers monitors that are not connected right now, and
/// refreshes itself when Windows reports a display or device change.
/// </summary>
internal sealed class ArrangeDisplaysForm : Form
{
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DEVICECHANGE = 0x0219;

    private readonly ProfileManager _profileManager;
    private readonly Func<DisplayProfile, Task> _applySavedProfile;
    private readonly Action _profilesChanged;

    private readonly MonitorLibrary _library;
    private DisplayInventory? _inventory;
    /// <summary>Usual-place coordinates → live coordinates, from the last refresh.</summary>
    private Point _homeOffset;
    private List<ArrangedMonitor> _monitors = [];
    private Dictionary<string, string> _baseline = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy;
    private bool _populating;

    private readonly DisplayCanvas _canvas;
    private readonly System.Windows.Forms.Timer _refreshDebounce = new() { Interval = 1200 };

    // Selected-monitor panel
    private readonly Label _titleLabel = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f, FontStyle.Bold) };
    private readonly TextBox _nameBox = new() { Width = 260 };
    private readonly Label _detailLabel = new() { AutoSize = true, MaximumSize = new Size(270, 0), ForeColor = SystemColors.GrayText };
    private readonly CheckBox _enabledCheck = new() { Text = "Use this display", AutoSize = true };
    private readonly CheckBox _primaryCheck = new() { Text = "Make this my main display", AutoSize = true };
    private readonly ComboBox _resolutionCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox _rateCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox _orientationCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Button _deleteButton = new() { Text = "Delete monitor...", AutoSize = true };
    private readonly Button _restoreHomesButton = new() { Text = "Restore usual places", AutoSize = true, Height = 30, Visible = false };

    // Bottom bar
    private readonly Label _statusLabel = new() { AutoSize = true, Padding = new Padding(2, 6, 0, 0) };
    private readonly ComboBox _profileCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
    private readonly CheckBox _confirmCheck = new() { Text = "Ask to keep changes", AutoSize = true, Padding = new Padding(0, 6, 8, 0) };
    private readonly Button _applyButton = new() { Text = "Apply", Width = 90, Height = 30 };
    private readonly Button _discardButton = new() { Text = "Discard changes", AutoSize = true, Height = 30 };
    private readonly Button _saveButton = new() { Text = "Save as Profile...", AutoSize = true, Height = 30 };
    private readonly Button _refreshButton = new() { Text = "Refresh", AutoSize = true, Height = 30 };
    private readonly Button _identifyButton = new() { Text = "Identify", AutoSize = true, Height = 30 };

    private static readonly (string Text, DISPLAYCONFIG_ROTATION Rotation)[] Orientations =
    [
        ("Landscape", DISPLAYCONFIG_ROTATION.IDENTITY),
        ("Portrait (90°)", DISPLAYCONFIG_ROTATION.ROTATE90),
        ("Landscape, flipped (180°)", DISPLAYCONFIG_ROTATION.ROTATE180),
        ("Portrait, flipped (270°)", DISPLAYCONFIG_ROTATION.ROTATE270),
    ];

    public ArrangeDisplaysForm(ProfileManager profileManager, MonitorLibrary library,
        Func<DisplayProfile, Task> applySavedProfile, Action profilesChanged)
    {
        _profileManager = profileManager;
        _library = library;
        _applySavedProfile = applySavedProfile;
        _profilesChanged = profilesChanged;

        Text = "Arrange Displays — Monitor Profile Switcher";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1180, 720);
        MinimumSize = new Size(900, 560);
        Font = SystemFonts.MessageBoxFont ?? Font;

        _canvas = new DisplayCanvas { Dock = DockStyle.Fill, Font = Font };
        _canvas.SelectionChanged += (_, _) => ShowSelected();
        _canvas.ArrangementChanged += (_, _) => OnEdited();
        _canvas.CommandRequested += OnMonitorCommand;
        _canvas.OffMonitorMoved += monitor =>
        {
            // Not something to apply: just where it goes next time it is on (or plugged in).
            _library.SetHomePositions([(monitor.DevicePath, monitor.X - _homeOffset.X, monitor.Y - _homeOffset.Y)]);
            monitor.Home = new Point(monitor.X, monitor.Y);
            UpdateStatus($"Display {monitor.Number} will go here when it is next {(monitor.CanEnable ? "enabled" : "plugged in")}");
        };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(8) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_canvas, 0, 0);
        root.Controls.Add(BuildSidePanel(), 1, 0);
        root.Controls.Add(_statusLabel, 0, 1);
        root.SetColumnSpan(_statusLabel, 2);
        var bottom = BuildBottomBar();
        root.Controls.Add(bottom, 0, 2);
        root.SetColumnSpan(bottom, 2);
        Controls.Add(root);

        _refreshDebounce.Tick += async (_, _) =>
        {
            _refreshDebounce.Stop();
            if (!_busy) await RefreshFromLiveAsync(force: false, reason: "Display change detected");
        };
        Shown += async (_, _) => await RefreshFromLiveAsync(force: true, reason: null);
        FormClosed += (_, _) => _refreshDebounce.Dispose();
    }

    // ---- layout of the window --------------------------------------------------------------

    private Control BuildSidePanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(10, 4, 0, 0),
        };
        Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 10, 0, 2) };

        _nameBox.Leave += (_, _) => CommitNickname();
        _nameBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitNickname(); } };
        _enabledCheck.CheckedChanged += (_, _) => OnEnabledToggled();
        _primaryCheck.CheckedChanged += (_, _) => OnPrimaryToggled();
        _resolutionCombo.SelectedIndexChanged += (_, _) => OnResolutionChosen();
        _rateCombo.SelectedIndexChanged += (_, _) => OnRateChosen();
        _orientationCombo.Items.AddRange(Orientations.Select(o => (object)o.Text).ToArray());
        _orientationCombo.SelectedIndexChanged += (_, _) => OnOrientationChosen();
        _deleteButton.Click += (_, _) => { if (_canvas.Selected is { } monitor) DeleteMonitor(monitor); };

        panel.Controls.AddRange([
            _titleLabel,
            Caption("Your name for it:"), _nameBox,
            _detailLabel,
            new Label { Height = 6 },
            _enabledCheck, _primaryCheck,
            Caption("Resolution:"), _resolutionCombo,
            Caption("Refresh rate:"), _rateCombo,
            Caption("Orientation:"), _orientationCombo,
            new Label { Height = 10 },
            _deleteButton,
            new Label
            {
                Text = "Drag displays to arrange them; they snap edge to edge. Right-click a display to enable, disable or " +
                       "delete it. Disabled displays stay in place (dashed) so they come back to the same spot; unplugged " +
                       "ones are dotted outlines in their usual place. Arrow keys nudge the selected display. " +
                       "Nothing changes until you press Apply.",
                AutoSize = true,
                MaximumSize = new Size(270, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 18, 0, 0),
            },
        ]);
        return panel;
    }

    private Control BuildBottomBar()
    {
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var left = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        _refreshButton.Click += async (_, _) => await RefreshFromLiveAsync(force: !HasChanges, reason: "Refreshed");
        _identifyButton.Click += (_, _) => Identify();
        _profileCombo.SelectedIndexChanged += async (_, _) => await LoadSelectedProfileAsync();
        _restoreHomesButton.Click += (_, _) =>
        {
            var moved = ArrangementModel.RestoreHomes(_monitors);
            _canvas.Invalidate();
            OnEdited();
            UpdateStatus(moved == 0 ? "Every display is already in its usual place" : $"Moved {moved} display(s) back — press Apply");
        };
        left.Controls.AddRange([_refreshButton, _identifyButton,
            new Label { Text = "Load profile:", AutoSize = true, Padding = new Padding(10, 8, 0, 0) }, _profileCombo, _restoreHomesButton]);

        var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        _confirmCheck.Checked = _library.ConfirmAfterApply;
        _confirmCheck.CheckedChanged += (_, _) => _library.ConfirmAfterApply = _confirmCheck.Checked;
        _discardButton.Click += async (_, _) => await RefreshFromLiveAsync(force: true, reason: "Changes discarded");
        _applyButton.Click += async (_, _) => await ApplyAsync();
        _saveButton.Click += async (_, _) => await SaveAsProfileAsync();
        right.Controls.AddRange([_confirmCheck, _discardButton, _applyButton, _saveButton]);

        bar.Controls.Add(left, 0, 0);
        bar.Controls.Add(right, 1, 0);
        return bar;
    }

    // ---- live state ------------------------------------------------------------------------

    private bool HasChanges => ArrangementModel.CountChanges(_monitors, _baseline) > 0;

    /// <summary>Re-reads the displays. With unapplied edits (and not forced) the edits are kept
    /// and only what is connected is updated, so a monitor waking up does not wipe your work.</summary>
    /// <param name="recordHome">After a kept Apply: the switched-off/unplugged monitors' spots in the
    /// new live coordinates, recorded with the live layout as everyone's usual place.</param>
    private async Task RefreshFromLiveAsync(bool force, string? reason,
        IReadOnlyCollection<(string DevicePath, int X, int Y)>? recordHome = null)
    {
        try
        {
            SetBusy(true, "Reading displays…");
            var inventory = await Task.Run(DisplayInventory.Query);
            _library.Observe(inventory);
            if (recordHome != null)
                _library.RecordHome(inventory.ActivePositions(), recordHome);
            _inventory = inventory;
            var fresh = ArrangementModel.Build(inventory, _library, out _homeOffset);
            var keepEdits = !force && HasChanges;
            if (keepEdits)
                MergeAvailability(fresh);
            else
                _monitors = fresh;
            _baseline = ArrangementModel.Snapshot(fresh);
            _canvas.SetMonitors(_monitors);
            ReloadProfileCombo();
            SetBusy(false, null);
            UpdateStatus(keepEdits ? $"{reason}; your unapplied changes were kept" : reason);
        }
        catch (Exception ex)
        {
            SetBusy(false, null);
            AppLog.Error("Arrange Displays could not read the display configuration.", ex);
            UpdateStatus($"Could not read displays: {ex.Message}");
        }
    }

    private void MergeAvailability(List<ArrangedMonitor> fresh)
    {
        var byPath = fresh.ToDictionary(monitor => monitor.DevicePath, StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in _monitors.ToList())
        {
            if (!byPath.TryGetValue(monitor.DevicePath, out var now))
            {
                _monitors.Remove(monitor);
                continue;
            }
            monitor.Availability = now.Availability;
            monitor.AdapterName = now.AdapterName;
            monitor.Connector = now.Connector;
            monitor.GdiDeviceName = now.GdiDeviceName;
            monitor.SourceId = now.SourceId;
            monitor.Home = now.Home;
            monitor.HasRememberedSpot = now.HasRememberedSpot;
            if (now.Modes.Count > 0) monitor.Modes = now.Modes;
            if (!monitor.CanEnable) monitor.Enabled = false;
        }
        foreach (var monitor in fresh.Where(f => _monitors.All(m => !m.DevicePath.Equals(f.DevicePath, StringComparison.OrdinalIgnoreCase))))
            _monitors.Add(monitor);
        for (int i = 0; i < _monitors.Count; i++)
            _monitors[i].Number = fresh.FirstOrDefault(f => f.DevicePath.Equals(_monitors[i].DevicePath, StringComparison.OrdinalIgnoreCase))?.Number ?? i + 1;
        ArrangementModel.MakeContiguous(_monitors);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Any display/device change: re-read shortly after (debounced; DP link training and
        // DisplayLink re-enumeration arrive as bursts of messages).
        if ((m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_DEVICECHANGE) && !_busy && IsHandleCreated)
        {
            _refreshDebounce.Stop();
            _refreshDebounce.Start();
        }
    }

    // ---- editing ---------------------------------------------------------------------------

    private void OnEdited()
    {
        ShowSelected();
        UpdateStatus(null);
    }

    private void ShowSelected()
    {
        var monitor = _canvas.Selected;
        _populating = true;
        try
        {
            var has = monitor != null;
            foreach (Control control in new Control[] { _nameBox, _enabledCheck, _primaryCheck, _resolutionCombo, _rateCombo, _orientationCombo })
                control.Enabled = has && !_busy;
            _deleteButton.Enabled = has && !_busy;
            if (monitor == null)
            {
                _titleLabel.Text = "Select a display";
                _nameBox.Text = "";
                _detailLabel.Text = "";
                return;
            }

            _titleLabel.Text = $"Display {monitor.Number}";
            _nameBox.Text = monitor.Name;
            _detailLabel.Text = DescribeDetail(monitor);

            _enabledCheck.Checked = monitor.Enabled;
            _enabledCheck.Enabled = !_busy && monitor.CanEnable;
            _primaryCheck.Checked = monitor.Primary;
            _primaryCheck.Enabled = !_busy && monitor.Enabled && !monitor.Primary;

            // The Surround desktop's mode belongs to the NVIDIA grid; only its position is editable here.
            var modeEditable = !_busy && monitor.CanEnable && !monitor.IsSurround;
            FillResolutions(monitor);
            FillRates(monitor);
            _resolutionCombo.Enabled = _rateCombo.Enabled = _orientationCombo.Enabled = modeEditable;
            _orientationCombo.SelectedIndex = Math.Max(0, Array.FindIndex(Orientations, o => o.Rotation == monitor.Rotation));
        }
        finally
        {
            _populating = false;
        }
    }

    private static string DescribeDetail(ArrangedMonitor monitor)
    {
        var lines = new List<string>
        {
            $"Detected as: {(monitor.EdidName.Length > 0 ? monitor.EdidName : "(no name in EDID)")} · {monitor.EdidId}",
            $"Connected to: {(monitor.AdapterName.Length > 0 ? monitor.AdapterName : "unknown adapter")}" +
                (monitor.ConnectorText.Length > 0 ? $" via {monitor.ConnectorText}" : ""),
        };
        lines.Add(monitor.Availability switch
        {
            MonitorAvailability.Connected when monitor.Enabled => $"Status: on{(monitor.GdiDeviceName.Length > 0 ? $" ({monitor.GdiDeviceName.Replace(@"\\.\", "")})" : "")}",
            MonitorAvailability.Connected => "Status: connected, switched off",
            MonitorAvailability.InSurround => "Status: one of the panels in the active NVIDIA Surround display",
            _ => $"Status: not connected{(monitor.LastConnected is { } seen ? $" — last seen {seen.ToLocalTime():yyyy-MM-dd HH:mm}" : "")}",
        });
        if (monitor.IsSurround)
            lines.Add("This is the combined NVIDIA Surround display. Drag it off the layout to turn Surround off.");
        return string.Join(Environment.NewLine, lines);
    }

    private sealed record ResolutionItem(uint Width, uint Height, bool Recommended)
    {
        public override string ToString() => $"{Width} × {Height}{(Recommended ? "  (recommended)" : "")}";
    }

    private sealed record RateItem(uint Hz, MonitorRefreshRate? Exact)
    {
        public override string ToString() => Exact is { IsSpecified: true } exact && Math.Abs(exact.ToHz() - Math.Round(exact.ToHz())) >= 0.005
            ? $"{exact.ToHz():0.00} Hz"
            : $"{Hz} Hz";
    }

    private void FillResolutions(ArrangedMonitor monitor)
    {
        _resolutionCombo.Items.Clear();
        var sizes = monitor.Modes.Select(mode => (mode.Width, mode.Height)).Distinct().ToList();
        if (!sizes.Contains((monitor.Width, monitor.Height)))
            sizes.Insert(0, (monitor.Width, monitor.Height));
        if (monitor.Preferred is { } preferred && !sizes.Contains((preferred.Width, preferred.Height)))
            sizes.Add((preferred.Width, preferred.Height));
        foreach (var (width, height) in sizes)
        {
            var recommended = monitor.Preferred is { } p && p.Width == width && p.Height == height;
            _resolutionCombo.Items.Add(new ResolutionItem(width, height, recommended));
        }
        _resolutionCombo.SelectedIndex = sizes.IndexOf((monitor.Width, monitor.Height));
    }

    private void FillRates(ArrangedMonitor monitor)
    {
        _rateCombo.Items.Clear();
        var currentHz = monitor.Rate.IsSpecified ? (uint)Math.Round(monitor.Rate.ToHz()) : 0;
        var rates = monitor.Modes.Where(mode => mode.Width == monitor.Width && mode.Height == monitor.Height)
            .Select(mode => mode.Hz).Distinct().OrderByDescending(hz => hz).ToList();
        if (currentHz > 0 && !rates.Contains(currentHz))
            rates.Insert(0, currentHz);
        foreach (var hz in rates)
            _rateCombo.Items.Add(new RateItem(hz, hz == currentHz ? monitor.Rate : null));
        if (rates.Count == 0)
            _rateCombo.Items.Add(new RateItem(0, null) { });
        _rateCombo.SelectedIndex = Math.Max(0, rates.IndexOf(currentHz));
    }

    private ArrangedMonitor? EditableSelection => _populating || _busy ? null : _canvas.Selected;

    private void OnEnabledToggled()
    {
        if (EditableSelection is not { } monitor || monitor.Enabled == _enabledCheck.Checked) return;
        if (!SetEnabled(monitor, _enabledCheck.Checked))
        {
            _populating = true;
            _enabledCheck.Checked = monitor.Enabled;
            _populating = false;
        }
    }

    private void OnMonitorCommand(ArrangedMonitor monitor, MonitorCommand command)
    {
        if (_busy) return;
        _canvas.Selected = monitor;
        switch (command)
        {
            case MonitorCommand.Enable: SetEnabled(monitor, true); break;
            case MonitorCommand.Disable: SetEnabled(monitor, false); break;
            case MonitorCommand.MakePrimary: MakePrimary(monitor); break;
            case MonitorCommand.Delete: DeleteMonitor(monitor); break;
        }
    }

    /// <summary>
    /// Switching a display off leaves it, and everything else, exactly where it is: it stays in
    /// the layout (dashed) so switching it back on returns it to the same spot. Applying closes
    /// any gap Windows needs closed; the off monitor's spot is remembered either way.
    /// </summary>
    private bool SetEnabled(ArrangedMonitor monitor, bool enabled)
    {
        if (monitor.Enabled == enabled) return true;
        if (enabled && !monitor.CanEnable)
        {
            UpdateStatus($"{monitor.Name} is not connected");
            return false;
        }
        if (!enabled && ArrangementModel.Enabled(_monitors).Count == 1)
        {
            UpdateStatus("At least one display has to stay on");
            return false;
        }

        monitor.Enabled = enabled;
        // Nothing moves on enable/disable: several toggles in a row pass through layouts that
        // are only temporary, so pulling monitors together at each step scrambles the result.
        // The one thing that cannot stand is two enabled monitors on top of each other.
        if (enabled && _monitors.Any(other => other != monitor && other.Enabled && other.Bounds.IntersectsWith(monitor.Bounds)))
            ArrangementModel.Attach(monitor, _monitors);
        ArrangementModel.EnsurePrimary(_monitors);
        _canvas.Invalidate();
        OnEdited();
        return true;
    }

    private void MakePrimary(ArrangedMonitor monitor)
    {
        if (!monitor.Enabled) return;
        foreach (var other in _monitors)
            other.Primary = other == monitor;
        _canvas.Invalidate();
        OnEdited();
    }

    private void OnPrimaryToggled()
    {
        if (EditableSelection is not { Enabled: true } monitor || !_primaryCheck.Checked) return;
        MakePrimary(monitor);
    }

    private void OnResolutionChosen()
    {
        if (EditableSelection is not { } monitor || _resolutionCombo.SelectedItem is not ResolutionItem item) return;
        if (item.Width == monitor.Width && item.Height == monitor.Height) return;
        var hz = monitor.Rate.IsSpecified ? (uint)Math.Round(monitor.Rate.ToHz()) : 0;
        monitor.Width = item.Width;
        monitor.Height = item.Height;
        // Keep the same refresh rate if the new resolution offers it, otherwise its best one.
        var available = monitor.Modes.Where(mode => mode.Width == item.Width && mode.Height == item.Height).Select(mode => mode.Hz).ToList();
        var newHz = available.Contains(hz) ? hz : available.DefaultIfEmpty(hz).Max();
        monitor.Rate = newHz > 0 ? _library.RateFor(monitor.DevicePath, item.Width, item.Height, newHz) : new MonitorRefreshRate();
        Resettle(monitor);
    }

    private void OnRateChosen()
    {
        if (EditableSelection is not { } monitor || _rateCombo.SelectedItem is not RateItem item || item.Hz == 0) return;
        if (monitor.Rate.IsSpecified && (uint)Math.Round(monitor.Rate.ToHz()) == item.Hz) return;
        monitor.Rate = _library.RateFor(monitor.DevicePath, monitor.Width, monitor.Height, item.Hz);
        OnEdited();
    }

    private void OnOrientationChosen()
    {
        if (EditableSelection is not { } monitor || _orientationCombo.SelectedIndex < 0) return;
        var rotation = Orientations[_orientationCombo.SelectedIndex].Rotation;
        if (rotation == monitor.Rotation) return;
        monitor.Rotation = rotation;
        Resettle(monitor);
    }

    /// <summary>After a size change the display may overlap or float: settle it against its neighbours.</summary>
    private void Resettle(ArrangedMonitor monitor)
    {
        if (monitor.Enabled)
        {
            ArrangementModel.Attach(monitor, _monitors);
            ArrangementModel.MakeContiguous(_monitors);
        }
        _canvas.Invalidate();
        OnEdited();
    }

    private void CommitNickname()
    {
        if (_populating || _canvas.Selected is not { } monitor) return;
        var name = _nameBox.Text.Trim();
        var current = _library.Find(monitor.DevicePath);
        if (current == null || (current.Nickname ?? "") == name || name == current.DisplayName) return;
        _library.SetNickname(monitor.DevicePath, name);
        monitor.Name = _library.Find(monitor.DevicePath)?.DisplayName ?? name;
        _canvas.Invalidate();
        UpdateStatus($"Named display {monitor.Number} \"{monitor.Name}\"");
    }

    /// <summary>Removes a monitor from the app's memory: its name, usual place, modes and rates.
    /// Saved profiles are not touched. A connected monitor comes straight back as a fresh entry.</summary>
    private async void DeleteMonitor(ArrangedMonitor monitor)
    {
        if (_busy) return;
        var connected = monitor.Availability != MonitorAvailability.NotConnected;
        var message = $"Delete '{monitor.Name}' ({DisplayCanvas.DetectedLine(monitor)}) from the remembered monitors?\n\n" +
            "Its name, usual place and saved modes are forgotten. Profiles that use it are not changed." +
            (connected ? "\n\nIt is connected right now, so it will reappear straight away as a new monitor with those reset." : "");
        if (MessageBox.Show(this, message, "Delete Monitor", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        _library.Forget(monitor.DevicePath);
        if (connected)
        {
            await RefreshFromLiveAsync(force: !HasChanges, reason: $"Reset {monitor.Name}");
            return;
        }
        _monitors.Remove(monitor);
        _canvas.SetMonitors(_monitors);
        UpdateStatus($"Deleted {monitor.Name}");
    }

    private void Identify()
    {
        IdentifyOverlay.Show(_monitors
            .Where(monitor => monitor.Enabled && monitor.GdiDeviceName.Length > 0 && _baseline.TryGetValue(monitor.DevicePath, out var state) && state != "off")
            .Select(monitor => (monitor.GdiDeviceName, monitor.Number, monitor.Name)));
    }

    // ---- profiles --------------------------------------------------------------------------

    private void ReloadProfileCombo()
    {
        _populating = true;
        _profileCombo.Items.Clear();
        _profileCombo.Items.Add("(choose a profile)");
        foreach (var profile in _profileManager.Profiles)
            _profileCombo.Items.Add(profile.Name);
        _profileCombo.SelectedIndex = 0;
        _populating = false;
    }

    private async Task LoadSelectedProfileAsync()
    {
        if (_populating || _profileCombo.SelectedIndex <= 0) return;
        var name = _profileCombo.SelectedItem!.ToString()!;
        _profileManager.Reload();
        var profile = _profileManager.GetProfile(name);
        ReloadProfileCombo();
        if (profile == null) return;

        // Switching NVIDIA Surround on/off (or to another grid) is a driver operation the
        // canvas cannot stage; hand that to the normal profile switch.
        var liveSurround = _inventory?.Surround;
        var surroundDiffers = profile.NvidiaSurround != null
            ? liveSurround == null || !NvidiaSurroundManager.Matches(liveSurround, profile.NvidiaSurround)
            : profile.IsNvidiaSurround != (liveSurround != null);
        if (surroundDiffers)
        {
            if (MessageBox.Show(this, $"'{profile.Name}' turns NVIDIA Surround {(profile.IsNvidiaSurround ? "on" : "off")}, which the editor can't stage. Apply the profile now?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                SetBusy(true, $"Applying '{profile.Name}'…");
                try { await _applySavedProfile(profile); }
                finally { SetBusy(false, null); }
                await RefreshFromLiveAsync(force: true, reason: $"Applied '{profile.Name}'");
            }
            return;
        }

        var saved = profile.Monitors.ToDictionary(monitor => monitor.DevicePath, StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in _monitors)
        {
            if (saved.TryGetValue(monitor.DevicePath, out var info) && monitor.CanEnable)
            {
                monitor.Enabled = true;
                monitor.Primary = info.IsPrimary;
                monitor.X = info.Position.X;
                monitor.Y = info.Position.Y;
                monitor.Width = info.Resolution.Width;
                monitor.Height = info.Resolution.Height;
                monitor.Rotation = info.Rotation;
                if (info.RefreshRate.IsSpecified) monitor.Rate = info.RefreshRate.Clone();
            }
            else
            {
                monitor.Enabled = false;
            }
        }
        if (ArrangementModel.Enabled(_monitors).Count == 0)
        {
            await RefreshFromLiveAsync(force: true, reason: null);
            UpdateStatus($"None of '{profile.Name}'s monitors are connected");
            return;
        }
        ArrangementModel.EnsurePrimary(_monitors);
        ArrangementModel.MakeContiguous(_monitors);
        ArrangementModel.TidyOffMonitors(_monitors);
        _canvas.SetMonitors(_monitors);

        var library = _library;
        var missing = profile.Monitors.Where(info => _monitors.All(m => !m.DevicePath.Equals(info.DevicePath, StringComparison.OrdinalIgnoreCase) || !m.CanEnable))
            .Where(info => info != profile.SurroundMonitor).Select(library.NameFor).ToList();
        var leftOut = missing.Count > 0 ? $" (not connected, left out: {string.Join(", ", missing)})" : "";
        UpdateStatus(HasChanges
            ? $"Loaded '{profile.Name}'{leftOut} — adjust it or press Apply"
            : $"'{profile.Name}' already matches what is on screen{leftOut}");
    }

    private async Task SaveAsProfileAsync()
    {
        if (HasChanges)
        {
            var answer = MessageBox.Show(this, "A profile saves what is on screen. Apply your changes first?", Text,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK || !await ApplyAsync() || HasChanges)
                return;
        }

        _profileManager.Reload();
        using var dialog = new CaptureDialog(_profileManager.Profiles, "Save Layout as Profile");
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var profile = _profileManager.CaptureProfile(dialog.ProfileName, dialog.SelectedHotkey);
            AppLog.Info($"Arrange Displays saved profile '{profile.Name}' ({profile.DisplaySummary}) with hotkey {profile.Hotkey?.ToString() ?? "none"}.");
            _profilesChanged();
            ReloadProfileCombo();
            UpdateStatus($"Saved profile '{profile.Name}'");
        }
        catch (Exception ex)
        {
            AppLog.Error("Arrange Displays could not save a profile.", ex);
            MessageBox.Show(this, $"Could not save the profile: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---- apply -----------------------------------------------------------------------------

    /// <summary>Applies every staged change in one operation. Returns true if the new layout is in place and kept.</summary>
    private async Task<bool> ApplyAsync()
    {
        if (_busy) return false;
        if (!HasChanges)
        {
            UpdateStatus("Nothing to apply");
            return true;
        }

        DisplayProfile target;
        try { target = ArrangementModel.ToProfile(_monitors, _inventory?.Surround); }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // Where the switched-off and unplugged monitors sit, in the coordinates Windows will use
        // once this is applied — recorded as their usual place if the change is kept.
        var offPositions = ArrangementModel.OffMonitorPositions(_monitors);

        SetBusy(true, "Applying…");
        DisplayProfile? previous = null;
        try { previous = await Task.Run(ArrangementModel.CaptureCurrent); }
        catch (Exception ex) { AppLog.Warn($"Arrange Displays could not capture the current layout for revert: {ex.Message}"); }

        var operationId = Guid.NewGuid().ToString("N")[..8];
        Exception? failure = null;
        try
        {
            await Task.Run(() => ProfileApplyRunner.Apply(target, [], operationId, "Arrange Displays", manageWindowLayouts: false));
        }
        catch (ProfileApplyBusyException ex)
        {
            SetBusy(false, null);
            UpdateStatus(ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // The layout can arrive after the runner stopped waiting; if it did, carry on as applied.
        if (failure != null && await SettledAnywayAsync(target, operationId, failure))
            failure = null;

        if (failure is { } error)
        {
            SetBusy(false, null);
            AppLog.Error($"[{operationId}] Arrange Displays apply failed.", error);
            var unchanged = false;
            if (previous != null)
            {
                try { unchanged = await Task.Run(() => IpcDisplayInspector.Capture().Matches(previous)); }
                catch (Exception readError) { AppLog.Warn($"Could not compare the layout after a failed apply: {readError.Message}"); }
            }
            if (previous == null || unchanged)
            {
                MessageBox.Show(this, $"Windows did not accept the layout:\n\n{error.Message}" +
                    (unchanged ? "\n\nYour displays were not changed." : ""), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (MessageBox.Show(this, $"Windows did not accept the whole layout:\n\n{error.Message}\n\nGo back to the previous layout?", Text,
                         MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                await RevertAsync(previous);
            }
            await RefreshFromLiveAsync(force: true, reason: "Apply failed");
            return false;
        }

        SetBusy(false, null);
        var kept = true;
        if (_library.ConfirmAfterApply && previous != null)
        {
            UpdateStatus("Waiting for you to keep or revert…");
            kept = await KeepChangesPrompt.AskAsync(15);
            if (!kept)
                await RevertAsync(previous);
        }
        await RefreshFromLiveAsync(force: true, reason: kept ? "Applied" : "Reverted to the previous layout",
            recordHome: kept ? offPositions : null);
        Activate();
        return kept;
    }

    /// <summary>After a reported failure, looks once more (for a few seconds) at whether the
    /// requested layout is in place after all — monitors coming back on can be slow to settle.</summary>
    private static async Task<bool> SettledAnywayAsync(DisplayProfile target, string operationId, Exception failure)
    {
        if (failure is ProfileApplyBusyException) return false;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (await Task.Run(() => IpcDisplayInspector.Capture().Matches(target)))
                {
                    AppLog.Warn($"[{operationId}] Apply reported '{failure.Message}', but the requested layout is now in place; treating it as applied.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[{operationId}] Could not re-check the layout after a failed apply: {ex.Message}");
            }
            await Task.Delay(500);
        }
        return false;
    }

    private async Task RevertAsync(DisplayProfile previous)
    {
        SetBusy(true, "Reverting…");
        try
        {
            await Task.Run(() => ProfileApplyRunner.Apply(previous, [], Guid.NewGuid().ToString("N")[..8], "Arrange Displays revert", manageWindowLayouts: false));
        }
        catch (Exception ex)
        {
            AppLog.Error("Arrange Displays could not revert to the previous layout.", ex);
            MessageBox.Show(this, $"Could not restore the previous layout: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    // ---- status ----------------------------------------------------------------------------

    private void SetBusy(bool busy, string? message)
    {
        _busy = busy;
        UseWaitCursor = busy;
        foreach (Control control in new Control[] { _applyButton, _discardButton, _saveButton, _refreshButton, _identifyButton, _profileCombo, _canvas })
            control.Enabled = !busy;
        if (message != null) _statusLabel.Text = message;
        if (!busy) ShowSelected();
    }

    private void UpdateStatus(string? message)
    {
        var changes = ArrangementModel.CountChanges(_monitors, _baseline);
        var pending = changes == 0 ? "No unapplied changes" : $"{changes} display{(changes == 1 ? "" : "s")} changed — press Apply";
        var floating = changes == 0 ? [] : ArrangementModel.Floating(_monitors);
        if (floating.Count > 0)
            pending += $" (display{(floating.Count == 1 ? "" : "s")} {string.Join(", ", floating.Select(monitor => monitor.Number))} " +
                $"{(floating.Count == 1 ? "isn't" : "aren't")} touching the others; Apply will move {(floating.Count == 1 ? "it" : "them")} alongside)";
        _statusLabel.Text = message == null ? pending : $"{message}. {pending}";
        _applyButton.Enabled = !_busy && changes > 0;
        _discardButton.Enabled = !_busy && changes > 0;
        // Typically Windows dropping a just-plugged-in monitor somewhere new.
        var away = _monitors.Where(monitor => monitor.AwayFromHome).ToList();
        _restoreHomesButton.Visible = away.Count > 0;
        _restoreHomesButton.Enabled = !_busy;
        if (away.Count > 0 && message == null && changes == 0)
            _statusLabel.Text = $"{string.Join(", ", away.Select(monitor => monitor.Name))} not in the usual place — use Restore usual places to put {(away.Count == 1 ? "it" : "them")} back";
        _applyButton.Font = new Font(Font, changes > 0 ? FontStyle.Bold : FontStyle.Regular);
    }
}
