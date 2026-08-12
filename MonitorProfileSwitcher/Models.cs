using System.Text.Json.Serialization;
using MonitorProfileSwitcher.Native;

namespace MonitorProfileSwitcher;

internal class DisplayProfile
{
    public string Name { get; set; } = "";
    public HotkeyBinding? Hotkey { get; set; }
    public MonitorInfo[] Monitors { get; set; } = [];
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
}

internal class MonitorInfo
{
    public string DevicePath { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public string GdiDeviceName { get; set; } = "";
    public uint SourceId { get; set; }
    public uint TargetId { get; set; }
    public bool IsPrimary { get; set; }
    public MonitorPosition Position { get; set; } = new();
    public MonitorResolution Resolution { get; set; } = new();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY OutputTechnology { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DISPLAYCONFIG_ROTATION Rotation { get; set; } = DISPLAYCONFIG_ROTATION.IDENTITY;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DISPLAYCONFIG_SCALING Scaling { get; set; } = DISPLAYCONFIG_SCALING.IDENTITY;

    public MonitorRefreshRate RefreshRate { get; set; } = new();
}

/// <summary>
/// Refresh rate as the exact rational the CCD API works in (e.g. 143999/1000), not a
/// rounded Hz figure — Windows matches target modes on the exact numerator/denominator.
///
/// Deliberately a plain model class rather than the interop DISPLAYCONFIG_RATIONAL
/// struct this used to be: that struct exposes Numerator/Denominator as FIELDS, and
/// System.Text.Json serializes properties only. So every profile persisted
/// "refreshRate": {} and the captured rate was silently lost on save.
/// </summary>
internal class MonitorRefreshRate
{
    public uint Numerator { get; set; }
    public uint Denominator { get; set; }

    /// <summary>False for profiles written before the rate was persisted correctly — they
    /// load back as 0/0. Those are left for Windows to choose rather than being applied
    /// as a nonsense 0 Hz.</summary>
    [JsonIgnore]
    public bool IsSpecified => Numerator != 0 && Denominator != 0;

    public double ToHz() => Denominator == 0 ? 0 : (double)Numerator / Denominator;

    public override string ToString() => IsSpecified ? $"{ToHz():F2}Hz" : "(unset)";

    public bool Matches(DISPLAYCONFIG_RATIONAL other) =>
        Numerator == other.Numerator && Denominator == other.Denominator;

    public static MonitorRefreshRate From(DISPLAYCONFIG_RATIONAL r) =>
        new() { Numerator = r.Numerator, Denominator = r.Denominator };

    public DISPLAYCONFIG_RATIONAL ToRational() =>
        new() { Numerator = Numerator, Denominator = Denominator };
}

internal class MonitorPosition
{
    public int X { get; set; }
    public int Y { get; set; }
}

internal class MonitorResolution
{
    public uint Width { get; set; }
    public uint Height { get; set; }
}

internal class HotkeyBinding
{
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }
    public string Key { get; set; } = "";

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Key);
        return string.Join("+", parts);
    }

    public uint GetModifiers()
    {
        uint mods = HotkeyApi.MOD_NOREPEAT;
        if (Ctrl) mods |= HotkeyApi.MOD_CONTROL;
        if (Alt) mods |= HotkeyApi.MOD_ALT;
        if (Shift) mods |= HotkeyApi.MOD_SHIFT;
        if (Win) mods |= HotkeyApi.MOD_WIN;
        return mods;
    }

    public uint GetVirtualKeyCode()
    {
        if (Key.Length == 1 && char.IsDigit(Key[0]))
            return (uint)Key[0]; // '0'-'9' map to VK 0x30-0x39
        if (Key.Length == 1 && char.IsLetter(Key[0]))
            return (uint)char.ToUpper(Key[0]); // 'A'-'Z' map to VK 0x41-0x5A

        return Key.ToUpper() switch
        {
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            _ => 0
        };
    }
}

internal class ProfileStore
{
    public List<DisplayProfile> Profiles { get; set; } = [];
}
