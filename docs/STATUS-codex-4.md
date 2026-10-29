# Codex round 4 status

Completed the visual fixes in `docs/BRIEF-codex-4.md`.

- FanCurveEditor now draws seven straight segments through the eight points for the stroke, glow and area fill. The live marker still uses linear interpolation.
- Every ComboBox uses an implicit graphite template: rounded dark gradient popup, hover, selection, keyboard focus and disabled states.
- ThickSlider retains range-relative positioning and no longer inflates short fills to the track height. Minimum-speed text reports the actual duty percentage and updates when edited. For clarity, 33 on the existing 0..60 range occupies 55% of the track and reads 33%; the value is not converted into a different duty percentage.
- Fan groups are rounded cards with rotor icons, header, live RPM, duty bar and orange #FF4D1A selection glow. Rotation period uses the live RPM (higher RPM means faster rotation), stops when unloaded and respects reduced motion. The pump has a vector lock and Fixed 100% pill; selecting it displays the fixed curve and disables editing.
- Added implicit button styling for colour swatches and other unstyled buttons, custom text/password templates, themed editing context menus and rounded tooltips. Scrollbars cover both orientations with hover/drag states. Tray menus and submenus use a graphite renderer through a separate theme helper.

## Validation

- `dotnet build Prism.sln -m:1`: passed, 0 warnings, 0 errors.
- `dotnet test Prism.sln -m:1`: passed, 264 tests (28 hardware, 44 lighting, 68 router, 36 service, 88 app); none skipped.
- `dotnet build tests/Prism.App.Tests/Preview/Prism.App.Preview.csproj -m:1`: passed, 0 warnings.
- `dotnet run --no-build --project tests/Prism.App.Tests/Preview -- tests/Prism.App.Tests/obj/preview-round4`: passed. Binding warning log is empty.
- Preview checks assert straight curve geometry and actual rendered slider edges for six cases, including a nonzero minimum, 33/60, tiny values and endpoints. Added a mock-only regression for percentage labels, RPM-dependent rotation periods, card selection and the fixed pump.
- Visually reviewed all nine page PNGs and shell, comparing shared graphite palette/control treatments with `docs/design/*.dc.html` and the fan card details with `Fans.dc.html`. The brief explicitly supersedes the reference's smooth curve. Also inspected the dropdown, control gallery, tooltips, context menu, detached tray/submenu and Case fans/pump selection renders. Four modem modes and ten widget types are rendered by the same preview run.

Preview artifacts are under `tests/Prism.App.Tests/obj/preview-round4/` (ignored build output). Motion is frozen for screenshots; rotation speed ordering is covered by the mock regression rather than a live desktop session.

Only authored changes inside `src/Prism.App`, `tests/Prism.App.Tests` and this status file. Other working-tree changes appeared during the task and were left alone. No real hardware, services, registry, router or lamp were accessed. The renderer uses a mock client, in-memory startup registry, null widget host and detached menus; it does not start the application or create a notification icon. No commit was made.
