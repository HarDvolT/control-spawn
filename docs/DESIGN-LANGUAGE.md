# Prism design language — "Graphite HUD"

Owner: the lead (Claude). Agents implement this; they do not invent their own styles. Mockups: the Design canvas
(artboards "v2 · …"). When a mockup and this file disagree, the mockup wins for layout, this file wins for tokens.

## 1. Principles
1. **One glance, one action.** Every card answers one question ("Is my GPU OK?") and offers at most one primary action.
2. **Direct manipulation.** Touch the thing itself: drag a colour onto the case, drag a fan curve point, tap a bulb to
   toggle it. Panels of sliders are the fallback, not the interface.
3. **Instant.** Every control reacts in the same frame; hardware follows within ~100 ms. Never wait for a refresh.
4. **Progressive disclosure.** Simple by default (Whole case, Both lamps, Fan mode); detail one click deeper
   ("Customise", "Advanced"). Setup forms live in collapsed drawers.
5. **Status before numbers.** Colour + shape say Cool / Ok / Warm / Hot before you read a digit.
6. **Alive, never noisy.** Live things move (tweening numbers, spinning fan rotors, animated LED previews). Chrome stays
   still. Only a critical reading pulses.

## 2. Tokens
Colour
- Ground `#07080A`, plain. No dot grid, no light washes.
- Surfaces: Glass 1 `#111216` (cards), Glass 2 `#17181D` (raised), Glass 3 `#1E2026` (controls). 1 px hairline
  `rgba(255,255,255,.07)`. Depth comes from surface steps and hairlines, not glows or inner highlights.
- Text: Primary `#F4F5F7`, Secondary `#A3A6AE`, Tertiary `#7C808A` (all ≥ 4.5:1 on Glass 1).
- **Accent (action / brand): Ember `#FF4D1A`** — primary buttons, selection, active toggles.
- **Data (cool readouts): Ion `#36D6FF`** — charts, gauges in the Cool/Ok range, focus rings. Gives the futuristic
  two-tone: warm = you, cool = machine.
- Status scale: Cool `#36D6FF` · Ok `#3DDC84` · Warm `#F5A524` · Hot `#FF4D4F` · Critical `#FF4D4F` + 1.2 s pulse.
- No glows on chrome (toggles, gauges, sliders, swatches, logo). Light only appears where it is real light: LED strips,
  the case twin and the lamp orbs.

Type
- UI: Segoe UI Variable Text. Numbers: Segoe UI Variable Display, tabular. Section labels: 12/500, sentence case.
- Weights: 400 and 500; 600 only for primary buttons and small emphasis. No bold headings.
- Ramp: Display 40 (hero numbers) · Title 22 · Heading 15/600 · Body 13 · Caption 11.

Shape & space
- Radius: cards 12–16, controls 8–10, tags 4–6. Nothing is a full pill except the toggle track. Spacing 4/8/12/16/24/32. 12-column grid, 16 px gutter, 24 px page padding.
- Card heights: S 132 · M 208 · L 320 · XL 456 — rows always align.

Motion
- Ease-out `cubic-bezier(.22,1,.36,1)`; values tween 600–800 ms; entrance = fade + 12 px rise, 40 ms stagger;
  hover = surface or hairline step, no lift; press = 0.97 scale. Entrance stagger covers at most six cards. Reduce animations → no movement, keep colour.

## 3. Signature components
- **Halo gauge**: 270° arc, segmented ticks, value arc in status colour with glow, big Space Grotesk number in the middle,
  tiny sparkline under it. Used for load / temperature.
- **Speed dial**: 220° arc with a glowing needle for clocks and RPM.
- **Thermal rail**: thin vertical/horizontal bar with gradient track (Ion→Ok→Warm→Hot) and a bright marker at the value;
  threshold ticks.
- **Heat grid**: 16 thread tiles that fill and tint with load.
- **Capsule meter**: segmented pill for RAM/VRAM/disk use.
- **Orb**: the lamp bulb / colour preview — a soft glowing sphere in the live colour; tap to toggle.
- **Case twin**: a clean vector illustration of the user's PC (3 front fans + rear fan + top radiator rings, 2 RAM sticks,
  GPU bar with TAICHI badge, board logo, strip) where every LED glows live. Click a part to select it.
- **Hue ring**: large colour wheel with brightness arc around it; swatches below; drag = instant.
- **Effect tile**: 1:1 card with a tiny live animated preview of the effect on the case twin silhouette.
- **Status chip**: pill with dot + label ("GPU hotspot · hot"), pulses when Critical.
- **Mode switch**: big segmented control with icons (Silent / Balanced / Turbo / Full).

## 4. Copy
Plain words, sentence case, no developer values. Devices get friendly default names: "RAM stick 1", "Graphics card",
"Motherboard", "Case fans & strip", "Desk lamp", "Bedside lamp", "Air fryer". Units always shown (°C, %, rpm, W, GHz).

## 5. Must-keep power features (user, 2026-10-29)
The v2 Lighting page is simpler on the surface, but nothing from v1 is removed:
- **Per-LED painting** (brush, fill, gradient, eyedropper, eraser, range, Exact/Overlay layers, presets) lives under
  Lighting → "Per device" → pick a device → "Paint LEDs". The case twin becomes the canvas.
- **Arrange the case map** (drag, **rotate**, flip every device and ARGB zone, move the lamps' sample points) lives under
  Lighting → "Per device" → "Arrange case", so spatial effects (Aurora, Comet, Rain, Ocean, waves…) flow in the right
  direction across the real case. Rotation in 15° steps plus free angle; snap to the case outline; live preview while
  dragging; Reset to default layout.
- Sensor-driven lighting lives under the "Sensor" mode chip.
Page agents must carry these over with the new components, not drop them.

## 6. Unslop pass (2026-11)
The app was cleaned up following [unslop](https://github.com/heydamir/unslop) by Damir Sokolovsky (CC BY 4.0).
Direction:
- **Product.** One Windows app that runs a gaming PC's fans and lights and the devices in its room.
- **Character.** Exact, calm, a little warm.
- **World.** The inside of the case at night: graphite, hairlines, and the real LED light.
- **Moments.** The live LED strips, the case twin and the lamp orbs carry the colour. Status colours mean status.
- **Signature.** The steady status dot.
Rules that came out of it: sentence case everywhere (no tracked caps), one ember accent for action and selection,
no hue gradients or glows on chrome, no greeting headlines, no arrows in link text, stat cards hold a reading and
nothing else. Fan safety cards and warnings keep their status colours and stay on the Fans page.
