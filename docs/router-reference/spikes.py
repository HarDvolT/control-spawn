"""Spike detective: finds ping spikes and explains what was using the network at that moment.

Evidence, all read-only and without admin rights:
  - this PC's total traffic (network adapter byte counters, exact)
  - which programs and Windows services own internet connections, and how busy each one's
    socket I/O was (Windows does not report per-app bytes without admin, so per-app numbers
    are shares of this PC's exact total: an estimate)
  - the router's own traffic counters and signal, taken from the signal reply the widget
    already reads (no extra router commands)

Detection and classification are pure functions so they can be tested with fake data.
"""

import collections
import ipaddress
import statistics
import threading
import time

try:
    import psutil
except ImportError:  # the widget still works; attribution is just unavailable
    psutil = None

BASELINE_S = 60
MIN_BASELINE = 10       # replies needed before spikes are judged
SPIKE_FACTOR = 2.5
SPIKE_EXTRA_MS = 40
END_AFTER = 2           # normal replies in a row that end a spike
LOOKBACK_S = 5          # evidence window starts this long before the spike
KEEP_S = 180
PC_BUSY_MBPS = 2.0
OTHER_DEVICE_MBPS = 2.0

# exe name -> friendly name, tip
APPS = {
    "steam.exe": ("Steam", "Pause Steam downloads while gaming, or limit them in Steam > Downloads."),
    "steamwebhelper.exe": ("Steam", "Pause Steam downloads while gaming, or limit them in Steam > Downloads."),
    "steamservice.exe": ("Steam", "Pause Steam downloads while gaming, or limit them in Steam > Downloads."),
    "epicgameslauncher.exe": ("Epic Games", "Pause Epic downloads or set a download limit in its settings."),
    "battle.net.exe": ("Battle.net", "Pause Battle.net downloads or limit their speed in its settings."),
    "agent.exe": ("Battle.net updater", "Pause Battle.net downloads or limit their speed in its settings."),
    "eadesktop.exe": ("EA app", "Pause EA app downloads while gaming."),
    "ubisoftconnect.exe": ("Ubisoft Connect", "Pause Ubisoft Connect downloads while gaming."),
    "gamingservices.exe": ("Xbox / Microsoft Store games", "Pause downloads in the Xbox app or Microsoft Store."),
    "xboxpcapp.exe": ("Xbox app", "Pause downloads in the Xbox app."),
    "winstore.app.exe": ("Microsoft Store", "Pause app updates in the Microsoft Store."),
    "onedrive.exe": ("OneDrive", "Pause OneDrive syncing (tray icon > Pause syncing) while gaming or on calls."),
    "googledrivefs.exe": ("Google Drive", "Pause Google Drive syncing while gaming or on calls."),
    "dropbox.exe": ("Dropbox", "Pause Dropbox syncing while gaming or on calls."),
    "chrome.exe": ("Chrome", "Close tabs that stream video or download, or pause downloads."),
    "msedge.exe": ("Edge", "Close tabs that stream video or download, or pause downloads."),
    "firefox.exe": ("Firefox", "Close tabs that stream video or download, or pause downloads."),
    "opera.exe": ("Opera", "Close tabs that stream video or download, or pause downloads."),
    "brave.exe": ("Brave", "Close tabs that stream video or download, or pause downloads."),
    "discord.exe": ("Discord", "Discord streams and updates use bandwidth; stop screen sharing if not needed."),
    "teams.exe": ("Teams", "Video calls use a lot of upload; turn off video if the call lags."),
    "ms-teams.exe": ("Teams", "Video calls use a lot of upload; turn off video if the call lags."),
    "zoom.exe": ("Zoom", "Video calls use a lot of upload; turn off video if the call lags."),
    "spotify.exe": ("Spotify", "Spotify downloads songs for offline use in the background."),
    "qbittorrent.exe": ("qBittorrent", "Torrents fill the line; set upload/download limits while gaming."),
    "utorrent.exe": ("uTorrent", "Torrents fill the line; set upload/download limits while gaming."),
    "bittorrent.exe": ("BitTorrent", "Torrents fill the line; set upload/download limits while gaming."),
    "msmpeng.exe": ("Microsoft Defender", "Defender was updating or scanning; it usually finishes quickly."),
    "mpdefendercoreservice.exe": ("Microsoft Defender", "Defender was updating or scanning; it usually finishes quickly."),
    "msedgewebview2.exe": ("An app's web view (WebView2)", "Some app's built-in web page was loading content."),
    "nvcontainer.exe": ("NVIDIA app", "NVIDIA may be downloading a driver; let it finish or pause it."),
    "adobe desktop service.exe": ("Adobe Creative Cloud", "Creative Cloud may be syncing or updating; pause it."),
}

