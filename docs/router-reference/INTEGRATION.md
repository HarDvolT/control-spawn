# Using Router Signal in another project

## Module map

| File | What it gives you | UI-free? | Needs |
|---|---|---|---|
| `router_api.py` | `RouterClient`: `login()`, `get_signal()`, `get_devices()`; `ALLOWED_CMDS` guard | yes | stdlib |
| `ratings.py` | `summarize(raw)` (ratings, `score` 0–100, bars, band), `trend()`, `PLAIN`, `METRICS` | yes | stdlib |
| `scanner.py` | `analyze(raw)` serving + neighbor cells, `decode_eci()`, `BestTracker` | yes | ratings |
| `speedtest.py` | `run(progress, cancel)` ping/jitter/down/up via speed.cloudflare.com | yes | stdlib |
| `pingmon.py` | `PingMonitor` thread (ping.exe), `parse_ping()`, `stats()`, `ping_rating()` | yes | stdlib |
| `spikes.py` | `SpikeDetector`, `Evidence`, `Sampler`, `classify()`, `feed_signal()` | yes | psutil (optional) |
| `devices.py` | `DeviceTracker` (ARP, ping sweep, reverse DNS, vendor), `merge_router_list()` | yes | stdlib |
| `credentials.py` | `load/save_password`, `load/save_session` (DPAPI, `%APPDATA%\RouterSignalWidget`) | yes | stdlib, Windows |
| `widget.py` | PySide6 UI: `GlassWindow` and its views, tray icon, single instance, autostart | no | PySide6 + all above |

Copy the `.py` files (and `tests/` if you want the checks) into a package folder in your project.
They import each other by plain module name (`import ratings`), so either put the folder on
`sys.path` or change those imports to relative ones (`from . import ratings`).

## Core without the UI

```python
import threading, time
from router_api import RouterClient, AuthError, RouterError
from ratings import summarize, PLAIN, score_rating
from scanner import analyze
import pingmon, speedtest, spikes

client = RouterClient(password, host="192.168.0.1")    # one client: the router allows ONE login
raw = client.get_signal()                               # logs in on first use, re-logs in on expiry
s = summarize(raw)
print(s["network"], s["band"], s["overall"], s["score"], PLAIN[score_rating(s["score"])])
cells = analyze(raw)                                    # serving cell, neighbors, PLMN

# live ping + spike detection
mon = pingmon.PingMonitor("1.1.1.1"); mon.start()
det, ev = spikes.SpikeDetector(), spikes.Evidence()
sampler = spikes.Sampler(ev); sampler.start()           # this PC's traffic + per-app activity
seen = 0
while True:
    spikes.feed_signal(ev, time.time(), client.get_signal())
    for t, ms in mon.snapshot():
        if t > seen:
            seen = t
            event = det.update(t, ms)
            if event:
                print(event["peak"], spikes.classify(event, ev)["causes"][0]["name"])
    time.sleep(2)

# speed test (blocking; run it in a thread)
result = speedtest.run(lambda phase, value, frac: None, threading.Event())
```

Errors: `AuthError(already_logged_in=True)` means another session (e.g. the router web page)
holds the login; retry later. `RouterError` means unreachable / bad reply.
To survive restarts without "logged in elsewhere", save `client.session_id` (see
`credentials.save_session`) and pass it back: `RouterClient(pw, session_id=saved)`.

## Embedding the UI in another PySide6 app

The simplest way is to run the whole window as-is; it is a normal top-level `QWidget`:

```python
from PySide6.QtWidgets import QApplication
import widget
app = QApplication([])
app.setStyleSheet(widget.APP_QSS)
win = widget.GlassWindow()        # mock=True for demo data
win.show_window()
app.exec()
```

`GlassWindow` owns the poller, ping monitor, spike sampler, tray icon and settings. The views
(`SimpleView`, `SignalView`, `ScannerView`, `SpeedView`) are painted `QWidget`s that call back
into their window (`win.rate`, `win.pingmon`, `win.show_info`, `win.set_mode`, …), so they are
not drop-in widgets on their own. To put the readings into your own UI, use the core modules
above and draw your own widgets. To reuse the look, take `draw_track`, `draw_panel`,
`draw_button`, the colors (`RATING_COLORS`) and `Tween` from `widget.py`.

## Standalone

- `Start.bat`: creates `.venv`, installs `requirements.txt` (PySide6-Essentials, psutil), starts
  `widget.py` with pythonw. `widget.py --mock` shows demo data without a router.
- Tests: `.venv\Scripts\python -m unittest discover -s tests`
- One-file exe: `build_exe.bat` (PyInstaller) → `dist\RouterSignal.exe`
- Settings, password and session live in `%APPDATA%\RouterSignalWidget`, not in the app folder.

## Safety rules (keep them when integrating)

- `RouterClient._call` refuses any cmd outside `ALLOWED_CMDS`: get token, login, read signal,
  read connected-devices list. The two read cmds are only ever sent with method GET. Other cmds
  on this router can reboot it or change settings.
- Certificate verification is off only for the router's own HTTPS opener (self-signed cert);
  the speed test uses normal TLS verification.
- The password is never logged or printed; it is stored DPAPI-encrypted per Windows user.
- Router UUIDs/fields are for the ZLT X17M (Ooredoo TN firmware); other models may differ.
