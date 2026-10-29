# Status — OpenCode: native router module (C# port)

Date: 2026-10-29. Scope: `src/Prism.Router`, `tests/Prism.Router.Tests`, this file.
Nothing else was edited; `Prism.sln` was not created or touched; nothing was committed.

Source of truth: `docs/router-reference/` (read only, never run). Behaviour, thresholds, wording,
scores and the command allow-list are kept as in the Python reference.

## What was ported

| Python | C# | Notes |
|---|---|---|
| `router_api.py` | `RouterClient.cs`, `HttpsRouterTransport.cs`, `RouterErrors.cs`, `Raw.cs` | Same 4 cmd UUIDs, allow-list + GET-only guard, token/login SHA-256 hashing, own-64-char session id fallback, `AuthError.AlreadyLoggedIn`, `RouterError`, session reuse/relogin, leading-junk `{` parse, 4 s timeout. Cert validation is disabled only for the configured router host. Behind `IRouterTransport`. |
| `ratings.py` | `Ratings.cs` (`SignalSummary`, `Link`, `Metric`) | Same thresholds, 0-100 score points, banks/ratings, bars (0-5), band labels ("n78", "B1+B3"), trend over 30 s, COLOR/PLAIN/VERDICTS/METRICS text, `threshold_text`, `weakest_metric`. |
| `scanner.py` | `Scanner.cs` (`Cell`, `Neighbor`, `ScanResult`, `Scanner.BestTracker`) | `DecodeEci`, `ServingCells`, `ParseNeighbors`, `Analyze` serving/stronger flags, PLMN lookup, best-cell tracking + the exact one-line copy text, `ToneHz`. |
| `pingmon.py` | `PingMonitor.cs` | Live monitor now uses `System.Net.NetworkInformation.Ping` (1 s interval, 5 min history) instead of ping.exe; `ParsePing`, `Stats`, `PingRating`, `PING_PLAIN` kept unchanged. |
| `speedtest.py` | `SpeedTest.cs` | ping/jitter/download/upload against speed.cloudflare.com on normal TLS, 10/8s/6 s phases, 4/3 streams, caps, warm-up, progress callback, cancellation. HTTP behind `ISpeedTestTransport`/`ISpeedTestConnection` so tests never hit the network. |
| `spikes.py` | `Spikes.cs`, `SpikeEvidence.cs`, `SpikeSampler.cs` | All constants, APPS/SERVICES tips, `Friendly`, `IsInternet`, `SpikeDetector`, `Evidence`, `FeedSignal` (router counters -> rates), `Classify`, `Offenders`, `IsBig`. Per-app sampling sits behind `IAppActivitySampler`; the shipped implementation reports "not available" (see below). |
| `devices.py` | `Devices.cs`, `DeviceTracker.cs` | OUI table, `NormMac`, `IsPrivateMac`, `Vendor`, `ParseArp`, `/24` subnet, `DeviceTracker.Refresh/ResolveNames/Sweep/Online/Label/IsNew`, `MergeRouterList` (DHCP names + Wi-Fi band/RSSI + cable), `ROUTER_DEVICE_TRAFFIC` hook off. ARP comes from `arp -a` parsing behind `IArpTableProvider` (no P/Invoke yet). |
| `credentials.py` | `CredentialStore.cs` | DPAPI `ProtectedData` (CurrentUser) in `%APPDATA%\Prism\router` (`credentials.json` / `session.json`), same `{"dpapi": base64}` record, secrets behind `ISecretProtector`. |

Safety kept: read-only commands only, the two read cmds GET-only, one shared client (session
reused, never a second login from `GetDevices`), no password logging, router certificate
exception scoped to the router host, speed test on normal TLS.

## Test mapping (Python -> xUnit, same cases and expected values)

| Python file / class | xUnit file / class |
|---|---|
| `test_score.py` `ScoreTests` | `ScoreTests.cs` `ScoreTests` |
| `test_score.py` `TrendTests` | `ScoreTests.cs` `TrendTests` |
| `test_scanner.py` `ScannerTests` (7 tests) | `ScannerTests.cs` `ScannerTests` |
| `test_ping_speed.py` `PingParseTests` | `PingSpeedTests.cs` `PingParseTests` |
| `test_ping_speed.py` `PingStatsTests` | `PingSpeedTests.cs` `PingStatsTests` |
| `test_ping_speed.py` `SpeedMathTests` (incl. cancel-before-start) | `PingSpeedTests.cs` `SpeedMathTests` |
| `test_spikes.py` `DetectorTests` | `SpikesAndDevicesTests.cs` `DetectorTests` |
| `test_spikes.py` `ClassifyTests` | `SpikesAndDevicesTests.cs` `ClassifyTests` |
| `test_spikes.py` `EvidenceTests` | `SpikesAndDevicesTests.cs` `EvidenceTests` |
| `test_spikes.py` `DeviceTests` | `SpikesAndDevicesTests.cs` `DeviceTests` |
| `test_widget_logic.py` `RatingTests` | `RouterClientTests.cs` `RatingTests` |
| `test_widget_logic.py` `ClientTests` | `RouterClientTests.cs` `ClientTests` (FakeRouter = the Python `FakeRouter`) |

