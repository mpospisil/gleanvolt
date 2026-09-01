#!/usr/bin/env python3
"""
Phase 0 of issue #137 (issue #138): how long does a session against VW's EU Data Act portal
actually live, what does it cost to get a new one, and how does the datadelivery endpoint fail?

THROWAWAY. This is a measuring instrument, not a component. It does not ship, nothing in the
controller imports it, and it is meant to be deleted the day #138 closes -- see the README.

The three answers it exists to produce, all of which need wall-clock time rather than cleverness:

  1. Session lifetime -- how long a jar survives, and whether that is a fixed TTL or an idle
     timeout. A poll every 15 minutes settles the second half: a session that dies *under* a
     steady poll cannot be dying of idleness (unless the idle window is under 15 minutes), so an
     expiry seen here is a fixed TTL. `watch --idle-first N` sits quiet for N hours before
     polling, which is the other half of that experiment.
  2. Cost of re-login -- whether replaying the identifier -> password form is enough on its own,
     or whether a consent screen, an email OTP or a CAPTCHA appears. An OTP ends #137's
     unattended design outright, and that is worth knowing in week one.
  3. Failure modes under a real poll -- what the endpoint does when it is unhappy, whether 5xx is
     transient or sticky, and whether anything rate-limits. Whatever comes back becomes Phase 1's
     backoff policy instead of a production surprise.

Nothing sensitive reaches the log. Response bodies are never written -- they carry the VIN, the
odometer and the car's location -- and neither are cookie values or the password. What is written
is a classification, a status, a content type, a byte count, and for HTML the list of keywords
that matched. VINs are masked to their last four characters. The log is meant to be pasteable
into an issue.

Stdlib only, on purpose: throwaway code that needs a virtualenv is throwaway code nobody runs.

  export VW_USERNAME='you@example.com'
  read -rs VW_PASSWORD && export VW_PASSWORD

  ./session_probe.py login                     # form replay once; seeds cookies.txt
  ./session_probe.py discover                  # VINs and dataset request ids, masked
  ./session_probe.py watch --vin … --request-id …   # the long run; leave it going for days
  ./session_probe.py fields --vin … --request-id …  # does settings.target_soc actually arrive?
  ./session_probe.py report                    # the three answers, out of the log
"""

from __future__ import annotations

import argparse
import getpass
import gzip
import io
import json
import os
import ssl
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from datetime import datetime, timedelta, timezone
from html.parser import HTMLParser
from http.cookiejar import MozillaCookieJar, LoadError

# Overridable so the measurement loop itself can be exercised against a stub server -- see the
# README's "Check it before you trust it". Against the real portal this is never set.
PORTAL = os.environ.get("VW_PORTAL_BASE", "https://eu-data-act.drivesomethinggreater.com").rstrip("/")

# The portal's own landing page. Locale is in the path; /de/en is what issue #137 records for this
# site, and it only decides which language the IdP renders its forms in.
LOGIN_PAGE = f"{PORTAL}/de/en/login"

VEHICLES_PATH = "/proxy_api/consent/me/vehicles"

HERE = os.path.dirname(os.path.abspath(__file__))
COOKIE_FILE = os.path.join(HERE, "cookies.txt")
LOG_FILE = os.path.join(HERE, "probe.jsonl")

# A real browser's UA. Not evasion -- the IdP serves a different (and sometimes broken) page to
# clients it does not recognise, and a spike that measured *that* page would be measuring itself.
USER_AGENT = (
    "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) "
    "Chrome/126.0.0.0 Safari/537.36"
)

# What an HTML body is scanned for when JSON was expected. Any hit is a human step, which is the
# single most consequential thing this spike can find: #137's whole unattended design assumes none
# of these ever appears in steady state.
INTERSTITIAL_KEYWORDS = {
    "captcha": ("captcha", "recaptcha", "hcaptcha", "turnstile"),
    "otp": ("one-time", "onetime", "verification code", "security code", "einmalcode"),
    "mfa": ("two-factor", "2fa", "multi-factor", "authenticator"),
    "consent": ("consent", "einwilligung", "terms and conditions", "privacy policy"),
    "login": ("password", "passwort", "sign in", "anmelden", "identifier"),
}


