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

    public DISPLAYCONFIG_RATIONAL RefreshRate { get; set; }
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
