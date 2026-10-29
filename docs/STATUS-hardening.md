# Hardening status — 2026-10-29

Implemented on `harden`, without commits. No files in `src/Prism.App` or `tests/Prism.App.Tests` were changed. No live scripts, installed services, registry operations, network devices, lamps, fans, or appliances were exercised.

## Changes

1. **Service-owned secrets.** `Prism.Core.SecretStore` stores ciphertext and a random 32-byte install entropy file in `%ProgramData%\Prism\secrets`. Production encryption uses DPAPI LocalMachine plus that entropy. Directory inheritance is disabled; directory and file DACLs grant only SYSTEM and Administrators. Files are replaced atomically, reparse points are refused, and missing entropy never silently rekeys existing records. Lamp/device tokens, router passwords, and router sessions use this store. Plaintext byte buffers are wiped after use. Explicit fake protectors remain available for unit fixtures.

2. **Verified pipe clients.** The server obtains the client PID from `GetNamedPipeClientProcessId`, resolves its executable, and requires the sibling installed `Prism.App.exe` plus matching installation hashes for both apphost and app DLL. The installer protects the entire binary directory and the trust manifest. Verification failure grants only hello, subscribe, and get-state access. Verified widget sessions retain ordinary commands; cooking preparation and confirmation also require app origin and the existing expiring confirmation nonce. A shared per-process limiter allows 20 requests per second across connections. Protocol 1 and its existing message shapes are unchanged.

3. **Independent fan watchdog.** The service launches a separate copy of its executable in `--fan-guard` mode before enabling software fan writes. This child acknowledges readiness and monitors a redirected heartbeat stream with its own monotonic deadline. Three seconds without a heartbeat stops the controller process and attempts LHM `SetDefault()` on every exposed control. EOF also restores defaults. Orderly shutdown restores defaults before disarming the guard. A dead guard prevents subsequent software writes. Mock and observe modes never launch it. The installer configures two SCM restarts, each after five seconds; subsequent failure runs an Event Log error action and leaves the service stopped. Nonzero host failures propagate to SCM's service exit code.

4. **Structured logging.** Microsoft.Extensions.Logging writes JSON lines under `%ProgramData%\Prism\logs`, using UTC daily files, seven calendar days of retention, and a 10 MiB daily cap. Errors additionally go to the Application Event Log (`Prism` source). Operational diagnostics identify modules and exception types, without request bodies, credentials, or arbitrary exception text. Former Console.Error/Trace calls use the host logging bridge. At the daily cap, file entries are dropped; error events still reach Event Log.

5. **Module isolation and state.** Sensor, fan, RGB, lamp, router, appliance, discovery, health, audio, and screen loops have module start/stop contracts, health, and independent exception/retry boundaries with exponential back-off capped at 30 seconds. Lamp workers also have individual boundaries. Appliance polling no longer runs inside discovery. Native sensor work starts independently of other production modules. The composition root retains adapter lifetime and shared snapshots. RGB pacing, shared bus turns, and refresh-in-place behavior remain intact. `StateHub` immediately notifies every subscribed session after commands, alongside periodic pushes.

6. **Settings schema.** Settings now write `schemaVersion: 1`. Unversioned settings migrate while retaining values. Invalid or unsupported settings preserve an exact `.corrupt-<id>.bak` copy, load defaults, and expose a health warning. Saves remain validated and atomic.

7. **Build hygiene.** Allowed source projects enforce nullable checking, warnings as errors, deterministic builds, and the .NET 8 default CA analyzer set. The service uses invariant globalization. App build policy was left outside this branch's scope.

## Migration and deployment notes

- Deploy service and app binaries together into a dedicated directory named `Prism`. The installer requires both apphost and app DLL, refuses reparse points, secures binary/data ACLs, records hashes, registers the Event Log source, and creates or updates the stopped service. It does not start it. Refresh the installation hashes whenever the app is updated.
- To migrate old CurrentUser records, run `Prism.Service.exe --migrate-secrets` elevated **as the original user**. This mode only migrates secrets; it does not construct hardware adapters or start the host. Exit code 0 means success; 1 means a migration failed. Ordinary elevated console mode also attempts migration, including disabled devices. Failed decryption or persistence retains the old record. Successful migration removes the old file. LocalSystem cannot decrypt another user's old DPAPI records; alternatively provision credentials through the verified installed app's pipe commands.
- Keep the entropy file with its ciphertext on the original Windows installation. Losing it prevents decryption. Never place credentials in settings, command-line arguments, logs, or state messages.
- A development app outside the secured installed directory has read-only pipe access. There is no mock/debug authorization bypass.

## Validation

`scripts/test-hardening.ps1 -Offline` runs only the four permitted unit-test projects in Release. Offline mode uses cached packages and disables NuGet vulnerability-audit network requests for that invocation; no project-level audit suppression was added.

Verified final counts: Hardware **34**, Lighting **141**, Router **68**, Service **133** — **376 tests**. Tests use fake hardware/transports/encryption and temporary files. Added coverage includes ACL construction, migration success/failure and disabled devices, lost entropy, heartbeat boundaries, hash tampering, process rate budgets, read-only impostors, cooking authorization, module isolation/back-off, cross-client immediate state pushes, schema migration/corrupt backups, and log retention/capping/redaction. Installer and test scripts are parsed without execution of installation actions. App tests are excluded by the task boundary.

## Still requires an explicitly authorized live check

- Inspect installed binary, trust-manifest, secret, entropy, and log ACLs under LocalSystem and a standard interactive account; verify real DPAPI migration and subsequent LocalSystem reads.
- Connect the installed app and its widgets; confirm unknown executables remain read-only, app updates require refreshed hashes, and appliance nonce expiry/replay protection remains effective.
- With supervised hardware, crash or stall the controller and verify actual BIOS fan handoff, guard behavior, and SCM's two five-second retries followed by the stopped/error state. Native driver initialization and BIOS behavior cannot be proven by fake tests.
- Verify Event Log delivery and daily rotation in an installed service session, plus existing RGB refresh/pacing, lamp/router reconnection, discovery, and capture behavior.
- No fryer heating/start command has been tested against a real appliance.
