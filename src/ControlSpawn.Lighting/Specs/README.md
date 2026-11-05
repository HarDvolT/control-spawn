# Offline MIoT specs

These files are normalized property/action tables, selected by exact model string. They do not fetch specs at runtime. Unsupported models cannot execute actions or property writes.

Sources consulted on 2026-10-29:

- https://home.miot-spec.com/spec/careli.fryer.maf10a
- https://home.miot-spec.com/spec/careli.fryer.maf02

The public catalogue exposes the corresponding instance URNs:

- `urn:miot-spec-v2:device:air-fryer:0000A0A4:careli-maf10a:1`
- `urn:miot-spec-v2:device:air-fryer:0000A0A4:careli-maf02:1`

The requested raw endpoint (`https://miot-spec.org/miot-spec-v2/instance?type=<URN>`) could not be retrieved in this environment. These are explicitly normalized tables transcribed from the public model specification pages, not claimed to be downloaded raw instances. Model information is not inferred from a similar model. The maf10a does not expose a current-temperature property: Prism displays “not reported” instead of substituting its target temperature. Time units are minutes.

Writable MIoT metadata is not sufficient permission to expose a write in Control Spawn. The service uses a separate allowlist for passive configuration. Reservation/status/recipe-sync writes and actions with recipe inputs are blocked; start and resume use a short-lived, one-use app confirmation. Actions do not retry after an ambiguous timeout. Startup/polling reads only properties; no stored cooking settings are replayed.

Cloud protocol reference (optional sign-in only): https://github.com/PiotrMachowski/Xiaomi-cloud-tokens-extractor . Prism's implementation uses an in-memory HTTP session behind `IXiaomiTokenProvider`; tests inject HTTP fixtures and never contact Xiaomi.
