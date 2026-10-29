import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from scanner import BestTracker, analyze, decode_eci, parse_neighbors, tone_hz  # noqa: E402

DATA = {
    "network_operator": "Ooredoo TN", "PCI": "272+272", "currentband": "1+3", "CELL_ID": "1E24A03",
    "RSRP": "-92", "RSRQ": "-14.0", "SINR": "0.0",
    "RSRP_5G": "-97", "RSRQ_5G": "-11", "SINR_5G": "8", "PCI_5G": "715", "currentband_5g": "78",
    "lte_info": "1850,272,-92,-14,0;1850,301,-89,-12,4;100,118,-101,-17,-3;",
    "nr_info": "",
    "plmn": "60503",
}


class ScannerTests(unittest.TestCase):
    def test_decode_eci(self):
        self.assertEqual(decode_eci("1E24A03"), (0x1E24A, 0x03))
        self.assertEqual(decode_eci("0x1E24A03"), (0x1E24A, 0x03))
        self.assertIsNone(decode_eci(""))
        self.assertIsNone(decode_eci("zz"))

    def test_parse_neighbors(self):
        n = parse_neighbors("1850,301,-89,-12,4;;bad;100,118,,,;", "4G")
        self.assertEqual([x["pci"] for x in n], ["301", "118"])
        self.assertEqual(n[0]["sinr"], 4.0)
        self.assertIsNone(n[1]["rsrp"])
        self.assertEqual(parse_neighbors("", "5G"), [])

    def test_analyze_marks_serving_and_stronger(self):
        scan = analyze(DATA)
        by_pci = {n["pci"]: n for n in scan["neighbors"]}
        self.assertTrue(by_pci["272"]["is_serving"])
        self.assertFalse(by_pci["272"]["stronger"])
        self.assertTrue(by_pci["301"]["stronger"])      # SINR 4 > 0 and RSRP -89 > -92
        self.assertFalse(by_pci["118"]["stronger"])
        self.assertEqual(scan["lte"]["enb"], 0x1E24A)
        self.assertEqual(scan["lte"]["band"], "B1+B3")
        self.assertEqual(scan["nr"]["band"], "n78")
        self.assertEqual(scan["plmn"], "60503")

    def test_no_5g(self):
        d = dict(DATA, RSRP_5G="", SINR_5G="", RSRQ_5G="")
        self.assertIsNone(analyze(d)["nr"])

    def test_best_tracker_and_copy(self):
        t = BestTracker()
        t.update(analyze(DATA), now=100)
        better = dict(DATA, SINR_5G="15", lte_info="1850,301,-85,-10,9;")
        t.update(analyze(better), now=200)
        t.update(analyze(DATA), now=300)
        self.assertEqual(t.best_sinr["sinr"], 15)
        self.assertEqual(t.best_sinr["at"], 200)
        self.assertEqual(t.best_neighbor["pci"], "301")
        self.assertEqual(t.best_neighbor["sinr"], 9)
        self.assertEqual(t.copy_text(),
                         "ZLT X17M best cell seen: PCI 301, EARFCN 1850, band B1+B3, SINR 9, RSRP -85, PLMN 60503")

    def test_real_router_fields(self):
        # Shape of the ZLT X17M reply: uppercase PLMN, FREQ per carrier, no neighbor lists.
        d = {k: v for k, v in DATA.items() if k not in ("lte_info", "nr_info", "plmn")}
        d.update(PLMN="60503", CELL_ID="05DF317", FREQ="101+1800", FREQ_5G="628704")
        scan = analyze(d)
        self.assertEqual(scan["plmn"], "60503")
        self.assertEqual(scan["lte"]["enb"], 24051)
        self.assertEqual(scan["neighbors"], [])
        t = BestTracker()
        t.update(scan)
        self.assertEqual(t.copy_text(scan),
                         "ZLT X17M best cell seen: PCI 715, EARFCN 628704, band n78, SINR 8, RSRP -97, PLMN 60503")

    def test_tone(self):
        self.assertEqual(tone_hz(0), 300)
        self.assertEqual(tone_hz(20), 900)
        self.assertEqual(tone_hz(50), 900)
        self.assertEqual(tone_hz(-5), 300)
        self.assertIsNone(tone_hz(None))


if __name__ == "__main__":
    unittest.main()
