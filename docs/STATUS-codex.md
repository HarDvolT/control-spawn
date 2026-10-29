# Codex status — 2026-10-29

## Scope and execution boundary

Implemented the hardware and lighting brief in `src/Prism.Core`, `src/Prism.Hardware`,
`src/Prism.Lighting`, `tests/Prism.Hardware.Tests`, and `tests/Prism.Lighting.Tests`,
plus `Prism.sln`, `Directory.Build.props`, and this file. Other agents' folders and
existing documentation were not edited. The solution currently includes these five
assigned projects; Router integration is left to its owner.

No real hardware adapters, sockets, discovery, listeners, service queries, drivers,
registry operations, WMI methods, router access, or lamp commands were executed.
No software or global tools were installed. No commit was made.

## What exists

- .NET 8 solution and shared nullable/implicit-using/build settings; LHM dependency
  pinned to `LibreHardwareMonitorLib` 0.9.6. Windows service-status queries use
  `System.ServiceProcess.ServiceController` 8.0.0, with an explicit Windows guard.
- Core sensor readings/snapshots, fan groups, validated eight-point curves, modes,
  temperature sources, RGB colors, lighting effects/scenes, hardware/fan/RGB/lamp
  interfaces, and an injectable monotonic clock.
- Lazy LHM adapter: opens only when explicitly read, recursively updates hardware,
  preserves sensor identifiers/names/kinds, and exports CPU, GPU, memory/DIMM,
  motherboard/Super I/O, storage and network sensors in one snapshot per 1 Hz poll.
  CPU total/core load, clocks, Tctl/CCD/package power and AMD GPU usage, temperatures,
  hotspot, memory, clocks, power and fan readings are retained when LHM exposes them.
  Physical RAM total is derived from LHM's used + available readings. Binary memory
  units are labeled GiB/MiB. Missing sensor values stay nullable, rather than zero.
- Fan map: Radiator #2, Case fans #6, fixed AIO pump #7; unused headers appear as
  `Empty header N` only when their RPM becomes positive. Only controls on the
  verified NCT6701D Super I/O chip are exposed; GPU fan control stays with its driver.
- Fan engine: exact Standard BIOS points, Silent/Turbo/Full presets, linear
  interpolation, temperature hysteresis, independent up/down slew limits, minimum
  and 100% clamps, and CPU/board/GPU/maximum source selection.
- Safety: pump always receives 100% and cannot be configured. Any CPU/GPU reading
  at least 90 C forces all available controls to 100% immediately, bypassing ramps.
  Three consecutive failed snapshots or missing required temperature sources force
  all controls full; individual failed snapshots/missing selected sources already
  use conservative 100% output. Success resets the failure counter. Recovery ramps
  down from full. Dispose, process exit, unhandled exceptions, and control-write
  failures attempt BIOS `SetDefault()` on every available control, continuing after
  individual cleanup failures. Fan writes/configuration/cleanup are serialized.
- Read-only Asus/iCUE service conflict check behind `IServiceQuery`; no service
  start/stop/configuration methods are present.
- Handwritten OpenRGB SDK codec/client: ORGB little-endian packet headers, bounded
  payloads, fragmented stream reads, protocol negotiation, legacy timeout/reconnect,
  controller enumeration and data parsing, modes/zones/matrices/LEDs/colors,
  UpdateLEDs, UpdateMode (preserves mode value), and SetCustomMode/direct mode.
  Deliberately advertises protocol **3**, supports formats 0–3, and negotiates down
  on older servers. Newer servers can supply the negotiated older format. Protocol
  4–6-only features are outside this implementation. A background reader invalidates
  cached controller indices on device-list notifications; callers explicitly refresh.
- Effect engine: 30 fps callback loop, deterministic rendering from injected time,
  Static, Breathing, Strobing, ColorCycle, RainbowWave, StarryNight, Smart (40–85 C
  cool/hot gradient), Music, Adaptive and Off. Audio level, screen average and CPU
  temperature inputs have interfaces; actual audio/screen capture is not included.
