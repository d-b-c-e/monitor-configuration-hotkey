# Monitor Profile Switcher IPC v1

The tray application listens on `\\.\pipe\MonitorProfileSwitcher_IPC` while it is running.
The pipe accepts local connections from the same Windows user. The installed executable is
`C:\Program Files\MonitorProfileSwitcher\MonitorProfileSwitcher.exe` on this machine, but a
client does not need to locate it to use the pipe. If the tray app is not running, start the
installed executable and wait for a successful `ping` response.

## Framing

Each request and response is one UTF-8 JSON object preceded by a 4-byte, little-endian,
signed length. This matches XOutputRedux's pipe framing. The server accepts at most 65,536
request bytes; larger or incomplete frames are rejected. Responses are capped at 1,048,576
bytes by the bundled CLI client. Each pipe connection carries one request and one response,
then closes. The server handles one connection at a time, so commands are serialized with tray
hotkeys and menu actions. A client should use a short connection timeout and allow up to 60
seconds for an `apply` response; NVIDIA mode changes can take several seconds. A disconnect or
timeout does not cancel a switch already in progress.

All requests include `protocolVersion: 1` and `command`. Property names are case-insensitive
when read; responses use camel case. Unknown response fields may be ignored by clients.

## Commands

`ping` checks whether the tray app and IPC server are running:

```json
{"protocolVersion":1,"command":"ping"}
```

```json
{"protocolVersion":1,"success":true,"message":"Monitor Profile Switcher is running."}
```

`list-profiles` returns profiles in tray-menu order:

```json
{"protocolVersion":1,"command":"list-profiles"}
```

```json
{"protocolVersion":1,"success":true,"profiles":[{"name":"Sim Racing","topology":"independent","summary":"3 monitor(s)","hotkey":"Ctrl+Alt+3"},{"name":"Sim Racing Surround","topology":"nvidia-surround","summary":"NVIDIA Surround 1x3","hotkey":"Ctrl+Alt+4"}]}
```

`status` reports the current Windows desktop and the saved profile name if exactly one
profile matches its device paths, geometry, rotation, refresh rate, and NVIDIA grid:

```json
{"protocolVersion":1,"command":"status"}
```

```json
{"protocolVersion":1,"success":true,"status":{"topology":"nvidia-surround","monitorCount":1,"desktopWidth":7680,"desktopHeight":1440,"activeProfileName":"Sim Racing Surround","surroundRows":1,"surroundColumns":3,"monitors":[{"name":"NV Surround","width":7680,"height":1440,"x":0,"y":0,"primary":true}]}}
```

`apply` takes an exact saved profile name (case-insensitive). The response arrives after the
switch and live-state verification. A successful response includes an `operationId` that can
be found in `%LOCALAPPDATA%\MonitorProfileSwitcher\logs\app.log`.

```json
{"protocolVersion":1,"command":"apply","profileName":"Sim Racing Surround"}
```

```json
{"protocolVersion":1,"success":true,"message":"Applied and verified profile 'Sim Racing Surround'.","operationId":"a1b2c3d4","profileName":"Sim Racing Surround","status":{"topology":"nvidia-surround","monitorCount":1,"desktopWidth":7680,"desktopHeight":1440,"activeProfileName":"Sim Racing Surround","surroundRows":1,"surroundColumns":3,"monitors":[{"name":"NV Surround","width":7680,"height":1440,"x":0,"y":0,"primary":true}]}}
```

Topology values are `nvidia-surround`, `independent` (two or more separate active Windows
monitors), and `single` (one non-Surround Windows monitor). `monitorCount` is the number of
logical monitors exposed by Windows, so a 1x3 Surround grid reports one monitor. The optional
`surroundRows` and `surroundColumns` fields identify the physical panel grid.

On failure, `success` is false and `errorCode`/`message` explain the problem. Known codes
include `profile_not_found`, `invalid_profile_name`, `apply_failed`, `unknown_command`,
`unsupported_version`, and `command_failed`. A failed `apply` may leave Windows in an
intermediate topology, so a launcher should refresh `status` and show the user the mismatch.
The bundled CLI client also uses `not_running` and `ipc_error` for connection failures.

## Bundled client

```powershell
& 'C:\Program Files\MonitorProfileSwitcher\MonitorProfileSwitcher.exe' --ipc list-profiles
& 'C:\Program Files\MonitorProfileSwitcher\MonitorProfileSwitcher.exe' --ipc status
& 'C:\Program Files\MonitorProfileSwitcher\MonitorProfileSwitcher.exe' --ipc apply 'Sim Racing Surround'
```

The CLI prints the response JSON and exits with code 0 on success, 1 on an IPC or command
failure, and 2 for invalid CLI arguments. Existing direct CLI commands such as `apply` still
operate without the tray app; launchers that need serialized switching and a verified response
should use IPC.
