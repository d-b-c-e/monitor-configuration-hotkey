using System.Reflection;
using MonitorProfileSwitcher.Native;

namespace MonitorProfileSwitcher;

internal class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ProfileManager _profileManager;
    private readonly Dictionary<int, DisplayProfile> _hotkeyMap = new();
    private int _nextHotkeyId = 1;
    private HiddenHotkeyWindow? _hotkeyWindow;
    private ReleaseInfo? _pendingUpdate;
    private FileSystemWatcher? _profileWatcher;
    private Control? _uiMarshal;
    private System.Windows.Forms.Timer? _reloadTimer;
    private System.Windows.Forms.Timer? _taskbarPoll;

    public TrayApplication()
    {
        _profileManager = new ProfileManager();
        _profileManager.Load();

        _trayIcon = new NotifyIcon
        {
            Icon = LoadEmbeddedIcon(),
            Text = "Monitor Profile Switcher",
            Visible = true,
        };

        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
                RebuildContextMenu();
        };

        RebuildContextMenu();

        _hotkeyWindow = new HiddenHotkeyWindow(OnHotkeyPressed, ReassertTrayIcon);
        RegisterAllHotkeys();

        SetupProfileWatcher();
        GuardAgainstMissingTrayIcon();

        // Check for updates on startup (fire-and-forget, rate-limited to once per 24h)
        if (UpdateService.ShouldCheck())
        {
            _ = CheckForUpdateAsync(silent: true);
        }
    }

    private void RebuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        // Profile list
        if (_profileManager.Profiles.Count > 0)
        {
            foreach (var profile in _profileManager.Profiles)
            {
                var hotkeyText = profile.Hotkey != null ? $"  ({profile.Hotkey})" : "";
                var monitorCount = profile.Monitors.Length;
                var item = menu.Items.Add(
                    $"{profile.Name}{hotkeyText}  [{monitorCount} monitor(s)]");
                item.Click += (_, _) => ApplyProfile(profile);
            }
            menu.Items.Add(new ToolStripSeparator());
        }

        // Capture
        var captureItem = menu.Items.Add("Capture Current Setup As...");
        captureItem.Click += (_, _) => CaptureCurrentSetup();

        // Manage
        if (_profileManager.Profiles.Count > 0)
        {
            var manageMenu = new ToolStripMenuItem("Manage Profiles");
            foreach (var profile in _profileManager.Profiles)
            {
                var profileMenu = new ToolStripMenuItem(profile.Name);

                var setHotkey = new ToolStripMenuItem("Set Hotkey...");
                setHotkey.Click += (_, _) => SetProfileHotkey(profile.Name);
                profileMenu.DropDownItems.Add(setHotkey);

                var recapture = new ToolStripMenuItem("Recapture (update to current)");
                recapture.Click += (_, _) =>
                {
                    _profileManager.CaptureProfile(profile.Name, profile.Hotkey);
                    ShowBalloon($"Profile '{profile.Name}' updated");
                };
                profileMenu.DropDownItems.Add(recapture);

                var delete = new ToolStripMenuItem("Delete");
                delete.Click += (_, _) =>
                {
                    _profileManager.DeleteProfile(profile.Name);
                    UnregisterAllHotkeys();
                    RegisterAllHotkeys();
                    ShowBalloon($"Profile '{profile.Name}' deleted");
                };
                profileMenu.DropDownItems.Add(delete);

                manageMenu.DropDownItems.Add(profileMenu);
            }
            menu.Items.Add(manageMenu);
            menu.Items.Add(new ToolStripSeparator());
        }

        // Show current config
        var showConfig = menu.Items.Add("Show Current Config");
        showConfig.Click += (_, _) =>
        {
            var desc = DisplayManager.DescribeCurrentConfig();
            MessageBox.Show(desc, "Current Display Configuration",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        // Update
        if (_pendingUpdate != null)
        {
            var updateItem = menu.Items.Add($"Update Available: v{_pendingUpdate.Version} ({_pendingUpdate.FormattedSize})");
            updateItem.Font = new Font(updateItem.Font, FontStyle.Bold);
            updateItem.Click += (_, _) => DownloadAndInstallUpdate(_pendingUpdate);
        }
        else
        {
            var checkItem = menu.Items.Add("Check for Updates");
            checkItem.Click += (_, _) => _ = CheckForUpdateAsync(silent: false);
        }

        var versionItem = menu.Items.Add($"v{UpdateService.GetCurrentVersion()}");
        versionItem.Enabled = false;

        menu.Items.Add(new ToolStripSeparator());

        // Exit
        var exitItem = menu.Items.Add("Exit");
        exitItem.Click += (_, _) =>
        {
            UnregisterAllHotkeys();
            _profileWatcher?.Dispose();
            StopTaskbarPoll();
            _trayIcon.Visible = false;
            Application.Exit();
        };

        _trayIcon.ContextMenuStrip = menu;
    }

    private void CaptureCurrentSetup()
    {
        using var dialog = new CaptureDialog(_profileManager.Profiles.Select(p => p.Name).ToList());
        if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.ProfileName))
        {
            var hotkey = dialog.SelectedHotkey;
            var profile = _profileManager.CaptureProfile(dialog.ProfileName, hotkey);

            UnregisterAllHotkeys();
            RegisterAllHotkeys();
            RebuildContextMenu();

            var hotkeyText = hotkey != null ? $" ({hotkey})" : "";
            ShowBalloon($"Captured '{profile.Name}'{hotkeyText} — {profile.Monitors.Length} monitor(s)");
        }
    }

    private void SetProfileHotkey(string profileName)
    {
        using var dialog = new HotkeyDialog();
        if (dialog.ShowDialog() == DialogResult.OK && dialog.SelectedHotkey != null)
        {
            _profileManager.SetHotkey(profileName, dialog.SelectedHotkey);
            UnregisterAllHotkeys();
            RegisterAllHotkeys();
            RebuildContextMenu();
            ShowBalloon($"Hotkey for '{profileName}' set to {dialog.SelectedHotkey}");
        }
    }

    private void ApplyProfile(DisplayProfile profile)
    {
        try
        {
            // Re-read profiles.json from disk first: this instance loaded profiles at
            // startup, and the file may have changed since (manual edit, restore from
            // backup, sync). Re-fetch by name so we apply the current saved monitors.
            _profileManager.Reload();
            var fresh = _profileManager.GetProfile(profile.Name) ?? profile;
            DisplayManager.ApplyProfile(fresh);
            ShowBalloon($"Switched to '{fresh.Name}'");
        }
        catch (Exception ex)
        {
            ShowBalloon($"Failed: {ex.Message}", ToolTipIcon.Error);
        }
    }

    /// <summary>
    /// Recover the tray icon when the shell was not ready to accept it.
    ///
    /// A logon scheduled task can start this app fractionally BEFORE Explorer. Tray icons
    /// are hosted by the taskbar, so with no taskbar yet Shell_NotifyIcon(NIM_ADD) has
    /// nothing to talk to and the icon is silently dropped. The app then runs on
    /// invisibly — hotkeys still work, since RegisterHotKey has nothing to do with the
    /// shell — so the only symptom is a missing icon, which reads as "it didn't start".
    ///
    /// Explorer broadcasts "TaskbarCreated" when it builds a taskbar, and WinForms'
    /// NotifyIcon re-adds itself on that (we listen too, via HiddenHotkeyWindow, which
    /// covers Explorer crashes/restarts). But that only rescues us if a broadcast
    /// actually arrives after the failed add. When the taskbar simply is not up yet we
    /// already know the add could not have landed, so wait for the shell and re-add
    /// explicitly rather than trusting a broadcast to arrive.
    ///
    /// Costs nothing on the normal path: if a taskbar exists at startup the icon
    /// registered fine and no timer is ever created.
    /// </summary>
    private void GuardAgainstMissingTrayIcon()
    {
        if (ShellApi.TaskbarExists())
            return;

        var waited = TimeSpan.Zero;
        var timeout = TimeSpan.FromMinutes(5);

        _taskbarPoll = new System.Windows.Forms.Timer { Interval = 1000 };
        _taskbarPoll.Tick += (_, _) =>
        {
            waited += TimeSpan.FromMilliseconds(_taskbarPoll!.Interval);

            if (ShellApi.TaskbarExists())
            {
                StopTaskbarPoll();
                ReassertTrayIcon();
            }
            else if (waited >= timeout)
            {
                // No shell after five minutes means something far stranger than a logon
                // race is going on; stop burning a timer on it.
                StopTaskbarPoll();
            }
        };
        _taskbarPoll.Start();
    }

    private void StopTaskbarPoll()
    {
        _taskbarPoll?.Stop();
        _taskbarPoll?.Dispose();
        _taskbarPoll = null;
    }

    /// <summary>Force a NIM_DELETE + NIM_ADD so the icon re-registers with whatever taskbar
    /// is live now. Only called when there is reason to believe the icon is missing —
    /// toggling a healthy one would send it to the back of the tray order.</summary>
    private void ReassertTrayIcon()
    {
        try
        {
            _trayIcon.Visible = false;
            _trayIcon.Visible = true;
        }
        catch
        {
            // Nothing useful to do, and not worth taking the app down over — the hotkeys
            // keep working with or without an icon.
        }
    }

    /// <summary>Watch profiles.json for external changes (manual edit, restore, sync) and
    /// reload + re-register hotkeys + rebuild the menu so the live app never runs on stale
    /// data. Failure here is non-fatal — watching is a convenience.</summary>
    private void SetupProfileWatcher()
    {
        try
        {
            // Hidden control whose handle is created on the UI thread; FileSystemWatcher
            // marshals its events onto this thread so the reload is UI-thread-safe.
            _uiMarshal = new Control();
            _ = _uiMarshal.Handle;

            _reloadTimer = new System.Windows.Forms.Timer { Interval = 300 };
            _reloadTimer.Tick += (_, _) =>
            {
                _reloadTimer!.Stop();
                ReloadProfilesFromDisk();
            };

            Directory.CreateDirectory(ProfileManager.StorageDir);
            _profileWatcher = new FileSystemWatcher(ProfileManager.StorageDir, ProfileManager.StorageFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                SynchronizingObject = _uiMarshal,
            };
            FileSystemEventHandler onChange = (_, _) => DebouncedReload();
            _profileWatcher.Changed += onChange;
            _profileWatcher.Created += onChange;
            _profileWatcher.Renamed += (_, _) => DebouncedReload();
            _profileWatcher.EnableRaisingEvents = true;
        }
        catch
        {
            // ignore — the reload-before-apply path still keeps applies correct.
        }
    }

    // Coalesce the burst of events a single save produces into one reload after things settle.
    private void DebouncedReload()
    {
        _reloadTimer?.Stop();
        _reloadTimer?.Start();
    }

    private void ReloadProfilesFromDisk()
    {
        try
        {
            _profileManager.Reload();
            UnregisterAllHotkeys();
            RegisterAllHotkeys();
            RebuildContextMenu();
        }
        catch
        {
            // Transient read error (file mid-write); the next event will retry.
        }
    }

    private void RegisterAllHotkeys()
    {
        if (_hotkeyWindow == null) return;

        foreach (var profile in _profileManager.Profiles)
        {
            if (profile.Hotkey == null) continue;
            var vk = profile.Hotkey.GetVirtualKeyCode();
            if (vk == 0) continue;

            int id = _nextHotkeyId++;
            if (HotkeyApi.RegisterHotKey(_hotkeyWindow.Handle, id,
                profile.Hotkey.GetModifiers(), vk))
            {
                _hotkeyMap[id] = profile;
            }
        }
    }

    private void UnregisterAllHotkeys()
    {
        if (_hotkeyWindow == null) return;

        foreach (var id in _hotkeyMap.Keys)
        {
            HotkeyApi.UnregisterHotKey(_hotkeyWindow.Handle, id);
        }
        _hotkeyMap.Clear();
        _nextHotkeyId = 1;
    }

    private void OnHotkeyPressed(int hotkeyId)
    {
        if (_hotkeyMap.TryGetValue(hotkeyId, out var profile))
        {
            ApplyProfile(profile);
        }
    }

    private async Task CheckForUpdateAsync(bool silent)
    {
        try
        {
            var release = await UpdateService.CheckForUpdateAsync();
            if (release != null)
            {
                _pendingUpdate = release;
                RebuildContextMenu();
                ShowBalloon($"Update available: v{release.Version} — right-click tray icon to install");
            }
            else if (!silent)
            {
                ShowBalloon("You're running the latest version.");
            }
        }
        catch
        {
            if (!silent)
                ShowBalloon("Unable to check for updates.", ToolTipIcon.Warning);
        }
    }

    private async void DownloadAndInstallUpdate(ReleaseInfo release)
    {
        ShowBalloon($"Downloading v{release.Version}...");

        var path = await UpdateService.DownloadInstallerAsync(release);
        if (path != null)
        {
            UnregisterAllHotkeys();
            _trayIcon.Visible = false;
            UpdateService.LaunchInstallerAndExit(path);
        }
        else
        {
            ShowBalloon("Download failed. Try again later.", ToolTipIcon.Error);
        }
    }

    private void ShowBalloon(string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _trayIcon.BalloonTipTitle = "Monitor Profile Switcher";
        _trayIcon.BalloonTipText = text;
        _trayIcon.BalloonTipIcon = icon;
        _trayIcon.ShowBalloonTip(3000);
    }

    private static Icon LoadEmbeddedIcon()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var stream = assembly.GetManifestResourceStream("MonitorProfileSwitcher.icon.png");
        if (stream != null)
        {
            using var bmp = new Bitmap(stream);
            var resized = new Bitmap(bmp, 32, 32);
            return Icon.FromHandle(resized.GetHicon());
        }
        return SystemIcons.Application;
    }
}

