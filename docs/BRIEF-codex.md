# Brief for Codex: hardware + lighting core

Read docs/ARCHITECTURE.md and docs/HARDWARE.md first and follow their rules.

Build, in C# .NET 8:

1. `Prism.sln`, `src/Prism.Core` (models: SensorReading, FanGroup, FanCurve (8 points temp→%), FanMode
   Silent/Standard/Turbo/Full/Custom, RgbColor, LightingEffect, Scene; interfaces IHardwareMonitor,
   IFanController, IRgbBackend, ILampClient), and `Directory.Build.props`.
2. `src/Prism.Hardware`:
   - `LhmHardwareMonitor` wrapping LibreHardwareMonitorLib (NuGet `LibreHardwareMonitorLib` 0.9.6):
     CPU (usage total + per core, clocks, Tctl, CCD temp, package power), GPU (AMD: usage, temp,
     hotspot, VRAM used/total, clock, power, fan), RAM (used/total, DIMM temps), motherboard (Super I/O
     temps, 7 fans, 7 controls), storage (temps, read/write rates), network (up/down rates). Polls at 1 Hz,
     raises one snapshot event.
   - Fan map from HARDWARE.md (Radiator=#2, Case fans=#6, AIO pump=#7, others "Empty header N", discovered
     when RPM > 0).
   - `FanCurveEngine`: linear interpolation between 8 points, hysteresis, spin-up/spin-down smoothing,
     minimum speed, temp source (CPU / Motherboard / GPU / max of all), the presets (Standard =
     20:20 30:26 40:34 50:45 60:60 67:80 70:100 100:100 from the BIOS; Silent, Turbo, Full sensible).
   - `FanSafety` (all rules in ARCHITECTURE.md) and a `FanController` that applies curves through an
     `IFanControlPort` (real = LHM `Control.SetSoftware/SetDefault`, fake for tests).
   - `AsusServiceCheck`: reports whether AsusFanControlService / LightingService / iCUE services are running
     (read-only query; never stop them in code that runs in tests).
3. `src/Prism.Lighting`:
   - `OpenRgbClient`: the OpenRGB SDK network protocol (TCP 6742, packet header "ORGB", protocol version
     negotiation, controller count/data, UpdateLEDs, UpdateMode/direct mode). Write it yourself against the
     protocol spec (research online), parse the controller data struct, unit-test the encoder/decoder with
     byte fixtures.
   - `EffectEngine` at 30 fps producing colors per LED for: Static, Breathing, Strobing, ColorCycle, Rainbow
     wave, StarryNight, Smart (CPU temp → cool/hot gradient, 40 °C→85 °C), Music (audio level input via
     interface), Adaptive (screen average via interface), Off. Deterministic given a clock for tests.
   - `YeelightClient`: SSDP discovery, JSON-RPC over TCP 55443 (set_power, set_bright, set_rgb, set_ct_abx,
     start_cf), rate limiter, music mode (local TcpListener, bulb connects back), and a `LampSync` that
     follows the engine output (average or main color, smoothed, max 10 updates/s in music mode).
4. `tests/` xUnit projects for both, covering curve interpolation, safety rules, smoothing, effect output,
   OpenRGB packet encoding/decoding, Yeelight message formatting and rate limiting.

Finish with a summary in docs/STATUS-codex.md: what exists, what was built/tested, what was not compiled
and why, open questions.
