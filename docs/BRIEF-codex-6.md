# Brief for Codex, round 6: creative lighting modes and animations

Builds on round 5 (per-LED layers, zones). Same rules: no real hardware/services/registry/router/lamp,
no commits, build 0 warnings, all tests green. Areas: src/Prism.Lighting, src/Prism.Service, src/Prism.App,
their tests, and additive contract changes in PipeContract.cs listed in docs/STATUS-codex-6.md.

## 1. Spatial "PC map" (makes animations flow across the whole case)
Each LED gets a 2-D position (0..1) on a case map: RAM 1 and RAM 2 as two vertical columns, GPU as a short
horizontal bar, board LEDs, the ARGB strip as a line (user can drag/rotate/flip each device on a map editor
in the app; sensible default layout for the user's tower). Effects render from position, not LED index,
so a wave really travels from the GPU up through the RAM to the strip, and the lamp samples the colour at
its own map point (default: the strip's centre).

## 2. Effect engine v2
Every effect: palette (built-in palettes + custom 2–6 stops), speed, direction/angle, intensity/scale,
brightness, per-device enable, and a deterministic `Render(time, positions)` for tests. New effects:
- **Aurora** – slow layered noise ribbons in greens/teals/violets
- **Comet** – bright head with fading tail, travels along the map path; count & tail length
- **Fire / Lava** – flicker noise rising upward, warm palette
- **Ocean ripple** – rings expanding from a point, palette blues
- **Plasma** – smooth 2-D noise colour field
- **Scanner** – Larson/KITT bounce across the map
- **Rain / Matrix** – drops falling down the RAM sticks and strip
- **Heartbeat** – double pulse, rate optionally tied to CPU load
- **Gradient flow** – palette scrolling at an angle
- **Candle** – soft warm flicker (good with the lamp)
- **Sparkle** – twinkles over a base colour (upgrade of Starry night)
- Keep existing ones (Static, Breathing, Strobing, Color cycle, Rainbow wave, Smart, Music, Adaptive, Off)
  and move them onto the map.

Reactive / data-driven modes (the control-center angle):
- **Load meters** – RAM sticks become CPU-load bars (green→amber→red), GPU LEDs show GPU load, strip shows
  RAM use; smooth animated fill with a bright tip.
- **Temperature flow** – Smart v2: colour from CPU/GPU temps per device, with a slow breathing shimmer.
- **Audio spectrum** – strip/RAM as an equaliser, beat flash; behind an `IAudioLevels` interface with a
  WASAPI loopback implementation (code only; not run in tests).
- **Screen ambient** – Adaptive v2 with left/centre/right screen zones mapped onto the case map
  (desktop duplication behind an interface; not run in tests).

Event layer (short animations over any effect, priority based, then fade back):
- ping spike / router signal poor → amber/red ripple; fan failsafe → red strobe on all devices;
  scene change → white sweep; "notification" command for future use. Each event togglable in Settings.

## 3. Contract (additive)
LightingState gains effect parameters (palette stops, angle, scale, intensity, per-device enable) and the
map layout; new commands `setEffectParams`, `setLightingMap`, `setEventEffects`. Keep `setLighting` working.

## 4. App
Lighting page: effect gallery with **live animated previews** (each card renders the effect on a mini case
map at 30 fps), parameter panel (palette editor with draggable stops, speed/angle/intensity ThickSliders,
direction dial), map editor (drag devices on a case silhouette), events section with toggles, and a big live
preview of the whole PC. Scenes can store an effect + params. Respect "Reduce animations".

## 5. Tests
Determinism per effect at fixed times, palette interpolation, map sampling (lamp point, flipped device),
event priority/fade, load-meter mapping, parameter validation. Render the new gallery and editor offscreen
and check them. Finish with docs/STATUS-codex-6.md.
