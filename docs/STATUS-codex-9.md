# Codex round 9 status — sensor lighting

Implemented only the sensor-lighting brief. No commit was created.

## Contract and configuration

- Protocol 1 remains additive: `LightingState.Bindings` maps target IDs to `SensorBinding`; `setSensorBinding` accepts `{ target, source, min, max, style, palette, smoothing, alert, autoLearn, hysteresis }`. A null/omitted source removes the binding. Device targets use their existing IDs; zones use `<device-id>/zone/<zero-based-index>`; configured lamps use their existing IDs.
- Bindings persist separately from the normal lighting configuration. Removing a binding restores that normal configuration. Multiple targets can use different sources/styles simultaneously, including a device outside the normal sync group. Zone bindings take precedence over a whole-device binding within that zone.
- Validation rejects unknown targets/sources, nonfinite or reversed ranges, invalid styles, smoothing outside 0–5 seconds, negative/nonfinite hysteresis, invalid alerts, and malformed palettes. Palettes contain 2–6 ordered stops spanning 0–1, with valid RGB hex colours. Invalid commands leave settings unchanged.
- Optional `SystemState.GpuMemoryJunction`, `DiskState.ActivityPercent`, and `LightingState.BindingReadings` expose available telemetry and session readings without changing existing fields. Disk activity never substitutes disk space usage.

## Sources and rendering

- Sources: GPU temperature/hotspot/memory junction, usage, watts, clock, VRAM percentage and fan RPM; CPU temperature/load/watts/clock/per-core maximum; RAM percentage and maximum DIMM temperature; board temperature; each published fan header's RPM; network download/upload; maximum disk activity/temperature; connected router signal score and ping.
- Sources read the existing state only. No new sensor polling, router request, discovery or hardware acquisition path was added. Missing/nonfinite readings and percentages with missing/zero totals fall back to the normal effect. The UI explains which source is unavailable.
- Defaults include GPU temperature 35–85 °C, hotspot 40–100 °C, GPU power 15–300 W and load 0–100%. Auto-learn tracks raw session extrema independently per target, with a centred marker until two distinct values are observed. Learned extrema are session-only and are retained across style edits; changing source or toggling auto-learn resets learning.
- Five styles: colour gradient (default blue ? green ? amber ? red), fill bar with white tip, breathing speed, brightness and threshold. Threshold style switches high at the range midpoint and returns low below midpoint minus hysteresis (default two source units).
- Exponential smoothing uses elapsed time and a 0–5 second time constant; zero is immediate. Alerts use the raw value so smoothing cannot delay an over-threshold red pulse. Alerts beat the binding and ordinary event effects; the fan-failsafe event retains highest priority. No fan failsafe/control rules changed.
- RGB sensor frames flow through the existing GPU 10 fps pacer, change suppression and bus-turn checks. Existing lamp sync pacing also remains in place. An own-colour target restores its configured colour/colour temperature when its sensor becomes unavailable or its binding is removed.

## App and mock preview

- Lighting has a “Driven by a sensor” section immediately below the case map. Device/lamp tabs and a zone selector choose the target. Controls include a grouped source picker, the five requested presets, segmented styles, a 2–6 colour palette editor, min/max ThickSliders, auto-learn, smoothing and alert settings. Apply saves a complete draft; Use normal effect removes it. Routine telemetry refreshes preserve editing drafts.
- The live value, gradient range and marker use service session readings when available. The mock client validates and round-trips the same command, supplies changing optional GPU/disk data, and uses the shared renderer for the case-map preview. Other devices continue showing their normal effects.

## Verification

- `dotnet build Prism.sln -m:1 --no-restore -p:NuGetAudit=false -v minimal`: **0 warnings, 0 errors**.
- `dotnet test Prism.sln -m:1 --no-build --no-restore -v minimal`: **460 passed, 0 failed, 0 skipped** (Hardware 34, Lighting 141, Router 68, Service 112, App 105).
- New coverage includes fixed-value mappings for all five styles, endpoint clamping, fill tip, elapsed-time smoothing, auto-learn/reset, hysteresis, raw alert priority, fan-failsafe priority, missing/nonfinite sources, validation including null palette entries, independent bindings, zones, persistence/removal and atomic command rejection. An injected fake-backend runtime verifies that an unsynced bound device follows its sensor while another device keeps its ordinary effect. Session readings are checked through the state contract.
- Detached sensor-only WPF preview: **0 build warnings/errors**, eight PNGs under `tests/Prism.App.Tests/obj/preview-codex-9/`, empty `bindings.log`. Visually reviewed wide/narrow layouts, device tabs, source grouping, range marker, missing-source explanation, fallback and red alert.
- `git diff --check`: passed.

Reproduce the detached sensor-only preview:

```powershell
dotnet build tests/Prism.App.Tests/Preview/Prism.App.Preview.csproj -m:1 --no-restore -p:NuGetAudit=false
dotnet run --project tests/Prism.App.Tests/Preview/Prism.App.Preview.csproj --no-build --no-restore -- tests/Prism.App.Tests/obj/preview-codex-9 --sensor
```

## Execution boundaries

Execution was limited to source edits, cached builds, tests using fakes/fixtures and detached WPF rendering. The production app/service was not launched. No real hardware, Windows services, registry, router, lamps or fryer were accessed or changed. Hardware/firmware behaviour was not live-tested. No commits were made.