# Windows service -> friendly name, tip
SERVICES = {
    "wuauserv": ("Windows Update", "Windows Update was downloading: set active hours or pause updates while gaming."),
    "usosvc": ("Windows Update", "Windows Update was downloading: set active hours or pause updates while gaming."),
    "dosvc": ("Delivery Optimization (update sharing)",
              "Windows was sharing updates: Settings > Windows Update > Delivery Optimization, turn it off."),
    "bits": ("Background downloads (BITS)", "Windows or an app was downloading in the background; it eases off on its own."),
    "wsearch": ("Windows Search", "Search indexing rarely uses the internet; probably harmless."),
    "cryptsvc": ("Certificate updates", "Windows was checking certificates; short and harmless."),
    "dnscache": ("DNS lookups", "Many lookups at once usually come from a browser or game starting."),
    "installservice": ("Microsoft Store installs", "Pause app updates in the Microsoft Store."),
    "clipsvc": ("Microsoft Store licensing", "Pause app updates in the Microsoft Store."),
    "windefend": ("Microsoft Defender", "Defender was updating or scanning; it usually finishes quickly."),
    "diagtrack": ("Windows telemetry", "Windows was sending diagnostic data; it is usually brief."),
}

SIGNAL_TIP = "The router's signal dropped at that moment: move the router or ask Ooredoo about the cell."
OTHER_TIP = "Another phone, TV or PC on your network was busy (updates, streaming, downloads)."
ISP_TIP = "Nothing on your side was busy: likely congestion on the mobile network; it often happens at peak hours."


def friendly(exe, services=()):
    """(name, detail, tip) for a process, resolving service hosts to their services."""
    exe_l = (exe or "?").lower()
    for svc in services:
        if svc.lower() in SERVICES:
            name, tip = SERVICES[svc.lower()]
            return name, f"{exe} ({svc})", tip
    if exe_l in APPS:
        name, tip = APPS[exe_l]
        return name, exe, tip
    if services:
        return f"Windows service: {', '.join(services[:2])}", f"{exe}", "A Windows service was using the network."
    base = exe[:-4] if exe_l.endswith(".exe") else exe
    return base, exe, f"{base} was using the network; close or pause it to test."


def is_internet(addr):
    try:
        ip = ipaddress.ip_address(addr)
    except ValueError:
        return False
    return not (ip.is_private or ip.is_loopback or ip.is_link_local or ip.is_multicast or ip.is_unspecified)


# ---------------------------------------------------------------- detection

class SpikeDetector:
    """Feed it ping samples (t, ms or None); it returns finished spike events."""

    def __init__(self):
        self.normal = collections.deque()  # (t, ms) of non-spike replies
        self.open = None
        self.calm = 0

    def baseline(self):
        vals = [ms for _, ms in self.normal]
        return statistics.median(vals) if len(vals) >= MIN_BASELINE else None

    def threshold(self):
        base = self.baseline()
        return None if base is None else max(SPIKE_FACTOR * base, base + SPIKE_EXTRA_MS)

    def update(self, t, ms):
        while self.normal and self.normal[0][0] < t - BASELINE_S:
            self.normal.popleft()
        limit = self.threshold()
        spiking = limit is not None and (ms is None or ms > limit)
        if spiking:
            if self.open is None:
                self.open = {"start": t, "end": t, "peak": None, "lost": 0, "count": 0,
                             "baseline": self.baseline()}
            ev = self.open
            ev["end"] = t
            ev["count"] += 1
            if ms is None:
                ev["lost"] += 1
            else:
                ev["peak"] = ms if ev["peak"] is None else max(ev["peak"], ms)
            self.calm = 0
            return None
        if ms is not None:
            self.normal.append((t, ms))
        if self.open is not None:
            self.calm += 1
            if self.calm >= END_AFTER:
                ev, self.open = self.open, None
                return ev
        return None


# ---------------------------------------------------------------- evidence

class Evidence:
    """Ring buffers of what was happening; filled by the sampler thread and the poller."""

    def __init__(self):
        self.lock = threading.Lock()
        self.pc = collections.deque()       # (t, down_mbps, up_mbps)
        self.procs = collections.deque()    # (t, {pid: {"exe", "services", "ops", "conns", "new"}})
        self.router = collections.deque()   # (t, down_mbps, up_mbps)
        self.signal = collections.deque()   # (t, sinr, rsrp, network, pci)
        self._router_last = None

    def _trim(self, dq, now):
        while dq and dq[0][0] < now - KEEP_S:
            dq.popleft()

    def add(self, name, item):
        with self.lock:
            dq = getattr(self, name)
            dq.append(item)
            self._trim(dq, item[0])

    def add_router_counters(self, t, rx_bytes, tx_bytes):
        """Router's cumulative WAN byte counters (wan_rx_bytes / wan_tx_bytes) -> rates."""
        if rx_bytes is None or tx_bytes is None:
            return
        last, self._router_last = self._router_last, (t, rx_bytes, tx_bytes)
        if last and t > last[0] and rx_bytes >= last[1] and tx_bytes >= last[2]:
            dt = t - last[0]
            self.add("router", (t, (rx_bytes - last[1]) * 8 / 1e6 / dt, (tx_bytes - last[2]) * 8 / 1e6 / dt))

    def window(self, name, t0, t1):
        with self.lock:
            return [x for x in getattr(self, name) if t0 <= x[0] <= t1]


