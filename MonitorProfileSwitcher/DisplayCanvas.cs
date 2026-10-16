using System.Drawing;
using System.Drawing.Drawing2D;

namespace MonitorProfileSwitcher;

internal enum MonitorCommand
{
    Enable,
    Disable,
    MakePrimary,
    Delete,
    CombineSurround,
    SplitSurround,
}

/// <summary>
/// The arrangement view. Enabled monitors are drawn to scale and dragged into place (snapping
/// to neighbours like Windows does). Connected monitors that are switched off stay in the
/// layout where they were, drawn dashed, so they can be switched back on in the same spot.
/// Monitors that cannot be switched on right now (unplugged, or inside an active NVIDIA
/// Surround) sit in the strip underneath. Nothing here touches the real displays.
/// </summary>
internal sealed class DisplayCanvas : Control
{
    private const int StripHeight = 74;
    private const int Pad = 28;
    private const int ChipWidth = 168;
    private const int ChipHeight = 52;
    private const int SnapScreenPixels = 14;

    private static readonly Color Background = Color.FromArgb(30, 30, 30);
    private static readonly Color StripBackground = Color.FromArgb(40, 40, 40);
    private static readonly Color Tile = Color.FromArgb(62, 62, 66);
    private static readonly Color TileSelected = Color.FromArgb(0, 103, 192);
    private static readonly Color Ghost = Color.FromArgb(38, 38, 42);
    private static readonly Color GhostSelected = Color.FromArgb(0, 55, 104);
    private static readonly Color TileBorder = Color.FromArgb(120, 120, 128);
    private static readonly Color Overlap = Color.FromArgb(220, 70, 60);
    private static readonly Color Muted = Color.FromArgb(170, 170, 175);

    private IList<ArrangedMonitor> _monitors = [];
    private ArrangedMonitor? _selected;

    // World (desktop pixels) → screen transform; frozen while dragging so the view holds still.
    private float _scale = 0.1f;
    private PointF _offset;
    private bool _transformFrozen;

    private ArrangedMonitor? _dragging;
    private bool _dragFromStrip; // an unplugged monitor being given its usual place
    private Point _dragScreen;
    private Point _grabOffset;   // world-space offset of the cursor inside the dragged tile
    private Point _dragStartPosition;
    private Point _mouseDownAt;
    private bool _dragStarted;

    public event EventHandler? SelectionChanged;
    /// <summary>Raised after a drop moved an enabled monitor.</summary>
    public event EventHandler? ArrangementChanged;
    /// <summary>Raised after a switched-off monitor was dragged to a new spot (not a change to apply).</summary>
    public event Action<ArrangedMonitor>? OffMonitorMoved;
    /// <summary>Right-click menu choices and drag-to-strip; the form applies the rules.</summary>
    public event Action<ArrangedMonitor, MonitorCommand>? CommandRequested;

