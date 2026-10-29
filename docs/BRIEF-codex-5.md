# Brief for Codex, round 5: per-LED lighting + the missing settings commands

The lead extended `src/Prism.Core/Ipc/PipeContract.cs` (additive, protocol 1): commands SetLedColors,
ClearLedColors, ResizeZone, SetLampToken, SetRouterPassword, SetServiceOptions; `RgbDeviceState` gained
Zones / CustomColors / OverlayColors and the new `RgbZoneState`. Read it first; do not change existing
shapes. Same rules as every round: never touch real hardware, services, registry, router or lamp; no commits;
`dotnet build Prism.sln -m:1` 0 warnings and `dotnet test Prism.sln -m:1` green (267 tests today).

Verified live on the user's PC through OpenRGB 1.1: 2x Corsair Vengeance RGB DDR5 (10 LEDs each), ASRock
RX 9070 XT Taichi (4 LEDs), ROG STRIX B850-F (board LEDs + an ARGB header that starts at 0 LEDs and must be
resized; the user has a strip on it). `OpenRgbClient.ResizeZoneAsync` and `KindName` already exist.

## Service / lighting (src/Prism.Lighting, src/Prism.Service, src/Prism.Core models if needed)

1. Add `LightingEffect.Custom` (append to the enum) and make every switch handle it.
2. Per-device LED layers in settings (persisted): `custom` colours (device shows exactly these when its
   effect is Custom or when the global effect is Custom) and `overlay` colours with alpha, composited over
   whatever effect is running (alpha blend per LED, 30 fps). Lengths must match the device's current LED
   count; after a resize, keep the first N and pad new LEDs with black / transparent.
3. Implement the six new commands with validation. ResizeZone → `ResizeZoneAsync`, then `RefreshAsync`,
   re-enter direct mode, persist the size per device id + zone and re-apply it on every reconnect (OpenRGB
   forgets sizes unless its own profile saves them). SetLampToken / SetRouterPassword store DPAPI-encrypted
   (reuse the existing stores), never log or echo secrets, then (re)connect. SetServiceOptions persists and
   applies (OpenRGB launch path, router enable/host).
4. Fill `RgbDeviceState.Zones` from the OpenRGB controller data (zone name, type, first LED index, count,
   min/max, LED names, matrix map) and Custom/OverlayColors from settings.
5. Tests: compositing maths, resize padding, command validation, zone projection from a fixture controller,
   secrets never appear in state JSON or logs.

## App (src/Prism.App)

Lighting page gains a **per-LED editor** (Ctrl+Space command "Edit LEDs"):
- Device tabs (RAM 1, RAM 2, GPU, Board, LED strip zone…). Each device drawn as its real layout: zones as
  rows of LED dots (matrix zones as grids using the Matrix map), RAM sticks as vertical bars of 10, GPU 4.
- Tools: brush (click/drag to paint LEDs), fill zone, fill device, gradient (pick 2 colours, drag across),
  eyedropper, eraser (overlay → transparent), select range + apply colour. Colour wheel + hex + recent swatches
  + brightness. Mode toggle per device: "Exact colours (Custom)" vs "Paint over effect (Overlay)".
- Live: edits send SetLedColors debounced (~60 ms) so the real LEDs follow the brush.
- Zone size control for resizable zones ("LED strip: 30 LEDs") with a ThickSlider + number box → ResizeZone.
- Presets: save/load named LED layouts per device locally (%APPDATA%\Prism\led-presets.json), and the
  scene editor can reference them later (store the name in the scene; no automation needed now).
- Also expose the other new commands in the UI: Lamp page "Mi Home token" (host + token, password box,
  Save → SetLampToken), Modem page "Router password" (Save → SetRouterPassword), Settings page OpenRGB path /
  launch / router enable+host → SetServiceOptions. Replace the "cannot provision" notes.
- Mock client: realistic zones for the user's devices (RAM 10+10, GPU 4, board 2 + ARGB 0→resizable to 120).
- Animated, glowing LED dots consistent with the graphite theme; render the new editor in the offscreen
  preview and check it.

Finish with docs/STATUS-codex-5.md.
