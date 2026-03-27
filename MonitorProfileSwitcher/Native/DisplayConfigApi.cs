using System.Runtime.InteropServices;

namespace MonitorProfileSwitcher.Native;

internal static class DisplayConfigApi
{
    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(
        QueryDisplayConfigFlags flags,
        out int numPathArrayElements,
        out int numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(
        QueryDisplayConfigFlags flags,
        ref int numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref int numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int SetDisplayConfig(
        int numPathArrayElements,
        DISPLAYCONFIG_PATH_INFO[]? pathArray,
        int numModeInfoArrayElements,
        DISPLAYCONFIG_MODE_INFO[]? modeInfoArray,
        SetDisplayConfigFlags flags);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(
        ref DISPLAYCONFIG_TARGET_DEVICE_NAME deviceName);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(
        ref DISPLAYCONFIG_SOURCE_DEVICE_NAME deviceName);

    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
}

[Flags]
internal enum QueryDisplayConfigFlags : uint
{
    QDC_ALL_PATHS = 0x00000001,
    QDC_ONLY_ACTIVE_PATHS = 0x00000002,
    QDC_DATABASE_CURRENT = 0x00000004,
    QDC_VIRTUAL_MODE_AWARE = 0x00000010,
    QDC_INCLUDE_HMD = 0x00000020,
    QDC_VIRTUAL_REFRESH_RATE_AWARE = 0x00000040,
}

[Flags]
internal enum SetDisplayConfigFlags : uint
{
    SDC_TOPOLOGY_INTERNAL = 0x00000001,
    SDC_TOPOLOGY_CLONE = 0x00000002,
    SDC_TOPOLOGY_EXTEND = 0x00000004,
    SDC_TOPOLOGY_EXTERNAL = 0x00000008,
    SDC_TOPOLOGY_SUPPLIED = 0x00000010,
    SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020,
    SDC_VALIDATE = 0x00000040,
    SDC_APPLY = 0x00000080,
    SDC_NO_OPTIMIZATION = 0x00000100,
    SDC_SAVE_TO_DATABASE = 0x00000200,
    SDC_ALLOW_CHANGES = 0x00000400,
    SDC_PATH_PERSIST_IF_REQUIRED = 0x00000800,
    SDC_FORCE_MODE_ENUMERATION = 0x00001000,
    SDC_ALLOW_PATH_ORDER_CHANGES = 0x00002000,
    SDC_VIRTUAL_MODE_AWARE = 0x00008000,
    SDC_VIRTUAL_REFRESH_RATE_AWARE = 0x00020000,
    SDC_USE_DATABASE_CURRENT = 0x0000000F,
}

internal enum DISPLAYCONFIG_DEVICE_INFO_TYPE : uint
{
    DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1,
    DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2,
    DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE = 3,
    DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME = 4,
    DISPLAYCONFIG_DEVICE_INFO_SET_TARGET_PERSISTENCE = 5,
    DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_BASE_TYPE = 6,
    DISPLAYCONFIG_DEVICE_INFO_GET_SUPPORT_VIRTUAL_RESOLUTION = 7,
    DISPLAYCONFIG_DEVICE_INFO_SET_SUPPORT_VIRTUAL_RESOLUTION = 8,
    DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9,
    DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 10,
    DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11,
}

internal enum DISPLAYCONFIG_MODE_INFO_TYPE : uint
{
    DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1,
    DISPLAYCONFIG_MODE_INFO_TYPE_TARGET = 2,
    DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE = 3,
}

internal enum DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY : uint
{
    OTHER = 0xFFFFFFFF,
    HD15 = 0,
    SVIDEO = 1,
    COMPOSITE_VIDEO = 2,
    COMPONENT_VIDEO = 3,
    DVI = 4,
    HDMI = 5,
    LVDS = 6,
    D_JPN = 8,
    SDI = 9,
    DISPLAYPORT_EXTERNAL = 10,
    DISPLAYPORT_EMBEDDED = 11,
    UDI_EXTERNAL = 12,
    UDI_EMBEDDED = 13,
    SDTVDONGLE = 14,
    MIRACAST = 15,
    INDIRECT_WIRED = 16,
    INDIRECT_VIRTUAL = 17,
    INTERNAL = 0x80000000,
}

internal enum DISPLAYCONFIG_ROTATION : uint
{
    IDENTITY = 1,
    ROTATE90 = 2,
    ROTATE180 = 3,
    ROTATE270 = 4,
}

internal enum DISPLAYCONFIG_SCALING : uint
{
    IDENTITY = 1,
    CENTERED = 2,
    STRETCHED = 3,
    ASPECTRATIOCENTEREDMAX = 4,
    CUSTOM = 5,
    PREFERRED = 128,
}

internal enum DISPLAYCONFIG_SCANLINE_ORDERING : uint
{
    UNSPECIFIED = 0,
    PROGRESSIVE = 1,
    INTERLACED = 2,
    INTERLACED_UPPERFIELDFIRST = 2,
    INTERLACED_LOWERFIELDFIRST = 3,
}

internal enum DISPLAYCONFIG_PIXELFORMAT : uint
{
    PIXELFORMAT_8BPP = 1,
    PIXELFORMAT_16BPP = 2,
    PIXELFORMAT_24BPP = 3,
    PIXELFORMAT_32BPP = 4,
    PIXELFORMAT_NONGDI = 5,
}

[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public uint LowPart;
    public int HighPart;

    public override string ToString() => $"{HighPart:X8}-{LowPart:X8}";
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_RATIONAL
{
    public uint Numerator;
    public uint Denominator;

    public double ToDouble() => Denominator == 0 ? 0 : (double)Numerator / Denominator;
    public override string ToString() => $"{ToDouble():F2}Hz";
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_2DREGION
{
    public uint cx;
    public uint cy;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTL
{
    public int x;
    public int y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY outputTechnology;
    public DISPLAYCONFIG_ROTATION rotation;
    public DISPLAYCONFIG_SCALING scaling;
    public DISPLAYCONFIG_RATIONAL refreshRate;
    public DISPLAYCONFIG_SCANLINE_ORDERING scanLineOrdering;
    public int targetAvailable;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_INFO
{
    public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
    public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
    public uint flags;

    public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
{
    public ulong pixelRate;
    public DISPLAYCONFIG_RATIONAL hSyncFreq;
    public DISPLAYCONFIG_RATIONAL vSyncFreq;
    public DISPLAYCONFIG_2DREGION activeSize;
    public DISPLAYCONFIG_2DREGION totalSize;
    public uint AdditionalSignalInfo;
    public DISPLAYCONFIG_SCANLINE_ORDERING scanLineOrdering;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_TARGET_MODE
{
    public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_SOURCE_MODE
{
    public uint width;
    public uint height;
    public DISPLAYCONFIG_PIXELFORMAT pixelFormat;
    public POINTL position;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO
{
    public POINTL PathSourceSize;
    public RECT DesktopImageRegion;
    public RECT DesktopImageClip;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int left, top, right, bottom;
}

[StructLayout(LayoutKind.Explicit)]
internal struct DISPLAYCONFIG_MODE_INFO
{
    [FieldOffset(0)] public DISPLAYCONFIG_MODE_INFO_TYPE infoType;
    [FieldOffset(4)] public uint id;
    [FieldOffset(8)] public LUID adapterId;
    [FieldOffset(16)] public DISPLAYCONFIG_TARGET_MODE targetMode;
    [FieldOffset(16)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
    [FieldOffset(16)] public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImageInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
{
    public DISPLAYCONFIG_DEVICE_INFO_TYPE type;
    public int size;
    public LUID adapterId;
    public uint id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint flags;
    public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY outputTechnology;
    public ushort edidManufactureId;
    public ushort edidProductCodeId;
    public uint connectorInstance;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string monitorFriendlyDeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string monitorDevicePath;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string viewGdiDeviceName;
}
