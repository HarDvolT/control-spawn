# Brief for OpenCode: native router module

Read docs/ARCHITECTURE.md first and follow its rules.

Port the Python router widget core to native C# .NET 8, in `src/Prism.Router` with xUnit tests in
`tests/Prism.Router.Tests`. The Python reference is copied to `docs/router-reference/` (READ ONLY, never edit it, never run it):
`router_api.py`, `ratings.py`, `scanner.py`, `pingmon.py`, `speedtest.py`, `spikes.py`, `devices.py`,
`credentials.py`, plus `tests/` and `INTEGRATION.md`.

Port, keeping behaviour identical (same thresholds, ratings, scores, wording):

1. `RouterClient` (ZLT X17M API: same cmd UUIDs, the same 4-command allow-list, GET-only reads, token +
   login hashing exactly as in Python, session reuse, AuthError(alreadyLoggedIn), RouterError, the router's
   self-signed HTTPS cert accepted only for the router host, 4 s timeout). Behind an `IRouterTransport`
   so tests never hit the network.
2. `Ratings` (summarize -> ratings, score 0-100, bars, band, trend, PLAIN sentences, METRICS).
3. `Scanner` (serving + neighbor cells, decode_eci, BestTracker).
4. `PingMonitor` (use System.Net.NetworkInformation.Ping to 1.1.1.1 every 1 s instead of ping.exe;
   keep parse/stats/rating logic), `SpikeDetector` + `Evidence` + `Classify` (+ the per-app activity sampler
   behind an interface; real one may use Windows performance counters / GetExtendedTcpTable later).
5. `SpeedTest` (speed.cloudflare.com, ping/jitter/download/upload, progress + cancellation, normal TLS).
6. `DeviceTracker` (ARP table via GetIpNetTable P/Invoke or `arp -a` parsing, reverse DNS, vendor lookup,
   merge with router list).
7. `CredentialStore` using DPAPI (`ProtectedData`, CurrentUser) in `%APPDATA%\Prism\router`.

Port every Python test in `tests/` to xUnit with the same cases and expected values.
Do not log in to the real router, do not run network tests against real hosts.

Finish with a summary in docs/STATUS-opencode.md: what was ported, test mapping, what was not compiled
and why, open questions.


Do not create or edit Prism.sln (Codex owns it); create your two .csproj files only. Codex works in other folders at the same time.
