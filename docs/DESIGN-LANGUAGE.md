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
6. **Alive, never noisy.** Subtle motion everywhere (breathing glows, tweening numbers, flowing gradients); loud motion
   only for alerts.

## 2. Tokens
Colour
- Ground `#07080A` with a radial "light from above" `rgba(255,255,255,.06)`; faint 24 px dot grid at 3 % (HUD feel).
- Surfaces: Glass 1 `#111216` (cards), Glass 2 `#17181D` (raised), Glass 3 `#1E2026` (controls). 1 px hairline
  `rgba(255,255,255,.07)`, inner top highlight `rgba(255,255,255,.05)`, outer shadow `0 20 40 rgba(0,0,0,.5)`.
- Text: Primary `#F4F5F7`, Secondary `#A3A6AE`, Tertiary `#7C808A` (all ≥ 4.5:1 on Glass 1).
- **Accent (action / brand): Ember `#FF4D1A`** — primary buttons, selection, active toggles.
- **Data (cool readouts): Ion `#36D6FF`** — charts, gauges in the Cool/Ok range, focus rings. Gives the futuristic
  two-tone: warm = you, cool = machine.
- Status scale: Cool `#36D6FF` · Ok `#3DDC84` · Warm `#F5A524` · Hot `#FF4D4F` · Critical `#FF4D4F` + 1.2 s pulse.
- Glow = the element's colour at 45 % with 14–24 px blur; only on live/active things.

Type
- UI: Inter. Numbers & readouts: Space Grotesk, tabular, tight tracking. Micro-labels: Inter 11/600 caps +0.12em.
- Ramp: Display 40 (hero numbers) · Title 22 · Heading 15/600 · Body 13 · Caption 11.

Shape & space
- Radius: cards 20, controls 12, chips 999. Spacing 4/8/12/16/24/32. 12-column grid, 16 px gutter, 24 px page padding.
- Card heights: S 132 · M 208 · L 320 · XL 456 — rows always align.

Motion
- Ease-out `cubic-bezier(.22,1,.36,1)`; values tween 600–800 ms; entrance = fade + 12 px rise, 40 ms stagger;
  hover = lift 2 px + hairline brightens; press = 0.97 scale. Reduce animations → no movement, keep colour.

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
