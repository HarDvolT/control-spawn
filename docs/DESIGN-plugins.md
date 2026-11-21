# Design: the plugin system

Status: **draft for the owner's review**, 2026-11-21. Nothing here is built yet.

This turns [RFC 0001](rfcs/0001-plugin-system.md) and sections 3.2 to 3.11 of the [plan](PLAN-platform.md) into a design that can be built. Where the RFC left a question open, this doc picks an answer and says why. Where only a test on real Windows can answer, it names the spike instead of guessing.

Read section 2 first. Fan safety is the part that must not move.

---

## 1. What this design decides

| Question | Decision |
|---|---|
| Where plugins run | One process per plugin, started by the service, inside an AppContainer and a job object |
| How they talk | JSON-RPC 2.0, one JSON message per line, over a private named pipe per plugin |
| Which pipe | **Not** the app's `controlspawn` pipe. Each plugin gets its own pipe, with its own small set of methods |
| Fans | The plugin protocol has no method that writes a fan, a fan mode, a curve or a safety setting |
| Plugin sensors | Never feed fan curves or the failsafe in SDK 0.x. Later, they may only make fans faster |
| Languages | Any language that can open a pipe and read and write JSON lines. C# and Rust are first-class from day one |
| Host language | C# for now, in the current service. The wire format lets it move to Rust later without breaking plugins |
| First plugins | Yeelight, then Mi Home with the fryer, then the ZLT router (plan, Phase 3) |
| SDK version | Stays below 1.0 until those three work as real plugins |

## 2. Fan safety: the rules this design can't break

Today fans are protected in three layers. The fan loop in the service applies curves and the failsafe (`FanController`, `FanSafety`). `ControlSpawn.FanGuard.exe` is a child process that hands the fans back to the BIOS if the service stops sending a heartbeat for 3 seconds. Fan control only runs on verified boards.

Plugins sit outside all three layers. These rules keep them there:

1. **No fan methods exist.** The plugin protocol (section 5) has no call that sets a fan, a fan mode, a curve, a scene with fans, a sensor binding or a safety limit. There's nothing to check, because there's nothing to send. A test asserts that the method list contains no fan or safety call.
2. **Plugins can't reach FanGuard.** FanGuard listens only on its own stdin, which only the service holds. Plugins aren't its children and get no handle to it. A plugin can't send it a beat, a stop, or anything else.
3. **Plugins can't reach the app pipe as a trusted client.** The `controlspawn` pipe grants write commands only to the verified app (`PipeClientVerifier`). A plugin process isn't the app, so at most it would get read-only access. Its AppContainer should block the pipe entirely, because the pipe's ACL has no entry for app containers. Spike S1 must prove this on the real PC.
4. **Plugin data can't slow a fan.** In SDK 0.x, plugin sensor readings are shown and logged, and never used by a fan curve or the failsafe. Once plugin sensors are allowed in curves (a later RFC), the rule is: the fan loop takes the higher of its own result and any plugin-driven result. A plugin can make a fan faster, never slower. A missing or stale plugin reading counts as "no opinion", never as 0.
5. **Plugins can't stall the fan loop.** The fan loop never waits on a plugin. Plugin traffic runs in its own module (`PluginModule`, a `HostedModule`) with bounded queues and timeouts. A test floods a fake plugin and checks the fan loop still ticks once a second and still beats FanGuard.
6. **Killing anything plugin-related leaves fans safe.** Killing a plugin, every plugin, or the plugin module leaves fans on their curves. Killing the service still hands fans to the BIOS through FanGuard, exactly as today. These are the Phase 3 exit tests on the real PC.
7. **Plugin fan channels aren't in this design.** The plan allows plugins to provide extra fans one day (`fan.channel`, such as a USB fan hub). That needs its own RFC, because a USB hub can't be handed back to the BIOS by FanGuard. It needs a device that falls back to a safe speed on its own when its plugin stops. Until that RFC is accepted, the host refuses any manifest that asks for `fan.channel`.
8. **Heating appliances keep their confirmed start.** The fryer's two-step start (`PrepareApplianceStart`, then `ConfirmApplianceStart` with a one-use nonce, app only) stays in the core. The host sends a heating action to a plugin only after the core has checked a valid confirmation. Honest limit: a plugin that talks to the fryer over the LAN could start it on its own. The core can't prevent that, so appliance plugins need review and a project signature (section 9).

If a later change needs to break one of these rules, it needs a new RFC and the owner's approval, not a code review.

## 3. Processes

