# How the code fits together

A short tour for new contributors. For the big picture, read [ARCHITECTURE.md](ARCHITECTURE.md). For where it's heading, read [PLAN-platform.md](PLAN-platform.md).

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

`src/ControlSpawn.App/Views/Pages/Lamp/LampTileViewModel.cs`

```csharp
private Task PushAsync() => RunLatestAsync("lamp:" + Id, Commands.SetLamp,
    new { lamp = Id, on = On, brightness = (int)Brightness, color = ColorEntry, follow = Follow, kelvin });
```

`RunLatestAsync` keeps only the newest value per key, so dragging a slider doesn't flood the pipe.

### 2. The contract names it

`src/ControlSpawn.Core/Ipc/PipeContract.cs` holds every command name in `Commands`, with its arguments in a comment. The app and service both use this file. The state the service pushes back is `StateMessage`, in the same file.

### 3. The pipe checks who's asking

`src/ControlSpawn.Service/PipeServer.cs` reads the message. A client that hasn't been verified can only read state, so its command is refused here.

### 4. The processor updates settings

`src/ControlSpawn.Service/CommandProcessor.cs` has one big `switch`. The `Commands.SetLamp` case finds the lamp, copies it with the new values, and builds the next settings. Settings live in `ServiceSettings` (`src/ControlSpawn.Service/Settings.cs`). They're validated and saved before the command succeeds.

### 5. A module makes it real

`src/ControlSpawn.Service/LampModule.cs` runs one worker per configured lamp, through `LampWorkerAsync` in `ServiceRuntime.cs`. The worker sees the new settings and talks to the bulb through `ILampClient` (`src/ControlSpawn.Core/Interfaces.cs`). The real clients are in `src/ControlSpawn.Lighting`, and tests use `FakeLampClient`.

Each module is a `HostedModule`. It has its own retry loop, so a failing lamp can't stop the fans.

### 6. The state comes back

The worker stores the lamp's state in `lampStates`, and the service pushes `StateMessage` about once a second. The app gets it through the `StateReceived` event on `IControlSpawnClient`, and the view model updates the page.

### 7. The tests

- `tests/ControlSpawn.Service.Tests/MultiLampTests.cs` and `LampWhiteCommandTests.cs` test commands without hardware.
- `tests/ControlSpawn.App.Tests/MultiLampTests.cs` tests the page logic.
- `src/ControlSpawn.App/Services/MockControlSpawnClient.cs` is a fake service inside the app.

## Adding a new command

1. Add the name and an argument comment to `Commands` in `PipeContract.cs`.
2. Add a `case` in `CommandProcessor.cs`, or in a partial file like `CommandProcessor.Automations.cs`.
3. If it saves something new, add it to `ServiceSettings` and validate it.
4. If it needs a device to change, update the module.
5. If the app should show the result, add it to `StateMessage` and `StateAssembler`.
6. Handle it in `MockControlSpawnClient.cs`, and add tests.
7. Call it from a view model with `Client.SendAsync(...)`.

Yes, that's a lot of places. It's the main reason for the plugin work in [RFC 0001](rfcs/0001-plugin-system.md).

## Where to find things

| You want to change | Look in |
|---|---|
| A page's look | `src/ControlSpawn.App/Views/Pages/<Page>/`. Controls are built in code with styles from `Themes/ControlSpawn.xaml` |
| The navigation | `src/ControlSpawn.App/Navigation/Pages.cs` |
| Fan curves and safety | `src/ControlSpawn.Hardware/FanController.cs` (extra review needed) |
| Which fan is which | `FanMap` and `VerifiedBoards` in `src/ControlSpawn.Hardware/LhmHardwareMonitor.cs` |
| Lighting effects | `src/ControlSpawn.Lighting/SpatialEffects.cs` |
| Automations | `src/ControlSpawn.Service/AutomationEngine.cs` |
| Saved settings | `src/ControlSpawn.Service/Settings.cs` |
| The installer | `installer/ControlSpawn.iss`, `installer/Configure-ControlSpawn.ps1`, `build/publish.ps1` |

## Known rough edges

- `ServiceRuntime.cs` and `CommandProcessor.cs` are large, and `IServiceRuntime` knows about Mi Home types.
- Fryer code lives in the lighting project, and the app references that project directly.
- The fan setup assumes one PC: headers 2, 6 and 7, and exactly two fan groups.

Issues are open for these. Please don't fix them as part of an unrelated PR.
