import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import speedtest  # noqa: E402
from pingmon import PingMonitor, parse_ping, ping_rating, stats  # noqa: E402


def reply(line):
    return f"\r\nPinging 1.1.1.1 with 32 bytes of data:\r\n{line}\r\n\r\nPing statistics for 1.1.1.1:\r\n"


class PingParseTests(unittest.TestCase):
    def test_english(self):
        self.assertEqual(parse_ping(reply("Reply from 1.1.1.1: bytes=32 time=39ms TTL=57")), 39)

    def test_under_one_ms(self):
        self.assertEqual(parse_ping(reply("Reply from 192.168.0.1: bytes=32 time<1ms TTL=64")), 0.5)

    def test_localized(self):
        self.assertEqual(parse_ping(reply("Antwort von 1.1.1.1: Bytes=32 Zeit=12ms TTL=57")), 12)
        self.assertEqual(parse_ping(reply("Réponse de 1.1.1.1 : octets=32 temps=23 ms TTL=57")), 23)
        self.assertEqual(parse_ping(reply("Respuesta desde 1.1.1.1: bytes=32 tiempo=101ms TTL=57")), 101)
        self.assertEqual(parse_ping(reply("Réponse de 8.8.8.8 : octets=32 temps<1ms TTL=118")), 0.5)

    def test_failures(self):
        self.assertIsNone(parse_ping(reply("Request timed out.")))
        self.assertIsNone(parse_ping(reply("Délai d'attente de la demande dépassé.")))
        self.assertIsNone(parse_ping(reply("Reply from 192.168.0.1: Destination host unreachable.")))
        self.assertIsNone(parse_ping("Ping request could not find host nowhere.invalid."))
        self.assertIsNone(parse_ping(""))

    def test_statistics_footer_is_not_a_reply(self):
        text = "Minimum = 22ms, Maximum = 39ms, Average = 30ms"
        self.assertIsNone(parse_ping(text))


class PingStatsTests(unittest.TestCase):
    def test_loss_avg_jitter(self):
        samples = [(0, 20.0), (1, None), (2, 30.0), (3, 26.0), (4, None)]
        st = stats(samples, 4, 60)
        self.assertEqual(st["count"], 5)
        self.assertAlmostEqual(st["loss"], 40.0)
        self.assertAlmostEqual(st["avg"], 76 / 3)
        self.assertEqual((st["min"], st["max"]), (20.0, 30.0))
        self.assertAlmostEqual(st["jitter"], (10 + 4) / 2)

    def test_window(self):
        samples = [(0, 500.0), (100, 20.0), (101, 22.0)]
        st = stats(samples, 101, 60)
        self.assertEqual(st["count"], 2)
        self.assertEqual(st["max"], 22.0)

    def test_empty(self):
        st = stats([], 0, 60)
        self.assertIsNone(st["loss"])
        self.assertIsNone(st["avg"])
        self.assertIsNone(st["jitter"])

    def test_rating(self):
        self.assertEqual(ping_rating(29.9), "Excellent")
        self.assertEqual(ping_rating(30), "Good")
        self.assertEqual(ping_rating(99), "Fair")
        self.assertEqual(ping_rating(100), "Poor")
        self.assertIsNone(ping_rating(None))

    def test_monitor_records_and_retargets(self):
        mon = PingMonitor(target="x", pinger=lambda t: 12.0)
        mon.samples.append((0, 5.0))
        mon.set_target("8.8.8.8")
        self.assertEqual(mon.target, "8.8.8.8")
        self.assertEqual(mon.snapshot(), [])


class SpeedMathTests(unittest.TestCase):
    def test_mbps(self):
        self.assertAlmostEqual(speedtest.mbps(12_500_000, 1.0), 100.0)
        self.assertEqual(speedtest.mbps(100, 0), 0.0)

    def test_ping_stats(self):
        med, jit = speedtest.ping_stats([20.0, 30.0, 25.0, 40.0])
        self.assertEqual(med, 27.5)
        self.assertAlmostEqual(jit, (10 + 5 + 15) / 3)
        self.assertEqual(speedtest.ping_stats([12.0]), (12.0, 0.0))
        self.assertEqual(speedtest.ping_stats([]), (None, None))

    def test_verdicts(self):
        self.assertIn("Great", speedtest.speed_verdict(150))
        self.assertIn("4K", speedtest.speed_verdict(25))
        self.assertIn("OK", speedtest.speed_verdict(8))
        self.assertIn("Slow", speedtest.speed_verdict(3))
        self.assertEqual(speedtest.speed_verdict(None), "")
        self.assertIn("great", speedtest.ping_verdict(10))
        self.assertIn("lag", speedtest.ping_verdict(150))

    def test_cancel_before_start(self):
        import threading
        cancel = threading.Event()
        cancel.set()
        calls = []
        orig = speedtest._conn

        class FakeResp:
            def read(self, *a):
                return b""

        class FakeConn:
            def request(self, *a):
                calls.append(a)

            def getresponse(self):
                return FakeResp()

            def close(self):
                pass

        speedtest._conn = FakeConn
        try:
            self.assertIsNone(speedtest.run(lambda *a: None, cancel))
        finally:
            speedtest._conn = orig
        self.assertEqual(len(calls), 1)  # only the warm-up request, no measuring


if __name__ == "__main__":
    unittest.main()
