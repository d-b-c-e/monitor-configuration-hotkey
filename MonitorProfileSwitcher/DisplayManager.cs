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
        // Query ALL paths (including inactive)
        var (allPaths, _) = QueryConfig(
            QueryDisplayConfigFlags.QDC_ALL_PATHS |
            QueryDisplayConfigFlags.QDC_VIRTUAL_MODE_AWARE);

        // Build set of wanted device paths
        var wantedDevicePaths = new HashSet<string>(
            profile.Monitors.Select(m => m.DevicePath),
            StringComparer.OrdinalIgnoreCase);

        // Build device path lookup for all paths
        var pathDevicePaths = new string[allPaths.Length];
        for (int i = 0; i < allPaths.Length; i++)
        {
            var name = GetTargetDeviceName(allPaths[i].targetInfo.adapterId, allPaths[i].targetInfo.id);
            pathDevicePaths[i] = name.monitorDevicePath ?? "";
        }

        // Strategy (following MartinGC94/DisplayConfig pattern):
        // 1. For each wanted monitor, pick one path and mark it ACTIVE with a unique clone group
        // 2. For all other paths, clear ACTIVE and invalidate modes
        // 3. Pass ALL paths to SetDisplayConfig with SDC_TOPOLOGY_SUPPLIED

        var matchedDevicePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedSourceIds = new HashSet<uint>();
        var activatedIndices = new HashSet<int>();
        uint cloneGroup = 0;

        // First pass: match wanted monitors to paths, preferring saved source IDs
        var savedMonitors = profile.Monitors.ToDictionary(m => m.DevicePath, StringComparer.OrdinalIgnoreCase);
        foreach (var pass in new[] { true, false }) // true=match saved sourceId, false=any
        {
            for (int i = 0; i < allPaths.Length; i++)
            {
                var devicePath = pathDevicePaths[i];
                if (string.IsNullOrEmpty(devicePath) || !wantedDevicePaths.Contains(devicePath))
                    continue;
                if (matchedDevicePaths.Contains(devicePath))
                    continue;
                if (usedSourceIds.Contains(allPaths[i].sourceInfo.id))
                    continue;

                if (pass && savedMonitors.TryGetValue(devicePath, out var saved) &&
                    allPaths[i].sourceInfo.id != saved.SourceId)
                    continue;

                activatedIndices.Add(i);
                matchedDevicePaths.Add(devicePath);
                usedSourceIds.Add(allPaths[i].sourceInfo.id);
            }
        }

        if (activatedIndices.Count == 0)
        {
            throw new InvalidOperationException(
                "No matching monitors found for this profile. Are the monitors connected?");
        }

        // Build the final path array — ALL paths, with flags set appropriately
        for (int i = 0; i < allPaths.Length; i++)
        {
            // Invalidate mode indices for all paths (Windows will supply modes)
            // For virtual mode aware: upper 16 = 0xFFFF (invalid), lower 16 = clone group or 0xFFFF
            allPaths[i].targetInfo.modeInfoIdx = 0xFFFFFFFF;

            if (activatedIndices.Contains(i))
            {
                allPaths[i].flags |= DISPLAYCONFIG_PATH_INFO.DISPLAYCONFIG_PATH_ACTIVE;
                // Set source modeInfoIdx: upper 16 = 0xFFFF (invalid mode), lower 16 = clone group
                allPaths[i].sourceInfo.modeInfoIdx = 0xFFFF0000 | cloneGroup;
                cloneGroup++;
            }
            else
            {
                allPaths[i].flags &= ~DISPLAYCONFIG_PATH_INFO.DISPLAYCONFIG_PATH_ACTIVE;
                allPaths[i].sourceInfo.modeInfoIdx = 0xFFFFFFFF;
            }
        }

        // Apply with SDC_TOPOLOGY_SUPPLIED — pass ALL paths, no modes
        int result = DisplayConfigApi.SetDisplayConfig(
            allPaths.Length, allPaths,
            0, null,
            SetDisplayConfigFlags.SDC_APPLY |
            SetDisplayConfigFlags.SDC_TOPOLOGY_SUPPLIED |
            SetDisplayConfigFlags.SDC_ALLOW_PATH_ORDER_CHANGES |
            SetDisplayConfigFlags.SDC_VIRTUAL_MODE_AWARE);

        if (result != DisplayConfigApi.ERROR_SUCCESS)
        {
            throw new InvalidOperationException(
                $"SetDisplayConfig failed with error code {result}. " +
                $"Tried to activate {activatedIndices.Count} monitor(s): " +
                string.Join(", ", profile.Monitors.Select(m => m.FriendlyName)));
        }

        // Phase 2: Apply saved positions, primary, and resolution
        // Now that the correct monitors are active, query the current config
        // and adjust positions/primary to match the saved profile
        ApplyLayout(profile);
    }

    private static void ApplyLayout(DisplayProfile profile)
    {
        var (paths, modes) = QueryConfig(
            QueryDisplayConfigFlags.QDC_ONLY_ACTIVE_PATHS |
            QueryDisplayConfigFlags.QDC_VIRTUAL_MODE_AWARE);

        // Build saved monitor lookup by device path
        var savedByPath = profile.Monitors.ToDictionary(
            m => m.DevicePath, StringComparer.OrdinalIgnoreCase);

        // Match current paths to saved monitors and update source modes
        bool changed = false;
        for (int i = 0; i < paths.Length; i++)
        {
            var name = GetTargetDeviceName(paths[i].targetInfo.adapterId, paths[i].targetInfo.id);
            if (!savedByPath.TryGetValue(name.monitorDevicePath ?? "", out var saved))
                continue;

            // Find the source mode for this path
            uint sourceModeIdx = paths[i].sourceInfo.modeInfoIdx >> 16;
            if (sourceModeIdx >= modes.Length ||
                modes[sourceModeIdx].infoType != DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
            {
                // Fallback: scan for matching source mode
                for (uint mi = 0; mi < modes.Length; mi++)
                {
                    if (modes[mi].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE &&
                        modes[mi].id == paths[i].sourceInfo.id &&
                        modes[mi].adapterId.LowPart == paths[i].sourceInfo.adapterId.LowPart)
                    {
                        sourceModeIdx = mi;
                        break;
                    }
                }
            }

            if (sourceModeIdx < modes.Length &&
                modes[sourceModeIdx].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
            {
                var mode = modes[sourceModeIdx];
                if (mode.sourceMode.position.x != saved.Position.X ||
                    mode.sourceMode.position.y != saved.Position.Y ||
                    mode.sourceMode.width != saved.Resolution.Width ||
                    mode.sourceMode.height != saved.Resolution.Height)
                {
                    mode.sourceMode.position.x = saved.Position.X;
                    mode.sourceMode.position.y = saved.Position.Y;
                    mode.sourceMode.width = saved.Resolution.Width;
                    mode.sourceMode.height = saved.Resolution.Height;
                    modes[sourceModeIdx] = mode;
                    changed = true;
                }
            }
        }

        if (!changed)
            return;

        // Apply the adjusted layout
        int result = DisplayConfigApi.SetDisplayConfig(
            paths.Length, paths,
            modes.Length, modes,
            SetDisplayConfigFlags.SDC_APPLY |
            SetDisplayConfigFlags.SDC_USE_SUPPLIED_DISPLAY_CONFIG |
            SetDisplayConfigFlags.SDC_ALLOW_CHANGES |
            SetDisplayConfigFlags.SDC_SAVE_TO_DATABASE |
            SetDisplayConfigFlags.SDC_VIRTUAL_MODE_AWARE);

        // Non-fatal if layout apply fails — topology is already correct
        if (result != DisplayConfigApi.ERROR_SUCCESS)
        {
            Console.Error.WriteLine($"Warning: Layout adjustment returned {result} — topology is correct but positions may differ");
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
