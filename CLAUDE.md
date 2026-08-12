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
- **Refresh rate lives on the TARGET side of the path (`targetInfo.refreshRate`), not in
  the source mode.** Position and resolution are source-mode fields; setting only those
  restores geometry and lets Windows pick whatever rate it likes. To actually set a rate:
  assign `targetInfo.refreshRate` AND set `targetInfo.modeInfoIdx =
  DISPLAYCONFIG_PATH_MODE_IDX_INVALID` (the already-chosen target mode still encodes the
  old timings, so it has to be dropped) — then apply with `SDC_USE_SUPPLIED_DISPLAY_CONFIG`.
- **Interop structs must not be persisted directly.** `DISPLAYCONFIG_RATIONAL` exposes
  `Numerator`/`Denominator` as *fields*, and `System.Text.Json` serializes properties only,
  so storing it wrote `"refreshRate": {}` and silently lost every captured rate. Persisted
  models are plain classes with properties (`MonitorRefreshRate`); interop types stay in
  `Native/`. Profiles written before this load back as 0/0 and are treated as "unspecified"
  — geometry is restored, rate is left to Windows.
- **Testing a rate change needs a PERSISTED starting state.** `ChangeDisplaySettingsEx`
  with flags 0 changes the rate for the session only; the CCD topology pass re-reads the
  saved display database and reverts it, so any build appears to "restore" the rate. Use
  `CDS_UPDATEREGISTRY` to set up the test or the result is meaningless.

## Tray icon lifetime

The icon can be lost in two ways, both of which leave the process running and hotkeys
working (`RegisterHotKey` is independent of the shell) — so the only symptom is a missing
icon, which reads to the user as "it didn't start":

1. **Logon race.** A logon scheduled task can start the app fractionally *before* Explorer.
   With no taskbar, `Shell_NotifyIcon(NIM_ADD)` has nothing to talk to and the icon is
   dropped. Handled by `GuardAgainstMissingTrayIcon()`: if no `Shell_TrayWnd` exists at
   startup, poll for it and re-add once it appears. No timer is created on the normal path.
2. **Explorer restart.** Handled by listening for the `TaskbarCreated` broadcast in
   `HiddenHotkeyWindow`. That window is deliberately a normal invisible top-level window,
   **not** message-only — `HWND_BROADCAST` does not reach message-only windows.

Re-adding is `Visible = false` then `true` (NIM_DELETE + NIM_ADD). Only do it when the icon
is believed missing: toggling a healthy icon sends it to the back of the tray order.

## Status

**Working**: CCD API reading, display enumeration, profile save/load infrastructure, CLI
**Needs testing**: Profile apply (SetDisplayConfig with active path removal), tray app GUI, hotkeys
