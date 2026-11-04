# Brief: rename what Windows shows from Prism to Control Spawn

Repo: https://github.com/HarDvolT/control-spawn. Read `CLAUDE.md` first: build, test and branch rules are there.

## The goal

The owner opens Task Manager and sees "Prism.App" and "Prism.Service". Everything Windows shows must say **Control Spawn**, and each background part must be named for what it does.

The internal code name stays: namespaces, project folders, `Prism.sln`, the `prism` pipe, and the data folders are not part of this task.

## Target names

| Part | Today | Target |
|---|---|---|
| App, Task Manager name | `Prism.App` | `Control Spawn` |
| App, exe | `Prism.App.exe` | `ControlSpawn.exe` |
| Background service, Task Manager name | `Prism.Service` | `Control Spawn Hardware Service` |
| Background service, exe | `Prism.Service.exe` | `ControlSpawn.Service.exe` |
| Windows service, key (Name column in the Services tab) | `Prism` | `ControlSpawn` |
| Windows service, display name | `Control Spawn` | `Control Spawn Hardware Service` |
| Fan watchdog process | `Prism.Service.exe --fan-guard <pid>` | Own name: `Control Spawn Fan Guard`, `ControlSpawn.FanGuard.exe` |
| OpenRGB | `OpenRGB.exe` | Leave as it is. It's bundled, unmodified third-party software. |

The owner hasn't approved these exact strings, so show them the table before building.

## How Windows gets these names

- Task Manager's Processes tab shows the file's **FileDescription**. The installed files have `FileDescription: Prism.App` and `Prism.Service`, because no `AssemblyTitle` is set and .NET falls back to the assembly name. `Product` is already `Control Spawn` in `Directory.Build.props`, and `CompanyName` is also `Prism.Service`, so set `Company` too.
- The Details tab shows the **exe name**, which comes from `AssemblyName`.
- The Services tab shows the **service key** and the display name.

So do this in stages. Setting `AssemblyTitle` and `Company` fixes the main complaint with almost no risk. Do it first, as its own commit.

## Where the names live

Run `git grep -nE "Prism\.(App|Service|FanGuard)"` and `git grep -n "'Prism'"` to get the full list. The ones that matter:

- `src/Prism.App/Prism.App.csproj:13` `AssemblyName`. `src/Prism.Service/Prism.Service.csproj` sets none.
- `src/Prism.Service/WindowsServiceHosting.cs` `ServiceName = "Prism"`. It must match the installed key exactly.
- `src/Prism.Service/FanGuard.cs:57` hard-codes `Prism.Service.exe`. `:87` checks that the parent has the same path as the guard, which breaks once the guard is its own exe.
- `src/Prism.Service/Program.cs:12` handles `--fan-guard`.
- `src/Prism.Service/PipeClientVerifier.cs:29-30,53,65` allows only the app with these file names and recorded hashes. If this is wrong the app connects but gets no commands.
- `src/Prism.App/Services/StartupRegistry.cs:59,71`.
- `installer/Configure-Prism.ps1`: about 20 places, including the Run-key cleanup, hash recording, `Assert-OwnedService`, `Register-PrismService`.
- `installer/Prism.iss:26,39,43`, `build/publish.ps1:77-96`, `scripts/package-check.ps1:96`.
- `scripts/install-service.ps1`, `scripts/uninstall-service.ps1`, `scripts/try-live.ps1` and every `scripts/live-*.ps1`. `install-service.ps1` still uses `delayed-auto`, which setup dropped on purpose; change it to match.
- `tests/Prism.Service.Tests/HardeningTests.cs:123-127,205-209`.

`build/publish.ps1` and `Configure-Prism.ps1` already have an optional `Prism.FanGuard` slot, but no such project exists. Today the guard is the service exe run again. A separate exe needs a small new project, and it has to keep doing its job: if the service dies, the fans must go back to the BIOS.

## Upgrading existing installs

People have 1.0.0, 1.0.1 and 1.0.2 installed, so setup must move them over safely:

