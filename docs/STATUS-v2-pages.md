# v2 pages status

Updated 2026-10-31 on `ui/v2-pages`. The pending control/page refinements are included in this pass. Settings General has been rebuilt against `docs/design-v2/V2Settings.png` at 1440 x 900. No push or main-branch changes.

## Settings

- Matched the reference's 280-pixel section card, 16-pixel gutter, 872-pixel main column, 228-pixel Display panel and 282-pixel Notifications panel. Refined section icons, active border/gradient, typography, row dividers, colour swatches, switches, health dots and save chip. Removed the extra General-page disclosures; discovery and startup controls remain under Devices & access, and the data-folder action remains under About.
- Celsius/Fahrenheit selection persists and converts temperature text, gauges, widgets and fan-curve labels. Service readings, safety thresholds and outgoing commands retain Celsius. The five accent choices persist and update button/navigation resources immediately.
- Running-hot, cooking-finished/shake and offline-device preferences persist and control transition-based entries in the notification bell. Repeated state pushes do not repeat alerts. Updates persists and compares the installed service version with the app version; there is no remote release feed or download/install updater.
- Health reads the service connection, connected OpenRGB inventory, the actual fan sensor chip identity, online lamps and router connection. The motherboard marketing name is no longer labelled as the fan chip.
- The final Settings preview was visually compared with the PNG. Geometry and composition now follow the reference. WPF glyph rasterization, icon strokes and glow rendering still differ from the browser reference: this is not a pixel-identical image sign-off. Preview device counts and identities remain mock fixtures, not copied reference values.

## Readings and their service sources

Production pages and widgets use IPC. No page reads host processes directly and no screenshot sample readings have been inserted into production state.

| Reading | Source and scope |
| --- | --- |
| Top processes | Service process CPU-time deltas, normalized to machine capacity. First poll has no rate. Protected/exiting processes are skipped; PID reuse and reset counters cannot produce false deltas. Four measured processes are sent over IPC. |
| System uptime | Service reads Windows/OS tick uptime; it is not app/service running time. Mock mode never samples the host uptime. |
| Thermal throttling | Explicit hardware thermal-throttling sensors. Counts represent episodes observed by the service, accompanied by an observation timestamp; absence of a sensor remains unknown, never zero events. |
| CPU/GPU/load/temperature histories | Timestamped service sensor snapshots, bounded to 3,600 samples. Dashboard, Monitor, fan cards and widgets consume service history; opening a page/widget does not need to manufacture a history. |
| Fan histories and tuning | Per-header measured RPM samples and the engine's actual source, curve, minimum, hysteresis and ramp configuration. Unmeasured fan topology is not described as a physical fan count. |
| Disk/network history | Measured network throughput and primary-disk read throughput in the service history. Disk occupancy explicitly selects the Used Space sensor, rather than accidentally using an activity sensor. |
| Hardware identity and CCD | LHM hardware names, the chip attached to fan/control sensors and CPU CCD temperature. Missing sensors remain null. |
| Internet signal/cells | The read-only router signal response and existing Scanner parser project RSSI, PCI, cell IDs, ARFCN, bandwidth, LTE anchor and neighbors into additive IPC fields. Radio history, ping, average and jitter use measured samples. |
| Internet clients | Ready network clients from the service device registry, including their measured identity and IP. Per-client throughput is not available from that inventory and is not fabricated. |
| Router summary | Optional model, uptime, monthly byte counter, carrier aggregation and tower-distance fields from the read-only response; address comes from the service's configured router endpoint. These are nullable projections, not proof that this firmware exposes those fields. |
| Speed test | Actual service response download/upload values; results are retained in the current UI session. No sample throughput result appears before a test. |
| Recent cooks | Bounded records of cooking sessions actually observed by the service. Disconnects cannot end a cook. Records retain observed-since/end times, target temperature/time and observed outcome; no earlier cooking history is reconstructed. Records currently last for the service session. |

Missing memory/GPU/fan measurements no longer become zero-valued meters, and widget histories no longer insert zero for absent network telemetry. Unknown readings retain unavailable/empty states; no measured value is inferred from a screenshot.

## Page and control refinements

