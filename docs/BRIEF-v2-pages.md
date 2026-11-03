# Brief: build the approved v2 design into the WPF app, exactly

The user approved the v2 "Graphite HUD" design. Rebuild every page of `src/Prism.App` so it looks and behaves like the design. Match it exactly: layout, card order, spacing, sizes, colours, fonts, text, glows and animations. Do not redesign anything.

## Source of truth
- `docs/design-v2/V2<Page>.dc.html`: the HTML for each screen. The `<main>` markup gives layout and styles; the `<script>` `renderVals()` gives behaviour, states and the text shown.
- `docs/design-v2/V2<Page>.png`: screenshots at 1440 px wide. Where a PNG and the HTML disagree, the HTML wins.
- `docs/DESIGN-LANGUAGE.md`: tokens and rules (orange = user's actions, cyan = machine data, status cool/ok/warm/hot/critical, 12-column card grid).
- `docs/design-v2/V2Kit.dc.html`: the component kit.

## Pages (design file → app folder under `src/Prism.App/Views/Pages/`)
Dashboard → Dashboard, Monitor → Monitor, Devices → Devices, Lighting → Lighting, Lamps → Lamp, Fans → Fans, Scenes → Scenes, Fryer → AirFryer, Modem → Modem (menu label "Internet"), Widgets → Widgets, Settings → Settings.

## Shared shell (do this first)
- Sidebar exactly as in the design: Prism logo, sections GENERAL / CONTROL / HOME / SYSTEM, an icon per item (the SVG path data is in each file's `shellVals()` ICONS), active item with an orange gradient and a left bar, and the "Service live · N devices" pill at the bottom.
- Top bar on every page: breadcrumb "Prism / <Page>", the "Search or type a command" box with a Ctrl Space hint, the "All systems normal" status pill, and the notifications button.
- Background: dark graphite with a dot grid and two slow-drifting blurred glows (orange top-left, cyan right).
- Put the shared styles and controls in `Themes/Prism.xaml` and `Controls/`. Reuse the controls already there (RingGauge, FanCurveEditor, ToggleSwitch, SegmentedControl, StatusChip, GlassCard, LedStrip, ColorWheel, CaseMapPreview, LineChart…) and restyle them to match. Add new ones where needed.

## Rules
- Keep every existing feature and service connection. Pages bind to the real view models and real data (IPC to Prism.Service). The numbers in the design are only samples; never hard-code them. If a page needs data the view model doesn't have yet, add it to the view model, and wire it to the service where the contract already supports it. Otherwise leave it as a clearly marked placeholder and list it in the status file.
- Keep the must-have power features: per-LED painting, arrange/rotate/flip case map, sensor-driven lighting, ARGB setup, token import, device renaming, fan safety (pump locked at 100%, BIOS hand-back).
- Air fryer: starting must always need a second confirm tap, settings lock while cooking, and scenes or automations can never start it.
- Controls must respond instantly: update the UI at once and send commands without blocking the UI thread.
- Respect Windows reduced-motion settings (turn off pulsing and slide-in animations).
- Fonts: Inter for text, Space Grotesk for numbers and headings. Bundle them as resources if they are not installed; fall back to Segoe UI.

## Done means
- `dotnet build Prism.sln -c Release` succeeds with 0 warnings and 0 errors, and `dotnet test Prism.sln` passes (keep or update the existing tests).
- Every page matches its design screenshot at 1440 px wide.
- Commit on the current branch `ui/v2-pages`, one commit per page or group of pages. Messages are short and plain.
- Write `docs/STATUS-v2-pages.md`: what was built per page, what still uses placeholder data, and anything you couldn't match exactly, with the reason.
- Don't push, and don't touch the `main` branch.
