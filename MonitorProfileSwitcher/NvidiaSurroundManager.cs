using System.Diagnostics;
using MonitorProfileSwitcher.Native;

namespace MonitorProfileSwitcher;

internal static class NvidiaSurroundManager
{
    private const uint ApplyWithBezelCorrectFlag = 1u << 0;
    private const uint ImmersiveGamingFlag = 1u << 1;
    private const uint BaseMosaicFlag = 1u << 2;
    private const uint DriverReloadAllowedFlag = 1u << 3;
    private const uint AcceleratePrimaryDisplayFlag = 1u << 4;
    private const uint PixelShiftFlag = 1u << 5;
    private static readonly object Sync = new();

    /// <summary>Returns the active multi-display Surround grid, or null on a non-NVIDIA system.</summary>
    public static NvidiaSurroundConfig? TryCaptureActive()
    {
        lock (Sync)
        {
            bool initialized = false;
            try
            {
                NvApi.Initialize();
                initialized = true;
                return CaptureActiveCore();
            }
            catch (Exception ex) when (IsUnavailable(ex))
            {
                return null;
            }
            finally
            {
                if (initialized)
                    NvApi.Unload();
            }
        }
    }

    public static void Apply(NvidiaSurroundConfig config)
    {
        Validate(config);

        lock (Sync)
        {
            WithNvApi(() =>
            {
                var grid = ToNative(config);
                ValidateNativeGrid(grid);
                try
                {
                    NvApi.SetDisplayGrids([grid]);
                }
                catch (NvidiaApiException ex) when (ex.Status is -3 or -9)
                {
                    // Some GeForce drivers expose grid enumeration but reject the public
                    // SetDisplayGrids structure version. Enabling the driver's persisted current
                    // topology is the documented fallback and is exactly what Control Panel does.
                    NvApi.EnableCurrentTopology(true);
                }
                WaitForState(expectedActive: true, config);
            });
        }
    }

    public static string ValidateActive()
    {
        lock (Sync)
        {
            string result = "";
            WithNvApi(() =>
            {
                var active = CaptureActiveCore() ??
                    throw new InvalidOperationException("NVIDIA Surround is not active.");
                Validate(active);
                bool driverValidated = ValidateNativeGrid(ToNative(active));
                result = driverValidated
                    ? $"NVIDIA Surround {active.Rows}x{active.Columns} is valid for the current hardware."
                    : $"NVIDIA Surround {active.Rows}x{active.Columns} was captured successfully; " +
                      "this driver does not support the optional grid-validation call.";
            });
            return result;
        }
    }

    /// <summary>Reconstruct a pre-Surround-support profile from the driver's active 1x1 grids.</summary>
    public static NvidiaSurroundConfig ApplyLegacy(DisplayProfile profile)
    {
        lock (Sync)
        {
            NvidiaSurroundConfig? migrated = null;
            WithNvApi(() =>
            {
                migrated = CaptureActiveCore() ?? InferLegacyGrid(profile);
                var grid = ToNative(migrated);
                ValidateNativeGrid(grid);
                NvApi.SetDisplayGrids([grid]);
                WaitForState(expectedActive: true, migrated);
            });
            return migrated!;
        }
    }

    /// <summary>Disable Surround before handing the physical outputs back to Windows CCD.</summary>
    public static bool DisableIfActive()
    {
        lock (Sync)
        {
            bool disabled = false;
            bool initialized = false;
            try
            {
                NvApi.Initialize();
                initialized = true;
                var active = CaptureActiveCore();
                if (active == null)
                    return false;

                try
                {
                    NvApi.EnableCurrentTopology(false);
                }
                catch (NvidiaApiException ex) when (ex.Status is -9 or -150)
                {
                    // Current GeForce drivers can enumerate and enable an immersive grid but
                    // return MODE_CHANGE_FAILED when disabling it through the older toggle API.
                    // NVIDIA's current SetDisplayGrids contract explicitly defines a 1x1 grid
                    // as the way to leave a multi-display grid.
                    NvApi.SetDisplayGrids([ToSingleDisplayGrid(active)]);
                }
                disabled = true;
                WaitForState(expectedActive: false, expected: null);
                return true;
            }
            catch (Exception ex) when (!disabled && IsUnavailable(ex))
            {
                // A normal Windows-only profile must continue to work on non-NVIDIA systems.
                return false;
            }
            finally
            {
                if (initialized)
                    NvApi.Unload();
            }
        }
    }

