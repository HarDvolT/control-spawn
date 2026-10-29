# Brief for Codex, round 3: finish the Prism app (WPF)

OpenCode started `src/Prism.App` and `tests/Prism.App.Tests` from docs/BRIEF-opencode-2.md but stopped
(its model quota ran out) before finishing, and it could never compile. Both projects are now in Prism.sln.
Take over that brief completely: read docs/BRIEF-opencode-2.md (the full spec), docs/ARCHITECTURE.md,
`src/Prism.Core/Ipc/PipeContract.cs` (do not edit) and the design mockups in docs/design/*.dc.html.

State today: theme dictionary, controls (GlowProgressBar, RingGauge, NumberTicker, ThickSlider, LineChart,
LedStrip, FanCurveEditor, GlassCard, ToggleSwitch, SegmentedControl), view-models for every page, the
pipe/mock clients, widget layout persistence, command search and 5 test files exist. Missing: the XAML
views (MainWindow shell, every page, widget windows), the tray icon, single instance, startup wiring,
App.xaml resources wiring, and `--mock`/`--tray` handling. `dotnet build Prism.sln` currently fails with
4 compile errors in FanCurveEditor.cs, FansViewModel.cs, ModemViewModel.cs and WidgetViewModel.cs.

Do:
1. Fix the build. Review OpenCode's code critically and fix what is wrong rather than working around it.
2. Build every view in the brief so the app runs end to end against `MockPrismClient` (`--mock`) and
   against the real service pipe otherwise: shell (custom chrome, sidebar groups, breadcrumb, Ctrl+Space
   command search), Dashboard, Monitor, Widgets, Lighting, Fans, Lamp (with a "Mi Home token" field and
   connection status), Scenes, Modem (all 4 views + ping strip + spike detective + devices), Settings
   (startup/background toggles, reduce animations, OpenRGB path, router enable). All the brief's animation
   and glow rules apply; every checkbox is a ToggleSwitch; sliders are ThickSlider with percentage.
3. Tray icon + menu, close-to-tray, single instance, start-with-Windows behind an interface (never
   written by tests), `--tray`.
4. Desktop widget windows (all types in the brief) with drag, snap, S/M/L, metric selection, opacity,
   always on top, click-through, lock, per-monitor persistence, restore at startup.
5. Keep all tests green (`dotnet test Prism.sln`) and add tests for anything non-visual you add.
   You may launch the app with `--mock` briefly only if your sandbox allows a GUI; otherwise don't run it.

Rules: work only in src/Prism.App, tests/Prism.App.Tests, Prism.sln and docs/STATUS-codex-3.md. Never touch
real hardware, drivers, services, the registry (tests use fakes), WMI, the router or the lamp. No commits.
If a NuGet package can't be restored (network may be blocked), use what's available (e.g. WinForms
NotifyIcon via UseWindowsForms instead of a tray package). Finish with docs/STATUS-codex-3.md.
