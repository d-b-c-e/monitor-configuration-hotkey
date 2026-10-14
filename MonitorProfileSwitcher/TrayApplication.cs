using System.Reflection;
using System.Runtime.InteropServices;
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
    private System.Windows.Forms.Timer? _hotkeyRetry;
    private Control? _ipcDispatchControl;
    private IpcService? _ipcService;
    private int _applyInProgress;
    private ArrangeDisplaysForm? _arrangeForm;
    private MonitorLibrary? _library;

    /// <summary>Profiles whose hotkey would not register, retried until they do or we give up.</summary>
    private readonly List<DisplayProfile> _failedHotkeys = new();

    /// <summary>Win32 error from the most recent failed registration, for the message we show.</summary>
    private int _lastHotkeyError;

    public TrayApplication()
    {
        _profileManager = new ProfileManager();
        _profileManager.Load();
        AppLog.Info("Initializing tray application.");

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
        _trayIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenArrangeDisplays();
        };

        RebuildContextMenu();

        _hotkeyWindow = new HiddenHotkeyWindow(OnHotkeyPressed, ReassertTrayIcon);
        RegisterAllHotkeys();

        SetupProfileWatcher();
        GuardAgainstMissingTrayIcon();
        StartIpcService();
        Application.ApplicationExit += (_, _) => _ipcService?.Dispose();

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
                var item = menu.Items.Add(
                    $"{profile.Name}{hotkeyText}  [{profile.DisplaySummary}]");
                item.Click += async (_, _) => await ApplyProfileAsync(profile, "tray menu");
            }
            menu.Items.Add(new ToolStripSeparator());
        }

        var arrangeItem = menu.Items.Add("Arrange Displays...");
        arrangeItem.Font = new Font(arrangeItem.Font, FontStyle.Bold);
        arrangeItem.Click += (_, _) => OpenArrangeDisplays();

        // Capture
        var captureItem = menu.Items.Add("Capture Current Setup As...");
        captureItem.Click += (_, _) => CaptureCurrentSetup();

        // Manage
        if (_profileManager.Profiles.Count > 0)
        {
            var manageItem = menu.Items.Add("Manage Profiles...");
            manageItem.Click += (_, _) => ManageProfiles();

            var rememberWindows = new ToolStripMenuItem("Remember Window Layouts")
            {
                Checked = WindowLayoutManager.IsEnabled(),
                CheckOnClick = false,
            };
            rememberWindows.Click += (_, _) =>
            {
                try
                {
                    var enabled = !WindowLayoutManager.IsEnabled();
                    WindowLayoutManager.SetEnabled(enabled);
                    rememberWindows.Checked = enabled;
                    ShowBalloon(enabled
                        ? "Window layouts will be saved when leaving each profile and restored when returning."
                        : "Automatic window-layout restore is off.");
                }
                catch (Exception ex)
                {
                    AppLog.Error("Could not change window-layout setting.", ex);
                    ShowBalloon($"Could not change window-layout setting: {ex.Message}", ToolTipIcon.Error);
                }
            };
            menu.Items.Add(rememberWindows);

            var saveWindowLayout = menu.Items.Add("Save Current Window Layout");
            saveWindowLayout.Click += (_, _) =>
            {
                try
                {
                    if (Volatile.Read(ref _applyInProgress) != 0)
                    {
                        ShowBalloon("Wait for the profile switch to finish before saving a window layout.", ToolTipIcon.Warning);
                        return;
                    }
                    _profileManager.Reload();
                    ShowBalloon(WindowLayoutManager.SaveCurrentLayout(_profileManager.Profiles)
                        ? "Window layout saved for the active profile."
                        : "No eligible windows or unique active profile; layout was not saved.",
                        ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Could not save current window layout.", ex);
                    ShowBalloon($"Could not save window layout: {ex.Message}", ToolTipIcon.Error);
                }
            };

            var restoreWindowLayout = menu.Items.Add("Restore Saved Window Layout");
            restoreWindowLayout.Click += (_, _) =>
            {
                try
                {
                    if (Volatile.Read(ref _applyInProgress) != 0)
                    {
                        ShowBalloon("Wait for the profile switch to finish before restoring a window layout.", ToolTipIcon.Warning);
                        return;
                    }
                    _profileManager.Reload();
                    var result = WindowLayoutManager.RestoreCurrentLayout(_profileManager.Profiles);
                    ShowBalloon(result == null
                        ? "No saved layout for the active profile."
                        : $"Restored {result.Restored} window(s) for '{result.ProfileName}'.",
                        result == null ? ToolTipIcon.Warning : ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Could not restore the saved window layout.", ex);
                    ShowBalloon($"Could not restore window layout: {ex.Message}", ToolTipIcon.Error);
                }
            };

            var actionsMenu = new ToolStripMenuItem("Profile Actions");
            foreach (var profile in _profileManager.Profiles)
            {
                var profileMenu = new ToolStripMenuItem(profile.Name);

                var recapture = new ToolStripMenuItem("Recapture (update to current)");
                recapture.Click += (_, _) =>
                {
                    _profileManager.CaptureProfile(profile.Name, profile.Hotkey);
                    AppLog.Info($"Recaptured profile '{profile.Name}'.");
                    ShowBalloon($"Profile '{profile.Name}' updated");
                };
                profileMenu.DropDownItems.Add(recapture);

                var delete = new ToolStripMenuItem("Delete");
                delete.Click += (_, _) =>
                {
                    _profileManager.DeleteProfile(profile.Name);
                    AppLog.Info($"Deleted profile '{profile.Name}'.");
                    UnregisterAllHotkeys();
                    RegisterAllHotkeys();
                    RebuildContextMenu();
                    ShowBalloon($"Profile '{profile.Name}' deleted");
                };
                profileMenu.DropDownItems.Add(delete);

                actionsMenu.DropDownItems.Add(profileMenu);
            }
            menu.Items.Add(actionsMenu);
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

        var openLogs = menu.Items.Add("Open Logs");
        openLogs.Click += (_, _) =>
        {
            try { AppLog.OpenLogFolder(); }
            catch (Exception ex) { ShowBalloon($"Could not open logs: {ex.Message}", ToolTipIcon.Error); }
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
            AppLog.Info("Tray application exiting by user request.");
            _ipcService?.Dispose();
            _ipcDispatchControl?.Dispose();
            UnregisterAllHotkeys();
            _profileWatcher?.Dispose();
            StopTaskbarPoll();
            _trayIcon.Visible = false;
            Application.Exit();
        };

        _trayIcon.ContextMenuStrip = menu;
    }

    private void OpenArrangeDisplays()
    {
        if (_arrangeForm is { IsDisposed: false })
        {
            if (_arrangeForm.WindowState == FormWindowState.Minimized)
                _arrangeForm.WindowState = FormWindowState.Normal;
            _arrangeForm.Activate();
            return;
        }

        _profileManager.Reload();
        _arrangeForm = new ArrangeDisplaysForm(
            _profileManager,
            Library,
            profile => ApplyProfileAsync(profile, "Arrange Displays"),
            () =>
            {
                UnregisterAllHotkeys();
                RegisterAllHotkeys();
                RebuildContextMenu();
            });
        _arrangeForm.FormClosed += (_, _) => _arrangeForm = null;
        _arrangeForm.Show();
        _arrangeForm.Activate();
    }

    /// <summary>One monitor library for the tray and the Arrange Displays window, so neither
    /// overwrites the other's saves.</summary>
    private MonitorLibrary Library => _library ??= MonitorLibrary.Load(_profileManager.Profiles);

    /// <summary>A profile is a layout the user chose, so after switching to one its monitors'
    /// positions become their usual places (what Arrange Displays restores and draws unplugged
    /// monitors at). Never fails the switch.</summary>
    private async Task RecordUsualPlacesAsync()
    {
        try
        {
            var inventory = await Task.Run(DisplayInventory.Query);
            Library.Observe(inventory);
            Library.RecordHome(inventory.ActivePositions(), []);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not record the usual monitor places after a profile switch: {ex.Message}");
        }
    }

    private void CaptureCurrentSetup()
    {
        using var dialog = new CaptureDialog(_profileManager.Profiles);
        if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.ProfileName))
        {
            var hotkey = dialog.SelectedHotkey;
            var profile = _profileManager.CaptureProfile(dialog.ProfileName, hotkey);
            AppLog.Info($"Captured profile '{profile.Name}' ({profile.DisplaySummary}) with hotkey {hotkey?.ToString() ?? "none"}.");

            UnregisterAllHotkeys();
            RegisterAllHotkeys();
            RebuildContextMenu();

            var hotkeyText = hotkey != null ? $" ({hotkey})" : "";
            ShowBalloon($"Captured '{profile.Name}'{hotkeyText} — {profile.DisplaySummary}");
        }
    }

    private void ManageProfiles()
    {
        using var dialog = new ManageProfilesDialog(_profileManager.Profiles);
        if (dialog.ShowDialog() != DialogResult.OK)
            return;

        try
        {
            _profileManager.ApplyMetadataChanges(dialog.Edits, dialog.DeletedProfileNames);
            UnregisterAllHotkeys();
            RegisterAllHotkeys();
            RebuildContextMenu();
            var deleted = dialog.DeletedProfileNames.Count;
            ShowBalloon(deleted == 0
                ? "Profile changes saved"
                : $"Profile changes saved ({deleted} profile{(deleted == 1 ? "" : "s")} deleted)");
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to save changes from Manage Profiles.", ex);
            MessageBox.Show($"Could not save profile changes: {ex.Message}", "Manage Profiles",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task<IpcResponse> ApplyProfileAsync(DisplayProfile profile, string trigger)
    {
        if (Interlocked.CompareExchange(ref _applyInProgress, 1, 0) != 0)
        {
            AppLog.Warn($"Rejected overlapping apply request for '{profile.Name}' from {trigger}.");
            if (trigger != "IPC")
                ShowBalloon("A profile switch is already in progress.", ToolTipIcon.Warning);
            return IpcResponse.Error("busy", "A display profile switch is already in progress. Wait for it to finish before trying another.");
        }

        var operationId = Guid.NewGuid().ToString("N")[..8];
        try
        {
            // Re-read profiles.json from disk first: this instance loaded profiles at
            // startup, and the file may have changed since (manual edit, restore from
            // backup, sync). Re-fetch by name so we apply the current saved monitors.
            _profileManager.Reload();
            var fresh = _profileManager.GetProfile(profile.Name) ??
                throw new InvalidOperationException($"Profile '{profile.Name}' no longer exists.");
            bool legacySurround = fresh.IsNvidiaSurround && fresh.NvidiaSurround == null;
            // Native display calls can block for minutes while the driver rebuilds its
            // topology. Keep the tray message loop alive and reject overlapping applies.
            var result = await Task.Run(() => ProfileApplyRunner.Apply(fresh, _profileManager.Profiles, operationId, trigger));
            if (legacySurround && fresh.NvidiaSurround != null)
                _profileManager.SaveMigratedSurroundGrid(fresh);
            var inspector = await Task.Run(IpcDisplayInspector.Capture);
            if (!inspector.Matches(result.Applied))
                throw new InvalidOperationException("The driver finished the switch, but the live display layout does not match the saved profile. See Open Logs for details.");
            var status = inspector.ToStatus(_profileManager.Profiles);

            string[]? skipped = null;
            var skippedText = "";
            if (result.SkippedMonitors.Count > 0)
            {
                skipped = result.SkippedMonitors.Select(Library.NameFor).ToArray();
                skippedText = $" without {string.Join(", ", skipped)} (not connected)";
            }
            await RecordUsualPlacesAsync();
            ShowBalloon(result.Outcome == ProfileApplyOutcome.AlreadyActive
                ? $"'{fresh.Name}' is already active{skippedText}"
                : $"Switched to '{fresh.Name}'{skippedText}",
                skipped == null ? ToolTipIcon.Info : ToolTipIcon.Warning);
            return new IpcResponse
            {
                Success = true,
                Message = result.Outcome switch
                {
                    ProfileApplyOutcome.AlreadyActive => $"Profile '{fresh.Name}' was already active and verified{skippedText}.",
                    ProfileApplyOutcome.VerifiedAfterApiError => $"Applied and verified profile '{fresh.Name}'{skippedText} despite a display API error; see logs.",
                    _ => $"Applied and verified profile '{fresh.Name}'{skippedText}.",
                },
                OperationId = operationId,
                ProfileName = fresh.Name,
                SkippedMonitors = skipped,
                Status = status,
            };
        }
        catch (ProfileApplyBusyException ex)
        {
            AppLog.Warn($"[{operationId}] Apply rejected because another process is switching profiles.");
            if (trigger != "IPC")
                ShowBalloon(ex.Message, ToolTipIcon.Warning);
            return new IpcResponse
            {
                Success = false,
                ErrorCode = "busy",
                Message = ex.Message,
                OperationId = operationId,
                ProfileName = profile.Name,
            };
        }
        catch (Exception ex)
        {
            AppLog.Error($"[{operationId}] Tray apply failed for '{profile.Name}'.", ex);
            ShowBalloon($"Failed: {ex.Message}", ToolTipIcon.Error);
            return new IpcResponse
            {
                Success = false,
                ErrorCode = "apply_failed",
                Message = ex.Message,
                OperationId = operationId,
                ProfileName = profile.Name,
            };
        }
        finally
        {
            Interlocked.Exchange(ref _applyInProgress, 0);
        }
    }

    private void StartIpcService()
    {
        try
        {
            _ipcDispatchControl = new Control();
            _ = _ipcDispatchControl.Handle;
            _ipcService = new IpcService(DispatchIpcAsync);
            _ipcService.Start();
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to start IPC server.", ex);
        }
    }

    private Task<IpcResponse> DispatchIpcAsync(IpcRequest request)
    {
        var completion = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _ipcDispatchControl!.BeginInvoke(new Action(async () =>
            {
                try { completion.SetResult(await HandleIpcRequestAsync(request)); }
                catch (Exception ex)
                {
                    AppLog.Error($"IPC command '{request.Command}' failed.", ex);
                    completion.SetResult(IpcResponse.Error("command_failed", ex.Message));
                }
            }));
        }
        catch (Exception ex)
        {
            completion.SetResult(IpcResponse.Error("unavailable", $"The tray application is closing: {ex.Message}"));
        }
        return completion.Task;
    }

    private async Task<IpcResponse> HandleIpcRequestAsync(IpcRequest request)
    {
        switch (request.Command?.Trim().ToLowerInvariant())
        {
            case "ping":
                return new IpcResponse { Success = true, Message = "Monitor Profile Switcher is running." };

            case "list-profiles":
                _profileManager.Reload();
                return new IpcResponse
                {
                    Success = true,
                    Profiles = _profileManager.Profiles.Select(profile => new IpcProfile
                    {
                        Name = profile.Name,
                        Topology = profile.IsNvidiaSurround ? "nvidia-surround" : profile.Monitors.Length > 1 ? "independent" : "single",
                        Summary = profile.DisplaySummary,
                        Hotkey = profile.Hotkey?.ToString(),
                    }).ToArray(),
                };

            case "status":
                return new IpcResponse
                {
                    Success = true,
                    Status = (await Task.Run(IpcDisplayInspector.Capture)).ToStatus(_profileManager.Profiles),
                };

            case "apply":
                if (string.IsNullOrWhiteSpace(request.ProfileName) || request.ProfileName.Length > 256)
                    return IpcResponse.Error("invalid_profile_name", "Specify a saved profile name of 1 to 256 characters.");
                _profileManager.Reload();
                var profile = _profileManager.GetProfile(request.ProfileName);
                if (profile is null)
                    return IpcResponse.Error("profile_not_found", $"Profile '{request.ProfileName}' was not found.");
                return await ApplyProfileAsync(profile, "IPC");

            default:
                return IpcResponse.Error("unknown_command", $"Unknown IPC command '{request.Command}'.");
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
        catch (Exception ex)
        {
            AppLog.Error("Failed to re-register the tray icon.", ex);
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
        catch (Exception ex)
        {
            AppLog.Error("Failed to initialize profiles.json watcher.", ex);
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
            AppLog.Info("Reloaded profiles after an external profiles.json change.");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not reload profiles after a file change: {ex.Message}");
            // Transient read error (file mid-write); the next event will retry.
        }
    }

    private void RegisterAllHotkeys()
    {
        if (_hotkeyWindow == null) return;

        _failedHotkeys.Clear();

        foreach (var profile in _profileManager.Profiles)
        {
            if (profile.Hotkey == null) continue;
            if (profile.Hotkey.GetVirtualKeyCode() == 0) continue;

            if (!TryRegisterHotkey(profile))
                _failedHotkeys.Add(profile);
        }

        if (_failedHotkeys.Count > 0)
            GuardAgainstFailedHotkeys();
    }

    /// <summary>Registers one profile's hotkey, recording the failure reason if it does not take.</summary>
    private bool TryRegisterHotkey(DisplayProfile profile)
    {
        int id = _nextHotkeyId++;
        if (HotkeyApi.RegisterHotKey(_hotkeyWindow!.Handle, id,
            profile.Hotkey!.GetModifiers(), profile.Hotkey.GetVirtualKeyCode()))
        {
            _hotkeyMap[id] = profile;
            AppLog.Info($"Registered hotkey {profile.Hotkey} for profile '{profile.Name}'.");
            return true;
        }

        _lastHotkeyError = Marshal.GetLastWin32Error();
        AppLog.Warn($"Could not register hotkey {profile.Hotkey} for profile '{profile.Name}'. Windows error {_lastHotkeyError}.");
        return false;
    }

    /// <summary>
    /// Retry hotkeys that would not register at startup, and say so if they never do.
    ///
    /// The logon scheduled task can start this app fractionally before the shell, and
    /// registration is not reliable that early — observed on 2026-09-09, where the app started
    /// 0.1s ahead of Explorer and came up with no tray icon AND no working hotkeys. The tray
    /// icon already had a guard for that race; hotkeys did not, on the assumption that
    /// RegisterHotKey is independent of the shell. It is not reliably so this early in a session.
    ///
    /// The old behaviour was the real defect: a failed registration was discarded without a
    /// retry, a log or any visible sign, so a tool whose entire purpose is hotkeys would start,
    /// look healthy, sit in the tray and do nothing. Failing loudly matters more than the retry.
    ///
    /// Costs nothing on the normal path: if everything registers, no timer is ever created.
    /// </summary>
    private void GuardAgainstFailedHotkeys()
    {
        if (_hotkeyRetry != null) return;

        var waited = TimeSpan.Zero;
        var timeout = TimeSpan.FromSeconds(60);

        _hotkeyRetry = new System.Windows.Forms.Timer { Interval = 2000 };
        _hotkeyRetry.Tick += (_, _) =>
        {
            waited += TimeSpan.FromMilliseconds(_hotkeyRetry!.Interval);

            for (int i = _failedHotkeys.Count - 1; i >= 0; i--)
            {
                if (TryRegisterHotkey(_failedHotkeys[i]))
                    _failedHotkeys.RemoveAt(i);
            }

            if (_failedHotkeys.Count == 0)
            {
                StopHotkeyRetry();
            }
            else if (waited >= timeout)
            {
                StopHotkeyRetry();
                ReportFailedHotkeys();
            }
        };
        _hotkeyRetry.Start();
    }

    private void StopHotkeyRetry()
    {
        _hotkeyRetry?.Stop();
        _hotkeyRetry?.Dispose();
        _hotkeyRetry = null;
    }

    /// <summary>Tell the user which hotkeys are dead, rather than leaving them to discover it by
    /// pressing one and wondering whether the app crashed.</summary>
    private void ReportFailedHotkeys()
    {
        var names = string.Join(", ", _failedHotkeys.Select(p => $"{p.Hotkey} ({p.Name})"));
        var reason = _lastHotkeyError == HotkeyApi.ERROR_HOTKEY_ALREADY_REGISTERED
            ? "another application already owns it"
            : $"Windows error {_lastHotkeyError}";

        ShowBalloon($"Hotkey not registered: {names} — {reason}. The profile still works from this menu.",
            ToolTipIcon.Warning);
        AppLog.Warn($"Hotkey registration permanently failed: {names} — {reason}.");

        _trayIcon.Text = "Monitor Profile Switcher (hotkeys unavailable)";
    }

    private void UnregisterAllHotkeys()
    {
        if (_hotkeyWindow == null) return;

        // A reload re-registers everything from scratch, so an in-flight retry is now chasing
        // stale profile objects. Drop it and let RegisterAllHotkeys start a fresh one if needed.
        StopHotkeyRetry();
        _failedHotkeys.Clear();
        _trayIcon.Text = "Monitor Profile Switcher";

        foreach (var id in _hotkeyMap.Keys)
        {
            HotkeyApi.UnregisterHotKey(_hotkeyWindow.Handle, id);
        }
        _hotkeyMap.Clear();
        _nextHotkeyId = 1;
    }

    private async void OnHotkeyPressed(int hotkeyId)
    {
        if (_hotkeyMap.TryGetValue(hotkeyId, out var profile))
        {
            await ApplyProfileAsync(profile, $"hotkey {profile.Hotkey}");
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
        catch (Exception ex)
        {
            AppLog.Error("Update check failed.", ex);
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
