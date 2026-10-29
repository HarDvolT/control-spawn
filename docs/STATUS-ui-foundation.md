# UI foundation — phase A

The page dictionary has been split into eleven independently owned folders. Existing CLR namespaces and implicit DataTemplate keys are preserved so navigation, bindings and helper references remain compatible. `App.xaml` merges each page dictionary directly; `Views/PageTemplates.xaml` has been removed.

## Page ownership

Each page agent owns its entire folder under `src/Prism.App/Views/Pages/`:

| Folder | Template | View models and page helpers |
| --- | --- | --- |
| Dashboard | Dashboard.xaml | DashboardViewModel.cs |
| Monitor | Monitor.xaml | MonitorViewModel.cs |
| Devices | Devices.xaml | DevicesViewModel.cs, DevicesWorkspace.cs, TokenImportPanel.cs |
| Widgets | Widgets.xaml | WidgetsViewModel.cs, DesktopPreview.xaml and code-behind |
| AirFryer | AirFryer.xaml | AppliancesViewModel.cs, AppliancesWorkspace.cs |
| Lighting | Lighting.xaml | LightingViewModel.cs, LightingWorkspace.cs, LedEditor.cs, LedEditorViewModel.cs, SensorBindingPanel.cs, CaseLightingSetup.cs |
| Fans | Fans.xaml | FansViewModel.cs |
| Lamp | Lamp.xaml | LampViewModel.cs, TokenEntry.xaml and code-behind |
| Scenes | Scenes.xaml | ScenesViewModel.cs |
| Modem | Modem.xaml | ModemViewModel.cs |
| Settings | Settings.xaml | SettingsViewModel.cs |

The navigation key `Appliances` is retained for compatibility; its page folder is `AirFryer`. Helper namespaces remain `Prism.App.Views`, and view models remain `Prism.App.ViewModels`. A page agent need not edit the application resource merges or another page folder.

## Shared foundation

- `Themes/Prism.xaml`: Inter display/title/heading/body/caption sizes 32/20/15/13/11, Space Grotesk numeric styles with tabular figures, spacing 4/8/12/16/24/32, radius 12/16/20, status brushes and label/status converters.
- `Controls/CardGrid.cs`: twelve columns, six below 960 px and one below 600 px; `GlassCard.Size` is Auto/S/M/L (140/220/320 px), and `GlassCard.Span` or attached `CardGrid.Span` sets column span. Auto preserves existing page behavior.
- `Controls/MetricControls.cs`: RadialGauge, ArcSpeedometer, ThermalBar, HeatGrid and SegmentedCapsule. Metric values tween, statuses drive glow, and critical gauges pulse. `SensorKind`, `Value`, `Maximum` and `Suffix` are bindable. Missing metric values use NaN and show an em dash; null heat-grid entries show an unavailable tile.
- `Controls/LineChart.cs`: existing Sparkline reused with compact defaults.
- `Controls/StatusLanguage.cs`: Cool/Ok/Warm/Hot/Critical, sensor thresholds, metric-to-level, level-to-brush/glow and friendly labels. StatusChip adds text and a shape to colour, including critical pulse.
- `Controls/ColorWheel.cs`: continuous hue/saturation picker with two-way ColorHex and a release event. The lamp page uses it immediately.
- `Controls/DeviceIcon.cs`: vector RAM/GPU/board/strip/fan/lamp/router/fryer/network icons and IconSegmented choices (Key, Label, Icon).
- Existing shared controls (including EventOptionsPanel, used by Lighting and Settings), `Views/WidgetContent.*`, per-widget models and shell/navigation stay shared. Coordinate shared-file changes before editing them in parallel.

## Instant commands

`IPrismClient.SendLatestAsync(key, command, args)` uses `LatestCommandQueue`: an immediate leading send, then serial sends at least 30 ms apart, retaining only the newest pending arguments per key. All callers receive their batch result. A value arriving during an in-flight send is delivered afterward, including the final slider value. Cancelling a caller's wait does not discard a pending final value. `ThickSlider` also samples the release position and exposes ValueCommitted.

Lighting, effect parameters, map changes and lamp controls update their model before sending. `PageViewModel.RunLatestAsync` shields these edits from stale snapshots while commands are pending and refreshes service state on rejection. Separate lamp IDs use independent lanes. Existing per-LED draft/resize synchronization remains in the LED editor.

`CommandProcessor.StateChanged` signals all subscribed sessions after an accepted command. Each `PipeSession` has a bounded notification channel and one serialized writer, alongside periodic telemetry. Pushes build a fresh state rather than waiting for the one-second tick. Lamp intent in state reflects accepted settings immediately; the lamp worker checks latest intent every 50 ms and paces configuration changes at 500 ms for miIO. RGB rendering retains the existing 30 fps loop and device-specific bus pacing.

## Lighting commands and setup

- `identifyZone { device, zone, size }` validates/resizes through the existing path and saves ZoneSizes. For 30 seconds the selected zone shows a moving white marker over dim white LEDs. State includes `Lighting.IdentifyingZones` with a one-based LED index; the case preview and setup helper display it. `resizeZone` saves the final count and ends identification. Identification is transient and does not overwrite painted layers.
- `CaseLightingSetup` lists every adjustable zone, starts at min(120, header maximum), supports a typed count and fans × LEDs per fan, and explains reducing the chain length until the final LED goes dark, then adding one back.
- `setAllLighting { effect?, color?, brightness?, speed?, resetOverrides? }` updates whole-case lighting, enables RGB participation, and switches every configured lamp to Same as PC. Painted LED layers and sensor bindings are retained unless resetOverrides is true. Off or zero brightness turns lamps off. The Lighting workspace exposes “Sync whole case and both lamps”; future page work can call `LightingViewModel.SyncAllAsync`.
- Mock commands implement the same setup and whole-case flow without contacting hardware.

## Verification

Validation uses the full solution build/tests and `tests/Prism.App.Tests/Preview`, which constructs WPF views offscreen with mock state, in-memory startup preferences and a null widget host. The preview does not start App, connect pipes, change registry settings or launch hardware/services. It renders every page plus foundation controls, responsive variants, icons, segmented choices and expanded ARGB setup.

Test additions cover coalescing, final delivery during an in-flight request, independent lanes, cancelled waiters, optimistic stale-state protection and rejection rollback, immediate fresh pushes to two subscribers, threshold boundaries, zone persistence/validation, and whole-case lamp/override behavior.

Final results:

- `dotnet build Prism.sln -m:1`: succeeded, 0 warnings, 0 errors.
- `dotnet test Prism.sln -m:1`: 472 passed, 0 failed, 0 skipped (Hardware 34, Lighting 141, Router 68, Service 115, App 114).
- Offscreen preview: exit 0, 111 PNGs, empty `bindings.log`. Every page was visually inspected, together with the foundation sheet, narrow grid, icon selector, vector icons and expanded ARGB helper. Contrast, inherited selector styling, remaining raw device labels and a reused-preview parent-width issue found during review were corrected and rendered again.
- Preview artifacts: `tests/Prism.App.Tests/obj/ui-foundation-preview/`. Reproduce by building `tests/Prism.App.Tests/Preview/Prism.App.Preview.csproj -m:1`, then running its output DLL with that directory as the first argument.
- `git diff --check`: clean. `docs/UX-SPEC.md`: untouched. No commits. No real hardware, service, registry, router, lamp or fryer interactions.

This phase supplies page ownership and shared primitives; subsequent page agents own the full visual redesign and density of their existing compositions. Live transport latency was intentionally not measured under the no-hardware rule.
