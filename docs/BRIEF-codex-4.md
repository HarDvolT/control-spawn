# Brief for Codex, round 4: visual fixes in the app

Only `src/Prism.App`, `tests/Prism.App.Tests` and docs/STATUS-codex-4.md. Same safety rules as before. No commits.
Use the offscreen renderer (tests/Prism.App.Tests/Preview) to check each fix and compare with docs/design/*.dc.html.

1. FanCurveEditor: the fan engine interpolates linearly between the 8 points, so draw straight segments
   (keep the glow and the area fill); the curve must show what the fans will really do.
2. Style every ComboBox (the Fans "follows" picker and any other) in the graphite theme: dark glass
   popup, rounded, hover/selected states, no default white Windows look.
3. ThickSlider: the filled width must equal the value's position within Minimum..Maximum (the Fans
   "Minimum speed" shows 33% but fills about half). Show the percentage of the value itself.
4. Fans page group selector (Radiator / Case fans / AIO pump): make them cards like the design — icon with
   a fan that spins faster with RPM, name, header, live RPM, selected state glowing in #FF4D1A, the pump
   card locked with a "Fixed 100%" pill.
5. Review every page render for anything else that looks like a default WPF control (buttons, scrollbars,
   text boxes, tooltips, context menus, the tray menu) and theme it.
Keep `dotnet build Prism.sln -m:1` at 0 warnings and `dotnet test Prism.sln -m:1` green.
