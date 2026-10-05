namespace MonitorProfileSwitcher;

/// <summary>Builds an IPC-friendly view of the live displays and verifies an applied profile.</summary>
internal sealed class IpcDisplayInspector
{
    private readonly MonitorInfo[] _monitors;
    private readonly NvidiaSurroundConfig? _surround;

    public NvidiaSurroundConfig? ActiveSurround => _surround;

    private IpcDisplayInspector(MonitorInfo[] monitors, NvidiaSurroundConfig? surround)
    {
        _monitors = monitors;
        _surround = surround;
    }

    public static IpcDisplayInspector Capture()
    {
        var monitors = DisplayManager.CaptureCurrentConfig().Monitors;
        var surround = NvidiaSurroundManager.TryCaptureActive();
        return new IpcDisplayInspector(monitors, surround);
    }

    public IpcDisplayStatus ToStatus(IReadOnlyList<DisplayProfile> profiles)
    {
        long left = 0, top = 0, right = 0, bottom = 0;
        if (_monitors.Length > 0)
        {
            left = _monitors.Min(monitor => (long)monitor.Position.X);
            top = _monitors.Min(monitor => (long)monitor.Position.Y);
            right = _monitors.Max(monitor => (long)monitor.Position.X + monitor.Resolution.Width);
            bottom = _monitors.Max(monitor => (long)monitor.Position.Y + monitor.Resolution.Height);
        }

        var matchingProfiles = profiles.Where(Matches).Take(2).ToArray();
        return new IpcDisplayStatus
        {
            Topology = _surround is not null ? "nvidia-surround" : _monitors.Length > 1 ? "independent" : "single",
            MonitorCount = _monitors.Length,
            DesktopWidth = (uint)Math.Max(0, right - left),
            DesktopHeight = (uint)Math.Max(0, bottom - top),
            ActiveProfileName = matchingProfiles.Length == 1 ? matchingProfiles[0].Name : null,
            SurroundRows = _surround?.Rows,
            SurroundColumns = _surround?.Columns,
            Monitors = _monitors.Select(monitor => new IpcMonitor
            {
                Name = monitor.FriendlyName,
                Width = monitor.Resolution.Width,
                Height = monitor.Resolution.Height,
                X = monitor.Position.X,
                Y = monitor.Position.Y,
                Primary = monitor.IsPrimary,
            }).ToArray(),
        };
    }

    public bool Matches(DisplayProfile profile)
    {
        if (profile.IsNvidiaSurround)
        {
            var saved = profile.NvidiaSurround;
            if (_surround is null || saved is null)
                return false;
            if (_surround.Rows != saved.Rows || _surround.Columns != saved.Columns ||
                _surround.WidthPerDisplay != saved.WidthPerDisplay ||
                _surround.HeightPerDisplay != saved.HeightPerDisplay ||
                !_surround.Displays.Select(display => display.DisplayId)
                    .SequenceEqual(saved.Displays.Select(display => display.DisplayId)))
                return false;
        }
        else if (_surround is not null)
        {
            return false;
        }

        if (_monitors.Length != profile.Monitors.Length)
            return false;

        var currentByPath = _monitors.ToDictionary(monitor => monitor.DevicePath, StringComparer.OrdinalIgnoreCase);
        foreach (var saved in profile.Monitors)
        {
            if (!currentByPath.TryGetValue(saved.DevicePath, out var current))
                return false;
            if (saved.IsPrimary != current.IsPrimary ||
                saved.Position.X != current.Position.X || saved.Position.Y != current.Position.Y ||
                saved.Resolution.Width != current.Resolution.Width ||
                saved.Resolution.Height != current.Resolution.Height ||
                saved.Rotation != current.Rotation ||
                (saved.RefreshRate.IsSpecified && !saved.RefreshRate.Matches(current.RefreshRate.ToRational())))
                return false;
        }

        return true;
    }
}
