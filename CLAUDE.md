# Control Spawn (code name Prism)

One Windows control centre for a gaming PC and its room: fans, case RGB, Mi Home lamps, an air fryer and the 5G router. It replaces ASUS Armoury Crate and Corsair iCUE. The product name is **Control Spawn**; the source code (namespaces, project folders) still says **Prism**, but nothing on an installed PC does.

## Layout

C# .NET 10 solution `Prism.sln`:

- `src/Prism.Core`: IPC contract (`Ipc/PipeContract.cs`), shared models.
- `src/Prism.Hardware`: LibreHardwareMonitor sensors and fan control.
- `src/Prism.Lighting`: OpenRGB client (TCP 6742), effects (`SpatialEffects.cs`), sensor lighting (`SensorLighting.cs`), Mi Home and Yeelight lamps.
- `src/Prism.Router`: ZLT router client.
- `src/Prism.Service`: background service (runs as admin or LocalSystem), named pipe `controlspawn`, lighting loop, automations. Builds `ControlSpawn.Service.exe`; Windows service key `ControlSpawn`.
- `src/Prism.FanGuard`: `ControlSpawn.FanGuard.exe`, the fan watchdog the service starts as a child process (never a Windows service). A build copies it beside the service exe.
- `src/Prism.App`: `ControlSpawn.exe`, WPF app, "Graphite HUD" dark theme (`Themes/Prism.xaml`, accent `#FF4D1A`, Segoe UI Variable), desktop widgets, tray.
- `tests/*`: xUnit tests (about 508), plus `tests/Prism.App.Tests/Preview`, an offscreen renderer for app pages.
- `docs/`: design (`DESIGN-LANGUAGE.md`, `design-v2/`), hardware notes (`HARDWARE.md`), briefs and status reports.
- `installer/`, `build/publish.ps1`: Inno Setup installer and self-contained publish.

## Working rules

- Build and test in **Debug**: `dotnet build Prism.sln -c Debug` (0 warnings expected) and `dotnet test Prism.sln -c Debug`. The owner's live app runs from `bin\Release`, so Release files may be locked.
- Look at UI changes with the preview tool, then open the PNG it writes:
  `dotnet run --project tests/Prism.App.Tests/Preview -c Debug -- <outdir> --v2 --page=Lighting`
- Run the service without hardware: `src/Prism.Service/bin/Debug/net10.0-windows/ControlSpawn.Service.exe --mock --console`; the app connects to it.
- Live hardware runs only on the owner's PC through `scripts/try-live.ps1` (admin). Don't try to drive real fans or RGB elsewhere.
- Match the surrounding code: dense C#, short comments that explain *why*, controls built in code with the theme's styles.
- Work on a branch cut from `ui/v2-pages`. Commit with clear messages ending in `Co-Authored-By: Claude <noreply@anthropic.com>`, push the branch, and open a pull request into `ui/v2-pages`. Never push to `main` or `ui/v2-pages` directly.

## The owner's hardware

ASUS ROG STRIX B850-F board. ARGB header 1 carries the AIO pump fan and then three radiator fans (48 LEDs, 12 per fan, radiator mounted on top). Header 2 carries the back and front case fans (36 LEDs). Nothing visible is on header 3. Also: Ryzen 7 9800X3D, RX 9070 XT (ASRock Taichi), 2× Corsair Vengeance DDR5, two Yeelight colour bulbs (Mi Home), a Careli air fryer, a ZLT 5G router. More in `docs/HARDWARE.md`.
