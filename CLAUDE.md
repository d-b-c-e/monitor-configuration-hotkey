# Monitor Profile Switcher

System tray app for switching between saved monitor configurations via hotkeys.
Built because MultiMonitorTool and DisplayFusion can't disable monitors on Windows 11 25H2.

## Architecture

- **C# / .NET 8 / WinForms** (tray icon only, no main window)
- Uses Windows CCD API (`QueryDisplayConfig`/`SetDisplayConfig`) — the proper modern API
- Uses NVIDIA NVAPI Mosaic/immersive-gaming calls for Surround topologies
- Monitors identified by persistent `monitorDevicePath` (survives reboots, unlike adapter LUIDs)
- Profiles saved as JSON in `%LOCALAPPDATA%\MonitorProfileSwitcher\profiles.json`
- Rotating diagnostics saved in `%LOCALAPPDATA%\MonitorProfileSwitcher\logs\app.log`
- Global hotkeys via `RegisterHotKey` Win32 API

## Key Files

- `Native/DisplayConfigApi.cs` — P/Invoke declarations for CCD API structs and functions
- `Native/NvApi.cs` — Minimal direct NVAPI interop for Surround grids
- `Native/HotkeyApi.cs` — P/Invoke for global hotkey registration
- `Native/ShellApi.cs` — Explorer/taskbar readiness and restart notifications
- `DisplayManager.cs` — Core logic: capture current config, apply saved profile
- `NvidiaSurroundManager.cs` — Capture, validate, migrate, enable, and disable Surround grids
- `ProfileManager.cs` — JSON serialization, CRUD for profiles
- `TrayApplication.cs` — System tray icon, context menu, hotkey handling
- `IpcService.cs` / `IpcDisplayInspector.cs` — Current-user named pipe, profile queries, and verified apply responses
- `ManageProfilesDialog.cs` — Profile ordering, renaming, and hotkey management
- `CaptureDialog.cs` / `HotkeyDialog.cs` — Capture and hotkey WinForms dialogs
- `AppLog.cs` — Non-fatal rotating file diagnostics
- `Models.cs` — Data models for profiles, monitors, hotkeys
- `Program.cs` — Entry point with CLI and tray modes
- `DebugDump.cs` — Raw CCD path/mode diagnostics
- `UpdateService.cs` — Rate-limited GitHub release checks and installer download
- `scripts/release.ps1` / `installer/MonitorProfileSwitcher.iss` — Publish and installer pipeline

## IPC integration

The running tray instance listens on the current-user-only named pipe
`\\.\pipe\MonitorProfileSwitcher_IPC`. The versioned JSON request/response contract, framing,
time limits, and examples are in `docs/ipc.md`. External launchers should call `list-profiles`
to select a saved profile, `status` to check the live topology, and `apply` before launching a
game. An `apply` response reports success only after the live layout matches that profile.

The built-in CLI client can exercise the pipe:

```
MonitorProfileSwitcher --ipc status
MonitorProfileSwitcher --ipc list-profiles
MonitorProfileSwitcher --ipc apply "Sim Racing Surround"
```

## CLI Usage

```
MonitorProfileSwitcher                      # tray mode (default)
MonitorProfileSwitcher --status             # show current display config
MonitorProfileSwitcher --validate-surround  # validate/read the active NVIDIA Surround grid
MonitorProfileSwitcher --debug              # dump raw CCD API data
MonitorProfileSwitcher capture "Name"       # capture current setup
MonitorProfileSwitcher apply "Name"         # apply a saved profile
MonitorProfileSwitcher delete "Name"        # delete a profile
MonitorProfileSwitcher --list               # list saved profiles
```

## NVIDIA Surround

- Surround is a driver topology, not a Windows CCD topology. The app uses the public NVAPI
  Mosaic/immersive-gaming calls directly; it does not automate NVIDIA Control Panel.
- Capturing while Surround is active stores the 1xN/Nx1 grid, ordered NVIDIA display IDs,
  per-panel mode, overlap/bezel data, rotation and pixel-shift metadata alongside the logical
  Windows `NV Surround` monitor.
- Applying a Surround profile asks NVAPI to restore that grid and waits for it to become active.
  Applying a normal profile leaves Surround through NVAPI's direct 1x1-grid path, waits for the
  physical outputs to return, then runs the existing CCD apply. The older topology-toggle disable
  call is deliberately avoided because current GeForce drivers can return `MODE_CHANGE_FAILED`
  and display an NVIDIA error popup even when a fallback subsequently succeeds.
