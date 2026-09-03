#!/usr/bin/env python3
"""Start a published Gleanvolt build and prove it is actually runnable.

Nothing else in the pipeline executes a produced binary. The failures this catches are the ones a
compile cannot see: Blazor's client script missing from a cross-published self-contained output, a
native dependency that is absent on the target, an arm64 build that is quietly x64, or a binary that
reports a version nobody built.

It is deliberately one script for all three platforms rather than a shell script per runner. The four
assertions below are the contract, and two implementations of a contract drift.

The controller is pointed at an inverter and a charger that are not there, on purpose. Surviving that
is the first assertion: a controller that dies when its hardware is unreachable is broken in a way
worth failing a release for.
"""

import argparse
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request

# Fixed rather than free: a hardcoded port that is wrong fails loudly here, where an unused one
# chosen at random would make a failure depend on what else the runner happens to be doing.
PORT = 8099
KEY = "smoke-test-key-not-a-secret"

# Generous. A self-contained build on a cold arm64 runner is the slow case, and the cost of being
# wrong here is a flaky release gate.
STARTUP_TIMEOUT = 90


def environment(expected_version):
    """What the binary is told about the world. Everything outbound is switched off or unreachable."""
    env = dict(os.environ)
    env.update({
        # Not Development: appsettings.Development.json travels in the publish output, and a smoke
        # test that quietly ran under it would be testing a configuration nobody deploys.
        "DOTNET_ENVIRONMENT": "Production",
        "ASPNETCORE_ENVIRONMENT": "Production",

        # One socket serves both surfaces, so this is the port for the UI and the API alike.
        "Web__Enabled": "true",
        "Web__Port": str(PORT),

        # Off by default, and switched on here because /health lives on it. Enabled with no key is a
        # deliberate startup failure, so a key is not optional.
        "Api__Enabled": "true",
        f"Api__Keys__smoke": KEY,

        "Pv__Id": "smoke",
        "Pv__Name": "Smoke test",

        # Refused immediately rather than routed and dropped: the controller should handle a dead
        # inverter, and this makes it discover that in milliseconds instead of at a TCP timeout.
        "Pv__Inverter__Host": "127.0.0.1",
        "Pv__Inverter__Port": "1",
        "Pv__Chargers__0__Host": "127.0.0.1",
        "Pv__Chargers__0__Port": "1",

        # A named IANA zone rather than the default. This is the cheapest exercise of the ICU data a
        # self-contained build has to carry, and resolving it on Windows and on arm64 is exactly the
        # sort of thing that only fails on the target.
        "Controller__TimeZone": "Europe/Prague",

        # No outbound call may leave this test. Blanked rather than assumed absent: a developer
        # running this locally has a real .env somewhere above them, and Solcast's quota is small
        # enough that spending one here would be a genuine cost.
        "Solcast__ApiKey": "",
        "Weather__ApiKey": "",

        # No broker on the runner, and nothing to say to one.
        "HomeAssistant__Enabled": "false",
        "Vehicle__Enabled": "false",
    })
    return env


def get(path, key=None):
    """GET one path. Returns (status, body bytes) — an HTTP error is an answer, not an exception."""
    request = urllib.request.Request(f"http://127.0.0.1:{PORT}{path}")
    if key:
        request.add_header("Authorization", f"Bearer {key}")
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()
    except (urllib.error.URLError, ConnectionError, OSError):
        return None, b""


def wait_for_it(process, log_path):
    """Poll until the API answers, or the process dies, or we run out of patience."""
    deadline = time.monotonic() + STARTUP_TIMEOUT
    while time.monotonic() < deadline:
        if process.poll() is not None:
            fail(f"the process exited with code {process.returncode} during startup", log_path)
        status, _ = get("/api/v1/health", KEY)
        if status is not None:
            return
        time.sleep(1)
    fail(f"nothing answered on port {PORT} within {STARTUP_TIMEOUT}s", log_path)


