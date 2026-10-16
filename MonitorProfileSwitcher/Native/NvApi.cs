using System.Runtime.InteropServices;
using System.Text;

namespace MonitorProfileSwitcher.Native;

/// <summary>
/// Minimal interop for the public NVIDIA Mosaic/Surround API. NVIDIA calls consumer Surround
/// "immersive gaming" in this API. Definitions mirror the Release 590 nvapi.h structures.
/// </summary>
internal static class NvApi
{
    internal const int Success = 0;
    internal const int MaxDisplays = 64;

    private const uint InitializeId = 0x0150E828;
    private const uint UnloadId = 0xD22BDD7E;
    private const uint GetErrorMessageId = 0x6C2D048C;
    private const uint EnableCurrentTopologyId = 0x5F1AA66C;
    private const uint SetDisplayGridsId = 0x4D959A89;
    private const uint ValidateDisplayGridsId = 0xCF43903D;
    private const uint EnumDisplayGridsId = 0xDF2887AF;
    private const uint GetDisplayIdByDisplayNameId = 0xAE457190; // nvapi_interface.h

    private static readonly IntPtr Library = LoadLibrary();
    private static readonly QueryInterfaceDelegate QueryInterface = LoadQueryInterface();
    private static readonly InitializeDelegate InitializeFunction = GetFunction<InitializeDelegate>(InitializeId);
    private static readonly InitializeDelegate UnloadFunction = GetFunction<InitializeDelegate>(UnloadId);
    private static readonly GetErrorMessageDelegate GetErrorMessageFunction = GetFunction<GetErrorMessageDelegate>(GetErrorMessageId);
    private static readonly EnableCurrentTopologyDelegate EnableCurrentTopologyFunction = GetFunction<EnableCurrentTopologyDelegate>(EnableCurrentTopologyId);
    private static readonly DisplayGridsDelegate SetDisplayGridsFunction = GetFunction<DisplayGridsDelegate>(SetDisplayGridsId);
    private static readonly ValidateDisplayGridsDelegate ValidateDisplayGridsFunction = GetFunction<ValidateDisplayGridsDelegate>(ValidateDisplayGridsId);
    private static readonly EnumDisplayGridsDelegate EnumDisplayGridsFunction = GetFunction<EnumDisplayGridsDelegate>(EnumDisplayGridsId);

    private static GetDisplayIdByDisplayNameDelegate? _getDisplayIdByDisplayName;

    internal static void Initialize() => ThrowIfFailed(InitializeFunction(), "NvAPI_Initialize");

    /// <summary>The NVIDIA display id behind a GDI display (\\.\DISPLAYn) — how a monitor on the
    /// Windows desktop is matched to an entry in NVIDIA's display grids. Resolved on first use
    /// rather than at type load, so a driver without it cannot break Surround switching.</summary>
    internal static uint GetDisplayIdByDisplayName(string gdiDeviceName)
    {
        _getDisplayIdByDisplayName ??= GetFunction<GetDisplayIdByDisplayNameDelegate>(GetDisplayIdByDisplayNameId);
        ThrowIfFailed(_getDisplayIdByDisplayName(gdiDeviceName, out var displayId), "NvAPI_DISP_GetDisplayIdByDisplayName");
        return displayId;
    }

    internal static void Unload()
    {
        // Unload is paired with Initialize, but a failure during cleanup should never hide the
        // more useful error from the topology operation itself.
        _ = UnloadFunction();
    }

