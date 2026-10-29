# v2 pages status

Updated 2026-10-29 on `ui/v2-pages`. The previously committed shell and eleven pages have been refined, the pending view-model changes completed, and `.v2-pages.py` removed. No push or main-branch changes.

All eleven pages were rendered and visually compared with their corresponding `docs/design-v2/V2*.png`. This is **not a pixel-exact design sign-off**: the remaining visual and service gaps are recorded below. Sample readings, device identities, histories and unsupported actions have not been copied into the production UI to make a screenshot look complete.

## Shared work

- Kept the graphite shell, sectioned navigation, command search, notifications, bundled Inter/Space Grotesk resources and reduced-motion support. Corrected search width, navigation spacing, service device count and normal-status copy.
- Completed gradient scene backgrounds, live gauge status/history, GPU load/temperature dashboard readings, machine identity and appliance summaries.
- Added configurable 270-degree gauge arcs, a hue-ring option and shaded lamp orbs. Corrected button content alignment, selected mode colours, card backgrounds and equal-height rows.
- Removed blank rename-error space while preserving rename/reset, draft editing and command handling.
- Restored the missing Lamps list styles that previously caused a runtime XAML exception.

## Page comparison and remaining gaps

All dimensions below are the actual reference PNG dimensions, rather than a common assumed viewport height.

| Page / reference | Size | Built and corrected | Remaining differences and reasons |
| --- | --- | --- | --- |
| Dashboard / V2Dashboard | 1440 x 900 | Gradient scene buttons and state badges; four partial-arc sensor cards; compact lighting and fan controls; lower lamp, internet and fryer cards. | No fabricated sparklines before enough service samples arrive. Hardware summary uses machine identity and actual device counts because processor/board marketing names are not supplied as a complete machine summary. Lighting swatches and top thermal chips now use the reference treatments and live commands/readings. Service sample history is available over IPC; Internet uses ping history rather than CPU history. Further gauge, LED/orb, signal-bar and typography matching is in progress. |
| Monitor / V2Monitor | 1440 x 1040 | Processor and graphics gauge pairs; live stats and threads; memory/temperature rails; fans, network, disks, internet and lower summary cards. | Process CPU deltas and OS uptime are now supplied by the service. Explicit thermal-throttling sensors are projected when present; event counts remain unsupported. Mini-cards, 16-segment meters, gradient rails, thread tiles and needle dials replace the simplified rows/bars. Gauge ticks/labels, disk composition and typography still need final matching. |
| Devices / V2Devices | 1440 x 1160 | Four-column cards, grouped PC/home/network/appliance inventory, live counts, discovery/import actions, setup/dismiss and renaming. | Inventory is the actual registry, including unresolved devices. Featured discovery banners, tinted icon tiles, upper-right statuses and separated sensor/colour footers are implemented. Inline rename now uses a small pencil; reset remains in its context menu. Actual extra discoveries can extend the page beyond the sample inventory; icon paths and detailed footer coverage still need matching. |
| Lighting / V2Lighting | 1440 x 940 | Whole-case/per-device/sensor modes, larger live case map, hue ring, effect gallery beside the colour controls, ARGB setup and advanced controls. | Added the reference case illustration for whole-case mode, amber setup banner and nine gradient effect tiles beside hue/swatch/brightness controls. The editable spatial canvas and additional effects remain available in per-device/advanced controls. Illustration glow, hue-ring handle and device-chip styling still need final matching. |
| Lamps / V2Lamps | 1440 x 1000 | Shared power/brightness bar; independent shaded orbs, brightness presets, colour swatches and follow selectors; compact custom-colour disclosure; setup/token editor retained. | Saved moods and schedules remain explicitly unavailable. Shared colour/follow bar, eight swatches, white-colour presets, connected chips and outlined follow tiles are now implemented. The fourth map-follow mode remains accessible. Shared mini-orbs, per-lamp slider tint/glow and exact empty mood/schedule treatments still need matching. Names, colours and brightness come from connected lamps. |
| Fans / V2Fans | 1440 x 1130 | Prominent live RPM/ring cards, mode switch, editable curve, compact source selector, safety/smoothing panels and numerically ordered headers. | Service RPM history, sampled min/max cards and a selected-point speed/temperature nudge toolbar are implemented. The service still does not report physical fan counts or equivalent smoothing controls; those remain empty/unavailable. Minimum-speed editing is disclosed and explicit apply remains. Graph axes, source chips, pump colour and the safety/smoothing row treatments still need matching. |
| Scenes / V2Scenes | 1440 x 1000 | Gradient scene cards with LED previews, real apply commands, automation availability explanation and saved LED-layout references. | Four service scenes are available; Focus/Away, custom scene creation/import, automation editing and the three-part scene editor are not implemented by the existing contract. The six-slot scene grid, empty automation rows and three-part scene editor are present. Unsupported Focus/Away slots and editing controls are explicitly unavailable; the functional layout-reference form is retained in a disclosure. Radial gradients and scene tags replace flat/simple cards. |
| Air fryer / V2Fryer | 1440 x 940 | Side-by-side timer and target settings, primary start action, separate presets/safety/history regions, firmware details and pause/cancel/resume retained. | Saved presets and cooking history are explicitly unavailable. Range-aware temperature/time steppers, segmented timer dial, full-width start action, safety checklist and empty preset/history tiles now follow the reference composition. Current temperature is labelled unavailable when absent. Start uses a second confirmation dialog; no scene or automation starts heating. |
| Internet / V2Modem | 1440 x 1180 | Partial signal-score arc, live 5G/LTE metric groups, ping/spike inspection, speed-test action, serving-cell details and collapsed password entry. | RSSI, connected-client inventory, throughput test results, router uptime/traffic totals and nearby-cell data are unavailable where not supplied. Both radio generations remain visible together rather than the reference's generation tab and four-metric grid. Graph and router-summary layouts still differ. |
| Widgets / V2Widgets | 1440 x 1050 | Tall gradient desktop preview; existing-widget selector; collapsed add gallery and metric controls; size, opacity, interaction and startup/service footer controls. | Preview uses actual saved widget positions and current native widget contents, so its arrangement differs from the sample desktop. Background presets and per-widget enable switches are not implemented; existing add/remove, opacity and global visibility remain. |
| Settings / V2Settings | 1440 x 900 | Wider section navigation, health summary, display/notification panels, one motion setting, collapsed event effects, existing access/startup/safety/about controls. | Temperature-unit selection, configurable accent, backup and per-event notification/update preferences remain unsupported. The Celsius-only label and unavailable explanations remain instead of inactive-looking controls. Event-effect and startup settings are preserved beyond the reference's general page. |

