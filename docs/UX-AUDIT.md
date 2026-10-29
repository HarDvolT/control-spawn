# Prism UX audit — from the user's first real run (2026-10-29)

The user's verdict: "the UI/UX experience is bad, not well structured". Screenshots of Monitor, Devices, Lighting and
Lamp from the live app. Target quality: the approved design canvas (docs/design/*.dc.html) and the Vaulto-style
graphite reference — premium, glowing, animated, instantly responsive. This is the bar for every page.

## Global problems
1. **Unbalanced cards and type.** Card sizes are arbitrary (Monitor cards are huge with mostly empty space; Fans card is
   cut off at the bottom). Text sizes jump around: tiny 11 px labels next to huge numbers, inconsistent headings.
   → One type ramp (Display 32 / Title 20 / Heading 15 / Body 13 / Caption 11), one spacing scale (4/8/12/16/24/32),
   a 12-column responsive card grid with fixed card height classes (S 140 / M 220 / L 320) so rows line up.
2. **Controls are not instant.** Changing a lamp or lighting setting lags: the UI waits for the 1 Hz state push.
   → Optimistic UI (the control moves and the preview changes immediately), the service applies the command at once and
   pushes a fresh state right after every command (not on the next tick), sliders send while dragging (throttled ~30 ms,
   latest-value-wins) and commit on release. Real lamps must update within ~100 ms of a change (rate limits permitting).
3. **Raw developer values leak into the UI:** "True", "miio", "own", "RgbController", "FanHeader", device ids like
   `rgb:6C0BBAA12F08...:0`, "ARGB / 0 LEDs", model strings `yeelink.light.color5` as names. → Human labels, icons and
   badges everywhere; ids only in an "Advanced" drawer.
4. **No status language.** Nothing is colour-coded. → Shared status scale: cool/ok (blue→green), warm (amber), hot
   (red, pulsing glow), with thresholds per sensor (e.g. CPU Tctl 75/85 °C, GPU hotspot 90/100 °C, load 70/90 %).
   Every metric shows its state with colour + shape, never text alone.

## Monitor (bad)
Cards far too large; values are plain text rows; per-thread load is a row of odd dots; fans card cut off.
→ Dense dashboard of gauges: radial gauges with glow for CPU/GPU load and temperature, arc speedometers for clocks
and fan RPM, thermal bars with the status colour, sparkline per metric, a 16-thread heat-grid tile (Vaulto "net
cashflow" style), VRAM/RAM as segmented capsules, power as a live wattage meter, alert chips ("GPU hotspot hot").
Everything animates smoothly between 1 Hz samples. Must fit on one 1080p screen without scrolling at default size.

## Devices (just a list)
"15 new devices found" although they are already set up; Set up/Dismiss on working devices; raw kind headers; ids.
→ Visual device gallery: a card per device with an icon/illustration (RAM stick, GPU, board, strip, lamp bulb, fan,
router, air fryer), live status dot and a mini live readout (colour swatch, RPM, temperature, signal), rename inline,
grouped into "This PC", "Lights", "Home" (lamps, fryer), "Network". "New" only for genuinely unconfigured devices.

## Lighting (too hard)
Hard to understand what is selected, what a control affects, and how the case map relates to the devices; layout is
complicated; the right column is a long list of sliders; "ARGB / 0 LEDs"; **no way to set the whole case at once**.
→ Three clear levels, top to bottom:
  1. **Whole case** hero: one big colour wheel + brightness + effect chips that apply to EVERYTHING (all RGB devices
     and both lamps) instantly. "Sync all" is the default mode.
  2. **Effects** gallery with live animated previews; selecting one shows only that effect's 2–4 relevant controls.
  3. **Per device** (optional, behind "Customise devices"): device chips; choose follow-whole-case / own effect /
     sensor-driven / paint LEDs. LED painting opens a focused editor.
  The case map is a clear, realistic illustration of the user's case (front fans, rear fan, radiator, RAM, GPU,
  strip, both lamps beside the case) that previews live; "Arrange" is an advanced mode, not on by default.
  The strip with 0 LEDs shows a friendly "How many LEDs does your strip have?" prompt.

## Lamp (a list of controls)
Lamps are not separated; a raw table ("True miio 192.168.0.8"); one shared detail panel; "own" dropdown; hex text;
token form always visible.
→ One card per lamp, side by side, each with a glowing bulb illustration in its current colour, big power toggle,
brightness ring/slider, colour wheel + swatches, follow mode as icon segmented control (Own / Same as PC / Average),
per-lamp rename. Both lamps controllable together ("Both lamps") too. Setup (token, find lamps) moves to a
collapsed "Add or fix a lamp" panel. Changes reach the bulb immediately.

## Others to bring up to the same bar
Dashboard, Fans (curve editor already decent), Scenes, Modem, Widgets, Settings, Air fryer — same grid, type ramp,
status colours, instant controls.
