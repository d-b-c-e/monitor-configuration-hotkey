using System.Drawing;
using MonitorProfileSwitcher.Native;

namespace MonitorProfileSwitcher;

internal enum MonitorAvailability
{
    /// <summary>Plugged in and detected; can be switched on.</summary>
    Connected,
    /// <summary>One of the physical panels behind an active NVIDIA Surround desktop.</summary>
    InSurround,
    /// <summary>Remembered from before, not detected now.</summary>
    NotConnected,
}

/// <summary>One monitor as the arrangement editor shows and edits it. Positions are desktop
/// pixels; Width/Height are unrotated like CCD keeps them.</summary>
internal sealed class ArrangedMonitor
{
    public required string DevicePath { get; init; }
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string EdidName { get; set; } = "";
    public string AdapterName { get; set; } = "";
    public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY Connector { get; set; }
    public MonitorAvailability Availability { get; set; }
    public bool IsSurround { get; init; }
    public DateTime? LastConnected { get; set; }
    public string GdiDeviceName { get; set; } = "";
    public uint SourceId { get; set; }

    public bool Enabled { get; set; }
    public bool Primary { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public MonitorRefreshRate Rate { get; set; } = new();
    public DISPLAYCONFIG_ROTATION Rotation { get; set; } = DISPLAYCONFIG_ROTATION.IDENTITY;
    public MonitorResolution? Preferred { get; set; }
    public List<DisplayModeOption> Modes { get; set; } = [];

    /// <summary>Its usual place, in live coordinates; null if it has none yet.</summary>
    public Point? Home { get; set; }
    /// <summary>Some position is known for it (usual place or where Windows last had it).</summary>
    public bool HasRememberedSpot { get; set; }

    /// <summary>A Surround display staged in this editor (not live yet): the grid to enable.</summary>
    public NvidiaSurroundConfig? StagedGrid { get; set; }
    /// <summary>For a staged Surround: the panels it is made of, left to right.</summary>
    public List<string> SurroundMembers { get; set; } = [];
    /// <summary>A panel folded into a staged Surround; hidden until the combine is undone.</summary>
    public bool MergedIntoSurround { get; set; }
    /// <summary>A panel of the live Surround that a staged split gives back to Windows.</summary>
    public bool ReleasedFromSurround { get; set; }

    public string EdidId => DisplayLayout.EdidIdFromDevicePath(DevicePath);
    public bool IsNvidia => AdapterName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
    public bool CanEnable => Availability == MonitorAvailability.Connected || ReleasedFromSurround;

    /// <summary>Drawn in the layout: on, switched off but connected, or unplugged with a usual
    /// place (as an outline — the MSI and the Odyssey sharing one cable share one spot). An
    /// unplugged monitor never placed by the user waits in the strip to be dragged into place.</summary>
    public bool InLayout => !MergedIntoSurround && (Enabled || CanEnable ||
        (Availability == MonitorAvailability.NotConnected && Home != null));

    /// <summary>On, but not where the user last put it (typically Windows re-placing a monitor
    /// that was just plugged in). A few pixels of drift is not worth offering to fix.</summary>
    public bool AwayFromHome => Enabled && Home is { } home && (Math.Abs(home.X - X) > 16 || Math.Abs(home.Y - Y) > 16);
    public Size DesktopSize => DisplayLayout.DesktopSize(Width, Height, Rotation);
    public Rectangle Bounds => new(new Point(X, Y), DesktopSize);

    public string ConnectorText => Connector switch
    {
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYPORT_EXTERNAL or DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYPORT_EMBEDDED => "DisplayPort",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.HDMI => "HDMI",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DVI => "DVI",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.INDIRECT_WIRED or DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.INDIRECT_VIRTUAL => "USB/indirect",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.INTERNAL => "Internal",
        _ => "",
    };

    public string RateText => !Rate.IsSpecified ? "auto" :
        Rate.IsApproximate || Math.Abs(Rate.ToHz() - Math.Round(Rate.ToHz())) < 0.005
            ? $"{Math.Round(Rate.ToHz()):0} Hz"
            : $"{Rate.ToHz():0.00} Hz";

    /// <summary>Everything except position, for change detection.</summary>
    public string SettingsKey => Enabled
        ? $"on|{Primary}|{Width}x{Height}|{Rate.Numerator}/{Rate.Denominator}|{Rotation}"
        : "off";
}

/// <summary>The editor's working set of monitors plus the rules for keeping it applyable.</summary>
internal static class ArrangementModel
{
    /// <summary>Live state merged with the library: every connected monitor, every remembered
    /// one, numbered in the stable order the library first saw them.</summary>
    public static List<ArrangedMonitor> Build(DisplayInventory inventory, MonitorLibrary library, out Point homeOffset)
    {
        homeOffset = library.HomeOffset(inventory.ActivePositions());
        var offset = homeOffset;
        var live = inventory.Displays.ToDictionary(display => display.DevicePath, StringComparer.OrdinalIgnoreCase);
        var surroundEdid = inventory.Displays.FirstOrDefault(display => display.IsSurround && display.Active != null)?.EdidId;
        var result = new List<ArrangedMonitor>();

        foreach (var known in library.Monitors)
        {
            live.TryGetValue(known.DevicePath, out var display);
            var isSurround = known.FriendlyName.Equals("NV Surround", StringComparison.OrdinalIgnoreCase);
            // A Surround desktop that is not live is a driver mode, not a monitor; profiles
            // switch it, so the editor does not offer it.
            if (isSurround && display == null)
                continue;

            var availability = display != null ? MonitorAvailability.Connected
                : surroundEdid != null && known.EdidId.Equals(surroundEdid, StringComparison.OrdinalIgnoreCase)
                    ? MonitorAvailability.InSurround
                    : MonitorAvailability.NotConnected;

            var monitor = new ArrangedMonitor
            {
                DevicePath = known.DevicePath,
                Name = known.DisplayName,
                EdidName = known.FriendlyName,
                AdapterName = display?.AdapterName ?? known.AdapterName,
                Connector = display?.Connector ?? known.Connector,
                Availability = availability,
                IsSurround = isSurround,
                LastConnected = known.LastConnected,
                Preferred = display?.PreferredResolution ?? known.PreferredResolution,
                Modes = display?.Modes.Count > 0 ? display.Modes : known.Modes,
                Home = known.HomePosition is { } home ? new Point(home.X + offset.X, home.Y + offset.Y) : null,
                HasRememberedSpot = known.HomePosition != null || known.LastPosition != null,
            };

            if (display?.Active is { } active)
            {
                monitor.Enabled = true;
                monitor.Primary = active.IsPrimary;
                monitor.X = active.Position.X;
                monitor.Y = active.Position.Y;
                monitor.Width = active.Resolution.Width;
                monitor.Height = active.Resolution.Height;
                monitor.Rate = active.RefreshRate.Clone();
                monitor.Rotation = active.Rotation;
                monitor.GdiDeviceName = active.GdiDeviceName;
                monitor.SourceId = active.SourceId;
            }
            else
            {
                // Off or unplugged: show it the way it was last used, so switching it back on
                // restores that rather than whatever Windows would invent.
                var resolution = known.LastResolution ?? display?.PreferredResolution ?? known.PreferredResolution;
                monitor.Width = resolution?.Width > 0 ? resolution.Width : 1920;
                monitor.Height = resolution?.Height > 0 ? resolution.Height : 1080;
                monitor.Rate = known.LastRefreshRate?.Clone() ?? display?.PreferredRefreshRate?.Clone() ?? new MonitorRefreshRate();
                monitor.Rotation = known.LastRotation;
                // Its usual place if it has one; else wherever Windows last had it.
                var spot = monitor.Home ?? (known.LastPosition is { } last ? new Point(last.X, last.Y) : Point.Empty);
                monitor.X = spot.X;
                monitor.Y = spot.Y;
            }
            result.Add(monitor);
        }

        for (int i = 0; i < result.Count; i++)
            result[i].Number = i + 1;
        TidyOffMonitors(result);
        return result;
    }

