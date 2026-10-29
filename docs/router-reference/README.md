# Router Signal Widget

An always-on-top glass widget for Windows that shows the live signal of a ZLT X17M 5G router
(Ooredoo TN) in plain words, plus a speed test, a live ping monitor that explains ping spikes,
and a list of devices on the network. Python 3.12 + PySide6.

## Run it

Double-click **Start.bat**. The first run creates `.venv` and installs `requirements.txt`
(PySide6-Essentials, psutil); later runs start instantly. Type the router admin password once.

- Try it without a router: `.venv\Scripts\python widget.py --mock`
- Tests: `.venv\Scripts\python -m unittest discover -s tests`
- Standalone .exe: `build_exe.bat` → `dist\RouterSignal.exe`

## Modes (header switch)

| Mode | What it shows |
|---|---|
| Simple | 0–100 signal score ring, one plain sentence, 5G/4G bars, getting better/worse |
| Detailed | RSRP / SINR / RSRQ for 5G and 4G side by side, 5-minute history lanes with rating zones |
| Scan | serving cell (PCI, eNodeB, band), neighbor cells, SINR placement meter with tone, "Copy for Ooredoo" |
| Speed | speed test (ping, jitter, download, upload) against speed.cloudflare.com, last 5 results |

Every mode has a ping strip (1.1.1.1 once a second). Click it for the **spike detective**: each
ping spike with its likely cause (app or Windows service on this PC, weak signal, another
device, or the ISP side), tips and top causes. Right-click or the tray icon for the menu.

To reuse parts of it in another project, see **INTEGRATION.md**.

## Safety

- Router: read-only. Only the 4 cmds in `router_api.ALLOWED_CMDS` can be sent (token, login,
  signal, connected-devices list); other cmds on this router can reboot it or change settings.
- The password and router session are DPAPI-encrypted for the current Windows user.
