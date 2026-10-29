# Brief for OpenCode, round 2: the Prism app (WPF) + desktop widgets

Read docs/ARCHITECTURE.md and `src/Prism.Core/Ipc/PipeContract.cs` (wire contract to the service; do NOT
edit it). The approved-direction design is in `docs/design/*.dc.html` (HTML mockups: Main = Dashboard,
Monitor, Lighting, Fans, Scenes, Modem, Widgets). Read them for layout, colours, copy and animations; you
are recreating them natively, not embedding HTML.

Rules: work only in `src/Prism.App/`, `tests/Prism.App.Tests/` and `docs/STATUS-opencode-2.md`; do not
edit other projects (Codex is building `src/Prism.Service` and `src/Prism.Lighting` at the same time).
You may add your two projects to Prism.sln with `dotnet sln add`. Never touch hardware, the router, the
registry or services at run time. `dotnet build Prism.sln` must stay green. No commits.

## Build

`src/Prism.App`: WPF, `net8.0-windows`, `UseWPF`, references only `Prism.Core`.

1. **Theme** (`Themes/Prism.xaml`): graphite glass from the design — page #0A0A0B with a soft top light,
   panels #1A1A1D→#141416, 1px rgba(255,255,255,.06) borders, 20px radius, accent #FF4D1A, good #3DDC84,
   fair #F5A524, poor #FF4D4F, text #F4F4F5 / #A1A1AA / #8A8A93. Fonts: Inter (UI), Space Grotesk
   (numbers) with Segoe UI fallback. Custom window chrome (borderless, own title bar, Mica-like backdrop
   via DwmSetWindowAttribute when available).
2. **Controls** (reusable, in `Controls/`): GlowProgressBar (thick capsule bar, animated fill with
   CubicEase/ease-out ~800 ms, soft glow DropShadowEffect in the bar colour, shimmer sweep, value label
   with percentage), RingGauge (animated arc + glow + tweening number), ToggleSwitch (orange glow when on,
   sliding knob — every checkbox in the app is a toggle), Sparkline/LineChart (smooth curve on dotted
   grid), SegmentedControl, GlassCard (fade+rise on load with stagger, hover lift), FanCurveEditor
   (8 draggable points, live "now" marker), NumberTicker (counts up/down between values). Sliders in the
   design are "thick progress-bar sliders without a thumb, with a percentage" — build them that way.
3. **Shell**: sidebar with groups GENERAL (Dashboard, Monitor, Widgets), TOOLS (Lighting, Fans, Lamp,
   Scenes), INSIGHTS (Modem), OTHER (Settings); top bar with breadcrumb + command search (Ctrl+Space
   opens it; it filters pages and commands like "Fan mode Turbo", "Scene Night").
4. **Pages**: Dashboard, Monitor, Lighting, Fans, Lamp, Scenes, Modem (Simple / Detailed / Scan / Speed
   + ping strip + spike detective panel + devices), Widgets, Settings — matching the mockups, bound with
   MVVM (CommunityToolkit.Mvvm is fine) to a `IPrismClient`.
5. **Service client**: `PipePrismClient` (NamedPipeClientStream, JSON lines per PipeContract, reconnect
   with back-off, "Service not running" banner) and `MockPrismClient` producing realistic changing data
   (use the real hardware values from docs/HARDWARE.md as the base). `--mock` flag picks the mock.
6. **Tray + background**: NotifyIcon (System.Windows.Forms interop or H.NotifyIcon.Wpf) with menu: Open,
   Scenes ▸, Fan mode ▸, Lights off, Show/Hide widgets, Quit. Closing the window hides to tray when
   "Keep running when closed" is on. Single instance (named mutex; second launch focuses the first).
   "Start with Windows" writes HKCU\Software\Microsoft\Windows\CurrentVersion\Run (in code only, behind an
   interface; never executed by tests), "Start minimized to tray" adds `--tray`.
7. **Desktop widgets**: each widget is its own borderless, transparent, non-taskbar window
   (`WindowStyle=None`, `AllowsTransparency`, `ShowInTaskbar=false`), draggable, snap to an 8 px grid,
   options: size S/M/L, metrics shown, background opacity, always on top, click-through
   (WS_EX_TRANSPARENT|WS_EX_LAYERED via SetWindowLong), lock position. Types: System monitor, CPU, GPU,
   Fans (mode switch works), Lighting (colour + effect), Lamp, Modem, Ping graph, Network, Scenes.
   Layout persisted per monitor in `%APPDATA%\Prism\widgets.json`, restored at startup. The Widgets page
   edits them (desktop preview + customize panel + gallery) as in Widgets.dc.html.
8. **Tests** (`tests/Prism.App.Tests`, xUnit, no UI thread needed): JSON-line framing/parsing of
   PipeContract messages, widget layout save/load round-trip, snap-to-grid maths, command-search matching,
   view-model formatting (nulls show "—", RPM formatting, percentages).

Respect `prefers reduced motion` equivalent: a Settings toggle "Reduce animations".
Finish with docs/STATUS-opencode-2.md (what exists, build/test results, what is missing).