def now() -> datetime:
    return datetime.now(timezone.utc)


def stamp(when: datetime) -> str:
    return when.isoformat(timespec="seconds")


def mask_vin(vin: str) -> str:
    """A VIN identifies a car and its owner. The last four are enough to tell two cars apart."""
    return f"…{vin[-4:]}" if len(vin) > 4 else "…"


def log(event: str, **fields) -> dict:
    """One append-only line per thing that happened. Append, so a restart continues the run."""
    record = {"at": stamp(now()), "event": event, **fields}

    with open(LOG_FILE, "a", encoding="utf-8") as handle:
        handle.write(json.dumps(record, ensure_ascii=False) + "\n")

    print(f"{record['at']}  {event:<12} " + "  ".join(
        f"{key}={value}" for key, value in fields.items() if key not in ("steps", "form")),
        flush=True)

    return record


class FormParser(HTMLParser):
    """
    The first <form> on a page, with every input it carries.

    Every hidden field is replayed verbatim rather than being named in code: the IdP's own
    templateModel supplies `hmac`, `_csrf` and `relayState`, and a spike that hard-coded that list
    would break silently the day VW adds a fourth. What the log records is the field *names* it
    found, which is exactly the documentation Phase 1 needs.
    """

    def __init__(self) -> None:
        super().__init__()
        self.action: str | None = None
        self.method = "post"
        self.fields: dict[str, str] = {}
        self.types: dict[str, str] = {}
        self._in_form = False
        self._done = False

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if self._done:
            return

        attributes = {key.lower(): (value or "") for key, value in attrs}

        if tag == "form" and not self._in_form:
            self._in_form = True
            self.action = attributes.get("action")
            self.method = attributes.get("method", "post").lower()
        elif tag == "input" and self._in_form:
            name = attributes.get("name")
            if name:
                self.fields[name] = attributes.get("value", "")
                self.types[name] = attributes.get("type", "text").lower()

    def handle_endtag(self, tag: str) -> None:
        if tag == "form" and self._in_form:
            self._in_form = False
            self._done = True

    @staticmethod
    def parse(body: str) -> "FormParser":
        parser = FormParser()
        parser.feed(body)
        return parser

    def field_named(self, *candidates: str) -> str | None:
        """The first field whose name or type matches, so the flow survives a rename."""
        for candidate in candidates:
            for name, kind in self.types.items():
                if candidate in (name.lower(), kind):
                    return name
        return None


class Response:
    """What came back, classified. The body is kept in memory and never written to the log."""

    def __init__(self, url: str, status: int, headers, body: bytes) -> None:
        self.url = url
        self.status = status
        self.headers = headers
        self.body = body
        self.content_type = (headers.get("Content-Type") or "").split(";")[0].strip().lower()

    @property
    def text(self) -> str:
        return self.body.decode("utf-8", errors="replace")

    def json(self):
        return json.loads(self.body.decode("utf-8"))

    def keywords(self) -> list[str]:
        """Which human-step keywords the body contains. Only meaningful for HTML."""
        lowered = self.text.lower()
        return sorted(
            label for label, needles in INTERSTITIAL_KEYWORDS.items()
            if any(needle in lowered for needle in needles))

    def classify(self) -> str:
        """
        The one judgement this spike turns on: is the session still good?

        The three shapes issue #138 names -- a 401, a bounce to /login, and HTML where JSON was
        expected -- all mean the same thing operationally and are still recorded separately,
        because "which of them does it actually do?" is what Phase 1 has to detect on.
        """
        if self.status == 401 or self.status == 403:
            return "unauthorised"

        if "/login" in urllib.parse.urlparse(self.url).path:
            return "login-redirect"

        if self.status >= 500:
            return "server-error"

        if self.status >= 400:
            return "client-error"

        if "json" not in self.content_type:
            return "html-instead-of-json"

        return "ok"