    /// <summary>
    /// Switched-off monitors keep their last spot in the layout, but that spot can be taken
    /// since (by an enabled monitor, or another switched-off one). Move only those that
    /// collide, to the nearest free spot, so every tile stays visible and clickable.
    /// </summary>
    public static void TidyOffMonitors(IList<ArrangedMonitor> monitors)
    {
        var placed = monitors.Where(monitor => monitor.Enabled).Select(monitor => monitor.Bounds).ToList();
        foreach (var off in monitors.Where(monitor => !monitor.Enabled && monitor.CanEnable))
        {
            if (placed.Any(rect => rect.IntersectsWith(off.Bounds)))
            {
                var location = DisplayLayout.PlaceNear(off.DesktopSize, new Point(off.X, off.Y), placed, snap: 0);
                off.X = location.X;
                off.Y = location.Y;
            }
            placed.Add(off.Bounds);
        }
    }

    /// <summary>Where the switched-off and unplugged monitors sit, relative to the main display —
    /// the coordinates Windows will report once the layout is applied (main display at 0,0).</summary>
    public static List<(string DevicePath, int X, int Y)> OffMonitorPositions(IList<ArrangedMonitor> monitors)
    {
        var primary = monitors.FirstOrDefault(monitor => monitor.Enabled && monitor.Primary);
        var originX = primary?.X ?? 0;
        var originY = primary?.Y ?? 0;
        return monitors.Where(monitor => !monitor.Enabled && monitor.InLayout)
            .Select(monitor => (monitor.DevicePath, monitor.X - originX, monitor.Y - originY))
            .ToList();
    }

