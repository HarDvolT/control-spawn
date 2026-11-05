# How the code fits together

A short tour for new contributors. For the big picture, read [ARCHITECTURE.md](ARCHITECTURE.md). For where it's heading, read [PLAN-platform.md](PLAN-platform.md).

The source uses the old code name Prism. Control Spawn and Prism are the same project.

## The idea in one minute

```text
App (WPF, normal user)
  |  sends a command, gets state pushed back
  |  named pipe, one JSON message per line
Service (LocalSystem)
  |-- CommandProcessor   checks the command, updates settings
  |-- Modules            loops that apply settings to real devices
  |-- StateAssembler     builds the state sent to the app
```

Two rules explain most of the code:

1. **The app never touches hardware.** It sends commands and draws the state it receives.
2. **Commands change settings, and modules act on settings.** A command doesn't usually drive a device itself. It saves what the user wants, and a module loop makes the device match.

## Walkthrough: changing a lamp's brightness

Follow one real feature from slider to bulb.

### 1. The page sends a command

`src/Prism.App/Views/Pages/Lamp/LampTileViewModel.cs`

```csharp
private Task PushAsync() => RunLatestAsync("lamp:" + Id, Commands.SetLamp,
    new { lamp = Id, on = On, brightness = (int)Brightness, color = ColorEntry, follow = Follow, kelvin });
```

`RunLatestAsync` keeps only the newest value per key, so dragging a slider doesn't flood the pipe.

### 2. The contract names it

`src/Prism.Core/Ipc/PipeContract.cs` holds every command name in `Commands`, with its arguments in a comment. The app and service both use this file. The state the service pushes back is `StateMessage`, in the same file.

### 3. The pipe checks who's asking

`src/Prism.Service/PipeServer.cs` reads the message. A client that hasn't been verified can only read state, so its command is refused here.

### 4. The processor updates settings

`src/Prism.Service/CommandProcessor.cs` has one big `switch`. The `Commands.SetLamp` case finds the lamp, copies it with the new values, and builds the next settings. Settings live in `ServiceSettings` (`src/Prism.Service/Settings.cs`). They're validated and saved before the command succeeds.

### 5. A module makes it real

`src/Prism.Service/LampModule.cs` runs one worker per configured lamp, through `LampWorkerAsync` in `ServiceRuntime.cs`. The worker sees the new settings and talks to the bulb through `ILampClient` (`src/Prism.Core/Interfaces.cs`). The real clients are in `src/Prism.Lighting`, and tests use `FakeLampClient`.

Each module is a `HostedModule`. It has its own retry loop, so a failing lamp can't stop the fans.

### 6. The state comes back

The worker stores the lamp's state in `lampStates`, and the service pushes `StateMessage` about once a second. The app gets it through the `StateReceived` event on `IPrismClient`, and the view model updates the page.

### 7. The tests

- `tests/Prism.Service.Tests/MultiLampTests.cs` and `LampWhiteCommandTests.cs` test commands without hardware.
- `tests/Prism.App.Tests/MultiLampTests.cs` tests the page logic.
- `src/Prism.App/Services/MockPrismClient.cs` is a fake service inside the app.

## Adding a new command

1. Add the name and an argument comment to `Commands` in `PipeContract.cs`.
2. Add a `case` in `CommandProcessor.cs`, or in a partial file like `CommandProcessor.Automations.cs`.
3. If it saves something new, add it to `ServiceSettings` and validate it.
4. If it needs a device to change, update the module.
5. If the app should show the result, add it to `StateMessage` and `StateAssembler`.
6. Handle it in `MockPrismClient.cs`, and add tests.
7. Call it from a view model with `Client.SendAsync(...)`.

Yes, that's a lot of places. It's the main reason for the plugin work in [RFC 0001](rfcs/0001-plugin-system.md).

## Where to find things

| You want to change | Look in |
|---|---|
| A page's look | `src/Prism.App/Views/Pages/<Page>/`. Controls are built in code with styles from `Themes/Prism.xaml` |
| The navigation | `src/Prism.App/Navigation/Pages.cs` |
| Fan curves and safety | `src/Prism.Hardware/FanController.cs` (extra review needed) |
| Which fan is which | `FanMap` and `VerifiedBoards` in `src/Prism.Hardware/LhmHardwareMonitor.cs` |
| Lighting effects | `src/Prism.Lighting/SpatialEffects.cs` |
| Automations | `src/Prism.Service/AutomationEngine.cs` |
| Saved settings | `src/Prism.Service/Settings.cs` |
| The installer | `installer/Prism.iss`, `installer/Configure-ControlSpawn.ps1`, `build/publish.ps1` |

## Known rough edges

- `ServiceRuntime.cs` and `CommandProcessor.cs` are large, and `IServiceRuntime` knows about Mi Home types.
- Fryer code lives in the lighting project, and the app references that project directly.
- The fan setup assumes one PC: headers 2, 6 and 7, and exactly two fan groups.

Issues are open for these. Please don't fix them as part of an unrelated PR.
