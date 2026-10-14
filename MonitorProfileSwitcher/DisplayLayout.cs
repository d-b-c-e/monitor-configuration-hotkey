using System.Drawing;
using MonitorProfileSwitcher.Native;

namespace MonitorProfileSwitcher;

/// <summary>
/// Desktop-geometry rules shared by the arrangement editor and by profile apply. Windows wants
/// every active display to share an edge with the rest (no islands, no overlaps) and the
/// primary display at (0,0); these helpers produce layouts that satisfy that.
/// </summary>
internal static class DisplayLayout
{
    public static string EdidIdFromDevicePath(string devicePath)
    {
        // \\?\DISPLAY#MSI3FA8#5&5ee4a48&0&UID4353#{e6f07b5f-...}
        var parts = devicePath.Split('#');
        return parts.Length > 1 ? parts[1] : "";
    }

    public static bool IsPortrait(DISPLAYCONFIG_ROTATION rotation) =>
        rotation is DISPLAYCONFIG_ROTATION.ROTATE90 or DISPLAYCONFIG_ROTATION.ROTATE270;

    /// <summary>CCD keeps the source mode unrotated, so a portrait panel's desktop area is the
    /// saved resolution with width and height swapped.</summary>
    public static Size DesktopSize(uint width, uint height, DISPLAYCONFIG_ROTATION rotation) =>
        IsPortrait(rotation) ? new Size((int)height, (int)width) : new Size((int)width, (int)height);

    public static Rectangle Bounds(MonitorInfo monitor) =>
        new(new Point(monitor.Position.X, monitor.Position.Y),
            DesktopSize(monitor.Resolution.Width, monitor.Resolution.Height, monitor.Rotation));

    /// <summary>True when the two rectangles share a stretch of edge (corners alone do not count).</summary>
    public static bool Touches(Rectangle a, Rectangle b)
    {
        bool sideBySide = (a.Right == b.Left || b.Right == a.Left) && a.Top < b.Bottom && b.Top < a.Bottom;
        bool stacked = (a.Bottom == b.Top || b.Bottom == a.Top) && a.Left < b.Right && b.Left < a.Right;
        return sideBySide || stacked;
    }