| Page | Included work |
| --- | --- |
| Dashboard | Four service-backed gauges/history, thermal chips, seven live swatches, lights-off command, corrected fan/LED/orb treatments and router sparkline source. Removed the unmeasured front/rear fan topology label. |
| Monitor | Live process rows, uptime, throttling scope/counts, CPU/GPU mini-stats, thread tiles, temperature rails, disk activity/occupancy and service-backed histories. |
| Devices | Grouped actual inventory, discovery banners, tinted icons, status treatments, inline rename and live device footers. |
| Lighting | Live animated case illustration, sensor/custom/overlay LED rendering, improved hue ring, gradient effect gallery and device chips; per-device/spatial/ARGB tools remain accessible. |
| Lamps | Connected-state orbs, shared controls, live swatch selection, brightness gradients, white presets and follow tiles. |
| Fans | Needle dials, live header/control labels, service RPM min/max/history, editable curve with ordered selected-point nudges and actual source/minimum/smoothing readings. Pump locking remains enforced. |
| Scenes | Scene tags, radial/LED previews, six-slot composition and retained service scene commands/layout references. |
| Air fryer | Timer/target steppers, guarded start, cooking-lock controls, safety checklist and service-observed Recent cooks. |
| Internet | Radio tabs/rails, service-backed details, client inventory, histories, measured ping/jitter and actual speed results. |
| Widgets | Service history hydration, temperature display preferences, live device summaries, persisted enable/background choices and native/preview consistency. |
| Settings | Reference General composition and functional display/notification preferences, with existing access/startup/safety/about tools retained. |

Shared refinements include selected colour swatches, gradient slider fills, animated service-driven case/LED previews, hue-ring handle, status bars and a neutral unavailable status. The full-shell preview recreates its visual tree per page so a previous page's scrollbar measurement cannot narrow the following capture. Reduced motion is forced for deterministic capture without changing the displayed preference.

## Remaining limits

The request for every screenshot reading to be populated with no exceptions is **not fully satisfied**. The fields are wired to service sources, but no live service was running during this pass and firmware/hardware availability has not been validated here. In particular, the existing router endpoint is not established to return model/uptime/monthly traffic/tower distance/bandwidth, per-client rates are absent from its inventory, and hardware may not expose a throttling sensor. Those values remain unavailable when absent. Completing unconditional population requires verified read-only sources for those measurements; adding nullable contract fields alone does not establish them.

Existing product features outside telemetry remain unavailable: lamp moods/schedules, fryer saved presets, Focus/Away and custom scene/automation editing, and Settings backup/restore. They retain explicit unavailable or empty states. They have not been replaced by sample records. Other page-specific browser/WPF visual differences from the earlier comparison remain; this pass does not claim pixel-exact parity for all eleven pages.

A read-only connection check of the configured default router address also failed (HTTPS connection refused), so firmware fields could not be verified live. No physical hardware writes were performed. Fan pump protection/BIOS hand-back, per-LED editing, layout transforms, sensor bindings, ARGB sizing, credentials and renaming remain accessible. Appliance heating still requires prepare plus a second explicit confirmation; settings lock while cooking and pause/cancel remain available.

## Validation

- `dotnet build Prism.sln -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false`: **0 warnings, 0 errors**.
- `dotnet test Prism.sln -c Release --no-build -m:1 -nodeReuse:false -p:UseSharedCompilation=false`: **483 passed, 0 failed, 0 skipped** (34 hardware, 141 lighting, 68 router, 120 service, 120 app).
- Added regression coverage for normalized process rates/PID reuse, router IPC projection/missing values, observed cooking history, Settings persistence/reset/conversion, Internet projections/history deduplication and notification transitions.
- Preview Release build: **0 warnings, 0 errors**.
- All eleven full-shell pages rendered at their reference PNG dimensions; **bindings.log is empty**.
- Full power-feature preview rendered controls, menus, modem modes, widget kinds, lamp/device variants, fryer idle/cooking/offline states and lighting/editor variants; **bindings.log is empty**. Linear-curve geometry and six slider-fill cases pass.
- `git diff --check`: passed.

The MSBuild flags avoid this environment's parallel/compiler-server issues; they do not suppress diagnostics or omit projects.

Regenerate preview evidence from the repository root:

```powershell
dotnet build tests/Prism.App.Tests/Preview/Prism.App.Preview.csproj -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet tests/Prism.App.Tests/Preview/bin/Release/net10.0-windows/Prism.App.Preview.dll tests/Prism.App.Tests/obj/v2-preview --v2
dotnet tests/Prism.App.Tests/Preview/bin/Release/net10.0-windows/Prism.App.Preview.dll tests/Prism.App.Tests/obj/settings-preview --v2 --page=Settings
dotnet tests/Prism.App.Tests/Preview/bin/Release/net10.0-windows/Prism.App.Preview.dll tests/Prism.App.Tests/obj/v2-power-preview
```

PNGs and binding logs live in ignored `obj` directories. The pre-existing untracked `docs/codex-v2-pages.log` is preserved and excluded from the implementation commit.
