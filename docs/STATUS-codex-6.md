# Codex round 6 status

Implemented the round 6 creative-lighting brief on the round 5 LED editor and output layers. Protocol version remains 1. No commit was created. The pre-existing changes to `docs/BRIEF-codex-6.md` were left untouched.

## Spatial lighting and effects

- Added normalized case coordinates, tower defaults, independently movable/rotatable/flippable devices and resizable ARGB zones, matrix positions that preserve empty cells, and an independently draggable lamp point. The default lamp point tracks the strip centre, including after moving the strip.
- Added a pure `SpatialEffects.Render(scene, parameters, time, positions, device, kind, inputs)` and a matching `EffectEngine.Render` overload. Fixed-time output has no random state, I/O or wall-clock dependency. The legacy count-based renderer remains available and accepts zero speed to freeze its phase.
- Added Aurora, Comet, Fire / Lava, Ocean ripple, Plasma, Scanner, Rain / Matrix, Heartbeat, Gradient flow, Candle, Sparkle, Load meters, Temperature flow, Audio spectrum and Screen ambient. Existing effects run through the spatial renderer in the service. Starry night remains available alongside Sparkle.
- Parameters include built-in Aurora, Fire, Ocean, Spectrum and Candle palettes, custom 2-6 nonuniform stops, angle, scale, intensity, comet count/tail, optional CPU-driven heartbeat and device enable flags. Existing brightness and speed controls remain supported. Effect defaults choose suitable warm, ocean or spectrum palettes.
- Load meters map CPU load to RAM, GPU load to GPU LEDs, and memory use to board/strip LEDs. Bars use local vertical/horizontal extents, a bright tip, green/amber/red interpolation and elapsed-time smoothing between sensor polls. Temperature flow and Smart use the appropriate CPU/GPU temperature and a breathing shimmer.
- The existing 30 fps RGB loop renders from map positions, composites saved exact/overlay layers, then applies events. Average lamp follow includes per-device reactive rendering and saved layers; map-point follow samples the spatial field at the lamp point. Existing lamp rate limiting and smoothing remain in place.

## Events and capture boundaries

- Added prioritized three-second event overlays with a final 0.8-second fade. Fan failsafe has highest priority, followed by poor signal, ping spike, notification and scene change. Failsafe strobing reaches disabled/unsynced RGB devices when its event toggle is enabled.
- Router events trigger on transitions to ping above 100 ms or signal score below 40 / fewer than two bars. Scene application triggers a white sweep. Notification is exposed as a future-facing command. Every event has a persisted toggle in Lighting and Settings.
- Added `IAudioLevels` and `IScreenZones` injection boundaries. WASAPI shared-endpoint loopback and DXGI Desktop Duplication implementations are code only in this task: neither was run. Capture construction does no I/O; explicit service-owned workers start only for the relevant effect, stop on effect changes/shutdown, and retry unavailable sessions with a warning and silent/black fallback.
- Audio processing uses a Hann window and logarithmically spaced frequency bands. The native loopback implementation supports float32 and PCM16 mix formats. Desktop duplication samples left/centre/right zones on output zero of the default adapter. Session permissions, device changes, other mix formats and monitor configurations need later live integration verification.
- The app receives only audio levels, beat amount and three average colours for its main preview. It does not receive captured audio samples or desktop pixels. Mock/gallery previews use illustrative inputs.

## Additive contract changes

`src/Prism.Core/Ipc/PipeContract.cs` adds:

- Optional `LightingState.Parameters`, `Map`, `Events` and `Inputs` fields.
- `EffectParameters`, `PaletteStop`, `MapPoint`, `DevicePlacement`, `LightingMap`, `EventEffectOptions`, `LightingEventKind` and `ReactiveInputState`. Placement `Zone` optionally identifies an independently positioned ARGB zone; null identifies the device.
- Optional `RgbDeviceState.CustomEnabled`, so the preview can distinguish saved custom colours from an active exact layer.
- `setEffectParams`: `{ parameters: EffectParameters }`.
- `setLightingMap`: `{ map: LightingMap }`.
- `setEventEffects`: `{ events: EventEffectOptions }`.
- `notifyLighting`: `{ kind: "Notification" }`; externally requested safety/router events are rejected.
- `saveLightingScene`: `{ name: "Gaming|Work|Night|Movie" }`; saves the current effect, colour, brightness, speed and parameters into that existing scene, retaining its fan/lamp configuration.

