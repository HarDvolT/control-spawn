# .NET 10 migration status

2026-11-05, branch `chore/dotnet-10`. Changes are uncommitted. Migration builds cleanly; full test sign-off is blocked by this execution environment's named-pipe access restrictions.

## Changes

- Moved the shared target and every explicit project target, including the offscreen Preview project, to `net10.0` / `net10.0-windows`. Projects without an explicit target inherit it from `Directory.Build.props`.
- Raised the existing analyzer level from 8.0 to 10.0. Kept C# 12, existing warning policies, invariant globalization, product version, executable names and IPC/settings schemas.
- Updated script paths, development-path test fixtures, README/CLAUDE instructions, architecture targets, reusable preview/packaging instructions and the Settings design reference's runtime label.
- Reviewed `git grep -n -E 'net8|\.NET 8|8\.0\.'`. Remaining old-framework mentions in earlier briefs and implementation reports describe historical work; historical SDK/package versions remain factual. IP addresses matched by `8.0.` are unrelated. `build/publish.ps1` and `installer/` contain no old-framework hardcodes and need no edits. Installer `AppVersion` and `AppId` are unchanged.
- Fixed three CS9191 diagnostics by passing GUIDs to `Marshal.QueryInterface` with `in` rather than `ref`.
- Fixed WFO0003 by moving PerMonitorV2 DPI selection from the manifest to `Program.Main`, before constructing the WPF application. Kept the manifest's long-path and OS declarations. Removed the old WFAC010 `NoWarn`; added no suppression or analyzer disablement.
- No `global.json` was needed: the installed SDK already selects .NET 10.

## Packages and toolchain

| Package | Before | After | Direct consumers |
| --- | --- | --- | --- |
| System.Security.Cryptography.ProtectedData | 8.0.0 | 10.0.14 | Core, Lighting, Router |
| System.ServiceProcess.ServiceController | 8.0.0 | 10.0.14 | Hardware, Service |

These are Microsoft-authored `System.*` packages. No `Microsoft.*` package was pinned to 8.x. All other direct package pins remain unchanged, including Microsoft.NET.Test.Sdk, xUnit, coverlet and LibreHardwareMonitorLib. ServiceController resolves System.Diagnostics.EventLog 10.0.14 transitively.

Validation used Windows x64, SDK **10.0.403**, MSBuild **18.9.15**, and runtime **10.0.14**.

## Validation

- Final `dotnet build Prism.sln -c Debug -m:1 -p:UseSharedCompilation=false`: **0 warnings, 0 errors**.
- The requested full `dotnet test Prism.sln -c Debug` was attempted; parallel MSBuild cannot establish its worker connections here. Running with `-m:1 -p:UseSharedCompilation=false` reaches the tests. Hardware, Lighting, Router and Service all pass, but the App suite stalls in its test-pipe handshake.
- Isolated reruns identify `HardeningUiTests.ConnectedOnlyAfterTheServiceAcceptsTheHelloAndReadOnlyRepliesRaiseTheBanner` and `HardeningUiTests.RefusedHelloNeverShowsConnected` as blocked. Temporary tracing showed repeated `UnauthorizedAccessException` in `NamedPipeClientStream.TryConnect`, before the test server accepts its connection. Isolated runs with 15–45 second hang timeouts also abort. The original stalled App host was stopped; subsequent abort messages come from the diagnostic hang timeout, not an observed application crash. All temporary tracing was removed, and both tests remain unchanged.
- A diagnostic solution run excluding only those two tests passes **655 tests, 0 failed, 0 skipped**. This is not a passing full-suite result: **657 tests are expected, with 2 blocked**.

| Test project | Passed in final diagnostic run | Blocked |
| --- | ---: | ---: |
| Hardware | 41 | 0 |
| Lighting | 180 | 0 |
| Router | 68 | 0 |
| Service | 214 | 0 |
| App | 152 | 2 |
| Total | 655 | 2 |

`FailedModuleBacksOffWhileSiblingKeepsRunning` passed in both solution runs that reached the Service tests. No tests were deleted, weakened, marked skipped or given a permanent filter. The diagnostic filter was:

```text
FullyQualifiedName!~ConnectedOnlyAfterTheServiceAcceptsTheHelloAndReadOnlyRepliesRaiseTheBanner&FullyQualifiedName!~RefusedHelloNeverShowsConnected
```

