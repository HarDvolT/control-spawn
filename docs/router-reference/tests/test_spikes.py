import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import devices  # noqa: E402
import spikes  # noqa: E402
from spikes import Evidence, SpikeDetector, classify, feed_signal, friendly, offenders  # noqa: E402


def run_detector(values, t0=0):
    d, events = SpikeDetector(), []
    for i, ms in enumerate(values):
        ev = d.update(t0 + i, ms)
        if ev:
            events.append(ev)
    return d, events


class DetectorTests(unittest.TestCase):
    def test_needs_a_baseline_first(self):
        _, events = run_detector([30, 300, 30, 30])
        self.assertEqual(events, [])

    def test_one_spike_merged(self):
        values = [30] * 15 + [200, 260, None, 220] + [31, 30] + [30] * 5
        _, events = run_detector(values)
        self.assertEqual(len(events), 1)
        ev = events[0]
        self.assertEqual((ev["start"], ev["end"]), (15, 18))
        self.assertEqual(ev["peak"], 260)
        self.assertEqual(ev["lost"], 1)
        self.assertEqual(ev["count"], 4)
        self.assertEqual(ev["baseline"], 30)

    def test_threshold_rules(self):
        d, _ = run_detector([30] * 15)
        self.assertEqual(d.threshold(), 75)          # 2.5 x 30
        d, _ = run_detector([100] * 15)
        self.assertEqual(d.threshold(), 250)         # 2.5 x 100
        d, _ = run_detector([10] * 15)
        self.assertEqual(d.threshold(), 50)          # 10 + 40 ms

    def test_two_spikes_separate(self):
        values = [30] * 12 + [200] + [30, 30, 30] + [180, 190] + [30, 30]
        _, events = run_detector(values)
        self.assertEqual(len(events), 2)

    def test_spike_samples_do_not_raise_baseline(self):
        d, _ = run_detector([30] * 12 + [300] * 3)
        self.assertEqual(d.baseline(), 30)


def evidence(pc=(0.2, 0.1), procs=None, signal=None, router=None, t=100):
    ev = Evidence()
    for i in range(-10, 5):
        ev.add("pc", (t + i, pc[0] if i >= -5 else 0.1, pc[1] if i >= -5 else 0.05))
    for i in range(-6, 3, 2):
        ev.add("procs", (t + i, procs or {}))
    for row in signal or []:
        ev.add("signal", row)
    for row in router or []:
        ev.add("router", row)
    return ev


EVENT = {"start": 100, "end": 102, "peak": 240, "lost": 0, "count": 3, "baseline": 30}


class ClassifyTests(unittest.TestCase):
    def test_app_on_this_pc(self):
        procs = {
            1: {"exe": "steam.exe", "services": [], "ops": 900, "conns": 6, "new": 2},
            2: {"exe": "steamwebhelper.exe", "services": [], "ops": 100, "conns": 2, "new": 0},
            3: {"exe": "chrome.exe", "services": [], "ops": 150, "conns": 9, "new": 0},
        }
        res = classify(EVENT, evidence(pc=(30.0, 2.0), procs=procs))
        top = res["causes"][0]
        self.assertEqual(top["kind"], "app")
        self.assertEqual(top["name"], "Steam")   # helpers merged into one
        self.assertEqual(top["confidence"], "likely")
        self.assertAlmostEqual(top["share"], 1000 / 1150)
        self.assertAlmostEqual(top["mbps"], 32 * 1000 / 1150)
        self.assertEqual(res["causes"][1]["name"], "Chrome")
        self.assertIn("Steam", top["tip"])

    def test_windows_update_inside_svchost(self):
        procs = {7: {"exe": "svchost.exe", "services": ["DoSvc"], "ops": 500, "conns": 4, "new": 1},
                 8: {"exe": "svchost.exe", "services": ["wuauserv", "BITS"], "ops": 400, "conns": 2, "new": 0}}
        res = classify(EVENT, evidence(pc=(20.0, 8.0), procs=procs))
        names = [c["name"] for c in res["causes"]]
        self.assertEqual(names[:2], ["Delivery Optimization (update sharing)", "Windows Update"])
        self.assertEqual(res["causes"][1]["detail"], "svchost.exe (wuauserv)")

    def test_unknown_program_is_named(self):
        name, detail, _ = friendly("someupdater.exe")
        self.assertEqual((name, detail), ("someupdater", "someupdater.exe"))

    def test_quiet_pc_falls_back_to_isp(self):
        procs = {1: {"exe": "chrome.exe", "services": [], "ops": 50, "conns": 3, "new": 0}}
        res = classify(EVENT, evidence(pc=(0.3, 0.1), procs=procs))
        self.assertEqual([c["kind"] for c in res["causes"]], ["isp"])

    def test_other_device_from_router_counters(self):
        router = [(99, 25.0, 3.0), (101, 30.0, 2.0)]
        res = classify(EVENT, evidence(pc=(0.5, 0.2), router=router))
        self.assertEqual(res["causes"][0]["kind"], "other_device")
        self.assertAlmostEqual(res["causes"][0]["mbps"], 33.0 - 0.7)

    def test_signal_drop(self):
        sig = [(80 + i, 9.0, -92.0, "5G", "715") for i in range(15)] + \
              [(97, 2.0, -93.0, "5G", "715"), (101, 1.0, -94.0, "5G", "715")]
        res = classify(EVENT, evidence(signal=sig))
        self.assertEqual(res["causes"][0]["kind"], "signal")
        self.assertIn("9 → 1 dB", res["causes"][0]["detail"])

    def test_5g_to_4g(self):
        sig = [(80 + i, 9.0, -92.0, "5G", "715") for i in range(15)] + [(101, 8.0, -91.0, "4G", "272")]
        res = classify(EVENT, evidence(signal=sig))
        self.assertIn("5G → 4G", res["causes"][0]["detail"])

    def test_offenders(self):
        s = [{"causes": [{"name": "Steam"}]}, {"causes": [{"name": "Steam"}]}, {"causes": [{"name": "OneDrive"}]}]
        self.assertEqual(offenders(s), [("Steam", 2), ("OneDrive", 1)])


