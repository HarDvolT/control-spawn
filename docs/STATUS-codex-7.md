# Codex round 7 status

Implemented the multiple Mi Home lamp brief. Protocol version remains 1. No commit was created.

## Service, discovery and credentials

- Settings retain the legacy `Lamp` value and add `Lamps`, with stable `lamp:<decimal deviceId>` identities, names, optional cached hosts, power, brightness, colour, follow mode and map points. An unresolved legacy lamp remains available until its identity can be learned; adding another identified lamp preserves that unresolved record.
- `MiioDiscovery` sends hello packets to `255.255.255.255:54321`, each active IPv4 interface's /24 directed broadcast, and unresolved legacy hosts. It collects replies for 1.5 seconds, validates the header, reads device ID/stamp in big endian, deduplicates devices and ignores malformed replies.
- Initial connections and retries resolve the current host by device ID. Shared discovery is limited to once per 30 seconds, including while lamps are unreachable. Resolved hosts are persisted as caches. Authenticated commands are sent only after the hello device ID matches the configured identity.
- Each lamp owns its client, sync smoothing, rate limit, retry state and worker. A failure in one worker leaves the other lamp running. Credential updates reconnect the affected lamp. Idle lamps also receive a periodic property check so an unavailable lamp can enter discovery/retry.
- Tokens use the existing CurrentUser DPAPI protection in separate per-device records. Legacy migration saves the protected token under the learned ID, persists settings, then removes the legacy token. Unresolved legacy credentials are retained. Removing a lamp removes its token and map point. Temporary token buffers are cleared after use.
- State retains `Lamp` as the first lamp and adds `Lamps`, including identity, name, device ID, resolved host, connection, transport, power, brightness, output colour, follow mode and error. Removing the first lamp promotes the next for legacy callers.
- OpenRGB connection refusal or timeout produces one `[rgb] OpenRGB not reachable` log line per minute while connection attempts continue. Other connection errors retain their diagnostics.

## Additive commands

- `setLampToken`: required 32-hex `token`, optional `deviceId` as a JSON decimal integer, decimal string or `0x`-hex string, optional `name` and optional legacy `host`. At least an ID or host is required. Tokens are never included in settings, state, responses or command logs.
- `discoverLamps`: returns `{ ip, deviceId, alreadyConfigured }` entries.
- `setLamp`: optional `lamp` ID, defaulting to the first configured lamp. Supports individual power, brightness, colour and `pc|average|own|map` follow, plus rename through optional `name`. Existing full-setting callers remain supported.
- `removeLamp`: `{ lamp: id }`.
- Lighting maps add a dictionary of points keyed by lamp ID. Existing single-lamp map data remains compatible.

## App and script

- The Lamp page lists every lamp with its name, colour dot, connection, transport and host. Select a lamp to control its power ToggleSwitch, brightness, colour, follow mode and name, or remove it.
- Find lamps lists discovered IPs/device IDs and configuration status. Selecting a result fills the provisioning ID. The selected device's token uses a hidden password input; IDs accept decimal extractor output or hex, and the optional host field can remain empty. Secrets clear on submission/unload and are not retained in the view model.
- Lighting maps draw and drag one point per lamp. Defaults match runtime sampling. The dashboard and Lamp widget show a separate colour/status row for each lamp; existing quick controls continue to address the first lamp.
- The mock client starts with two independently controllable miIO lamps and supports discovery, provisioning, rename, removal, scenes and first-lamp compatibility without storing secrets or making network calls.
- `scripts/live-lamp-router.ps1` discovers devices, prints their IDs/IPs, asks for each token using hidden input, and runs/logs the colour sequence for each lamp separately, including connection, transport, host and error. The fixed lamp address was removed. The script was syntax-checked only and was never executed.

## Verification

- `dotnet build Prism.sln -m:1 --no-restore -p:NuGetAudit=false`: succeeded, **0 warnings, 0 errors**.
- `dotnet test Prism.sln -m:1 --no-build --no-restore`: **389 passed, 0 failed, 0 skipped** (Hardware 34, Lighting 99, Router 68, Service 90, App 98).
- New coverage includes two byte-fixture hello replies, malformed/duplicate filtering, directed broadcasts and legacy host probes, cancellation, per-ID encrypted-token isolation, wrong-device rejection, shared 30-second scan throttling, independent lamp failure, decimal/hex/numeric provisioning, rename/remove/default-first commands, unresolved-legacy preservation, token migration and duplicate-identity merging without overwriting a provisioned token, host caching, settings round trips, independent map points, scene identity preservation, selected-lamp app commands, stable editing drafts and first-lamp promotion.
- The detached preview project built with **0 warnings, 0 errors** and rendered **97 PNGs**. Visually checked both-lamp dashboard/widget rows, independent map points, discovered devices, second-lamp controls, the narrow page and unresolved legacy state. Corrected the lists' default white appearance to match the graphite theme. `bindings.log` is empty.
- Preview output: `tests/Prism.App.Tests/obj/preview-codex-7/`. Reproduce with:

  ```powershell
  dotnet build tests/Prism.App.Tests/Preview/Prism.App.Preview.csproj -m:1 --no-restore -p:NuGetAudit=false
  dotnet run --project tests/Prism.App.Tests/Preview/Prism.App.Preview.csproj --no-build --no-restore -- tests/Prism.App.Tests/obj/preview-codex-7
  ```

- Builds used the existing dependency cache with restore disabled and audit disabled for the command; no repository audit configuration was changed.
- PowerShell parser syntax check for `scripts/live-lamp-router.ps1`: passed without executing the script.
- `git diff --check`: passed.

All execution uses fixture packets, injected fake transports/adapters, temporary test files or the detached offscreen mock preview. No real hardware, running service, registry, router or lamp was accessed. Production service/app entry points and the live script were not launched. Real network discovery, DPAPI under the service identity and lamp behaviour remain for a later authorized live test. Pre-existing live-test logs were left untouched.
