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

Two ways to run it, with the same four assertions:

  --exe <path>        start the binary from a publish folder or an unpacked zip
  --service <unit>    check a systemd service that is already running -- the installed .deb (#205),
                      configured with the settings `--print-env` prints

The second exists because a package can be broken in ways the binary is not: a dependency the
package does not declare, a unit that points at the wrong content root, a data path the service user
cannot write. Only starting the installed service shows those.
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


def settings():
    """What the binary is told about the world. Everything outbound is switched off or unreachable."""
    return {
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
    }


def environment():
    """The process environment for --exe: this runner's, with the settings above on top."""
    env = dict(os.environ)
    env.update(settings())
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


class Process:
    """A binary this script starts itself (--exe). Its output goes to a file beside it."""

    def __init__(self, executable, log_path):
        self.executable = executable
        self.log_path = log_path
        self.process = None

    def start(self):
        print(f"Starting {self.executable}")
        # Run from beside the binary: the log file and the two SQLite stores are opened relative to
        # the working directory, and opening them is part of what is being proved.
        with open(self.log_path, "wb") as sink:
            self.process = subprocess.Popen(
                [self.executable],
                cwd=os.path.dirname(self.executable),
                env=environment(),
                stdout=sink,
                stderr=subprocess.STDOUT,
            )

    def died(self):
        """None while running, otherwise a sentence saying how it ended."""
        if self.process.poll() is None:
            return None
        return f"the process exited with code {self.process.returncode}"

    def output(self):
        try:
            with open(self.log_path, "r", encoding="utf-8", errors="replace") as handle:
                return handle.read()
        except OSError:
            return "(no output captured)"

    def stop(self):
        self.process.terminate()
        try:
            self.process.wait(timeout=30)
        except subprocess.TimeoutExpired:
            self.process.kill()


class Service:
    """A systemd unit that is already running (--service). Started, and stopped, by its package."""

    def __init__(self, unit):
        self.unit = unit

    def start(self):
        print(f"Checking the running service {self.unit}")

    def show(self, prop):
        return subprocess.run(
            ["systemctl", "show", "--value", "-p", prop, self.unit],
            capture_output=True, text=True,
        ).stdout.strip()

    def died(self):
        state = self.show("ActiveState")
        if state == "active":
            return None
        return f"the service is {state} (result {self.show('Result')}, exit status {self.show('ExecMainStatus')})"

    def output(self):
        # This run of the unit only, so an earlier start with a different configuration cannot supply
        # the startup line being asserted.
        invocation = self.show("InvocationID")
        selector = [f"_SYSTEMD_INVOCATION_ID={invocation}"] if invocation else ["-u", self.unit]
        result = subprocess.run(
            ["journalctl", "--no-pager", "-o", "cat", *selector],
            capture_output=True, text=True,
        )
        return result.stdout or result.stderr or "(no output captured)"

    def stop(self):
        # Not ours to stop: the workflow that installed it removes it.
        pass


def wait_for_it(target):
    """Poll until the API answers, or the target dies, or we run out of patience."""
    deadline = time.monotonic() + STARTUP_TIMEOUT
    while time.monotonic() < deadline:
        ended = target.died()
        if ended is not None:
            fail(f"{ended} during startup", target)
        status, _ = get("/api/v1/health", KEY)
        if status is not None:
            return
        time.sleep(1)
    fail(f"nothing answered on port {PORT} within {STARTUP_TIMEOUT}s", target)


def fail(message, target):
    print(f"\nSMOKE TEST FAILED: {message}\n", file=sys.stderr)
    print("--- captured output ---", file=sys.stderr)
    print(target.output(), file=sys.stderr)
    sys.exit(1)


def startup_line(target):
    """The line the worker logs before anything can go wrong: `Gleanvolt <version> (<sha>) starting.`"""
    for line in target.output().splitlines():
        if "Gleanvolt " in line and " starting." in line:
            return line
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    what = parser.add_mutually_exclusive_group(required=True)
    what.add_argument("--exe", help="the published Gleanvolt.Worker executable, started by this script")
    what.add_argument("--service", help="a systemd unit already running with the --print-env settings")
    what.add_argument("--print-env", action="store_true",
                      help="print the smoke-test settings as KEY=VALUE lines, for a systemd EnvironmentFile, and exit")
    parser.add_argument("--version", help="the version this run built, e.g. 1.0.7")
    arguments = parser.parse_args()

    if arguments.print_env:
        for key, value in settings().items():
            print(f"{key}={value}")
        return 0

    if not arguments.version:
        parser.error("--version is required with --exe and --service")

    if arguments.exe:
        executable = os.path.abspath(arguments.exe)
        if not os.path.isfile(executable):
            print(f"SMOKE TEST FAILED: no executable at {executable}", file=sys.stderr)
            return 1
        target = Process(executable, os.path.join(os.path.dirname(executable), "smoke-test-output.log"))
    else:
        target = Service(arguments.service)

    target.start()

    try:
        wait_for_it(target)

        # 1. Still alive. Everything below would also fail if it were not, but this says why.
        ended = target.died()
        if ended is not None:
            fail(f"{ended} after answering", target)
        print("ok: the process survived an unreachable inverter and charger")

        # 2. The health endpoint answers. Not "reports healthy" -- it cannot be, with no inverter to
        #    poll. That it answers at all is the liveness claim being made here.
        status, body = get("/api/v1/health", KEY)
        if status != 200:
            fail(f"/api/v1/health answered {status}, not 200", target)
        print("ok: /api/v1/health answered 200")

        # 3. Blazor's client script. A 404 means the pages render once and then sit dead. A zero-byte
        #    200 means the same thing and is the harder one to notice, so length is checked too.
        status, script = get("/_framework/blazor.web.js")
        if status != 200:
            fail(f"/_framework/blazor.web.js answered {status}, not 200 -- the web UI would never open a circuit", target)
        if len(script) == 0:
            fail("/_framework/blazor.web.js answered 200 with an empty body, which is the same dead page as a 404", target)
        print(f"ok: /_framework/blazor.web.js answered 200 with {len(script)} bytes")

        # 4. The version that was built is the version that is running -- asserted in both places it
        #    appears, because they are stamped by different mechanisms and either can be the one that
        #    is wrong.
        try:
            reported = json.loads(body).get("version") or ""
        except (ValueError, AttributeError):
            fail("/api/v1/health did not return readable JSON", target)
        # BuildInfo.Describe(), so "1.0.7 (31bf347)" rather than "1.0.7" -- the commit is appended
        # whenever the build was stamped with one, which in CI is always. Compare the version alone;
        # the sha is not this test's business and pinning it here would fail on every commit.
        if reported.split(" ")[0] != arguments.version:
            fail(f"/api/v1/health reports version {reported!r}, but this run built {arguments.version!r}", target)

        line = startup_line(target)
        if line is None:
            fail("the worker never logged its startup line", target)
        if f"Gleanvolt {arguments.version} " not in line:
            fail(f"the startup line does not carry {arguments.version!r}: {line.strip()!r}", target)
        print(f"ok: running build reports {arguments.version} in the log and on /health")

        print("\n--- startup output ---")
        print(target.output())
        return 0
    finally:
        target.stop()


if __name__ == "__main__":
    sys.exit(main())