1. **Stop the old service before touching files.** `Prepare` must gracefully stop the old `Prism` key. Never kill it: the graceful stop is what hands the fans back to the BIOS.
2. **Delete the old key, create the new one.** Uninstall must handle both keys. The new key needs the same failure actions and plain `auto` start.
3. **Remove the old exes.** Inno won't delete them, so use `[InstallDelete]`, or `Prism.App.exe` stays behind.
4. **Keep the data.** `C:\ProgramData\Prism` and `%APPDATA%\Prism` stay, so settings, tokens and cook history carry over. Don't rename them.
5. **Repoint the Startup shortcut.** `Control Spawn.lnk` targets the old exe. Setup only rewrites it when the tray option is ticked, and the app counts any existing file as enabled, so an unticked install keeps a dead shortcut. Fix or remove it.
6. **Update the Start menu shortcut and `UninstallDisplayIcon`.**
7. **Record hashes for the new file names** in `app-hashes.json`.
8. **Keep the `AppId`** in `Prism.iss`, or Windows treats it as a different program.

## Traps we already hit

- **Quotes in service paths.** In Windows PowerShell 5.1, `sc.exe` loses the quotes on a path with spaces. That broke reinstall and uninstall in 1.0.0 and 1.0.1. Write `ImagePath` with `Set-ItemProperty`, as `Register-PrismService` does. `Assert-OwnedService` refuses a service whose path it doesn't recognise, so add the new names and keep accepting the old ones, quoted and unquoted.
- **Don't use the Run key for autostart.** Windows 25H2 never ran it on the owner's PC, and we don't know why. The Startup folder shortcut works.
- **Don't change a boot fix without a restart test.** A theory about the Startup apps record looked right and was wrong.
- **`OnShutdown` must not call `RequestAdditionalTime`.** It crashed the service on every Windows shutdown. Keep that behaviour.
- **OpenRGB starts empty as SYSTEM** and finds the board late, so saved header sizes are applied on connect and on refresh. Keep both.

## Testing

Normal checks: `dotnet build Prism.sln -c Debug` with 0 warnings, and `dotnet test Prism.sln -c Debug --no-build`. `HardeningTests.FailedModuleBacksOffWhileSiblingKeepsRunning` has an existing timing race and fails now and then; re-run it before blaming your change.

Unit tests can't prove this task. On the owner's PC, with their approval, check all of these:

1. Task Manager's Processes, Details and Services tabs show the new names.
2. Install over 1.0.2. The app connects and commands work, including a lighting change.
3. Run setup a **second** time over the new version. This is what caught the quotes bug.
4. Restart. The service starts in about 25 s and the app appears in the tray in about a minute. Fans and case RGB work with no window open.
5. Shut down, then check Event Viewer for `Prism.Service` or the new exe under Application Error.
6. Kill the service in a controlled test, and confirm the fan guard still hands fans back. Only do this if the owner agrees.
7. Uninstall. Check that both service keys, the shortcut and the install folder are gone.

Practical notes:
- Stop any `try-live.ps1` run before setup. It holds the same pipe and writes old settings over setup's.
- Don't run the exe with `--mock` while the real service is up. It fails on the pipe and logs errors that look like real crashes.
- Setup's admin prompt often opens behind other windows. Tell the owner to look for the shield icon on the taskbar.
- Real fans and RGB only run on the owner's PC, through setup or `scripts/try-live.ps1`.
- The installer output is `build/out`. `publish.ps1` refuses to run if `build/out/Prism` exists, so move it aside first.

## Delivery

- Cut a branch from `ui/v2-pages`, which holds the merged 1.0.2 code, and open a PR into `ui/v2-pages`. Never push to `main` or `ui/v2-pages` directly.
- Commit, push, merge and publish only when the owner says so, one step at a time. Don't publish a release, even after a working build, without a clear yes to that release.
- Name the next version **1.0.3**. Change it in `installer/Prism.iss`, `Directory.Build.props` and the README badge.
- README's project layout table explains that the code is still called Prism. Keep that, and add any new project to it.
