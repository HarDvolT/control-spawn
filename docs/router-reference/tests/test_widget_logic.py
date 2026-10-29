import hashlib
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import router_api  # noqa: E402
from ratings import EXCELLENT, FAIR, GOOD, POOR, rate, summarize  # noqa: E402
from router_api import ALLOWED_CMDS, AuthError, RouterClient  # noqa: E402

EXAMPLE = {
    "network_type_str": "5G(NSA)", "network_operator": "Ooredoo TN", "signal_lvl": "3",
    "RSRP": "-92", "RSRQ": "-14.0", "SINR": "0.0", "RSSI": "-77", "CQI": "9",
    "PCI": "272+272", "currentband": "1+3", "bandwidth": "20+20",
    "RSRP_5G": "-97", "RSRQ_5G": "-11", "SINR_5G": "8", "RSSI_5G": "-86", "CQI_5G": "7",
    "PCI_5G": "715", "currentband_5g": "78", "bandwidth_5g": "100",
}


class FakeRouter:
    """Stands in for the HTTPS transport and records every body sent."""

    def __init__(self, password="secret", signal_replies=None, login_reply=None):
        self.password = password
        self.sent = []
        self.signal_replies = list(signal_replies or [])
        self.login_reply = login_reply
        self.logins = 0

    def __call__(self, json):
        self.sent.append(json)
        cmd = json["cmd"]
        if cmd == router_api.CMD_GET_TOKEN:
            return ({"token": "tok123"})
        if cmd == router_api.CMD_LOGIN:
            self.logins += 1
            if self.login_reply is not None:
                return (self.login_reply)
            ok = json["passwd"] == hashlib.sha256(("tok123" + self.password).encode()).hexdigest()
            return ({"success": True, "sessionId": "S" * 64} if ok else {"success": False, "message": "PASSWD_ERR"})
        if cmd == router_api.CMD_SIGNAL:
            if self.signal_replies:
                return (self.signal_replies.pop(0))
            return (dict(EXAMPLE, success=True))
        raise AssertionError("unexpected cmd")


class RatingTests(unittest.TestCase):
    def test_boundaries(self):
        self.assertEqual(rate("RSRP", -80), EXCELLENT)
        self.assertEqual(rate("RSRP", -80.5), GOOD)
        self.assertEqual(rate("RSRP", -90), GOOD)
        self.assertEqual(rate("RSRP", -100), FAIR)
        self.assertEqual(rate("RSRP", -101), POOR)
        self.assertEqual(rate("SINR", 20), EXCELLENT)
        self.assertEqual(rate("SINR", 13), GOOD)
        self.assertEqual(rate("SINR", 0), FAIR)
        self.assertEqual(rate("SINR", -0.5), POOR)
        self.assertEqual(rate("RSRQ", -10), EXCELLENT)
        self.assertEqual(rate("RSRQ", -15), GOOD)
        self.assertEqual(rate("RSRQ", -20), FAIR)
        self.assertEqual(rate("RSRQ", -21), POOR)
        self.assertIsNone(rate("RSRP", None))

    def test_example_response(self):
        s = summarize(EXAMPLE)
        self.assertTrue(s["main_is_5g"])
        self.assertEqual(s["network"], "5G NSA")
        self.assertEqual(s["band"], "n78")
        self.assertEqual(s["bars"], 3)
        # 5G: RSRP -97 Fair, SINR 8 Fair -> Fair
        self.assertEqual(s["overall"], FAIR)
        self.assertEqual(s["lte"]["ratings"], {"RSRP": FAIR, "RSRQ": GOOD, "SINR": FAIR})
        self.assertEqual(s["nr"]["ratings"], {"RSRP": FAIR, "RSRQ": GOOD, "SINR": FAIR})

    def test_4g_only_and_empty_strings(self):
        d = dict(EXAMPLE, network_type_str="4G", RSRP_5G="", RSRQ_5G="", SINR_5G="", currentband_5g="", SINR="-2")
        s = summarize(d)
        self.assertFalse(s["main_is_5g"])
        self.assertEqual(s["band"], "B1+B3")
        self.assertEqual(s["overall"], POOR)  # RSRP -92 Fair, SINR -2 Poor
        self.assertIsNone(s["nr"]["values"]["RSRP"])

    def test_no_data(self):
        s = summarize({})
        self.assertIsNone(s["overall"])
        self.assertEqual(s["network"], "No service")