class Portal:
    """A cookie jar and the three requests this spike makes with it."""

    def __init__(self) -> None:
        self.jar = MozillaCookieJar(COOKIE_FILE)

        if os.path.exists(COOKIE_FILE):
            try:
                # Session cookies are what this whole spike is about, so they must survive a restart
                # of the script even though a browser would drop them.
                self.jar.load(ignore_discard=True, ignore_expires=True)
            except LoadError:
                print(f"warning: {COOKIE_FILE} is not a Netscape cookie file; starting empty",
                      file=sys.stderr)

        self.opener = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(self.jar),
            urllib.request.HTTPSHandler(context=ssl.create_default_context()))

    def save(self) -> None:
        self.jar.save(ignore_discard=True, ignore_expires=True)

    def request(self, url: str, data: dict | None = None, referer: str | None = None) -> Response:
        body = urllib.parse.urlencode(data).encode() if data is not None else None
        headers = {
            "User-Agent": USER_AGENT,
            "Accept": "application/json, text/html;q=0.9,*/*;q=0.8",
            "Accept-Encoding": "gzip",
            "Accept-Language": "en-GB,en;q=0.9",
        }

        if referer:
            headers["Referer"] = referer
        if body is not None:
            headers["Content-Type"] = "application/x-www-form-urlencoded"

        request = urllib.request.Request(url, data=body, headers=headers)

        try:
            with self.opener.open(request, timeout=45) as raw:
                return Response(raw.geturl(), raw.status, raw.headers, _read(raw))
        except urllib.error.HTTPError as error:
            # An error status is data here, not an exception: 400s and 500s from the datadelivery
            # endpoint are precisely one of the three things being measured.
            with error:
                return Response(error.geturl(), error.code, error.headers, _read(error))
        finally:
            self.save()


def _read(raw) -> bytes:
    payload = raw.read()

    if (raw.headers.get("Content-Encoding") or "").lower() == "gzip":
        return gzip.decompress(payload)

    return payload


def do_login(portal: Portal, username: str, password: str) -> dict:
    """
    Replay the identifier -> password form flow, and record every step of it.

    This is question 2, and the answer is whatever actually happens: if a consent screen, an OTP or
    a CAPTCHA appears, the log says which page it appeared on and what keywords matched, and #137's
    unattended design has its answer. A failure here is a result, not a crash.
    """
    steps: list[dict] = []
    started = now()

    page = portal.request(LOGIN_PAGE)
    steps.append({"step": "landing", "status": page.status, "url": _host_and_path(page.url)})

    # Two form posts: the identifier page, then the password page. Written as a loop rather than as
    # two blocks because the IdP has been known to interpose an extra page, and a loop records that
    # rather than mistaking it for the password form.
    for attempt in range(4):
        form = FormParser.parse(page.text)

        if not form.action:
            steps.append({"step": f"page-{attempt}", "outcome": "no-form",
                          "keywords": page.keywords(), "url": _host_and_path(page.url)})
            break

        identifier_field = form.field_named("identifier", "email", "username")
        password_field = form.field_named("password")

        if password_field:
            form.fields[password_field] = password
            kind = "password"
        elif identifier_field:
            form.fields[identifier_field] = username
            kind = "identifier"
        else:
            steps.append({"step": f"page-{attempt}", "outcome": "unfillable-form",
                          "fields": sorted(form.fields), "keywords": page.keywords(),
                          "url": _host_and_path(page.url)})
            break

        target = urllib.parse.urljoin(page.url, form.action)
        steps.append({
            "step": f"post-{kind}",
            "url": _host_and_path(target),
            # The hidden fields the IdP wanted, which is the documentation Phase 1 needs. Names
            # only -- their values are the session's, and one of them is a CSRF token.
            "fields": sorted(name for name in form.fields
                             if name not in (identifier_field, password_field)),
        })

        page = portal.request(target, data=form.fields, referer=page.url)
        steps[-1]["status"] = page.status

        # Landing back on the portal is not proof of a session -- an interstitial lives there too --
        # so success is defined as the thing the session is *for* answering. One request, and it
        # makes an unattended run self-verifying rather than optimistic.
        if _is_portal(page.url) and "/login" not in urllib.parse.urlparse(page.url).path:
            verdict = portal.request(PORTAL + VEHICLES_PATH).classify()
            steps.append({"step": "verify", "verdict": verdict})

            if verdict == "ok":
                return log("login", outcome="ok", seconds=int((now() - started).total_seconds()),
                           landed=_host_and_path(page.url), steps=steps)

    return log("login", outcome="incomplete", seconds=int((now() - started).total_seconds()),
               landed=_host_and_path(page.url), keywords=page.keywords(), steps=steps)


