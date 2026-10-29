# Brief (OpenCode, phase A): page-by-page UX spec — documentation only

Read docs/UX-AUDIT.md (the user's verdict on the live app), docs/ARCHITECTURE.md, docs/HARDWARE.md and the approved
design mockups docs/design/*.dc.html. Write **docs/UX-SPEC.md** only. Do not edit any code or other file.

For each page — Dashboard, Monitor, Devices, Lighting, Lamps, Fans, Scenes, Modem, Air fryer, Widgets, Settings — write:
1. **Purpose** in one sentence and the 3 most important tasks the user does there.
2. **Layout** for a 1920×1080 window on a 12-column grid: list every card with its column span and height class
   (S 140 / M 220 / L 320 px), in reading order, plus how it collapses below 1100 px width. Must fit without scrolling at
   1080p where the audit says so (Monitor, Dashboard).
3. **Components** per card, using the shared set: RadialGauge, ArcSpeedometer, ThermalBar, HeatGrid,
   SegmentedCapsule, Sparkline, StatusChip, DeviceIcon, ColorWheel, IconSegmented, ThickSlider, ToggleSwitch,
   GlowProgressBar. Say which metric drives which status colour and the thresholds.
4. **Interactions**: what happens on click/drag, what updates instantly, empty/offline/error states, first-run states.
5. **Copy**: every visible label in plain English (no raw values like True, miio, own, enum names, ids).
6. **Motion**: entrance, value tweening, glow pulses for hot/critical states.

Lighting must have three levels: Whole case (one colour/effect for every RGB device and both lamps) → Effect gallery
with live previews (each effect shows only its 2–4 relevant controls) → optional per-device customisation (follow /
own effect / sensor-driven / paint LEDs). Include a "Set up case lighting" first-run flow for the ARGB header that
starts at 0 LEDs (the user's case fans and strip are on it).
Lamps: one card per lamp side by side (bulb illustration glowing in its colour, power, brightness, colour wheel,
follow mode), plus "Both lamps" control and a collapsed "Add or fix a lamp" setup panel.
Devices: a visual gallery grouped This PC / Lights / Home / Network with icons and live mini readouts.

Be concrete and complete enough that four separate engineers can each build pages from it without guessing.