class EvidenceTests(unittest.TestCase):
    def test_router_counters_to_rates(self):
        ev = Evidence()
        feed_signal(ev, 0, {"SINR_5G": "8", "RSRP_5G": "-96", "PCI_5G": "715",
                            "wan_rx_bytes": "1000000", "wan_tx_bytes": "0"})
        feed_signal(ev, 2, {"SINR_5G": "8", "RSRP_5G": "-96", "PCI_5G": "715",
                            "wan_rx_bytes": "3500000", "wan_tx_bytes": "250000"})
        self.assertEqual(len(ev.router), 1)
        t, down, up = ev.router[0]
        self.assertAlmostEqual(down, 10.0)
        self.assertAlmostEqual(up, 1.0)
        self.assertEqual(ev.signal[-1][3], "5G")

    def test_counter_reset_is_ignored(self):
        ev = Evidence()
        ev.add_router_counters(0, 5000, 5000)
        ev.add_router_counters(1, 100, 100)
        self.assertEqual(len(ev.router), 0)

    def test_is_internet(self):
        self.assertTrue(spikes.is_internet("1.1.1.1"))
        self.assertFalse(spikes.is_internet("192.168.0.1"))
        self.assertFalse(spikes.is_internet("127.0.0.1"))
        self.assertFalse(spikes.is_internet("fe80::1"))


ARP = """
Interface: 192.168.0.59 --- 0x9
  Internet Address      Physical Address      Type
  192.168.0.1           fc-3f-fc-e1-d6-06     dynamic
  192.168.0.250         f6-eb-6f-11-22-33     dynamic
  192.168.0.40          a4-83-e7-00-00-01     dynamic
  192.168.0.255         ff-ff-ff-ff-ff-ff     static
  224.0.0.22            01-00-5e-00-00-16     static

Interface: 172.20.0.1 --- 0x12
  172.20.0.5            00-15-5d-00-00-01     dynamic
"""


class DeviceTests(unittest.TestCase):
    def test_parse_arp(self):
        found = devices.parse_arp(ARP, devices.local_subnet())
        self.assertEqual([ip for ip, _ in found], ["192.168.0.1", "192.168.0.250", "192.168.0.40"])
        self.assertEqual(found[0][1], "FC:3F:FC:E1:D6:06")

    def test_vendor_and_private_mac(self):
        self.assertEqual(devices.vendor("A4:83:E7:00:00:01"), "Apple")
        self.assertTrue(devices.is_private_mac("F6:EB:6F:11:22:33"))
        self.assertIn("Private address", devices.vendor("F6:EB:6F:11:22:33"))
        self.assertEqual(devices.vendor("00:00:01:00:00:00"), "")

    def test_tracker_labels_and_new(self):
        store = {}
        t = devices.DeviceTracker(store, own_ip="192.168.0.59")
        t.started = 0
        t.refresh(now=100, arp_text=ARP)
        self.assertEqual(len(store), 3)
        mac, d = next((m, d) for m, d in store.items() if d["ip"] == "192.168.0.1")
        self.assertEqual(t.label(mac, d), "Router")
        apple = next(d for d in store.values() if d["ip"] == "192.168.0.40")
        self.assertTrue(t.is_new(apple, now=200))
        self.assertFalse(t.is_new(apple, now=100 + devices.NEW_S + 1))
        t.refresh(now=500, arp_text=ARP)
        self.assertEqual(apple["first"], 100)
        self.assertEqual(apple["last"], 500)

    def test_merge_router_list(self):
        store = {}
        t = devices.DeviceTracker(store, own_ip="192.168.0.59")
        resp = {"dhcp_list_info": [
                    {"hostname": "Galaxy-S21", "ip": "192.168.0.250", "mac": "f6:eb:6f:11:22:33", "interface": "wlan"},
                    {"hostname": "*", "ip": "192.168.0.59", "mac": "a4-4c-c8-11-22-33", "interface": "eth"},
                    {"hostname": "bad"}],
                "wlan5g_wifi_info": [{"ip": "192.168.0.250", "rssi": "-54"}]}
        self.assertEqual(devices.merge_router_list(t, resp, now=50), 2)
        phone = store["F6:EB:6F:11:22:33"]
        self.assertEqual((phone["name"], phone["link"]), ("Galaxy-S21", "Wi-Fi 5 GHz (-54 dBm)"))
        pc = store["A4:4C:C8:11:22:33"]
        self.assertEqual((pc["name"], pc["link"]), ("", "Cable"))
        self.assertEqual(t.label("A4:4C:C8:11:22:33", pc), "This PC")

    def test_router_traffic_hook_is_off(self):
        self.assertFalse(devices.ROUTER_DEVICE_TRAFFIC)
        self.assertIsNone(devices.router_device_traffic({"anything": 1}))


if __name__ == "__main__":
    unittest.main()