    /// <summary>Stages putting every enabled monitor back in its usual place. Returns how many moved.</summary>
    public static int RestoreHomes(IList<ArrangedMonitor> monitors)
    {
        var moved = 0;
        foreach (var monitor in monitors.Where(monitor => monitor.AwayFromHome))
        {
            monitor.X = monitor.Home!.Value.X;
            monitor.Y = monitor.Home.Value.Y;
            moved++;
        }
        if (moved > 0)
            MakeContiguous(monitors);
        return moved;
    }

    public static List<ArrangedMonitor> Enabled(IEnumerable<ArrangedMonitor> monitors) =>
        monitors.Where(monitor => monitor.Enabled).ToList();

    /// <summary>Each enabled monitor's settings and its position relative to the primary — so
    /// moving everything together, or Windows renormalising coordinates, is not a "change".</summary>
    public static Dictionary<string, string> Snapshot(IEnumerable<ArrangedMonitor> monitors)
    {
        var list = monitors.ToList();
        var primary = list.FirstOrDefault(monitor => monitor.Enabled && monitor.Primary);
        var originX = primary?.X ?? 0;
        var originY = primary?.Y ?? 0;
        return list.ToDictionary(
            monitor => monitor.DevicePath,
            monitor => monitor.Enabled ? $"{monitor.SettingsKey}|{monitor.X - originX},{monitor.Y - originY}" : monitor.SettingsKey,
            StringComparer.OrdinalIgnoreCase);
    }

    public static int CountChanges(IEnumerable<ArrangedMonitor> monitors, IReadOnlyDictionary<string, string> baseline)
    {
        var current = Snapshot(monitors);
        return current.Count(pair => !baseline.TryGetValue(pair.Key, out var before) ? pair.Value != "off" : before != pair.Value);
    }

    /// <summary>Exactly one primary among the enabled monitors; prefers the biggest screen.</summary>
    public static void EnsurePrimary(IList<ArrangedMonitor> monitors)
    {
        var enabled = Enabled(monitors);
        foreach (var monitor in monitors.Where(monitor => !monitor.Enabled))
            monitor.Primary = false;
        if (enabled.Count == 0)
            return;
        var primary = enabled.FirstOrDefault(monitor => monitor.Primary) ??
            enabled.OrderByDescending(monitor => (long)monitor.Width * monitor.Height).First();
        foreach (var monitor in enabled)
            monitor.Primary = monitor == primary;
    }

    /// <summary>Enabled monitors not edge-connected to the main display's group. Windows needs one
    /// connected desktop, so applying will move these next to the others.</summary>
    public static List<ArrangedMonitor> Floating(IList<ArrangedMonitor> monitors)
    {
        var enabled = Enabled(monitors);
        var primary = enabled.FirstOrDefault(monitor => monitor.Primary);
        if (primary == null || enabled.Count < 2)
            return [];
        var group = new List<ArrangedMonitor> { primary };
        bool grew;
        do
        {
            grew = false;
            foreach (var monitor in enabled.Except(group).ToList())
            {
                if (group.Any(member => DisplayLayout.Touches(member.Bounds, monitor.Bounds)))
                {
                    group.Add(monitor);
                    grew = true;
                }
            }
        } while (grew);
        return enabled.Except(group).ToList();
    }

