# Brief: redesign the "Driven by a sensor" lighting panel

Read `CLAUDE.md` first. Branch from `ui/v2-pages`, for example `sensor-panel`.

## The problem

On the Lighting page, the top-right switch has three modes: Whole case, Per device, Sensor. The **Sensor** mode lets a sensor drive a device's colours, for example GPU temperature turning the RAM from blue to red. The owner says it "doesn't match the app and is not a good UI/UX experience".

Today (`src/Prism.App/Views/Pages/Lighting/SensorBindingPanel.cs`, hosted from `LightingWorkspace.cs`) it is a flat list:
- a row of plain device tabs
- two full-width dropdowns ("Whole device", sensor), then a row of sensor buttons that repeats the dropdown
- a style switch (Gradient / Fill bar / Breathing / Brightness / Threshold)
- a raw text box, "Palette · 2–6 comma-separated hex colours"
- huge full-width orange sliders for range min, range max and smoothing
- a bare "Normal effect · choose a source and Apply sensor" label with an empty text cursor under it
- a "Red pulse alert above" toggle with a full-width number box
- "Apply sensor" and "Use normal effect" buttons

## The goal

It should look and feel like the rest of the v2 app. Use the same cards, typography, `SegmentedControl`, `ThickSlider`, `ColorSwatch`, chips and styles as the other Lighting panels (`src/Prism.App/Themes/Prism.xaml`, `src/Prism.App/Controls/`). Suggested shape:

- **Left card, targets:** each RGB device as a selectable tile. For the motherboard, show its parts too: header 1 is "Pump & radiator", header 2 is "Case fans" (`CaseMap.PartName` in `src/Prism.Lighting/SpatialEffects.cs` gives these names). Each tile shows whether a sensor drives it and the live reading, e.g. "CPU temp · 52 °C".
- **Right side, one card per step:**
  - *Sensor:* tiles for CPU temp, GPU temp, GPU usage, GPU power, Ping and so on, each with its live value. One control, not a dropdown plus duplicate buttons.
  - *Look:* the style switch with a small live preview strip of what the LEDs do across the range.
  - *Colours:* palette presets plus editable swatches instead of the hex text box.
  - *Range:* one compact dual-ended range control, or two compact sliders with units, plus auto-learn.
  - *Extras:* smoothing, and the alert pulse with a compact number stepper.
- **Clear state:** "Driven by CPU temp · 52 °C" on the selected target, one primary **Apply** and a quiet **Stop using sensor**. Remove the stray empty text field.
- **Performance:** don't rebuild the visual tree every second; update values in place.

Keep the existing service protocol (`SensorBinding` and its commands in `src/Prism.Core/Ipc/PipeContract.cs`, `src/Prism.Lighting/SensorLighting.cs`) unless a small addition is clearly needed. If you change the contract, add tests.

**Stay inside the sensor panel.** The main session is editing `CaseIllustration.cs`, `CaseMapPreview.cs`, `SpatialEffects.cs`, the "Applies to" chips in `LightingWorkspace.cs`, and the service lighting loop. In `LightingWorkspace.cs`, change only how the sensor panel is hosted.

## Done means

- The preview tool renders the sensor mode, and the PNG looks like part of the app. Add a preview flag that opens the Lighting page in Sensor mode if there isn't one.
- It works against the mock service: bind a device to a sensor, change the style, colours and range, apply, then stop.
- `dotnet build Prism.sln -c Debug` has 0 warnings and `dotnet test Prism.sln -c Debug` passes.
- Push your branch and open a pull request into `ui/v2-pages` with before/after screenshots.