- Yeelight SSDP discovery behind a UDP transport, response parsing/deduplication,
  JSON commands with CRLF framing, set_power/set_bright/set_rgb/set_ct_abx/start_cf,
  response/error/notification handling and request IDs. Normal commands are
  serialized, spaced by at least one second and limited to 60 in a sliding minute.
  Music mode starts a local listener, sends set_music, validates the callback peer,
  and streams on that connection without normal-mode quota. Callback acceptance,
  responses and writes have deadlines; listeners and streams are disposed.
- LampSync: average/main color selection, exponential color smoothing, no more
  than 10 color updates per second in music mode or one per second in normal mode.
- Fake sensor/fan/service/RGB/lamp/network/discovery/music adapters. Both composition
  factories accept `--mock` and choose fakes without opening hardware or sockets.
  These are libraries: the future service host passes its arguments to the factories;
  there is no executable/service/UI entry point in this assignment.

## Validation and tests

Added xUnit tests for curve boundaries/interpolation, invalid curves, temperature
sources, hysteresis, minimum/maximum clamps, bidirectional smoothing, pump protection,
hot CPU/GPU overrides, consecutive failures/reset, missing sources, cleanup continuation,
fan discovery, RAM totals, fake service checks and snapshot delivery. Lighting tests
cover every effect's determinism, selected exact outputs, frame scheduling, fixed
OpenRGB wire fixtures, versioned controller structures and matrices, malformed input,
fragmented in-memory client traffic and device-list invalidation, Yeelight framing,
flow validation, quota/cancellation, fake discovery, command responses/errors, callback
stream routing, LampSync smoothing/caps, and mock composition.

Actually performed: inspected `dotnet --info`; parsed all five assigned project XML
files and shared props; checked local project/solution references; checked hand-authored
legacy controller and packet fixture sizes/fields; reviewed adapter boundaries and
cleanup paths; checked assigned files for trailing whitespace. These static checks
do not establish C# compilation or test success.

`dotnet build Prism.sln` and `dotnet test Prism.sln` were both attempted. Both stopped
at SDK resolution: **No .NET SDKs were found**. Only .NET 8 runtimes are installed.
Consequently, none of these projects was restored, compiled, or executed, and none of
the xUnit tests has run. Dependency/API compatibility still needs an SDK build.

Once an SDK is available, run `dotnet build Prism.sln` and `dotnet test Prism.sln`.
All current tests use fakes and in-memory streams, never real endpoints or hardware.

## Open questions and practical limits

- Confirm compilation/package restore and run the unit suite when the SDK is available.
- Real sensor coverage (including each DIMM temperature) depends on LHM exposure and
  privileges. It was not probed; absent readings remain absent/null.
- Select product defaults for Silent/Turbo, minimum speed, slew limits, hysteresis and
  LampSync smoothing. Current choices are explicit, conservative library defaults.
- Future hosting must wire snapshots to `FanController.Apply`, pass measured elapsed
  time, feed effects/sync, own cancellation/disposal, and expose pump read-only to UI.
- Managed cleanup is best-effort. Hard process termination, power loss, native faults
  that bypass managed handlers, and OS hangs cannot guarantee BIOS restoration; a
  future service/watchdog design is required to address that separately.
- Hardware conflict services are reported only. Resolving ownership/privileges and
  authorizing real operations belong to later integration; no automatic service changes.
- Discovery is IPv4. Music mode requires an explicitly selected LAN address reachable
  by the bulb and matching callback peer; no firewall/router changes are attempted.
- No hardware, OpenRGB server, bulb, or other real-network interoperability was tested.

## Primary references consulted

- [OpenRGB SDK protocol specification](https://gitlab.com/CalcProgrammer1/OpenRGB/-/blob/master/Documentation/OpenRGBSDK.md)
- [Yeelight WiFi Light Inter-Operation Specification](https://www.yeelight.com/download/Yeelight_Inter-Operation_Spec.pdf)
- [LHM 0.9.6 Computer API](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Computer.cs)
- [LHM 0.9.6 control interface](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/IControl.cs)
- [LHM 0.9.6 physical memory sensors](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Memory/TotalMemory.cs)