    /// <summary>Moves enabled monitors as little as possible so none overlap or float free.</summary>
    public static void MakeContiguous(IList<ArrangedMonitor> monitors)
    {
        EnsurePrimary(monitors);
        var enabled = Enabled(monitors);
        if (enabled.Count < 2)
            return;
        var rects = enabled.Select(monitor => monitor.Bounds).ToList();
        DisplayLayout.MakeContiguous(rects, enabled.FindIndex(monitor => monitor.Primary));
        for (int i = 0; i < enabled.Count; i++)
        {
            enabled[i].X = rects[i].X;
            enabled[i].Y = rects[i].Y;
        }
    }

    /// <summary>Puts <paramref name="monitor"/> at the nearest valid spot to where it is (or to
    /// <paramref name="desired"/>), touching the other enabled monitors.</summary>
    public static void Attach(ArrangedMonitor monitor, IEnumerable<ArrangedMonitor> monitors, Point? desired = null, int snap = 0)
    {
        var others = monitors.Where(other => other.Enabled && other != monitor).Select(other => other.Bounds).ToList();
        var location = DisplayLayout.PlaceNear(monitor.DesktopSize, desired ?? new Point(monitor.X, monitor.Y), others, snap);
        monitor.X = location.X;
        monitor.Y = location.Y;
    }

    /// <summary>The editor's enabled monitors as a profile ready for <see cref="ProfileApplyRunner"/>.</summary>
    public static DisplayProfile ToProfile(IList<ArrangedMonitor> monitors, NvidiaSurroundConfig? liveSurround)
    {
        var enabled = Enabled(monitors).Where(monitor => monitor.CanEnable).ToList();
        if (enabled.Count == 0)
            throw new InvalidOperationException("Turn on at least one display.");

        var infos = enabled.Select(monitor => new MonitorInfo
        {
            DevicePath = monitor.DevicePath,
            FriendlyName = monitor.EdidName,
            GdiDeviceName = monitor.GdiDeviceName,
            SourceId = monitor.SourceId,
            IsPrimary = monitor.Primary,
            Position = new MonitorPosition { X = monitor.X, Y = monitor.Y },
            Resolution = new MonitorResolution { Width = monitor.Width, Height = monitor.Height },
            OutputTechnology = monitor.Connector,
            Rotation = monitor.Rotation,
            RefreshRate = monitor.Rate.Clone(),
        }).ToArray();
        DisplayLayout.Repair(infos);

        // Keeping the Surround desktop means keeping its grid (or enabling the one staged here);
        // leaving it out of the layout turns Surround off and gives the panels back to Windows.
        var surround = enabled.FirstOrDefault(monitor => monitor.IsSurround);
        return new DisplayProfile
        {
            Id = "arrange-displays",
            Name = "Arranged layout",
            Monitors = infos,
            NvidiaSurround = surround == null ? null : surround.StagedGrid ?? liveSurround,
        };
    }

    /// <summary>
    /// The row of panels that would form a Surround with <paramref name="monitor"/>: enabled
    /// NVIDIA panels of the same size and rate, side by side with tops aligned, left to right.
    /// Fewer than two means there is no row to combine.
    /// </summary>
    public static List<ArrangedMonitor> SurroundRow(IList<ArrangedMonitor> monitors, ArrangedMonitor monitor)
    {
        if (!monitor.Enabled || !monitor.IsNvidia || monitor.IsSurround)
            return [];
        bool Fits(ArrangedMonitor other) =>
            other.Enabled && other.IsNvidia && !other.IsSurround && other.Y == monitor.Y &&
            other.Width == monitor.Width && other.Height == monitor.Height && other.Rotation == monitor.Rotation &&
            Math.Round(other.Rate.ToHz()) == Math.Round(monitor.Rate.ToHz());

        var row = new List<ArrangedMonitor> { monitor };
        for (var left = monitor; ;)
        {
            var next = monitors.FirstOrDefault(other => other != left && Fits(other) && other.Bounds.Right == left.X);
            if (next == null) break;
            row.Insert(0, next);
            left = next;
        }
        for (var right = monitor; ;)
        {
            var next = monitors.FirstOrDefault(other => other != right && Fits(other) && other.X == right.Bounds.Right);
            if (next == null) break;
            row.Add(next);
            right = next;
        }
        return row;
    }

    /// <summary>The live layout as a profile, to go back to if the user does not keep a change.</summary>
    public static DisplayProfile CaptureCurrent() => new()
    {
        Id = "arrange-displays-previous",
        Name = "Previous layout",
        Monitors = DisplayManager.CaptureCurrentConfig().Monitors,
        NvidiaSurround = NvidiaSurroundManager.TryCaptureActive(),
    };
}