The host denies writes to the normal CLI home and its native TLS stack fails NuGet requests with "No credentials are available in the security package". Validation therefore used temporary `DOTNET_CLI_HOME` and `NUGET_PACKAGES` directories, and `RestoreSources` pointing at a temporary local feed. That feed contains existing cached packages plus official api.nuget.org packages downloaded over certificate-verified HTTPS using Python. No repository NuGet configuration or audit setting was changed. An online vulnerability audit was not verified. Failed parallel build/test worker processes started by this task were cleaned up.

Test result directories are under `%TEMP%/controlspawn-dotnet10-results`, `%TEMP%/controlspawn-dotnet10-runnable-results`, and `%TEMP%/controlspawn-dotnet10-ui-*`.

### Preview and publish

- Built Preview in Debug with 0 warnings/errors, then ran `dotnet run --project tests/Prism.App.Tests/Preview -c Debug --no-build -- <outdir> --v2 --page=Lighting`. The ordinary run was attempted first but encountered the same parallel-build restriction.
- Output: `%TEMP%/controlspawn-dotnet10-preview/V2-Lighting.png`. Opened and visually checked the PNG: the shell, case illustration, colour controls and effects render. `bindings.log` is empty; renderer exit code is 0.
- Published Service, App and FanGuard separately in **Debug**, `win-x64`, self-contained, with `PublishReadyToRun=true`, `PublishTrimmed=false`, `PublishSingleFile=false`. All three succeeded without diagnostics. Outputs are under `%TEMP%/controlspawn-dotnet10-publish/Prism.*`; none were executed. Runtime configs include .NET 10.0.14, with ASP.NET Core for the service and WindowsDesktop for the app. Shared Service/FanGuard files are byte-identical, matching the packaging script's merge requirement.
- Did not run the full Release packaging script, compile/sign an installer, install anything, run live scripts, drive hardware, or stop/change the installed Control Spawn service/app. Release packaging and installed-service/live testing remain owner checks. Interactive multi-monitor DPI transitions were not checked by the offscreen preview.

## Runtime compatibility review

- **Service:** [.NET 10 runs all of BackgroundService.ExecuteAsync on a background task](https://learn.microsoft.com/en-us/dotnet/core/compatibility/extensions/10.0/backgroundservice-executeasync-task). `PrismWorker` already begins with `Task.Yield()` and does not rely on synchronous startup work there. Its pipe-before-hardware ordering, cancellation, restore-defaults paths and ten-second shutdown timeout remain unchanged. The custom ServiceBase SCM lifetime still requires an installed-service check.
- **Fan guard:** No watchdog, heartbeat, parent-process or fan-write behavior was changed. Its invariant globalization remains enabled. The new binaries compile/publish and share identical dependency files with the service; actual child-process crash recovery and hardware hand-back were not exercised.
- **Named pipe:** Name, ACL construction, verifier, protocol version and JSON line framing are unchanged. Development output paths now use the new framework directory. Test access denial prevents end-to-end handshake sign-off here; do not broaden production pipe ACLs to accommodate this environment.
- **JSON settings:** [.NET 10 configuration binding preserves nulls and empty arrays](https://learn.microsoft.com/en-us/dotnet/core/compatibility/extensions/10.0/configuration-null-values-preserved). Product settings use explicit System.Text.Json loading/validation rather than that binder. [.NET 10 also validates reserved metadata-property conflicts](https://learn.microsoft.com/en-us/dotnet/core/compatibility/serialization/10/property-name-validation); current settings/IPC models do not enable reference-preserving or polymorphic metadata. Settings schema, camel casing, enum conversion, null omission, repair/backups and DPAPI scopes remain unchanged; their runnable tests pass.
- **Self-contained publish:** The runtime is now bundled as 10.0.14; the machine's installed .NET 8 runtime is not used by those outputs. [.NET 10 changes native-library lookup for single-file apps](https://learn.microsoft.com/en-us/dotnet/core/compatibility/interop/10.0/native-library-search), but this product explicitly publishes multiple files without trimming. Retain the separate app/service folders and publish FanGuard alongside the service. Moving from 8 also crosses [.NET 9's default CET support change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/9.0); native hardware-driver interoperability remains a live verification item.

Stopped with the named-pipe environment blocker documented. Full approval still needs an unrestricted Debug solution test run with all 657 tests passing, plus the owner's later packaging/live checks.

## Reviewer's rerun

The two blocked tests came from the Codex sandbox. Outside it, the full run passes: 657 tests, 0 failed, on .NET 10. The installer and live hardware are still untested.