    public static string DescribeActive()
    {
        var config = TryCaptureActive();
        return config == null
            ? "NVIDIA Surround: inactive"
            : $"NVIDIA Surround: active {config.Rows}x{config.Columns}, " +
              $"{config.WidthPerDisplay}x{config.HeightPerDisplay} per display @ {config.Frequency}Hz";
    }

    private static void WithNvApi(Action action)
    {
        bool initialized = false;
        try
        {
            NvApi.Initialize();
            initialized = true;
            action();
        }
        catch (TypeInitializationException ex) when (ex.InnerException != null)
        {
            throw new InvalidOperationException(
                $"NVIDIA Surround support is unavailable: {ex.InnerException.Message}", ex.InnerException);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException("NVIDIA Surround support requires an NVIDIA display driver.", ex);
        }
        finally
        {
            if (initialized)
                NvApi.Unload();
        }
    }

    private static NvidiaSurroundConfig? CaptureActiveCore()
    {
        var grid = NvApi.EnumerateDisplayGrids().FirstOrDefault(IsSurroundGrid);
        if (!IsSurroundGrid(grid))
            return null;

        return new NvidiaSurroundConfig
        {
            Rows = grid.Rows,
            Columns = grid.Columns,
            WidthPerDisplay = grid.DisplaySettings.Width,
            HeightPerDisplay = grid.DisplaySettings.Height,
            BitsPerPixel = grid.DisplaySettings.BitsPerPixel,
            Frequency = grid.DisplaySettings.Frequency,
            ApplyWithBezelCorrection = (grid.Flags & ApplyWithBezelCorrectFlag) != 0,
            ImmersiveGaming = (grid.Flags & ImmersiveGamingFlag) != 0,
            AcceleratePrimaryDisplay = (grid.Flags & AcceleratePrimaryDisplayFlag) != 0,
            Displays = grid.Displays.Take((int)grid.DisplayCount).Select(d => new NvidiaSurroundDisplay
            {
                DisplayId = d.DisplayId,
                OverlapX = d.OverlapX,
                OverlapY = d.OverlapY,
                Rotation = d.Rotation,
                CloneGroup = d.CloneGroup,
                PixelShiftType = d.PixelShiftType,
            }).ToArray()
        };
    }

    private static NvidiaSurroundConfig InferLegacyGrid(DisplayProfile profile)
    {
        if (profile.Monitors.Length != 1)
            throw new InvalidOperationException("The legacy NVIDIA Surround profile is not a single logical display.");

        var logical = profile.Monitors[0].Resolution;
        var singles = NvApi.EnumerateDisplayGrids()
            .Where(g => g.Rows == 1 && g.Columns == 1 && g.DisplayCount == 1)
            .ToArray();

        var match = singles
            .GroupBy(g => new
            {
                g.DisplaySettings.Width,
                g.DisplaySettings.Height,
                g.DisplaySettings.BitsPerPixel,
                g.DisplaySettings.Frequency
            })
            .Select(group => new { Grids = group.ToArray(), group.Key })
            .FirstOrDefault(candidate =>
                (logical.Width == candidate.Key.Width * candidate.Grids.Length &&
                 logical.Height == candidate.Key.Height) ||
                (logical.Height == candidate.Key.Height * candidate.Grids.Length &&
                 logical.Width == candidate.Key.Width));

        if (match == null || match.Grids.Length < 2)
            throw new InvalidOperationException(
                "This legacy profile does not contain the NVIDIA display grid and it could not be inferred. " +
                "Enable Surround once in NVIDIA Control Panel, then recapture this profile.");

        bool horizontal = logical.Width == match.Key.Width * match.Grids.Length;
        return new NvidiaSurroundConfig
        {
            Rows = horizontal ? 1u : (uint)match.Grids.Length,
            Columns = horizontal ? (uint)match.Grids.Length : 1u,
            WidthPerDisplay = match.Key.Width,
            HeightPerDisplay = match.Key.Height,
            BitsPerPixel = match.Key.BitsPerPixel,
            Frequency = match.Key.Frequency,
            ImmersiveGaming = true,
            AcceleratePrimaryDisplay = false,
            Displays = match.Grids.Select(g => new NvidiaSurroundDisplay
            {
                DisplayId = g.Displays[0].DisplayId,
                Rotation = g.Displays[0].Rotation,
                PixelShiftType = g.Displays[0].PixelShiftType,
            }).ToArray()
        };
    }

