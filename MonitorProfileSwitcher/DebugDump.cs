using MonitorProfileSwitcher.Native;

namespace MonitorProfileSwitcher;

internal static class DebugDump
{
    public static void DumpAllPaths()
    {
        int result = DisplayConfigApi.GetDisplayConfigBufferSizes(
            QueryDisplayConfigFlags.QDC_ALL_PATHS | QueryDisplayConfigFlags.QDC_VIRTUAL_MODE_AWARE,
            out int pathCount, out int modeCount);
        Console.WriteLine($"QDC_ALL_PATHS: result={result}, paths={pathCount}, modes={modeCount}");

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        result = DisplayConfigApi.QueryDisplayConfig(
            QueryDisplayConfigFlags.QDC_ALL_PATHS | QueryDisplayConfigFlags.QDC_VIRTUAL_MODE_AWARE,
            ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        Console.WriteLine($"QueryDisplayConfig: result={result}");

        // Group by target device path
        var seen = new HashSet<string>();
        for (int i = 0; i < pathCount; i++)
        {
            var p = paths[i];
            var name = GetTargetName(p.targetInfo.adapterId, p.targetInfo.id);
            bool active = (p.flags & 1) != 0;
            string key = $"{name.monitorDevicePath}|src{p.sourceInfo.id}";

            if (!seen.Add(key) && !active) continue; // Skip duplicate inactive entries

            Console.WriteLine($"  [{i}] {(active ? "ACTIVE" : "      ")} " +
                $"src={p.sourceInfo.id} tgt={p.targetInfo.id} " +
                $"flags=0x{p.flags:X} " +
                $"monitor={name.monitorFriendlyDeviceName}");
        }
    }

    private static DISPLAYCONFIG_TARGET_DEVICE_NAME GetTargetName(LUID adapterId, uint targetId)
    {
        var name = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        name.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        name.header.size = System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        name.header.adapterId = adapterId;
        name.header.id = targetId;
        DisplayConfigApi.DisplayConfigGetDeviceInfo(ref name);
        return name;
    }

    public static void DumpConfig()
    {
        int result = DisplayConfigApi.GetDisplayConfigBufferSizes(
            QueryDisplayConfigFlags.QDC_ONLY_ACTIVE_PATHS | QueryDisplayConfigFlags.QDC_VIRTUAL_MODE_AWARE,
            out int pathCount, out int modeCount);
        Console.WriteLine($"GetDisplayConfigBufferSizes: result={result}, paths={pathCount}, modes={modeCount}");

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        result = DisplayConfigApi.QueryDisplayConfig(
            QueryDisplayConfigFlags.QDC_ONLY_ACTIVE_PATHS | QueryDisplayConfigFlags.QDC_VIRTUAL_MODE_AWARE,
            ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        Console.WriteLine($"QueryDisplayConfig: result={result}");

        Console.WriteLine($"\nPaths ({pathCount}):");
        for (int i = 0; i < pathCount; i++)
        {
            var p = paths[i];
            Console.WriteLine($"  [{i}] source.id={p.sourceInfo.id} source.modeInfoIdx=0x{p.sourceInfo.modeInfoIdx:X8}" +
                $" target.id={p.targetInfo.id} target.modeInfoIdx=0x{p.targetInfo.modeInfoIdx:X8}" +
                $" flags=0x{p.flags:X} active={(p.flags & 1) != 0}");
        }

        Console.WriteLine($"\nModes ({modeCount}):");
        for (int i = 0; i < modeCount; i++)
        {
            var m = modes[i];
            Console.WriteLine($"  [{i}] type={m.infoType} id={m.id} adapter={m.adapterId}");
            if (m.infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                Console.WriteLine($"       SOURCE: {m.sourceMode.width}x{m.sourceMode.height} pos=({m.sourceMode.position.x},{m.sourceMode.position.y}) fmt={m.sourceMode.pixelFormat}");
            else if (m.infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
                Console.WriteLine($"       TARGET: active={m.targetMode.targetVideoSignalInfo.activeSize.cx}x{m.targetMode.targetVideoSignalInfo.activeSize.cy}" +
                    $" vSync={m.targetMode.targetVideoSignalInfo.vSyncFreq}");
            else if (m.infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE)
                Console.WriteLine($"       DESKTOP_IMAGE");
        }
    }
}
