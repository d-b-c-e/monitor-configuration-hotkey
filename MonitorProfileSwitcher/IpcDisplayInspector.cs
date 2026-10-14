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

    public bool Matches(DisplayProfile profile) => Mismatch(profile) == null;

    /// <summary>Null when the live layout matches <paramref name="profile"/>; otherwise the first
    /// difference, in words, so a failed verification says what was actually wrong.</summary>
    public string? Mismatch(DisplayProfile profile)
    {
        if (profile.IsNvidiaSurround)
        {
            var saved = profile.NvidiaSurround;
            if (_surround is null || saved is null)
                return _surround is null ? "NVIDIA Surround is not active" : "the profile has no Surround grid saved";
            if (_surround.Rows != saved.Rows || _surround.Columns != saved.Columns ||
                _surround.WidthPerDisplay != saved.WidthPerDisplay ||
                _surround.HeightPerDisplay != saved.HeightPerDisplay ||
                !_surround.Displays.Select(display => display.DisplayId)
                    .SequenceEqual(saved.Displays.Select(display => display.DisplayId)))
                return $"the active Surround grid is {_surround.Rows}x{_surround.Columns}, not the saved {saved.Rows}x{saved.Columns} grid";
        }
        else if (_surround is not null)
        {
            return "NVIDIA Surround is still active";
        }

        var currentByPath = _monitors.ToDictionary(monitor => monitor.DevicePath, StringComparer.OrdinalIgnoreCase);
        foreach (var saved in profile.Monitors)
        {
            var name = Describe(saved);
            if (!currentByPath.TryGetValue(saved.DevicePath, out var current))
                return $"{name} is not on";
            if (saved.IsPrimary != current.IsPrimary)
                return $"{name} is {(current.IsPrimary ? "" : "not ")}the main display";
            if (saved.Position.X != current.Position.X || saved.Position.Y != current.Position.Y)
                return $"{name} is at ({current.Position.X},{current.Position.Y}), not ({saved.Position.X},{saved.Position.Y})";
            if (saved.Resolution.Width != current.Resolution.Width || saved.Resolution.Height != current.Resolution.Height)
                return $"{name} is {current.Resolution.Width}x{current.Resolution.Height}, not {saved.Resolution.Width}x{saved.Resolution.Height}";
            if (saved.Rotation != current.Rotation)
                return $"{name} is rotated {current.Rotation}, not {saved.Rotation}";
            if (saved.RefreshRate.IsSpecified && !saved.RefreshRate.Matches(current.RefreshRate.ToRational()))
                return $"{name} is at {current.RefreshRate}, not {saved.RefreshRate}";
        }

        var wanted = new HashSet<string>(profile.Monitors.Select(monitor => monitor.DevicePath), StringComparer.OrdinalIgnoreCase);
        var extra = _monitors.Where(monitor => !wanted.Contains(monitor.DevicePath)).ToList();
        return extra.Count > 0 ? $"{string.Join(", ", extra.Select(Describe))} should be off" : null;
    }

    private static string Describe(MonitorInfo monitor) =>
        monitor.FriendlyName.Length > 0 ? monitor.FriendlyName : monitor.EdidId;
}
