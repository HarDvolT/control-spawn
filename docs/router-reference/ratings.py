"""Signal ratings and parsing of the router's signal reply."""

EXCELLENT, GOOD, FAIR, POOR = "Excellent", "Good", "Fair", "Poor"
ORDER = [EXCELLENT, GOOD, FAIR, POOR]

COLORS = {
    EXCELLENT: "#3ddc84",
    GOOD: "#a3d65c",
    FAIR: "#f5b942",
    POOR: "#ef5350",
    None: "#7a7f87",
}

# (excellent_min, good_min, fair_min); anything below fair_min is Poor.
THRESHOLDS = {
    "RSRP": (-80, -90, -100),
    "SINR": (20, 13, 0),
    "RSRQ": (-10, -15, -20),
}


def to_float(value):
    """Router values are strings; "" (or anything unparsable) means no data."""
    if value is None:
        return None
    try:
        s = str(value).strip()
        return float(s) if s else None
    except ValueError:
        return None


def rate(metric, value):
    if value is None:
        return None
    exc, good, fair = THRESHOLDS[metric]
    if value >= exc:
        return EXCELLENT
    if value >= good:
        return GOOD
    if value >= fair:
        return FAIR
    return POOR


def worst(*ratings):
    present = [r for r in ratings if r is not None]
    return max(present, key=ORDER.index) if present else None


def _link(data, suffix):
    vals = {m: to_float(data.get(m + suffix)) for m in ("RSRP", "RSRQ", "SINR")}
    return {
        "values": vals,
        "ratings": {m: rate(m, v) for m, v in vals.items()},
        "present": vals["RSRP"] is not None or vals["SINR"] is not None,
    }


def _network_label(raw):
    # "5G(NSA)" -> "5G NSA", "4G" -> "4G"
    return raw.replace("(", " ").replace(")", "").strip() if raw else "No service"


# 0-100 score per reading. The rating thresholds land on 40 / 60 / 80, so the score
# and the Excellent / Good / Fair / Poor word always agree.
SCORE_POINTS = {
    "RSRP": [(-120, 0), (-100, 40), (-90, 60), (-80, 80), (-70, 100)],
    "SINR": [(-5, 0), (0, 40), (13, 60), (20, 80), (30, 100)],
}
SCORE_BANDS = [(80, EXCELLENT), (60, GOOD), (40, FAIR), (0, POOR)]


def metric_score(metric, value):
    if value is None:
        return None
    pts = SCORE_POINTS[metric]
    if value <= pts[0][0]:
        return 0.0
    for (x0, y0), (x1, y1) in zip(pts, pts[1:]):
        if value <= x1:
            return y0 + (value - x0) / (x1 - x0) * (y1 - y0)
    return 100.0


def link_score(link):
    """The weaker of signal strength and signal vs. noise, like the overall rating."""
    scores = [metric_score(m, link["values"][m]) for m in ("RSRP", "SINR")]
    scores = [x for x in scores if x is not None]
    return round(min(scores)) if scores else None


def score_rating(score):
    if score is None:
        return None
    return next(r for lo, r in SCORE_BANDS if score >= lo)


TREND_S = 30


def trend(samples, now):
    """samples: (time, score) pairs. Returns "up", "down", "steady", or None if too few readings.
    Uses the least-squares slope over the last TREND_S seconds."""
    pts = [(t, v) for t, v in samples if v is not None and now - t <= TREND_S]
    if len(pts) < 3 or pts[-1][0] - pts[0][0] < 5:
        return None
    mt = sum(t for t, _ in pts) / len(pts)
    mv = sum(v for _, v in pts) / len(pts)
    den = sum((t - mt) ** 2 for t, _ in pts)
    change = sum((t - mt) * (v - mv) for t, v in pts) / den * (pts[-1][0] - pts[0][0])
    if change >= 4:
        return "up"
    if change <= -4:
        return "down"
    return "steady"


def summarize(data):
    lte = _link(data, "")
    nr = _link(data, "_5G")
    main = nr if nr["present"] else lte

    overall = worst(main["ratings"]["RSRP"], main["ratings"]["SINR"])

    if nr["present"] and str(data.get("currentband_5g", "")).strip():
        band = "n" + str(data["currentband_5g"]).strip()
    elif str(data.get("currentband", "")).strip():
        band = "+".join("B" + b for b in str(data["currentband"]).split("+") if b)
    else:
        band = ""

    try:
        bars = max(0, min(5, int(str(data.get("signal_lvl", "")).strip())))
    except ValueError:
        bars = 0

    return {
        "overall": overall,
        "score": link_score(main),
        "nr_score": link_score(nr),
        "lte_score": link_score(lte),
        "bars": bars,
        "network": _network_label(str(data.get("network_type_str", "")).strip()),
        "band": band,
        "operator": str(data.get("network_operator", "")).strip(),
        "lte": lte,
        "nr": nr,
        "main_is_5g": main is nr,
        "main_rsrp": main["values"]["RSRP"],
        "main_sinr": main["values"]["SINR"],
    }


# ---------- plain-language help shown in the UI ----------

METRICS = {
    "RSRP": {
        "chart": (-120, -70),     # history chart range
        "min_span": 12,  # the chart never zooms in closer than this
        "title": "Signal strength",
        "unit": "dBm",
        "scale": (-125, -65),
        "ends": ("weak", "strong"),
        "help": "How strong the signal from the tower is when it reaches the router.\n"
                "Numbers are negative: closer to 0 is stronger (-75 beats -105).",
    },
    "RSRQ": {
        "chart": (-25, -5),     # history chart range
        "min_span": 6,  # the chart never zooms in closer than this
        "title": "Signal quality",
        "unit": "dB",
        "scale": (-25, -5),
        "ends": ("poor", "clean"),
        "help": "How clean the signal is, taking into account how busy the tower is.\n"
                "Closer to 0 is better.",
    },
    "SINR": {
        "chart": (-5, 30),     # history chart range
        "min_span": 8,  # the chart never zooms in closer than this
        "title": "Signal vs. noise",
        "unit": "dB",
        "scale": (-5, 30),
        "ends": ("noisy", "clear"),
        "help": "How much the signal stands out from interference and noise.\n"
                "Higher is better. This one affects your speed the most.",
    },
}

VERDICTS = {
    EXCELLENT: "Great signal, full speed",
    GOOD: "Solid signal, good speeds",
    FAIR: "Usable, speed may drop",
    POOR: "Weak signal, try moving the router",
    None: "No signal data yet",
}


PLAIN = {
    EXCELLENT: "Great for 4K streaming and gaming",
    GOOD: "Good for HD video and video calls",
    FAIR: "OK for browsing; video may buffer",
    POOR: "Slow and unstable: try moving the router",
    None: "Waiting for the router…",
}


def threshold_text(metric):
    exc, good, fair = THRESHOLDS[metric]
    unit = METRICS[metric]["unit"]
    return (f"Excellent  ≥ {exc:g} {unit}\n"
            f"Good       {good:g} to {exc:g}\n"
            f"Fair       {fair:g} to {good:g}\n"
            f"Poor       below {fair:g}")


def weakest_metric(link):
    """Which of RSRP / SINR holds the overall rating down (None if they tie or are missing)."""
    r, s = link["ratings"]["RSRP"], link["ratings"]["SINR"]
    if r is None or s is None or r == s:
        return None
    return "RSRP" if ORDER.index(r) > ORDER.index(s) else "SINR"
