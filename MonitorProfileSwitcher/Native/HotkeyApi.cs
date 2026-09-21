using System.Runtime.InteropServices;

namespace MonitorProfileSwitcher.Native;

internal static class HotkeyApi
{
    /// <remarks><c>SetLastError</c> matters here: a failed registration is silent otherwise, and
    /// the error code is the only way to tell "another app owns this combination" apart from a
    /// transient failure worth retrying.</remarks>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    /// <summary>Another process already owns the combination. Retrying will not help unless it exits.</summary>
    public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int WM_HOTKEY = 0x0312;
}
