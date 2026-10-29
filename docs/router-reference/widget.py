"""Always-on-top glass widget showing the router's live signal (PySide6).

Double-click Start.bat, or run:  pythonw widget.py
The first time, it asks for the router password and remembers it.
Try it without the router:       python widget.py --mock

Three modes, switched in the header (the last one is remembered):
  Simple   - a 0-100 signal score in plain words, 5G and 4G bars, getting better / worse
  Detailed - rating, every reading for 5G and 4G side by side, 5-minute history
  Scan     - serving cell, neighbor cells, a big SINR meter with optional tone, best spot,
             and "Copy for Ooredoo" for asking the carrier to lock a cell

Lives in the system tray: ✕ hides it, the tray icon shows it again. Click ⓘ for help,
the "every Ns" chip to change the update rate, right-click (or the tray icon) for the menu.
"""

import argparse
import collections
import json
import math
import os
import queue
import random
import html
import re
import socket
import sys
import threading
import time

from PySide6.QtCore import QPoint, QPointF, QRectF, Qt, QTimer
from PySide6.QtGui import (QActionGroup, QBrush, QColor, QConicalGradient, QFont, QFontMetrics, QIcon,
                           QLinearGradient, QPainter, QPainterPath, QPen, QPixmap, QRadialGradient)
from PySide6.QtNetwork import QLocalServer, QLocalSocket
from PySide6.QtWidgets import (QApplication, QCheckBox, QHBoxLayout, QInputDialog, QLabel, QLineEdit, QMenu,
                               QPushButton, QStackedLayout, QSystemTrayIcon, QVBoxLayout, QWidget)

import credentials
import devices
import speedtest
import spikes
from ratings import (EXCELLENT, FAIR, GOOD, METRICS, PLAIN, POOR, THRESHOLDS, TREND_S, VERDICTS, rate,
                     score_rating, summarize, threshold_text, trend, weakest_metric)
from pingmon import DEFAULT_TARGET, PING_PLAIN, TARGETS, PingMonitor, ping_rating
from pingmon import stats as ping_stats
from router_api import DEFAULT_HOST, AuthError, RouterClient, RouterError
from scanner import BestTracker, analyze, tone_hz

APP_TITLE = "Router Signal"
APP_NAME = credentials.APP_NAME
RATES = (1, 2, 5, 10)  # seconds between readings, picked in the menu or the footer chip
DEFAULT_RATE = 2
RETRY_S = 5
BUSY_RETRY_S = 30      # another session holds the single login
HISTORY_S = 5 * 60
ANIM_S = 0.6           # values glide to each new reading
PING_S = 0.9           # sonar ring after each reading
MOTION = {"on": True}  # "Reduce motion" turns it off

MODES = [("simple", "Simple"), ("detailed", "Detailed"), ("scan", "Scan"), ("speed", "Speed")]
OLD_MODES = {"signal": "detailed", "scanner": "scan"}

ROUTER_HOST = os.environ.get("ROUTER_HOST") or DEFAULT_HOST

W = 340
PAD = 16
UI_FONT = "Segoe UI Variable Text" if sys.platform == "win32" else "Inter"

RATING_COLORS = {
    EXCELLENT: QColor("#34d399"),
    GOOD: QColor("#a3e635"),
    FAIR: QColor("#fbbf24"),
    POOR: QColor("#f87171"),
    None: QColor("#9ca3af"),
}
ZONES = [POOR, FAIR, GOOD, EXCELLENT]
TEXT = QColor(255, 255, 255, 235)
TEXT_DIM = QColor(255, 255, 255, 140)
TEXT_FAINT = QColor(255, 255, 255, 90)
LIVE = QColor("#34d399")
OFFLINE = QColor("#9ca3af")
STRONGER = QColor("#34d399")
ACCENT = QColor("#60a5fa")
RSRP_LINE = QColor("#60a5fa")
SINR_LINE = QColor("#f59e0b")

AL = Qt.AlignmentFlag
LEFT = AL.AlignVCenter | AL.AlignLeft
RIGHT = AL.AlignVCenter | AL.AlignRight
CENTER = AL.AlignCenter


def font(size, weight=QFont.Weight.Normal):
    f = QFont(UI_FONT)
    f.setPointSizeF(size)
    f.setWeight(weight)
    return f


def alpha(color, a):
    c = QColor(color)
    c.setAlpha(a)
    return c


def fmt(v, digits=0, none="–"):
    return none if v is None else f"{v:.{digits}f}"


def elide(text, f, width):
    return QFontMetrics(f).elidedText(text, Qt.TextElideMode.ElideRight, int(width))


def clock(ts):
    return time.strftime("%H:%M:%S", time.localtime(ts))


def settings_path():
    return os.path.join(credentials.app_data_dir(), "settings.json")


