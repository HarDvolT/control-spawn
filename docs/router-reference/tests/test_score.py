import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from ratings import (EXCELLENT, FAIR, GOOD, POOR, metric_score, rate, score_rating, summarize,  # noqa: E402
                     trend)


class ScoreTests(unittest.TestCase):
    def test_thresholds_land_on_band_edges(self):
        self.assertEqual(metric_score("RSRP", -100), 40)
        self.assertEqual(metric_score("RSRP", -90), 60)
        self.assertEqual(metric_score("RSRP", -80), 80)
        self.assertEqual(metric_score("SINR", 0), 40)
        self.assertEqual(metric_score("SINR", 13), 60)
        self.assertEqual(metric_score("SINR", 20), 80)

    def test_clamped(self):
        self.assertEqual(metric_score("RSRP", -140), 0)
        self.assertEqual(metric_score("RSRP", -50), 100)
        self.assertEqual(metric_score("SINR", -20), 0)
        self.assertEqual(metric_score("SINR", 45), 100)
        self.assertIsNone(metric_score("SINR", None))

    def test_word_always_matches_rating(self):
        for v in range(-130, -60):
            self.assertEqual(score_rating(metric_score("RSRP", v)), rate("RSRP", v), v)
        for v in [x / 2 for x in range(-20, 70)]:
            self.assertEqual(score_rating(metric_score("SINR", v)), rate("SINR", v), v)

    def test_summary_score_is_weaker_of_main_link(self):
        s = summarize({"RSRP_5G": "-85", "SINR_5G": "8", "RSRP": "-70", "SINR": "25"})
        self.assertEqual(s["score"], s["nr_score"])
        self.assertEqual(score_rating(s["score"]), s["overall"])
        self.assertEqual(s["overall"], FAIR)  # SINR 8 Fair beats RSRP -85 Good
        self.assertEqual(score_rating(s["lte_score"]), EXCELLENT)
        self.assertIsNone(summarize({})["score"])

    def test_bands(self):
        self.assertEqual(score_rating(100), EXCELLENT)
        self.assertEqual(score_rating(79.9), GOOD)
        self.assertEqual(score_rating(40), FAIR)
        self.assertEqual(score_rating(0), POOR)
        self.assertIsNone(score_rating(None))


class TrendTests(unittest.TestCase):
    def test_directions(self):
        self.assertEqual(trend([(0, 40), (5, 44), (10, 48), (15, 52)], 15), "up")
        self.assertEqual(trend([(0, 60), (5, 55), (10, 50)], 10), "down")
        self.assertEqual(trend([(0, 50), (5, 52), (10, 49), (15, 51)], 15), "steady")

    def test_needs_enough_readings(self):
        self.assertIsNone(trend([(0, 40), (2, 60)], 2))
        self.assertIsNone(trend([(0, 40), (1, 50), (2, 60)], 2))  # under 5 s of data
        self.assertIsNone(trend([(0, 40), (5, 50), (10, 60)], 100))  # all older than 30 s

    def test_ignores_missing(self):
        self.assertEqual(trend([(0, 40), (5, None), (10, 50), (15, 60)], 15), "up")


if __name__ == "__main__":
    unittest.main()
