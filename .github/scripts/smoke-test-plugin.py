#!/usr/bin/env python3
"""Start a published plugin build and prove it runs (#426).

The plugin zips are the Windows route to a shipped plugin, and the release would otherwise attach them
without anything having started one. What this catches is what a compile cannot see: Blazor's client
script missing from a self-contained output, a single-file build that cannot find its own files, a
missing native dependency.

The plugin is pointed at a Gleanvolt that is not there, on purpose. It must keep running and keep
trying, not stop: on a real install Gleanvolt is often started after it, or restarted under it.

Three assertions:

  1. it is still running after it has failed to reach Gleanvolt;
  2. its page answers 403 without a ticket -- the SDK's guard is in front of it, and it is serving;
  3. Blazor's client script is served to a browser with a ticket.
"""

import argparse
import base64
import hashlib
import hmac
import os
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

PORT = 8098
TOKEN = "gvp_smoke-test-token-not-a-secret"
PROVIDER_ID = "openweather"
STARTUP_TIMEOUT = 90


def ticket():
    """A page ticket as Gleanvolt makes one: '<expiry>.<base64url HMAC-SHA256 of purpose, provider, expiry>'.

    Mirrors ProviderPage.CreateTicket. If the format changes there, assertion 3 fails here, which is
    the right place to find out the two have drifted.
    """
    expires = int(time.time()) + 60
    payload = f"gleanvolt-page-ticket/v1\n{PROVIDER_ID}\n{expires}".encode()
    signature = hmac.new(TOKEN.encode(), payload, hashlib.sha256).digest()
    return f"{expires}.{base64.urlsafe_b64encode(signature).decode().rstrip('=')}"


def status(url, opener=None):
    try:
        with (opener or urllib.request.build_opener()).open(url, timeout=5) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()
    except (urllib.error.URLError, ConnectionError, TimeoutError):
        return None, b""


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True)
    args = parser.parse_args()

    exe = os.path.abspath(args.exe)
    data = tempfile.mkdtemp(prefix="plugin-smoke-")
    log_path = os.path.join(data, "plugin.log")
    env = dict(os.environ)
    env.update({
        "DOTNET_ENVIRONMENT": "Production",
        "ASPNETCORE_ENVIRONMENT": "Production",
        "Urls": f"http://127.0.0.1:{PORT}",
        # Nothing listens there.
        "Gleanvolt__Endpoint": "http://127.0.0.1:9",
        "Gleanvolt__Token": TOKEN,
        "OpenWeather__DataDirectory": os.path.join(data, "data"),
    })

    with open(log_path, "w") as log:
        process = subprocess.Popen([exe], cwd=os.path.dirname(exe), env=env, stdout=log, stderr=subprocess.STDOUT)

    def fail(message):
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
        print(f"::error::{message}")
        with open(log_path) as log:
            print(log.read())
        sys.exit(1)

    try:
        deadline = time.time() + STARTUP_TIMEOUT
        code = None
        while time.time() < deadline:
            if process.poll() is not None:
                fail(f"The plugin exited with {process.returncode} instead of waiting for Gleanvolt.")
            code, _ = status(f"http://127.0.0.1:{PORT}/")
            if code is not None:
                break
            time.sleep(1)
        else:
            fail(f"The plugin did not answer on port {PORT} within {STARTUP_TIMEOUT} s.")

        if code != 403:
            fail(f"The page answered {code} without a ticket; the SDK's guard should answer 403.")

        # Still running after its first attempts at Gleanvolt failed.
        time.sleep(5)
        if process.poll() is not None:
            fail(f"The plugin exited with {process.returncode} after failing to reach Gleanvolt.")

        # A ticket sets the page's cookie; the cookie then admits the script.
        opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor())
        code, _ = status(f"http://127.0.0.1:{PORT}/?gvticket={ticket()}&culture=en", opener)
        if code != 200:
            fail(f"The page answered {code} with a ticket.")
        code, body = status(f"http://127.0.0.1:{PORT}/_framework/blazor.web.js", opener)
        if code != 200 or len(body) == 0:
            fail(f"Blazor's script answered {code} with {len(body)} bytes; the page would not be interactive.")

        print("The plugin starts, waits for Gleanvolt, and serves its page behind the ticket.")
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()


if __name__ == "__main__":
    main()
