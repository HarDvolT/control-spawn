# Codex round 8 status

Implemented the round 8 registry, discovery, import, naming and offline MIoT path. Protocol remains version 1 with additive commands and optional state fields. No commit was created.

## Registry and discovery

- `ServiceSettings.Devices` persists stable identities, kind, detected/display name, sources, first/last seen, online/offline state, capabilities, cached endpoint, model/firmware and MAC/vendor. Source observations merge by device identity or normalized MAC, preserving user names, dismissal, provisioning and first-seen time. IP addresses remain endpoint caches rather than identities.
- State adds `Devices`, `NewDevices`, `Discovery` options and `Appliances`. Dismissed or configured devices do not reappear in the new-device list. Completed discovery passes expire absent devices after 150 seconds and restore their previous online classification when rediscovered.
- Background miIO/Yeelight discovery runs every 60 seconds. Explicit Find devices requests are throttled to ten seconds; the existing shared miIO resolver retains its 30-second minimum between broadcasts. Authenticated `miIO.info` reads model, firmware and MAC once per credential revision. Unknown models remain listed as unsupported. The lamp worker verifies the reported model before issuing lamp setters.
- Yeelight LAN advertisements offer token-free lamp setup. OpenRGB identities enter the registry from its existing refresh-in-place path. Auto-join can be disabled; new devices then retain their own mode until explicitly joined. Joining enters direct mode and the case-map setup flow offers placement.
- Positive finite RPM on a previously unused fan header creates `Fan header N` with a persisted Standard configuration. Header identity remains separate from display name; duplicate names cannot select the wrong header. Header 7 remains a fixed pump. Scenes include the newly detected fan groups.
- The router polling adapter uses only the existing read-only connected-devices command, at most once per minute, and publishes network name/IP/MAC/vendor plus the router itself. No new router mutation API was added.

## Import, naming and app

- Devices is in the GENERAL sidebar group, with devices grouped by kind, status/details, a new-device banner, Set up and Dismiss. Setup joins RGB and opens its case map, opens fan curves, offers lamp token import, or acknowledges network information.
- Extractor text, nested JSON and quoted CSV are parsed by decimal/hex device ID. The import panel accepts paste, drop or file selection, shows masked tokens and unchecked device choices, and saves only selected records through the existing per-device DPAPI store. A selected discovered device can also accept a single pasted 32-hex token. Imports use discovered endpoint caches in preference to extractor IPs. Secret inputs and records clear on submission/unload.
- Optional Sign in with Xiaomi is behind its own button and `IXiaomiTokenProvider`. It uses an in-memory HTTP/cookie session, a region picker, encrypted device-list requests and the same import parser. A two-step response opens its HTTPS verification URL and asks the user to retry sign-in after verification. Account credentials are never persisted. This path was exercised only with an injected HTTP handler and sanitized protocol fixtures, never Xiaomi cloud.
- `renameDevice { id, name }` trims and validates 1–40 characters; `reset: true` restores the detected name. Names persist independently of scene settings. The shared pencil editor supports Enter to save and Escape to cancel in Devices, Lamp, Lighting device tabs/sync rows and Fans cards. Device names are projected into lamp/dashboard/widget rows, RGB/case-map state, fan state and tray notifications. Editing drafts survive ordinary status refreshes.
- Settings adds new-device tray notifications, auto-join RGB and optional completion lighting flash. Search adds Find devices and Import tokens. The tray reports new devices, cooking completion and basket reminders, and exposes a direct cancel-cooking item. The Air fryer widget displays status/remaining time and one-click cancellation, without a start action.
- The mock client includes a registry, new devices and a maf10a card, and implements rename/reset, dismissal, import, clamping and confirmation without retaining tokens or accessing the network.

## MIoT and heating safety