    public DisplayCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        BackColor = Background;
    }

    public ArrangedMonitor? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetMonitors(IList<ArrangedMonitor> monitors)
    {
        _monitors = monitors;
        if (_selected != null)
            _selected = monitors.FirstOrDefault(monitor => monitor.DevicePath == _selected.DevicePath);
        _selected ??= monitors.FirstOrDefault(monitor => monitor.Enabled && monitor.Primary) ?? monitors.FirstOrDefault();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private Rectangle LayoutArea => new(0, 0, Width, Math.Max(0, Height - StripHeight));
    private Rectangle StripArea => new(0, Math.Max(0, Height - StripHeight), Width, StripHeight);

    /// <summary>Everything drawn in the layout: enabled, switched-off, and unplugged-with-a-spot.</summary>
    private List<ArrangedMonitor> InLayout => _monitors.Where(monitor => monitor.InLayout).ToList();
    private List<ArrangedMonitor> EnabledMonitors => _monitors.Where(monitor => monitor.Enabled).ToList();
    /// <summary>Monitors with no place in the layout: Surround panels, and unplugged ones never placed.</summary>
    private List<ArrangedMonitor> InStrip => _monitors.Where(monitor => !monitor.InLayout && !monitor.MergedIntoSurround).ToList();

    private void UpdateTransform()
    {
        if (_transformFrozen) return;
        var placed = InLayout;
        var area = LayoutArea;
        if (placed.Count == 0 || area.Width < 2 * Pad || area.Height < 2 * Pad)
        {
            _scale = 0.1f;
            _offset = new PointF(area.Width / 2f, area.Height / 2f);
            return;
        }
        var world = placed.Select(monitor => monitor.Bounds).Aggregate(Rectangle.Union);
        _scale = Math.Min(0.2f, Math.Min((area.Width - 2f * Pad) / world.Width, (area.Height - 2f * Pad) / world.Height));
        _offset = new PointF(
            area.Left + (area.Width - world.Width * _scale) / 2f - world.Left * _scale,
            area.Top + (area.Height - world.Height * _scale) / 2f - world.Top * _scale);
    }

    private RectangleF ToScreen(Rectangle world) =>
        new(_offset.X + world.X * _scale, _offset.Y + world.Y * _scale, world.Width * _scale, world.Height * _scale);

    private Point ToWorld(Point screen) =>
        new((int)Math.Round((screen.X - _offset.X) / _scale), (int)Math.Round((screen.Y - _offset.Y) / _scale));

    private Rectangle ChipRect(int index)
    {
        var strip = StripArea;
        // Shrink chips to fit when many monitors are remembered, rather than running off the edge.
        var count = Math.Max(1, InStrip.Count);
        var width = Math.Clamp((strip.Width - 20) / count - 8, 70, ChipWidth);
        return new Rectangle(strip.Left + 10 + index * (width + 8), strip.Top + 16, width, ChipHeight);
    }

    /// <summary>Draw order, bottom to top: unplugged outlines, switched-off monitors, enabled ones, the selection.</summary>
    private IEnumerable<ArrangedMonitor> PaintOrder() =>
        InLayout.OrderBy(monitor => monitor.Enabled ? 2 : monitor.CanEnable ? 1 : 0).ThenBy(monitor => monitor == _selected ? 1 : 0);

    private ArrangedMonitor? HitTest(Point point)
    {
        if (StripArea.Contains(point))
        {
            var strip = InStrip;
            for (int i = 0; i < strip.Count; i++)
                if (ChipRect(i).Contains(point)) return strip[i];
            return null;
        }
        foreach (var monitor in PaintOrder().Reverse())
            if (ToScreen(monitor.Bounds).Contains(point)) return monitor;
        return null;
    }

    // ---- painting ---------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        UpdateTransform();

        if (InLayout.Count == 0)
        {
            TextRenderer.DrawText(g, "No connected displays.", Font,
                LayoutArea, Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        var enabled = EnabledMonitors;
        foreach (var monitor in PaintOrder())
        {
            var overlaps = monitor.Enabled && enabled.Any(other => other != monitor && other.Bounds.IntersectsWith(monitor.Bounds));
            DrawTile(g, monitor, ToScreen(monitor.Bounds), overlaps);
        }

        DrawStrip(g);

        if (_dragging != null && _dragFromStrip && _dragStarted)
        {
            var ghost = new Rectangle(_dragScreen.X - ChipWidth / 2, _dragScreen.Y - ChipHeight / 2, ChipWidth, ChipHeight);
            DrawChip(g, _dragging, ghost);
        }
    }

    private void DrawTile(Graphics g, ArrangedMonitor monitor, RectangleF rect, bool overlaps)
    {
        var selected = monitor == _selected;
        var on = monitor.Enabled;
        var unplugged = !on && !monitor.CanEnable;
        // Unplugged monitors are only an outline: their spot may be shared with the monitor that
        // replaced them on the same cable, which then covers them.
        if (!unplugged || selected)
        {
            using var fill = new SolidBrush(on ? (selected ? TileSelected : Tile) : (selected ? GhostSelected : Ghost));
            g.FillRectangle(fill, rect);
        }
        using (var border = new Pen(overlaps ? Overlap : selected ? Color.White : unplugged ? Color.FromArgb(105, 105, 112) : TileBorder,
                   overlaps || selected ? 2f : 1f)
               { DashStyle = on ? DashStyle.Solid : unplugged ? DashStyle.Dot : DashStyle.Dash })
            g.DrawRectangle(border, rect.X, rect.Y, rect.Width, rect.Height);

        var bounds = Rectangle.Round(rect);
        bounds.Inflate(-6, -4);
        if (bounds.Width < 20 || bounds.Height < 16)
            return;

        // Big number, Windows-style, plus as many detail lines as the tile has room for.
        var text = on ? Color.White : Muted;
        var detail = on ? Color.FromArgb(215, 215, 220) : Color.FromArgb(140, 140, 146);
        using var numberFont = new Font(Font.FontFamily, Math.Clamp(rect.Height / 4.5f, 9f, 26f), FontStyle.Bold);
        using var nameFont = new Font(Font.FontFamily, 9f, FontStyle.Bold);
        var lines = new List<(string Text, Font Font, Color Color)>
        {
            (monitor.Number.ToString(), numberFont, text),
            (monitor.Name, nameFont, text),
        };
        // State lines first: on a short tile they matter more than the details below them.
        var surroundGreen = Color.FromArgb(118, 185, 0);
        if (unplugged)
            lines.Add(("Not connected — keeps this spot", Font, Muted));
        else if (!on)
            lines.Add(("Off — right-click to enable", Font, Color.FromArgb(255, 214, 102)));
        if (monitor.StagedGrid != null)
            lines.Add(($"New NVIDIA Surround 1x{monitor.StagedGrid.Columns} — turns on when you Apply", Font, surroundGreen));
        else if (monitor.IsSurround)
            lines.Add(("NVIDIA Surround", Font, surroundGreen));
        if (monitor.ReleasedFromSurround && on)
            lines.Add(("Leaves Surround when you Apply", Font, surroundGreen));
        lines.Add((DetectedLine(monitor), Font, detail));
        lines.Add(($"{monitor.DesktopSize.Width}×{monitor.DesktopSize.Height} · {monitor.RateText}", Font, detail));
        if (monitor.Primary)
            lines.Add(("★ Main display", Font, Color.FromArgb(255, 214, 102)));

        var y = bounds.Top;
        foreach (var (line, font, color) in lines)
        {
            var size = TextRenderer.MeasureText(g, line, font, new Size(bounds.Width, int.MaxValue),
                TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (y + size.Height > bounds.Bottom) break;
            TextRenderer.DrawText(g, line, font, new Rectangle(bounds.Left, y, bounds.Width, size.Height), color,
                TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter);
            y += size.Height;
        }
    }

    /// <summary>"MSI3FA8 · RTX 5080 · HDMI": what the monitor reports itself as and where it is plugged in.</summary>
    public static string DetectedLine(ArrangedMonitor monitor) =>
        string.Join(" · ", new[] { monitor.EdidId, ShortAdapter(monitor.AdapterName), monitor.ConnectorText }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    public static string ShortAdapter(string adapter) => adapter
        .Replace("NVIDIA GeForce ", "", StringComparison.OrdinalIgnoreCase)
        .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
        .Replace("Intel UHD Graphics", "Intel UHD", StringComparison.OrdinalIgnoreCase)
        .Replace(" USB Device", "", StringComparison.OrdinalIgnoreCase)
        .Trim();

    private void DrawStrip(Graphics g)
    {
        var strip = StripArea;
        using (var fill = new SolidBrush(StripBackground))
            g.FillRectangle(fill, strip);
        var dropTarget = _dragging is { Enabled: true } && _dragStarted;
        using (var line = new Pen(dropTarget ? TileSelected : Color.FromArgb(70, 70, 70), dropTarget ? 2f : 1f))
            g.DrawLine(line, strip.Left, strip.Top, strip.Right, strip.Top);

        var chips = InStrip;
        var caption = dropTarget ? "Drop here to switch this display off (it keeps its place in the layout)"
            : chips.Count == 0 ? "Unavailable: (none)"
            : "Not placed — drag an unplugged monitor onto the layout to set its usual spot (Surround panels belong to the grid)";
        TextRenderer.DrawText(g, caption, Font, new Point(strip.Left + 8, strip.Top + 1), Muted);
        for (int i = 0; i < chips.Count; i++)
            DrawChip(g, chips[i], ChipRect(i));
    }

    private void DrawChip(Graphics g, ArrangedMonitor monitor, Rectangle rect)
    {
        var selected = monitor == _selected;
        using (var fill = new SolidBrush(selected ? GhostSelected : Color.FromArgb(52, 52, 56)))
            g.FillRectangle(fill, rect);
        using (var border = new Pen(selected ? Color.White : TileBorder) { DashStyle = DashStyle.Dash })
            g.DrawRectangle(border, rect);

        var status = monitor.Availability == MonitorAvailability.InSurround ? "In NVIDIA Surround" : "Not connected";
        var detail = string.Join(" · ", new[] { status, DetectedLine(monitor) }.Where(part => part.Length > 0));
        var inner = Rectangle.Inflate(rect, -6, -3);
        using var nameFont = new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(g, $"{monitor.Number}  {monitor.Name}", nameFont, new Rectangle(inner.Left, inner.Top, inner.Width, 18),
            Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(g, detail, Font, new Rectangle(inner.Left, inner.Top + 18, inner.Width, inner.Height - 18),
            Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak);
    }

    // ---- interaction ------------------------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var hit = HitTest(e.Location);
        Selected = hit;
        if (e.Button != MouseButtons.Left || hit == null) return;
        // Everything in the layout can be dragged; from the strip, only unplugged monitors
        // (to give them a usual place) — Surround panels belong to the grid.
        _dragFromStrip = !hit.InLayout;
        if (_dragFromStrip && hit.Availability != MonitorAvailability.NotConnected) return;

        _mouseDownAt = e.Location;
        _dragStarted = false;
        _dragging = hit;
        _dragStartPosition = new Point(hit.X, hit.Y);
        _transformFrozen = true;
        var world = ToWorld(e.Location);
        _grabOffset = new Point(world.X - hit.X, world.Y - hit.Y);
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging == null) return;
        if (!_dragStarted && Math.Abs(e.X - _mouseDownAt.X) + Math.Abs(e.Y - _mouseDownAt.Y) < 4) return;
        _dragStarted = true;
        _dragScreen = e.Location;

        if (!_dragFromStrip && LayoutArea.Contains(e.Location))
        {
            var world = ToWorld(e.Location);
            var desired = new Point(world.X - _grabOffset.X, world.Y - _grabOffset.Y);
            var snapped = SnapToEdges(_dragging, desired, (int)Math.Ceiling(SnapScreenPixels / _scale));
            _dragging.X = snapped.X;
            _dragging.Y = snapped.Y;
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            if (HitTest(e.Location) is { } target)
                ShowMenu(target, e.Location);
            return;
        }

        var dragged = _dragging;
        var started = _dragStarted;
        _dragging = null;
        _dragStarted = false;
        Capture = false;
        _transformFrozen = false;
        if (dragged == null || !started)
        {
            Invalidate();
            return;
        }

        var snap = (int)Math.Ceiling(SnapScreenPixels / _scale);
        if (_dragFromStrip)
        {
            // An unplugged monitor dropped on the layout: that is its usual place from now on.
            if (LayoutArea.Contains(e.Location))
            {
                var world = ToWorld(e.Location);
                var size = dragged.DesktopSize;
                var desired = SnapToEdges(dragged, new Point(world.X - size.Width / 2, world.Y - size.Height / 2), snap);
                dragged.X = desired.X;
                dragged.Y = desired.Y;
                OffMonitorMoved?.Invoke(dragged);
            }
        }
        else if (StripArea.Contains(e.Location))
        {
            // Dropped on the strip: put it back where it was; an enabled one is switched off there.
            dragged.X = _dragStartPosition.X;
            dragged.Y = _dragStartPosition.Y;
            if (dragged.Enabled)
                CommandRequested?.Invoke(dragged, MonitorCommand.Disable);
        }
        else if (dragged.Enabled)
        {
            // Land on the nearest spot that touches an enabled neighbour without overlapping.
            ArrangementModel.Attach(dragged, _monitors, null, snap);
            ArrangementChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (dragged.CanEnable)
        {
            // A switched-off monitor only needs a tidy spot for when it is switched back on.
            var others = InLayout.Where(other => other != dragged && (other.Enabled || other.CanEnable)).Select(other => other.Bounds).ToList();
            var location = DisplayLayout.PlaceNear(dragged.DesktopSize, new Point(dragged.X, dragged.Y), others, snap);
            dragged.X = location.X;
            dragged.Y = location.Y;
            OffMonitorMoved?.Invoke(dragged);
        }
        else
        {
            // Unplugged: wherever it is dropped (sharing a spot with its stand-in is the point).
            OffMonitorMoved?.Invoke(dragged);
        }
        Invalidate();
    }

    private void ShowMenu(ArrangedMonitor monitor, Point location)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripLabel($"{monitor.Number}  {monitor.Name}") { Font = new Font(Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());

        void Add(string text, MonitorCommand command, bool enabled = true, bool bold = false)
        {
            var item = new ToolStripMenuItem(text) { Enabled = enabled };
            if (bold) item.Font = new Font(item.Font, FontStyle.Bold);
            item.Click += (_, _) => CommandRequested?.Invoke(monitor, command);
            menu.Items.Add(item);
        }

        switch (monitor.Availability)
        {
            case MonitorAvailability.Connected when monitor.Enabled:
                Add("Disable monitor", MonitorCommand.Disable);
                Add("Make main display", MonitorCommand.MakePrimary, enabled: !monitor.Primary);
                if (monitor.IsSurround)
                {
                    menu.Items.Add(new ToolStripSeparator());
                    Add(monitor.StagedGrid != null ? "Undo combine (back to separate panels)" : "Split NVIDIA Surround into separate displays",
                        MonitorCommand.SplitSurround);
                }
                else if (ArrangementModel.SurroundRow(_monitors, monitor) is { Count: >= 2 } row)
                {
                    menu.Items.Add(new ToolStripSeparator());
                    Add($"Combine row into NVIDIA Surround ({row.Count} panels)", MonitorCommand.CombineSurround);
                }
                break;
            case MonitorAvailability.Connected:
                Add("Enable monitor", MonitorCommand.Enable, bold: true);
                break;
            case MonitorAvailability.InSurround:
                menu.Items.Add(new ToolStripMenuItem("Part of the active NVIDIA Surround display") { Enabled = false });
                break;
            default:
                menu.Items.Add(new ToolStripMenuItem("Not connected — plug it in to enable it") { Enabled = false });
                break;
        }
        menu.Items.Add(new ToolStripSeparator());
        Add("Delete monitor...", MonitorCommand.Delete);
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, location);
    }

    /// <summary>While dragging, pull edges onto nearby neighbour edges (and line up tops,
    /// bottoms, lefts and rights) within <paramref name="snap"/> desktop pixels.</summary>
    private Point SnapToEdges(ArrangedMonitor moving, Point desired, int snap)
    {
        var size = moving.DesktopSize;
        int bestX = desired.X, bestY = desired.Y, dx = snap + 1, dy = snap + 1;
        foreach (var other in InLayout.Where(other => other != moving).Select(other => other.Bounds))
        {
            foreach (var x in new[] { other.Right, other.Left - size.Width, other.Left, other.Right - size.Width })
            {
                var distance = Math.Abs(desired.X - x);
                if (distance < dx) { dx = distance; bestX = x; }
            }
            foreach (var y in new[] { other.Bottom, other.Top - size.Height, other.Top, other.Bottom - size.Height })
            {
                var distance = Math.Abs(desired.Y - y);
                if (distance < dy) { dy = distance; bestY = y; }
            }
        }
        return new Point(dx <= snap ? bestX : desired.X, dy <= snap ? bestY : desired.Y);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Arrow keys nudge the selected display by 10 px (1 px with Shift) for fine alignment.
        if (_selected is not { Enabled: true } || e.KeyCode is not (Keys.Left or Keys.Right or Keys.Up or Keys.Down))
            return;
        var step = e.Shift ? 1 : 10;
        switch (e.KeyCode)
        {
            case Keys.Left: _selected.X -= step; break;
            case Keys.Right: _selected.X += step; break;
            case Keys.Up: _selected.Y -= step; break;
            case Keys.Down: _selected.Y += step; break;
        }
        ArrangementModel.Attach(_selected, _monitors);
        e.Handled = true;
        Invalidate();
        ArrangementChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);
}