def _host_and_path(url: str) -> str:
    parts = urllib.parse.urlparse(url)
    return f"{parts.netloc}{parts.path}"


def _is_portal(url: str) -> bool:
    """Whether the flow has landed back on the portal, which is what "signed in" looks like."""
    return urllib.parse.urlparse(url).netloc == urllib.parse.urlparse(PORTAL).netloc


def do_discover(portal: Portal) -> None:
    """
    The VINs this account can see, and whatever ids hang off them, masked.

    Printed as a shape rather than as a payload: the response carries the car's identity, and the
    only thing needed from it is which two strings go in the `watch` command.
    """
    response = portal.request(PORTAL + VEHICLES_PATH)
    verdict = response.classify()

    log("discover", status=response.status, verdict=verdict, bytes=len(response.body),
        content_type=response.content_type)

    if verdict != "ok":
        print("Not signed in, or the endpoint moved. Run `login` first.", file=sys.stderr)
        return

    print(json.dumps(_masked(response.json()), indent=2, ensure_ascii=False))


def _masked(value, key: str = ""):
    """Recursively mask anything that looks like it identifies the car or its owner."""
    if isinstance(value, dict):
        return {name: _masked(item, name) for name, item in value.items()}

    if isinstance(value, list):
        return [_masked(item) for item in value]

    lowered = key.lower()

    if isinstance(value, str) and any(word in lowered for word in ("vin", "mail", "name", "address")):
        return mask_vin(value) if "vin" in lowered else "…"

    return value


def dataset_url(vin: str, request_id: str) -> str:
    return f"{PORTAL}/proxy_api/euda-apim/datadelivery/vehicles/{vin}/{request_id}/list"


def do_watch(portal: Portal, args) -> None:
    """
    The long run. Poll, classify, and when the session dies write down when -- then try to get a
    new one and keep going, so a second expiry arrives without anyone sitting up for it.
    """
    url = dataset_url(args.vin, args.request_id)
    interval = timedelta(minutes=args.every)

    log("start", vin=mask_vin(args.vin), every_minutes=args.every,
        idle_first_hours=args.idle_first, relogin="yes" if args.username else "no")

    if args.idle_first:
        # The other half of question 1. A session that survives a steady poll and dies after a
        # quiet stretch is an idle timeout; one that dies either way is a TTL.
        log("idle", hours=args.idle_first)
        time.sleep(args.idle_first * 3600)

    alive_since = now()
    was_alive = True

    while True:
        response = portal.request(url)
        verdict = response.classify()
        healthy = verdict == "ok"

        entry = {
            "status": response.status,
            "verdict": verdict,
            "bytes": len(response.body),
            "content_type": response.content_type,
        }

        if not healthy and "html" in response.content_type:
            entry["keywords"] = response.keywords()

        log("poll", **entry)

        if was_alive and not healthy:
            # The measurement this whole script exists for.
            lived = now() - alive_since
            log("expiry", verdict=verdict, status=response.status,
                lived_hours=round(lived.total_seconds() / 3600, 2),
                since=stamp(alive_since))

            if args.username:
                outcome = do_login(portal, args.username, args.password)

                if outcome["outcome"] == "ok":
                    alive_since = now()
                    was_alive = True
                    time.sleep(args.every * 60)
                    continue

                print("Re-login did not complete; see the log. Stopping so the page can be "
                      "inspected by hand while it is still failing.", file=sys.stderr)
                return

            print("Session is gone and no credentials were given. Sign in again and restart, or "
                  "pass --username to measure re-login too.", file=sys.stderr)
            return

        if healthy and not was_alive:
            alive_since = now()

        was_alive = healthy
        time.sleep(interval.total_seconds())