def _to_int(v):
    try:
        return int(str(v).strip())
    except (TypeError, ValueError):
        return None


def _to_float(v):
    try:
        s = str(v).strip()
        return float(s) if s else None
    except (TypeError, ValueError):
        return None


def feed_signal(evidence, t, raw):
    """Take what the detective needs from the router's signal reply."""
    sinr5, sinr4 = _to_float(raw.get("SINR_5G")), _to_float(raw.get("SINR"))
    rsrp5, rsrp4 = _to_float(raw.get("RSRP_5G")), _to_float(raw.get("RSRP"))
    on5g = rsrp5 is not None or sinr5 is not None
    evidence.add("signal", (t, sinr5 if on5g else sinr4, rsrp5 if on5g else rsrp4,
                            "5G" if on5g else "4G", str(raw.get("PCI_5G") if on5g else raw.get("PCI", ""))))
    evidence.add_router_counters(t, _to_int(raw.get("wan_rx_bytes")), _to_int(raw.get("wan_tx_bytes")))


class Sampler(threading.Thread):
    """Samples this PC's traffic every second and per-app socket activity every 2 s.
    Only processes that own internet connections are looked at, which keeps it light."""

    def __init__(self, evidence):
        super().__init__(daemon=True)
        self.ev = evidence
        self.stopped = threading.Event()
        self.paused = threading.Event()
        self.services = {}
        self.services_at = 0.0
        self.prev_ops = {}
        self.seen_conns = set()

    def available(self):
        return psutil is not None

    def run(self):
        if psutil is None:
            return
        last_net = None
        tick = 0
        while not self.stopped.is_set():
            t = time.time()
            if not self.paused.is_set():
                try:
                    n = psutil.net_io_counters()
                    if last_net:
                        dt = t - last_net[0]
                        self.ev.add("pc", (t, (n.bytes_recv - last_net[1]) * 8 / 1e6 / dt,
                                           (n.bytes_sent - last_net[2]) * 8 / 1e6 / dt))
                    last_net = (t, n.bytes_recv, n.bytes_sent)
                    if tick % 2 == 0:
                        self._sample_procs(t)
                except Exception:
                    pass
            tick += 1
            self.stopped.wait(max(0.05, 1.0 - (time.time() - t)))

    def _refresh_services(self, t):
        if t - self.services_at < 60:
            return
        self.services_at = t
        svc = {}
        try:
            for s in psutil.win_service_iter():
                try:
                    d = s.as_dict()
                except Exception:
                    continue
                if d.get("pid"):
                    svc.setdefault(d["pid"], []).append(d["name"])
        except Exception:
            pass
        self.services = svc

    def _sample_procs(self, t):
        self._refresh_services(t)
        conns = {}
        new = collections.Counter()
        current = set()
        for c in psutil.net_connections("inet"):
            if not c.pid or not c.raddr or not is_internet(c.raddr.ip):
                continue
            conns[c.pid] = conns.get(c.pid, 0) + 1
            key = (c.pid, c.laddr, c.raddr)
            current.add(key)
            if key not in self.seen_conns:
                new[c.pid] += 1
        first = not self.seen_conns
        self.seen_conns = current
        snap, ops_now = {}, {}
        for pid, count in conns.items():
            try:
                p = psutil.Process(pid)
                io = p.io_counters()
                exe = p.name()
            except Exception:
                continue
            ops = io.other_count + io.read_count + io.write_count
            ops_now[pid] = ops
            delta = ops - self.prev_ops.get(pid, ops)
            snap[pid] = {"exe": exe, "services": self.services.get(pid, []), "ops": max(0, delta),
                         "conns": count, "new": 0 if first else new[pid]}
        self.prev_ops = ops_now
        self.ev.add("procs", (t, snap))

    def stop(self):
        self.stopped.set()


# ---------------------------------------------------------------- classification

def _peak(rows, idx):
    vals = [r[idx] for r in rows if r[idx] is not None]
    return max(vals) if vals else 0.0


