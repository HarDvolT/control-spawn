# Prism — architecture

Native Windows app, C# / .NET 8. One solution `Prism.sln`.

```
src/
  Prism.Core/              shared models, settings, interfaces (no hardware)
  Prism.Hardware/          LibreHardwareMonitorLib sensors + fan control, fan curve engine, failsafe   (Codex)
  Prism.Lighting/          OpenRGB SDK client (TCP 6742), effect engine (30 fps), Yeelight LAN + music mode (Codex)
  Prism.Router/            native C# port of router-signal-widget core modules                           (OpenCode)
  Prism.Service/           elevated Windows service hosting Hardware + Lighting, named-pipe API          (later)
  Prism.App/               WinUI 3 UI, waits for the design to be approved                                (later)
tests/
  Prism.Hardware.Tests/  Prism.Lighting.Tests/  Prism.Router.Tests/   (xUnit)
docs/  HARDWARE.md (verified facts), ARCHITECTURE.md (this), fan-research-codex.md
```

## Control center, background and desktop widgets (user requirement, 2026-10-29)

Prism is the full control center for the PC. Three processes:

1. **Prism.Service** (Windows service, LocalSystem, starts at boot, before sign-in): hardware sensors,
   fan curves + safety, OpenRGB/effects, lamp sync, router polling. It keeps working with no UI open.
   Serves a named-pipe API (`\\.\pipe\prism`) for state snapshots (1 Hz push) and commands.
2. **Prism.App** (WinUI 3, per-user): the main window. Closing it hides to the tray (setting
   "Keep running when closed"). Tray menu: open, scenes, fan mode, lighting off, widgets show/hide, quit.
   "Start with Windows" = per-user Run key or a scheduled task at logon, with "Start minimized to tray".
3. **Desktop widgets** (hosted by Prism.App, one borderless window each): System monitor, CPU, GPU, Fans
   (mode switch + RPM), Lighting (colour + effect), Lamp, Modem (verdict, bars, RSRP/SINR, ping), Ping
   graph, Network, Scenes. Each widget is customizable: size S/M/L, which metrics it shows, background
   opacity, always on top, click-through, lock position, snap to grid; can control as well as display.
   Layout (positions, sizes, per-widget settings) saved per monitor in `%APPDATA%\Prism\widgets.json`
   and restored at startup ("Show widgets at startup"). Widgets read from the same service snapshots, so
   they cost almost nothing extra.

The UI layers never talk to hardware directly: only the service does, so admin rights live in one place.

## Rules for everyone writing code here

- Target `net8.0` (`net8.0-windows` only where Windows APIs are needed). Nullable enabled, warnings as errors off.
- Every hardware or network dependency sits behind an interface, with a fake for tests and a `--mock` mode,
  so all logic is unit-testable without touching hardware, the router or the network.
- Do NOT run anything that touches real hardware, drivers, services, the registry, WMI methods, the router
  or the lamp. Code and unit tests only. Do not install global tools or system software.
- The .NET 8 **SDK is not installed yet** on this PC (only the runtime). If `dotnet build` is unavailable,
  write the code carefully anyway, keep projects buildable, and list in your summary what was not compiled.
- Each agent only edits its own folders (see owners above) plus shared files it creates under `src/Prism.Core`
  only when listed in its brief. Do not edit another agent's folders.
- Do not commit; leave changes in the working tree.

## Safety rules the code must enforce

- Fans: AIO pump (Fan #7) always 100%, never writable from the UI. Any fan curve value is clamped to
  [minimum, 100]. If any CPU/GPU temp ≥ 90 °C, or the sensor read fails 3 times in a row, every
  controllable fan goes to 100%. On shutdown/crash/unhandled exception: `SetDefault()` on every control
  (hand back to BIOS). Spin-up/down smoothing limits the change per second.
- Router: read-only, same 4 allowed commands as the Python `ALLOWED_CMDS`, the two read cmds GET only.
  The router allows ONE login: one shared client. Password and session stored with DPAPI (CurrentUser).
- Yeelight: rate limits (~60 commands/min in normal mode); use music mode (the bulb connects back to a
  local TCP listener) for effect streaming.
