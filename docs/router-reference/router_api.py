"""Read-only client for the ZLT X17M 5G router web API.

Only four API commands are ever sent: get token, login, read signal, and read the
connected-devices list. Every request goes through RouterClient._call, which refuses any
other cmd, because other cmds on this router can reboot it or change settings.

Uses only the Python standard library, so nothing needs to be pip-installed.
"""

import hashlib
import json
import secrets
import ssl
import urllib.error
import urllib.request

CMD_GET_TOKEN = "3830c61a-620d-47da-ae47-33d8401401c4"
CMD_LOGIN = "d2aa9843-494b-4947-9621-a46ec652ecd9"
CMD_SIGNAL = "89af35c9-b448-4fc7-a477-828a3e9467f8"
# Read-only connected-devices list (allowed by the owner on 2026-10-28). The router web UI's
# "Connected devices" page (assets/connectedDevice.*.js) reads it with request_get, i.e.
# method GET; it returns dhcp_list_info (hostname, ip, ipv6, mac, interface) and
# wlan24g/5g_wifi_info. That page's "Block" button uses a different cmd (23, POST), never sent here.
CMD_DEVICES = "5332f5ee-5be9-4843-b85f-1b251aa5f4ff"

ALLOWED_CMDS = frozenset({CMD_GET_TOKEN, CMD_LOGIN, CMD_SIGNAL, CMD_DEVICES})
GET_ONLY_CMDS = frozenset({CMD_SIGNAL, CMD_DEVICES})

DEFAULT_HOST = "192.168.0.1"
TIMEOUT_S = 4


class RouterError(Exception):
    """Network or protocol failure; the caller should retry later."""


class AuthError(RouterError):
    """Login was refused (wrong password, or another session holds the login)."""

    def __init__(self, message, already_logged_in=False):
        super().__init__(message)
        self.already_logged_in = already_logged_in


def _md5_random():
    return hashlib.md5(secrets.token_bytes(32)).hexdigest()


def _is_already_login(resp):
    text = json.dumps(resp).lower()
    return "alreadylogin" in text or "already_login" in text


def _session_expired(resp):
    return resp.get("message") in ("NO_AUTH", "LOGIN_TIMEOUT")


def _parse(text):
    """The router's web page also strips anything before the first "{"."""
    return json.loads(text[text.find("{"):] if "{" in text else text)


def https_transport(url):
    """POST JSON to the router. Certificate checks are off for this opener only
    (the router has a self-signed certificate), and system proxies are ignored
    because the router is on the LAN."""
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    opener = urllib.request.build_opener(
        urllib.request.ProxyHandler({}),
        urllib.request.HTTPSHandler(context=ctx),
    )

    def send(body):
        req = urllib.request.Request(
            url,
            data=json.dumps(body).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with opener.open(req, timeout=TIMEOUT_S) as r:
                return _parse(r.read().decode("utf-8", "replace"))
        except (urllib.error.URLError, OSError) as e:
            raise RouterError(f"Router unreachable: {e.__class__.__name__}") from e
        except ValueError as e:
            raise RouterError("Router sent a non-JSON reply") from e

    return send


class RouterClient:
    def __init__(self, password, host=DEFAULT_HOST, username="admin", transport=None, session_id=""):
        if not password:
            raise ValueError("password is empty")
        self.host = host
        self.url = f"https://{host}/cgi-bin/http.cgi"
        self.username = username
        self._password = password
        # A session saved from the last run is tried first: the router allows only one login
        # and keeps a closed widget's session alive for a while, so logging in again would fail.
        self.session_id = session_id or ""
        self._send = transport or https_transport(self.url)

    def _call(self, body):
        if body.get("cmd") not in ALLOWED_CMDS:
            raise PermissionError(f"Refusing to send non read-only cmd {body.get('cmd')!r}")
        if body.get("cmd") in GET_ONLY_CMDS and body.get("method") != "GET":
            raise PermissionError("Read commands are only ever sent with method GET")
        resp = self._send(body)
        if not isinstance(resp, dict):
            raise RouterError("Unexpected router reply")
        return resp

    def login(self):
        resp = self._call({"cmd": CMD_GET_TOKEN, "method": "GET", "sessionId": ""})
        token = resp.get("token")
        if not token:
            raise RouterError("No token in router reply")

        # Our own sessionId, kept in case the login reply does not return one.
        own_sid = _md5_random() + _md5_random()
        passwd = hashlib.sha256((token + self._password).encode("utf-8")).hexdigest()
        resp = self._call({
            "cmd": CMD_LOGIN,
            "method": "POST",
            "username": self.username,
            "passwd": passwd,
            "token": token,
            "sessionId": "",
            "isSingleLogin": "1",
        })
        if _is_already_login(resp):
            raise AuthError("Router is logged in elsewhere", already_logged_in=True)
        if resp.get("success") is False or "fail" in (resp.get("login_fail"), resp.get("login_fail2")):
            raise AuthError(f"Login refused: {resp.get('message') or 'unknown reason'}")
        self.session_id = resp.get("sessionId") or own_sid
        return self.session_id

    def get_signal(self):
        """Return the signal dict, logging in first (or again) when needed."""
        if not self.session_id:
            self.login()
        resp = self._call({"cmd": CMD_SIGNAL, "method": "GET", "sessionId": self.session_id})
        if _session_expired(resp) or _is_already_login(resp):
            self.session_id = ""
            self.login()
            resp = self._call({"cmd": CMD_SIGNAL, "method": "GET", "sessionId": self.session_id})
            if _session_expired(resp):
                self.session_id = ""
                raise AuthError("Session rejected right after login", already_logged_in=True)
        if resp.get("success") is False:
            raise RouterError(f"Router error: {resp.get('message')}")
        return resp

    def get_devices(self):
        """Connected-devices list, or None when there is no valid session. Never logs in by
        itself, so it can't fight the signal poll for the router's single login."""
        if not self.session_id:
            return None
        resp = self._call({"cmd": CMD_DEVICES, "method": "GET", "sessionId": self.session_id})
        if _session_expired(resp) or resp.get("success") is False:
            return None
        return resp