- The generic MIoT adapter implements `miIO.info`, batched `get_properties`, batched `set_properties` and `action` on the existing authenticated transport. Property operations retain bounded retries and the 500 ms rate limit. Actions deliberately do not retry after ambiguous delivery, so a cooking action cannot be duplicated by a timeout retry.
- Exact-model offline JSON specs are bundled for `careli.fryer.maf10a` and `careli.fryer.maf02`. Boolean, ranged numeric, enum and read-only properties map to controls; supported actions map to buttons. Unknown models expose read-only model/firmware metadata and cannot execute unverified property/action IDs.
- The air-fryer page includes mapped operating/fault state, target temperature/time, remaining time with an animated ring, mode selection, start/resume, pause, cancel and basket reminders. It does not invent a current temperature: maf10a does not report that property.
- Start/resume requires an app-origin prepare request followed by an explicit confirmation dialog and a short-lived one-use nonce. Settings changes invalidate an outstanding nonce. The generic action command permits only pause/cancel; widget pipe sessions cannot prepare/confirm starts. Scenes, search, schedules and widgets have no cooking-start path. Start actions requiring recipe input payloads are intentionally unavailable rather than guessed.
- Temperature/time values clamp to the exact model's ranges and steps. Writable status, reservation and recipe-sync fields are blocked by a separate service allowlist. Startup/polling never replays cooking settings or actions. On contact loss while cooking, state retains the last reading and warns that the fryer's firmware remains in control. Cancellation remains exposed for known offline specs.

### Spec provenance and limits

The required raw `miot-spec.org/miot-spec-v2/instance?type=<URN>` endpoint could not be retrieved in this environment. Bundled files are explicitly normalized tables transcribed from the public model spec pages, not represented as raw downloaded instances. Sources, exact URNs and the safety restriction are recorded in [the spec README](../src/Prism.Lighting/Specs/README.md). No runtime spec fetch is required.

No live behavior is claimed: real multicast/broadcast reception, actual firmware responses, service-identity DPAPI and Xiaomi authentication/verification remain untested. Unknown models have metadata-only inspection until a verified offline spec is added.

## Verification

- `dotnet build Prism.sln -m:1 --no-restore -p:NuGetAudit=false`: succeeded, **0 warnings, 0 errors**.
- `dotnet test Prism.sln -m:1 --no-build --no-restore`: **424 passed, 0 failed, 0 skipped** (Hardware 34, Lighting 114, Router 68, Service 106, App 102).
- Added fixture coverage for multi-source identity/MAC merging, offline recovery and dismissal, strictly positive finite fan detection, persisted Standard curves and RGB auto-join options, selected token imports with decimal IDs `470481367` / `470481368`, extractor text/nested JSON/CSV, secret redaction, exact-model spec selection, ranges/steps, MIoT property/action packets and batching, confirmation expiry/replay/invalidation, read-only startup, cooking contact loss, metadata caching, duplicate-name fan selection, command search and widget cancellation.
- Optional cloud tests use an injected HTTP handler with sanitized response fixtures, cover login/device-list parsing and verification URL handling, and check RC4-drop1024 against the RFC 6229 vector. No live HTTP client is used in those tests.
- Detached preview project: **0 warnings, 0 errors**; **106 PNGs** rendered under `tests/Prism.App.Tests/obj/preview-codex-8/`, with an empty `bindings.log`. Visually reviewed Devices/import/narrow layouts, lighting rename controls, Fans, cooking/offline fryer cards and the cancellation widget. Fixed token-field contrast, duplicate device labels and action placement during QA.
- `git diff --check`: passed. Build/test dependencies used the existing cache with restore and audit disabled for these commands; no repository audit configuration changed.

Reproduce the detached preview without starting the production app or service:

```powershell
dotnet build tests/Prism.App.Tests/Preview/Prism.App.Preview.csproj -m:1 --no-restore -p:NuGetAudit=false
dotnet run --project tests/Prism.App.Tests/Preview/Prism.App.Preview.csproj --no-build --no-restore -- tests/Prism.App.Tests/obj/preview-codex-8
```

## Boundaries

Execution was limited to builds, tests with injected fake transports/adapters and temporary test files, and the detached offscreen mock preview. Production app/service entry points and live scripts were not launched. No real hardware, running services, Windows registry, router, lamp, fryer or Xiaomi cloud was accessed. Public specification/reference pages were read for development. No commits were created. An unrelated change to `docs/live-lamp-router.txt` appeared externally during this work; that file was not read, modified or reverted.
