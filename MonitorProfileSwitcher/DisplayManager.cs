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
                RefreshRate = MonitorRefreshRate.From(path.targetInfo.refreshRate),
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

    public static void ApplyProfile(DisplayProfile profile, NvidiaSurroundConfig? recentlyObservedSurround = null)
    {
        // Surround is created by the NVIDIA driver, below Windows CCD. A newly captured
        // profile carries the exact grid; a legacy "NV Surround" profile can still ask the
        // driver to re-enable its last persisted grid.
        if (profile.NvidiaSurround != null)
        {
            // Enabling Surround leaves ONLY the Surround desktop active. Every other display,
            // even one on another adapter (a USB DisplayLink panel), stays connected but is
            // switched off. A profile that saved more than the Surround desktop must therefore
            // restore its Windows layout on top of the grid.
            var hasOtherMonitors = profile.Monitors.Length > 1;
            var gridAlreadyLive = recentlyObservedSurround != null &&
                NvidiaSurroundManager.Matches(recentlyObservedSurround, profile.NvidiaSurround);
            if (gridAlreadyLive)
            {
                // Rebuilding a live grid is a ~15s driver reset that would only switch the
                // other monitors off again; only the Windows layout around it differs.
                AppLog.Info($"NVIDIA Surround grid for '{profile.Name}' is already active; applying its Windows layout only.");
            }
            else
            {
                AppLog.Info($"Applying NVIDIA Surround profile '{profile.Name}' ({profile.NvidiaSurround.Rows}x{profile.NvidiaSurround.Columns}, displays={string.Join(",", profile.NvidiaSurround.Displays.Select(display => display.DisplayId))}).");
                NvidiaSurroundManager.Apply(profile.NvidiaSurround);
            }

            if (hasOtherMonitors || gridAlreadyLive)
            {
                // Never supply a topology without the Surround desktop in it: that would make
                // the extra monitor the only display. Wait for every saved monitor first.
                WaitForMonitorsAvailable(profile, TimeSpan.FromSeconds(10));
                AppLog.Info($"Restoring the Windows layout of '{profile.Name}' around the Surround desktop ({profile.Monitors.Length} monitor(s)).");
                ApplyWindowsTopology(profile);
            }
            return;
        }
        if (profile.IsNvidiaSurround)
        {
            AppLog.Info($"Applying legacy NVIDIA Surround profile '{profile.Name}'.");
            profile.NvidiaSurround = NvidiaSurroundManager.ApplyLegacy(profile);
            return;
        }

        // CCD cannot split a Surround source into physical monitors. Give the outputs back to
        // Windows first, wait for the driver transition, then restore the saved Windows paths.
        var surroundWasDisabled = NvidiaSurroundManager.DisableIfActive(recentlyObservedSurround);
        AppLog.Info($"Applying Windows display profile '{profile.Name}'. NVIDIA Surround transition required={surroundWasDisabled}.");
        ApplyWindowsTopology(profile);
    }

    /// <summary>Wait until every monitor saved in the profile is an available CCD target.
    /// Throws, naming the missing monitors, if they do not all appear within the timeout.</summary>
    private static void WaitForMonitorsAvailable(DisplayProfile profile, TimeSpan timeout)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var (allPaths, _) = QueryConfig(
                QueryDisplayConfigFlags.QDC_ALL_PATHS |
                QueryDisplayConfigFlags.QDC_VIRTUAL_MODE_AWARE);
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in allPaths.Where(path => path.targetInfo.targetAvailable != 0))
                available.Add(GetTargetDeviceName(path.targetInfo.adapterId, path.targetInfo.id).monitorDevicePath ?? "");

            var missing = profile.Monitors.Where(monitor => !available.Contains(monitor.DevicePath)).ToList();
            if (missing.Count == 0)
            {
                AppLog.Info($"All {profile.Monitors.Length} monitor(s) of '{profile.Name}' available after {elapsed.ElapsedMilliseconds}ms.");
                return;
            }
            if (elapsed.Elapsed >= timeout)
                throw new InvalidOperationException(
                    $"'{profile.Name}' could not be completed: still waiting for {string.Join(", ", missing.Select(DescribeMonitor))} " +
                    $"after {timeout.TotalSeconds:0}s. Check that those displays are powered on and connected.");
            Thread.Sleep(250);
        }

        static string DescribeMonitor(MonitorInfo monitor) =>
            string.IsNullOrWhiteSpace(monitor.FriendlyName) ? monitor.DevicePath : monitor.FriendlyName;
    }

    private const SetDisplayConfigFlags TopologyLookupFlags =
        SetDisplayConfigFlags.SDC_TOPOLOGY_SUPPLIED |
        SetDisplayConfigFlags.SDC_ALLOW_PATH_ORDER_CHANGES |
        SetDisplayConfigFlags.SDC_VIRTUAL_MODE_AWARE;

    private const SetDisplayConfigFlags BestModeFlags =
        SetDisplayConfigFlags.SDC_USE_SUPPLIED_DISPLAY_CONFIG |
        SetDisplayConfigFlags.SDC_ALLOW_CHANGES |
        SetDisplayConfigFlags.SDC_VIRTUAL_MODE_AWARE;

    private static void ApplyWindowsTopology(DisplayProfile profile)
    {
        var (allPaths, matched) = BuildTopologyPaths(profile);

        // Apply with SDC_TOPOLOGY_SUPPLIED — pass ALL paths, no modes; Windows takes the modes
        // it last used for exactly this combination of monitors.
        int result = DisplayConfigApi.SetDisplayConfig(allPaths.Length, allPaths, 0, null,
            SetDisplayConfigFlags.SDC_APPLY | TopologyLookupFlags);
        AppLog.Info($"SetDisplayConfig topology result={result}; requested={profile.Monitors.Length}; matched={matched}; allPaths={allPaths.Length}.");

        if (result != DisplayConfigApi.ERROR_SUCCESS)
        {
            // That lookup fails for a combination Windows has never had (any layout built in
            // Arrange Displays, or a profile with an unplugged monitor left out). Microsoft's
            // documented recovery: the same paths with SDC_USE_SUPPLIED_DISPLAY_CONFIG, so best
            // mode logic picks the modes. ApplyLayout below then sets the saved ones, and its
            // SDC_SAVE_TO_DATABASE means the lookup works for this combination next time.
            var retry = DisplayConfigApi.SetDisplayConfig(allPaths.Length, allPaths, 0, null,
                SetDisplayConfigFlags.SDC_APPLY | SetDisplayConfigFlags.SDC_SAVE_TO_DATABASE | BestModeFlags);
            AppLog.Info($"Windows has no stored layout for this monitor combination (topology lookup returned {result}); " +
                $"best-mode retry result={retry}.");
            if (retry != DisplayConfigApi.ERROR_SUCCESS)
            {
                throw new InvalidOperationException(
                    $"Windows refused to switch on {DescribeMonitors(profile)} (SetDisplayConfig error {result}, then {retry}).");
            }
        }

        // Phase 2: Apply saved positions, primary, and resolution
        // Now that the correct monitors are active, query the current config
        // and adjust positions/primary to match the saved profile
        ApplyLayout(profile);
    }

    /// <summary>Asks Windows, without changing anything, whether it would accept the profile's
    /// monitor combination: via its stored layout, and via best-mode logic. Diagnostics only.</summary>
    internal static (int TopologyLookup, int BestMode) ValidateTopology(DisplayProfile profile)
    {
        var (allPaths, _) = BuildTopologyPaths(profile);
        var lookup = DisplayConfigApi.SetDisplayConfig(allPaths.Length, allPaths, 0, null,
            SetDisplayConfigFlags.SDC_VALIDATE | TopologyLookupFlags);
        var bestMode = DisplayConfigApi.SetDisplayConfig(allPaths.Length, allPaths, 0, null,
            SetDisplayConfigFlags.SDC_VALIDATE | BestModeFlags);
        return (lookup, bestMode);
    }

    private static string DescribeMonitors(DisplayProfile profile) =>
        string.Join(", ", profile.Monitors.Select(m => m.FriendlyName.Length > 0 ? m.FriendlyName : m.EdidId));

    /// <summary>Every CCD path, with exactly one path per wanted monitor marked active (each with
    /// its own clone group) and no modes supplied.</summary>
    private static (DISPLAYCONFIG_PATH_INFO[] Paths, int Matched) BuildTopologyPaths(DisplayProfile profile)
    {
        // Query ALL paths (including inactive)
        var (allPaths, _) = QueryConfig(
            QueryDisplayConfigFlags.QDC_ALL_PATHS |
            QueryDisplayConfigFlags.QDC_VIRTUAL_MODE_AWARE);

        // Build set of wanted device paths
        var wantedDevicePaths = new HashSet<string>(
            profile.Monitors.Select(m => m.DevicePath),
            StringComparer.OrdinalIgnoreCase);

        // Build device path lookup for all paths, and collect the friendly names of every
        // currently-connected monitor (for a useful error if nothing matches).
        var pathDevicePaths = new string[allPaths.Length];
        var connectedNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < allPaths.Length; i++)
        {
            var name = GetTargetDeviceName(allPaths[i].targetInfo.adapterId, allPaths[i].targetInfo.id);
            pathDevicePaths[i] = name.monitorDevicePath ?? "";
            if (!string.IsNullOrEmpty(pathDevicePaths[i]) &&
                !string.IsNullOrWhiteSpace(name.monitorFriendlyDeviceName))
                connectedNames.Add(name.monitorFriendlyDeviceName);
        }

        // Strategy (following MartinGC94/DisplayConfig pattern):
        // 1. For each wanted monitor, pick one path and mark it ACTIVE with a unique clone group
        // 2. For all other paths, clear ACTIVE and invalidate modes
        // 3. Pass ALL paths to SetDisplayConfig with SDC_TOPOLOGY_SUPPLIED

        var matchedDevicePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Source ids restart at 0 on every adapter, so a source is (adapter, id). Keying on the
        // id alone let four NVIDIA monitors (sources 0-3) block every DisplayLink source.
        var usedSources = new HashSet<(uint Low, int High, uint Id)>();
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
                var source = (allPaths[i].sourceInfo.adapterId.LowPart, allPaths[i].sourceInfo.adapterId.HighPart, allPaths[i].sourceInfo.id);
                if (usedSources.Contains(source))
                    continue;

                if (pass && savedMonitors.TryGetValue(devicePath, out var saved) &&
                    allPaths[i].sourceInfo.id != saved.SourceId)
                    continue;

                activatedIndices.Add(i);
                matchedDevicePaths.Add(devicePath);
                usedSources.Add(source);
            }
        }

        if (activatedIndices.Count == 0)
        {
            var wanted = string.Join(", ", profile.Monitors.Select(m => m.FriendlyName));
            var connected = connectedNames.Count > 0
                ? string.Join(", ", connectedNames)
                : "(none detected)";
            throw new InvalidOperationException(
                $"None of '{profile.Name}'s monitors are connected. " +
                $"Wanted: {wanted}. Currently connected: {connected}. " +
                "Power those displays on; if you edited profiles.json outside the app, restart it.");
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

        return (allPaths, activatedIndices.Count);
    }

    private static void ApplyLayout(DisplayProfile profile)
    {
        var (paths, modes) = QueryConfig(
            QueryDisplayConfigFlags.QDC_ONLY_ACTIVE_PATHS |
            QueryDisplayConfigFlags.QDC_VIRTUAL_MODE_AWARE);

        // Pristine copy of the paths, so a refresh rate the display refuses can be retried
        // WITHOUT the rate but WITH everything else, instead of losing the whole apply.
        // Geometry edits land in `modes` and target edits land in `paths`, so the two are
        // cleanly separable. Both arrays hold structs, so Clone() is a real copy.
        var pristinePaths = (DISPLAYCONFIG_PATH_INFO[])paths.Clone();

        // Rotation edits are replayed onto the pristine copy if the rate has to be dropped —
        // otherwise a rejected refresh rate would silently take the rotation down with it.
        var rotationEdits = new List<(int Index, DISPLAYCONFIG_ROTATION Rotation)>();

        // Build saved monitor lookup by device path
        var savedByPath = profile.Monitors.ToDictionary(
            m => m.DevicePath, StringComparer.OrdinalIgnoreCase);

        // Match current paths to saved monitors and update source modes
        bool changed = false;
        bool rateChanged = false;
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

            // Rotation ALSO lives on the target side, and was likewise captured, displayed
            // in the UI, and then never applied. The failure is invisible in the common
            // case, but lethal for a profile that differs from its twin ONLY by rotation
            // (portrait vs landscape on the same monitor at the same resolution): the
            // apply silently becomes a no-op. That is exactly what "it tries to switch but
            // nothing happens" looks like.
            //
            // NOTE: CCD keeps the source mode UNROTATED — a 90-rotated 4K panel reports a
            // 3840x2160 source with ROTATE90, even though the desktop measures 2160x3840.
            // So the saved resolution and rotation are already consistent; they just both
            // have to be applied.
            if (IsValidRotation(saved.Rotation) && paths[i].targetInfo.rotation != saved.Rotation)
            {
                paths[i].targetInfo.rotation = saved.Rotation;
                rotationEdits.Add((i, saved.Rotation));
                changed = true;
            }

            // Refresh rate hangs off the TARGET side of the path — it is not part of the
            // source mode. Restoring only the source mode (all this used to do) brought
            // back resolution and position but left Windows free to pick the rate, which
            // is how a profile captured at 60Hz came back at 144Hz.
            if (saved.RefreshRate.IsSpecified && !saved.RefreshRate.Matches(paths[i].targetInfo.refreshRate))
            {
                paths[i].targetInfo.refreshRate = saved.RefreshRate.ToRational();
                // The target mode Windows already chose encodes the OLD timings, so drop
                // it and let the new rate drive mode selection.
                paths[i].targetInfo.modeInfoIdx = DisplayConfigApi.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                changed = true;
                rateChanged = true;
            }
        }

        if (!changed)
        {
            AppLog.Info($"Layout for '{profile.Name}' already matched the saved positions, resolution, rotation, and refresh rate.");
            return;
        }

        // Apply the adjusted layout
        int result = ApplySuppliedConfig(paths, modes);
        AppLog.Info($"SetDisplayConfig layout result={result}; profile='{profile.Name}'; refreshRateChanged={rateChanged}; rotationChanges={rotationEdits.Count}.");
        if (result == DisplayConfigApi.ERROR_SUCCESS)
            return;

        if (rateChanged)
        {
            // The rate was refused — the monitor may be on a different port than when the
            // profile was captured, or behind a cable/EDID that cannot carry it. Retry
            // without it, but KEEP the rotation, so the rest of the profile still lands.
            foreach (var (index, rotation) in rotationEdits)
                pristinePaths[index].targetInfo.rotation = rotation;

            int retry = ApplySuppliedConfig(pristinePaths, modes);
            AppLog.Warn($"Layout with saved refresh rate failed ({result}); retry without refresh rate returned {retry} for profile '{profile.Name}'.");
            if (retry == DisplayConfigApi.ERROR_SUCCESS)
            {
                Console.Error.WriteLine(
                    $"Warning: '{profile.Name}' refresh rate was rejected (error {result}) — " +
                    "resolution, position and rotation were restored, rate left as Windows chose it");
                return;
            }
            result = retry;
        }

        // Non-fatal if layout apply fails — topology is already correct
        Console.Error.WriteLine($"Warning: Layout adjustment returned {result} — topology is correct but positions may differ");
        AppLog.Warn($"Layout adjustment returned {result} for profile '{profile.Name}'; topology succeeded but positions may differ.");
    }

    /// <summary>Guards against a profile written before rotation was persisted, or edited by
    /// hand — the enum is 1-4, so a 0 would be rejected by SetDisplayConfig and take the
    /// whole apply down with it.</summary>
    private static bool IsValidRotation(DISPLAYCONFIG_ROTATION r) =>
        r >= DISPLAYCONFIG_ROTATION.IDENTITY && r <= DISPLAYCONFIG_ROTATION.ROTATE270;

    private static int ApplySuppliedConfig(
        DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes) =>
        DisplayConfigApi.SetDisplayConfig(
            paths.Length, paths,
            modes.Length, modes,
            SetDisplayConfigFlags.SDC_APPLY |
            SetDisplayConfigFlags.SDC_USE_SUPPLIED_DISPLAY_CONFIG |
            SetDisplayConfigFlags.SDC_ALLOW_CHANGES |
            SetDisplayConfigFlags.SDC_SAVE_TO_DATABASE |
            SetDisplayConfigFlags.SDC_VIRTUAL_MODE_AWARE);

    public static string DescribeCurrentConfig()
    {
        var snapshot = CaptureCurrentConfig();
        var lines = new List<string>
        {
            NvidiaSurroundManager.DescribeActive(),
            $"Active Windows monitors ({snapshot.Monitors.Length}):"
        };
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

    /// <summary>Device paths of monitors that are connected right now (on or off). Unlike
    /// <see cref="GetAvailableMonitorDevicePaths"/>, this checks the target is actually available.</summary>
    public static HashSet<string> GetConnectedMonitorDevicePaths()
    {
        var (paths, _) = QueryConfig(
            QueryDisplayConfigFlags.QDC_ALL_PATHS |
            QueryDisplayConfigFlags.QDC_VIRTUAL_MODE_AWARE);
        var devicePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(path => path.targetInfo.targetAvailable != 0))
        {
            var name = GetTargetDeviceName(path.targetInfo.adapterId, path.targetInfo.id);
            if (!string.IsNullOrWhiteSpace(name.monitorDevicePath))
                devicePaths.Add(name.monitorDevicePath);
        }
        return devicePaths;
    }

    /// <summary>Unique device paths currently available to Windows, including inactive paths.</summary>
    public static HashSet<string> GetAvailableMonitorDevicePaths()
    {
        var (paths, _) = QueryConfig(
            QueryDisplayConfigFlags.QDC_ALL_PATHS |
            QueryDisplayConfigFlags.QDC_VIRTUAL_MODE_AWARE);
        var devicePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var name = GetTargetDeviceName(path.targetInfo.adapterId, path.targetInfo.id);
            if (!string.IsNullOrWhiteSpace(name.monitorDevicePath))
                devicePaths.Add(name.monitorDevicePath);
        }
        return devicePaths;
    }

    internal static (DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes) QueryConfig(
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

    internal static DISPLAYCONFIG_TARGET_DEVICE_NAME GetTargetDeviceName(LUID adapterId, uint targetId)
    {
        var name = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        name.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        name.header.size = Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        name.header.adapterId = adapterId;
        name.header.id = targetId;
        DisplayConfigApi.DisplayConfigGetDeviceInfo(ref name);
        return name;
    }

    internal static DISPLAYCONFIG_SOURCE_DEVICE_NAME GetSourceDeviceName(LUID adapterId, uint sourceId)
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