```text
ControlSpawn.Service.exe  (LocalSystem today, Core after the split)
 ├─ ControlSpawn.FanGuard.exe      fan watchdog, stdin only, unchanged
 ├─ GPU sensor host                child process (PR #49), unchanged
 └─ PluginModule
     ├─ com.controlspawn.yeelight  AppContainer, job object, private pipe
     ├─ com.controlspawn.mihome    AppContainer, job object, private pipe
     └─ com.controlspawn.zlt       AppContainer, job object, private pipe
```

- **The plugin manager is a module, not a new service.** It runs as `PluginModule` inside today's service, with the same retry rules as every other module. When the plan splits the service into Guardian (SYSTEM) and Core (low rights), `PluginModule` moves to Core unchanged. Plugins never run beside the Guardian.
- **Each plugin is its own process.** A crash in one doesn't touch the others, the service or the fans.
- **Each plugin runs in an AppContainer** with only the capabilities its manifest declares, such as `internetClient` or `privateNetworkClientServer` for LAN devices. The process token has no admin rights, even though the service that starts it does.
- **Each plugin sits in a job object** that kills it when the service exits, and caps its memory and process count. Default caps: 256 MB, 4 processes. The manifest can't raise them in SDK 0.x.
- **stdout and stderr go to the plugin's log**, capped at 1 MB per file. The protocol never uses stdio, so a library that prints can't break it. (PR #49's GPU host had to redirect `Console.Out` for exactly this reason.)

Spike S1 checks what an AppContainer started from a LocalSystem service can really do and reach. Until it passes, official plugins run with a restricted token and a job object, and community plugins don't run at all.

## 4. The pipe

### 4.1 Why not the existing `controlspawn` pipe

The app pipe carries every command the app can send, including `setFanMode`, `setFanCurve` and `setServiceOptions`. Its whole security model is "the verified app gets writes, everyone else reads". Putting plugins on it would mean filtering commands per client forever, and one missed filter would hand a plugin fan control.

A separate pipe per plugin has a different, tiny method list (section 5). Fan commands aren't filtered out of it. They were never in it.

The wire format stays the same as the app pipe: UTF-8 JSON, one message per line, `\n` at the end, 64 KB per line at most. The same reader (`PipeSession.ReadLineAsync`) and limits apply. That's what keeps the protocol easy in any language.

### 4.2 Why not stdio

Stdio is the simplest transport (the Language Server Protocol uses it). It was rejected for two reasons. A stray print from any library corrupts the stream. And a pipe can carry a second channel later for frames (section 7), where stdio can't.

### 4.3 How the pipe is set up

1. The host creates the pipe before it starts the plugin: `\\.\pipe\controlspawn-plugin-<random 128-bit hex>`, with one instance only.
2. The pipe's ACL allows only that plugin's AppContainer SID, plus SYSTEM. Network access is denied, as on the app pipe.
3. The host starts the plugin with two environment variables: `CONTROLSPAWN_PIPE` (the full pipe name) and `CONTROLSPAWN_PLUGIN_ID`.
4. The plugin opens the pipe as a client. The host checks the client process ID matches the process it started, using `GetNamedPipeClientProcessId` as `PipeClientVerifier` does today. Anything else is disconnected and logged.
5. The host sends `initialize`. If no valid reply comes in 10 seconds, it kills the plugin and counts a failure.

Because the pipe exists before the plugin starts, has one instance, and is locked to one SID and one process ID, no other program can squat on it or connect to it.

## 5. The protocol

JSON-RPC 2.0. A request has `id`, `method` and `params`, and gets one reply with the same `id`. A notification has no `id` and gets no reply. Both sides can send both.

### 5.1 Host to plugin

| Method | Kind | What it does |
|---|---|---|
| `initialize` | request | Protocol version, plugin settings, data folder. The plugin replies with its capabilities and SDK version |
| `shutdown` | request | Stop cleanly. The host kills the process 5 s later if it hasn't exited |
| `ping` | request | Liveness. Sent every 5 s. Three missed replies mean the plugin is hung and gets restarted |
| `discover` | request | Look for devices now (the user pressed Find devices) |
| `device.command` | request | Run one capability action on one device the plugin owns, such as `light@1` `set` |
| `settings.changed` | notification | The user changed this plugin's settings |

### 5.2 Plugin to host

| Method | Kind | What it does |
|---|---|---|
| `device.add` | request | Report a device: id, name, model, capabilities. The id is prefixed with the plugin id by the host, so plugins can't collide or touch each other's devices |
| `device.remove` | notification | A device is gone for good |
| `device.health` | notification | Online, Slow, Offline, Needs setup or Unsupported, with a reason in plain words (plan, 3.8) |
| `state.update` | notification | Changed capability state for one device. Only the fields that changed |
| `event.emit` | notification | Something happened, such as "cooking finished". Goes to the event history and automations |
| `secret.get` | request | Read one of this plugin's own secrets, by a name its manifest declares |
| `secret.set` | request | Store one of its own secrets (DPAPI, as `SecretStore` does today) |
| `log` | notification | A log line. Rate limited. The host strips anything that looks like a declared secret |
| `notify` | request | Ask to show a notification. Needs the `notifications` permission |

That is the whole list for SDK 0.x. Anything else gets JSON-RPC error `-32601` (method not found).

### 5.3 Example

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocol":1,"pluginId":"com.controlspawn.yeelight","dataDir":"C:\\ProgramData\\Control Spawn\\plugins\\com.controlspawn.yeelight","settings":{}}}
{"jsonrpc":"2.0","id":1,"result":{"sdk":"0.1.0","capabilities":["light@1"]}}
{"jsonrpc":"2.0","id":1,"method":"device.add","params":{"id":"0x1a2b3c","name":"Desk lamp","model":"yeelink.light.color4","capabilities":["light@1"]}}
{"jsonrpc":"2.0","id":1,"result":{"device":"com.controlspawn.yeelight:0x1a2b3c"}}
{"jsonrpc":"2.0","id":7,"method":"device.command","params":{"device":"0x1a2b3c","capability":"light@1","action":"set","args":{"on":true,"brightness":40}}}
{"jsonrpc":"2.0","id":7,"result":{}}
{"jsonrpc":"2.0","method":"state.update","params":{"device":"0x1a2b3c","capability":"light@1","state":{"on":true,"brightness":40}}}
```

Ids are per direction, so each side keeps its own counter. That's standard JSON-RPC.

### 5.4 Limits

| Limit | Value | Why |
|---|---|---|
| Line length | 64 KB | Same as the app pipe |
| Requests from a plugin | 20 a second | Same budget as `ClientCommandLimiter` |
| `state.update` | Coalesced per device, sent to the app at most once a second | Matches today's state push |
| Host request timeout | 5 s, or 30 s for `discover` | A slow lamp shouldn't hold a scene |
| Requests in flight per plugin | 16 | Same as `PipeSession` |

A plugin that breaks a limit gets an error first. If it keeps going, it's restarted and counted as a failure.

### 5.5 Errors

Standard JSON-RPC codes, plus a few of our own:

| Code | Meaning |
|---|---|
| `-32001` | Device offline |
| `-32002` | Device needs setup, such as a missing token |
| `-32003` | Not allowed: the permission or capability isn't declared |
| `-32004` | Needs confirmation (heating appliances) |

Every error has a `message` written for the user, such as "The lamp didn't answer."

## 6. Capabilities

A capability is a versioned contract, such as `light@1`. It lists the state fields and the actions. It's written once as JSON Schema in `contracts/`, and the C# and Rust types are generated from it (plan, 3.10).

The first three, needed by the first three plugins:

| Capability | State | Actions |
|---|---|---|
| `light@1` | `on`, `brightness` (1-100), `color` (`#RRGGBB`), `kelvin` | `set` with any of those |
| `appliance@1` | `status`, `properties` (as `ApplianceProperty` today), `actions` | `act` with `{ action, confirmation? }`. Actions marked `heats: true` need the core's confirmation |
| `network@1` | `connected`, signal fields, `pingMs` | None. Read-only, as the router is today |

`sensor@1` comes next, read-only and display-only (rule 4). `leds@1` waits for the frame spike (section 7).

Breaking a capability means a new version, such as `light@2`. The host keeps serving `light@1` plugins for the SDK's support window.

## 7. LED frames

Case RGB runs at 30 fps through OpenRGB today, and `DeviceFramePacer` paces each device. Sending every LED as JSON 30 times a second may be fine for 84 LEDs and wasteful for 1,000.

So LED plugins aren't in the first round. Spike S3 measures JSON lines against a shared-memory ring on the real PC, inside the sandbox, before `leds@1` is designed. Case RGB moves last, as the plan already says.

## 8. Lifecycle

```text
installed -> starting -> running -> stopping -> stopped
                 |           |
                 +-> failed <+   (backoff, then retry)
                         |
                         +-> disabled (5 failures in 10 minutes)
```

- **Restart backoff:** 1, 2, 4, 8, 16, then 30 s, as `HostedModule` does. A minute of good running resets it.
- **Disabled:** after 5 failures in 10 minutes. The user sees "Yeelight stopped working and was turned off" with a Try again button. Other plugins aren't touched.
- **Safe mode:** if the service crashes twice in 5 minutes while plugins run, the next start runs no plugins and names the likely one (plan, 3.11). Fans keep running throughout.
- **Settings:** each plugin has its own file in `C:\ProgramData\Control Spawn\plugins\<id>\settings.json`, written with `AtomicFile`. One plugin can't damage another's.

## 9. Manifest, permissions and trust

The manifest is the one in the RFC, with these rules added:

- `id` is reverse-DNS and must match the folder name.
- `backend.run` is a path inside the plugin folder. No arguments, no paths outside.
- Each permission maps to something the host can enforce, or it's marked as review-only:

| Permission | Enforced by | Strength |
|---|---|---|
| `network.lan` | AppContainer `privateNetworkClientServer` | Strong, once S1 confirms it |
| `network.internet` | AppContainer `internetClient` | Strong for on or off. Per-host limits are review-only |
| `secrets: [names]` | Host checks every `secret.get` | Strong |
| `notifications` | Host checks every `notify` | Strong |
| `usb.hid: [vid:pid]` | Not in SDK 0.x | Spike S2 |
| `fan.channel` | Refused (rule 7) | n/a |

**Trust levels**, from the plan:

| Level | Runs where | Needs |
|---|---|---|
| Official | Sandbox | Project signature. Allowed in Phase 3 |
| Local | Sandbox, labelled "not reviewed" | The user turns on local plugins. Allowed after the service split |
| Community | Tightest sandbox | Registry review and signature. Phase 6 |

No community or local plugin runs until the service is split (plan, Phase 3), because before the split the host is a SYSTEM process.

## 10. Rust

Rust is a first-class plugin language, not an afterthought. Three things make that true:

1. **The wire format is plain.** A plugin needs to open a file path (`\\.\pipe\...`), read lines, and parse JSON. Rust's standard library opens a named pipe with `std::fs::OpenOptions`, and `serde_json` does the rest. No Windows crate is needed for the protocol.
2. **Types come from the same contracts.** `contracts/*.json` generates the Rust types (with `typify`) alongside the C# ones. CI checks both build.
3. **The conformance kit is a program, not a library.** `cs plugin test <folder>` starts any plugin, drives it through the protocol, and checks the replies. It doesn't care what language the plugin is in.

### 10.1 A minimal Rust plugin

This is a sketch of a whole plugin: a fake lamp. A real SDK crate (`controlspawn-plugin`) would hide the loop and the JSON plumbing.

```rust
// Cargo.toml: serde = { version = "1", features = ["derive"] }, serde_json = "1"
use serde_json::{json, Value};
use std::io::{BufRead, BufReader, Write};

fn main() -> std::io::Result<()> {
    let name = std::env::var("CONTROLSPAWN_PIPE").expect("started by Control Spawn");
    let pipe = std::fs::OpenOptions::new().read(true).write(true).open(&name)?;
    let mut out = pipe.try_clone()?;
    let mut send = |msg: Value| -> std::io::Result<()> {
        writeln!(out, "{msg}")?; out.flush()
    };
    let mut on = false;
    let mut brightness = 50;

    for line in BufReader::new(pipe).lines() {
        let msg: Value = match serde_json::from_str(&line?) { Ok(v) => v, Err(_) => continue };
        let id = msg.get("id").cloned();
        match msg["method"].as_str() {
            Some("initialize") => {
                send(json!({"jsonrpc":"2.0","id":id,"result":{"sdk":"0.1.0","capabilities":["light@1"]}}))?;
                send(json!({"jsonrpc":"2.0","id":1,"method":"device.add",
                    "params":{"id":"demo","name":"Demo lamp","model":"demo","capabilities":["light@1"]}}))?;
            }
            Some("ping") => send(json!({"jsonrpc":"2.0","id":id,"result":{}}))?,
            Some("device.command") => {
                let args = &msg["params"]["args"];
                if let Some(v) = args["on"].as_bool() { on = v; }
                if let Some(v) = args["brightness"].as_u64() { brightness = v.clamp(1, 100); }
                send(json!({"jsonrpc":"2.0","id":id,"result":{}}))?;
                send(json!({"jsonrpc":"2.0","method":"state.update",
                    "params":{"device":"demo","capability":"light@1","state":{"on":on,"brightness":brightness}}}))?;
            }
            Some("shutdown") => { send(json!({"jsonrpc":"2.0","id":id,"result":{}}))?; break; }
            Some(_) if id.is_some() => send(json!({"jsonrpc":"2.0","id":id,
                "error":{"code":-32601,"message":"Method not found"}}))?,
            _ => {} // replies to our own requests, and notifications we don't use
            // One thread, so a write never waits behind a blocking read on the same pipe.
            // A real SDK opens the pipe for overlapped I/O and reads and writes on separate tasks.
        }
    }
    Ok(())
}
```

A release build of this is a few hundred KB and uses a few MB of memory. That's a guess to check in spike S4, not a measurement.

### 10.2 The C# equivalent

The C# SDK (`ControlSpawn.Sdk`) gives the same thing as a base class. Authors override `OnCommandAsync` and call `AddDeviceAsync` and `UpdateStateAsync`. The SDK recommends NativeAOT, because a normal .NET process idles at tens of MB. Spike S4 measures both.

### 10.3 Moving the host to Rust later

Because plugins only see JSON lines on a pipe, the host can be rewritten in any language without touching a plugin. The order that keeps fan risk lowest:

1. Plugins first. Yeelight is small and a good first Rust plugin.
2. The plugin host next, once the service is split and it lives in low-rights Core.
3. Core after that, one module at a time, behind the same app pipe contract.
4. The Guardian and FanGuard last, if ever. They're small, tested, and verified on one board. A rewrite there needs the same live fan tests on the real PC.

## 11. Spikes before code

Each spike ends with a short written result in `docs/`, run on the owner's PC where it says so.

| Spike | Question | Where |
|---|---|---|
| S1 | Can a LocalSystem service start a plugin in an AppContainer with LAN access? Does the AppContainer block the `controlspawn` pipe? Can it reach a Yeelight on the LAN? | Owner's PC |
| S2 | Can a sandboxed plugin open one declared USB HID device and nothing else? | Owner's PC, with a peripheral |
| S3 | JSON lines against shared memory for LED frames, at 30 fps, inside the sandbox | Owner's PC |
| S4 | Idle memory of a Rust plugin, a NativeAOT C# plugin and a normal .NET plugin | Any Windows PC |

## 12. How it gets built

Each step is its own PR, ships on its own, and changes nothing users see until step 5. Implementation PRs that touch `ServiceRuntime` or `Program.cs` wait until the open crash-fix PRs (#47, #49, #50) are merged, because those PRs change the same files.

1. **Contracts.** `contracts/plugin-protocol@1.json`, `light@1`, `appliance@1`, `network@1`. Generated C# types. A test that the protocol has no fan or safety method (rule 1).
2. **`PluginModule` with an in-memory fake plugin.** No processes yet. Lifecycle, limits, backoff, disable, safe mode. Tests for rules 5 and 6 with fakes.
3. **Spikes S1 and S4.**
4. **Process host.** Private pipe, job object, AppContainer (or restricted token until S1 passes). The C# SDK and `cs plugin test`.
5. **Yeelight as a plugin**, behind a setting, beside the built-in code. Then remove the built-in code.
6. **Rust SDK crate and the Rust demo plugin**, checked by `cs plugin test` in CI.
7. **Mi Home with the fryer.** Tests secrets and the confirmed start.
8. **ZLT router.**
9. **Service split** (plan, Phase 3). Then local plugins.

## 13. Tests that must exist

- The protocol method list has no fan, curve, scene-with-fans, sensor-binding or safety method.
- A plugin that sends `setFanMode`, or any unknown method, gets `-32601` and no side effect.
- A plugin that floods requests doesn't delay the fan loop's tick or its FanGuard beat.
- Killing a plugin process leaves fans on their curves. Killing the service hands them to the BIOS (live, on the owner's PC).
- A plugin can't read another plugin's secrets, settings or devices.
- A heating action without a valid core confirmation never reaches the plugin.
- A plugin's sensor reading never changes a fan duty (SDK 0.x).
- A second client can't connect to a plugin's pipe.

## 14. Still open

- **Per-host internet limits.** Windows can't enforce them cleanly from an AppContainer. Review is the backstop. Is that enough for community plugins?
- **Shared hosts for tiny plugins.** One process per plugin is simple and safe. If S4 shows .NET plugins are too heavy, should official C# plugins share one host? This design says no until the numbers say otherwise.
- **Plugin UI.** Automatic cards come free with capabilities. Declarative and web panel UI need their own design, after the first three plugins.
- **Who wins** when a scene, an automation and a plugin event want the same lamp. The plan's request-and-priority model (3.6) applies, and isn't redesigned here.