def fail(message, log_path):
    print(f"\nSMOKE TEST FAILED: {message}\n", file=sys.stderr)
    print("--- captured output ---", file=sys.stderr)
    print(read(log_path), file=sys.stderr)
    sys.exit(1)


def read(log_path):
    try:
        with open(log_path, "r", encoding="utf-8", errors="replace") as handle:
            return handle.read()
    except OSError:
        return "(no output captured)"


def startup_line(log_path):
    """The line the worker logs before anything can go wrong: `Gleanvolt <version> (<sha>) starting.`"""
    for line in read(log_path).splitlines():
        if "Gleanvolt " in line and " starting." in line:
            return line
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", required=True, help="the published Gleanvolt.Worker executable")
    parser.add_argument("--version", required=True, help="the version this run built, e.g. 1.0.7")
    arguments = parser.parse_args()

    executable = os.path.abspath(arguments.exe)
    if not os.path.isfile(executable):
        print(f"SMOKE TEST FAILED: no executable at {executable}", file=sys.stderr)
        return 1

    # Run from beside the binary: the log file and the two SQLite stores are opened relative to the
    # working directory, and opening them is part of what is being proved.
    working_directory = os.path.dirname(executable)
    log_path = os.path.join(working_directory, "smoke-test-output.log")

    print(f"Starting {executable}")
    with open(log_path, "wb") as sink:
        process = subprocess.Popen(
            [executable],
            cwd=working_directory,
            env=environment(arguments.version),
            stdout=sink,
            stderr=subprocess.STDOUT,
        )

    try:
        wait_for_it(process, log_path)

        # 1. Still alive. Everything below would also fail if it were not, but this says why.
        if process.poll() is not None:
            fail(f"the process exited with code {process.returncode} after answering", log_path)
        print("ok: the process survived an unreachable inverter and charger")

        # 2. The health endpoint answers. Not "reports healthy" -- it cannot be, with no inverter to
        #    poll. That it answers at all is the liveness claim being made here.
        status, body = get("/api/v1/health", KEY)
        if status != 200:
            fail(f"/api/v1/health answered {status}, not 200", log_path)
        print("ok: /api/v1/health answered 200")

        # 3. Blazor's client script. A 404 means the pages render once and then sit dead. A zero-byte
        #    200 means the same thing and is the harder one to notice, so length is checked too.
        status, script = get("/_framework/blazor.web.js")
        if status != 200:
            fail(f"/_framework/blazor.web.js answered {status}, not 200 -- the web UI would never open a circuit", log_path)
        if len(script) == 0:
            fail("/_framework/blazor.web.js answered 200 with an empty body, which is the same dead page as a 404", log_path)
        print(f"ok: /_framework/blazor.web.js answered 200 with {len(script)} bytes")

        # 4. The version that was built is the version that is running -- asserted in both places it
        #    appears, because they are stamped by different mechanisms and either can be the one that
        #    is wrong.
        try:
            reported = json.loads(body).get("version") or ""
        except (ValueError, AttributeError):
            fail("/api/v1/health did not return readable JSON", log_path)
        # BuildInfo.Describe(), so "1.0.7 (31bf347)" rather than "1.0.7" -- the commit is appended
        # whenever the build was stamped with one, which in CI is always. Compare the version alone;
        # the sha is not this test's business and pinning it here would fail on every commit.
        if reported.split(" ")[0] != arguments.version:
            fail(f"/api/v1/health reports version {reported!r}, but this run built {arguments.version!r}", log_path)

        line = startup_line(log_path)
        if line is None:
            fail("the worker never logged its startup line", log_path)
        if f"Gleanvolt {arguments.version} " not in line:
            fail(f"the startup line does not carry {arguments.version!r}: {line.strip()!r}", log_path)
        print(f"ok: running build reports {arguments.version} in the log and on /health")

        print("\n--- startup output ---")
        print(read(log_path))
        return 0
    finally:
        process.terminate()
        try:
            process.wait(timeout=30)
        except subprocess.TimeoutExpired:
            process.kill()


if __name__ == "__main__":
    sys.exit(main())
