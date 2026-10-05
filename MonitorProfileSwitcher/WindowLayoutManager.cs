using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MonitorProfileSwitcher.Native;

namespace MonitorProfileSwitcher;

internal sealed record WindowRestoreResult(string ProfileName, int Restored, int Missing, int Offscreen, int Failed);

/// <summary>
/// Optional, conservative restoration of windows that remain open across a display switch.
/// HWND + process start time intentionally avoids guessing which newly launched window is
/// equivalent to a closed one. Window positions never affect whether a profile apply succeeds.
/// </summary>
internal static class WindowLayoutManager
{
    private static readonly string StorePath = Path.Combine(ProfileManager.StorageDir, "window-layouts.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static bool IsEnabled()
    {
        try { return Load().Enabled; }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not read window-layout setting: {ex.Message}");
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        var store = Load();
        store.Enabled = enabled;
        Save(store);
        AppLog.Info($"Automatic window-layout restore {(enabled ? "enabled" : "disabled")}.");
    }

    public static bool SaveCurrentLayout(IReadOnlyList<DisplayProfile> profiles)
    {
        var store = Load();
        var current = IpcDisplayInspector.Capture();
        var source = FindActiveProfile(current, profiles, store.LastActiveProfileId);
        if (source == null)
            return false;
        return CaptureAndSave(store, source, "manual");
    }

    public static WindowRestoreResult? RestoreCurrentLayout(IReadOnlyList<DisplayProfile> profiles)
    {
        var store = Load();
        var current = IpcDisplayInspector.Capture();
        var profile = FindActiveProfile(current, profiles, store.LastActiveProfileId);
        if (profile == null || !store.Snapshots.TryGetValue(profile.Id, out var snapshot))
            return null;
        return RestoreSnapshot(snapshot, profile, "manual");
    }

    public static void BeforeSwitch(IpcDisplayInspector current, DisplayProfile target,
        IReadOnlyList<DisplayProfile> profiles, string operationId)
    {
        try
        {
            var store = Load();
            if (!store.Enabled)
                return;

            var source = FindActiveProfile(current, profiles, store.LastActiveProfileId);
            if (source == null)
            {
                AppLog.Warn($"[{operationId}] Window layout not captured: active profile could not be identified uniquely.");
                return;
            }
            if (source.Id == target.Id)
                return;
            CaptureAndSave(store, source, operationId);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[{operationId}] Window layout capture failed; display switch continues: {ex.Message}");
        }
    }

    public static void AfterSwitch(DisplayProfile target, string operationId)
    {
        try
        {
            var store = Load();
            if (!store.Enabled)
                return;
            store.LastActiveProfileId = target.Id;
            try { Save(store); }
            catch (Exception ex) { AppLog.Warn($"[{operationId}] Could not persist active window-layout profile: {ex.Message}"); }
            if (!store.Snapshots.TryGetValue(target.Id, out var snapshot))
            {
                AppLog.Info($"[{operationId}] No prior window layout for '{target.Name}'; one will be captured when leaving it.");
                return;
            }

            // The driver and Explorer can still send display-change notifications after
            // CCD/NVAPI has verified the topology. Give them a brief chance to finish.
            Thread.Sleep(1000);
            if (!IpcDisplayInspector.Capture().Matches(target))
            {
                AppLog.Warn($"[{operationId}] Window restore skipped: display topology changed again after verification.");
                return;
            }
            RestoreSnapshot(snapshot, target, operationId);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[{operationId}] Window layout restore failed; display switch remains applied: {ex.Message}");
        }
    }

    private static WindowRestoreResult RestoreSnapshot(WindowLayoutSnapshot snapshot,
        DisplayProfile profile, string operationId)
    {
        var screens = Screen.AllScreens.Select(screen => screen.Bounds).ToArray();
        int restored = 0, missing = 0, offscreen = 0, failed = 0;
        var foreground = WindowPlacementApi.GetForegroundWindow();
        // EnumWindows enumerates front to back. Restoring back to front reduces
        // unwanted changes to the user's window stacking order.
        foreach (var window in snapshot.Windows.AsEnumerable().Reverse())
        {
            var handle = new IntPtr(window.Handle);
            if (!IsSameLiveWindow(handle, window))
            {
                missing++;
                continue;
            }

            var saved = window.Placement;
            var bounds = Rectangle.FromLTRB(saved.Left, saved.Top, saved.Right, saved.Bottom);
            if (!screens.Any(screen => Rectangle.Intersect(bounds, screen).Width >= 64 &&
                                       Rectangle.Intersect(bounds, screen).Height >= 32))
            {
                offscreen++;
                continue;
            }

            var placement = saved.ToNative();
            placement.Flags = WindowPlacementApi.WPF_ASYNCWINDOWPLACEMENT;
            placement.ShowCmd = saved.ShowCmd switch
            {
                WindowPlacementApi.SW_SHOWNORMAL => WindowPlacementApi.SW_SHOWNOACTIVATE,
                WindowPlacementApi.SW_SHOWMINIMIZED => WindowPlacementApi.SW_SHOWMINNOACTIVE,
                _ => saved.ShowCmd,
            };
            if (WindowPlacementApi.SetWindowPlacement(handle, ref placement))
                restored++;
            else
            {
                failed++;
                AppLog.Warn($"[{operationId}] Could not restore {window.ClassName} PID={window.ProcessId}: Windows error {Marshal.GetLastWin32Error()}.");
            }
        }
        if (foreground != IntPtr.Zero && WindowPlacementApi.IsWindow(foreground))
            WindowPlacementApi.SetForegroundWindow(foreground);
        var result = new WindowRestoreResult(profile.Name, restored, missing, offscreen, failed);
        AppLog.Info($"[{operationId}] Window layout for '{profile.Name}': restored={restored}, closed/replaced={missing}, offscreen={offscreen}, failed={failed}.");
        return result;
    }

    private static DisplayProfile? FindActiveProfile(IpcDisplayInspector current,
        IReadOnlyList<DisplayProfile> profiles, string? lastActiveProfileId)
    {
        var matches = profiles.Where(current.Matches).ToArray();
        if (matches.Length == 1)
            return matches[0];
        if (matches.Length > 1 && lastActiveProfileId != null)
            return matches.FirstOrDefault(profile => profile.Id == lastActiveProfileId);
        return null;
    }

    private static bool CaptureAndSave(WindowLayoutStore store, DisplayProfile profile, string operationId)
    {
        var windows = CaptureWindows();
        if (windows.Count == 0)
        {
            AppLog.Warn($"[{operationId}] Window layout for '{profile.Name}' was not replaced because no eligible windows were found.");
            return false;
        }
        store.Snapshots[profile.Id] = new WindowLayoutSnapshot
        {
            CapturedAt = DateTime.UtcNow,
            Windows = windows,
        };
        store.LastActiveProfileId = profile.Id;
        Save(store);
        AppLog.Info($"[{operationId}] Captured {windows.Count} window(s) for '{profile.Name}'.");
        return true;
    }

    private static List<SavedWindow> CaptureWindows()
    {
        var windows = new List<SavedWindow>();
        WindowPlacementApi.EnumWindows((handle, _) =>
        {
            try
            {
                if (!WindowPlacementApi.IsWindowVisible(handle) ||
                    WindowPlacementApi.GetWindow(handle, WindowPlacementApi.GW_OWNER) != IntPtr.Zero ||
                    (WindowPlacementApi.GetWindowLong(handle, WindowPlacementApi.GWL_EXSTYLE) & WindowPlacementApi.WS_EX_TOOLWINDOW) != 0 ||
                    WindowPlacementApi.GetWindowTextLength(handle) == 0)
                    return true;

                if (WindowPlacementApi.DwmGetWindowAttribute(handle, WindowPlacementApi.DWMWA_CLOAKED,
                        out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                    return true;

                WindowPlacementApi.GetWindowThreadProcessId(handle, out uint processId);
                if (processId == 0 || processId == Environment.ProcessId)
                    return true;

                var placement = WindowPlacementApi.WindowPlacement.Empty;
                if (!WindowPlacementApi.GetWindowPlacement(handle, ref placement) ||
                    placement.NormalPosition.Right <= placement.NormalPosition.Left ||
                    placement.NormalPosition.Bottom <= placement.NormalPosition.Top)
                    return true;

                // Do not reposition a borderless full-screen game. Maximized desktop
                // windows remain eligible because their restored rectangle is useful.
                if (placement.ShowCmd == WindowPlacementApi.SW_SHOWNORMAL &&
                    WindowPlacementApi.GetWindowRect(handle, out var currentRect))
                {
                    var currentBounds = Rectangle.FromLTRB(currentRect.Left, currentRect.Top,
                        currentRect.Right, currentRect.Bottom);
                    if (Screen.AllScreens.Any(screen =>
                        Math.Abs(currentBounds.Left - screen.Bounds.Left) <= 2 &&
                        Math.Abs(currentBounds.Top - screen.Bounds.Top) <= 2 &&
                        Math.Abs(currentBounds.Right - screen.Bounds.Right) <= 2 &&
                        Math.Abs(currentBounds.Bottom - screen.Bounds.Bottom) <= 2))
                        return true;
                }

                var className = GetClassName(handle);
                if (className.Length == 0)
                    return true;
                using var process = Process.GetProcessById((int)processId);
                windows.Add(new SavedWindow
                {
                    Handle = handle.ToInt64(),
                    ProcessId = (int)processId,
                    ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    ClassName = className,
                    Placement = SavedPlacement.FromNative(placement),
                });
            }
            catch
            {
                // A window may close between enumeration and inspection, or belong to
                // a protected process. Neither should interrupt the display switch.
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static bool IsSameLiveWindow(IntPtr handle, SavedWindow saved)
    {
        if (!WindowPlacementApi.IsWindow(handle) || !WindowPlacementApi.IsWindowVisible(handle) ||
            GetClassName(handle) != saved.ClassName)
            return false;
        WindowPlacementApi.GetWindowThreadProcessId(handle, out uint processId);
        if (processId != saved.ProcessId)
            return false;
        try
        {
            using var process = Process.GetProcessById(saved.ProcessId);
            return process.StartTime.ToUniversalTime().Ticks == saved.ProcessStartUtcTicks;
        }
        catch { return false; }
    }

    private static string GetClassName(IntPtr handle)
    {
        var buffer = new StringBuilder(256);
        return WindowPlacementApi.GetClassName(handle, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    private static WindowLayoutStore Load()
    {
        if (!File.Exists(StorePath))
            return new WindowLayoutStore();
        return JsonSerializer.Deserialize<WindowLayoutStore>(File.ReadAllText(StorePath), JsonOptions)
            ?? new WindowLayoutStore();
    }

    private static void Save(WindowLayoutStore store)
    {
        Directory.CreateDirectory(ProfileManager.StorageDir);
        var temporaryPath = StorePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(store, JsonOptions));
        File.Move(temporaryPath, StorePath, true);
    }

    private sealed class WindowLayoutStore
    {
        public bool Enabled { get; set; }
        public string? LastActiveProfileId { get; set; }
        public Dictionary<string, WindowLayoutSnapshot> Snapshots { get; set; } = new();
    }

    private sealed class WindowLayoutSnapshot
    {
        public DateTime CapturedAt { get; set; }
        public List<SavedWindow> Windows { get; set; } = [];
    }

    private sealed class SavedWindow
    {
        public long Handle { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public string ClassName { get; set; } = "";
        public SavedPlacement Placement { get; set; } = new();
    }

    private sealed class SavedPlacement
    {
        public uint ShowCmd { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public int MinX { get; set; }
        public int MinY { get; set; }
        public int MaxX { get; set; }
        public int MaxY { get; set; }

        public static SavedPlacement FromNative(WindowPlacementApi.WindowPlacement native) => new()
        {
            ShowCmd = native.ShowCmd,
            Left = native.NormalPosition.Left,
            Top = native.NormalPosition.Top,
            Right = native.NormalPosition.Right,
            Bottom = native.NormalPosition.Bottom,
            MinX = native.MinPosition.X,
            MinY = native.MinPosition.Y,
            MaxX = native.MaxPosition.X,
            MaxY = native.MaxPosition.Y,
        };

        public WindowPlacementApi.WindowPlacement ToNative() => new()
        {
            Length = (uint)Marshal.SizeOf<WindowPlacementApi.WindowPlacement>(),
            ShowCmd = ShowCmd,
            MinPosition = new WindowPlacementApi.Point { X = MinX, Y = MinY },
            MaxPosition = new WindowPlacementApi.Point { X = MaxX, Y = MaxY },
            NormalPosition = new WindowPlacementApi.Rect
            {
                Left = Left, Top = Top, Right = Right, Bottom = Bottom,
            },
        };
    }
}
