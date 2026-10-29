"""Devices on your network: seen from this PC (ARP table, optional ping sweep, reverse DNS)
and, when the widget is logged in, from the router's read-only connected-devices list
(merge_router_list; the widget fetches it, this module never talks to the router).

It can list who is online, but not how much each one downloads: that would need the
router to report per-device traffic. ROUTER_DEVICE_TRAFFIC is the hook for that and is off.
"""

import concurrent.futures
import ipaddress
import re
import socket
import subprocess
import sys
import threading
import time

ROUTER_DEVICE_TRAFFIC = False  # hook: per-device traffic from the router (not implemented, no router call)

REFRESH_S = 30
NEW_S = 10 * 60

# A small set of common vendor prefixes (first 3 bytes of the MAC).
OUI = {
    "00:1A:11": "Google", "3C:5A:B4": "Google", "F4:F5:D8": "Google", "D8:6C:63": "Google",
    "F0:D2:F1": "Amazon", "74:C2:46": "Amazon", "FC:65:DE": "Amazon", "44:65:0D": "Amazon",
    "00:1B:63": "Apple", "28:CF:E9": "Apple", "3C:07:54": "Apple", "A4:83:E7": "Apple", "F0:18:98": "Apple",
    "BC:92:6B": "Apple", "DC:A9:04": "Apple", "8C:85:90": "Apple", "AC:BC:32": "Apple", "F8:FF:C2": "Apple",
    "00:16:6C": "Samsung", "5C:0A:5B": "Samsung", "8C:77:12": "Samsung", "A0:0B:BA": "Samsung",
    "CC:07:AB": "Samsung", "F4:7B:09": "Samsung", "64:1C:AE": "Samsung", "84:25:19": "Samsung",
    "28:6C:07": "Xiaomi", "64:09:80": "Xiaomi", "9C:99:A0": "Xiaomi", "F8:A4:5F": "Xiaomi", "50:8F:4C": "Xiaomi",
    "00:E0:FC": "Huawei", "28:6E:D4": "Huawei", "48:46:FB": "Huawei", "E8:CD:2D": "Huawei", "AC:E2:15": "Huawei",
    "A4:3B:FA": "Oppo", "2C:5B:B8": "Oppo", "C8:F2:30": "Oppo",
    "00:1E:75": "LG", "10:68:3F": "LG", "A8:23:FE": "LG", "C4:36:6C": "LG",
    "00:1D:BA": "Sony", "FC:F1:52": "Sony", "AC:9B:0A": "Sony", "70:9E:29": "Sony",
    "14:CC:20": "TP-Link", "50:C7:BF": "TP-Link", "C0:4A:00": "TP-Link", "F4:F2:6D": "TP-Link", "98:DA:C4": "TP-Link",
    "00:1B:21": "Intel", "3C:A9:F4": "Intel", "8C:8D:28": "Intel", "A4:4C:C8": "Intel", "F8:63:3F": "Intel",
    "00:E0:4C": "Realtek", "52:54:00": "Virtual machine", "00:15:5D": "Hyper-V",
    "B8:27:EB": "Raspberry Pi", "DC:A6:32": "Raspberry Pi", "18:B4:30": "Nest",
    "FC:3F:FC": "Router (ZLT)", "8E:31:29": "Router (ZLT)",
}

_ARP = re.compile(r"^\s*(\d+\.\d+\.\d+\.\d+)\s+([0-9a-fA-F]{2}(?:[-:][0-9a-fA-F]{2}){5})\s+(\w+)", re.M)


def norm_mac(mac):
    return mac.replace("-", ":").upper()


def is_private_mac(mac):
    """Phones and tablets use random 'private' addresses: the locally-administered bit is set."""
    return bool(int(mac[:2], 16) & 0x02)


def vendor(mac):
    if is_private_mac(mac):
        return "Private address (usually a phone or tablet)"
    return OUI.get(mac[:8], "")


def parse_arp(text, subnet=None):
    """[(ip, mac)] from `arp -a`, skipping broadcast/multicast and other subnets."""
    out = []
    for ip, mac, _kind in _ARP.findall(text or ""):
        mac = norm_mac(mac)
        if mac == "FF:FF:FF:FF:FF:FF" or mac.startswith("01:00:5E") or mac.startswith("33:33"):
            continue
        addr = ipaddress.ip_address(ip)
        if addr.is_multicast or ip.endswith(".255"):
            continue
        if subnet and addr not in subnet:
            continue
        out.append((ip, mac))
    return out


def _run(cmd, timeout=10):
    flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
    try:
        r = subprocess.run(cmd, capture_output=True, timeout=timeout, creationflags=flags)
        return r.stdout.decode("oem" if sys.platform == "win32" else "utf-8", "replace")
    except (OSError, subprocess.TimeoutExpired):
        return ""