class ClientTests(unittest.TestCase):
    def test_login_and_poll(self):
        fake = FakeRouter()
        c = RouterClient("secret", transport=fake)
        data = c.get_signal()
        self.assertEqual(data["RSRP_5G"], "-97")
        login = fake.sent[1]
        self.assertEqual(login["passwd"], hashlib.sha256(b"tok123secret").hexdigest())
        self.assertEqual(login["isSingleLogin"], "1")
        self.assertEqual(login["sessionId"], "")
        self.assertEqual(fake.sent[2]["sessionId"], "S" * 64)
        c.get_signal()
        self.assertEqual(fake.logins, 1)  # session reused

    def test_no_auth_relogs(self):
        fake = FakeRouter(signal_replies=[{"success": True, **EXAMPLE}, {"success": False, "message": "NO_AUTH"}])
        c = RouterClient("secret", transport=fake)
        c.get_signal()
        c.get_signal()
        self.assertEqual(fake.logins, 2)

    def test_login_timeout_relogs(self):
        fake = FakeRouter(signal_replies=[{"success": False, "message": "LOGIN_TIMEOUT"}])
        c = RouterClient("secret", transport=fake, session_id="old")
        self.assertEqual(c.get_signal()["RSRP_5G"], "-97")
        self.assertEqual(fake.logins, 1)

    def test_saved_session_is_reused(self):
        fake = FakeRouter()
        c = RouterClient("secret", transport=fake, session_id="saved")
        c.get_signal()
        self.assertEqual(fake.logins, 0)
        self.assertEqual(fake.sent[0]["sessionId"], "saved")

    def test_login_fail_flag(self):
        c = RouterClient("secret", transport=FakeRouter(login_reply={"login_fail": "fail", "login_times": "1"}))
        with self.assertRaises(AuthError) as cm:
            c.login()
        self.assertFalse(cm.exception.already_logged_in)

    def test_parse_skips_leading_junk(self):
        self.assertEqual(router_api._parse('﻿xx{"success": true}'), {"success": True})

    def test_wrong_password(self):
        c = RouterClient("wrong", transport=FakeRouter())
        with self.assertRaises(AuthError) as cm:
            c.get_signal()
        self.assertFalse(cm.exception.already_logged_in)

    def test_already_login(self):
        c = RouterClient("secret", transport=FakeRouter(login_reply={"success": False, "message": "alreadyLogin"}))
        with self.assertRaises(AuthError) as cm:
            c.get_signal()
        self.assertTrue(cm.exception.already_logged_in)

    def test_only_allowed_cmds_are_sent(self):
        fake = FakeRouter(signal_replies=[{"success": False, "message": "NO_AUTH"}])
        c = RouterClient("secret", transport=fake)
        for _ in range(3):
            c.get_signal()
        self.assertTrue(all(b["cmd"] in ALLOWED_CMDS for b in fake.sent))
        self.assertEqual(ALLOWED_CMDS, {router_api.CMD_GET_TOKEN, router_api.CMD_LOGIN,
                                        router_api.CMD_SIGNAL, router_api.CMD_DEVICES})
        with self.assertRaises(PermissionError):
            c._call({"cmd": "00000000-reboot", "method": "POST"})

    def test_device_list_is_get_only(self):
        fake = FakeRouter()
        c = RouterClient("secret", transport=fake)
        self.assertIsNone(c.get_devices())  # no session yet: no request and no login
        self.assertEqual(fake.sent, [])
        c.session_id = "S" * 64
        for cmd in (router_api.CMD_DEVICES, router_api.CMD_SIGNAL):
            with self.assertRaises(PermissionError):
                c._call({"cmd": cmd, "method": "POST", "sessionId": c.session_id})
        self.assertEqual(fake.sent, [])

    def test_own_session_id_fallback(self):
        c = RouterClient("x", transport=FakeRouter(login_reply={"success": True}))
        sid = c.login()
        self.assertEqual(len(sid), 64)
        int(sid, 16)


if __name__ == "__main__":
    unittest.main()