def load_settings():
    try:
        with open(settings_path(), encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def save_settings(data):
    try:
        with open(settings_path(), "w", encoding="utf-8") as f:
            json.dump(data, f)
    except OSError:
        pass


def enable_windows_blur(widget):
    """Blur whatever is behind the window (Windows 10/11) and round its corners (Windows 11).
    Returns False elsewhere or if Windows refuses, and the card is then painted more opaque."""
    if sys.platform != "win32":
        return False
    try:
        import ctypes
        from ctypes import wintypes

        hwnd = int(widget.winId())

        class AccentPolicy(ctypes.Structure):
            _fields_ = [("AccentState", ctypes.c_int), ("AccentFlags", ctypes.c_int),
                        ("GradientColor", ctypes.c_uint), ("AnimationId", ctypes.c_int)]

        class CompositionData(ctypes.Structure):
            _fields_ = [("Attribute", ctypes.c_int), ("Data", ctypes.c_void_p), ("SizeOfData", ctypes.c_size_t)]

        accent = AccentPolicy(3, 0, 0, 0)  # ACCENT_ENABLE_BLURBEHIND
        data = CompositionData(19, ctypes.cast(ctypes.pointer(accent), ctypes.c_void_p), ctypes.sizeof(accent))
        ok = ctypes.windll.user32.SetWindowCompositionAttribute(wintypes.HWND(hwnd), ctypes.byref(data))
        corner = ctypes.c_int(2)  # DWMWCP_ROUND
        ctypes.windll.dwmapi.DwmSetWindowAttribute(wintypes.HWND(hwnd), 33, ctypes.byref(corner), ctypes.sizeof(corner))
        return bool(ok)
    except Exception:
        return False


# ---------------------------------------------------------------- data

class MockClient:
    """Example values from the router, with a little jitter."""

    BASE = {
        "network_type_str": "5G(NSA)", "network_operator": "Ooredoo TN", "signal_lvl": "3",
        "RSRP": "-92", "RSRQ": "-14.0", "SINR": "0.0", "RSSI": "-77", "CQI": "9",
        "PCI": "272+272", "currentband": "1+3", "bandwidth": "20+20", "CELL_ID": "1E24A03",
        "RSRP_5G": "-97", "RSRQ_5G": "-11", "SINR_5G": "8", "RSSI_5G": "-86", "CQI_5G": "7",
        "PCI_5G": "715", "currentband_5g": "78", "bandwidth_5g": "100",
        "lte_info": "1850,272,-92,-14,0;1850,301,-89,-12,4;100,118,-101,-17,-3;3650,56,-106,-19,-6;",
        "nr_info": "",
    }

    def login(self):
        return "mock"

    def get_signal(self):
        d = dict(self.BASE)
        for k in ("RSRP", "RSRP_5G", "SINR", "SINR_5G", "RSRQ", "RSRQ_5G"):
            d[k] = f"{float(d[k]) + random.uniform(-3, 3):.1f}"
        rows = []
        for row in d["lte_info"].strip(";").split(";"):
            e, p, r, q, s = row.split(",")
            rows.append(f"{e},{p},{float(r) + random.uniform(-3, 3):.0f},{q},{float(s) + random.uniform(-2, 2):.0f}")
        d["lte_info"] = ";".join(rows) + ";"
        return d


class Poller(threading.Thread):
    """Polls in the background so the window never freezes."""

    def __init__(self, out):
        super().__init__(daemon=True)
        self.out = out
        self.client = None
        self.saved_sid = None
        self.interval = DEFAULT_RATE
        self.paused = threading.Event()
        self.wake = threading.Event()
        self.stopped = threading.Event()

    def run(self):
        while not self.stopped.is_set():
            client = self.client
            if client is None or self.paused.is_set():
                delay = 3600
            else:
                try:
                    self.out.put(("ok", client.get_signal()))
                    delay = self.interval
                    sid = getattr(client, "session_id", None)
                    if sid and sid != self.saved_sid:
                        self.saved_sid = sid
                        try:
                            credentials.save_session(sid)
                        except OSError:
                            pass
                except AuthError as e:
                    if e.already_logged_in:
                        delay = BUSY_RETRY_S
                        self.out.put(("err", f"Logged in elsewhere, retry in {delay}s"))
                    else:
                        self.client = None
                        self.out.put(("bad_password", "The router rejected the saved password."))
                        delay = 3600
                except RouterError:
                    delay = RETRY_S
                    self.out.put(("err", "Reconnecting…"))
                except Exception as e:  # keep the widget alive whatever happens
                    delay = RETRY_S
                    self.out.put(("err", f"Error: {e.__class__.__name__}"))
            self.wake.wait(delay)
            self.wake.clear()

    def set_client(self, client):
        self.client = client
        self.wake.set()

    def set_interval(self, seconds):
        self.interval = seconds
        self.wake.set()

    def set_paused(self, paused):
        (self.paused.set if paused else self.paused.clear)()
        self.wake.set()

    def stop(self):
        self.stopped.set()
        self.wake.set()


class Tone(threading.Thread):
    """Placement beeper: pitch follows SINR (0 dB -> 300 Hz, 20 dB -> 900 Hz). Windows only."""

    def __init__(self):
        super().__init__(daemon=True)
        self.on = threading.Event()
        self.hz = None
        self.available = sys.platform == "win32"

    def run(self):
        if not self.available:
            return
        import winsound
        while True:
            self.on.wait()
            hz = self.hz
            if hz:
                try:
                    winsound.Beep(hz, 160)
                except RuntimeError:
                    pass
            time.sleep(0.14)


# ---------------------------------------------------------------- shared painting

def draw_track(p, rect, metric, value, rating, live=True, ticks=True, color=None):
    """Colored Poor→Excellent zones with a marker at the current value."""
    meta = METRICS[metric]
    lo, hi = meta["scale"]
    exc, good, fair = THRESHOLDS[metric]
    bounds = [lo, fair, good, exc, hi]
    x0, x1 = rect.left(), rect.right()
    color = (color or RATING_COLORS[rating]) if live else OFFLINE

    def xpos(v):
        return x0 + (min(max(v, lo), hi) - lo) / (hi - lo) * (x1 - x0)

    ty, th = rect.top(), rect.height()
    clip = QPainterPath()
    clip.addRoundedRect(rect, th / 2, th / 2)
    p.save()
    p.setClipPath(clip)
    p.setPen(Qt.PenStyle.NoPen)
    for i, zone in enumerate(ZONES):
        zr = QRectF(xpos(bounds[i]), ty, xpos(bounds[i + 1]) - xpos(bounds[i]), th)
        p.setBrush(alpha(RATING_COLORS[zone], 200 if (zone == rating and live) else 70))
        p.drawRect(zr)
    p.restore()
    for b in (fair, good, exc):
        p.setPen(QPen(QColor(0, 0, 0, 90), 1.5))
        p.drawLine(QPointF(xpos(b), ty), QPointF(xpos(b), ty + th))

    if value is not None:
        mx, my = xpos(value), ty + th / 2
        g = QRadialGradient(QPointF(mx, my), 12)
        g.setColorAt(0, alpha(color, 120))
        g.setColorAt(1, alpha(color, 0))
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(g)
        p.drawEllipse(QPointF(mx, my), 12, 12)
        p.setBrush(QColor("#ffffff"))
        p.setPen(QPen(color, 2.5))
        p.drawEllipse(QPointF(mx, my), 6, 6)

    if ticks:
        p.setFont(font(7))
        p.setPen(TEXT_FAINT)
        for b in (fair, good, exc):
            p.drawText(QRectF(xpos(b) - 20, ty + th + 2, 40, 12), CENTER, f"{b:g}")
        p.drawText(QRectF(x0, ty + th + 2, 60, 12), AL.AlignLeft, "◂ " + meta["ends"][0])
        p.drawText(QRectF(x1 - 60, ty + th + 2, 60, 12), AL.AlignRight, meta["ends"][1] + " ▸")


def draw_panel(p, rect, radius=10):
    p.setPen(QPen(QColor(255, 255, 255, 22), 1))
    p.setBrush(QColor(0, 0, 0, 40))
    p.drawRoundedRect(rect, radius, radius)


def draw_section_label(p, x, y, text, right_text=""):
    p.setFont(font(7.5, QFont.Weight.DemiBold))
    p.setPen(TEXT_DIM)
    p.drawText(QRectF(x, y, 200, 14), LEFT, text)
    if right_text:
        p.setFont(font(7.5))
        p.setPen(TEXT_FAINT)
        p.drawText(QRectF(W - PAD - 200, y, 200, 14), RIGHT, right_text)


def draw_button(p, rect, text, hover, active=False):
    g = QLinearGradient(rect.topLeft(), rect.bottomLeft())
    if active:
        g.setColorAt(0, alpha(ACCENT, 200))
        g.setColorAt(1, QColor("#3b82f6"))
    else:
        g.setColorAt(0, QColor(255, 255, 255, 46 if hover else 30))
        g.setColorAt(1, QColor(255, 255, 255, 30 if hover else 16))
    p.setBrush(g)
    p.setPen(QPen(QColor(255, 255, 255, 50), 1))
    p.drawRoundedRect(rect, 9, 9)
    p.setPen(QColor("#ffffff") if active else TEXT)
    p.setFont(font(8.5, QFont.Weight.DemiBold))
    p.drawText(rect, CENTER, text)


# ---------------------------------------------------------------- signal view

# ---------------------------------------------------------------- animation helpers

def ease(k):
    return 1 - (1 - k) ** 3  # ease-out cubic


def mix(c1, c2, k):
    return QColor(int(c1.red() + (c2.red() - c1.red()) * k), int(c1.green() + (c2.green() - c1.green()) * k),
                  int(c1.blue() + (c2.blue() - c1.blue()) * k), int(c1.alpha() + (c2.alpha() - c1.alpha()) * k))


class Tween:
    """A number or QColor that glides to each new target over ANIM_S (jumps when motion is off)."""

    def __init__(self):
        self.a = self.b = None
        self.t0 = 0.0

    def set(self, target, now):
        if target is None or self.b is None or not MOTION["on"]:
            self.a = self.b = target
            return
        self.a, self.b, self.t0 = self.get(now), target, now

    def get(self, now):
        if self.a is None or self.b is None:
            return self.b
        k = ease(min(1.0, (now - self.t0) / ANIM_S)) if MOTION["on"] else 1.0
        if isinstance(self.b, QColor):
            return mix(self.a, self.b, k)
        return self.a + (self.b - self.a) * k


class SignalView(QWidget):
    """Detailed view. Everything is painted here, so it can be translucent.

    Nothing pops up on hover: help sits behind the small ⓘ icons and opens on click."""

    MODE = "detailed"

    def __init__(self, win):
        super().__init__()
        self.win = win
        self.setMouseTracking(True)
        self.summary = None
        self.history = collections.deque()  # (time, rsrp, sinr) of the main link
        self.live = False
        self.status = "Connecting…"
        self.updated_at = None
        self.pulse_at = 0.0
        self.tweens = collections.defaultdict(Tween)
        self.buttons = {}         # name -> rect
        self.infos = {}           # button name -> help text
        self.hover_btn = None
        self.tick = QTimer(self, interval=1000, timeout=self.update)
        self.tick.start()
        self.anim = QTimer(self, interval=16, timeout=self._animate)

    # ----- data -----
    def show_data(self, s, raw):
        self.summary = s
        self.live = True
        self.status = ""
        self.updated_at = time.time()
        self.pulse_at = self.updated_at
        self._targets(s, self.pulse_at)
        if self.isVisible() and MOTION["on"]:
            self.anim.start()
        main = s["nr"] if s["main_is_5g"] else s["lte"]
        if main["present"]:
            self.history.append((self.updated_at, s["main_rsrp"], s["main_sinr"]))
        while self.history and self.history[0][0] < self.updated_at - HISTORY_S:
            self.history.popleft()
        self.update()

    def set_offline(self, status):
        self.live = False
        self.status = status
        self.update()

    def tv(self, key, now=None):
        return self.tweens[key].get(now or time.time())

    def _targets(self, s, now):
        main = s["nr"] if s["main_is_5g"] else s["lte"]
        self.tweens["spark_RSRP"].set(main["values"]["RSRP"], now)
        self.tweens["spark_SINR"].set(main["values"]["SINR"], now)
        for key in ("nr", "lte"):
            for m in METRICS:
                v = s[key]["values"][m]
                self.tweens[f"{key}_{m}"].set(v, now)
                self.tweens[f"{key}_{m}_c"].set(RATING_COLORS[rate(m, v)], now)

    def _animate(self):
        if time.time() - self.pulse_at > PING_S:
            self.anim.stop()
        self.update()

    def preferred_height(self):
        return 548

    # ----- painting -----
    def paintEvent(self, _):
        p = self._begin()
        s = self.summary
        y = self._header(p, 14, s)
        y = self._hero(p, y + 10, s)
        for m in ("RSRP", "SINR", "RSRQ"):
            y = self._metric(p, y + 14, m, s)
        y = self._spark(p, y + 14)
        y = self._pingstrip(p, y + 10)
        self._footer(p, y + 8)
        p.end()

    def _begin(self):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setRenderHint(QPainter.RenderHint.TextAntialiasing)
        self.buttons, self.infos = {}, {}
        return p

    def _info(self, p, x, y, text):
        """Small ⓘ at (x, y) (top-left of a 14 px circle). Clicking it shows `text`."""
        name = f"info{len(self.infos)}"
        r = QRectF(x, y, 14, 14)
        self.buttons[name] = r.adjusted(-4, -4, 4, 4)
        self.infos[name] = text
        hov = self.hover_btn == name
        p.setPen(QPen(QColor(255, 255, 255, 150 if hov else 80), 1))
        p.setBrush(QColor(255, 255, 255, 40 if hov else 0))
        p.drawEllipse(r)
        p.setPen(QColor(255, 255, 255, 220 if hov else 150))
        p.setFont(font(7, QFont.Weight.Bold))
        p.drawText(r, CENTER, "i")
        return x + 18

    def _ping(self, p, center, r0, r1, color):
        """Sonar ring that expands and fades after each new reading."""
        k = (time.time() - self.pulse_at) / PING_S
        if not MOTION["on"] or not 0 <= k < 1:
            return
        r = r0 + (r1 - r0) * ease(k)
        p.setPen(QPen(alpha(color, int(200 * (1 - k))), 1.6))
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.drawEllipse(center, r, r)

    def _header(self, p, y, s):
        w = self.width()
        c = LIVE if self.live else OFFLINE
        center = QPointF(PAD + 4, y + 10)
        if self.live:
            g = QRadialGradient(center, 9)
            g.setColorAt(0, alpha(c, 150))
            g.setColorAt(1, alpha(c, 0))
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(g)
            p.drawEllipse(center, 9, 9)
            self._ping(p, center, 4, 11, c)
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(c)
        p.drawEllipse(center, 4, 4)

        # close
        r = QRectF(w - PAD - 20, y - 1, 22, 22)
        self.buttons["close"] = r
        if self.hover_btn == "close":
            p.setBrush(QColor(255, 255, 255, 30))
            p.drawRoundedRect(r, 6, 6)
        p.setPen(TEXT_DIM)
        p.setFont(font(9))
        p.drawText(r, CENTER, "✕")

        # Simple · Detailed · Scan
        sf = font(7, QFont.Weight.DemiBold)
        fm = QFontMetrics(sf)
        widths = [fm.horizontalAdvance(label) + 12 for _, label in MODES]
        box = QRectF(r.left() - 6 - sum(widths) - 4, y, sum(widths) + 4, 20)
        p.setPen(QPen(QColor(255, 255, 255, 30), 1))
        p.setBrush(QColor(255, 255, 255, 14))
        p.drawRoundedRect(box, 10, 10)
        x = box.left() + 2
        for (mode, label), sw in zip(MODES, widths):
            sr = QRectF(x, y + 2, sw, 16)
            self.buttons["mode_" + mode] = sr
            if mode == self.MODE:
                g = QLinearGradient(sr.topLeft(), sr.bottomLeft())
                g.setColorAt(0, alpha(ACCENT, 210))
                g.setColorAt(1, QColor("#3b82f6"))
                p.setPen(Qt.PenStyle.NoPen)
                p.setBrush(g)
                p.drawRoundedRect(sr, 8, 8)
            elif self.hover_btn == "mode_" + mode:
                p.setPen(Qt.PenStyle.NoPen)
                p.setBrush(QColor(255, 255, 255, 28))
                p.drawRoundedRect(sr, 8, 8)
            p.setPen(QColor("#ffffff") if mode == self.MODE else TEXT_DIM)
            p.setFont(sf)
            p.drawText(sr, CENTER, label)
            x += sw

        title = "Connecting…"
        if s:
            title = s["network"] + (f" · {s['band']}" if s["band"] else "")
        tf = font(9.5, QFont.Weight.DemiBold)
        if s and QFontMetrics(tf).horizontalAdvance(title) > box.left() - (PAD + 16) - 6:
            title = s["network"]  # drop the band before eliding
        p.setPen(TEXT)
        p.setFont(tf)
        tw = box.left() - (PAD + 16) - 6
        p.drawText(QRectF(PAD + 16, y, tw, 20), LEFT, elide(title, tf, tw))
        return y + 20

    def _hero(self, p, y, s):
        w = self.width()
        rating = s["overall"] if s else None
        color = RATING_COLORS[rating] if self.live else OFFLINE
        p.setPen(color)
        hf = font(24, QFont.Weight.Bold)
        p.setFont(hf)
        word = rating or ("No signal" if s else "—")
        p.drawText(QRectF(PAD, y, 220, 36), LEFT, word)

        verdict = VERDICTS[rating]
        if s and rating in (FAIR, POOR):
            main = s["nr"] if s["main_is_5g"] else s["lte"]
            weak = weakest_metric(main)
            if weak == "SINR":
                verdict = "Held back by interference (SINR)"
            elif weak == "RSRP":
                verdict = "Held back by weak strength (RSRP)"
        vf = font(9)
        p.setPen(TEXT_DIM)
        p.setFont(vf)
        vw = QFontMetrics(vf).horizontalAdvance(verdict)
        p.drawText(QRectF(PAD + 1, y + 36, vw + 2, 18), LEFT, verdict)
        self._info(p, PAD + vw + 8, y + 38,
                   "Overall rating: the weaker of Signal strength (RSRP) and Signal vs. noise (SINR)\n"
                   "on the main link (5G when connected, otherwise 4G).\n\n"
                   "Bars on the right are the router's own 0–5 bars.")

        bars = s["bars"] if s else 0
        bx = w - PAD - 5 * 9 + 3
        for i in range(5):
            h = 9 + i * 6
            r = QRectF(bx + i * 9, y + 34 - h, 6, h)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(color if i < bars else QColor(255, 255, 255, 38))
            p.drawRoundedRect(r, 2, 2)
        p.setPen(TEXT_FAINT)
        p.setFont(font(7.5))
        p.drawText(QRectF(bx - 10, y + 38, 52, 14), CENTER, f"{bars} of 5 bars")
        return y + 56

    def _metric(self, p, y, metric, s):
        """One reading, 5G and 4G on their own rows so both are always visible."""
        meta = METRICS[metric]
        x0, x1 = PAD, self.width() - PAD
        tf = font(9.5, QFont.Weight.DemiBold)
        p.setPen(TEXT)
        p.setFont(tf)
        p.drawText(QRectF(x0, y, 160, 16), LEFT, meta["title"])
        cx = x0 + QFontMetrics(tf).horizontalAdvance(meta["title"]) + 6
        chip = QRectF(cx, y + 1, 34, 14)
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(QColor(255, 255, 255, 22))
        p.drawRoundedRect(chip, 4, 4)
        p.setPen(TEXT_DIM)
        p.setFont(font(7, QFont.Weight.DemiBold))
        p.drawText(chip, CENTER, metric)
        self._info(p, chip.right() + 6, y + 1, f"{meta['title']} ({metric})\n{meta['help']}\n\n{threshold_text(metric)}")
        y += 20

        for key, kind in (("nr", "5G"), ("lte", "4G")):
            link = s[key] if s else None
            present = bool(link and link["present"])
            value = link["values"][metric] if present else None
            rating = link["ratings"][metric] if present else None
            color = (self.tv(f"{key}_{metric}_c") or RATING_COLORS[rating]) if self.live else OFFLINE

            chip = QRectF(x0, y + 1, 24, 15)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(alpha(ACCENT, 70) if present else QColor(255, 255, 255, 18))
            p.drawRoundedRect(chip, 5, 5)
            p.setPen(TEXT if present else TEXT_FAINT)
            p.setFont(font(7, QFont.Weight.Bold))
            p.drawText(chip, CENTER, kind)

            if not present:
                p.setPen(TEXT_FAINT)
                p.setFont(font(8))
                p.drawText(QRectF(x0 + 32, y, 200, 17), LEFT,
                           f"{kind} not connected" if s else "waiting for data…")
                y += 21
                continue

            draw_track(p, QRectF(x0 + 32, y + 5, x1 - x0 - 32 - 122, 7), metric, self.tv(f"{key}_{metric}"),
                       rating, self.live, ticks=False, color=color)
            vf = font(9, QFont.Weight.Bold)
            txt = fmt(value, 0 if metric == "RSRP" else 1)
            vr = QRectF(x1 - 114, y, 54, 17)
            uw = QFontMetrics(font(7)).horizontalAdvance(meta["unit"])
            p.setPen(color)
            p.setFont(vf)
            p.drawText(vr.adjusted(0, 0, -uw - 3, 0), RIGHT, txt)
            p.setPen(TEXT_DIM)
            p.setFont(font(7))
            p.drawText(vr, RIGHT, meta["unit"])

            pill_text = rating or "No data"
            pf = font(7, QFont.Weight.DemiBold)
            pill = QRectF(x1 - 54, y + 1, 54, 15)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(alpha(color, 46))
            p.drawRoundedRect(pill, 7, 7)
            p.setPen(color)
            p.setFont(pf)
            p.drawText(pill, CENTER, pill_text)
            y += 21
        return y - 4

    def _spark(self, p, y):
        """History of the main link as two lanes with the rating zones behind each line."""
        w = self.width()
        now = time.time()
        box = QRectF(PAD, y, w - 2 * PAD, 112)
        draw_panel(p, box)
        span = HISTORY_S
        if self.history:
            span = min(HISTORY_S, max(20.0, now - self.history[0][0]))
        label = "LAST 5 MIN" if span >= HISTORY_S - 1 else f"LAST {int(span) // 60}M {int(span) % 60:02d}S" \
            if span >= 60 else f"LAST {int(span)}S"
        lf = font(7.5, QFont.Weight.DemiBold)
        p.setFont(lf)
        p.setPen(TEXT_DIM)
        p.drawText(QRectF(box.x() + 10, y + 5, 120, 14), LEFT, label)
        self._info(p, box.x() + 14 + QFontMetrics(lf).horizontalAdvance(label), y + 5,
                   "How the main link changed recently. Each lane is colored by zone:\n"
                   "red Poor, amber Fair, lime Good, green Excellent. Higher = better.\n"
                   "The dashed line is the best value seen. Move the router slowly and\n"
                   "watch the lines climb.")

        plot_l, plot_r = box.x() + 34, box.right() - 10
        lanes = [("RSRP", "Signal strength", 0), ("SINR", "Signal vs. noise", 1)]
        ly = y + 22
        for metric, title, idx in lanes:
            vals = [(t, smp[idx + 1]) for t, smp in ((smp[0], smp) for smp in self.history) if smp[idx + 1] is not None]
            cur = vals[-1][1] if vals else None
            shown = self.tv("spark_" + metric, now) if cur is not None else None
            rating = rate(metric, cur)
            p.setFont(font(7.5, QFont.Weight.DemiBold))
            p.setPen(TEXT)
            p.drawText(QRectF(box.x() + 10, ly, 150, 12), LEFT, title)
            if cur is not None:
                txt = f"{fmt(cur, 0 if metric == 'RSRP' else 1)} {METRICS[metric]['unit']} · {rating}"
                p.setPen(RATING_COLORS[rating] if self.live else OFFLINE)
                p.drawText(QRectF(box.right() - 160, ly, 150, 12), RIGHT, txt)
            plot = QRectF(plot_l, ly + 14, plot_r - plot_l, 24)

            lo, hi = METRICS[metric]["chart"]
            if vals:  # zoom to the data, but keep a sensible minimum range
                dmin, dmax = min(v for _, v in vals), max(v for _, v in vals)
                need = METRICS[metric]["min_span"]
                mid = (dmin + dmax) / 2
                half = max(need, dmax - dmin + need / 2) / 2
                lo, hi = max(lo, mid - half), min(hi, mid + half)

            def ypos(v, lo=lo, hi=hi, plot=plot):
                return plot.bottom() - (min(max(v, lo), hi) - lo) / (hi - lo) * plot.height()

            exc, good, fair = THRESHOLDS[metric]
            bounds = [(-1e9, fair, POOR), (fair, good, FAIR), (good, exc, GOOD), (exc, 1e9, EXCELLENT)]
            p.setPen(Qt.PenStyle.NoPen)
            for a, b, zone in bounds:
                if b <= lo or a >= hi:
                    continue
                top, bot = ypos(min(b, hi)), ypos(max(a, lo))
                p.setBrush(alpha(RATING_COLORS[zone], 34))
                p.drawRect(QRectF(plot.left(), top, plot.width(), bot - top))
            p.setFont(font(6))
            for b in (fair, good, exc):
                if lo < b < hi:
                    yy = ypos(b)
                    p.setPen(QPen(QColor(255, 255, 255, 30), 1, Qt.PenStyle.DotLine))
                    p.drawLine(QPointF(plot.left(), yy), QPointF(plot.right(), yy))
                    p.setPen(TEXT_FAINT)
                    p.drawText(QRectF(box.x() + 4, yy - 6, plot_l - box.x() - 7, 12), RIGHT, f"{b:g}")

            pts = []
            for i, (t, v) in enumerate(vals):
                if i == len(vals) - 1:
                    v = shown
                pt = QPointF(max(plot.right() - (now - t) / span * plot.width(), plot.left()), ypos(v))
                if pts and pt.x() - pts[-1][0].x() < 0.5:
                    pts.pop()  # readings at the same spot: keep the newest
                pts.append((pt, v))
            if vals:
                best = max(v for _, v in vals)
                p.setPen(QPen(QColor(255, 255, 255, 110), 1, Qt.PenStyle.DashLine))
                p.drawLine(QPointF(plot.left(), ypos(best)), QPointF(plot.right(), ypos(best)))
            for (a, va), (b, vb) in zip(pts, pts[1:]):
                seg = QPainterPath(a)
                mid = (a.x() + b.x()) / 2
                seg.cubicTo(QPointF(mid, a.y()), QPointF(mid, b.y()), b)
                c = RATING_COLORS[rate(metric, vb)] if self.live else OFFLINE
                p.setBrush(Qt.BrushStyle.NoBrush)
                p.setPen(QPen(c, 2, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
                p.drawPath(seg)
            if pts:
                tip, v = pts[-1]
                c = RATING_COLORS[rate(metric, v)] if self.live else OFFLINE
                g = QRadialGradient(tip, 8)
                g.setColorAt(0, alpha(c, 200))
                g.setColorAt(1, alpha(c, 0))
                p.setPen(Qt.PenStyle.NoPen)
                p.setBrush(g)
                p.drawEllipse(tip, 8, 8)
                p.setBrush(QColor("#ffffff"))
                p.drawEllipse(tip, 2.5, 2.5)
            ly += 42

        p.setFont(font(6.5))
        p.setPen(TEXT_FAINT)
        left = f"-{int(span) // 60}m" if span >= 60 else f"-{int(span)}s"
        if 60 <= span < HISTORY_S - 1 and int(span) % 60:
            left += f"{int(span) % 60:02d}s"
        p.drawText(QRectF(plot_l, ly - 2, 60, 10), AL.AlignLeft, left)
        p.drawText(QRectF(plot_r - 60, ly - 2, 60, 10), AL.AlignRight, "now")
        if len(self.history) < 2:
            p.setFont(font(7.5))
            p.drawText(QRectF(plot_l, y + 22, plot_r - plot_l, 84), CENTER, "Collecting…")
        return y + 112

    def _pingstrip(self, p, y):
        """Live internet ping (1.1.1.1 by default) with the last minute as tiny bars."""
        w = self.width()
        mon = self.win.pingmon
        samples = mon.snapshot() if mon else []
        now = time.time()
        box = QRectF(PAD, y, w - 2 * PAD, 24)
        self.buttons["ping"] = box
        hov = self.hover_btn == "ping"
        p.setPen(QPen(QColor(255, 255, 255, 40 if hov else 22), 1))
        p.setBrush(QColor(255, 255, 255, 16) if hov else QColor(0, 0, 0, 40))
        p.drawRoundedRect(box, 8, 8)

        last = samples[-1][1] if samples else None
        paused = mon is None or mon.paused.is_set()
        st = ping_stats(samples, now, 60)
        p.setPen(TEXT_FAINT)
        p.setFont(font(6.5, QFont.Weight.DemiBold))
        p.drawText(QRectF(box.x() + 9, y, 30, 24), LEFT, "PING")
        vf = font(9, QFont.Weight.Bold)
        if paused:
            text, c = "paused", TEXT_FAINT
        elif not samples:
            text, c = "…", TEXT_FAINT
        elif last is None:
            text, c = "timeout", RATING_COLORS[POOR]
        else:
            text, c = f"{last:.0f} ms", RATING_COLORS[ping_rating(last)]
        p.setPen(c)
        p.setFont(vf)
        p.drawText(QRectF(box.x() + 38, y, 58, 24), LEFT, text)

        loss = st["loss"]
        lt = "" if loss is None else f"{loss:.0f}% loss"
        p.setFont(font(7, QFont.Weight.DemiBold))
        p.setPen(RATING_COLORS[POOR] if loss else TEXT_FAINT)
        p.drawText(QRectF(box.right() - 58, y, 50, 24), RIGHT, lt)

        log = self.win.spike_log
        hour = [e for e in log if now - e["start"] <= 3600]
        badge_w = 0
        if hour:
            bf = font(7, QFont.Weight.Bold)
            label = f"⚡{len(hour)}"
            badge_w = QFontMetrics(bf).horizontalAdvance(label) + 10
            b = QRectF(box.right() - 58 - badge_w - 2, y + 5, badge_w, 14)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(alpha(RATING_COLORS[POOR], 60))
            p.drawRoundedRect(b, 7, 7)
            p.setPen(QColor("#fca5a5"))
            p.setFont(bf)
            p.drawText(b, CENTER, label)
            badge_w += 6

        bars = QRectF(box.x() + 98, y + 5, box.width() - 98 - 64 - badge_w, 14)
        recent = [(t, ms) for t, ms in samples if now - t <= 60]
        bw = bars.width() / 60
        for t, ms in recent:
            x = bars.right() - (now - t) / 60 * bars.width()
            if x < bars.left():
                continue
            if ms is None:
                p.setPen(Qt.PenStyle.NoPen)
                p.setBrush(alpha(RATING_COLORS[POOR], 150))
                p.drawRect(QRectF(x - bw + 0.5, bars.top(), max(1.0, bw - 1), bars.height()))
                continue
            h = max(2.0, min(1.0, math.log10(1 + ms) / math.log10(301)) * bars.height())
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(alpha(RATING_COLORS[ping_rating(ms)], 200))
            p.drawRoundedRect(QRectF(x - bw + 0.5, bars.bottom() - h, max(1.0, bw - 1), h), 0.8, 0.8)
        for e in log:  # red marker over each spike of the last minute
            if now - e["end"] > 60:
                continue
            x0 = max(bars.left(), bars.right() - (now - e["start"] + 1) / 60 * bars.width())
            x1 = bars.right() - (now - e["end"]) / 60 * bars.width()
            p.setPen(QPen(QColor("#f87171"), 2, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
            p.drawLine(QPointF(x0, bars.top() - 2), QPointF(max(x1, x0 + 1), bars.top() - 2))
        return y + 24

    def ping_details(self):
        mon = self.win.pingmon
        if mon is None:
            return "The ping monitor is off in demo mode."
        samples = mon.snapshot()
        now = time.time()
        one, five = ping_stats(samples, now, 60), ping_stats(samples, now, 300)

        def f(v, unit=" ms"):
            return "–" if v is None else f"{v:.0f}{unit}"
        rating = ping_rating(one["avg"])
        return (f"Ping to {mon.target}, once a second\n"
                f"{PING_PLAIN[rating]}\n\n"
                f"Last minute:  avg {f(one['avg'])} · min {f(one['min'])} · max {f(one['max'])}\n"
                f"              jitter {f(one['jitter'])} · loss {f(one['loss'], '%')}\n"
                f"Last 5 min:   avg {f(five['avg'])} · jitter {f(five['jitter'])} · loss {f(five['loss'], '%')}\n\n"
                "Ping = how long a tiny message takes to reach the internet and come back.\n"
                "Under 30 ms is great for gaming, under 60 is fine for calls, over 100 feels laggy.\n"
                "Jitter = how much it jumps around. Loss = replies that never came back.\n"
                "Red bars are lost replies. Change the target in the right-click menu.")

    def _footer(self, p, y, hint=""):
        w = self.width()
        p.setFont(font(7.5))
        if self.live and self.updated_at:
            ago = int(time.time() - self.updated_at)
            p.setPen(TEXT_FAINT)
            text = "Updated just now" if ago < 2 else f"Updated {ago}s ago"
        else:
            p.setPen(QColor("#fbbf24"))
            text = self.status or "Not connected"
        p.drawText(QRectF(PAD, y, 190, 16), LEFT, elide(text, font(7.5), 190))

        rf = font(7.5, QFont.Weight.DemiBold)
        label = f"⟳ every {self.win.rate}s"
        rw = QFontMetrics(rf).horizontalAdvance(label) + 16
        r = QRectF(w - PAD - rw, y, rw, 16)
        self.buttons["rate"] = r
        p.setPen(QPen(QColor(255, 255, 255, 45), 1))
        p.setBrush(QColor(255, 255, 255, 34 if self.hover_btn == "rate" else 16))
        p.drawRoundedRect(r, 8, 8)
        p.setPen(TEXT_DIM)
        p.setFont(rf)
        p.drawText(r, CENTER, label)
        if hint:
            p.setPen(TEXT_FAINT)
            p.setFont(font(7.5))
            p.drawText(QRectF(r.left() - 130, y, 124, 16), RIGHT, hint)

    # ----- mouse -----
    def _button_at(self, pos):
        for name, r in self.buttons.items():
            if r.contains(pos):
                return name
        return None

    def mouseMoveEvent(self, e):
        btn = self._button_at(e.position())
        if btn != self.hover_btn:
            self.hover_btn = btn
            self.update()
        self.setCursor(Qt.CursorShape.PointingHandCursor if btn else Qt.CursorShape.ArrowCursor)

    def leaveEvent(self, _):
        self.hover_btn = None
        self.update()

    def mousePressEvent(self, e):
        if e.button() != Qt.MouseButton.LeftButton:
            return
        btn = self._button_at(e.position())
        if btn is None:
            self.win.windowHandle().startSystemMove()
        else:
            self.on_button(btn)

    def on_button(self, btn):
        if btn == "close":
            self.win.hide_to_tray()
        elif btn.startswith("mode_"):
            self.win.set_mode(btn[5:])
        elif btn == "rate":
            self.win.cycle_rate()
        elif btn == "ping":
            self.win.show_spike_panel(self.mapToGlobal(self.buttons["ping"].topLeft().toPoint()))
        elif btn in self.infos:
            self.win.show_info(self.infos[btn], self.mapToGlobal(self.buttons[btn].bottomLeft().toPoint()))

    def contextMenuEvent(self, e):
        self.win.show_menu(e.globalPos())


# ---------------------------------------------------------------- simple view

TREND_TEXT = {"up": ("▲  Getting better", LIVE), "down": ("▼  Getting worse", QColor("#f87171")),
              "steady": ("●  Steady", TEXT_DIM), None: ("Watching for changes…", TEXT_FAINT)}


class SimpleView(SignalView):
    """One score out of 100 and plain words. No dB numbers."""

    MODE = "simple"

    def __init__(self, win):
        super().__init__(win)
        self.scores = collections.deque()  # (time, score) of the main link

    def _targets(self, s, now):
        super()._targets(s, now)
        for key in ("score", "nr_score", "lte_score"):
            self.tweens[key].set(s[key], now)
            self.tweens[key + "_c"].set(RATING_COLORS[score_rating(s[key])], now)

    def show_data(self, s, raw):
        now = time.time()
        self.scores.append((now, s["score"]))
        while self.scores and self.scores[0][0] < now - TREND_S:
            self.scores.popleft()
        super().show_data(s, raw)

    def preferred_height(self):
        return 330

    def paintEvent(self, _):
        p = self._begin()
        s = self.summary
        y = self._header(p, 14, s)
        y = self._score(p, y + 16, s)
        y = self._links(p, y + 16, s)
        y = self._pingstrip(p, y + 10)
        self._footer(p, y + 8)
        p.end()

    def _score(self, p, y, s):
        now = time.time()
        score = s["score"] if s else None
        rating = score_rating(score)
        shown = self.tv("score", now) if score is not None else None
        color = (self.tv("score_c", now) or RATING_COLORS[rating]) if self.live else OFFLINE

        ring = QRectF(PAD + 8, y + 8, 108, 108)
        c = ring.center()
        for i in range(11):  # faint ticks every 10 points
            ang = math.radians(225 - 27 * i)
            r0, r1 = 62, (69 if i % 5 == 0 else 66)
            p.setPen(QPen(QColor(255, 255, 255, 70 if i % 5 == 0 else 38), 1.2))
            p.drawLine(QPointF(c.x() + r0 * math.cos(ang), c.y() - r0 * math.sin(ang)),
                       QPointF(c.x() + r1 * math.cos(ang), c.y() - r1 * math.sin(ang)))
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.setPen(QPen(QColor(255, 255, 255, 26), 10, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
        p.drawArc(ring, 225 * 16, -270 * 16)
        if shown is not None and shown > 0:
            span = int(-270 * 16 * shown / 100)
            for width, a in ((22, 22), (16, 40)):  # soft outer glow
                p.setPen(QPen(alpha(color, a), width, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
                p.drawArc(ring, 225 * 16, span)
            grad = QConicalGradient(c, 225)
            grad.setColorAt(0.0, color.lighter(150))
            grad.setColorAt(0.75, alpha(color, 150))
            grad.setColorAt(1.0, color.lighter(150))
            p.setPen(QPen(QBrush(grad), 10, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
            p.drawArc(ring, 225 * 16, span)
            ang = math.radians(225 - 270 * shown / 100)  # bright tip
            tip = QPointF(c.x() + 54 * math.cos(ang), c.y() - 54 * math.sin(ang))
            g = QRadialGradient(tip, 10)
            g.setColorAt(0, QColor(255, 255, 255, 200))
            g.setColorAt(1, alpha(color, 0))
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(g)
            p.drawEllipse(tip, 10, 10)
        if self.live:
            self._ping(p, c, 30, 74, color)
        p.setPen(color if score is not None else TEXT_FAINT)
        p.setFont(font(30, QFont.Weight.Bold))
        p.drawText(ring.adjusted(0, -8, 0, -8), CENTER, "–" if shown is None else str(round(shown)))
        p.setPen(TEXT_FAINT)
        p.setFont(font(7.5, QFont.Weight.DemiBold))
        p.drawText(QRectF(ring.x(), ring.center().y() + 18, ring.width(), 14), CENTER, "out of 100")

        tx = ring.right() + 18
        tw = self.width() - PAD - tx
        p.setPen(color)
        p.setFont(font(20, QFont.Weight.Bold))
        p.drawText(QRectF(tx, y + 12, tw, 32), LEFT, rating or ("No signal" if s else "—"))
        # Wrap the sentence into at most two lines right of the ring, shrinking the font if needed.
        box = QRectF(tx, y + 46, tw, 38)
        flags = AL.AlignLeft | AL.AlignTop | Qt.TextFlag.TextWordWrap
        for size in (9.5, 9, 8.5, 8):
            sf = font(size)
            if QFontMetrics(sf).boundingRect(box.toRect(), flags, PLAIN[rating]).height() <= box.height():
                break
        p.setPen(TEXT)
        p.setFont(sf)
        p.drawText(box, flags, PLAIN[rating])
        text, tc = TREND_TEXT[trend(self.scores, time.time()) if s else None]
        if not self.live:
            tc = TEXT_FAINT
        tf = font(8.5, QFont.Weight.DemiBold)
        p.setPen(tc)
        p.setFont(tf)
        p.drawText(QRectF(tx, y + 88, tw, 16), LEFT, text)
        self._info(p, self.width() - PAD - 14, y + 2,
                   "Signal score: 0 to 100, higher is better.\n"
                   "80+ Excellent · 60+ Good · 40+ Fair · below 40 Poor.\n\n"
                   "It is the weaker of signal strength and interference on the link\n"
                   "you are using (5G when connected). The arrow shows the last 30 seconds.\n"
                   "Switch to Detailed for the actual numbers.")
        return y + 124

    def _links(self, p, y, s):
        x0, x1 = PAD, self.width() - PAD
        box = QRectF(x0, y, x1 - x0, 66)
        draw_panel(p, box)
        for i, (key, kind) in enumerate((("nr", "5G"), ("lte", "4G"))):
            ry = y + 10 + i * 26
            score = s[key + "_score"] if s else None
            rating = score_rating(score)
            color = (self.tv(key + "_score_c") or RATING_COLORS[rating]) if self.live else OFFLINE
            p.setPen(TEXT if score is not None else TEXT_FAINT)
            p.setFont(font(9, QFont.Weight.Bold))
            p.drawText(QRectF(x0 + 12, ry, 28, 18), LEFT, kind)
            bar = QRectF(x0 + 42, ry + 6, box.width() - 42 - 96, 7)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(QColor(255, 255, 255, 26))
            p.drawRoundedRect(bar, 3.5, 3.5)
            if score is not None:
                shown = self.tv(key + "_score")
                fill = QRectF(bar.x(), bar.y(), max(bar.height(), bar.width() * shown / 100), bar.height())
                g = QLinearGradient(fill.topLeft(), fill.topRight())
                g.setColorAt(0, alpha(color, 120))
                g.setColorAt(1, color.lighter(130))
                p.setBrush(g)
                p.drawRoundedRect(fill, 3.5, 3.5)
            p.setPen(color if score is not None else TEXT_FAINT)
            p.setFont(font(8.5, QFont.Weight.DemiBold))
            label = rating if score is not None else ("not connected" if s else "waiting…")
            p.drawText(QRectF(x1 - 92, ry, 80, 18), RIGHT, label)
        return y + 66


# ---------------------------------------------------------------- scanner view

class ScannerView(SignalView):
    """Scanner / Placement mode: serving cell, neighbors, SINR meter, best spot."""

    MODE = "scan"
    MAX_ROWS = 5

    def __init__(self, win, tone):
        super().__init__(win)
        self.scan = None
        self.best = BestTracker()
        self.tone = tone
        self.copied_until = 0

    def show_data(self, s, raw):
        self.scan = analyze(raw)
        self.best.update(self.scan)
        self.tweens["scan_sinr"].set((self.scan["nr"] or self.scan["lte"])["sinr"], time.time())
        active = self.scan["nr"] or self.scan["lte"]
        self.tone.hz = tone_hz(active["sinr"])
        super().show_data(s, raw)

    def set_offline(self, status):
        self.tone.hz = None
        super().set_offline(status)

    def preferred_height(self):
        return 532

    def _label(self, p, y, text, right_text, info):
        draw_section_label(p, PAD, y, text, right_text)
        self._info(p, PAD + QFontMetrics(font(7.5, QFont.Weight.DemiBold)).horizontalAdvance(text) + 6, y, info)

    def paintEvent(self, _):
        p = self._begin()
        y = self._header(p, 14, self.summary)
        y = self._serving(p, y + 12)
        y = self._neighbors(p, y + 12)
        y = self._placement(p, y + 12)
        y = self._pingstrip(p, y + 10)
        self._footer(p, y + 8, "Read-only")
        p.end()

    # ----- 1. serving cell -----
    def _serving(self, p, y):
        sc = self.scan
        op = ""
        if self.summary:
            op = " · ".join(x for x in (self.summary["operator"], self.summary["network"]) if x)
        self._label(p, y, "SERVING CELL", op,
                    "The cells your router is connected to now.\n"
                    "4G anchor: eNodeB = the tower site, cell = the sector on it\n"
                    "(decoded from CELL_ID: eNodeB = ECI >> 8, cell = ECI & 0xFF).\n"
                    "5G NR: in 5G NSA the 4G cell is the anchor and 5G adds speed.\n\n"
                    "Numbers are colored by rating: green Excellent, lime Good,\n"
                    "amber Fair, red Poor (thresholds in Detailed mode).")
        y += 16
        box = QRectF(PAD, y, W - 2 * PAD, 84)
        draw_panel(p, box)
        cols = [("RSRP", 224, 0), ("RSRQ", 266, 1), ("SINR", 306, 1)]
        p.setFont(font(7))
        p.setPen(TEXT_FAINT)
        for name, cx, _ in cols:
            p.drawText(QRectF(cx - 40, y + 4, 40, 12), RIGHT, name)
        rows = [sc["lte"] if sc else None, sc["nr"] if sc else None]
        for i, cell in enumerate(rows):
            ry = y + 18 + i * 32
            kind = "4G" if i == 0 else "5G"
            chip = QRectF(PAD + 8, ry + 4, 26, 18)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(alpha(ACCENT, 70) if cell else QColor(255, 255, 255, 20))
            p.drawRoundedRect(chip, 6, 6)
            p.setPen(TEXT if cell else TEXT_FAINT)
            p.setFont(font(8, QFont.Weight.Bold))
            p.drawText(chip, CENTER, kind)
            if not cell:
                p.setPen(TEXT_FAINT)
                p.setFont(font(8.5))
                p.drawText(QRectF(PAD + 42, ry, 160, 26), LEFT, "not connected" if sc else "waiting for data…")
                continue
            p.setPen(TEXT)
            p.setFont(font(8.5, QFont.Weight.DemiBold))
            line1 = " · ".join(x for x in (cell["band"], f"PCI {cell['pci']}" if cell["pci"] else "") if x)
            p.drawText(QRectF(PAD + 42, ry, 150, 15), LEFT, line1 or "—")
            p.setPen(TEXT_DIM)
            p.setFont(font(7.5))
            if kind == "4G":
                line2 = (f"eNodeB {cell['enb']} · cell {cell['cell']}" if cell["enb"] is not None
                         else "eNodeB unknown")
            else:
                line2 = "NR secondary cell"
            p.drawText(QRectF(PAD + 42, ry + 14, 150, 13), LEFT, line2)
            for name, cx, digits in cols:
                v = cell[name.lower()]
                c = RATING_COLORS[rate(name, v)] if self.live else OFFLINE
                p.setPen(c)
                p.setFont(font(9.5, QFont.Weight.Bold))
                p.drawText(QRectF(cx - 44, ry, 44, 26), RIGHT, fmt(v, digits))
        return y + 84

    # ----- 2. neighbors -----
    def _neighbors(self, p, y):
        sc = self.scan
        nbs = sc["neighbors"] if sc else []
        self._label(p, y, "NEIGHBOR CELLS", f"{len(nbs)} seen" if sc else "",
                    "Other cells the router hears. Green rows measure better than your\n"
                    "serving cell; ● marks the cell you are on. If one stays stronger, ask\n"
                    "Ooredoo to lock you to it (Copy for Ooredoo).\n\n"
                    "Some routers do not report neighbors; then this list stays empty.")
        y += 16
        box = QRectF(PAD, y, W - 2 * PAD, 16 + self.MAX_ROWS * 17 + 6)
        draw_panel(p, box)
        cols = [("Type", PAD + 10, LEFT, 30), ("EARFCN", PAD + 40, LEFT, 50), ("PCI", PAD + 96, LEFT, 40),
                ("RSRP", 224, RIGHT, 40), ("RSRQ", 266, RIGHT, 40), ("SINR", 306, RIGHT, 40)]
        p.setFont(font(7))
        p.setPen(TEXT_FAINT)
        for name, x, align, width in cols:
            r = QRectF(x - width, y + 4, width, 12) if align == RIGHT else QRectF(x, y + 4, width, 12)
            p.drawText(r, align, name)

        ordered = sorted(nbs, key=lambda n: (not n["stronger"], -(n["sinr"] if n["sinr"] is not None else -999)))
        for i, n in enumerate(ordered[:self.MAX_ROWS]):
            ry = y + 18 + i * 17
            row = QRectF(box.x() + 4, ry, box.width() - 8, 16)
            if n["stronger"]:
                p.setPen(Qt.PenStyle.NoPen)
                p.setBrush(alpha(STRONGER, 40))
                p.drawRoundedRect(row, 5, 5)
            base = STRONGER if n["stronger"] else (TEXT_DIM if n["is_serving"] else TEXT)
            values = [n["type"], n["earfcn"] or "–", n["pci"] + (" ●" if n["is_serving"] else ""),
                      fmt(n["rsrp"]), fmt(n["rsrq"], 1), fmt(n["sinr"], 1)]
            for (name, x, align, width), val in zip(cols, values):
                r = QRectF(x - width, ry, width, 16) if align == RIGHT else QRectF(x, ry, width + 10, 16)
                p.setPen(base)
                p.setFont(font(8, QFont.Weight.DemiBold if n["stronger"] else QFont.Weight.Normal))
                p.drawText(r, align, val)
        if not nbs:
            p.setPen(TEXT_FAINT)
            p.setFont(font(8))
            p.drawText(QRectF(box.x(), y + 18, box.width(), 17 * 3), CENTER,
                       "No neighbor cells reported" if sc else "Waiting for data…")
        elif len(nbs) > self.MAX_ROWS:
            p.setPen(TEXT_FAINT)
            p.setFont(font(7))
            p.drawText(QRectF(box.x(), box.bottom() - 12, box.width() - 10, 10), RIGHT,
                       f"+{len(nbs) - self.MAX_ROWS} more")
        y = box.bottom() + 5

        stronger = [n for n in ordered if n["stronger"]]
        p.setFont(font(8, QFont.Weight.DemiBold))
        if stronger:
            n = stronger[0]
            serving = sc["lte"] if n["type"] == "4G" else sc["nr"]
            p.setPen(STRONGER)
            p.drawText(QRectF(PAD + 2, y, W - 2 * PAD, 15), LEFT, elide(
                f"▲ PCI {n['pci']} looks stronger · SINR {fmt(n['sinr'], 1)} vs {fmt(serving['sinr'], 1)}"
                f" · RSRP {fmt(n['rsrp'])} vs {fmt(serving['rsrp'])}", font(8, QFont.Weight.DemiBold), W - 2 * PAD - 4))
        elif sc:
            p.setPen(TEXT_DIM)
            p.drawText(QRectF(PAD + 2, y, W - 2 * PAD, 15), LEFT, "Your serving cell is the strongest right now")
        y += 15
        b = self.best.best_neighbor
        p.setFont(font(7.5))
        p.setPen(TEXT_DIM)
        text = (f"★ Best: {b['type']} PCI {b['pci']} · EARFCN {b['earfcn'] or '?'} · "
                f"SINR {fmt(b['sinr'], 1)} · RSRP {fmt(b['rsrp'])} · {clock(b['at'])}") if b else \
            "★ Best neighbor this session: none yet"
        p.drawText(QRectF(PAD + 2, y, W - 2 * PAD, 14), LEFT, elide(text, font(7.5), W - 2 * PAD - 4))
        return y + 14

    # ----- 3. placement meter -----
    def _placement(self, p, y):
        sc = self.scan
        active = (sc["nr"] or sc["lte"]) if sc else None
        kind = active["type"] if active else ""
        self._label(p, y, "PLACEMENT METER", f"SINR on {kind} link" if active else "",
                    "Signal vs. noise on the active link. Move the router slowly and keep it\n"
                    "where this number is highest. Higher = faster.\n\n"
                    "Tone: beeps with a pitch that rises as SINR improves (300–900 Hz).\n"
                    "Copy for Ooredoo: a one-line summary of the best cell seen, to send when\n"
                    "asking them to lock your line to it. Nothing is changed on the router.\n\n"
                    + threshold_text("SINR"))
        y += 16
        box = QRectF(PAD, y, W - 2 * PAD, 128)
        draw_panel(p, box)

        sinr = active["sinr"] if active else None
        rating = rate("SINR", sinr)
        color = RATING_COLORS[rating] if self.live else OFFLINE
        p.setPen(color)
        p.setFont(font(34, QFont.Weight.Bold))
        big = fmt(self.tv("scan_sinr") if sinr is not None else None, 1)
        p.drawText(QRectF(PAD + 12, y + 4, 140, 50), LEFT, big)
        bw = QFontMetrics(font(34, QFont.Weight.Bold)).horizontalAdvance(big)
        p.setFont(font(9, QFont.Weight.DemiBold))
        p.drawText(QRectF(PAD + 16 + bw, y + 14, 70, 16), LEFT, "dB SINR")
        p.setPen(TEXT_DIM)
        p.setFont(font(8.5))
        p.drawText(QRectF(PAD + 16 + bw, y + 30, 90, 16), LEFT, rating or "no data")

        # buttons
        bx = box.right() - 120
        tone_on = self.tone.on.is_set()
        tone_rect = QRectF(bx, y + 10, 110, 26)
        copy_rect = QRectF(bx, y + 42, 110, 26)
        self.buttons["tone"] = tone_rect
        self.buttons["copy"] = copy_rect
        draw_button(p, tone_rect, "♪  Tone on" if tone_on else "♪  Tone off", self.hover_btn == "tone", tone_on)
        copied = time.time() < self.copied_until
        draw_button(p, copy_rect, "Copied ✓" if copied else "Copy for Ooredoo", self.hover_btn == "copy", copied)

        draw_track(p, QRectF(PAD + 12, y + 78, box.width() - 24, 7), "SINR", sinr, rating, self.live, ticks=False)

        p.setFont(font(7.5))
        bs, br = self.best.best_sinr, self.best.best_rsrp
        p.setPen(TEXT_DIM)
        p.drawText(QRectF(PAD + 12, y + 92, box.width() - 24, 14), LEFT,
                   f"★ Best SINR  {fmt(bs['sinr'], 1)} dB  (RSRP {fmt(bs['rsrp'])})  at {clock(bs['at'])}"
                   if bs else "★ Best SINR  –")
        p.drawText(QRectF(PAD + 12, y + 107, box.width() - 24, 14), LEFT,
                   f"★ Best RSRP  {fmt(br['rsrp'])} dBm  (SINR {fmt(br['sinr'], 1)})  at {clock(br['at'])}"
                   if br else "★ Best RSRP  –")
        return y + 128

    def on_button(self, btn):
        if btn == "tone":
            (self.tone.on.clear if self.tone.on.is_set() else self.tone.on.set)()
            self.update()
        elif btn == "copy":
            text = self.best.copy_text(self.scan)
            if text:
                QApplication.clipboard().setText(text)
                self.copied_until = time.time() + 2
                self.win.show_info("Copied:\n" + text, self.mapToGlobal(self.buttons["copy"].bottomLeft().toPoint()))
                self.update()
        else:
            super().on_button(btn)


# ---------------------------------------------------------------- speed test view

SPEED_TICKS = [0, 5, 10, 25, 50, 100, 250, 500]
PHASES = [("ping", "Ping"), ("download", "Download"), ("upload", "Upload")]
PHASE_COLORS = {"ping": QColor("#34d399"), "download": QColor("#60a5fa"), "upload": QColor("#c084fc")}
HISTORY_MAX = 5


def speed_frac(mbps):
    """Log-ish dial: 0-500 Mbps, small speeds still move the needle."""
    return min(1.0, math.log10(1 + max(0.0, mbps)) / math.log10(501))


class SpeedView(SignalView):
    """Internet speed test (Cloudflare servers, not the router) with a live speedometer."""

    MODE = "speed"

    def __init__(self, win):
        super().__init__(win)
        self.running = False
        self.cancel = None
        self.phase = None
        self.live = False
        self.current = {}       # ping / jitter / down / up of the test in progress or last test
        self.error = ""

    def preferred_height(self):
        return 542

    # ----- test control -----
    def start_test(self):
        if self.running:
            return
        self.running, self.phase, self.error, self.current = True, "ping", "", {}
        self.cancel = threading.Event()
        cancel, events = self.cancel, self.win.events
        self.tweens["speed"].set(0.0, time.time())

        def progress(phase, value, frac):
            events.put(("speed", ("progress", phase, value, frac)))

        def work():
            try:
                res = speedtest.run(progress, cancel)
                events.put(("speed", ("done", res)))
            except speedtest.SpeedTestError as e:
                events.put(("speed", ("error", str(e))))
            except Exception as e:  # keep the widget alive whatever happens
                events.put(("speed", ("error", f"Speed test failed ({e.__class__.__name__})")))

        threading.Thread(target=work, daemon=True).start()
        self.anim.start()
        self.update()

    def stop_test(self):
        if self.cancel:
            self.cancel.set()

    def on_event(self, ev):
        now = time.time()
        kind = ev[0]
        if kind == "progress":
            _, phase, value, frac = ev
            if self.cancel and self.cancel.is_set():
                return
            self.phase = phase
            if phase == "ping":
                self.current["ping"] = value
            else:
                self.current["down" if phase == "download" else "up"] = value
                self.tweens["speed"].set(value, now)
        elif kind == "done":
            self.running, self.phase = False, None
            res = ev[1]
            if res is None:
                self.error = "Cancelled"
            else:
                self.current = dict(res)
                s = self.win.last
                entry = {k: (round(v, 1) if isinstance(v, float) else v) for k, v in res.items()}
                entry["at"] = int(res["at"])
                if s:
                    entry.update(score=s["score"], rating=s["overall"], band=s["band"], network=s["network"])
                hist = [entry] + list(self.win.settings.get("speed_history", []))
                self.win.settings["speed_history"] = hist[:HISTORY_MAX]
                save_settings(self.win.settings)
            self.tweens["speed"].set(self.current.get("down", 0.0) or 0.0, now)
        elif kind == "error":
            self.running, self.phase = False, None
            self.error = ev[1]
            self.tweens["speed"].set(0.0, now)
        self.win._update_tray()
        self.update()

    def _animate(self):
        if not self.running and time.time() - self.pulse_at > PING_S:
            self.anim.stop()
        self.update()

    # ----- painting -----
    def paintEvent(self, _):
        p = self._begin()
        y = self._header(p, 14, self.summary)
        y = self._dial(p, y + 8)
        y = self._controls(p, y + 4)
        y = self._results(p, y + 12)
        y = self._history(p, y + 12)
        y = self._pingstrip(p, y + 10)
        self._footer(p, y + 8)
        p.end()

    def _dial(self, p, y):
        now = time.time()
        cx, cy, r = self.width() / 2, y + 86, 74
        phase = self.phase or ("download" if self.current.get("down") is not None else None)
        color = PHASE_COLORS.get(phase, ACCENT)
        rect = QRectF(cx - r, cy - r, 2 * r, 2 * r)
        start, sweep = 210, -240

        p.setBrush(Qt.BrushStyle.NoBrush)
        p.setPen(QPen(QColor(255, 255, 255, 24), 9, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
        p.drawArc(rect, start * 16, sweep * 16)
        p.setFont(font(6.5))
        for v in SPEED_TICKS:
            ang = math.radians(start + sweep * speed_frac(v))
            p.setPen(QPen(QColor(255, 255, 255, 60), 1.2))
            p.drawLine(QPointF(cx + (r + 7) * math.cos(ang), cy - (r + 7) * math.sin(ang)),
                       QPointF(cx + (r + 11) * math.cos(ang), cy - (r + 11) * math.sin(ang)))
            p.setPen(TEXT_FAINT)
            lr = r + 21
            p.drawText(QRectF(cx + lr * math.cos(ang) - 14, cy - lr * math.sin(ang) - 6, 28, 12), CENTER, str(v))

        value = self.tv("speed", now) or 0.0
        frac = speed_frac(value)
        if frac > 0.002:
            for width, a in ((20, 20), (14, 40)):
                p.setPen(QPen(alpha(color, a), width, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
                p.drawArc(rect, start * 16, int(sweep * 16 * frac))
            grad = QConicalGradient(QPointF(cx, cy), start)
            grad.setColorAt(0.0, color.lighter(150))
            grad.setColorAt(0.67, alpha(color, 140))
            grad.setColorAt(1.0, color.lighter(150))
            p.setPen(QPen(QBrush(grad), 9, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
            p.drawArc(rect, start * 16, int(sweep * 16 * frac))
        # needle
        ang = math.radians(start + sweep * frac)
        tip = QPointF(cx + (r - 14) * math.cos(ang), cy - (r - 14) * math.sin(ang))
        p.setPen(QPen(alpha(color, 70), 6, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
        p.drawLine(QPointF(cx, cy), tip)
        p.setPen(QPen(QColor(255, 255, 255, 230), 2, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
        p.drawLine(QPointF(cx, cy), tip)
        g = QRadialGradient(QPointF(cx, cy), 12)
        g.setColorAt(0, QColor(255, 255, 255, 220))
        g.setColorAt(0.4, alpha(color, 200))
        g.setColorAt(1, alpha(color, 0))
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(g)
        p.drawEllipse(QPointF(cx, cy), 12, 12)
        if self.running and MOTION["on"]:  # scanning ring while testing
            k = (now * 0.8) % 1.0
            p.setPen(QPen(alpha(color, int(120 * (1 - k))), 1.4))
            p.setBrush(Qt.BrushStyle.NoBrush)
            p.drawEllipse(QPointF(cx, cy), 14 + 46 * k, 14 + 46 * k)

        # number under the hub
        if self.phase == "ping":
            big, unit = fmt(self.current.get("ping")), "ms ping"
        elif self.running or self.current.get("down") is not None:
            big, unit = fmt(value, 1), "Mbps"
        else:
            big, unit = "–", "Mbps"
        p.setPen(TEXT)
        p.setFont(font(20, QFont.Weight.Bold))
        p.drawText(QRectF(cx - 80, cy + 18, 160, 30), CENTER, big)
        p.setPen(TEXT_DIM)
        p.setFont(font(7.5, QFont.Weight.DemiBold))
        p.drawText(QRectF(cx - 80, cy + 46, 160, 12), CENTER, unit)

        # phase steps
        sf = font(7.5, QFont.Weight.DemiBold)
        labels = [lab for _, lab in PHASES]
        widths = [QFontMetrics(sf).horizontalAdvance(lab) + 16 for lab in labels]
        x = cx - (sum(widths) + 8) / 2
        order = [k for k, _ in PHASES]
        done_upto = order.index(self.phase) if self.phase in order else (3 if self.current.get("up") is not None else -1)
        for (key, lab), w in zip(PHASES, widths):
            i = order.index(key)
            active = key == self.phase
            c = PHASE_COLORS[key]
            p.setPen(c if (active or i < done_upto) else TEXT_FAINT)
            p.setFont(sf)
            p.drawText(QRectF(x, cy + 64, w, 14), CENTER, ("● " if active else "") + lab)
            x += w + 4
        return cy + 80

    def _controls(self, p, y):
        w = self.width()
        b = QRectF(w / 2 - 70, y, 140, 28)
        self.buttons["speed_btn"] = b
        draw_button(p, b, "Cancel" if self.running else ("Run again" if self.current.get("up") else "Start test"),
                    self.hover_btn == "speed_btn", active=not self.running)
        self._info(p, b.right() + 8, y + 7,
                   "Speed test: measures your internet connection through the router.\n\n"
                   "Ping: how long a tiny message takes to reach the server and come back.\n"
                   "Lower is better; matters for games and calls.\n"
                   "Jitter: how much the ping jumps around. Lower is smoother.\n"
                   "Download: how fast data comes in (streaming, web pages).\n"
                   "Upload: how fast data goes out (video calls, sending files).\n\n"
                   "Uses Cloudflare's public speed test servers and up to about 200 MB of data.\n"
                   "Nothing is changed on the router. It only runs when you press Start.")
        p.setFont(font(7))
        p.setPen(QColor("#f87171") if self.error and self.error != "Cancelled" else TEXT_FAINT)
        note = self.error or "Uses up to ~200 MB of data"
        p.drawText(QRectF(PAD, y + 31, w - 2 * PAD, 12), CENTER, elide(note, font(7), w - 2 * PAD))
        return y + 44

    def _results(self, p, y):
        w = self.width()
        cur = self.current
        tiles = [("PING", cur.get("ping"), "ms", 0, ping_rating(cur.get("ping"))),
                 ("JITTER", cur.get("jitter"), "ms", 0, None),
                 ("DOWNLOAD", cur.get("down"), "Mbps", 1, None),
                 ("UPLOAD", cur.get("up"), "Mbps", 1, None)]
        gap = 6
        tw = (w - 2 * PAD - 3 * gap) / 4
        for i, (label, v, unit, digits, rating) in enumerate(tiles):
            r = QRectF(PAD + i * (tw + gap), y, tw, 48)
            draw_panel(p, r)
            p.setPen(TEXT_FAINT)
            p.setFont(font(6.5, QFont.Weight.DemiBold))
            p.drawText(QRectF(r.x(), r.y() + 5, r.width(), 10), CENTER, label)
            c = RATING_COLORS[rating] if rating else (PHASE_COLORS["download"] if label == "DOWNLOAD" else
                                                       PHASE_COLORS["upload"] if label == "UPLOAD" else TEXT)
            p.setPen(c if v is not None else TEXT_FAINT)
            p.setFont(font(12, QFont.Weight.Bold))
            p.drawText(QRectF(r.x(), r.y() + 15, r.width(), 20), CENTER, fmt(v, digits))
            p.setPen(TEXT_DIM)
            p.setFont(font(6.5))
            p.drawText(QRectF(r.x(), r.y() + 34, r.width(), 10), CENTER, unit)
        y += 52
        vf = font(8, QFont.Weight.DemiBold)
        if cur.get("up") is not None:
            text = speedtest.speed_verdict(cur.get("down"))
            with_ping = f"{text} · ping {speedtest.ping_verdict(cur.get('ping'))}"
            if QFontMetrics(vf).horizontalAdvance(with_ping) <= w - 2 * PAD:
                text = with_ping
        elif self.running:
            text = {"ping": "Measuring ping…", "download": "Measuring download…",
                    "upload": "Measuring upload…"}.get(self.phase, "Starting…")
        else:
            text = "Press Start to measure your internet speed"
        p.setPen(TEXT if cur.get("up") is not None else TEXT_DIM)
        p.setFont(vf)
        p.drawText(QRectF(PAD, y, w - 2 * PAD, 16), CENTER, elide(text, vf, w - 2 * PAD))
        return y + 16

    def _history(self, p, y):
        w = self.width()
        hist = self.win.settings.get("speed_history", [])
        label = "LAST RESULTS"
        draw_section_label(p, PAD, y, label)
        self._info(p, PAD + QFontMetrics(font(7.5, QFont.Weight.DemiBold)).horizontalAdvance(label) + 6, y,
                   "Your last 5 speed tests, newest first, with the signal score at that moment.\n"
                   "Run a test after each router move to see which spot is faster.")
        y += 16
        box = QRectF(PAD, y, w - 2 * PAD, 14 + HISTORY_MAX * 16)
        draw_panel(p, box)
        cols = [("Time", PAD + 8, LEFT, 40), ("Down", PAD + 92, RIGHT, 44), ("Up", PAD + 140, RIGHT, 44),
                ("Ping", PAD + 184, RIGHT, 40), ("Signal", w - PAD - 8, RIGHT, 100)]
        p.setFont(font(6.5))
        p.setPen(TEXT_FAINT)
        for name, x, align, cw in cols:
            p.drawText(QRectF(x - cw, y + 3, cw, 10) if align == RIGHT else QRectF(x, y + 3, cw, 10), align, name)
        if not hist:
            p.setPen(TEXT_FAINT)
            p.setFont(font(8))
            p.drawText(box.adjusted(0, 12, 0, 0), CENTER, "No tests yet")
        for i, h in enumerate(hist[:HISTORY_MAX]):
            ry = y + 15 + i * 16
            sig = ""
            if h.get("rating"):
                sig = f"{h['rating']} {h.get('score', '')}" + (f" · {h['band']}" if h.get("band") else "")
            vals = [time.strftime("%H:%M", time.localtime(h["at"])), fmt(h.get("down"), 1), fmt(h.get("up"), 1),
                    fmt(h.get("ping")) + " ms", sig or "–"]
            colors = [TEXT_DIM, PHASE_COLORS["download"], PHASE_COLORS["upload"],
                      RATING_COLORS[ping_rating(h.get("ping"))], RATING_COLORS.get(h.get("rating"), TEXT_DIM)]
            for (name, x, align, cw), v, c in zip(cols, vals, colors):
                p.setPen(c if i == 0 else alpha(c, 170))
                p.setFont(font(7.5, QFont.Weight.DemiBold if i == 0 else QFont.Weight.Normal))
                r = QRectF(x - cw, ry, cw, 15) if align == RIGHT else QRectF(x, ry, cw + 10, 15)
                p.drawText(r, align, v)
        return box.bottom()

    def on_button(self, btn):
        if btn == "speed_btn":
            (self.stop_test if self.running else self.start_test)()
        else:
            super().on_button(btn)


# ---------------------------------------------------------------- login view

LOGIN_QSS = """
QLabel#title { color: rgba(255,255,255,235); font-size: 15pt; font-weight: 600; }
QLabel#sub { color: rgba(255,255,255,140); font-size: 8.5pt; }
QLabel#msg { font-size: 8.5pt; }
QLineEdit {
    background: rgba(255,255,255,18); border: 1px solid rgba(255,255,255,40);
    border-radius: 10px; padding: 8px 12px; color: white; font-size: 11pt;
    selection-background-color: #3b82f6;
}
QLineEdit:focus { border: 1px solid rgba(96,165,250,200); background: rgba(255,255,255,26); }
QCheckBox { color: rgba(255,255,255,170); font-size: 8.5pt; spacing: 6px; }
QCheckBox::indicator { width: 14px; height: 14px; border-radius: 4px;
    border: 1px solid rgba(255,255,255,90); background: rgba(255,255,255,15); }
QCheckBox::indicator:checked { background: #3b82f6; border: 1px solid #60a5fa; }
QPushButton#connect {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #60a5fa, stop:1 #3b82f6);
    color: white; border: none; border-radius: 10px; padding: 8px 18px; font-weight: 600; font-size: 9.5pt;
}
QPushButton#connect:hover { background: #60a5fa; }
QPushButton#connect:disabled { background: rgba(255,255,255,40); color: rgba(255,255,255,140); }
QPushButton#close { background: transparent; color: rgba(255,255,255,150); border: none; font-size: 10pt;
    border-radius: 6px; padding: 2px 6px; }
QPushButton#close:hover { background: rgba(255,255,255,30); }
"""


class LoginView(QWidget):
    def __init__(self, win):
        super().__init__()
        self.win = win
        self.setStyleSheet(LOGIN_QSS)
        lay = QVBoxLayout(self)
        lay.setContentsMargins(20, 14, 14, 18)
        lay.setSpacing(6)

        top = QHBoxLayout()
        title = QLabel("Router login", objectName="title")
        close = QPushButton("✕", objectName="close", cursor=Qt.CursorShape.PointingHandCursor)
        close.clicked.connect(win.hide_to_tray)
        top.addWidget(title)
        top.addStretch()
        top.addWidget(close)
        lay.addLayout(top)
        lay.addWidget(QLabel(f"Enter the admin password of your router ({ROUTER_HOST}).\n"
                             "You only need to do this once.", objectName="sub"))
        lay.addSpacing(6)

        self.pw = QLineEdit(echoMode=QLineEdit.EchoMode.Password, placeholderText="Router password")
        self.pw.returnPressed.connect(self.submit)
        lay.addWidget(self.pw)

        row = QHBoxLayout()
        self.remember = QCheckBox("Remember on this PC", checked=True)
        self.btn = QPushButton("Connect", objectName="connect", cursor=Qt.CursorShape.PointingHandCursor)
        self.btn.clicked.connect(self.submit)
        row.addWidget(self.remember)
        row.addStretch()
        row.addWidget(self.btn)
        lay.addLayout(row)

        self.msg = QLabel("", objectName="msg", wordWrap=True)
        lay.addWidget(self.msg)
        lay.addStretch()

    def reset(self, message=""):
        self.pw.clear()
        self.btn.setEnabled(True)
        self.set_msg(message, error=True)
        QTimer.singleShot(50, self.pw.setFocus)

    def set_msg(self, text, error=False):
        self.msg.setStyleSheet(f"color: {'#f87171' if error else 'rgba(255,255,255,150)'};")
        self.msg.setText(text)

    def submit(self):
        pw = self.pw.text()
        if not pw:
            self.set_msg("Type the password first.", error=True)
            return
        self.btn.setEnabled(False)
        self.set_msg("Connecting…")
        remember = self.remember.isChecked()
        events = self.win.events

        def attempt():
            client = RouterClient(pw, host=ROUTER_HOST)
            try:
                client.login()
                events.put(("login_ok", (client, pw, remember)))
            except AuthError as e:
                if e.already_logged_in:
                    # Password could not be checked yet; the poller keeps retrying.
                    events.put(("login_ok", (client, pw, remember)))
                else:
                    events.put(("login_err", "Wrong password. Try again."))
            except RouterError:
                events.put(("login_err", f"Can't reach the router at {ROUTER_HOST}. "
                                         "Is this PC connected to its Wi-Fi or cable?"))

        threading.Thread(target=attempt, daemon=True).start()

    def mousePressEvent(self, e):
        if e.button() == Qt.MouseButton.LeftButton:
            self.win.windowHandle().startSystemMove()


def local_ip(router_ip):
    """This PC's address on the router's network (no packets are sent)."""
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sk:
            sk.connect((router_ip, 80))
            return sk.getsockname()[0]
    except OSError:
        return None


# ---------------------------------------------------------------- start with Windows

RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"


def launch_command():
    """Command line for the per-user Run key: pythonw (no console) + widget.py, or the frozen .exe."""
    if getattr(sys, "frozen", False):
        return f'"{sys.executable}" --autostart'
    exe = sys.executable
    pyw = os.path.join(os.path.dirname(exe), "pythonw.exe")
    if os.path.exists(pyw):
        exe = pyw
    return f'"{exe}" "{os.path.abspath(__file__)}" --autostart'


def autostart_enabled():
    if sys.platform != "win32":
        return False
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as k:
            winreg.QueryValueEx(k, APP_NAME)
            return True
    except OSError:
        return False


def set_autostart(on):
    """Per-user Run key, no admin rights needed. Returns the real state afterwards."""
    if sys.platform != "win32":
        return False
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as k:
            if on:
                winreg.SetValueEx(k, APP_NAME, 0, winreg.REG_SZ, launch_command())
            else:
                try:
                    winreg.DeleteValue(k, APP_NAME)
                except FileNotFoundError:
                    pass
    except OSError:
        pass
    return autostart_enabled()


# ---------------------------------------------------------------- window

APP_QSS = """
QMenu { background: rgba(28,32,42,245); border: 1px solid rgba(255,255,255,40); border-radius: 10px;
        padding: 6px; color: rgba(255,255,255,225); }
QMenu::item { padding: 6px 18px 6px 10px; border-radius: 6px; }
QMenu::item:selected { background: rgba(255,255,255,30); }
QMenu::item:disabled { color: rgba(255,255,255,90); }
QMenu::separator { height: 1px; background: rgba(255,255,255,30); margin: 4px 8px; }
"""


class InfoPopup(QWidget):
    """Help panel opened by the ⓘ icons. Paints its own dark glass, so it never shows through.
    Closes on any click, Esc, or a second click on the same ⓘ."""

    SHADOW = 8
    MAX_W = 300

    def __init__(self):
        super().__init__(None, Qt.WindowType.Popup | Qt.WindowType.FramelessWindowHint
                         | Qt.WindowType.NoDropShadowWindowHint)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.label = QLabel(self, wordWrap=True)
        self.label.setStyleSheet("color: rgba(255,255,255,232); background: transparent;")
        self.label.setFont(font(8.5))
        self.label.setTextInteractionFlags(Qt.TextInteractionFlag.LinksAccessibleByMouse)
        self.label.linkActivated.connect(self._link)
        self.on_link = None
        self.key = None
        self.closed_key, self.closed_at = None, 0.0

    def _link(self, href):
        callback = self.on_link
        self.close()
        if callback:
            callback(href)

    def open(self, text, pos, near, on_link=None, key=None):
        """Plain help text, or rich text (starts with "<") whose links call on_link(href)."""
        key = key or text
        # A click on the ⓘ that is showing closes the popup first; don't reopen it.
        if key == self.closed_key and time.time() - self.closed_at < 0.35:
            self.closed_key = None
            return
        self.key, self.on_link = key, on_link
        m = self.SHADOW + 12
        fm = QFontMetrics(self.label.font())
        if text.lstrip().startswith("<"):
            self.label.setTextFormat(Qt.TextFormat.RichText)
            self.label.setText(text)
            lw = self.MAX_W
        else:
            self.label.setTextFormat(Qt.TextFormat.PlainText)
            # Help strings are hard-wrapped for width; rejoin lines that break mid-sentence and let the label wrap.
            text = re.sub(r"(?<=[a-z,)])\n(?=[a-z(])", " ", text)
            self.label.setText(text)
            natural = max(fm.horizontalAdvance(line) for line in text.splitlines() or [""])
            lw = min(natural + 2, self.MAX_W)
        lh = self.label.heightForWidth(lw) if self.label.wordWrap() else fm.height()
        self.label.setGeometry(m, m - 2, lw, lh)
        self.setFixedSize(lw + 2 * m, lh + 2 * m - 4)

        screen = QApplication.screenAt(pos) or QApplication.primaryScreen()
        g = screen.availableGeometry()
        # Keep it over the widget when it fits, else anywhere on the screen.
        left = max(g.left(), near.left()) if self.width() <= near.width() else g.left()
        right = min(g.right(), near.right()) if self.width() <= near.width() else g.right()
        x = min(max(pos.x() - self.width() // 2, left), right - self.width())
        y = pos.y() - self.SHADOW + 2
        if y + self.height() > g.bottom():
            y = pos.y() - self.height() - 14
        self.move(x, max(y, g.top()))
        self.show()
        self.raise_()

    def keyPressEvent(self, e):
        if e.key() == Qt.Key.Key_Escape:
            self.close()

    def mousePressEvent(self, e):
        self.close()  # click inside closes too; outside clicks close it via Qt.Popup

    def hideEvent(self, e):
        super().hideEvent(e)
        self.closed_key, self.closed_at = self.key, time.time()

    def paintEvent(self, _):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        body = QRectF(self.rect()).adjusted(self.SHADOW, self.SHADOW - 2, -self.SHADOW, -self.SHADOW - 2)
        for i in range(self.SHADOW, 0, -1):  # soft shadow
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(QColor(0, 0, 0, int(26 * (1 - i / self.SHADOW)) + 4))
            p.drawRoundedRect(body.adjusted(-i, -i + 3, i, i + 3), 12 + i, 12 + i)
        g = QLinearGradient(body.topLeft(), body.bottomLeft())
        g.setColorAt(0, QColor(34, 40, 54, 248))
        g.setColorAt(1, QColor(18, 21, 30, 250))
        p.setBrush(g)
        p.setPen(QPen(QColor(255, 255, 255, 55), 1))
        p.drawRoundedRect(body, 11, 11)
        edge = QLinearGradient(body.topLeft(), body.topRight())
        edge.setColorAt(0, alpha(ACCENT, 0))
        edge.setColorAt(0.5, alpha(ACCENT, 160))
        edge.setColorAt(1, alpha(ACCENT, 0))
        p.setPen(QPen(QBrush(edge), 1.2))
        p.drawLine(QPointF(body.left() + 12, body.top() + 0.6), QPointF(body.right() - 12, body.top() + 0.6))
        p.end()


def tray_pixmap(rating, live):
    """Four signal bars; as many lit as the rating deserves, in its color."""
    pm = QPixmap(32, 32)
    pm.fill(Qt.GlobalColor.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    lit = {EXCELLENT: 4, GOOD: 3, FAIR: 2, POOR: 1}.get(rating, 0) if live else 0
    color = RATING_COLORS[rating] if live else OFFLINE
    for i in range(4):
        h = 8 + i * 7
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(color if i < lit else QColor(160, 160, 170, 110))
        p.drawRoundedRect(QRectF(2 + i * 7.5, 30 - h, 5.5, h), 1.5, 1.5)
    p.end()
    return pm


class GlassWindow(QWidget):
    def __init__(self, mock=False, autostart=False):
        super().__init__(None, Qt.WindowType.FramelessWindowHint | Qt.WindowType.WindowStaysOnTopHint
                         | Qt.WindowType.Tool)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setWindowTitle(APP_TITLE)
        self.settings = load_settings()
        MOTION["on"] = not self.settings.get("reduce_motion", False)
        self.rate = self.settings.get("rate") if self.settings.get("rate") in RATES else DEFAULT_RATE
        self.events = queue.Queue()
        self.poller = Poller(self.events)
        self.poller.interval = self.rate
        self.tone = Tone()
        self.blur = False
        self.last = None  # last summary, for the tray

        self.stack = QStackedLayout(self)
        self.stack.setContentsMargins(0, 0, 0, 0)
        self.login = LoginView(self)
        self.simple = SimpleView(self)
        self.signal = SignalView(self)
        self.scanner = ScannerView(self, self.tone)
        self.speed = SpeedView(self)
        self.views = {"simple": self.simple, "detailed": self.signal, "scan": self.scanner, "speed": self.speed}
        self.pingmon = None if mock else PingMonitor(self.settings.get("ping_target") or DEFAULT_TARGET)
        self.evidence = spikes.Evidence()
        self.sampler = spikes.Sampler(self.evidence)
        self.detector = spikes.SpikeDetector()
        self.spike_log = []          # finished spikes, oldest first (this session)
        self._ping_seen = 0.0
        self._router_devices_at = 0.0
        self.devices = devices.DeviceTracker(self.settings.setdefault("devices", {}), router_ip=ROUTER_HOST,
                                             own_ip=local_ip(ROUTER_HOST))
        mode = OLD_MODES.get(self.settings.get("mode"), self.settings.get("mode"))
        self.mode = mode if mode in self.views else "simple"
        for v in (self.login, self.simple, self.signal, self.scanner, self.speed):
            self.stack.addWidget(v)

        self.info = InfoPopup()

        self._build_menu()
        self.tray = QSystemTrayIcon(self)
        self.tray.setContextMenu(self.menu)
        self.tray.activated.connect(self._tray_clicked)
        self._update_tray()
        self.tray.show()

        self.save_timer = QTimer(self, singleShot=True, interval=400, timeout=self._save_pos)
        self.drain_timer = QTimer(self, interval=200, timeout=self._drain)
        self.tray_timer = QTimer(self, interval=1000, timeout=self._each_second)
        self.tray_timer.start()
        self.bg_timer = QTimer(self, interval=80, timeout=self.update)  # slow background drift, ~12 fps

        password = None if mock else (os.environ.get("ROUTER_PASSWORD") or credentials.load_password())
        if mock:
            self.poller.set_client(MockClient())
            self.show_data_view()
        elif password:
            self.poller.set_client(RouterClient(password, host=ROUTER_HOST,
                                                session_id=credentials.load_session() or ""))
            self.show_data_view()
        else:
            self.show_login()

        # Started with Windows: stay in the tray if asked to, or if it was hidden when last closed.
        self.start_hidden = bool(password) and autostart and (
            self.settings.get("start_hidden") or self.settings.get("visible") is False)

        self.poller.start()
        if self.pingmon:
            self.pingmon.start()
            self.sampler.start()
            threading.Thread(target=self._device_loop, daemon=True).start()
        self.tone.start()
        self.drain_timer.start()

    def _build_menu(self):
        self.menu = QMenu()
        self.menu.setWindowFlags(self.menu.windowFlags() | Qt.WindowType.FramelessWindowHint
                                 | Qt.WindowType.NoDropShadowWindowHint)
        self.menu.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.act_show = self.menu.addAction("Hide", self.toggle_visible)
        self.menu.addAction("Run speed test", self.run_speed_test)
        self.menu.addAction("Ping spikes…", lambda: self.show_spike_panel(None))
        self.menu.addAction("Devices on my network…", lambda: self.show_devices_panel(None))
        self.menu.addSeparator()

        mode_menu = self.menu.addMenu("Mode")
        group = QActionGroup(self)
        self.mode_actions = {}
        for mode, label in MODES:
            a = mode_menu.addAction(label, lambda m=mode: self.set_mode(m))
            a.setCheckable(True)
            group.addAction(a)
            self.mode_actions[mode] = a

        rate_menu = self.menu.addMenu("Update every")
        group = QActionGroup(self)
        self.rate_actions = {}
        for r in RATES:
            a = rate_menu.addAction(f"{r} second{'s' if r > 1 else ''}", lambda r=r: self.set_rate(r))
            a.setCheckable(True)
            a.setChecked(r == self.rate)
            group.addAction(a)
            self.rate_actions[r] = a

        ping_menu = self.menu.addMenu("Ping target")
        group = QActionGroup(self)
        self.ping_actions = {}
        current = self.settings.get("ping_target") or DEFAULT_TARGET
        for t in TARGETS + ["custom"]:
            a = ping_menu.addAction("Custom…" if t == "custom" else t, lambda t=t: self.set_ping_target(t))
            a.setCheckable(True)
            group.addAction(a)
            self.ping_actions[t] = a
        self.ping_actions[current if current in TARGETS else "custom"].setChecked(True)

        self.menu.addSeparator()
        self.act_autostart = self.menu.addAction("Start with Windows", self.toggle_autostart)
        self.act_autostart.setCheckable(True)
        self.act_autostart.setChecked(autostart_enabled())
        self.act_autostart.setEnabled(sys.platform == "win32")
        self.act_hidden = self.menu.addAction("Start hidden in the tray", self.toggle_start_hidden)
        self.act_hidden.setCheckable(True)
        self.act_hidden.setChecked(bool(self.settings.get("start_hidden")))
        self.act_notify = self.menu.addAction("Notify me about big ping spikes", self.toggle_notify)
        self.act_notify.setCheckable(True)
        self.act_notify.setChecked(bool(self.settings.get("spike_notify")))
        self.act_motion = self.menu.addAction("Reduce motion", self.toggle_motion)
        self.act_motion.setCheckable(True)
        self.act_motion.setChecked(not MOTION["on"])
        self.menu.addSeparator()
        self.act_pause = self.menu.addAction("Pause", self.toggle_pause)
        self.menu.addAction("Change password…", self.change_password)
        self.menu.addSeparator()
        self.menu.addAction("Exit", self.quit)

    def showEvent(self, e):
        super().showEvent(e)
        if not self.blur:
            self.blur = enable_windows_blur(self)
            self.update()
        if MOTION["on"]:
            self.bg_timer.start()
        self.act_show.setText("Hide")

    def hideEvent(self, e):
        super().hideEvent(e)
        self.bg_timer.stop()
        self.act_show.setText("Show")

    # ----- screens -----
    def data_view(self):
        return self.views[self.mode]

    def on_data_view(self):
        return self.stack.currentWidget() in self.views.values()

    def show_login(self, message=""):
        self.tone.on.clear()
        self.stack.setCurrentWidget(self.login)
        self.setFixedSize(W, 206)
        self._place(center=True)
        self.login.reset(message)
        self.show_window()

    def show_data_view(self):
        view = self.data_view()
        self.stack.setCurrentWidget(view)
        self.setFixedSize(W, view.preferred_height())
        self.mode_actions[self.mode].setChecked(True)
        if self.mode != "scan":
            self.tone.on.clear()
        self._place()

    def set_mode(self, mode):
        self.mode = mode
        self.settings["mode"] = mode
        save_settings(self.settings)
        self.mode_actions[mode].setChecked(True)
        if self.on_data_view():
            self.show_data_view()

    def set_rate(self, r):
        self.rate = r
        self.settings["rate"] = r
        save_settings(self.settings)
        self.rate_actions[r].setChecked(True)
        self.poller.set_interval(r)  # wakes the poller, so it applies right away
        for v in self.views.values():
            v.update()

    def run_speed_test(self):
        if not self.isVisible():
            self.show_window()
        self.set_mode("speed")
        self.speed.start_test()

    def set_ping_target(self, target):
        if target == "custom":
            current = self.settings.get("ping_target") or DEFAULT_TARGET
            text, ok = QInputDialog.getText(self, "Ping target", "Host name or IP address to ping:", text=current)
            target = text.strip()
            if not ok or not re.fullmatch(r"[A-Za-z0-9.:-]{1,253}", target):
                self.ping_actions[current if current in TARGETS else "custom"].setChecked(True)
                return
        self.settings["ping_target"] = target
        save_settings(self.settings)
        self.ping_actions[target if target in TARGETS else "custom"].setChecked(True)
        if self.pingmon:
            self.pingmon.set_target(target)

    def cycle_rate(self):
        self.set_rate(RATES[(RATES.index(self.rate) + 1) % len(RATES)])

    def show_info(self, text, pos, on_link=None, key=None):
        if pos is None:  # opened from the menu: over the widget
            if not self.isVisible():
                self.show_window()
            pos = self.mapToGlobal(QPoint(PAD, 40))
        self.info.open(text, pos, self.frameGeometry(), on_link, key)

    # ----- spike detective -----
    def _each_second(self):
        self._check_spikes()
        self._update_tray()

    def _check_spikes(self):
        if not self.pingmon:
            return
        for t, ms in self.pingmon.snapshot():
            if t <= self._ping_seen:
                continue
            self._ping_seen = t
            ev = self.detector.update(t, ms)
            if ev:
                self._spike_found(ev)

    def _spike_found(self, ev):
        ev.update(spikes.classify(ev, self.evidence))
        if any(c["kind"] == "other_device" for c in ev["causes"]):
            self.fetch_router_devices()  # fresh names for next time; the spike itself just ended
            online = self.devices.online(ev["start"])
            ev["devices"] = sorted(self.devices.label(m, d) for m, d in online.items()
                                   if d["ip"] not in (ROUTER_HOST, self.devices.own_ip))
        self.spike_log.append(ev)
        del self.spike_log[:-50]
        if self.settings.get("spike_notify") and spikes.is_big(ev) and QSystemTrayIcon.isSystemTrayAvailable():
            top = ev["causes"][0]
            peak = f"{ev['peak']:.0f} ms" if ev["peak"] else "lost replies"
            self.tray.showMessage(f"Ping spike: {peak}", f"{top['confidence'].capitalize()}: {top['name']}. {top['tip']}",
                                  QIcon(tray_pixmap(POOR, True)), 6000)

    def _spike_line(self, ev):
        when = time.strftime("%H:%M:%S", time.localtime(ev["start"]))
        peak = f"{ev['peak']:.0f} ms" if ev["peak"] else "timeout"
        dur = max(1, round(ev["end"] - ev["start"] + 1))
        return when, peak, dur

    def spike_html(self):
        esc = html.escape
        dim, red = "color:rgba(255,255,255,150)", "color:#fca5a5"
        mon = self.pingmon
        now = time.time()
        parts = []
        if mon:
            st = ping_stats(mon.snapshot(), now, 60)
            last = mon.snapshot()[-1][1] if mon.snapshot() else None
            cur = "–" if last is None else f"{last:.0f} ms"
            avg = "–" if st["avg"] is None else f"{st['avg']:.0f} ms"
            loss = "–" if st["loss"] is None else f"{st['loss']:.0f}%"
            parts.append(f"<b>Ping to {esc(mon.target)}</b> &nbsp;now {cur} · 1-min avg {avg} · loss {loss}<br>"
                         f"<span style='{dim}'>{esc(PING_PLAIN[ping_rating(st['avg'])])}</span>")
        if not self.sampler.available():
            parts.append(f"<span style='{red}'>App detection is off: the psutil package is missing. "
                         "Run Start.bat once to install it.</span>")
        log = self.spike_log
        if not log:
            base = self.detector.threshold()
            parts.append("<b>No ping spikes yet</b><br>"
                         f"<span style='{dim}'>A spike is when the ping jumps to 2.5× your usual "
                         f"(at least +40 ms) or replies are lost"
                         + (f": right now anything over {base:.0f} ms" if base else "") +
                         ". When one happens, this shows which app, Windows service or other cause was "
                         "busy at that moment.</span>")
        else:
            ev = log[-1]
            when, peak, dur = self._spike_line(ev)
            pc = f"this PC ↓ {ev['pc_down']:.1f} · ↑ {ev['pc_up']:.1f} Mbps" if ev.get("has_pc_data") else ""
            parts.append(f"<b>Latest spike</b> &nbsp;{when} · <span style='{red}'>{peak}</span> · {dur}s"
                         + (f"<br><span style='{dim}'>{pc}</span>" if pc else ""))
            rows = []
            for c in ev["causes"]:
                extra = ""
                if c["kind"] == "app":
                    extra = f" · ≈{c['share'] * 100:.0f}% of this PC's traffic (~{c['mbps']:.1f} Mbps)"
                rows.append(f"• <b>{esc(c['name'])}</b> <span style='{dim}'>({esc(c['detail'])})</span> — "
                            f"{c['confidence']}{extra}")
            if ev.get("devices"):
                rows.append(f"<span style='{dim}'>Online then: {esc(', '.join(ev['devices'][:8]))}. "
                            "This PC can't tell which of them it was.</span>")
            rows.append(f"<i>Tip: {esc(ev['causes'][0]['tip'])}</i>")
            parts.append("<br>".join(rows))
            if len(log) > 1:
                lines = []
                for e in reversed(log[-9:-1]):
                    w, pk, _ = self._spike_line(e)
                    lines.append(f"{w[:5]} &nbsp;{pk} &nbsp;<span style='{dim}'>{esc(e['causes'][0]['name'])}</span>")
                parts.append("<b>Earlier spikes</b><br>" + "<br>".join(lines))
            top = spikes.offenders(log)
            parts.append("<b>Top causes this session</b><br>" + " · ".join(f"{esc(n)} ×{k}" for n, k in top[:4]))
        parts.append(f"<span style='{dim}; font-size:7.5pt'>Per-app numbers are estimates: Windows only "
                     "reports exact per-app traffic to admin tools. This PC's total is exact.</span><br>"
                     "<a style='color:#93c5fd' href='devices'>Devices on my network</a> &nbsp;·&nbsp; "
                     "<a style='color:#93c5fd' href='ping'>Ping details</a>")
        return "<br><br>".join(parts)

    def show_spike_panel(self, pos):
        def link(href):
            if href == "devices":
                self.show_devices_panel(pos)
            elif href == "ping":
                self.show_info(self.data_view().ping_details() if self.on_data_view() else "", pos)
        self.show_info(self.spike_html(), pos, on_link=link, key="spikes")

    # ----- devices -----
    def _device_loop(self):
        n = 0
        while True:
            try:
                self.devices.refresh()
                if n % 10 == 0:
                    self.devices.resolve_names()
                    self.events.put(("devices", None))
            except Exception:
                pass
            n += 1
            time.sleep(devices.REFRESH_S)

    def devices_html(self):
        esc = html.escape
        dim = "color:rgba(255,255,255,150)"
        now = time.time()
        tr = self.devices
        with tr.lock:
            items = sorted(tr.store.items(), key=lambda kv: tuple(int(x) for x in kv[1]["ip"].split(".")))
        online = [kv for kv in items if now - kv[1]["last"] <= devices.REFRESH_S * 3]
        rows = []
        for mac, d in items:
            seen = "online" if now - d["last"] <= devices.REFRESH_S * 3 else \
                f"seen {time.strftime('%d/%m %H:%M', time.localtime(d['last']))}"
            new = " <span style='color:#86efac'><b>NEW</b></span>" if tr.is_new(d, now) else ""
            vend = d.get("vendor") or ""
            if d.get("name") and vend.startswith("Private address"):
                vend = ""  # the real name says more than "probably a phone"
            sub = " · ".join(x for x in (vend if vend != tr.label(mac, d) else "", d.get("link", ""), d["ip"], seen)
                             if x)
            rows.append(f"<b>{esc(tr.label(mac, d))}</b>{new}<br><span style='{dim}'>{esc(sub)}</span>")
        head = f"<b>Devices on your network</b> &nbsp;{len(online)} online"
        scan = ("<i>Scanning the network…</i>" if tr.scanning else
                "<a style='color:#93c5fd' href='scan'>Scan the network now</a>")
        body = "<br>".join(rows) if rows else f"<span style='{dim}'>None seen yet.</span>"
        note = (f"<span style='{dim}; font-size:7.5pt'>From this PC's address table and the router's "
                "connected-devices list (read only). It shows who is online, not how much each one uses.</span>")
        return f"{head}<br><br>{body}<br><br>{scan}<br>{note}"

    def show_devices_panel(self, pos):
        def link(href):
            if href == "scan":
                threading.Thread(target=self._scan_devices, daemon=True).start()
                QTimer.singleShot(100, lambda: self.show_devices_panel(pos))
        self.fetch_router_devices()
        self.show_info(self.devices_html(), pos, on_link=link, key="devices")

    def fetch_router_devices(self):
        """Read the router's connected-devices list (read-only cmd, GET), at most every 5 s and
        only on demand: when the devices panel opens or a spike points at another device."""
        client = self.poller.client
        now = time.time()
        if not isinstance(client, RouterClient) or now - self._router_devices_at < 5:
            return
        self._router_devices_at = now

        def work():
            try:
                resp = client.get_devices()
            except (RouterError, PermissionError):
                return
            if resp and devices.merge_router_list(self.devices, resp):
                self.events.put(("devices", "router"))
        threading.Thread(target=work, daemon=True).start()

    def _scan_devices(self):
        try:
            self.devices.sweep()
        finally:
            self.events.put(("devices", "scanned"))

    def toggle_notify(self):
        self.settings["spike_notify"] = self.act_notify.isChecked()
        save_settings(self.settings)

    def _place(self, center=False):
        screen = (self.screen() or QApplication.primaryScreen()).availableGeometry()
        if center:
            self.move(screen.center().x() - self.width() // 2, screen.center().y() - self.height() // 2)
            return
        pos = self.settings.get("pos")
        x, y = (pos if pos else (screen.right() - W - 24, screen.top() + 24))
        # Keep it on a screen if the monitor layout changed or the taller mode would hang off the bottom.
        visible = any(s.availableGeometry().contains(int(x) + 20, int(y) + 20) for s in QApplication.screens())
        if not visible:
            x, y = screen.right() - W - 24, screen.top() + 24
        for s in QApplication.screens():
            if s.availableGeometry().contains(int(x) + 20, int(y) + 20):
                screen = s.availableGeometry()
        y = min(int(y), screen.bottom() - self.height())
        self.move(int(x), max(int(y), screen.top()))

    def moveEvent(self, e):
        super().moveEvent(e)
        if self.on_data_view():
            self.save_timer.start()

    def _save_pos(self):
        self.settings["pos"] = [self.x(), self.y()]
        save_settings(self.settings)

    # ----- tray / visibility -----
    def show_window(self):
        self.show()
        self.raise_()
        self.activateWindow()
        self._remember_visible(True)

    def hide_to_tray(self):
        self.hide()
        self._remember_visible(False)
        if not self.settings.get("tray_hint_shown") and QSystemTrayIcon.isSystemTrayAvailable():
            self.tray.showMessage(APP_TITLE, "Still running in the tray. Click the bars icon to show it again.",
                                  QIcon(tray_pixmap(self.last and self.last["overall"], True)), 4000)
            self.settings["tray_hint_shown"] = True
            save_settings(self.settings)

    def toggle_visible(self):
        if self.isVisible():
            self.hide_to_tray()
        else:
            self.show_window()

    def _remember_visible(self, visible):
        if self.settings.get("visible") != visible:
            self.settings["visible"] = visible
            save_settings(self.settings)

    def _tray_clicked(self, reason):
        if reason == QSystemTrayIcon.ActivationReason.Trigger:
            self.toggle_visible()

    def _update_tray(self):
        s = self.last
        live = self.data_view().live if s else False
        rating = s["overall"] if s else None
        self.tray.setIcon(QIcon(tray_pixmap(rating, live)))
        if s and live:
            tip = f"{s['network']} · {rating or 'No signal'}" + (f" · score {s['score']}" if s["score"] is not None else "")
        else:
            tip = f"{APP_TITLE} · {self.data_view().status or 'Not connected'}"
        samples = self.pingmon.snapshot() if self.pingmon else []
        if samples:
            ms = samples[-1][1]
            tip += " · ping " + ("timeout" if ms is None else f"{ms:.0f} ms")
        if self.speed.running:
            tip += " · speed test running"
        self.tray.setToolTip(tip)

    def toggle_autostart(self):
        self.act_autostart.setChecked(set_autostart(self.act_autostart.isChecked()))

    def toggle_start_hidden(self):
        self.settings["start_hidden"] = self.act_hidden.isChecked()
        save_settings(self.settings)

    def toggle_motion(self):
        MOTION["on"] = not self.act_motion.isChecked()
        self.settings["reduce_motion"] = not MOTION["on"]
        save_settings(self.settings)
        if MOTION["on"] and self.isVisible():
            self.bg_timer.start()
        else:
            self.bg_timer.stop()
        self.update()

    # ----- painting the glass card -----
    def paintEvent(self, _):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        r = QRectF(self.rect()).adjusted(0.5, 0.5, -0.5, -0.5)
        radius = 8 if self.blur else 16  # match Windows 11 window rounding when blur is on
        a_top, a_bot = (150, 175) if self.blur else (232, 242)
        g = QLinearGradient(r.topLeft(), r.bottomLeft())
        g.setColorAt(0, QColor(42, 48, 64, a_top))
        g.setColorAt(1, QColor(16, 18, 26, a_bot))
        path = QPainterPath()
        path.addRoundedRect(r, radius, radius)
        p.fillPath(path, g)

        # slow drifting accent glow behind everything (still when motion is reduced)
        t = time.time() if MOTION["on"] else 0.0
        gx = r.width() * (0.5 + 0.42 * math.sin(t * 0.21))
        gy = r.height() * (0.55 + 0.38 * math.sin(t * 0.13 + 1.3))
        accent = self.data_view().tv("score_c") if self.mode == "simple" and self.simple.live else None
        glow = QRadialGradient(QPointF(gx, gy), r.width() * 0.75)
        glow.setColorAt(0, alpha(accent or ACCENT, 34))
        glow.setColorAt(1, alpha(accent or ACCENT, 0))
        p.fillPath(path, glow)

        # soft sheen across the top-left, like light on glass
        sheen = QRadialGradient(QPointF(r.width() * 0.15, -20), r.width() * 0.9)
        sheen.setColorAt(0, QColor(255, 255, 255, 34))
        sheen.setColorAt(1, QColor(255, 255, 255, 0))
        p.fillPath(path, sheen)

        p.setPen(QPen(QColor(255, 255, 255, 46), 1))
        p.drawPath(path)
        edge = QLinearGradient(r.topLeft(), r.topRight())
        edge.setColorAt(0, QColor(255, 255, 255, 0))
        edge.setColorAt(0.5, QColor(255, 255, 255, 90))
        edge.setColorAt(1, QColor(255, 255, 255, 0))
        p.setPen(QPen(QBrush(edge), 1))
        p.drawLine(QPointF(r.left() + radius, r.top() + 1), QPointF(r.right() - radius, r.top() + 1))
        p.end()

    # ----- events from the poller / login -----
    def _drain(self):
        try:
            while True:
                kind, payload = self.events.get_nowait()
                if kind == "devices":
                    save_settings(self.settings)
                    if payload in ("scanned", "router") and self.info.isVisible() and self.info.key == "devices":
                        self.info.close()
                        self.show_devices_panel(None)
                    continue
                if kind == "ok":
                    spikes.feed_signal(self.evidence, time.time(), payload)
                    s = summarize(payload)
                    self.last = s
                    # All views stay up to date so switching mode keeps history.
                    for v in self.views.values():
                        v.show_data(s, payload)
                    self._update_tray()
                elif kind == "speed":
                    self.speed.on_event(payload)
                elif kind == "err":
                    for v in self.views.values():
                        v.set_offline(payload)
                    self._update_tray()
                elif kind == "login_ok":
                    client, pw, remember = payload
                    if remember:
                        try:
                            credentials.save_password(pw)
                        except OSError:
                            pass  # still works, it just asks again next time
                    self.poller.set_paused(False)
                    self.poller.set_client(client)
                    for v in self.views.values():
                        v.set_offline("Connecting…")
                    self.show_data_view()
                elif kind == "login_err":
                    self.login.btn.setEnabled(True)
                    self.login.set_msg(payload, error=True)
                    self.login.pw.setFocus()
                    self.login.pw.selectAll()
                elif kind == "bad_password":
                    credentials.forget_password()
                    self.show_login(payload + " Please enter it again.")
        except queue.Empty:
            pass

    # ----- menu actions -----
    def show_menu(self, pos):
        self.menu.exec(pos)

    def toggle_pause(self):
        paused = not self.poller.paused.is_set()
        self.poller.set_paused(paused)
        if self.pingmon:
            (self.pingmon.paused.set if paused else self.pingmon.paused.clear)()
        (self.sampler.paused.set if paused else self.sampler.paused.clear)()
        self.act_pause.setText("Resume" if paused else "Pause")
        for v in self.views.values():
            v.set_offline("Paused" if paused else "Resuming…")
        self._update_tray()

    def change_password(self):
        self.poller.set_client(None)
        credentials.forget_password()
        for v in self.views.values():
            v.history.clear()
            v.summary = None
        self.simple.scores.clear()
        self.scanner.scan = None
        self.last = None
        self._update_tray()
        self.show_login()

    def quit(self):
        if self.on_data_view() and self.isVisible():
            self._save_pos()
        self.tone.on.clear()
        self.poller.stop()
        if self.pingmon:
            self.pingmon.stop()
        self.sampler.stop()
        save_settings(self.settings)
        self.speed.stop_test()
        self.tray.hide()
        QApplication.quit()


# ---------------------------------------------------------------- single instance

def single_instance(window_factory):
    """Returns the local server when this is the only copy running, or None after asking
    the running copy to show itself."""
    name = f"{APP_NAME}-{os.environ.get('USERNAME', 'user')}"
    sock = QLocalSocket()
    sock.connectToServer(name)
    if sock.waitForConnected(300):
        sock.write(b"show")
        sock.flush()
        sock.waitForBytesWritten(300)
        sock.disconnectFromServer()
        return None
    QLocalServer.removeServer(name)  # left over after a crash
    server = QLocalServer()
    server.listen(name)
    return server


def main():
    ap = argparse.ArgumentParser(description="Router signal widget")
    ap.add_argument("--mock", action="store_true", help="show fake data, do not contact the router")
    ap.add_argument("--autostart", action="store_true", help="started with Windows")
    ap.add_argument("--minimized", action="store_true", help="start hidden in the tray")
    args = ap.parse_args()

    app = QApplication(sys.argv)
    app.setApplicationName(APP_TITLE)
    app.setQuitOnLastWindowClosed(False)  # it lives in the tray
    app.setStyleSheet(APP_QSS)

    server = None
    if not args.mock:
        server = single_instance(None)
        if server is None:
            return 0

    win = GlassWindow(mock=args.mock, autostart=args.autostart)
    if server is not None:
        def on_connect():
            conn = server.nextPendingConnection()
            if conn is not None:
                conn.readyRead.connect(lambda: (conn.readAll(), win.show_window()))
                conn.disconnected.connect(conn.deleteLater)
        server.newConnection.connect(on_connect)

    if args.minimized or win.start_hidden:
        win._place()
    else:
        win.show_window()
    code = app.exec()
    win.poller.stop()
    return code


if __name__ == "__main__":
    sys.exit(main())
