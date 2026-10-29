# Brief (Codex, parallel branch `harden`): architecture hardening — production quality

You work in your own git worktree on branch `harden`. Another agent is redesigning the WPF app on `main` at the same
time; **do not touch src/Prism.App or tests/Prism.App.Tests**. Stay in src/Prism.Service, src/Prism.Core,
src/Prism.Hardware, src/Prism.Lighting, src/Prism.Router, their tests, scripts/ and docs/STATUS-hardening.md.
Keep PipeContract changes additive. Same rules: no real hardware/services/registry/router/lamps/fryer, no commits,
0 warnings, all tests green.

Goal: an architecture a senior Windows engineer would sign off for daily use. Fix, in this order:

1. **Secrets under the service account.** Tokens and the router password are DPAPI CurrentUser in %APPDATA%. The
   installed service runs as LocalSystem, so it cannot read them. Move all secrets into a `SecretStore` owned by the
   service: `%ProgramData%\Prism\secrets\` with an ACL of SYSTEM + Administrators only (no inheritance), DPAPI
   LocalMachine scope + per-install random entropy file (same ACL). One-time migration: if a CurrentUser record exists and
   the service runs as the same user (console mode), re-protect into the new store and delete the old file. Secrets
   only ever enter through pipe commands; never logged, never in state.
2. **Verified pipe clients.** Today a client only *claims* "app". Use `GetNamedPipeClientProcessId` → process image path
   + Authenticode/strong check that it is the installed Prism.App (same directory as the service, or signature/hash match
   recorded at install); widgets are windows of that process. Unknown processes get read-only state (no commands).
   Fryer start additionally requires the verified app + the existing confirmation nonce. Rate-limit commands per client.
3. **Fan watchdog.** A tiny separate process/service `Prism.FanGuard` (or a native thread with its own heartbeat file +
   a Windows service recovery action) that, if the main service stops heart-beating for 3 s or crashes, calls the LHM
   `SetDefault()` on every control (hands fans to the BIOS). Configure SCM recovery: restart after 5 s, twice, then
   leave stopped with an Event Log error. Test the heartbeat/timeout logic with fakes.
4. **Structured logging.** Microsoft.Extensions.Logging with a rolling file sink in `%ProgramData%\Prism\logs`
   (daily files, 7 days, 10 MB cap; write your own small sink if the package can't be restored), Event Log for errors,
   scopes per module (fans, rgb, lamps, router, appliances, pipe). Replace Console.Error/Trace writes. No secrets.
5. **Split ServiceRuntime** (≈640 lines, one class runs everything) into hosted modules behind small interfaces:
   `SensorModule`, `FanModule`, `RgbModule` (OpenRGB connection, pacer, bus turns, refresh-in-place), `LampModule`,
   `RouterModule`, `ApplianceModule`, `DiscoveryModule`, plus a `StateHub` that assembles state and pushes immediately
   after commands. Each module: start/stop, health, own exception boundary (one module failing never stops the others or
   the fan safety), restart with back-off. Keep all existing behaviour and tests; add module isolation tests.
6. **Config versioning.** settings.json gets a schemaVersion and migrations with tests; corrupt file → keep a backup,
   load defaults, health warning.
7. **Release build hygiene.** `Directory.Build.props`: Nullable + TreatWarningsAsErrors for src, deterministic builds,
   `<InvariantGlobalization>` where safe, analyzers (CA rules at a sensible level). Fix what that surfaces.

Finish with docs/STATUS-hardening.md (what changed, migration notes, what still needs a live check).