def local_subnet(router_ip="192.168.0.1"):
    """The /24 the router is on (the widget only cares about the router's LAN)."""
    return ipaddress.ip_network(router_ip + "/24", strict=False)


class DeviceTracker:
    """Remembers devices seen on the LAN. `store` is a dict kept in settings.json."""

    def __init__(self, store, router_ip="192.168.0.1", own_ip=None):
        self.store = store              # mac -> {"ip", "name", "vendor", "first", "last"}
        self.subnet = local_subnet(router_ip)
        self.router_ip = router_ip
        self.own_ip = own_ip
        self.lock = threading.Lock()
        self.started = time.time()
        self.scanning = False

    def refresh(self, now=None, arp_text=None):
        now = now or time.time()
        text = arp_text if arp_text is not None else _run(["arp", "-a"])
        found = parse_arp(text, self.subnet)
        with self.lock:
            for ip, mac in found:
                d = self.store.get(mac)
                if d is None:
                    d = self.store[mac] = {"ip": ip, "name": "", "vendor": vendor(mac), "first": now, "last": now}
                d["ip"], d["last"] = ip, now
        return found

    def resolve_names(self):
        """Reverse DNS for devices without a name (the router often knows DHCP names)."""
        with self.lock:
            todo = [(mac, d["ip"]) for mac, d in self.store.items() if not d.get("name")]

        def lookup(item):
            mac, ip = item
            try:
                return mac, socket.gethostbyaddr(ip)[0].split(".")[0]
            except (OSError, UnicodeError):
                return mac, ""
        socket.setdefaulttimeout(1.5)
        try:
            with concurrent.futures.ThreadPoolExecutor(8) as pool:
                for mac, name in pool.map(lookup, todo):
                    if name and name != self.store[mac]["ip"]:
                        with self.lock:
                            self.store[mac]["name"] = name
        finally:
            socket.setdefaulttimeout(None)

    def sweep(self):
        """One gentle ping sweep of the /24 (on request only) so quiet devices show up in ARP."""
        if self.scanning:
            return
        self.scanning = True
        try:
            hosts = [str(h) for h in self.subnet.hosts()]
            flags = ["-n", "1", "-w", "300"] if sys.platform == "win32" else ["-c", "1", "-W", "1"]
            with concurrent.futures.ThreadPoolExecutor(16) as pool:
                list(pool.map(lambda ip: _run(["ping", *flags, ip], timeout=3), hosts))
            self.refresh()
            self.resolve_names()
        finally:
            self.scanning = False

    def online(self, at=None, window=REFRESH_S * 3):
        at = at or time.time()
        with self.lock:
            return {mac: dict(d) for mac, d in self.store.items() if abs(d["last"] - at) <= window or
                    (d["first"] <= at <= d["last"])}

    def label(self, mac, d):
        if d["ip"] == self.router_ip:
            return "Router"
        if d["ip"] == self.own_ip:
            return "This PC"
        return d.get("name") or d.get("vendor") or "Unknown device"

    def is_new(self, d, now=None):
        now = now or time.time()
        return d["first"] > self.started + 5 and now - d["first"] < NEW_S


def merge_router_list(tracker, resp, now=None):
    """Merge the router's read-only connected-devices reply into the tracker: names the router
    got from DHCP, Wi-Fi or cable, and the Wi-Fi band. Returns how many devices it listed."""
    now = now or time.time()
    band = {}
    for key, label in (("wlan24g_wifi_info", "Wi-Fi 2.4 GHz"), ("wlan5g_wifi_info", "Wi-Fi 5 GHz")):
        for w in resp.get(key) or []:
            if isinstance(w, dict) and w.get("ip"):
                rssi = str(w.get("rssi") or "").strip()
                band[w["ip"]] = f"{label} ({rssi} dBm)" if rssi.lstrip("-").isdigit() else label
    count = 0
    with tracker.lock:
        for d in resp.get("dhcp_list_info") or []:
            if not isinstance(d, dict) or not d.get("mac") or not d.get("ip"):
                continue
            mac = norm_mac(str(d["mac"]))
            entry = tracker.store.get(mac)
            if entry is None:
                entry = tracker.store[mac] = {"ip": d["ip"], "name": "", "vendor": vendor(mac),
                                              "first": now, "last": now}
            entry["ip"], entry["last"] = d["ip"], now
            host = str(d.get("hostname") or "").strip()
            if host and host not in ("*", "unknown"):
                entry["name"] = host
            iface = str(d.get("interface") or "")
            entry["link"] = band.get(d["ip"]) or ("Wi-Fi" if iface == "wlan" else "Cable" if iface == "eth" else "")
            count += 1
    return count


def router_device_traffic(raw):
    """Hook for a future per-device traffic source from the router. Off: returns None and
    makes no router call. The connected-devices list has a per-device "flow" field, but on
    this router it stays 0 for every device, so it can't be used yet."""
    if not ROUTER_DEVICE_TRAFFIC:
        return None
    return None
