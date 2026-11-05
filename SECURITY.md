# Security

Control Spawn runs a background service with full system rights and can change fan speeds, so we take security reports seriously.

## Reporting a problem

Please **don't open a public issue**. Report it privately here:

https://github.com/HarDvolT/control-spawn/security/advisories/new

Include what you found, how to reproduce it, and which version you tested. We aim to reply within 7 days. This is a volunteer project, so there's no bounty, but we'll credit you in the release notes if you'd like.

## Supported versions

Only the latest release gets fixes.

## What counts

- A way for a normal user or a website to get system rights through the service.
- A way to get past the fan safety limits.
- Tokens or passwords leaking into logs, the app or the network.
- Unsafe handling of data from lamps, the router or the local network.