    private static bool IsSurroundGrid(NvMosaicGridTopologyV2 grid) =>
        grid.Displays != null && grid.DisplayCount > 1 &&
        (grid.Rows > 1 || grid.Columns > 1) &&
        (grid.Flags & (ImmersiveGamingFlag | BaseMosaicFlag)) != 0;

    private static NvMosaicGridTopologyV2 ToNative(NvidiaSurroundConfig config)
    {
        var grid = NvMosaicGridTopologyV2.CreateEmpty();
        grid.Rows = config.Rows;
        grid.Columns = config.Columns;
        grid.DisplayCount = (uint)config.Displays.Length;
        grid.Flags = DriverReloadAllowedFlag;
        if (config.ApplyWithBezelCorrection) grid.Flags |= ApplyWithBezelCorrectFlag;
        if (config.ImmersiveGaming) grid.Flags |= ImmersiveGamingFlag;
        // acceleratePrimaryDisplay is explicitly undefined when a grid is read back. Do not
        // replay that output-only value; current GeForce drivers reject it as an input flag.
        if (config.Displays.Any(d => d.PixelShiftType != 0)) grid.Flags |= PixelShiftFlag;

        grid.DisplaySettings.Width = config.WidthPerDisplay;
        grid.DisplaySettings.Height = config.HeightPerDisplay;
        grid.DisplaySettings.BitsPerPixel = config.BitsPerPixel;
        grid.DisplaySettings.Frequency = config.Frequency;

        for (int i = 0; i < config.Displays.Length; i++)
        {
            var saved = config.Displays[i];
            grid.Displays[i].DisplayId = saved.DisplayId;
            grid.Displays[i].OverlapX = saved.OverlapX;
            grid.Displays[i].OverlapY = saved.OverlapY;
            grid.Displays[i].Rotation = saved.Rotation;
            grid.Displays[i].CloneGroup = saved.CloneGroup;
            grid.Displays[i].PixelShiftType = saved.PixelShiftType;
        }
        return grid;
    }

    private static NvMosaicGridTopologyV2 ToSingleDisplayGrid(NvidiaSurroundConfig config)
    {
        var grid = NvMosaicGridTopologyV2.CreateEmpty();
        grid.Rows = 1;
        grid.Columns = 1;
        grid.DisplayCount = 1;
        grid.Flags = DriverReloadAllowedFlag;
        grid.DisplaySettings.Width = config.WidthPerDisplay;
        grid.DisplaySettings.Height = config.HeightPerDisplay;
        grid.DisplaySettings.BitsPerPixel = config.BitsPerPixel;
        grid.DisplaySettings.Frequency = config.Frequency;

        // Prefer the center panel in a horizontal/vertical strip so the temporary 1x1 desktop
        // appears on the natural primary screen. CCD activates the complete saved layout next.
        int index = config.Rows == 1
            ? (int)(config.Columns / 2)
            : (int)(config.Rows / 2 * config.Columns);
        var selected = config.Displays[index];
        grid.Displays[0].DisplayId = selected.DisplayId;
        grid.Displays[0].Rotation = selected.Rotation;
        grid.Displays[0].PixelShiftType = selected.PixelShiftType;
        return grid;
    }