def do_fields(portal: Portal, args) -> None:
    """
    Free while we are in here (issue #138's postscript): does `settings.target_soc` /
    `remaining_charging_time_target_soc` actually arrive for this car, or is it null the way the
    WeConnect field was? #101 deferred a gate on exactly that.

    Field *names* only. The values are the car's, and this prints to a terminal.
    """
    listing = portal.request(dataset_url(args.vin, args.request_id))

    if listing.classify() != "ok":
        log("fields", outcome="not-signed-in", status=listing.status)
        return

    datasets = listing.json()
    entries = datasets if isinstance(datasets, list) else datasets.get("items") or datasets.get("data") or []

    if not entries:
        log("fields", outcome="no-datasets")
        print(json.dumps(_masked(datasets), indent=2)[:2000])
        return

    newest = entries[0]
    download = newest.get("downloadUrl") or newest.get("url") or newest.get("href")

    if not download:
        log("fields", outcome="no-download-url", keys=sorted(newest))
        return

    archive = portal.request(urllib.parse.urljoin(PORTAL, download))
    log("fields", outcome="downloaded", bytes=len(archive.body),
        content_type=archive.content_type)

    names: set[str] = set()

    if not zipfile.is_zipfile(io.BytesIO(archive.body)):
        # A bounce to /login, an error page, or a format change. All three are worth recording as
        # themselves rather than as a stack trace.
        log("fields", outcome="not-a-zip", verdict=archive.classify(),
            content_type=archive.content_type, bytes=len(archive.body))
        return

    with zipfile.ZipFile(io.BytesIO(archive.body)) as bundle:
        for member in bundle.namelist():
            if not member.lower().endswith(".json"):
                print(f"  {member}  (not JSON; opened by hand)")
                continue

            with bundle.open(member) as handle:
                _collect_names(json.load(handle), names)

    interesting = sorted(name for name in names if "soc" in name.lower()
                         or "charg" in name.lower() or "target" in name.lower())

    print("\nFields mentioning soc / charge / target:")
    for name in interesting:
        print(f"  {name}")

    log("fields", outcome="ok", total=len(names), interesting=interesting)


def _collect_names(value, into: set[str], prefix: str = "") -> None:
    if isinstance(value, dict):
        for key, item in value.items():
            path = f"{prefix}.{key}" if prefix else key
            into.add(path)
            _collect_names(item, into, path)
    elif isinstance(value, list):
        for item in value[:3]:
            _collect_names(item, into, prefix)


