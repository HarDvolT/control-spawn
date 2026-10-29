# Brief for Codex, round 7: two Mi Home lamps, found by device id

Same rules: no real hardware/services/registry/router/lamp access, no commits, 0 warnings, all tests green
(369 today). Areas: src/Prism.Lighting, src/Prism.Service, src/Prism.App, their tests, additive PipeContract changes.

## Facts from the live test (2026-10-29)
- The user has **two** yeelink.light.color5 lamps paired in Xiaomi Mi Home. Yeelight LAN (SSDP/55443) does not
  answer; both answer the miIO hello on UDP 54321.
- miIO hello scan: 192.168.0.175 → device id 0x1C10FE71, 192.168.0.246 → 0x1C058697 (an earlier scan saw
  192.168.0.8 → 0x1C0AF9D7). DHCP addresses change, so a hard-coded host is wrong: the first live test saved
  host 192.168.0.8 and the lamp never connected ("Lamp unavailable").
- Tokens come from Xiaomi Cloud Tokens Extractor, which lists NAME, ID (decimal, e.g. 470481367), IP, TOKEN, MODEL.

## Do
1. **Multiple lamps.** Settings hold a list of lamps: { id (stable, e.g. "lamp:<deviceId>"), name, deviceId (uint),
   lastHost (optional cache), follow (pc|average|own|map), colour, brightness, on, mapPoint }. Tokens stored
   DPAPI-encrypted per deviceId (extend LampTokenStore; migrate the existing single token + host into lamp #1 if its
   device id can be learned from a hello at that host, else keep it as "unresolved").
2. **Discovery by device id.** `MiioDiscovery`: broadcast hello to 255.255.255.255:54321 (and the /24 directed
   broadcast), collect (ip, deviceId, stamp) replies for ~1.5 s. On connect and after any timeout, resolve each
   lamp's current IP from its deviceId; cache lastHost; rescan at most every 30 s while a lamp is unreachable.
   Unit-test with a fake UDP transport and byte fixtures for the hello reply (device id is bytes 8..11 big-endian).
3. **Per-lamp runtime.** One MiioLampClient + LampSync per lamp, independent failures, each with its own rate limit.
   LampState → keep the existing `Lamp` field (first lamp) for compatibility and add `Lamps: IReadOnlyList<LampState>`
   with Id, Name, DeviceId, Host, Connected, Transport, On, Brightness, Color, Follow, Error.
4. **Contract (additive).** `setLampToken` gains optional `deviceId` (decimal or 0x-hex) and `name`; `host` becomes
   optional. New `discoverLamps` → returns found { ip, deviceId, alreadyConfigured }. `setLamp` gains optional `lamp`
   id (default: first lamp, so old callers keep working). `removeLamp` { lamp }. Secrets never echoed or logged.
5. **App.** Lamp page lists every lamp (name, live colour dot, connected/transport, IP), "Find lamps" button showing
   discovered devices, per-lamp "Add token" (hidden input, paste the 32-hex token + device id; also accept the
   extractor's ID in decimal), rename, follow mode, colour, brightness, on/off ToggleSwitch, remove. Lighting map gets a
   draggable point per lamp. Dashboard and Lamp widget show both lamps. Mock client: two lamps.
6. Replace the hard-coded host in `scripts/live-lamp-router.ps1`: it should call `discoverLamps`, print the found
   devices, ask for the token for each (hidden), and run the colour sequence on each lamp in turn, logging per-lamp
   connected/transport/error. Never log tokens.
7. Quieter logs: when OpenRGB is simply not running, log one "[rgb] OpenRGB not reachable" line per minute instead of
   every 5 s.

Finish with docs/STATUS-codex-7.md.
