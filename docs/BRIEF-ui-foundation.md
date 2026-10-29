# Brief (Codex, phase A): UI foundation + instant controls + ARGB setup

Read docs/UX-AUDIT.md first: it is the user's verdict on the live app and the quality bar. Same rules as every round:
no real hardware/services/registry/router/lamps/fryer, no commits, `dotnet build Prism.sln -m:1` 0 warnings,
`dotnet test Prism.sln -m:1` green (460 tests today). Render with the offscreen preview and look at every page.

This phase prepares the app so several agents can then redesign pages **in parallel without touching the same files**.

## 1. Split the views (mechanical, keep behaviour identical)
`src/Prism.App/Views/PageTemplates.xaml` holds every page. Split it into one file per page under
`src/Prism.App/Views/Pages/<Page>.xaml` (Dashboard, Monitor, Devices, Widgets, AirFryer, Lighting, Fans, Lamp, Scenes,
Modem, Settings), merged from App.xaml. Move page-specific code-behind/helpers (LightingWorkspace, LedEditor,
SensorBindingPanel, DevicesWorkspace, AppliancesWorkspace, TokenImportPanel…) next to their page. Shared controls stay in
`Controls/`. After this, a page agent edits only its own folder.

## 2. Design system (Themes/Prism.xaml + Controls/)
- Type ramp resources: Display 32 / Title 20 / Heading 15 / Body 13 / Caption 11 (Inter; numbers Space Grotesk,
  tabular). Spacing scale 4/8/12/16/24/32. Radius 12/16/20.
- `CardGrid` panel: 12 responsive columns, card height classes S 140 / M 220 / L 320 so rows line up; `GlassCard`
  gains Size and Span.
- Status language: `StatusLevel` (Cool, Ok, Warm, Hot, Critical) with brushes (blue, green, amber, red, red+pulse) and a
  `Thresholds` table per sensor kind (CPU Tctl 75/85 °C, GPU edge 80/90, GPU hotspot 90/100, DIMM 50/60, NVMe 60/70,
  load 70/90 %, ping 60/120 ms…). Converter metric→level→brush/glow.
- New shared controls, animated with glow, respecting Reduce animations: `RadialGauge` (value arc + status colour +
  ticks + tweening number), `ArcSpeedometer` (clocks, RPM, needle), `ThermalBar` (gradient track, status-coloured fill,
  threshold ticks), `HeatGrid` (per-thread tiles), `SegmentedCapsule` (RAM/VRAM), `Sparkline` (tiny trend),
  `StatusChip` ("GPU hotspot hot" with pulse), `DeviceIcon` (vector icons: RAM, GPU, board, strip, fan, lamp, router,
  fryer, network), `ColorWheel` (large, smooth, instant), `IconSegmented`.
- Replace any raw values shown to users ("True", "miio", "own", enum names, ids) with label converters.

## 3. Instant controls (the user's #1 complaint after visuals)
- App: optimistic UI — controls and previews update immediately; `IPrismClient` exposes `SendLatestAsync(key, cmd, args)`
  that coalesces rapid changes (slider drag) to at most one send per ~30 ms with latest-value-wins and always sends the
  final value on release. Roll back only if the service rejects.
- Service: after every accepted command, push a fresh StateMessage to all subscribers immediately (not on the next
  1 Hz tick). Lighting/lamp changes take effect on the next 30 fps frame; lamps get the new colour within ~100 ms
  (miIO allows ~2/s steady — send the latest immediately, then pace).
- Tests for coalescing, final-value delivery, immediate push after command, rollback on rejection.

## 4. ARGB header setup (case RGB doesn't light)
The board's ARGB zone (where the case fans' rings and strip connect) starts at **0 LEDs** in OpenRGB, so the user's case
RGB stays dark. Add a service command `identifyZone` { device, zone, size } that resizes and lights the zone with a moving
white marker + LED index, and an app helper "Set up case lighting": explain, set to max (120) to light everything,
then let the user lower the count until the last LED of the chain goes dark (or type the known count: N fans × LEDs per
fan). Remember the size (existing ZoneSizes). If the board exposes more than one ARGB zone, list each.

## 5. Whole-case control (foundation for the Lighting page)
Service command `setAllLighting` { effect?, color?, brightness?, speed? } that applies to every RGB device **and both
lamps** at once (lamps switch to "Same as PC"), clearing per-device overrides only if `resetOverrides: true`.

Finish with docs/STATUS-ui-foundation.md listing the file map so page agents know what they own.
