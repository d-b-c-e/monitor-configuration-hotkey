# Window layout restore (trial)

The tray menu's **Remember Window Layouts** option is off by default. When enabled, a
profile switch saves ordinary open windows for the profile being left *before* the
display topology changes. After the destination profile is verified, it restores
that profile's most recently saved layout. The first switch away from a profile
establishes its baseline; **Save Current Window Layout** can establish or replace
it without switching. **Restore Saved Window Layout** retries the saved layout
without changing monitors, which is useful for testing or recovery. The same
manual actions are available as `MonitorProfileSwitcher layout save|restore`;
the CLI also supports `layout enable|disable|status`.

To try it, arrange windows in Work Mode, choose **Save Current Window Layout**,
and enable **Remember Window Layouts**. Switch to another profile, arrange its
windows and save that layout too, then switch back and forth. All switch paths
(tray, hotkey, IPC, direct CLI) share this behavior. The log records how many
windows were captured, restored, closed/replaced, off-screen, or failed.

Snapshots are stored in `%LOCALAPPDATA%\MonitorProfileSwitcher\window-layouts.json`.
The profiles have stable IDs so renaming one does not lose its snapshot. Windows
are matched by live handle, process ID, process start time, and window class;
closed/reopened apps are deliberately not guessed or relaunched. Tool windows,
cloaked windows, borderless full-screen windows, and windows without captions
are omitted. Windows wholly outside the active desktop are not moved. Snapshot
or restore errors do not turn a successful monitor switch into a failure.

This restores geometry and minimized/maximized state, not PowerToys FancyZones'
internal zone membership. Full-screen apps, elevated/protected windows, and apps
that reposition themselves may not obey the restore. The option can be turned
off immediately from the tray menu without changing saved snapshots.
