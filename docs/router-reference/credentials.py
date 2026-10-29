"""Remembers the router password between runs.

On Windows the password is encrypted with DPAPI (CryptProtectData), so only
your Windows user account on this PC can decrypt it. Stored in
%APPDATA%\\RouterSignalWidget\\credentials.json.

The router's login session is kept the same way in session.json, so a restarted
widget carries on with it instead of being told the router is logged in elsewhere.
"""

import base64
import json
import os
import sys

APP_NAME = "RouterSignalWidget"


def app_data_dir():
    base = os.environ.get("APPDATA") or os.path.expanduser("~")
    folder = os.path.join(base, APP_NAME)
    os.makedirs(folder, exist_ok=True)
    return folder


def _path(name):
    return os.path.join(app_data_dir(), name)


if sys.platform == "win32":
    import ctypes
    from ctypes import wintypes

    class _Blob(ctypes.Structure):
        _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]

    def _dpapi(data, protect):
        buf = ctypes.create_string_buffer(data, len(data))
        inp = _Blob(len(data), ctypes.cast(buf, ctypes.POINTER(ctypes.c_char)))
        out = _Blob()
        fn = ctypes.windll.crypt32.CryptProtectData if protect else ctypes.windll.crypt32.CryptUnprotectData
        if not fn(ctypes.byref(inp), None, None, None, None, 0, ctypes.byref(out)):
            raise OSError("DPAPI call failed")
        try:
            return ctypes.string_at(out.pbData, out.cbData)
        finally:
            ctypes.windll.kernel32.LocalFree(ctypes.cast(out.pbData, ctypes.c_void_p))

    def _encode(pw):
        return {"dpapi": base64.b64encode(_dpapi(pw.encode("utf-8"), True)).decode()}

    def _decode(rec):
        return _dpapi(base64.b64decode(rec["dpapi"]), False).decode("utf-8")
else:
    # Non-Windows (development only): not encrypted, file is user-readable only.
    def _encode(pw):
        return {"b64": base64.b64encode(pw.encode("utf-8")).decode()}

    def _decode(rec):
        return base64.b64decode(rec["b64"]).decode("utf-8")


def _load(name):
    try:
        with open(_path(name), encoding="utf-8") as f:
            return _decode(json.load(f)) or None
    except (OSError, ValueError, KeyError, TypeError):
        return None


def _save(name, secret):
    path = _path(name)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(_encode(secret), f)
    try:
        os.chmod(path, 0o600)
    except OSError:
        pass


def _remove(name):
    try:
        os.remove(_path(name))
    except OSError:
        pass


def load_password():
    return _load("credentials.json")


def save_password(password):
    _save("credentials.json", password)


def forget_password():
    _remove("credentials.json")
    forget_session()


def load_session():
    return _load("session.json")


def save_session(session_id):
    if session_id:
        _save("session.json", session_id)
    else:
        forget_session()


def forget_session():
    _remove("session.json")