    internal static NvMosaicGridTopologyV2[] EnumerateDisplayGrids()
    {
        uint count = 0;
        int status = EnumDisplayGridsFunction(IntPtr.Zero, ref count);
        ThrowIfFailed(status, "NvAPI_Mosaic_EnumDisplayGrids");
        if (count == 0)
            return [];

        int structSize = Marshal.SizeOf<NvMosaicGridTopologyV2>();
        IntPtr buffer = Marshal.AllocHGlobal(checked(structSize * (int)count));
        try
        {
            for (int i = 0; i < count; i++)
                Marshal.StructureToPtr(NvMosaicGridTopologyV2.CreateEmpty(), IntPtr.Add(buffer, i * structSize), false);

            uint returned = count;
            status = EnumDisplayGridsFunction(buffer, ref returned);
            ThrowIfFailed(status, "NvAPI_Mosaic_EnumDisplayGrids");

            var grids = new NvMosaicGridTopologyV2[returned];
            for (int i = 0; i < returned; i++)
                grids[i] = Marshal.PtrToStructure<NvMosaicGridTopologyV2>(IntPtr.Add(buffer, i * structSize));
            return grids;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void SetDisplayGrids(NvMosaicGridTopologyV2[] grids)
    {
        if (grids.Length == 0)
            throw new ArgumentException("At least one NVIDIA display grid is required.", nameof(grids));

        int structSize = Marshal.SizeOf<NvMosaicGridTopologyV2>();
        IntPtr buffer = Marshal.AllocHGlobal(checked(structSize * grids.Length));
        try
        {
            for (int i = 0; i < grids.Length; i++)
                Marshal.StructureToPtr(grids[i], IntPtr.Add(buffer, i * structSize), false);

            // No flags: allow NVAPI to select the viable GPU topology and reload the display
            // driver if the transition requires it. This matches NVIDIA Control Panel behavior.
            ThrowIfFailed(SetDisplayGridsFunction(buffer, (uint)grids.Length, 0),
                "NvAPI_Mosaic_SetDisplayGrids");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static NvMosaicDisplayTopologyStatus[] ValidateDisplayGrids(NvMosaicGridTopologyV2[] grids)
    {
        if (grids.Length == 0)
            throw new ArgumentException("At least one NVIDIA display grid is required.", nameof(grids));

        int gridSize = Marshal.SizeOf<NvMosaicGridTopologyV2>();
        int statusSize = Marshal.SizeOf<NvMosaicDisplayTopologyStatus>();
        IntPtr gridBuffer = Marshal.AllocHGlobal(checked(gridSize * grids.Length));
        IntPtr statusBuffer = Marshal.AllocHGlobal(checked(statusSize * grids.Length));
        try
        {
            for (int i = 0; i < grids.Length; i++)
            {
                Marshal.StructureToPtr(grids[i], IntPtr.Add(gridBuffer, i * gridSize), false);
                Marshal.StructureToPtr(NvMosaicDisplayTopologyStatus.CreateEmpty(),
                    IntPtr.Add(statusBuffer, i * statusSize), false);
            }

            ThrowIfFailed(ValidateDisplayGridsFunction(0, gridBuffer, statusBuffer, (uint)grids.Length),
                "NvAPI_Mosaic_ValidateDisplayGrids");

            var statuses = new NvMosaicDisplayTopologyStatus[grids.Length];
            for (int i = 0; i < statuses.Length; i++)
                statuses[i] = Marshal.PtrToStructure<NvMosaicDisplayTopologyStatus>(
                    IntPtr.Add(statusBuffer, i * statusSize));
            return statuses;
        }
        finally
        {
            Marshal.FreeHGlobal(statusBuffer);
            Marshal.FreeHGlobal(gridBuffer);
        }
    }

    internal static void EnableCurrentTopology(bool enable) =>
        ThrowIfFailed(EnableCurrentTopologyFunction(enable ? 1u : 0u),
            "NvAPI_Mosaic_EnableCurrentTopo");

    internal static string GetErrorMessage(int status)
    {
        var text = new StringBuilder(64);
        return GetErrorMessageFunction(status, text) == Success
            ? text.ToString()
            : $"NVAPI status {status}";
    }

    private static void ThrowIfFailed(int status, string operation)
    {
        if (status != Success)
            throw new NvidiaApiException($"{operation} failed: {GetErrorMessage(status)} ({status})", status);
    }

    private static IntPtr LoadLibrary()
    {
        string name = Environment.Is64BitProcess ? "nvapi64.dll" : "nvapi.dll";
        if (!NativeLibrary.TryLoad(name, out var library))
            throw new DllNotFoundException($"{name} was not found. Install an NVIDIA display driver.");
        return library;
    }

    private static QueryInterfaceDelegate LoadQueryInterface()
    {
        IntPtr export = NativeLibrary.GetExport(Library, "nvapi_QueryInterface");
        return Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(export);
    }

    private static T GetFunction<T>(uint id) where T : Delegate
    {
        IntPtr function = QueryInterface(id);
        if (function == IntPtr.Zero)
            throw new EntryPointNotFoundException($"NVIDIA NVAPI function 0x{id:X8} is unavailable.");
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    internal static uint MakeVersion<T>(uint version) =>
        checked((uint)Marshal.SizeOf<T>() | (version << 16));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr QueryInterfaceDelegate(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetErrorMessageDelegate(int status, StringBuilder description);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnableCurrentTopologyDelegate(uint enable);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DisplayGridsDelegate(IntPtr grids, uint gridCount, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ValidateDisplayGridsDelegate(
        uint flags, IntPtr grids, IntPtr topologyStatuses, uint gridCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumDisplayGridsDelegate(IntPtr grids, ref uint gridCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDisplayIdByDisplayNameDelegate([MarshalAs(UnmanagedType.LPStr)] string displayName, out uint displayId);
}

internal sealed class NvidiaApiException : InvalidOperationException
{
    public int Status { get; }

    public NvidiaApiException(string message, int status) : base(message) => Status = status;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvMosaicGridTopologyDisplayV2
{
    public uint Version;
    public uint DisplayId;
    public int OverlapX;
    public int OverlapY;
    public uint Rotation;
    public uint CloneGroup;
    public uint PixelShiftType;

    public static NvMosaicGridTopologyDisplayV2 CreateEmpty() => new()
    {
        // NVIDIA defines this embedded structure as version 0, size 28.
        Version = NvApi.MakeVersion<NvMosaicGridTopologyDisplayV2>(0)
    };
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvMosaicDisplaySettingV1
{
    public uint Version;
    public uint Width;
    public uint Height;
    public uint BitsPerPixel;
    public uint Frequency;

    public static NvMosaicDisplaySettingV1 CreateEmpty() => new()
    {
        Version = NvApi.MakeVersion<NvMosaicDisplaySettingV1>(1)
    };
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvMosaicGridTopologyV2
{
    public uint Version;
    public uint Rows;
    public uint Columns;
    public uint DisplayCount;
    public uint Flags;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = NvApi.MaxDisplays)]
    public NvMosaicGridTopologyDisplayV2[] Displays;

    public NvMosaicDisplaySettingV1 DisplaySettings;

    public static NvMosaicGridTopologyV2 CreateEmpty()
    {
        var displays = new NvMosaicGridTopologyDisplayV2[NvApi.MaxDisplays];
        for (int i = 0; i < displays.Length; i++)
            displays[i] = NvMosaicGridTopologyDisplayV2.CreateEmpty();

        var grid = new NvMosaicGridTopologyV2
        {
            Displays = displays,
            DisplaySettings = NvMosaicDisplaySettingV1.CreateEmpty()
        };
        grid.Version = NvApi.MakeVersion<NvMosaicGridTopologyV2>(2);
        return grid;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvMosaicDisplayTopologyStatusDisplay
{
    public uint DisplayId;
    public uint ErrorFlags;
    public uint WarningFlags;
    public uint SupportsRotationAndReserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvMosaicDisplayTopologyStatus
{
    public uint Version;
    public uint ErrorFlags;
    public uint WarningFlags;
    public uint DisplayCount;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = NvApi.MaxDisplays)]
    public NvMosaicDisplayTopologyStatusDisplay[] Displays;

    public static NvMosaicDisplayTopologyStatus CreateEmpty() => new()
    {
        Version = NvApi.MakeVersion<NvMosaicDisplayTopologyStatus>(1),
        Displays = new NvMosaicDisplayTopologyStatusDisplay[NvApi.MaxDisplays]
    };
}