    private static void Validate(NvidiaSurroundConfig config)
    {
        if (config.Rows == 0 || config.Columns == 0)
            throw new InvalidOperationException("The saved NVIDIA Surround grid has no rows or columns.");
        if (config.Displays.Length == 0 || config.Displays.Length > NvApi.MaxDisplays)
            throw new InvalidOperationException("The saved NVIDIA Surround grid has an invalid display count.");
        if ((ulong)config.Rows * config.Columns != (ulong)config.Displays.Length)
            throw new InvalidOperationException("The saved NVIDIA Surround grid dimensions do not match its displays.");
        if (config.WidthPerDisplay == 0 || config.HeightPerDisplay == 0 || config.Frequency == 0)
            throw new InvalidOperationException("The saved NVIDIA Surround display mode is incomplete; recapture the profile.");
        if (config.Displays.Select(d => d.DisplayId).Distinct().Count() != config.Displays.Length)
            throw new InvalidOperationException("The saved NVIDIA Surround grid contains duplicate displays.");
        if (config.Displays.Any(d => d.Rotation > 4))
            throw new InvalidOperationException("The saved NVIDIA Surround grid contains an invalid rotation.");
    }

    private static bool ValidateNativeGrid(NvMosaicGridTopologyV2 grid)
    {
        NvMosaicDisplayTopologyStatus status;
        try
        {
            status = NvApi.ValidateDisplayGrids([grid])[0];
        }
        catch (NvidiaApiException ex) when (ex.Status is -3 or -9)
        {
            return false;
        }

        if (status.ErrorFlags != 0)
            throw new InvalidOperationException(
                $"NVIDIA rejected the saved Surround topology (error flags 0x{status.ErrorFlags:X}).");

        var displayErrors = status.Displays.Take((int)status.DisplayCount)
            .Where(d => d.ErrorFlags != 0)
            .Select(d => $"display {d.DisplayId}: 0x{d.ErrorFlags:X}")
            .ToArray();
        if (displayErrors.Length > 0)
            throw new InvalidOperationException(
                "NVIDIA rejected the saved Surround displays: " + string.Join(", ", displayErrors));
        return true;
    }

    private static void WaitForState(bool expectedActive, NvidiaSurroundConfig? expected)
    {
        var timeout = Stopwatch.StartNew();
        Exception? lastError = null;
        while (timeout.Elapsed < TimeSpan.FromSeconds(20))
        {
            try
            {
                var active = CaptureActiveCore();
                if (!expectedActive && active == null)
                    return;
                if (expectedActive && active != null && (expected == null || Matches(active, expected)))
                    return;
            }
            catch (Exception ex)
            {
                // The driver can briefly reject queries while its display topology is rebuilding.
                lastError = ex;
            }
            Thread.Sleep(250);
        }

        string target = expectedActive ? "become active" : "turn off";
        throw new TimeoutException($"Timed out waiting for NVIDIA Surround to {target}.", lastError);
    }

    private static bool Matches(NvidiaSurroundConfig actual, NvidiaSurroundConfig expected) =>
        actual.Rows == expected.Rows && actual.Columns == expected.Columns &&
        actual.WidthPerDisplay == expected.WidthPerDisplay &&
        actual.HeightPerDisplay == expected.HeightPerDisplay &&
        actual.Displays.Select(d => d.DisplayId).SequenceEqual(expected.Displays.Select(d => d.DisplayId));

    private static bool IsUnavailable(Exception ex) =>
        ex is NvidiaApiException { Status: -3 or -6 or -104 } ||
        ex is DllNotFoundException or EntryPointNotFoundException ||
        ex is TypeInitializationException { InnerException: DllNotFoundException or EntryPointNotFoundException };
}
