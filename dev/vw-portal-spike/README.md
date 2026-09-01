# Spike: how long does a VW portal session actually live?

**Phase 0 of [#137](https://github.com/mpospisil/gleanvolt/issues/137), which is
[#138](https://github.com/mpospisil/gleanvolt/issues/138). Throwaway. Delete this whole folder the
day #138 closes.**

Nothing in the controller imports it, nothing builds it, and `.dockerignore` already excludes `dev/`.
It is a measuring instrument, and its only output is three paragraphs in `docs/DECISIONS.md`.

## The one question, and why it blocks everything

Does a session against `eu-data-act.drivesomethinggreater.com` survive unattended, and for how long?

The ~15 hour expiry we keep quoting is from [#73](https://github.com/mpospisil/gleanvolt/issues/73),
and it is a property of `volkswagen_connect`'s **reverse-engineered app client** — not evidence about
the official portal, which is a different backend reached a different way. Nobody has measured the
portal. #137's authorisation decision (VW ID password at rest on the Pi, headless form replay) rests
entirely on the answer:

- **Sessions last weeks** → the stored password stops being load-bearing, and #137's decision should
  be *reopened* rather than honoured out of habit.
- **Sessions die in hours** → headless re-login is the only thing that makes the feature usable, and
  the password is the price. Confirm the decision and move on.

Both are fine. Guessing is not.

## Running it

Python 3 and nothing else — no virtualenv, no packages. Throwaway code that needs an install is
throwaway code nobody runs.

```bash
cd dev/vw-portal-spike

export VW_USERNAME='you@example.com'
read -rs VW_PASSWORD && export VW_PASSWORD      # not in shell history, not in a file

./session_probe.py login                        # replay the form flow once; seeds cookies.txt
./session_probe.py discover                     # the VINs and request ids, masked
./session_probe.py watch --vin WVW… --request-id … --username "$VW_USERNAME"
```

Leave `watch` running for **days**, not hours — the point is to see the expiry *twice*, so the first
one is not mistaken for a coincidence. It polls every 15 minutes (`--every`), matching the portal's
own batch cadence, notices the first bad response, writes down how long the session lived, gets a new
one, and carries on. Run it in `tmux` on the Pi, or under `nohup`; the log is appended, so a restart
continues the same run.

Then, at any point:

```bash
./session_probe.py report        # the log, turned into the three answers
```

Two more, both worth doing once:

```bash
# Is it a fixed TTL or an idle timeout? A session that dies under a steady poll cannot be dying of
# idleness, so `watch` alone answers "TTL". This answers the other half: sit quiet, then poll.
./session_probe.py watch --vin … --request-id … --idle-first 20

# Free while we are in here: does settings.target_soc actually arrive for this car, or is it null
# the way the WeConnect field was? #101 deferred a gate on exactly that.
./session_probe.py fields --vin … --request-id …
```

### Check it before you trust it

```bash
./selftest.py
```

Runs the probe against a stand-in portal and identity provider on localhost, shaped the way #137
describes the real pair: two form pages, a redirect back to the portal's own `/login`, and data
endpoints that 401 once the session expires. It asserts that the probe completes a login, notices an
expiry, recovers from it unattended, notices the *next* one, and finds `settings.target_soc` in a
downloaded bundle. Three days is a long time to discover the classifier was wrong.

It cannot tell you anything about how VW's IdP really behaves. That is what the real run is for.

## What it writes down, and what it deliberately does not

`probe.jsonl` (append-only) and `cookies.txt` are produced beside the script and are **gitignored**.

Response bodies are never logged — they carry the VIN, the odometer and the car's location. Neither
are cookie values or the password. What is written is a classification, a status, a content type, a
byte count, and for HTML the list of keywords that matched. VINs are masked to their last four
characters. **The log is meant to be pasteable into an issue**; check it once before you paste it
anyway.

The password is read from `VW_PASSWORD` or prompted for. It is never written to disk by this script.
`cookies.txt` *is* a live credential while the session lasts — treat it like `.env`, and delete it
along with the folder.

## The answers this has to produce

Transcribe into `docs/DECISIONS.md` as prose beside #73's original reasoning. `report` prints the
first three; the fourth is a judgement.

1. **Session lifetime.** How long, and fixed TTL or idle timeout. Two observations minimum, with the
   spread between them — a tight spread reads as a TTL.
2. **Cost of re-login.** Does replaying the form suffice on its own? Did a consent screen reappear?
   Did an **email OTP or a CAPTCHA** ever appear — because that ends #137's unattended design
   outright, and it is worth knowing in week one rather than month three.
3. **Failure modes under a real poll.** What the datadelivery endpoint does when unhappy: the tally
   by status, and whether a 5xx is transient or sticky. Whatever comes back *becomes* Phase 1's
   backoff policy rather than a production surprise.
4. **#137's authorisation decision: confirmed, or reopened.** Say which, and against which of the
   numbers above.

Plus the free one: whether `settings.target_soc` / `remaining_charging_time_target_soc` actually
arrives for this car.

## Then delete it

```bash
git rm -r dev/vw-portal-spike
```

The decision record is the deliverable. This folder is scaffolding, and scaffolding that is left up
gets mistaken for the building.
