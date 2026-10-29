# Brief for Codex, round 2: background service + Mi Home lamp

Read docs/ARCHITECTURE.md, docs/HARDWARE.md, docs/STATUS-codex.md (your round 1) and
`src/Prism.Core/Ipc/PipeContract.cs` (the wire contract, written by the lead; do not change its
public shape without listing the change in your status file). Same rules as round 1: never touch real
hardware, drivers, services, registry, WMI methods, the router or the lamp; code + tests only; no commits.
The .NET 8 SDK is now installed: `dotnet build Prism.sln` and `dotnet test Prism.sln` must stay green
(127 tests pass today). Add new projects to Prism.sln.

## 1. Mi Home lamp transport (src/Prism.Lighting)

The lamp (`yeelink.light.color5`) is paired in the **Xiaomi Mi Home** app, not the Yeelight app, so the
Yeelight LAN protocol may be unavailable. Add a second `ILampClient`:

- `MiioLampClient`: Xiaomi miIO protocol over UDP 54321. Hello packet (32 bytes, 0x2131 magic, all 0xFF),
  parse device id + stamp, then encrypted packets: AES-128-CBC, key = MD5(token), IV = MD5(key + token),
  PKCS7, header checksum = MD5(header-with-token + ciphertext). JSON-RPC `{"id":n,"method":...,"params":[...]}`.
  Methods for this model: `set_power ["on"|"off","smooth",ms]`, `set_bright [1-100,"smooth",ms]`,
  `set_rgb [int,"smooth",ms]`, `set_ct_abx [kelvin,"smooth",ms]`, `get_prop ["power","bright","rgb","ct","color_mode"]`.
  Stamp tracking, retries, 2 s timeout, rate limit (miIO has no music mode: max ~2 colour updates/s, smoothed).
- `LampFactory`: prefer Yeelight LAN if the bulb answers SSDP or TCP 55443, else miIO when a token is
  configured, else "not configured". Token stored DPAPI-encrypted in `%APPDATA%\Prism\lamp.json`
  (reuse the router module's `CredentialStore` pattern; do not edit src/Prism.Router — copy the small DPAPI helper).
- Unit tests with fixed byte fixtures for hello, encrypt/decrypt round trip, checksum, and a known
  token/packet vector (find a published vector, e.g. from python-miio tests, and cite it).

## 2. Prism.Service (new project, `net8.0-windows`, Worker Service)

- Runs as a Windows service (`UseWindowsService`) and also as a console app (`--console`) for debugging,
  with `--mock` using all the fakes (no admin, no hardware, no network) — the UI team develops against this.
- Hosts: LhmHardwareMonitor (1 Hz) → FanController.Apply, EffectEngine (30 fps) → OpenRgbClient,
  LampSync → lamp client, router polling (Prism.Router RouterClient every 2 s, PingMonitor, SpeedTest on
  command). Reference src/Prism.Router as a project; don't edit it.
- Named-pipe server implementing PipeContract exactly: multiple clients, hello/version check, subscribe →
  StateMessage push ~1 Hz, all Commands, JSON lines. Pipe ACL: allow the interactive user (so the
  non-elevated app connects) and SYSTEM/Administrators; deny network logons.
- Settings persisted in `%ProgramData%\Prism\settings.json` (fan modes/curves per group, lighting, synced
  devices, lamp, scenes Gaming/Work/Night/Movie with the design's values). Scenes apply fans+lighting+lamp.
- Safety: on stop/crash call FanController.RestoreDefaults (BIOS) — already in round 1; make sure the
  host wires it to service stop, AppDomain unhandled exceptions and Ctrl+C.
- Health warnings in StateMessage.Health: AsusFanControlService / LightingService / iCUE running,
  PawnIO missing, not elevated, OpenRGB server not reachable, lamp not configured, router login held elsewhere.
- OpenRGB: the service expects an OpenRGB server on 127.0.0.1:6742. Add an `OpenRgbProcess` helper that can
  launch a bundled `OpenRGB.exe --server --noautoconnect` (path from settings) — code only, not run in tests.
- `scripts/install-service.ps1` / `uninstall-service.ps1` (sc.exe create, delayed auto start, recovery =
  restart). Do NOT run them.
- Tests (`tests/Prism.Service.Tests`): pipe protocol round-trip over an in-memory/duplex stream, command
  validation, scene application against fakes, state assembly from a fake snapshot (nulls preserved).

Finish with docs/STATUS-codex-2.md (what exists, build/test results, open questions).