internal class HiddenHotkeyWindow : NativeWindow
{
    private readonly Action<int> _onHotkey;
    private readonly Action _onTaskbarCreated;
    private readonly uint _taskbarCreatedMsg;

    public HiddenHotkeyWindow(Action<int> onHotkey, Action onTaskbarCreated)
    {
        _onHotkey = onHotkey;
        _onTaskbarCreated = onTaskbarCreated;

        // Resolve before the handle exists so WndProc can always compare against it.
        _taskbarCreatedMsg = ShellApi.RegisterWindowMessage("TaskbarCreated");

        // Deliberately a normal (if invisible) top-level window rather than a
        // message-only one: "TaskbarCreated" is sent to HWND_BROADCAST, which does not
        // reach message-only windows.
        CreateHandle(new CreateParams
        {
            Caption = "MonitorProfileSwitcher_HotkeyWindow",
            Style = 0,
        });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == HotkeyApi.WM_HOTKEY)
        {
            _onHotkey(m.WParam.ToInt32());
        }
        else if (_taskbarCreatedMsg != 0 && m.Msg == (int)_taskbarCreatedMsg)
        {
            // Explorer built a new taskbar — every tray icon it was hosting is gone.
            _onTaskbarCreated();
        }
        base.WndProc(ref m);
    }
}
