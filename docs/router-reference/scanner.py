"""Scanner / placement helpers: serving cell details, neighbor parsing, best-spot tracking.

Pure functions only (no UI, no network) so they are easy to test.
"""

import time

from ratings import to_float


def decode_eci(cell_id):
    """CELL_ID is the 28-bit E-UTRAN cell id in hex. Returns (eNodeB, cell) or None."""
    s = str(cell_id or "").strip().lower()
    if s.startswith("0x"):
        s = s[2:]
    if not s:
        return None
    try:
        eci = int(s, 16)
    except ValueError:
        return None
    return eci >> 8, eci & 0xFF


def _pcis(value):
    return {p.strip() for p in str(value or "").split("+") if p.strip()}


def find_plmn(data):
    for key in ("plmn", "PLMN", "network_plmn", "plmn_id", "PLMN_ID"):
        if str(data.get(key, "")).strip():
            return str(data[key]).strip()
    mcc = str(data.get("mcc", data.get("MCC", ""))).strip()
    mnc = str(data.get("mnc", data.get("MNC", ""))).strip()
    if mcc and mnc:
        return mcc + mnc
    return ""


def serving_cells(data):
    """Serving 4G anchor and 5G cell as dicts (5G is None when not connected)."""
    lte_band = "+".join("B" + b.strip() for b in str(data.get("currentband", "")).split("+") if b.strip())
    eci = decode_eci(data.get("CELL_ID"))
    lte = {
        "type": "4G",
        "band": lte_band,
        "pci": str(data.get("PCI", "")).strip(),
        "pcis": _pcis(data.get("PCI")),
        "earfcn": str(data.get("FREQ", "")).strip(),  # e.g. "101+1800", one per carrier
        "enb": eci[0] if eci else None,
        "cell": eci[1] if eci else None,
        "rsrp": to_float(data.get("RSRP")),
        "rsrq": to_float(data.get("RSRQ")),
        "sinr": to_float(data.get("SINR")),
    }
    nr = {
        "type": "5G",
        "band": ("n" + str(data["currentband_5g"]).strip()) if str(data.get("currentband_5g", "")).strip() else "",
        "pci": str(data.get("PCI_5G", "")).strip(),
        "pcis": _pcis(data.get("PCI_5G")),
        "earfcn": str(data.get("FREQ_5G", "")).strip(),  # NR-ARFCN
        "rsrp": to_float(data.get("RSRP_5G")),
        "rsrq": to_float(data.get("RSRQ_5G")),
        "sinr": to_float(data.get("SINR_5G")),
    }
    if nr["rsrp"] is None and nr["sinr"] is None:
        nr = None
    return lte, nr


def parse_neighbors(text, kind):
    """'EARFCN,PCI,RSRP,RSRQ,SINR;...' -> list of dicts. Bad entries are skipped."""
    out = []
    for chunk in str(text or "").split(";"):
        parts = [p.strip() for p in chunk.split(",")]
        if len(parts) < 2 or not any(parts):
            continue
        parts += [""] * (5 - len(parts))
        earfcn, pci, rsrp, rsrq, sinr = parts[:5]
        if not pci:
            continue
        out.append({
            "type": kind, "earfcn": earfcn, "pci": pci,
            "rsrp": to_float(rsrp), "rsrq": to_float(rsrq), "sinr": to_float(sinr),
        })
    return out


def _gt(a, b):
    return a is not None and b is not None and a > b


def analyze(data):
    """Serving cells + neighbors, each neighbor marked serving / stronger."""
    lte, nr = serving_cells(data)
    neighbors = parse_neighbors(data.get("lte_info"), "4G") + parse_neighbors(data.get("nr_info"), "5G")
    for n in neighbors:
        serving = lte if n["type"] == "4G" else nr
        n["is_serving"] = bool(serving and n["pci"] in serving["pcis"])
        n["stronger"] = bool(serving and not n["is_serving"]
                             and (_gt(n["sinr"], serving["sinr"]) or _gt(n["rsrp"], serving["rsrp"])))
    return {"lte": lte, "nr": nr, "neighbors": neighbors, "plmn": find_plmn(data)}


def _score(c):
    """Rank cells: SINR first, RSRP as tie-breaker. Missing values rank lowest."""
    return (c["sinr"] if c["sinr"] is not None else -999,
            c["rsrp"] if c["rsrp"] is not None else -999)


class BestTracker:
    """Remembers the best readings seen since the app started."""

    def __init__(self):
        self.best_neighbor = None   # neighbor dict + "at"
        self.best_sinr = None       # {"sinr", "rsrp", "type", "at"}
        self.best_rsrp = None       # {"rsrp", "sinr", "type", "at"}
        self.plmn = ""
        self.bands = {"4G": "", "5G": ""}

    def update(self, scan, now=None):
        now = now if now is not None else time.time()
        if scan["plmn"]:
            self.plmn = scan["plmn"]
        for cell in (scan["lte"], scan["nr"]):
            if cell and cell["band"]:
                self.bands[cell["type"]] = cell["band"]

        active = scan["nr"] or scan["lte"]
        if active["sinr"] is not None and (self.best_sinr is None or active["sinr"] > self.best_sinr["sinr"]):
            self.best_sinr = {"sinr": active["sinr"], "rsrp": active["rsrp"], "type": active["type"], "at": now}
        if active["rsrp"] is not None and (self.best_rsrp is None or active["rsrp"] > self.best_rsrp["rsrp"]):
            self.best_rsrp = {"rsrp": active["rsrp"], "sinr": active["sinr"], "type": active["type"], "at": now}

        for n in scan["neighbors"]:
            if n["sinr"] is None and n["rsrp"] is None:
                continue
            if self.best_neighbor is None or _score(n) > _score(self.best_neighbor):
                self.best_neighbor = dict(n, at=now)

    def copy_text(self, scan=None):
        """One-line summary to send to Ooredoo when asking for a cell lock."""
        b = self.best_neighbor
        if b is None and scan is not None:
            cell = scan["nr"] or scan["lte"]
            pci = cell["pci"] or "?"
            earfcn = cell["earfcn"] or "?"
            band = cell["band"] or "?"
            sinr, rsrp = cell["sinr"], cell["rsrp"]
        elif b is not None:
            pci, earfcn = b["pci"], b["earfcn"] or "?"
            band = self.bands.get(b["type"]) or "?"
            sinr, rsrp = b["sinr"], b["rsrp"]
        else:
            return ""
        fmt = lambda v: "?" if v is None else f"{v:g}"
        return (f"ZLT X17M best cell seen: PCI {pci}, EARFCN {earfcn}, band {band}, "
                f"SINR {fmt(sinr)}, RSRP {fmt(rsrp)}, PLMN {self.plmn or '?'}")


def tone_hz(sinr):
    """SINR 0 -> 300 Hz, 20 -> 900 Hz (clamped)."""
    if sinr is None:
        return None
    return int(300 + max(0.0, min(20.0, sinr)) / 20.0 * 600)
