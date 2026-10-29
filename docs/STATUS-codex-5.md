# Codex round 5 status

Implemented the round 5 brief. Protocol version 1 and `PipeContract.cs` are unchanged.

## Service and lighting

- Appended `LightingEffect.Custom`; updated effect rendering, labels, and preview handling.
- Added persisted per-device custom and RGBA overlay layers. Exact mode uses the saved custom colours; overlay mode composites over the running effect in the existing 30 fps loop. Global Custom also selects each device's custom colours. Exact colours retain their saved RGB values independently of global effect brightness.
- Implemented and validated all six new commands: SetLedColors, ClearLedColors, ResizeZone, SetLampToken, SetRouterPassword, and SetServiceOptions.
- Resize serializes against RGB output, calls ResizeZoneAsync then RefreshAsync and direct mode, persists device/zone sizes, and trims or pads layers with black/transparent. Saved zone sizes are reapplied during every OpenRGB reconnect before output resumes.
- Projected zone types, offsets, limits, LED names, and matrix maps (including empty cells) into device state.
- Credential commands reuse LampTokenStore and CredentialStore with their existing CurrentUser DPAPI protection. Secrets stay out of ordinary settings, state, responses, and command logs. Credential changes trigger adapter reconnection; router enable/host changes are applied by the polling loop. OpenRGB launch options apply on connection attempts.

## App

- Lighting now opens with Edit LEDs; Ctrl+Space search includes an Edit LEDs command.
- Device and strip-zone tabs, vertical RAM layouts, zone rows and matrix grids, glowing dots, and edit animations respect the existing graphite theme and reduced-motion setting.
- Brush drag, zone/device fill, directional two-colour gradients, eyedropper, overlay eraser, range selection/application, colour wheel, hex inputs, recent swatches, brightness, and Custom/Overlay modes are available.
- Painting sends SetLedColors after a 60 ms debounce. Pending edits are flushed before resizing; removed LED colours cannot return from stale drafts after shrinking and growing a zone.
- Resizable zones expose a ThickSlider, numeric entry, and Resize zone action.
- Named device layouts save/load through `%APPDATA%\Prism\led-presets.json`. Scenes can save a layout-name reference per scene in local app settings for future automation; no automatic preset application was added.
- Lamp host/token and router password forms use password boxes, clear the secret on submission/unload, and send the provisioning commands. Settings exposes OpenRGB path/launch and router enable/host with Save service options.
- Mock devices are RAM 1 and RAM 2 (10 LEDs each), GPU (4), and board (2) with an ARGB zone initially at 0 LEDs and resizable to 120. Mock credential commands do not save secrets or contact devices.

## Verification

- `dotnet build Prism.sln -m:1`: succeeded, **0 warnings, 0 errors**.
- `dotnet test Prism.sln -m:1`: **300 passed, 0 failed, 0 skipped** (Hardware 31, Lighting 52, Router 68, Service 57, App 92).
- Added coverage for alpha blending, exact colours, resize padding/truncation, command validation and persistence, fixture zone/matrix projection, secret exclusion from state/responses/trace, fake-runtime compositing, fixture-stream resize/refresh/direct-mode ordering, mock layouts, gradients, erasing, preset round trips, command search, and pending-edit resize handling.
- Built and ran the detached offscreen preview. It renders all existing pages/widgets plus RAM, GPU, board, resized strip, overlay, and matrix editor variants. Visually checked editor and credential renders and corrected field visibility and the restored mode selector. `bindings.log` is empty.
- Preview output: `tests/Prism.App.Tests/obj/preview-codex-5/`. Reproduce with:

  ```powershell
  dotnet build tests/Prism.App.Tests/Preview/Prism.App.Preview.csproj -m:1
  dotnet run --project tests/Prism.App.Tests/Preview/Prism.App.Preview.csproj --no-build -- tests/Prism.App.Tests/obj/preview-codex-5
  ```

- `git diff --check`: no whitespace errors.

All runtime verification used test doubles, fixture streams, or the offscreen mock client. No real hardware, running services, registry, router, or lamp was touched. No commit was created. The pre-existing round 6 brief and live-test files were left untouched. Live integration behaviour remains unverified, as required by the task boundaries.