def classify(event, ev):
    """Ranked causes for a spike: [{"kind", "name", "detail", "tip", "confidence", "share", "mbps"}]
    plus totals. kind: app / signal / other_device / isp."""
    t0, t1 = event["start"] - LOOKBACK_S, event["end"] + 1
    pc = ev.window("pc", t0, t1)
    pc_down, pc_up = _peak(pc, 1), _peak(pc, 2)
    pc_total = pc_down + pc_up
    causes = []

    # 1. programs / services on this PC
    agg = {}
    for _, snap in ev.window("procs", t0, t1):
        for pid, info in snap.items():
            a = agg.setdefault(pid, {"exe": info["exe"], "services": info["services"], "ops": 0, "new": 0})
            a["ops"] += info["ops"]
            a["new"] += info["new"]
    total_ops = sum(a["ops"] for a in agg.values())
    # group by friendly name so Steam's helpers count as one
    groups = {}
    for a in agg.values():
        name, detail, tip = friendly(a["exe"], a["services"])
        g = groups.setdefault(name, {"detail": detail, "tip": tip, "ops": 0, "new": 0})
        g["ops"] += a["ops"]
        g["new"] += a["new"]
    if total_ops > 0 and pc_total >= PC_BUSY_MBPS:
        ranked = sorted(groups.items(), key=lambda kv: -kv[1]["ops"])
        for name, g in ranked[:3]:
            share = g["ops"] / total_ops
            if share < 0.1:
                continue
            likely = share >= 0.4 and pc_total >= 5
            causes.append({"kind": "app", "name": name, "detail": g["detail"], "tip": g["tip"],
                           "confidence": "likely" if likely else "possibly", "share": share,
                           "mbps": share * pc_total, "new": g["new"]})

    # 2. the router's signal
    sig_before = ev.window("signal", event["start"] - 30, event["start"] - LOOKBACK_S)
    sig_during = ev.window("signal", t0, t1)
    if sig_before and sig_during:
        sinr_b = [s[1] for s in sig_before if s[1] is not None]
        rsrp_b = [s[2] for s in sig_before if s[2] is not None]
        sinr_d = [s[1] for s in sig_during if s[1] is not None]
        rsrp_d = [s[2] for s in sig_during if s[2] is not None]
        reasons = []
        if sinr_b and sinr_d and statistics.mean(sinr_b) - min(sinr_d) >= 3:
            reasons.append(f"signal vs. noise fell {statistics.mean(sinr_b):.0f} → {min(sinr_d):.0f} dB")
        if rsrp_b and rsrp_d and statistics.mean(rsrp_b) - min(rsrp_d) >= 5:
            reasons.append(f"strength fell {statistics.mean(rsrp_b):.0f} → {min(rsrp_d):.0f} dBm")
        if sig_before[-1][3] == "5G" and any(s[3] == "4G" for s in sig_during):
            reasons.append("switched 5G → 4G")
        elif len({s[4] for s in sig_before[-1:] + sig_during if s[4]}) > 1:
            reasons.append("changed cell")
        if reasons:
            causes.append({"kind": "signal", "name": "Weak signal", "detail": ", ".join(reasons), "tip": SIGNAL_TIP,
                           "confidence": "likely" if not causes else "possibly", "share": None, "mbps": None})

    # 3. other devices: router traffic well above this PC's
    router = ev.window("router", t0, t1 + 5)
    router_total = _peak(router, 1) + _peak(router, 2) if router else None
    if router_total is not None and router_total - pc_total >= OTHER_DEVICE_MBPS:
        causes.append({"kind": "other_device", "name": "Another device on your network",
                       "detail": f"router {router_total:.1f} Mbps, this PC {pc_total:.1f} Mbps", "tip": OTHER_TIP,
                       "confidence": "likely" if not causes else "possibly", "share": None,
                       "mbps": router_total - pc_total})

    # 4. nothing found on our side
    if not causes:
        causes.append({"kind": "isp", "name": "Internet / Ooredoo side",
                       "detail": f"this PC {pc_total:.1f} Mbps" + (f", router {router_total:.1f} Mbps"
                                                                   if router_total is not None else ""),
                       "tip": ISP_TIP, "confidence": "possibly", "share": None, "mbps": None})
    return {"causes": causes, "pc_down": pc_down, "pc_up": pc_up, "router_total": router_total,
            "has_pc_data": bool(pc), "has_proc_data": total_ops > 0}


def offenders(spikes):
    """How many spikes each cause was the top cause of, most first."""
    c = collections.Counter(s["causes"][0]["name"] for s in spikes if s.get("causes"))
    return c.most_common()


def is_big(event):
    return (event.get("peak") or 0) >= 150 or event.get("lost", 0) >= 2
