# Codex round 3 status

Implemented in `src/Prism.App` and `tests/Prism.App.Tests`. No commits. No source or configuration edits outside the permitted paths; Prism.sln already contained both projects and needed no change. Build-generated bin/obj output is ignored.

## Delivered

- Fixed the four original compilation errors and wired the application resources and startup.
- Native WPF custom-chrome shell, graphite/glass theme, grouped sidebar, selected navigation, breadcrumbs, service banner, and Ctrl+Space command search with filtering, arrow keys, Enter and Escape.
- XAML templates for Dashboard, Monitor, Lighting, Fans, Lamp, Scenes, Widgets, Settings, and all four Modem modes. Includes live chart/metric bindings, device sync switches, fan curve editing, lamp power/brightness/colour/follow controls, an obscured Mi Home token field, scene presets, ping strip, spike panel and device availability panel. Monitor ranges select real retained samples for one minute, five minutes or one hour.
- `--mock` selects the mock client; normal startup selects only the named-pipe client. `--tray` and the minimized preference suppress the main window. Mock mode uses an in-memory startup registry implementation.
- WinForms NotifyIcon with Open, Scenes, Fan mode, Lights off, Show/Hide widgets, Quit. Close-to-tray preference and explicit shutdown cleanup. Session-local mutex/event single-instance activation, with separate identities for mock and live modes.
- Start-with-Windows code behind IStartupRegistry. Construction does not read or write the registry. Only an explicit live-mode preference change invokes RegistryStartup; failures are reported and do not save an incorrect enabled setting. Tests use fakes exclusively.
- All ten widget types share the same snapshots and WidgetContent view. Desktop windows are borderless, transparent, hidden from the taskbar, draggable by their headers and configurable for S/M/L, metrics, background opacity, topmost, click-through, locking and eight-pixel snapping. Fans, lighting effects/colours, lamp and scenes have service-client controls.
- Widgets page has a draggable desktop preview, selection/customization panel, gallery and startup preferences. Layouts retain monitor device names and positions; restoration clamps widgets into an available work area if a display disappears. Saved empty layouts remain empty, malformed IDs are repaired, and settings/layout saves use replacement files.
- Corrected binding-preserving slider interaction; primary/secondary chart collection subscriptions and weak event handling; frozen toggle-template transforms; reduced-motion toggle animation; card entry/hover motion; stable dashboard gauges and animated numeric labels; missing-reading formatting; selected-group fan commands and pump protection; command failure feedback; and incomplete state-message validation.
- Removed fabricated live chart history, fan calibration results, modem device/neighbor readings, speed-test completion progress and asserted spike causes. Spike observations now distinguish measured latency from unknown causes.

## Validation

- `dotnet build Prism.sln --no-restore -m:1 -v:minimal`: passed, zero warnings and errors.
- `dotnet test Prism.sln --no-restore -m:1 -v:minimal`: all suites passed. Final app suite: 87 tests (64 original plus 23 added cases). Full solution total: 262 (28 hardware, 44 lighting, 68 router, 35 service, 87 app).
- The sandbox's parallel MSBuild invocation initially returned failure without a diagnostic; single-worker builds are reliable. No packages were added.
- Added a separate manual offscreen renderer at `tests/Prism.App.Tests/Preview`. Build it with `dotnet build tests/Prism.App.Tests/Preview/Prism.App.Preview.csproj -m:1`, then run `dotnet tests/Prism.App.Tests/Preview/bin/Debug/net8.0-windows/Prism.App.Preview.dll tests/Prism.App.Tests/obj/preview`. It renders the shell, all nine page templates, all four modem modes and all ten widget types; binding warnings fail the run. Output is under the app tests' ignored obj folder. It does not start App, timers, pipes, tray, registry operations or desktop widget windows. The ordinary unit tests require no UI thread.
- Briefly launched the real application with `--mock` only. Startup completed, but the sandbox desktop interface did not expose its windows, so interactive desktop inspection was unavailable. Stopped the mock processes. Used offscreen renders for visual and binding checks instead.
- Did not connect to the real service pipe, install/start/stop services or drivers, invoke WMI, change the registry, contact the router/lamp, or access hardware sensors/controllers.

## Limits imposed by the existing contract and unverified desktop behavior

The app is runnable and its implemented controls use the supplied contract. The complete product brief cannot be represented as fully operational with the immutable PipeContract currently supplied:

- Mi Home token provisioning has no command. The obscured field is memory-only and explicitly says it cannot provision a token. No token is persisted or sent.
- Neighbor cells, PCI/eNodeB/anchor details, connected device lists, and download/upload/speed-test result history have no state fields. Those sections show availability explanations or missing values. Start speed test sends the existing request and displays its acknowledgement/error only.
- OpenRGB path and router-enable preferences persist locally; the protocol exposes no service configuration command. The UI explains this. It does not launch OpenRGB or change service polling.
- Fan smoothing/calibration and custom scene authoring/automatic game/night/lock scene switching have no supported configuration contract here. Calibration does not manufacture results; smoothing is described as service-managed. Scene presets work, but there is no automatic-scene runner or custom-scene editor in this delivery.
- Actual multi-monitor mixed-DPI drag/restore, click-through interaction, tray interaction, second-launch foreground activation and Mica appearance still need interactive Windows acceptance testing. Their native implementations compile; placement, activation signaling and nonvisual settings/commands have fake-backed tests. Offscreen renders cannot establish those desktop behaviors or animation timing.

These limitations are surfaced rather than hidden behind simulated successful operations. No other project or shared wire contract was changed to bypass the requested scope.