Every Python test case has a C# counterpart. Four extra `CredentialStoreTests` cover the DPAPI
round-trip with a fake protector in a temp folder (the reference had no credential tests).

## Not compiled / not run, and why

- **No .NET SDK on this PC** (ARCHITECTURE says only the runtime is installed; `dotnet --list-sdks`
  returns nothing), so `dotnet build` / `dotnet test` were not possible. The code was written and
  reviewed by hand. Expect to fix small compile details on the first build.
- `net8.0-windows` was used because the module needs DPAPI; `EnableWindowsTargeting` is set so the
  projects can also restore on a non-Windows build host.
- `System.Security.Cryptography.ProtectedData` 8.0.0 is a NuGet package (not in the shared
  framework), so a restore is required before the first build.
- The test project references `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`,
  `coverlet.collector` at currently common versions (17.9.0 / 2.7.0 / 2.5.7 / 6.0.2); adjust if
  Codex pins different versions solution-wide.
- No network test, no router login, no hardware, no registry, no service calls were made.
  `PingMonitor.PingOnce`, `DeviceTracker.Sweep/ResolveNames` and the Cloudflare transport are
  implemented but only exercised through injected fakes (or not at all).
- `--mock` is a UI concept; this is a class library, so every network/hardware dependency is
  injected instead (`IRouterTransport`, `ISpeedTestTransport`, `IArpTableProvider`, `IReverseDns`,
  `ILocalTrafficSource`, `IAppActivitySampler`, `ISecretProtector`). Fakes live in the tests.

## Deliberate deviations / open questions

1. **Per-app spike attribution is not implemented yet.** `IAppActivitySampler` is the seam;
   `UnavailableAppActivitySampler` returns `Available = false`, so `Classify` still works but only
   reports signal / other-device / ISP causes. The real sampler needs `GetExtendedTcpTable` (P/Invoke)
   or Windows performance counters to replace psutil. This is the biggest missing feature.
2. **ARP source**: `arp -a` text parsing behind `IArpTableProvider` (as allowed by the brief).
   `GetIpNetTable` P/Invoke would avoid parsing localized command output.
3. **DPAPI record format**: same JSON as Python, but the folder is `%APPDATA%\Prism\router` (brief)
   instead of `%APPDATA%\RouterSignalWidget`, so an existing widget's saved password/session is not
   picked up. Best-effort `chmod 600` was dropped (DPAPI already scopes decryption to the user).
4. **Ping monitor RTT**: `Ping.Send` reports 0 ms for sub-millisecond replies, while `ParsePing`
   (kept for ping.exe text) still maps `<1ms` to 0.5 ms. The live monitor uses the real RTT.
5. **`IsInternet`**: reimplements Python's `ipaddress` private/loopback/link-local/multicast/
   unspecified rules for IPv4 plus IPv6 ULA/link-local; the full Python private table (e.g. all
   reserved blocks) is approximated.
6. **`Friendly`** uses `"?"` when `exe` is null/empty, as Python does for the key, but its detail
   string then says `?` rather than Python's `None`. No test depends on it.
7. **Router replies** are the Python dict shape (`Dictionary<string, object?>` with `List<object?>`
   for arrays). `AuthError`/`RouterError` keep the Python names (the brief uses them); the new
   `RouterPermissionError` stands in for Python's built-in `PermissionError`.
8. **HTTP status handling**: a non-2xx router reply becomes `RouterError("Router unreachable: HTTP …")`
   (Python's urllib raised `HTTPError`, reported the same way).
9. **`CredentialStore`** exposes `Load/Save/Forget` for password and session; `SaveSession("")`
   deletes the session file, matching `save_session`.
10. **Solution wiring**: `Prism.sln` is Codex's; these two `.csproj` files are not added to it.
