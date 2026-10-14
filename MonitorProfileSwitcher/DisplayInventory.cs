using System.Runtime.InteropServices;
using MonitorProfileSwitcher.Native;

namespace MonitorProfileSwitcher;

/// <summary>One whole-Hz mode a monitor offers, as GDI lists it.</summary>
internal sealed class DisplayModeOption
{
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint Hz { get; set; }
}

/// <summary>A monitor Windows can drive right now — switched on or not.</summary>
internal sealed class LiveDisplay
{
    public required string DevicePath { get; init; }
    public string FriendlyName { get; init; } = "";
    public string AdapterName { get; init; } = "";
    public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY Connector { get; init; }
    public string EdidId => DisplayLayout.EdidIdFromDevicePath(DevicePath);

    /// <summary>The live path when the monitor is part of the desktop, otherwise null.</summary>
    public MonitorInfo? Active { get; init; }
    public MonitorResolution? PreferredResolution { get; init; }
    public MonitorRefreshRate? PreferredRefreshRate { get; init; }

    /// <summary>Supported modes, unrotated like CCD resolutions. Empty when the monitor is off:
    /// GDI only lists modes for a monitor that has an active source.</summary>
    public List<DisplayModeOption> Modes { get; init; } = [];

    public bool IsSurround => FriendlyName.Equals("NV Surround", StringComparison.OrdinalIgnoreCase);
}

internal sealed class DisplayInventory
{
    public required IReadOnlyList<LiveDisplay> Displays { get; init; }
    public NvidiaSurroundConfig? Surround { get; init; }

    public IReadOnlyList<(string DevicePath, int X, int Y)> ActivePositions() =>
        Displays.Where(display => display.Active != null)
            .Select(display => (display.DevicePath, display.Active!.Position.X, display.Active.Position.Y))
            .ToList();

    public static DisplayInventory Query()
    {
        var active = DisplayManager.CaptureCurrentConfig().Monitors
            .Where(monitor => !string.IsNullOrEmpty(monitor.DevicePath))
            .GroupBy(monitor => monitor.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var adapterNames = AdapterNamesByGdiDevice();

        var (paths, _) = DisplayManager.QueryConfig(
            QueryDisplayConfigFlags.QDC_ALL_PATHS | QueryDisplayConfigFlags.QDC_VIRTUAL_MODE_AWARE);

        var displays = new List<LiveDisplay>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (path.targetInfo.targetAvailable == 0)
                continue;
            var name = DisplayManager.GetTargetDeviceName(path.targetInfo.adapterId, path.targetInfo.id);
            var devicePath = name.monitorDevicePath ?? "";
            if (devicePath.Length == 0 || !seen.Add(devicePath))
                continue;

            // Every path of a target runs through some source on the same adapter, and GDI
            // names those sources, so any of them identifies the adapter.
            var source = DisplayManager.GetSourceDeviceName(path.sourceInfo.adapterId, path.sourceInfo.id);
            adapterNames.TryGetValue(source.viewGdiDeviceName ?? "", out var adapterName);

            active.TryGetValue(devicePath, out var live);
            var preferred = GetPreferredMode(path.targetInfo.adapterId, path.targetInfo.id);
            displays.Add(new LiveDisplay
            {
                DevicePath = devicePath,
                FriendlyName = name.monitorFriendlyDeviceName ?? "",
                AdapterName = adapterName ?? "",
                Connector = name.outputTechnology,
                Active = live,
                PreferredResolution = preferred?.Resolution,
                PreferredRefreshRate = preferred?.RefreshRate,
                Modes = live == null ? [] : ListModes(live),
            });
        }

        return new DisplayInventory { Displays = displays, Surround = NvidiaSurroundManager.TryCaptureActive() };
    }

    private static (MonitorResolution Resolution, MonitorRefreshRate RefreshRate)? GetPreferredMode(LUID adapterId, uint targetId)
    {
        var mode = new DISPLAYCONFIG_TARGET_PREFERRED_MODE();
        mode.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE;
        mode.header.size = Marshal.SizeOf<DISPLAYCONFIG_TARGET_PREFERRED_MODE>();
        mode.header.adapterId = adapterId;
        mode.header.id = targetId;
        if (DisplayConfigApi.DisplayConfigGetDeviceInfo(ref mode) != DisplayConfigApi.ERROR_SUCCESS || mode.width == 0)
            return null;
        return (new MonitorResolution { Width = mode.width, Height = mode.height },
            MonitorRefreshRate.From(mode.targetMode.targetVideoSignalInfo.vSyncFreq));
    }

    /// <summary>\\.\DISPLAYn → adapter description ("NVIDIA GeForce RTX 5080").</summary>
    private static Dictionary<string, string> AdapterNamesByGdiDevice()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; DisplayConfigApi.EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            names[device.DeviceName] = device.DeviceString;
            device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        }
        return names;
    }

    private static List<DisplayModeOption> ListModes(MonitorInfo live)
    {
        if (string.IsNullOrEmpty(live.GdiDeviceName))
            return [];

        var modes = new List<DisplayModeOption>();
        var seen = new HashSet<(uint, uint, uint)>();
        var mode = NewDevMode();
        for (int i = 0; DisplayConfigApi.EnumDisplaySettingsEx(live.GdiDeviceName, i, ref mode, 0); i++)
        {
            if (mode.dmBitsPerPel is 32 or 0 && mode.dmDisplayFrequency > 1 &&
                seen.Add((mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency)))
            {
                modes.Add(new DisplayModeOption { Width = mode.dmPelsWidth, Height = mode.dmPelsHeight, Hz = mode.dmDisplayFrequency });
            }
            mode = NewDevMode();
        }

        // GDI reports a rotated display's modes in its rotated orientation, CCD does not. If the
        // current GDI mode is the CCD resolution turned sideways, turn the whole list back.
        var current = NewDevMode();
        if (DisplayConfigApi.EnumDisplaySettingsEx(live.GdiDeviceName, DisplayConfigApi.ENUM_CURRENT_SETTINGS, ref current, 0) &&
            live.Resolution.Width != live.Resolution.Height &&
            current.dmPelsWidth == live.Resolution.Height && current.dmPelsHeight == live.Resolution.Width)
        {
            foreach (var option in modes)
                (option.Width, option.Height) = (option.Height, option.Width);
        }

        return modes
            .OrderByDescending(option => (ulong)option.Width * option.Height)
            .ThenByDescending(option => option.Width)
            .ThenByDescending(option => option.Hz)
            .ToList();
    }

    private static DEVMODE NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
}
