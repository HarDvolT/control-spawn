"""Live ping monitor: pings an internet host (default 1.1.1.1) once a second with Windows'
own ping.exe. Never talks to the router's API.
"""

import collections
import re
import statistics
import subprocess
import sys
import threading
import time

DEFAULT_TARGET = "1.1.1.1"
TARGETS = ["1.1.1.1", "8.8.8.8"]
INTERVAL_S = 1.0
KEEP_S = 5 * 60

# Reply lines carry "TTL=" in every Windows language; the time is "time=12ms", "Zeit=12ms",
# "temps=12 ms", "tiempo=12ms", or "time<1ms".
_TIME = re.compile(r"[=<]\s*(\d+(?:[.,]\d+)?)\s*ms\b", re.I)


def parse_ping(text):
    """Round trip in ms from ping.exe output, or None for a timeout / unreachable."""
    for line in str(text or "").splitlines():
        if "ttl=" not in line.lower():
            continue
        m = _TIME.search(line)
        if m:
            ms = float(m.group(1).replace(",", "."))
            return 0.5 if line[m.start()] == "<" else ms
    return None


def ping_once(target, timeout_ms=1000):
    if sys.platform == "win32":
        cmd = ["ping", "-n", "1", "-w", str(timeout_ms), target]
        flags = subprocess.CREATE_NO_WINDOW
    else:
        cmd = ["ping", "-c", "1", "-W", str(max(1, timeout_ms // 1000)), target]
        flags = 0
    try:
        out = subprocess.run(cmd, capture_output=True, timeout=timeout_ms / 1000 + 2, creationflags=flags)
    except (OSError, subprocess.TimeoutExpired):
        return None
    return parse_ping(out.stdout.decode("oem" if sys.platform == "win32" else "utf-8", "replace"))


def stats(samples, now, window_s):
    """samples: (time, ms or None). Returns dict with count, loss %, avg/min/max/jitter of replies."""
    win = [ms for t, ms in samples if now - t <= window_s]
    ok = [ms for ms in win if ms is not None]
    jitter = statistics.mean(abs(a - b) for a, b in zip(ok, ok[1:])) if len(ok) > 1 else None
    return {
        "count": len(win),
        "loss": 100.0 * (len(win) - len(ok)) / len(win) if win else None,
        "avg": statistics.mean(ok) if ok else None,
        "min": min(ok) if ok else None,
        "max": max(ok) if ok else None,
        "jitter": jitter,
    }


def ping_rating(ms):
    """Excellent < 30 ms, Good < 60, Fair < 100, else Poor (same words as the signal)."""
    if ms is None:
        return None
    if ms < 30:
        return "Excellent"
    if ms < 60:
        return "Good"
    if ms < 100:
        return "Fair"
    return "Poor"


PING_PLAIN = {
    "Excellent": "Great for gaming and calls",
    "Good": "Fine for calls and most games",
    "Fair": "OK for browsing; games may feel laggy",
    "Poor": "Laggy: calls and games will suffer",
    None: "No replies",
}


class PingMonitor(threading.Thread):
    def __init__(self, target=DEFAULT_TARGET, pinger=ping_once):
        super().__init__(daemon=True)
        self.target = target
        self.pinger = pinger
        self.samples = collections.deque()
        self.lock = threading.Lock()
        self.paused = threading.Event()
        self.stopped = threading.Event()

    def run(self):
        while not self.stopped.is_set():
            t = time.time()
            if not self.paused.is_set():
                ms = self.pinger(self.target)
                with self.lock:
                    self.samples.append((t, ms))
                    while self.samples and self.samples[0][0] < t - KEEP_S:
                        self.samples.popleft()
            self.stopped.wait(max(0.05, INTERVAL_S - (time.time() - t)))

    def snapshot(self):
        with self.lock:
            return list(self.samples)

    def set_target(self, target):
        self.target = target
        with self.lock:
            self.samples.clear()

    def stop(self):
        self.stopped.set()
