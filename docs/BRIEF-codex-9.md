# Brief for Codex, round 9: data-driven lighting per device ("sensor lighting")

After round 8. Same rules: no real hardware/services/registry/router/lamps/fryer, no commits, 0 warnings, all tests green.

User request: "set the RGB of the GPU to show colours based on its thermals or power usage or its usage". Generalise
the existing Load meters / Temperature flow into a **Sensor lighting** effect that any device (or zone, or lamp) can use
on its own while other devices keep running their normal effect.

## Per-device binding
For each device/zone/lamp, optionally bind: { source, range min..max, style, palette/colours, smoothing, alert }.
- **Sources** (from the service's live state): GPU temperature, GPU hotspot, GPU memory junction (if present), GPU load,
  GPU power (W), GPU clock, VRAM use %, GPU fan RPM; CPU temperature, CPU load, CPU power, CPU clock, per-core max load;
  RAM use %, DIMM temperature; board temperature; any fan RPM; network down/up; disk activity/temperature; router signal
  score and ping. Unavailable source → device falls back to its normal effect and the UI says why.
- **Range:** sensible defaults per source (GPU temp 35–85 °C, hotspot 40–100 °C, GPU power 15–300 W for the RX 9070 XT,
  load 0–100 %), editable, with "auto-learn" (track the observed min/max over the session).
- **Styles:** Colour gradient (whole device follows a 2–6 stop palette, default blue → green → amber → red), Fill bar
  (LEDs fill along the device with a bright tip — current Load meters), Breathing speed (pulse faster as the value rises),
  Brightness (dim → bright), Threshold (one colour below, another above, with hysteresis).
- **Smoothing:** 0–5 s exponential so values don't jitter; GPU writes still obey the GPU pacer (10 fps) and bus turns.
- **Alert:** optional threshold that overrides with a red pulse (e.g. GPU hotspot ≥ 95 °C), independent of the fan failsafe.
- Several devices can bind different sources at once (GPU → GPU temp, RAM → CPU load, strip → GPU power, lamp → ping).

## Contract (additive)
LightingState gains `Bindings` (per device/zone/lamp id); new command `setSensorBinding` { target, source?, min, max,
style, palette, smoothing, alert? } (source null = remove binding). Validate ranges and source names.

## App
On the Lighting page each device tab gets a "Driven by a sensor" section: source picker (grouped CPU/GPU/RAM/…), live
value with the range shown as a gradient bar and a marker, style segmented control, palette editor, min/max ThickSliders,
"Auto-learn range" toggle, smoothing, alert. Presets: "GPU temperature", "GPU power", "GPU usage", "CPU temperature",
"Ping". The case-map preview shows the result live (mock data animates).

## Tests
Mapping value→colour for each style at fixed values, clamping, auto-learn, smoothing, hysteresis, alert priority over
the binding but below the fan failsafe event, fallback when the sensor is missing, validation.

Finish with docs/STATUS-codex-9.md.
