# Session Notes
<!-- Written by /wrapup. Read by /catchup at the start of the next session. -->
<!-- Overwritten each session — history preserved in git log of this file. -->

- **Date:** 2026-09-20
- **Branch:** master

## What Was Done
- Added direct NVIDIA NVAPI Mosaic/immersive-gaming interop in `MonitorProfileSwitcher/Native/NvApi.cs`.
- Added Surround capture, validation, legacy migration, enable/disable, and state polling in `MonitorProfileSwitcher/NvidiaSurroundManager.cs`.
- Extended profile models and capture/apply flows to persist NVIDIA 1xN/Nx1 grids alongside Windows CCD state.
- Added `--validate-surround`, Surround-aware `--status`/`--list`, tray labels, and capture previews.
- Preserved and completed pending hotkey-registration retry/error reporting changes in `Native/HotkeyApi.cs` and `TrayApplication.cs`.
- Documented the NVAPI behavior, driver fallbacks, architecture, and verified status in `CLAUDE.md`.
- Added generated `publish/` and `dist/` directories to `.gitignore` and updated release notes.
- Published and manually deployed version 1.0.4 to `C:\Program Files\MonitorProfileSwitcher`; the installed tray process is running.
- Migrated the local `Sim Racing Surround` profile to a saved 1x3 grid in `%LOCALAPPDATA%\MonitorProfileSwitcher\profiles.json`.

## Decisions Made
- Use direct public NVAPI instead of UI automation or the old managed wrapper: current driver enumeration works while the wrapper's validation structure is incompatible.
- Leave Surround through a temporary 1x1 NVAPI grid before applying CCD: `EnableCurrentTopo(false)` returns `NVAPI_MODE_CHANGE_FAILED` on the tested GeForce driver.
- Never replay `acceleratePrimaryDisplay`: NVIDIA documents it as undefined on reads and the driver rejects it on apply.
- Treat NVAPI validation as advisory and verify the actual post-switch grid: the installed driver returns `NVAPI_INCOMPATIBLE_STRUCT_VERSION` for its own enumerated topology.

## Open Items
- [ ] Resolve the local hotkey conflict: `Pinball Landscape` and `Sim Racing Surround` both use `Ctrl+Alt+4`.
- [ ] Build and test the Inno Setup installer when Inno Setup is available locally; deployment this session copied the published payload with elevation.

## Next Steps
1. Assign a unique hotkey to `Sim Racing Surround` or `Pinball Landscape` and confirm both register.
2. Tag/release version 1.0.4 if this deployment should become a GitHub release.
3. Exercise Surround switching after any NVIDIA driver update or display-port change and recapture if display IDs change.

## Context for Next Session
The complete 1x3 Surround → three independent 2560x1440 panels → 1x3 Surround round trip passed twice on the RTX 5080. The installed 1.0.4 payload matches the repository publish output; source changes still need the wrapup commit/push that follows these notes.
