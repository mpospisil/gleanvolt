#!/usr/bin/env python3
"""
Proves the probe measures what it claims to, against a stand-in portal, before anyone points it at
a real account and waits three days for an answer.

The stand-in is shaped like the real thing as issue #137 describes it: a portal, a *separate* OIDC
identity provider, an identifier page and then a password page, and a redirect_uri that lands back
on the portal's own /login -- which is what mints the session. Its data endpoints 401 once the
session has expired, and only a completed password post revives them.

What this exercises: the login replay end to end, the expiry being noticed on the first bad
response, re-login restoring access, a *second* expiry arriving unattended, the target_soc field
scan through a real ZIP, and the report that turns the log into the three answers.

What it cannot exercise, and nothing local could: how VW's IdP actually behaves. The field names,
the number of pages and whether a human step appears are exactly what the real run is for.

    ./selftest.py
"""

from __future__ import annotations

import http.server
import importlib.util
import io
import json
import os
import socketserver
import sys
import tempfile
import threading
import types
import urllib.parse
import zipfile

PORTAL_PORT, IDP_PORT = 8791, 8792

# Three good polls, then the session is gone. Enough to see a lifetime, a recovery and a second
# lifetime without waiting for one.
STATE = {"polls": 0, "expire_after": 3, "signed_in": False}

IDENTIFIER_FORM = """<html><body><form action="/oidc/v1/password" method="post">
<input type="hidden" name="_csrf" value="c1"><input type="hidden" name="relayState" value="r1">
<input type="hidden" name="hmac" value="h1"><input type="email" name="identifier" value="">
</form></body></html>"""

PASSWORD_FORM = """<html><body><form action="/oidc/v1/complete" method="post">
<input type="hidden" name="_csrf" value="c2"><input type="hidden" name="hmac" value="h2">
<input type="hidden" name="relayState" value="r1">
<input type="password" name="password" value=""></form></body></html>"""


def _report_zip() -> bytes:
    """A dataset bundle with the field #101 is waiting on, plus a member that is not JSON."""
    buffer = io.BytesIO()

    with zipfile.ZipFile(buffer, "w") as bundle:
        bundle.writestr("report.json", json.dumps({
            "vehicle": {"vin": "WVWZZZE2ZMP012345", "odometer_km": 18234},
            "battery": {"state_of_charge_pct": 61},
            "settings": {"target_soc": 80, "remaining_charging_time_target_soc": 95},
        }))
        bundle.writestr("manifest.txt", "not json")

    return buffer.getvalue()


REPORT_ZIP = _report_zip()


class Base(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args) -> None:
        pass

    def send(self, status: int, body="", ctype="text/html", location=None) -> None:
        data = body.encode() if isinstance(body, str) else body
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))

        if location:
            self.send_header("Location", location)

        self.end_headers()
        self.wfile.write(data)


class PortalHandler(Base):
    def do_GET(self) -> None:
        path = urllib.parse.urlparse(self.path).path

        if path == "/de/en/login":
            if "code=" in self.path:
                STATE.update(signed_in=True, polls=0)
                return self.send(302, location="/de/en/dashboard")
            return self.send(302, location=f"http://127.0.0.1:{IDP_PORT}/oidc/v1/authorize")

        if path.startswith("/proxy_api/"):
            if path.endswith("/list"):
                STATE["polls"] += 1
                if STATE["polls"] > STATE["expire_after"]:
                    STATE["signed_in"] = False

            if not STATE["signed_in"]:
                return self.send(401, '{"error":"unauthorized"}', "application/json")

            if path.endswith("/vehicles"):
                return self.send(
                    200, json.dumps([{"vin": "WVWZZZE2ZMP012345", "requestId": "req-1"}]),
                    "application/json")

            return self.send(200, json.dumps([{"id": "d1", "downloadUrl": "/dl"}]),
                             "application/json")

        if path == "/dl":
            return self.send(200, REPORT_ZIP, "application/zip")

        return self.send(200, "<html>portal</html>")


class IdpHandler(Base):
    def do_GET(self) -> None:
        self.send(200, IDENTIFIER_FORM)

    def do_POST(self) -> None:
        self.rfile.read(int(self.headers.get("Content-Length", 0)))
        path = urllib.parse.urlparse(self.path).path

        if path == "/oidc/v1/password":
            return self.send(200, PASSWORD_FORM)

        if path == "/oidc/v1/complete":
            return self.send(302, location=f"http://127.0.0.1:{PORTAL_PORT}/de/en/login?code=xyz")

        self.send(404, "no")


class Server(socketserver.TCPServer):
    allow_reuse_address = True


def serve() -> None:
    for port, handler in ((PORTAL_PORT, PortalHandler), (IDP_PORT, IdpHandler)):
        server = Server(("127.0.0.1", port), handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()


def main() -> int:
    serve()
    os.environ["VW_PORTAL_BASE"] = f"http://127.0.0.1:{PORTAL_PORT}"

    here = os.path.dirname(os.path.abspath(__file__))
    spec = importlib.util.spec_from_file_location("probe", os.path.join(here, "session_probe.py"))
    probe = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(probe)

    # A scratch jar and a scratch log, so a self-test never touches a real run in progress.
    with tempfile.TemporaryDirectory() as scratch:
        probe.COOKIE_FILE = os.path.join(scratch, "cookies.txt")
        probe.LOG_FILE = os.path.join(scratch, "probe.jsonl")
        probe.time.sleep = lambda seconds: None      # the loop's clock, not its logic

        portal = probe.Portal()

        print("--- login ---")
        assert probe.do_login(portal, "you@example.com", "hunter2")["outcome"] == "ok", \
            "the form replay did not end in a working session"

        print("\n--- discover ---")
        probe.do_discover(portal)

        print("\n--- watch: two expiries, one unattended recovery between them ---")
        args = types.SimpleNamespace(
            vin="WVWZZZE2ZMP012345", request_id="req-1", every=15, idle_first=0,
            username="you@example.com", password="hunter2")

        # The third re-login is refused, which is how the loop is brought to a stop -- and it doubles
        # as the check that a human step is reported loudly rather than swallowed.
        real_login = probe.do_login
        attempts = {"count": 0}

        def limited(session, username, password):
            attempts["count"] += 1

            if attempts["count"] > 2:
                return probe.log("login", outcome="incomplete", seconds=1, landed="idp/login",
                                 keywords=["login", "captcha"], steps=[])

            return real_login(session, username, password)

        probe.do_login = limited
        probe.do_watch(portal, args)
        probe.do_login = real_login

        print("\n--- fields ---")
        STATE.update(signed_in=True, polls=0)
        probe.do_fields(portal, args)

        print("\n--- report ---")
        probe.do_report()

        with open(probe.LOG_FILE, encoding="utf-8") as handle:
            records = [json.loads(line) for line in handle if line.strip()]

    expiries = [record for record in records if record["event"] == "expiry"]
    recovered = [record for record in records
                 if record["event"] == "login" and record["outcome"] == "ok"]
    fields = [record for record in records
              if record["event"] == "fields" and record["outcome"] == "ok"]

    assert len(expiries) >= 2, f"expected two expiries, saw {len(expiries)}"
    assert len(recovered) >= 2, "re-login did not restore access unattended"
    assert any("settings.target_soc" in record["interesting"] for record in fields), \
        "the field scan missed settings.target_soc"

    print("\nOK — the probe sees an expiry, recovers from it, sees the next one, and finds "
          "settings.target_soc in a bundle.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