WPF text rasterization, shadows, chart interpolation and icon strokes also differ from browser rendering. These differences and the UI compositions identified above need further design work before claiming exact visual parity. Expanded power tools can require scrolling; they remain accessible.

## Data and safety

Production pages continue to bind to their existing IPC clients and view models. Offline sensor values remain unavailable, not zero; dashboard gauge tests now cover all four v2 readings. The preview uses `MockPrismClient` only, with no hardware, network, registry or tray interaction. A single initial preview sample does not invent a history.

Per-LED painting, layers/presets, case arrangement/rotation/flips, sensor bindings, ARGB sizing, token import and device renaming remain accessible. Pump selection cannot send a curve or mode command; BIOS hand-back remains wired. Appliance start still requires prepare plus explicit confirmation, settings lock while cooking, and pause/cancel remain accessible. Tests and offscreen previews cover these existing behavior paths; no physical-hardware interaction was performed.

## Validation

- `dotnet build Prism.sln -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false`: **0 warnings, 0 errors**.
- `dotnet test Prism.sln -m:1 -nodeReuse:false -p:UseSharedCompilation=false`: **472 passed, 0 failed, 0 skipped** across all five test projects (34 hardware, 141 lighting, 68 router, 115 service, 114 app).
- Full-shell `--v2` preview: all eleven pages rendered at the dimensions above; **no WPF binding warnings**.
- Existing full preview: controls, modem modes, widget kinds, lamp/device variants, idle/cooking/offline fryer states, fan selections, effects, sensor-related lighting and LED editor variants rendered with **no binding warnings**. Linear curve geometry and six slider fill cases pass.
- `git diff --check`: passed.

The extra MSBuild flags avoid the environment's failing default parallel build and compiler-server behavior; they do not disable warnings or skip projects.

Regenerate local render evidence from the repository root:

```powershell
dotnet build tests/Prism.App.Tests/Preview/Prism.App.Preview.csproj -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet tests/Prism.App.Tests/Preview/bin/Release/net8.0-windows/Prism.App.Preview.dll tests/Prism.App.Tests/obj/v2-preview --v2
dotnet tests/Prism.App.Tests/Preview/bin/Release/net8.0-windows/Prism.App.Preview.dll tests/Prism.App.Tests/obj/v2-power-preview
```

PNG files and `bindings.log` live in those ignored `obj` directories. The pre-existing untracked `docs/codex-v2-pages.log` is preserved and excluded from the implementation commit.

## Current refinement pass

- Dashboard: restored seven colour swatches, lights-off command, thermal chips, equal gutters, caption-only appliance ring and correct router sparkline source. Removed the empty error row that caused a scrollbar and narrowed the page. Rendered and compared at 1440 x 900; remaining treatment differences are still recorded above.
- Added bounded, timestamped service history, process CPU deltas and OS uptime to the additive IPC contract. Mock telemetry does not sample host processes or invent uptime.
- Baseline after shared/Dashboard changes: Release solution build has 0 warnings/errors; all 472 existing tests pass. No hardware writes performed.

- Monitor: rendered at 1440 x 1040 after replacing the simplified metrics. Added missing-data/history regression tests; 474 tests pass. Preview build: 0 warnings/errors; bindings.log empty.

- Devices: rendered at 1440 x 1160 and compared; discovery banners and card hierarchy now follow the reference, with actual inventory and measured fan/lamp/RAM/GPU footer data. Preview build 0 warnings/errors, no binding warnings.

- Lighting: rendered at 1440 x 940; whole-case illustration follows the reference geometry using service device colours and connected lamp identities. Nine design effects are primary; extra service effects remain under Advanced. Preview build 0 warnings/errors; no binding warnings.

- Lamps: rendered at 1440 x 1000, compared, and refined brightness/header/follow composition. Moods and schedules use empty tile/row regions without sample records. Preview build 0 warnings/errors; no binding warnings.

- Fans: rendered at 1440 x 1130. Added selected-point nudges with ordering and pump-lock regression coverage, native gradient graph area and measured per-header RPM history. No physical fan writes performed.

- Air fryer: rendered and compared at 1440 x 940; range-aware steppers and timer segments replace the generic sliders/dial. Current temperature stays unavailable when absent. Fixed cooking-lock coverage for FrameworkElement sliders as well as buttons; pause/cancel remain visible while cooking.

- Scenes: rendered and compared at 1440 x 1000; restored six card slots, radial treatments, tags, five empty automation rows and the three-part editor. No unsupported scene or automation sends a command.
