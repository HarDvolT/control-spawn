# RFC 0001: the plugin system

Status: **open for comment.** Nothing here is built yet. Disagree, poke holes, and suggest better ideas in the linked GitHub issue.

## Why

Control Spawn wants to replace every brand's PC software without becoming one more bloated suite. So the core has to stay small, and each brand or protocol has to be optional.

Today, adding a device means editing the contract, the command switch, settings, modules, state and pages. See [CODE-GUIDE.md](../CODE-GUIDE.md). That doesn't scale to hundreds of devices, and it means every contributor has to change the core.

## Goals

1. Install only what you own.
2. A crashing plugin can't crash the app or affect fans.
3. A plugin can't reach fan safety, or take over the PC.
4. Plugins can be written in any language.
5. A lamp from any brand joins scenes, automations and the dashboard with no extra work.
6. Plugins add little or no memory when you don't use them.

## Not goals

- Loading plugin DLLs into the service. A DLL inside a LocalSystem process gets full control of the PC.
- Paid plugins.
- Non-Windows support in version 1, though nothing here should rule it out.

## The proposal

### Plugins provide capabilities

The core works with kinds of things, not brands:

| Capability | Examples |
|---|---|
| `sensor` | Temperatures, load |
| `fan` | Speed, curve |
| `leds` | Addressable strips and zones |
| `light` | On/off, brightness, colour |
| `pointer`, `keyboard`, `headset` | Peripherals |
| `battery` | Charge level |
| `appliance` | Fryer programs, always with a confirmed start |
| `network` | Signal, usage |

Plugins are grouped by ecosystem or protocol. A Mi Home plugin provides lamps and the fryer, because they share one protocol. A Hue plugin adds lamps to the same page.

### Each plugin is a separate process

```text
com.example.hue/
  plugin.json      identity, version, permissions, what it provides
  backend/         the process that runs
  README.md
```

```json
{
  "id": "com.example.hue",
  "name": "Philips Hue",
  "version": "1.0.0",
  "sdk": "^0.1",
  "platforms": ["windows-x64"],
  "provides": { "capabilities": ["light@1"] },
  "permissions": { "network.lan": true, "secrets": ["bridge-key"] },
  "backend": { "run": "backend/Hue.exe" }
}
```

The host starts the process with low rights and restarts it if it crashes.

### The protocol is JSON-RPC over a private pipe

This is the same design as the Language Server Protocol, so any language works.

| Direction | Calls |
|---|---|
| Host to plugin | `initialize`, `shutdown`, `discover`, `device.command`, `settings.changed` |
| Plugin to host | `device.add`, `device.health`, `state.update`, `event.emit`, `secret.get`, `log` |

A hypothetical exchange:

```json
{"jsonrpc":"2.0","id":7,"method":"device.command","params":{"device":"hue:12","capability":"light@1","action":"set","args":{"on":true,"brightness":40}}}
{"jsonrpc":"2.0","id":7,"result":{"ok":true}}
{"jsonrpc":"2.0","method":"state.update","params":{"device":"hue:12","capability":"light@1","state":{"on":true,"brightness":40}}}
```

LED frames at 30 fps probably need a faster binary path. See open question 3.

### UI comes in three levels

1. **Automatic:** standard capabilities get their cards with no UI code. Most plugins stop here.
2. **Declarative:** a plugin describes forms and charts as data, and the app draws them.
3. **Web panel:** a sandboxed page for rich screens, used rarely.

### Safety stays in the core

Plugins may provide a fan channel, but they never decide fan speed. The core's safety loop always does. Heating appliances always need a confirmed start. The full list is in section 3.4 of the [plan](../PLAN-platform.md).

### How we get there

No rewrite. Each step ships on its own:

1. Add a generic device command beside the old commands.
2. Move lamps to it, then the fryer and router.
3. Add the plugin host, and move those three out as real plugins.
4. Split the service, so plugins are managed outside LocalSystem.
5. Freeze SDK 1.0 only after those three plugins work.

## Open questions

These are where help is most useful:

1. **Sandbox:** what can AppContainer, restricted tokens and job objects really enforce on a plugin? How do we limit network access per host?
2. **USB and HID:** can a low-rights plugin get the device access that peripherals need, and only for declared devices?
3. **LED frames:** is JSON good enough, or do we need shared memory? We need measurements, and proof that it works in a sandbox.
4. **Memory:** a process per plugin costs RAM. Should small plugins share a host? A .NET plugin isn't small by default, so what should the SDK guidance be?
5. **Capability design:** are these the right capabilities? How should they be versioned? How do we describe vendor-only features without losing "one UI"?
6. **Who wins:** how should scenes, automations and alerts share one device? Section 3.6 of the plan proposes priorities with expiry.
7. **Distribution:** how should review, signing and revocation work without one maintainer becoming the bottleneck?

## How to comment

Reply on the linked issue, or open a PR against this file. If you've built plugin systems before, or know a vendor protocol well, we'd really like to hear from you.
