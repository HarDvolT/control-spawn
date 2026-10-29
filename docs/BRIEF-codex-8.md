# Brief for Codex, round 8: detect every device automatically, now and later

Builds on round 7 (two lamps by device id). Same rules: no real hardware/services/registry/router/lamp/cloud
access in tests or while working, no commits, 0 warnings, all tests green. Additive PipeContract changes only.

User requirement: "the app should detect any devices we have or add later". Prism keeps a **device registry** and
notices new hardware on its own, then offers to set it up — no scripts, no typing IPs.

## 1. Device registry (service)
Persisted list of known devices: { id, kind (Lamp | RgbController | FanHeader | Router | NetworkDevice), name,
source (miio | yeelight | openrgb | lhm | router), firstSeen, lastSeen, status (ready | needsToken | unsupported |
offline), capabilities }. State gains `Devices` and `NewDevices` (seen but not yet set up / dismissed).

## 2. Discovery sources (background, rate-limited)
- **Xiaomi / Mi Home (miIO):** hello broadcast every 60 s (and on demand). Unknown device ids → NewDevices with
  status needsToken. Try `miIO.info` once a token exists to read model/firmware; map yeelink.light.* to Lamp, others
  to "unsupported for now" but still listed (name it from the model).
- **Yeelight LAN:** SSDP M-SEARCH on 239.255.255.250:1982 every 60 s; bulbs with LAN control need no token.
- **OpenRGB:** controllers appearing/disappearing via device-list-updated (already refreshed in place) → registry
  entries; a newly plugged RGB device gets direct mode + joins sync automatically (setting "auto-join new RGB").
- **Fans:** a header whose RPM goes from 0 to >0 becomes a new fan group ("Fan header N") with the Standard curve.
- **Router device list:** the read-only connected-devices command already used by the widget → NetworkDevice
  entries (name, ip, mac, vendor), so new Xiaomi devices can be matched by MAC/vendor too.

## 3. Token import (no retyping)
- **Paste or drop the Tokens Extractor output** (its text block or its JSON/CSV) into the app: parse every NAME / ID /
  IP / TOKEN / MODEL record, match by device id to discovered devices, and save tokens for the ones the user ticks.
  Tokens shown masked; never logged or echoed in state.
- Optional, behind an explicit "Sign in with Xiaomi" button: fetch tokens from Xiaomi's cloud the way the extractor
  does (user enters their Xiaomi credentials in the app; region picker; handle 2-step verification by opening the
  verification URL). Credentials are never stored — only the resulting tokens (DPAPI). Implement behind an interface,
  unit-test the parsing with recorded fixtures; do NOT contact Xiaomi from tests. Clearly label it as optional.

## 4. App
- A **Devices** page (sidebar, GENERAL group): every known device grouped by kind with live status, plus a
  "New devices found" banner/card with Set up / Dismiss. Set up flows: lamp → token (paste one, or import file),
  RGB → joins sync + case-map placement, fan → name + curve, network device → just info.
- Tray notification when a new device is found (toggle in Settings).
- Command search: "Find devices", "Import tokens".

## 5. Tests
Registry merge (same device seen from two sources), offline/online transitions, new-fan detection threshold,
extractor text/JSON parsing (fixture with two yeelink.light.color5 lamps, decimal IDs 470481367-style),
NewDevices dismissal persistence, no secrets in state JSON.

Finish with docs/STATUS-codex-8.md.

## 6. Rename anything (user request)
Every device in the registry (lamps, RGB controllers, fan groups, network devices, appliances) has a user-editable
display name, persisted by stable id and used everywhere (sidebar, widgets, case map, tray, scenes). Command
`renameDevice` { id, name } (1–40 chars, trimmed). Inline rename in the Devices page, Lamp page, Lighting device
tabs and Fans cards (pencil icon → text box → Enter saves, Esc cancels). Reset to the detected name.

## 7. Xiaomi appliances via MIoT (first one: the user's air fryer)
The user has a Xiaomi smart air fryer on the same Mi Home account (its token came from the same extractor run).
- Add a generic **MIoT client** on top of the existing miIO transport: `get_properties` / `set_properties` /
  `action` with siid/piid/aiid, batched, with retries and the existing rate limit.
- **Spec-driven:** a device's model (e.g. `careli.fryer.*`, `xiaomi.fryer.*`, `silen.fryer.*`) selects a spec. Ship
  a bundled JSON spec file per supported model (fetched by the developer from https://miot-spec.org, cached in the
  repo; the app must work offline). Map the spec's services to UI controls generically: bool → ToggleSwitch,
  int/float with range → ThickSlider + number, enum → segmented control, actions → buttons. Unknown models still show
  read-only properties.
- **Air fryer page / card:** status (idle / preheating / cooking / paused / done / fault), current and target
  temperature, target and remaining time (animated ring countdown), mode/recipe picker, start / pause / cancel,
  "turn basket" or "done" notification if the spec has it.
- **Safety (heating appliance):** Start always needs an explicit confirmation in the app; no scene, schedule,
  automation, widget or command-search entry may start cooking; Stop/Cancel is always one click and works from the
  widget and tray; temperature and time are clamped to the spec's ranges; if Prism loses contact while cooking it
  only shows a warning (the fryer's own firmware keeps control). Never send anything on startup.
- Tray notification and optional event-light flash when cooking finishes.
- Tests: spec parsing, property mapping, clamping, the start-needs-confirmation rule, no automation can start it,
  fixture replies for get_properties/action. Do NOT contact the real fryer.

The lead will supply the exact model string from the user before live testing; build the generic path plus a spec
for the most common Xiaomi/Mijia smart air fryer models found on miot-spec.org.

## Live device facts for round 8
- Air fryer model: **careli.fryer.maf10a** (Mi Home, same account as the lamps). Build and bundle its MIoT spec first
  (look it up on miot-spec.org: https://miot-spec.org/miot-spec-v2/instance?type= with the URN for this model).
