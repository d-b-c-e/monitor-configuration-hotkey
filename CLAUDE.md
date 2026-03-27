# Monitor Profile Switcher

System tray app for switching between saved monitor configurations via hotkeys.
Built because MultiMonitorTool and DisplayFusion can't disable monitors on Windows 11 25H2.

## Architecture

- **C# / .NET 8 / WinForms** (tray icon only, no main window)
- Uses Windows CCD API (`QueryDisplayConfig`/`SetDisplayConfig`) — the proper modern API
- Monitors identified by persistent `monitorDevicePath` (survives reboots, unlike adapter LUIDs)
- Profiles saved as JSON in `%LOCALAPPDATA%\MonitorProfileSwitcher\profiles.json`
- Global hotkeys via `RegisterHotKey` Win32 API

## Key Files

- `Native/DisplayConfigApi.cs` — P/Invoke declarations for CCD API structs and functions
- `Native/HotkeyApi.cs` — P/Invoke for global hotkey registration
- `DisplayManager.cs` — Core logic: capture current config, apply saved profile
- `ProfileManager.cs` — JSON serialization, CRUD for profiles
- `TrayApplication.cs` — System tray icon, context menu, hotkey handling
- `CaptureDialog.cs` / `HotkeyDialog.cs` — Minimal WinForms dialogs
- `Models.cs` — Data models for profiles, monitors, hotkeys
- `Program.cs` — Entry point with CLI and tray modes

## CLI Usage

```
MonitorProfileSwitcher                      # tray mode (default)
MonitorProfileSwitcher --status             # show current display config
MonitorProfileSwitcher --debug              # dump raw CCD API data
MonitorProfileSwitcher capture "Name"       # capture current setup
MonitorProfileSwitcher apply "Name"         # apply a saved profile
MonitorProfileSwitcher delete "Name"        # delete a profile
MonitorProfileSwitcher --list               # list saved profiles
```

## CCD API Notes (Win11 25H2)

- `QDC_VIRTUAL_MODE_AWARE` flag is required — without it, mode indices are wrong
- Source `modeInfoIdx` is packed: upper 16 bits = source mode index, lower 16 = clone group
- Target `modeInfoIdx` is packed: upper 16 bits = desktop image index, lower 16 = target mode index
- `adapterId` (LUID) changes every reboot — match monitors by `monitorDevicePath` instead
- To disable a monitor: exclude its path from the array passed to `SetDisplayConfig`
- Must use `SDC_ALLOW_CHANGES | SDC_VIRTUAL_MODE_AWARE` flags

## Status

**Working**: CCD API reading, display enumeration, profile save/load infrastructure, CLI
**Needs testing**: Profile apply (SetDisplayConfig with active path removal), tray app GUI, hotkeys
