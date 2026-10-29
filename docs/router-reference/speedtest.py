"""Internet speed test: ping, download and upload against Cloudflare's public speed test
servers (speed.cloudflare.com). Has nothing to do with the router's API.

Standard library only. run() blocks, so call it from a background thread; progress is
reported through a callback and it stops early when the cancel event is set.
"""

import http.client
import os
import ssl
import statistics
import threading
import time

HOST = "speed.cloudflare.com"
PING_COUNT = 10
DOWN_S = 8          # seconds of downloading
UP_S = 6            # seconds of uploading
DOWN_STREAMS = 4    # parallel connections, like browser speed tests
UP_STREAMS = 3
DOWN_CHUNK = 25_000_000
UP_CHUNK = 2_000_000
DOWN_CAP = 150_000_000  # stop early on fast lines, so a test uses at most ~200 MB
UP_CAP = 50_000_000
WARMUP_S = 1.0      # ignore the slow start of each phase
TIMEOUT_S = 10


class SpeedTestError(Exception):
    pass


def _conn():
    return http.client.HTTPSConnection(HOST, timeout=TIMEOUT_S, context=ssl.create_default_context())


def ping_stats(samples_ms):
    """Median round trip and jitter (mean change between consecutive samples), in ms."""
    if not samples_ms:
        return None, None
    jitter = (statistics.mean(abs(a - b) for a, b in zip(samples_ms, samples_ms[1:]))
              if len(samples_ms) > 1 else 0.0)
    return statistics.median(samples_ms), jitter


def mbps(nbytes, seconds):
    return nbytes * 8 / 1e6 / seconds if seconds > 0 else 0.0


class _Meter:
    """Counts bytes from several threads; gives the live rate and the rate after warm-up."""

    def __init__(self):
        self.lock = threading.Lock()
        self.total = 0
        self.t0 = time.time()
        self.marks = [(self.t0, 0)]
        self.warm = None  # (time, total) when warm-up ended

    def add(self, n):
        with self.lock:
            self.total += n

    def sample(self):
        now = time.time()
        with self.lock:
            total = self.total
        self.marks.append((now, total))
        if self.warm is None and now - self.t0 >= WARMUP_S:
            self.warm = (now, total)
        # live rate over the last ~1 s
        old = next(((t, b) for t, b in self.marks if now - t <= 1.0), self.marks[0])
        return mbps(total - old[1], now - old[0])

    def result(self):
        now = time.time()
        with self.lock:
            total = self.total
        start = self.warm or (self.t0, 0)
        return mbps(total - start[1], now - start[0])


def _download_worker(meter, stop, errors):
    try:
        conn = _conn()
        while not stop.is_set():
            conn.request("GET", f"/__down?bytes={DOWN_CHUNK}")
            r = conn.getresponse()
            while not stop.is_set():
                data = r.read(65536)
                if not data:
                    break
                meter.add(len(data))
        conn.close()
    except (OSError, http.client.HTTPException) as e:
        errors.append(e)


def _upload_worker(meter, stop, errors):
    block = os.urandom(65536)  # random, so nothing on the way can compress it
    try:
        conn = _conn()
        while not stop.is_set():
            conn.putrequest("POST", "/__up")
            conn.putheader("Content-Type", "application/octet-stream")
            conn.putheader("Content-Length", str(UP_CHUNK))
            conn.endheaders()
            sent = 0
            while sent < UP_CHUNK:
                if stop.is_set():
                    conn.close()  # abandon this request
                    return
                n = min(len(block), UP_CHUNK - sent)
                conn.send(block[:n])
                sent += n
                meter.add(n)
            conn.getresponse().read()
        conn.close()
    except (OSError, http.client.HTTPException) as e:
        errors.append(e)


def _timed_phase(worker, streams, seconds, cap, phase, progress, cancel):
    meter, stop, errors = _Meter(), threading.Event(), []
    threads = [threading.Thread(target=worker, args=(meter, stop, errors), daemon=True) for _ in range(streams)]
    for t in threads:
        t.start()
    end = time.time() + seconds
    while time.time() < end and not cancel.is_set():
        if len(errors) == streams or meter.total >= cap:
            break
        time.sleep(0.15)
        progress(phase, meter.sample(), 1 - (end - time.time()) / seconds)
    stop.set()
    if cancel.is_set():
        return None
    if meter.total == 0:
        raise SpeedTestError(f"{phase} failed: {errors[0].__class__.__name__ if errors else 'no data'}")
    return meter.result()


def measure_ping(progress, cancel, count=PING_COUNT):
    samples = []
    conn = _conn()
    try:
        conn.request("GET", "/__down?bytes=0")  # first request also opens TLS; not counted
        conn.getresponse().read()
        for i in range(count):
            if cancel.is_set():
                return None, None
            t = time.perf_counter()
            conn.request("GET", "/__down?bytes=0")
            conn.getresponse().read()
            samples.append((time.perf_counter() - t) * 1000)
            progress("ping", samples[-1], (i + 1) / count)
    except (OSError, http.client.HTTPException) as e:
        raise SpeedTestError(f"Can't reach the speed test server ({e.__class__.__name__})") from e
    finally:
        conn.close()
    return ping_stats(samples)


def run(progress, cancel):
    """progress(phase, value, fraction) with phase in ping / download / upload; value is ms or Mbps.
    Returns {"ping", "jitter", "down", "up", "at"} or None if cancelled. Raises SpeedTestError."""
    ping, jitter = measure_ping(progress, cancel)
    if cancel.is_set():
        return None
    down = _timed_phase(_download_worker, DOWN_STREAMS, DOWN_S, DOWN_CAP, "download", progress, cancel)
    if down is None:
        return None
    up = _timed_phase(_upload_worker, UP_STREAMS, UP_S, UP_CAP, "upload", progress, cancel)
    if up is None:
        return None
    return {"ping": ping, "jitter": jitter, "down": down, "up": up, "at": time.time()}


# ---------- plain words

def speed_verdict(down):
    if down is None:
        return ""
    if down >= 100:
        return "Great: 4K on several screens and fast downloads"
    if down >= 25:
        return "Good for 4K or HD streaming and video calls"
    if down >= 8:
        return "OK for browsing and HD video on one screen"
    return "Slow: pages and video will lag"


def ping_verdict(ping):
    if ping is None:
        return ""
    if ping < 30:
        return "great for gaming"
    if ping < 60:
        return "fine for gaming and calls"
    if ping < 100:
        return "OK, games may feel laggy"
    return "high: calls and games will lag"