    private static double Distance(Point a, Point b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    /// <summary>
    /// The position nearest to <paramref name="desired"/> where a display of <paramref name="size"/>
    /// shares an edge with one of <paramref name="placed"/> without overlapping any of them.
    /// Along the shared edge, it snaps to aligned tops/bottoms/lefts/rights/centres within
    /// <paramref name="snap"/> pixels, the way Windows' own arrangement view does.
    /// </summary>
    public static Point PlaceNear(Size size, Point desired, IReadOnlyList<Rectangle> placed, int snap)
    {
        if (placed.Count == 0)
            return desired;

        Point? best = null;
        double bestDistance = double.MaxValue;
        foreach (var anchor in placed)
        {
            foreach (var candidate in EdgeCandidates(size, desired, anchor, snap))
            {
                var rect = new Rectangle(candidate, size);
                if (placed.Any(other => other.IntersectsWith(rect)))
                    continue;
                var distance = Distance(candidate, desired);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
        }

        if (best is { } found)
            return found;

        // Every edge was blocked (only possible with odd overlapping input): park it to the
        // right of everything, top-aligned.
        var right = placed.Max(rect => rect.Right);
        var top = placed.OrderByDescending(rect => rect.Right).First().Top;
        return new Point(right, top);
    }

    private static IEnumerable<Point> EdgeCandidates(Size size, Point desired, Rectangle anchor, int snap)
    {
        // Along a vertical shared edge the free axis is Y; along a horizontal one it is X.
        // Keep at least one pixel of overlap so the edges genuinely touch.
        int SlideY() => Snap(Math.Clamp(desired.Y, anchor.Top - size.Height + 1, anchor.Bottom - 1),
            [anchor.Top, anchor.Bottom - size.Height, anchor.Top + (anchor.Height - size.Height) / 2], snap);
        int SlideX() => Snap(Math.Clamp(desired.X, anchor.Left - size.Width + 1, anchor.Right - 1),
            [anchor.Left, anchor.Right - size.Width, anchor.Left + (anchor.Width - size.Width) / 2], snap);

        yield return new Point(anchor.Right, SlideY());
        yield return new Point(anchor.Left - size.Width, SlideY());
        yield return new Point(SlideX(), anchor.Bottom);
        yield return new Point(SlideX(), anchor.Top - size.Height);
    }

    private static int Snap(int value, int[] targets, int snap)
    {
        foreach (var target in targets)
        {
            if (Math.Abs(value - target) <= snap)
                return target;
        }
        return value;
    }

    /// <summary>
    /// Moves displays as little as possible so that all of them form one edge-connected group
    /// around <paramref name="anchorIndex"/>, with no overlaps. Displays already attached stay put.
    /// </summary>
    public static void MakeContiguous(IList<Rectangle> rects, int anchorIndex)
    {
        if (rects.Count <= 1)
            return;

        var placed = new List<int> { anchorIndex };
        var pending = Enumerable.Range(0, rects.Count).Where(index => index != anchorIndex).ToList();
        while (pending.Count > 0)
        {
            // First keep every display that already touches the group without overlapping it.
            bool attachedAny;
            do
            {
                attachedAny = false;
                foreach (var index in pending.ToList())
                {
                    var rect = rects[index];
                    if (placed.Any(p => rects[p].IntersectsWith(rect)) || !placed.Any(p => Touches(rects[p], rect)))
                        continue;
                    placed.Add(index);
                    pending.Remove(index);
                    attachedAny = true;
                }
            } while (attachedAny);

            if (pending.Count == 0)
                break;

            // Then pull in the nearest stray display, so a gap closes from the inside out.
            var groupBounds = placed.Select(p => rects[p]).Aggregate(Rectangle.Union);
            var groupCentre = new Point(groupBounds.Left + groupBounds.Width / 2, groupBounds.Top + groupBounds.Height / 2);
            var nearest = pending.OrderBy(index => Distance(Centre(rects[index]), groupCentre)).First();
            var stray = rects[nearest];
            var location = PlaceNear(stray.Size, stray.Location, placed.Select(p => rects[p]).ToList(), snap: 0);
            rects[nearest] = new Rectangle(location, stray.Size);
            placed.Add(nearest);
            pending.Remove(nearest);
        }
    }

    private static Point Centre(Rectangle rect) => new(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);

    /// <summary>
    /// Makes a monitor list valid to apply: exactly one primary, every display attached, and
    /// the primary at (0,0). When the saved primary is gone, the display nearest the old origin
    /// takes over — it is where the user's attention was.
    /// </summary>
    public static void Repair(IList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0)
            return;

        var primaryIndex = IndexOfPrimary(monitors);
        for (int i = 0; i < monitors.Count; i++)
            monitors[i].IsPrimary = i == primaryIndex;

        var rects = monitors.Select(Bounds).ToList();
        MakeContiguous(rects, primaryIndex);
        var origin = rects[primaryIndex].Location;
        for (int i = 0; i < monitors.Count; i++)
        {
            monitors[i].Position.X = rects[i].X - origin.X;
            monitors[i].Position.Y = rects[i].Y - origin.Y;
        }
    }

    private static int IndexOfPrimary(IList<MonitorInfo> monitors)
    {
        for (int i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].IsPrimary)
                return i;
        }

        var best = 0;
        var bestDistance = double.MaxValue;
        for (int i = 0; i < monitors.Count; i++)
        {
            var distance = Distance(Centre(Bounds(monitors[i])), Point.Empty);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// The part of <paramref name="profile"/> that can be applied with the monitors connected
    /// now. Unplugged monitors are skipped and the rest re-laid out (primary, gaps) instead of
    /// failing the whole switch — swapping one monitor for another must not strand a profile.
    /// The Surround desktop counts as present: it only exists once the grid is enabled.
    /// </summary>
    public static DisplayProfile WithoutMissingMonitors(DisplayProfile profile, ISet<string> connectedDevicePaths,
        out IReadOnlyList<MonitorInfo> skipped)
    {
        var surround = profile.SurroundMonitor;
        var kept = profile.Monitors
            .Where(monitor => monitor == surround || connectedDevicePaths.Contains(monitor.DevicePath))
            .ToList();
        skipped = profile.Monitors.Where(monitor => !kept.Contains(monitor)).ToList();
        if (kept.Count == 0)
        {
            // Nothing to fall back to: let the apply report the profile's monitors as missing.
            skipped = [];
            return profile;
        }
        if (skipped.Count == 0)
            return profile;

        var monitors = kept.Select(monitor => monitor.Clone()).ToArray();
        Repair(monitors);
        return profile.WithMonitors(monitors);
    }
}
