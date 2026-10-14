using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using MonitorProfileSwitcher.Native;

namespace MonitorProfileSwitcher;

/// <summary>
/// Every monitor the app has ever seen, connected or not, so the arrangement editor can show a
/// monitor that is unplugged right now (and where it used to go) instead of forgetting it.
/// Keyed by CCD device path, which encodes both the monitor (EDID) and the port it is on.
/// </summary>
internal sealed class KnownMonitor
{
    public string DevicePath { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    /// <summary>The user's own name for it; EDID names can be blank or generic.</summary>
    public string? Nickname { get; set; }
    public string AdapterName { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY Connector { get; set; }

    public DateTime? LastConnected { get; set; }
    /// <summary>Where Windows last had it — overwritten whenever it is seen active.</summary>
    public MonitorPosition? LastPosition { get; set; }
    /// <summary>Its usual place, set only by the user (applying a layout or profile, or dragging a
    /// switched-off/unplugged monitor), never by Windows' own rearranging. Stored in the coordinates
    /// of the last layout recorded; <see cref="MonitorLibrary.HomeOffset"/> maps it to live ones.</summary>
    public MonitorPosition? HomePosition { get; set; }
    public MonitorResolution? LastResolution { get; set; }
    public MonitorRefreshRate? LastRefreshRate { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DISPLAYCONFIG_ROTATION LastRotation { get; set; } = DISPLAYCONFIG_ROTATION.IDENTITY;

    public MonitorResolution? PreferredResolution { get; set; }
    public List<DisplayModeOption> Modes { get; set; } = [];

    /// <summary>Exact refresh rationals seen per resolution. GDI lists whole hertz only, but
    /// profile verification compares the exact rational (e.g. 59951/1000), so remember them.</summary>
    public List<ExactRefreshRate> ExactRates { get; set; } = [];

    [JsonIgnore]
    public string EdidId => DisplayLayout.EdidIdFromDevicePath(DevicePath);

    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Nickname) ? Nickname! :
        !string.IsNullOrWhiteSpace(FriendlyName) ? FriendlyName :
        EdidId.Length > 0 ? EdidId : "Unknown monitor";
}

internal sealed class ExactRefreshRate
{
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint Numerator { get; set; }
    public uint Denominator { get; set; }
}

internal sealed class MonitorLibraryStore
{
    /// <summary>Show the "keep these settings?" prompt after applying from the editor.</summary>
    public bool ConfirmAfterApply { get; set; } = true;
    public List<KnownMonitor> Monitors { get; set; } = [];
}

internal sealed class MonitorLibrary
{
    private static readonly string StorePath = Path.Combine(ProfileManager.StorageDir, "monitors.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private MonitorLibraryStore _store = new();

    public IReadOnlyList<KnownMonitor> Monitors => _store.Monitors;

    public bool ConfirmAfterApply
    {
        get => _store.ConfirmAfterApply;
        set { _store.ConfirmAfterApply = value; TrySave(); }
    }

    /// <summary>Loads the library and adds any monitor named in a profile that it has not
    /// seen yet — a profile is proof the monitor exists even if it is unplugged today.</summary>
    public static MonitorLibrary Load(IEnumerable<DisplayProfile> profiles)
    {
        var library = new MonitorLibrary();
        try
        {
            if (File.Exists(StorePath))
                library._store = JsonSerializer.Deserialize<MonitorLibraryStore>(File.ReadAllText(StorePath), JsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            // A corrupt library only costs remembered names and positions; never block the editor.
            AppLog.Warn($"Could not read {StorePath}; starting a new monitor library: {ex.Message}");
            library._store = new MonitorLibraryStore();
        }

        var added = 0;
        foreach (var monitor in profiles.SelectMany(profile => profile.Monitors))
        {
            if (string.IsNullOrEmpty(monitor.DevicePath) || library.Find(monitor.DevicePath) != null)
                continue;
            library._store.Monitors.Add(new KnownMonitor
            {
                DevicePath = monitor.DevicePath,
                FriendlyName = monitor.FriendlyName,
                Connector = monitor.OutputTechnology,
                LastPosition = new MonitorPosition { X = monitor.Position.X, Y = monitor.Position.Y },
                LastResolution = new MonitorResolution { Width = monitor.Resolution.Width, Height = monitor.Resolution.Height },
                LastRefreshRate = monitor.RefreshRate.IsSpecified ? monitor.RefreshRate.Clone() : null,
                LastRotation = monitor.Rotation,
            });
            library.RememberExactRate(library._store.Monitors[^1], monitor.Resolution, monitor.RefreshRate);
            added++;
        }
        if (added > 0)
        {
            AppLog.Info($"Monitor library learned {added} monitor(s) from saved profiles.");
            library.TrySave();
        }
        return library;
    }

    public KnownMonitor? Find(string devicePath) =>
        _store.Monitors.FirstOrDefault(monitor => monitor.DevicePath.Equals(devicePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Records what is connected and how the active ones are set up right now.</summary>
    public void Observe(DisplayInventory inventory)
    {
        foreach (var display in inventory.Displays)
        {
            var known = Find(display.DevicePath);
            if (known == null)
            {
                known = new KnownMonitor { DevicePath = display.DevicePath };
                _store.Monitors.Add(known);
                AppLog.Info($"Monitor library: new monitor {display.EdidId} '{display.FriendlyName}' on {display.AdapterName}.");
            }

            known.FriendlyName = display.FriendlyName;
            if (display.AdapterName.Length > 0)
                known.AdapterName = display.AdapterName;
            known.Connector = display.Connector;
            known.LastConnected = DateTime.UtcNow;
            if (display.PreferredResolution != null)
                known.PreferredResolution = display.PreferredResolution;

            if (display.Active is { } active)
            {
                known.LastPosition = new MonitorPosition { X = active.Position.X, Y = active.Position.Y };
                known.LastResolution = new MonitorResolution { Width = active.Resolution.Width, Height = active.Resolution.Height };
                known.LastRefreshRate = active.RefreshRate.IsSpecified ? active.RefreshRate.Clone() : known.LastRefreshRate;
                known.LastRotation = active.Rotation;
                if (display.Modes.Count > 0)
                    known.Modes = display.Modes;
                RememberExactRate(known, active.Resolution, active.RefreshRate);
            }
        }
        TrySave();
    }

    private void RememberExactRate(KnownMonitor known, MonitorResolution resolution, MonitorRefreshRate rate)
    {
        if (!rate.IsSpecified || rate.IsApproximate)
            return;
        var hz = (uint)Math.Round(rate.ToHz());
        known.ExactRates.RemoveAll(existing => existing.Width == resolution.Width && existing.Height == resolution.Height &&
            Math.Round((double)existing.Numerator / existing.Denominator) == hz);
        known.ExactRates.Add(new ExactRefreshRate
        {
            Width = resolution.Width,
            Height = resolution.Height,
            Numerator = rate.Numerator,
            Denominator = rate.Denominator,
        });
    }

    /// <summary>The rate to request for a whole-Hz choice: the exact rational seen before for
    /// that resolution if there is one, otherwise an approximate one Windows rounds to a mode.</summary>
    public MonitorRefreshRate RateFor(string devicePath, uint width, uint height, uint hz)
    {
        var exact = Find(devicePath)?.ExactRates.FirstOrDefault(rate =>
            rate.Width == width && rate.Height == height && rate.Denominator != 0 &&
            Math.Round((double)rate.Numerator / rate.Denominator) == hz);
        return exact != null
            ? new MonitorRefreshRate { Numerator = exact.Numerator, Denominator = exact.Denominator }
            : new MonitorRefreshRate { Numerator = hz, Denominator = 1, IsApproximate = true };
    }

    /// <summary>
    /// The shift that maps usual places into Windows' current coordinates (live = home + offset).
    /// Windows puts whichever display is main at (0,0), so swapping the main monitor moves every
    /// coordinate; the monitors that did not move agree on the shift, so take the majority.
    /// </summary>
    public Point HomeOffset(IEnumerable<(string DevicePath, int X, int Y)> livePositions)
    {
        var offsets = livePositions
            .Select(live => (Live: live, Home: Find(live.DevicePath)?.HomePosition))
            .Where(pair => pair.Home != null)
            .Select(pair => new Point(pair.Live.X - pair.Home!.X, pair.Live.Y - pair.Home.Y))
            .ToList();
        return offsets.Count == 0 ? Point.Empty
            : offsets.GroupBy(offset => offset).OrderByDescending(group => group.Count()).First().Key;
    }

    /// <summary>
    /// Makes the live layout the usual places: active monitors where they are now, plus
    /// <paramref name="others"/> (switched-off or unplugged monitors, already in live coordinates).
    /// Every other remembered usual place is re-based into the same coordinates first, so all of
    /// them stay comparable with each other.
    /// </summary>
    public void RecordHome(IReadOnlyCollection<(string DevicePath, int X, int Y)> active,
        IEnumerable<(string DevicePath, int X, int Y)> others)
    {
        var offset = HomeOffset(active);
        var activePaths = new HashSet<string>(active.Select(position => position.DevicePath), StringComparer.OrdinalIgnoreCase);
        foreach (var known in _store.Monitors.Where(known => known.HomePosition != null && !activePaths.Contains(known.DevicePath)))
            known.HomePosition = new MonitorPosition { X = known.HomePosition!.X + offset.X, Y = known.HomePosition.Y + offset.Y };
        foreach (var (devicePath, x, y) in active.Concat(others))
        {
            if (Find(devicePath) is { } known)
                known.HomePosition = new MonitorPosition { X = x, Y = y };
        }
        TrySave();
    }

    /// <summary>Sets the usual place of individual monitors (already in usual-place coordinates).</summary>
    public void SetHomePositions(IEnumerable<(string DevicePath, int X, int Y)> positions)
    {
        foreach (var (devicePath, x, y) in positions)
        {
            if (Find(devicePath) is { } known)
                known.HomePosition = new MonitorPosition { X = x, Y = y };
        }
        TrySave();
    }

    public void SetNickname(string devicePath, string? nickname)
    {
        var known = Find(devicePath);
        if (known == null)
            return;
        known.Nickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
        TrySave();
    }

    public void Forget(string devicePath)
    {
        _store.Monitors.RemoveAll(monitor => monitor.DevicePath.Equals(devicePath, StringComparison.OrdinalIgnoreCase));
        TrySave();
        AppLog.Info($"Monitor library: forgot {DisplayLayout.EdidIdFromDevicePath(devicePath)}.");
    }

    /// <summary>The name to show for a monitor anywhere in the app (nickname first).</summary>
    public string NameFor(MonitorInfo monitor) =>
        Find(monitor.DevicePath)?.DisplayName ??
        (!string.IsNullOrWhiteSpace(monitor.FriendlyName) ? monitor.FriendlyName : monitor.EdidId);

    private void TrySave()
    {
        try { Save(); }
        catch (Exception ex) { AppLog.Warn($"Could not save the monitor library: {ex.Message}"); }
    }

    private void Save()
    {
        Directory.CreateDirectory(ProfileManager.StorageDir);
        var temporaryPath = StorePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_store, JsonOptions));
        File.Move(temporaryPath, StorePath, true);
    }
}
