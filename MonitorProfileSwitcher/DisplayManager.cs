using System.Runtime.InteropServices;
using MonitorProfileSwitcher.Native;

namespace MonitorProfileSwitcher;

internal static class DisplayManager
{
    public static DisplaySnapshot CaptureCurrentConfig()
    {
        var (paths, modes) = QueryConfig(
            QueryDisplayConfigFlags.QDC_ONLY_ACTIVE_PATHS |
            QueryDisplayConfigFlags.QDC_VIRTUAL_MODE_AWARE);

        var monitors = new List<MonitorInfo>();
        foreach (var path in paths)
        {
            if ((path.flags & DISPLAYCONFIG_PATH_INFO.DISPLAYCONFIG_PATH_ACTIVE) == 0)
                continue;

            var targetName = GetTargetDeviceName(path.targetInfo.adapterId, path.targetInfo.id);
            var sourceName = GetSourceDeviceName(path.sourceInfo.adapterId, path.sourceInfo.id);

            DISPLAYCONFIG_SOURCE_MODE? sourceMode = null;
            // With QDC_VIRTUAL_MODE_AWARE, source modeInfoIdx is packed:
            // upper 16 bits = source mode index, lower 16 bits = clone group (0xFFFF = none)
            uint sourceModeIdx = path.sourceInfo.modeInfoIdx >> 16;

            if (sourceModeIdx < modes.Length &&
                modes[sourceModeIdx].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
            {
                sourceMode = modes[sourceModeIdx].sourceMode;
            }

            // Fallback: scan all modes for matching source id
            if (sourceMode == null)
            {
                for (int mi = 0; mi < modes.Length; mi++)
                {
                    if (modes[mi].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE &&
                        modes[mi].id == path.sourceInfo.id &&
                        modes[mi].adapterId.LowPart == path.sourceInfo.adapterId.LowPart &&
                        modes[mi].adapterId.HighPart == path.sourceInfo.adapterId.HighPart)
                    {
                        sourceMode = modes[mi].sourceMode;
                        break;
                    }
                }
            }

            monitors.Add(new MonitorInfo
            {
                DevicePath = targetName.monitorDevicePath ?? "",
                FriendlyName = targetName.monitorFriendlyDeviceName ?? "",
                GdiDeviceName = sourceName.viewGdiDeviceName ?? "",
                SourceId = path.sourceInfo.id,
                TargetId = path.targetInfo.id,
                OutputTechnology = targetName.outputTechnology,
                Rotation = path.targetInfo.rotation,
                Scaling = path.targetInfo.scaling,
                RefreshRate = path.targetInfo.refreshRate,
                IsPrimary = sourceMode?.position.x == 0 && sourceMode?.position.y == 0,
                Position = sourceMode.HasValue
                    ? new MonitorPosition { X = sourceMode.Value.position.x, Y = sourceMode.Value.position.y }
                    : new MonitorPosition(),
                Resolution = sourceMode.HasValue
                    ? new MonitorResolution { Width = sourceMode.Value.width, Height = sourceMode.Value.height }
                    : new MonitorResolution(),
            });
        }

        return new DisplaySnapshot
        {
            Paths = paths,
            Modes = modes,
            Monitors = monitors.ToArray()
        };
    }

    public static void ApplyProfile(DisplayProfile profile)
    {
        // Query ALL paths (including inactive) to find monitors we need to activate
        var (allPaths, allModes) = QueryConfig(
            QueryDisplayConfigFlags.QDC_ALL_PATHS |
            QueryDisplayConfigFlags.QDC_VIRTUAL_MODE_AWARE);

        // Build a lookup of current monitors by device path
        var currentMonitors = new Dictionary<string, (int pathIndex, DISPLAYCONFIG_TARGET_DEVICE_NAME name)>();
        for (int i = 0; i < allPaths.Length; i++)
        {
            var name = GetTargetDeviceName(allPaths[i].targetInfo.adapterId, allPaths[i].targetInfo.id);
            if (!string.IsNullOrEmpty(name.monitorDevicePath))
            {
                currentMonitors.TryAdd(name.monitorDevicePath, (i, name));
            }
        }

        // Build new path and mode arrays from the saved profile
        var newPaths = new List<DISPLAYCONFIG_PATH_INFO>();
        var newModes = new List<DISPLAYCONFIG_MODE_INFO>();

        foreach (var savedMonitor in profile.Monitors)
        {
            if (!currentMonitors.TryGetValue(savedMonitor.DevicePath, out var current))
                continue; // Monitor not currently connected, skip

            var path = allPaths[current.pathIndex];
            path.flags = DISPLAYCONFIG_PATH_INFO.DISPLAYCONFIG_PATH_ACTIVE;
            path.targetInfo.rotation = savedMonitor.Rotation;
            path.targetInfo.scaling = savedMonitor.Scaling;
            path.targetInfo.refreshRate = savedMonitor.RefreshRate;

            // Source mode
            var sourceMode = new DISPLAYCONFIG_MODE_INFO
            {
                infoType = DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
                id = path.sourceInfo.id,
                adapterId = path.sourceInfo.adapterId,
                sourceMode = new DISPLAYCONFIG_SOURCE_MODE
                {
                    width = savedMonitor.Resolution.Width,
                    height = savedMonitor.Resolution.Height,
                    pixelFormat = DISPLAYCONFIG_PIXELFORMAT.PIXELFORMAT_32BPP,
                    position = new POINTL { x = savedMonitor.Position.X, y = savedMonitor.Position.Y }
                }
            };

            // Target mode — get from current all-paths config if available
            DISPLAYCONFIG_MODE_INFO targetMode;
            if (path.targetInfo.modeInfoIdx < allModes.Length &&
                allModes[path.targetInfo.modeInfoIdx].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
            {
                targetMode = allModes[path.targetInfo.modeInfoIdx];
                targetMode.adapterId = path.targetInfo.adapterId;
                targetMode.id = path.targetInfo.id;
            }
            else
            {
                targetMode = new DISPLAYCONFIG_MODE_INFO
                {
                    infoType = DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET,
                    id = path.targetInfo.id,
                    adapterId = path.targetInfo.adapterId,
                };
            }

            path.sourceInfo.modeInfoIdx = (uint)newModes.Count;
            newModes.Add(sourceMode);

            path.targetInfo.modeInfoIdx = (uint)newModes.Count;
            newModes.Add(targetMode);

            newPaths.Add(path);
        }

        if (newPaths.Count == 0)
        {
            throw new InvalidOperationException("No matching monitors found for this profile. Are the monitors connected?");
        }

        var pathArray = newPaths.ToArray();
        var modeArray = newModes.ToArray();

        // Apply with SDC_ALLOW_CHANGES to let Windows adjust modes if needed
        var flags = SetDisplayConfigFlags.SDC_APPLY |
                    SetDisplayConfigFlags.SDC_USE_SUPPLIED_DISPLAY_CONFIG |
                    SetDisplayConfigFlags.SDC_ALLOW_CHANGES |
                    SetDisplayConfigFlags.SDC_SAVE_TO_DATABASE |
                    SetDisplayConfigFlags.SDC_VIRTUAL_MODE_AWARE;

        int result = DisplayConfigApi.SetDisplayConfig(
            pathArray.Length, pathArray,
            modeArray.Length, modeArray,
            flags);

        if (result != DisplayConfigApi.ERROR_SUCCESS)
        {
            throw new InvalidOperationException(
                $"SetDisplayConfig failed with error code {result}. " +
                $"Tried to activate {newPaths.Count} monitor(s).");
        }
    }

    public static string DescribeCurrentConfig()
    {
        var snapshot = CaptureCurrentConfig();
        var lines = new List<string> { $"Active monitors ({snapshot.Monitors.Length}):" };
        foreach (var m in snapshot.Monitors)
        {
            var primary = m.IsPrimary ? " [PRIMARY]" : "";
            var rotation = m.Rotation != DISPLAYCONFIG_ROTATION.IDENTITY
                ? $" {m.Rotation}" : "";
            lines.Add($"  {m.FriendlyName} ({m.GdiDeviceName}){primary}");
            lines.Add($"    {m.Resolution.Width}x{m.Resolution.Height} @ {m.RefreshRate}{rotation}");
            lines.Add($"    Position: ({m.Position.X}, {m.Position.Y})");
            lines.Add($"    Path: {m.DevicePath}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static (DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes) QueryConfig(
        QueryDisplayConfigFlags flags)
    {
        int retries = 3;
        while (retries-- > 0)
        {
            int result = DisplayConfigApi.GetDisplayConfigBufferSizes(flags, out int pathCount, out int modeCount);
            if (result != DisplayConfigApi.ERROR_SUCCESS)
                throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed: {result}");

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

            result = DisplayConfigApi.QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == DisplayConfigApi.ERROR_SUCCESS)
            {
                Array.Resize(ref paths, pathCount);
                Array.Resize(ref modes, modeCount);
                return (paths, modes);
            }

            if (result != DisplayConfigApi.ERROR_INSUFFICIENT_BUFFER)
                throw new InvalidOperationException($"QueryDisplayConfig failed: {result}");
        }
        throw new InvalidOperationException("QueryDisplayConfig failed after retries");
    }

    private static DISPLAYCONFIG_TARGET_DEVICE_NAME GetTargetDeviceName(LUID adapterId, uint targetId)
    {
        var name = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        name.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        name.header.size = Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        name.header.adapterId = adapterId;
        name.header.id = targetId;
        DisplayConfigApi.DisplayConfigGetDeviceInfo(ref name);
        return name;
    }

    private static DISPLAYCONFIG_SOURCE_DEVICE_NAME GetSourceDeviceName(LUID adapterId, uint sourceId)
    {
        var name = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
        name.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        name.header.size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
        name.header.adapterId = adapterId;
        name.header.id = sourceId;
        DisplayConfigApi.DisplayConfigGetDeviceInfo(ref name);
        return name;
    }
}

internal class DisplaySnapshot
{
    public DISPLAYCONFIG_PATH_INFO[] Paths { get; set; } = [];
    public DISPLAYCONFIG_MODE_INFO[] Modes { get; set; } = [];
    public MonitorInfo[] Monitors { get; set; } = [];
}
