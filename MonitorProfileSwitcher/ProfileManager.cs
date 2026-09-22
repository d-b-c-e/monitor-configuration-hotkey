using System.Text.Json;

namespace MonitorProfileSwitcher;

internal class ProfileManager
{
    private static readonly string ProfileDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MonitorProfileSwitcher");

    private static readonly string ProfilePath = Path.Combine(ProfileDir, "profiles.json");

    /// <summary>Directory containing profiles.json (for an external-change watcher).</summary>
    public static string StorageDir => ProfileDir;

    /// <summary>The profiles.json filename (no path).</summary>
    public static string StorageFileName => "profiles.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private ProfileStore _store = new();

    public IReadOnlyList<DisplayProfile> Profiles => _store.Profiles;

    public void Load()
    {
        if (!File.Exists(ProfilePath))
        {
            _store = new ProfileStore();
            AppLog.Info($"No profile store found at {ProfilePath}; starting with an empty store.");
            return;
        }

        var json = File.ReadAllText(ProfilePath);
        _store = JsonSerializer.Deserialize<ProfileStore>(json, JsonOptions) ?? new ProfileStore();
        AppLog.Info($"Loaded {_store.Profiles.Count} profile(s) from {ProfilePath}.");
    }

    /// <summary>Re-read profiles.json from disk, discarding the in-memory copy. Use when the
    /// file may have changed outside the app (manual edit, restore from backup, sync).</summary>
    public void Reload() => Load();

    public void Save()
    {
        Directory.CreateDirectory(ProfileDir);
        var json = JsonSerializer.Serialize(_store, JsonOptions);
        var temporaryPath = ProfilePath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, ProfilePath, true);
        AppLog.Info($"Saved {_store.Profiles.Count} profile(s) to {ProfilePath}.");
    }

    public DisplayProfile CaptureProfile(string name, HotkeyBinding? hotkey = null)
    {
        var snapshot = DisplayManager.CaptureCurrentConfig();
        var profile = new DisplayProfile
        {
            Name = name,
            Hotkey = hotkey,
            Monitors = snapshot.Monitors,
            NvidiaSurround = NvidiaSurroundManager.TryCaptureActive(),
            CapturedAt = DateTime.UtcNow
        };

        // Replace existing profile with same name
        var existing = _store.Profiles.FindIndex(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            _store.Profiles[existing] = profile;
        else
            _store.Profiles.Add(profile);

        Save();
        return profile;
    }

    public void DeleteProfile(string name)
    {
        _store.Profiles.RemoveAll(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public void SetHotkey(string profileName, HotkeyBinding? hotkey)
    {
        var profile = _store.Profiles.FirstOrDefault(p =>
            p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        if (profile != null)
        {
            profile.Hotkey = hotkey;
            Save();
        }
    }

    public void ApplyMetadataChanges(IReadOnlyList<ProfileMetadataEdit> edits)
    {
        if (edits.Count != _store.Profiles.Count)
            throw new InvalidOperationException("The profile list changed while it was being edited. Reopen Manage Profiles and try again.");

        var existingByName = _store.Profiles.ToDictionary(profile => profile.Name, StringComparer.OrdinalIgnoreCase);
        var originals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reordered = new List<DisplayProfile>(edits.Count);

        foreach (var edit in edits)
        {
            if (!originals.Add(edit.OriginalName) || !existingByName.TryGetValue(edit.OriginalName, out var profile))
                throw new InvalidOperationException("The profile list changed while it was being edited. Reopen Manage Profiles and try again.");

            var name = edit.Name.Trim();
            if (name.Length == 0)
                throw new InvalidOperationException("Profile names cannot be blank.");
            if (!newNames.Add(name))
                throw new InvalidOperationException($"More than one profile is named '{name}'. Profile names must be unique.");

            profile.Name = name;
            profile.Hotkey = edit.Hotkey?.Clone();
            reordered.Add(profile);
        }

        _store.Profiles.Clear();
        _store.Profiles.AddRange(reordered);
        Save();
        AppLog.Info($"Updated profile names, order, and hotkeys: {string.Join(", ", reordered.Select(profile => $"{profile.Name} [{profile.Hotkey?.ToString() ?? "no hotkey"}]"))}");
    }

    public DisplayProfile? GetProfile(string name)
    {
        return _store.Profiles.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}