def do_report() -> None:
    """
    The log, turned into the three answers issue #138 asks for. This is the part that gets
    transcribed into docs/DECISIONS.md.
    """
    if not os.path.exists(LOG_FILE):
        print("No log yet. Run `watch` first.")
        return

    with open(LOG_FILE, encoding="utf-8") as handle:
        records = [json.loads(line) for line in handle if line.strip()]

    expiries = [record for record in records if record["event"] == "expiry"]
    logins = [record for record in records if record["event"] == "login"]
    polls = [record for record in records if record["event"] == "poll"]

    print("\n1. Session lifetime")
    if not expiries:
        first = records[0]["at"] if records else "—"
        print(f"   No expiry seen yet. Polling since {first}, {len(polls)} polls.")
    else:
        for index, expiry in enumerate(expiries, start=1):
            print(f"   #{index}: {expiry['lived_hours']} h "
                  f"({expiry['since']} → {expiry['at']}), died as {expiry['verdict']}")

        if len(expiries) >= 2:
            spread = max(e["lived_hours"] for e in expiries) - min(e["lived_hours"] for e in expiries)
            print(f"   Spread between observations: {round(spread, 2)} h — a tight spread reads as "
                  f"a fixed TTL, a wide one as something else.")
        else:
            print("   One observation is a coincidence. Keep it running for a second.")

    print("\n2. Cost of re-login")
    if not logins:
        print("   Never attempted.")
    for login_record in logins:
        detail = f"   {login_record['at']}: {login_record['outcome']} in {login_record['seconds']} s"
        if login_record.get("keywords"):
            detail += f" — page mentioned {', '.join(login_record['keywords'])}"
        print(detail)

    human = sorted({word for record in logins for word in record.get("keywords", [])}
                   & {"captcha", "otp", "mfa"})
    if human:
        print(f"   *** A human step appeared: {', '.join(human)}. #137's unattended design does "
              f"not survive this as written. ***")
    elif logins:
        print("   No CAPTCHA, OTP or MFA keyword seen on any attempt.")

    print("\n3. Failure modes under a real poll")
    tally: dict[str, int] = {}
    for poll in polls:
        key = f"{poll['verdict']} ({poll['status']})"
        tally[key] = tally.get(key, 0) + 1

    for key, count in sorted(tally.items(), key=lambda item: -item[1]):
        share = 100 * count / len(polls)
        print(f"   {key:<28} {count:>5}  ({share:.1f}%)")

    print(f"\n   {_longest_run(polls)}")


def _longest_run(polls: list[dict]) -> str:
    """Whether a 5xx is transient or sticky, which is the whole of Phase 1's backoff decision."""
    longest = 0
    current = 0

    for poll in polls:
        if poll["verdict"] == "server-error":
            current += 1
            longest = max(longest, current)
        else:
            current = 0

    if longest == 0:
        return "No 5xx seen at all."

    return (f"Longest unbroken run of 5xx: {longest} polls — one is transient, a run is sticky and "
            f"means Phase 1 backs off rather than retries.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[1])
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("login", help="replay the form flow once and record what it cost")
    sub.add_parser("discover", help="list the VINs and ids this account can see, masked")
    sub.add_parser("report", help="turn the log into the three answers")

    watch = sub.add_parser("watch", help="poll until the session dies, then get a new one")
    watch.add_argument("--vin", required=True)
    watch.add_argument("--request-id", required=True)
    watch.add_argument("--every", type=int, default=15, metavar="MINUTES",
                       help="poll interval; 15 matches the portal's own batch cadence")
    watch.add_argument("--idle-first", type=int, default=0, metavar="HOURS",
                       help="sit quiet this long before polling, to separate an idle timeout "
                            "from a fixed TTL")

    fields = sub.add_parser("fields", help="does settings.target_soc actually arrive?")
    fields.add_argument("--vin", required=True)
    fields.add_argument("--request-id", required=True)

    for command in (watch,):
        command.add_argument("--username", default=os.environ.get("VW_USERNAME"),
                             help="enables re-login on expiry; VW_USERNAME by default")

    args = parser.parse_args()

    if args.command in ("login",) or getattr(args, "username", None):
        args.username = getattr(args, "username", None) or os.environ.get("VW_USERNAME")

        if not args.username:
            args.username = input("VW ID email: ").strip()

        args.password = os.environ.get("VW_PASSWORD") or getpass.getpass("VW ID password: ")
    else:
        args.password = None

    portal = Portal()

    if args.command == "login":
        do_login(portal, args.username, args.password)
    elif args.command == "discover":
        do_discover(portal)
    elif args.command == "watch":
        do_watch(portal, args)
    elif args.command == "fields":
        do_fields(portal, args)
    elif args.command == "report":
        do_report()

    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        # Stopping the watch by hand is the normal way it ends, and the log is already on disk.
        print("\nStopped. The log is intact; `report` reads it.", file=sys.stderr)
        sys.exit(130)
