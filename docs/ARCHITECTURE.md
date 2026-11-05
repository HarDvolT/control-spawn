# Architecture

This describes the app as it is today. Where it's heading is in [PLAN-platform.md](PLAN-platform.md).

## Processes

| Process | Runs as | Job |
|---|---|---|
| `ControlSpawn.Service.exe` | Windows service `ControlSpawn`, LocalSystem | Sensors, fan curves, safety, lighting loop, lamps, router, fryer, automations |
| `ControlSpawn.FanGuard.exe` | Child of the service | Hands fans back to the BIOS if the service hangs or dies |
| `ControlSpawn.exe` | The signed-in user | Main window, tray and desktop widgets |
| `OpenRGB.exe` | Bundled and unmodified | RGB server on TCP 6742 |

Only the service touches hardware, so admin rights live in one place. Fans, lighting and automations keep running with no window open.

## The pipe

The app talks to the service over the named pipe `\\.\pipe\controlspawn`, using one JSON message per line. The contract is `src/ControlSpawn.Core/Ipc/PipeContract.cs`:

- The client sends a request and gets a reply with the same id.
- The service pushes a state message about once a second.
- A client that can't be verified gets read-only access.

## Projects

| Project | Job |
|---|---|
| `ControlSpawn.Core` | Pipe contract and shared models |
| `ControlSpawn.Hardware` | LibreHardwareMonitor sensors, fan curves and safety |
| `ControlSpawn.Lighting` | OpenRGB client, effects at 30 fps, sensor lighting, Mi Home and Yeelight |
| `ControlSpawn.Router` | ZLT router client |
| `ControlSpawn.Service` | Service host. Each module has its own retry loop, so one failure doesn't stop the others |
| `ControlSpawn.FanGuard` | Fan watchdog |
| `ControlSpawn.App` | WPF app |

## Fan safety

These rules are enforced in code and covered by tests:

- Fan control only runs on verified boards (`VerifiedBoards`). Everywhere else, the BIOS keeps the fans.
- The AIO pump stays at 100%.
- If the CPU or GPU reaches 90 °C, or sensors fail three times in a row, every controlled fan goes to 100%.
- Curves can't go below their minimum.
- On shutdown or a crash, fans go back to the BIOS.

To add a board, check every header on the real PC first. Fan roles are still tied to header numbers (`FanMap`, `FanController`), and making them configurable is Phase 1 of the plan.

## Other rules

- The router is read-only. It allows one login, so there's one shared client.
- Passwords and tokens are stored with Windows DPAPI.
- The air fryer asks before it starts or resumes.
- Every hardware or network dependency sits behind an interface, with a fake for tests and for `--mock`.

## Where data lives

- Settings and service logs: `C:\ProgramData\Control Spawn`
- Per-user app data: `%APPDATA%\Control Spawn` and `%LOCALAPPDATA%\Control Spawn`