`LightingEffect` values were appended in `Models.cs`; existing values were not reordered. Existing `setLighting` arguments still work and preserve the current extended parameters. Older state/settings payloads deserialize with defaults. New parameter/map commands validate before persistence and leave settings unchanged on invalid input.

## App

- Rebuilt Lighting as a two-column desktop workspace: a large glowing case map / per-LED canvas on the left and the effect/parameter inspector on the right. Below 850 px it stacks into one column.
- Every gallery card renders a miniature case through the same spatial engine at 30 fps. Loaded controls own their preview timers and stop them on unload. Reduce animations freezes preview time; detached offscreen rendering uses fixed times.
- Added a floating icon toolbar with tooltips, a draggable palette-stop strip, built-in palettes, stop colour editing/add/remove, ThickSliders, direction dial, device toggles, lamp follow, event toggles, and saving lighting parameters to the existing scenes.
- Arrange map supports device/ARGB-zone dragging, rotation, flipping, lamp dragging and reset. Zero-LED strips have a dashed placeholder so they can still be positioned before resizing.
- Kept brush dragging, zone/device fill, directional gradients, eyedropper, overlay eraser, range selection/application, colour wheel and hex inputs, recent swatches, paint brightness, Custom/Overlay modes, resize controls, named LED-layout presets and scene layout references. Painting remains debounced and pending edits still flush before resizing.
- Dashboard/widget strip previews also recognize the new effects.

## Verification

- `dotnet build Prism.sln -m:1 --no-restore -p:NuGetAudit=false`: succeeded, **0 warnings, 0 errors**.
- `dotnet test Prism.sln -m:1 --no-build --no-restore`: **362 passed, 0 failed, 0 skipped** (Hardware 31, Lighting 95, Router 68, Service 73, App 95).
- Coverage includes every effect at fixed times, zero-speed freezing, brightness/device disabling, nonuniform palette interpolation, map rotation/flip, independent ARGB placement and lamp sampling, matrix empty cells, shared wave phase, local load-bar mapping and frame-rate-independent smoothing, temperature/screen zones, synthetic audio frequency analysis, event priority/toggles/fades, overlays on exact colours, runtime lifetime, capture telemetry through doubles, invalid commands without mutation, old payload defaults, persistence and scene round trips.
- Built the detached preview project with **0 warnings, 0 errors**. Rendered **94 PNGs**, including all effects at two fixed times, wide/narrow gallery layouts, the map editor, RAM/GPU/board/strip/overlay/matrix variants, and the existing pages/widgets/controls. Visually checked the workspace, narrow stacking, floating toolbar, readable toggles, effect colours, overlay and matrix layouts. `bindings.log` is empty.
- Preview output: `tests/Prism.App.Tests/obj/preview-codex-6/`. Reproduce with:

  ```powershell
  dotnet restore Prism.sln -m:1 --ignore-failed-sources -p:NuGetAudit=false
  dotnet build Prism.sln -m:1 --no-restore -p:NuGetAudit=false
  dotnet test Prism.sln -m:1 --no-build --no-restore
  dotnet build tests/Prism.App.Tests/Preview/Prism.App.Preview.csproj -m:1 -p:NuGetAudit=false --ignore-failed-sources
  dotnet run --project tests/Prism.App.Tests/Preview/Prism.App.Preview.csproj --no-build -- tests/Prism.App.Tests/obj/preview-codex-6
  ```

- NuGet's vulnerability endpoint was unavailable in this restricted environment. Dependencies restored from the existing cache with audit disabled for these commands; no repository audit setting was changed. Native capture adds no package dependency.
- `git diff --check`: no whitespace errors. No temporary WPF project files remain.

All runtime verification used fake adapters, fixture streams, pure input data or the detached offscreen mock client. No real hardware, running services, registry, router or lamp was touched. The service/app production entry points and native capture workers were not launched. No live integration claims are made.

Native capture references: [WASAPI loopback initialization](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize), [capture buffer lifecycle](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer), [Desktop Duplication API](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api).