- Profiles captured before this support are recognized by their single `NV Surround` monitor.
  On first apply, the app infers a matching horizontal/vertical grid from active NVIDIA 1x1
  displays, enables it, and upgrades the profile in `profiles.json`. If the panel dimensions do
  not identify one unambiguous grid, enable Surround once in NVIDIA Control Panel and recapture.
- Some GeForce drivers return `NVAPI_INCOMPATIBLE_STRUCT_VERSION` from the optional validation
  entry point even for the grid they just enumerated. Apply first tries the full saved grid and
  falls back to `NvAPI_Mosaic_EnableCurrentTopo`, whose topology persists across reboots.
- NVAPI display IDs identify physical NVIDIA outputs. If panels move to different GPU ports,
  recapture the Surround profile.

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
- **Rotation lives on the target side too (`targetInfo.rotation`)** and must be applied
  explicitly, exactly like refresh rate. It was captured and shown in the UI but never
  applied — invisible in most profiles, but it makes a profile that differs from its twin
  ONLY by rotation (portrait vs landscape, same monitor, same resolution) apply as a silent
  no-op that still reports success.
- **CCD keeps the source mode UNROTATED.** A 90°-rotated 4K panel reports a `3840x2160`
  source with `ROTATE90`, even though the desktop measures `2160x3840`
  (`Screen.AllScreens`). So saved resolution and rotation are already consistent — do NOT
  swap width/height when applying; just apply both fields.
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

The icon can be lost in two ways, both of which leave the process running — so the usual
symptom is a missing icon, which reads to the user as "it didn't start":

1. **Logon race.** A logon scheduled task can start the app fractionally *before* Explorer.
   With no taskbar, `Shell_NotifyIcon(NIM_ADD)` has nothing to talk to and the icon is
   dropped. Handled by `GuardAgainstMissingTrayIcon()`: if no `Shell_TrayWnd` exists at
   startup, poll for it and re-add once it appears. No timer is created on the normal path.
2. **Explorer restart.** Handled by listening for the `TaskbarCreated` broadcast in
   `HiddenHotkeyWindow`. That window is deliberately a normal invisible top-level window,
   **not** message-only — `HWND_BROADCAST` does not reach message-only windows.

Re-adding is `Visible = false` then `true` (NIM_DELETE + NIM_ADD). Only do it when the icon
is believed missing: toggling a healthy icon sends it to the back of the tray order.

## Hotkey registration is NOT independent of the shell

This file used to claim that `RegisterHotKey` is unaffected by the logon race, so a missing
tray icon never meant broken hotkeys. **That is wrong**, and it cost real diagnosis time on
2026-09-09: the app started 0.1s ahead of Explorer (Winlogon 7001 at 08:54:39, both processes
at 08:54:40) and came up with **no tray icon and no working hotkeys**, while the process sat
there alive and `Responding=True`. A plain restart fixed it.

The actual defect was not the race but the silence. `RegisterAllHotkeys()` discarded the
return value, so a failure produced no retry, no log and no visible sign — a tool whose entire
purpose is hotkeys could start, look healthy and do nothing. Now:

- `RegisterHotKey` is declared `SetLastError = true`, and the error code is kept.
- Failures go into `_failedHotkeys` and `GuardAgainstFailedHotkeys()` retries them every 2s for
  60s — same shape as the tray-icon guard, and no timer is created when everything registers.
- If they still fail, a balloon names the hotkey and the reason, and the tray tooltip becomes
  "(hotkeys unavailable)". `ERROR_HOTKEY_ALREADY_REGISTERED` (1409) is called out separately,
  since another app owning the combination is not something retrying will fix.

**Diagnosing this:** the process running is not evidence the app works — check a hotkey.
`--status` exercises the CLI path only and will happily succeed while the tray instance has no
hotkeys at all.

## Status

**Working**: CCD capture/apply, monitor enable/disable, position, primary display, resolution,
rotation, refresh rate, NVIDIA Surround ↔ independent-monitor switching, profile persistence and
legacy migration, profile ordering/renaming/hotkey management, rotating switch diagnostics, tray UI,
global hotkeys with retry/error reporting, CLI diagnostics, and updater.

The 1x3 Surround → three independent displays → 1x3 Surround round trip was exercised on a
GeForce RTX 5080 with three 2560x1440 panels. NVIDIA's optional validation API returns
`NVAPI_INCOMPATIBLE_STRUCT_VERSION` on that driver, so validation is advisory; the actual grid
apply is verified after every switch by re-enumerating the active topology.
