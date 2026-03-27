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

        _hotkeyWindow = new HiddenHotkeyWindow(OnHotkeyPressed);
        RegisterAllHotkeys();
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

        menu.Items.Add(new ToolStripSeparator());

        // Exit
        var exitItem = menu.Items.Add("Exit");
        exitItem.Click += (_, _) =>
        {
            UnregisterAllHotkeys();
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
            DisplayManager.ApplyProfile(profile);
            ShowBalloon($"Switched to '{profile.Name}'");
        }
        catch (Exception ex)
        {
            ShowBalloon($"Failed: {ex.Message}", ToolTipIcon.Error);
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

    public HiddenHotkeyWindow(Action<int> onHotkey)
    {
        _onHotkey = onHotkey;
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
        base.WndProc(ref m);
    }
}
