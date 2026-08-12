using System.Runtime.InteropServices;

namespace MonitorProfileSwitcher.Native;

/// <summary>Shell interop needed to keep the tray icon alive across taskbar restarts.</summary>
internal static class ShellApi
{
    /// <summary>Resolves a system-wide message name to its id. Explorer broadcasts
    /// "TaskbarCreated" under such an id every time it builds a new taskbar, which is
    /// the only notification tray apps get that their icon needs re-adding.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    /// <summary>True when a shell taskbar exists to host tray icons. False in the window
    /// between logon and Explorer starting — during which Shell_NotifyIcon has nothing
    /// to talk to and silently drops the icon.</summary>
    public static bool TaskbarExists() => FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;
}
