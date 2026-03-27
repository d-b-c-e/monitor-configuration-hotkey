using System.Text.Json;

namespace MonitorProfileSwitcher;

internal class ProfileManager
{
    private static readonly string ProfileDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MonitorProfileSwitcher");

    private static readonly string ProfilePath = Path.Combine(ProfileDir, "profiles.json");

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
            return;
        }

        var json = File.ReadAllText(ProfilePath);
        _store = JsonSerializer.Deserialize<ProfileStore>(json, JsonOptions) ?? new ProfileStore();
    }

    public void Save()
    {
        Directory.CreateDirectory(ProfileDir);
        var json = JsonSerializer.Serialize(_store, JsonOptions);
        File.WriteAllText(ProfilePath, json);
    }

    public DisplayProfile CaptureProfile(string name, HotkeyBinding? hotkey = null)
    {
        var snapshot = DisplayManager.CaptureCurrentConfig();
        var profile = new DisplayProfile
        {
            Name = name,
            Hotkey = hotkey,
            Monitors = snapshot.Monitors,
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

    public void SetHotkey(string profileName, HotkeyBinding hotkey)
    {
        var profile = _store.Profiles.FirstOrDefault(p =>
            p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        if (profile != null)
        {
            profile.Hotkey = hotkey;
            Save();
        }
    }

    public DisplayProfile? GetProfile(string name)
    {
        return _store.Profiles.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}
