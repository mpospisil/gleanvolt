# Self-hosted web UI (the `Web` section)

The controller serves its own UI, so it can be looked at and driven without a second application. The two surfaces are independent adapters over the
same internal state, so all four combinations run: UI only, MQTT only, both, neither.

**What is built so far.** [Issue #44](https://github.com/mpospisil/gleanvolt/issues/44) lands
in phases. Phase 0 is the plumbing: `/health` shows the running build, the configured time zone, and
the time of the last completed poll — a liveness check (the timestamp updates itself as each poll
lands, so a page that sits still means the poll loop has stopped while the web host is fine).
Phase 1 adds `/`, a read-only telemetry dashboard, since regrouped (see
[The dashboard reports; the plan page decides](#the-dashboard-reports-the-plan-page-decides) below):
every figure carries its meaning inline, since MQTT discovery has nowhere to put one (see
[What each entity means](HOME_ASSISTANT.md#what-each-entity-means) above; the wording is the same). Phase 2 adds
optional authentication — a single shared password that gates every page once one is configured, see
[Authentication](#authentication) — landing before phase 3 gives the UI anything that can write to
hardware.

Phase 3 adds the same controls Home Assistant has — a button per strategy with the running mode
reported read-only beside them, the **battery discharge hold** switch (shown only while
`BatteryHold:Enabled` is on) and the runtime numbers (**minimum battery SOC**, **SOC resume
margin**). Phase 5 adds the `Forecasted` mode's day plan as one
coherent view instead of the loosely related entities Home Assistant renders it as: plan state,
charge window, EV energy budget, projected shortfall, surplus spill, loan headroom, required SOC floor,
forecast remaining today, tomorrow's forecast, forecast accuracy and battery loaned today, each with an
explanation next to it, plus a timeline chart plotting forecast surplus
against the charge window with the required-SOC-floor projection overlaid on a second axis. The
chart's data is computed once, in `Gleanvolt.Core`, by the same `SolarDayPlanner` that builds the plan
itself — the floor projection is the identical formula the live figure uses, evaluated at every
remaining forecast period instead of only the current instant — so the picture can never disagree
with the numbers next to it.

All of it drives the exact same Core interfaces the MQTT worker uses (`IChargeActions`,
`IBatteryHoldSelector`, `IForecastRuntimeSettings`, `ITargetedChargeSelector`, `IFastChargeSelector`), so there is no second
control path and the two surfaces cannot disagree about what the charger is doing — the last one to
press wins, visible on the other within a poll interval. The same semantics apply here as on the MQTT
side: nothing set from the UI persists across a restart, `FastNoBattery` and `Targeted` can switch the
mode back to `Off` on their own once the car finishes (the state line follows, and the "started from
the Web UI at 13:42" note goes with it), a charger that refuses the use-mode write says so on the page
instead of leaving a mode that quietly does nothing, and the battery-hold switch shows the last
command that was actually written to the inverter, not what was requested — a write that fails to take
shows the switch springing back on its own.

## The system's name, on every page

The header carries the installation's name beside the product's, and so does the browser tab —
`Dashboard — Home Roof` rather than `Dashboard — Gleanvolt`. Two tabs onto two roofs is the case this
is for; the product name is the one thing both of them already agree on. The sign-in page is the
exception, and keeps the product name: it is reached before anyone has signed in.

## `/pv-system` — the installation, and editing it

What this controller is and what it is talking to, from a browser rather than from the startup log:
the system's name, id and address, its coordinates, the array's bearing (with the compass point spelled
out, because `172°` is only checkable by someone who already thinks in degrees), tilt, capacity, loss
factor and commissioning date, and a table of the devices — the inverter and each charger, with model,
address and unit id. See [The PV system](CONFIGURATION.md#the-pv-system-the-pv-section) for what each value means.

**What it shows is what is running.** Every value in those sections is resolved once at startup —
the Modbus clients are constructed from it, and anything unusable has already stopped the host — and
none of it changes under a running process.

**The Edit form at the top changes what the *next* start reads**
([issue #204](https://github.com/mpospisil/gleanvolt/issues/204)). It covers every key in the `Pv`
section the page shows — identity, location and orientation, capacity, the inverter and the one
charger — and nothing else: secrets, `ChargeControl`, `BatteryHold`, `Ev` and `HomeAssistant` stay in
`.env`.

- **A save is checked by the startup resolver.** The form builds the configuration the next start
  would read and runs it through the same checks; if they refuse it, nothing is written and the page
  shows why. A saved file can therefore never be the reason the controller does not start.
- **It goes to the overrides file, `Pv:OverridesPath`** — `data/pv-system.json` by default, which is
  `/opt/gleanvolt/data/` on the Pi and `/var/lib/gleanvolt/pv-system.json` under the .deb. The file
  holds only the keys that differ, and **wins over `.env`** (and over `/etc/gleanvolt/gleanvolt.env`)
  key by key, while a command-line argument still wins over it. Each field says where its value comes
  from — *default*, *appsettings.json*, *environment* or *web UI* — and a field set from the web UI has
  **Revert**, which takes the key back out of the file (the last one deletes it). The startup log line
  `PV system: …` ends with the keys the file overrode, so a `docker logs` dump explains why an `.env`
  edit "isn't working".
- **A changed device address is probed once** before saving: one read, and what answered. An
  inverter address that reads no believable battery SOC, or a charger address that reads no
  believable run mode, is flagged as a likely swap. It warns and never blocks — a charger that is
  switched off answers nothing and is still the right address.
- **Changing `Id` or clearing the coordinates asks first**: the id moves every MQTT topic (see
  [Renaming what Home Assistant sees](HOME_ASSISTANT.md#changing-the-device-id)), and without coordinates no
  weather is recorded.
- **Saved is not applied.** Until a restart, the page shows *Saved, not applied — N changes take
  effect on restart*, each as *running → saved*, with **Restart** beside it. The banner survives a
  reload and disappears once the restarted controller is running with those values.
- **With authentication switched off the form is read-only**, and says why: it includes the addresses
  the controller writes Modbus commands to, and anyone on the LAN could redirect them. Since the setup
  page this is only reachable by setting `Web:RequireAuthentication=false` deliberately.

It is also where the **deprecation notices** live: any older key still supplying a value is listed
under *Configuration to move*. The same lines are logged once at startup, where nobody ever sees them
again.

**And the MQTT links.** The Devices table is what the controller *drives*; the **MQTT** section below
it is what the controller *talks through* — the two links kept apart, because they are separately
optional and may well be two different brokers. For [Home Assistant](HOME_ASSISTANT.md): the broker
it dials and as whom, the client id the broker's own log and ACL file know this controller by, the
discovery prefix, the unique-id root actually in force, the **topic prefix** — `gleanvolt/home-roof`,
composed at startup from `HomeAssistant:BaseTopic` and `Pv:Id`, and therefore guessable from neither —
the well-known topics under it (battery hold appearing only when the feature is on) with the
`{prefix}/{object_id}/state|set` pattern for everything else, the status interval, and the retirement
lists when an installation carries one. For [vehicle telemetry](VEHICLE.md#vehicle-telemetry-the-vehicle-section):
the same connection details, the topic the worker *actually subscribed to* (which comes from the car,
not from the feed), the staleness guard and the retry interval. Each link off reads as the default it
is, and says which key turns it on.

**The broker password is shown only when a login is enforced.** `MQTT_PASSWORD` is the account that
publishes to the `…/set` topics — the stop button on the wallbox by another route — and this UI is an
open LAN dashboard until a [`Web:PasswordHash`](#authentication) is configured. So the host hands the
page a null unless one is: the section cannot disclose what it was never given, and says that setting
a password is what makes it readable. Even then it renders masked, behind *Reveal* and *Copy* — a
login is not a closed door, and a shoulder or a screenshot is a different threat from the network.

None of it says whether either link is **up**: every value is read once at startup. That is a
different question, and no page answers it yet.

**And the API.** An **API** section states whether it is on — off is the default, so it says what turns
it on and that a key is part of that — the base URL built from the address *this page* arrived on
rather than from `Web:Port` (a proxy or a hostname in front means the configured port is not
necessarily the one that works, while the address that just delivered the page demonstrably is), links
to the index and the OpenAPI document, the configured keys by name, and the `curl` from the README with
this installation's address and key already in it. The keys are shown on the same terms as the broker
password: only behind a login, masked, with *Reveal* and *Copy*.

## `/car` — the car, and editing it

The other side of the cable from [`/pv-system`](#pv-system--the-installation-and-editing-it), on the
same mechanism ([issue #214](https://github.com/mpospisil/gleanvolt/issues/214)). Configuring the car
used to be the last thing here that could only be done over ssh, and it is the thing a new owner does
first — spread across four configuration sections that nothing said were about the same car, two of
which together are a startup failure.

**One `<select>`: the manufacturer.** It is the only control on the page that changes which fields
exist, and it is what makes the bad combinations unrepresentable — one feed is selected, so
`Vehicle:Website` and `Vehicle:Skoda` cannot both be enabled, and `Vehicle:DataAct` cannot be arranged
beside a live feed by accident.

| Choice | Feed it configures | What you then fill in | Freshness |
|---|---|---|---|
| **Volkswagen** | [`Vehicle:Website`](VEHICLE.md#vehiclewebsite--volkswagende-the-live-source) | VW ID e-mail, password, VIN | ~20 s, plug state, live while charging |
| **Škoda** | [`Vehicle:Skoda`](VEHICLE.md#vehicleskoda--the-myškoda-public-api-the-live-source-for-a-škoda) | VIN, then an API key under **Account** | 5–15 min, plug state |
| **Audi · SEAT · Cupra · Bentley · VW commercial** | [`Vehicle:DataAct`](VEHICLE.md#the-car-from-the-manufacturer-on-a-clock-the-vehicledataact-section) | Brand, VW ID, password, VIN | hours behind; no plug state |
| **A feed I publish myself** | [`Vehicle`](VEHICLE.md#vehicle-telemetry-the-vehicle-section) | Topic | whatever publishes it |
| **Another manufacturer — charge only** | none | nothing | — |

The brand list under the Data Act choice is the client's own table, so a brand added there appears
here. `ClientId` is behind a disclosure rather than in the form: it is the escape hatch for a brand
whose sign-in id has changed, which is documentation for a failure rather than a field to fill.

**The last row is not an error state.** Every EV with a Type 2 inlet charges without any feed at all.
It costs exactly one thing: targets are asked for in kWh instead of per cent.

**The car's own fields are always there** — id, name, make, model, phases, the two currents, the usable
pack and the charge efficiency — because they are what make charging correct and they are independent
of any feed. Phases is a choice rather than free text, since that is
the one that goes wrong quietly. What the installation allows
is printed beside the currents, so the narrower-of-the-two rule is visible while the number is typed
rather than discovered in a startup refusal.

**The same overrides file, the same rules as `/pv-system`.** A save writes only what differs to
`Pv:OverridesPath` (`data/pv-system.json`), which wins over `.env` key by key; every field says where
its value comes from, and a field set here has **Revert**. A save is checked by the same rules startup
applies — `EvRules` against the charger's amp band, so *"the car's minimum 8 A is above the
installation's maximum 6 A"* is refused at the form — and nothing is written when anything is refused.
With authentication switched off the form is read-only: these fields are a manufacturer account.

**Passwords never go in that file.** A password typed here goes to the
[secret store](CONFIGURATION.md#the-data-directory-holds-secrets-the-secrets-section) under `Secrets:Directory`, written owner-only, and the page
says which store and what protects it. It is **write-only**: the box renders empty always, empty on
submit means *unchanged* and never *clear*, clearing is the explicit **Remove** button, and nothing —
the provenance line, the pending-changes banner, a log line, an error — ever echoes it. volkswagen.de
and the Data Act portal are entered with the same VW ID, so there is one stored password for both.

**A save cannot prove an account**, and the page says so rather than letting a green save look like a
working feed. The feed is built at startup out of these very keys, so there is nothing running to test
them against — and a cold volkswagen.de sign-in wants an emailed one-time code anyway. The sequence is
*save → restart → sign in → Ask the car*, all four of them on this one page since
[#227](https://github.com/mpospisil/gleanvolt/issues/227), and the **Saved, not applied** banner carries
the **Restart** that begins it.

**Below the form is what is running**: the resolved car with the three-column limits table, and the
feed this process actually started — which is not always the feed the configuration asks for, since a
live feed sets the Data Act portal aside. `Ev:Vehicles:0:Make` still selects nothing: the `<select>`
writes the make *and* the feed's `Enabled` key as two separate edits, and the composition root goes on
reading only the second.

**One page, in the order the work is done** ([issue
#227](https://github.com/mpospisil/gleanvolt/issues/227)). `/vehicle-portal` used to hold the second
half of this — the sign-in and the on-demand read — on the grounds that this page was *which car, and
which account* and that one was *does that account work*. That reads well as a sentence and badly as a
journey: the sequence is *save → restart → sign in → ask the car*, and it crossed the navigation twice
for one sitting. The sections are now **Edit · Account · Ask the car · Running · Feed · Diagnostics**,
and `/vehicle-portal` redirects here so bookmarks and the docs' links do not 404.

**Account answers for every manufacturer.** It used to be rendered only where a sign-in seam was
registered, which is two of the five choices; for the other three the block vanished with no heading
and no sentence, and silence is indistinguishable from a bug.

| Chosen feed | What Account shows |
|---|---|
| **Volkswagen** | **Sign in** → the one-time code box → **Signed in** / **Sign out** |
| **Škoda** | the API key box: a password input, empty on render, emptied after every submit |
| **Data Act** | *this portal signs in on each read, so there is no session to establish here* — the account is the VW ID and password in the form above |
| **A feed I publish myself** | *nothing here signs in: you publish the readings yourself* |
| **Charge only** | *no account, because nothing reads this car* — charging is unaffected |

A feed switched on with too few of its fields filled in for the host to have built a sign-in gets a
sentence too, rather than the heading over a gap it used to get.

**Ask the car asks whatever feed this installation has.** The button used to be the Data Act portal's
alone, so four of five installations met a red *not configured* and a hint telling them to set
`VW_BRAND`, `VW_USERNAME` and `VW_PASSWORD` — advice that was wrong for them, since following it
arranges a Data Act portal beside their live feed that
[#212](https://github.com/mpospisil/gleanvolt/issues/212) then sets aside. What generalises it is that
`IVehicleUpdateService.FetchAsync` is the same call the polling worker makes, asked because a person
wants to know rather than because a clock said so.

**The two sources differ in one way the page says out loud rather than papers over.** A Data Act read
signs in afresh per press and is fed to nothing — not the dashboard's card, not any charging decision —
which is what makes it the right thing to press before switching a feed on. A live feed's press is the
feed's own fetch, so what comes back **does** reach the dashboard: an owner pressing a button is asking
the whole UI to catch up. Neither is ever triggered by a render: opening this page does not replay a
password or email anybody a code. Charge-only and own-topic get no button and a sentence — for an own
topic, what to publish and what last arrived on it.

Then **one card** for whatever answered: battery, range, reading age, charge state and plug state — the
dashboard vehicle card's own figures, in its own labels and the same rounding, so the two pages can be
read side by side without translating — plus the two only the Data Act portal carries, the car's own
**target SOC** and its own **time left**.

Under a Data Act read, what the feed is holding at that moment and how far apart the two are. They are
different sessions asking at different moments, so a couple of points is the expected outcome rather
than a fault. An install with no feed switched on is told it has nothing to compare against, which is a
supported answer and not an error.

**Diagnostics**, collapsed, holds everything that page used to lead with: the delivery it arrived in
(snapshots, their span, the odometer), every field the build recognised with the raw value and the
moment the *car* reported it, and every field in the bundle that nothing here reads yet. That is the
only thing that answers *why is that figure a dash* when a car starts reporting something new, so it is
kept rather than dropped — and it **opens by itself when the read failed**, because a bundle in which
nothing matched is a failure whose field list is the whole diagnosis. It is Data-Act-shaped and appears
only for a Data Act read; a live feed's equivalent is the **Feed** section's health, on the same page.

Setup for the Data Act portal — the credentials and the browser steps it needs first — is
[docs/VW_PORTAL_SETUP.md](VW_PORTAL_SETUP.md). The feed with its own clock is a separate switch,
[`Vehicle:DataAct:Enabled`](VEHICLE.md#the-car-from-the-manufacturer-on-a-clock-the-vehicledataact-section), and
it holds a session rather than replaying the password.

## The dashboard reports; the plan page decides

Those phases left the UI in three places for one question. The dashboard was fourteen telemetry tiles
followed by a column of inputs; `/forecast` held the plan those inputs shape; `/targeted` held a mode
with a form of its own. Reading an outcome and adjusting its input meant changing pages.

**The nav is now Dashboard · Charging plan · Sessions · Energy · Forecast · PV system · Car · Health.** The
**day plan** and `/targeted` are gone as destinations; what was on them lives on **`/charging-plan`**,
one tab per mode. (`/forecast` is a route again, and is a different page: the sun, not the car — see
[Looking at the forecast](#looking-at-the-forecast) below.)

**`/` reports and no longer decides.** It carries no button, no input and no select at all — three
sections, in the order the questions are actually asked:

- **Energy** — solar power against what the forecast expected of this instant, the whole of today's
  forecast production (morning included; a dash while no forecast is held for today), solar surplus,
  battery SOC and power, grid power. True whatever is or isn't plugged in, which is why it comes first.
- **Vehicle** — the car, because it is *configured*: its name and pack, then the charger's own view of
  whether one is connected, then whatever feed reports on it. The feed is an attachment, and the card
  names which of four situations it is in — no feed configured (nothing is wrong), a reading and its
  age, a reading marked **stale** past `MaxAge`, or **sign-in required** with the sentence saying what
  fixes it and a link to **Car**, where signing in is. The last two must never look alike: *stale* clears itself
  and *sign-in required* never will. **One car, one feed** ([#212](https://github.com/mpospisil/gleanvolt/issues/212)):
  the health is that of the installation's one feed, and *via …* names it in your words — volkswagen.de,
  MyŠkoda, Data Act portal. See [the car on a clock](VEHICLE.md#the-car-from-the-manufacturer-on-a-clock-the-vehicledataact-section).
- **Charging session** — charge mode, control state, charger status, session energy, EV charging power
  and current, target and active current, battery loan power. Shown **only while there is a session to
  report**: a mode is driving, or the car is drawing power under no mode at all (somebody put the
  charger into `Fast` by hand — the one case worth not hiding). Otherwise it is a single line naming
  the mode and the charger's state, and a link to start one, rather than a grid of dashes.

**`/charging-plan` is where charging is decided**, and its shape is a common header over a tab per
mode. The header carries what is true whatever is running — the mode itself, the **Off** that ends it,
and the **battery discharge hold**, which belongs to no single strategy: `FastNoBattery` turns it on
for the length of its charge and off again at the end, `Targeted` arms one of its own while its plan
imports, and it is worth arming by hand under any of them. Every action's note
and every charger refusal lands there too, next to the mode it moved.

Each tab then carries only what its own mode needs, which is what makes the modes legible against each
other:

| Tab | The button | What else is on it |
| --- | --- | --- |
| **Solar** | Charge from surplus | Nothing to configure — the current is whatever the sun leaves over. The surplus, the battery SOC that gates it, and what the car is drawing. |
| **Forecasted** | Charge to the forecast | The four runtime numbers the plan reads, then the day plan itself and the timeline chart. |
| **Fast (no battery)** | Charge at maximum | How much to deliver before it stops — until the car stops, an amount in kWh, or a battery target — and optionally when you need it, which holds the charge back so it finishes just in time. Then what the speed costs: the car's actual draw and current, the setpoint read back, grid power, and the amount delivered so far when there is one. |
| **Targeted** | Preview plan → Start charging / Cancel | What the car says about itself, then what you want — kilowatt-hours or a battery target — the charging priority and its rest level, **Sun only**, the departure, the minimum battery SOC the planner works to, and the plan in words and figures *before* the charger moves. |

The tab is in the URL (`/charging-plan/forecasted`), so a bookmark and a refresh come back to the same
mode and the back button walks them; `/charging-plan` with no tab opens on whatever is actually
driving the charger, which is nearly always the one being reached for. A dot in the strip marks the
running mode, so the tab you need is findable without reading the state line above it.

The targeted tab is still the plan **in words** — what will come from the sun and between when, what
the grid will supply and from when, and, when there isn't enough time, how far short it will fall and
the departure that would have covered it. Prose rather than a chart, deliberately: the question at
22:00 is "is my car going to be ready, and why is nothing happening yet?", and no chart answers that
as directly as a sentence. (The timeline chart is a separate story, and will read the same plan's
blocks unchanged.) Its form is validated before anything is set — a positive amount, a departure in
the future and inside the horizon — and composed through the app's configured time zone rather than
the server's clock, so a DST boundary between now and the departure cannot move it by an hour. The
forecast tab shows an explicit empty state while any mode other than `Forecasted` is driving, rather
than the last stale plan, and the targeted tab does the same — but the controls on both stay reachable
whatever is running, because a target is prepared *before* the mode is on.

The `/health` page also carries the two controls that aren't about charging. **Restart** is the same
graceful shutdown as Stop, ending with exit code `75` so that Docker's `restart: on-failure` or the
.deb's systemd unit starts it again — which is how a change saved on `/pv-system` is applied. It pauses
a charge in progress, comes back in mode **Off** like any start, leaves the UI unreachable for about
half a minute, and spends a Solcast call at startup; the page reloads by itself once the controller is
back. Where nothing supervises the process (`dotnet run`, a plain `docker run`, the Windows zip) it
simply exits. **Stop service**
shuts the whole controller down gracefully — the charger returned to its pause current, the open
charging session closed and written, the session store flushed, Modbus and MQTT closed — rather than
leaving it to be killed, which revokes nothing and leaves the car drawing at the last current we
wrote. It takes two clicks, because it is one-way from the browser: the UI goes down with the
service, and only a shell on the Pi (`docker compose start gleanvolt-controller`) brings it back. See
[Stopping and starting the controller](../deploy/README.md#stopping-and-starting-the-controller) for the
exit-code contract that keeps it stopped without also breaking restart-after-reboot. Note that the
button is as reachable as the rest of the UI: with no password configured, anyone on the LAN can stop
the controller — one more reason to consider [Authentication](#authentication) below.

```jsonc
"Web": {
  "Enabled": true,      // master switch; while false the process binds no socket at all
  "Port": 8090          // listens on every interface, plain HTTP
  // "RequireAuthentication"  // unset -- follow the password; see below, rarely worth setting
  // "PasswordHash": ""       // secret -- see below, never set it here
}
```

- **On by default, and needs no configuration.** Start the controller — from `dotnet run`, from the
  image, from the compose stack — and the UI is at `http://<host>:8090`. This is the surface a fresh
  install is operated through, and unlike the Home Assistant integration it needs no broker, no
  credentials and no onboarding to be useful, so there is nothing to gain by making it opt-in.
- **A password before anything is served.** See [Authentication](#authentication) below: a fresh
  installation serves only the page that sets one, so "on by default" does not mean "open by default".
- **`Web:Enabled=false` means nothing is listening** — not "listening but empty". An ASP.NET host
  would otherwise fall back to a default port; this one installs a server that binds nothing, so with
  the UI off the process is the same headless worker it has always been. `ss -ltnp` shows no socket
  inside the container. (In the compose stack the *host* port stays published either way — see
  [Deployment](../README.md#deployment) — so connections are refused rather than never accepted.)
- **Plain HTTP**, deliberately: this is a LAN appliance, and terminating TLS in front of it (or not)
  is the operator's decision rather than the controller's.

## Authentication

**A login is required, and an installation with no password set serves only the page that sets one.**
Nothing else: no dashboard, no history, no controls, until a password exists. Once it does, the UI
behaves as it always did and asks anonymous visitors to sign in.

This used to be the other way around — the UI served every page to anyone who could reach the port,
and logged a warning saying so. The warning was accurate and protected nobody, which is the whole
argument: the surface shows what the household is doing and can change how the charger is driven, so
it does not get to be open because opening it was convenient. A winget-pkgs reviewer made the same
point about the same default, and was right.

The choice of *how* to close it matters for an appliance. Binding to loopback would leave a headless
Pi's UI unreachable from the browser that operates it, with nothing on the box to say why; refusing to
boot would break a running installation on upgrade. Asking the first visitor for a password costs
neither, and cannot be skipped by not reading a log.

There is no per-user account: one shared password gates the whole UI, hashed with ASP.NET Core's
`PasswordHasher`, matching a LAN appliance with one or two operators rather than a multi-tenant system.
The setup page can only *set* a first password — never change an existing one, which an
unauthenticated page has no business doing. Changing it is on Settings; resetting a forgotten one is
[below](#forgot-your-password).

The hash is a secret and must never live in `appsettings.json` — supply it via `.env` / an
environment variable, exactly like the MQTT broker credentials:

```
Web__PasswordHash=<hash>
```

Setting it ahead of time means no setup page is ever shown. Generate one with the worker binary
itself, or with the image, without configuring anything (it prints the hash and exits — no listening
socket involved):

```bash
dotnet Gleanvolt.Worker.dll hash-password '<your password>'
docker run --rm ghcr.io/mpospisil/gleanvolt:latest hash-password '<your password>'
```

`Web:RequireAuthentication` overrides that inference in either direction and is rarely worth setting:

| `RequireAuthentication` | `PasswordHash` | Result |
|---|---|---|
| unset (default) | not set | **only the setup page is served**, until a password is set there |
| unset (default) | set | login required — **the normal way to run it** |
| `true` | set | login required; identical to the row above |
| `true` | not set | **host refuses to start** — nobody could ever sign in |
| `false` | either | UI served openly, no setup page; a warning is logged at every startup |

The one refusal is the combination that cannot be honoured: a login demanded with nothing to check it
against would leave the UI permanently unreachable, and a host that stops says so immediately where a
running one looks like a broken page. Everything else starts.

To change the password, sign in and use **Settings → Password**. It asks for the current one.

## Forgot your password

The sign-in page links to **Forgot your password?** (`/forgot-password`). There is no email and no
second account, so a reset proves one of two other things
([issue #301](https://github.com/mpospisil/gleanvolt/issues/301)):

- **The recovery key.** When a password is set in the web UI (on the setup page, in Settings, or by a
  reset), Gleanvolt shows a recovery key **once**, in five groups of five characters. Keep it with the
  password, for example in a password manager. Only its hash is stored, next to `Web:PasswordHash` in
  `data/pv-system.json`. A password set by hand with `Web__PasswordHash` has no key until it is
  changed in the UI.
- **A one-time code from the host.** **Write a code on the machine** writes a code to Gleanvolt's log
  and to `password-reset-code.txt` in the data directory (owner-only). The browser never receives it,
  so reading it proves you can reach the machine Gleanvolt runs on. Read it with:

  ```bash
  docker logs gleanvolt-controller 2>&1 | grep "reset code"          # Docker
  sudo cat /var/lib/gleanvolt/password-reset-code.txt                 # .deb
  ```

  On Windows, open `data\password-reset-code.txt` next to `gleanvolt.exe`, or the log in `logs\`.
  Only the newest code works, once, for 15 minutes. Codes can be requested once a minute and five
  times an hour.

Every reset or change does three things:

- it signs out every browser, including any tab still open (the sign-in cookie carries a stamp of the
  password, and an old stamp is refused);
- it issues a new recovery key, and the old one stops working;
- it is written to the log.

Ten wrong keys, codes or current passwords in 15 minutes lock all three paths for the rest of that
window.

## Setup mode

A fresh install has none of the required settings: the web UI password and the inverter's and the
charger's addresses. Those are what the controller cannot run without.

The site's location (`Pv:Latitude`, `Pv:Longitude`) and its time zone are **recommended**: setup asks
for them, but Gleanvolt starts without them. Without a location there is no weather; only one of the
two coordinates counts as no location, and is listed as such. Without a time
zone the controller runs in the host's own zone, which on Windows (it ignores `TZ`) or on a host that
reports plain UTC means UTC, so days start and sessions are filed at UTC midnight. The time zone
counts as set when `Controller:TimeZone` names a valid zone, or when it is empty and the host's own
zone can be trusted. Each recommended setting still missing is logged as a warning at startup.

Until every required setting is in place, Gleanvolt starts in **setup mode** instead of stopping
([issue #301](https://github.com/mpospisil/gleanvolt/issues/301)):

- The web UI, the settings stores and the secret store run. The controller does not: nothing polls
  the inverter, drives the charger, records sessions or energy, or talks to MQTT or a car. Its Modbus
  clients refuse every call.
- The UI shows the setup page (if no password is set yet), then the [setup wizard](#the-setup-wizard).
  Pages that need the controller, such as the dashboard, redirect to the wizard. Settings and Health
  show a **Setup mode** notice listing what is still missing.
- The API answers every request with `503` and "Gleanvolt is not configured".
- The log starts with a `SETUP MODE` warning that lists what is missing.

When nothing required is missing, the notice offers **Start the controller**, which restarts
Gleanvolt and starts it normally. Docker and the .deb's systemd unit start it again by themselves. The
Windows zip, winget and `dotnet run` have nothing that does: there the page says to start Gleanvolt
again yourself.

Some problems keep the controller stopped even when everything else is set: a configuration key that
has moved (its old name is listed with the new one), and a `Controller:TimeZone` this host cannot
resolve (on the Nano Server image, an IANA id
such as `Europe/Prague`; use `Central Europe Standard Time` there). Gleanvolt never starts the
controller on part of a configuration.

With the web UI disabled (`Web:Enabled=false`) there is nothing to finish a setup in. A configuration
that is not usable then stops Gleanvolt at startup, as before. The reason goes to the console and to
the log file, and the exit code is 78 (`EX_CONFIG`).

## The setup wizard

After the password, a fresh install continues at `/wizard`
([issue #302](https://github.com/mpospisil/gleanvolt/issues/302)). It is Settings in a guided order:
every step is one of the forms Settings uses, with the same checks, saving to the same file, so a step
skipped now is done later on Settings. Each step saves as it goes and has its own address, so a reload
or a closed browser loses nothing that was saved.

1. **Devices.** **Search** looks for SolaX devices that answer Modbus TCP on this machine's own subnet
   (or a private range you type, at most a /22) and lists what it finds with what each identified as.
   One search runs at a time, whoever starts it. Choose **Use as inverter** or **Use as charger**:
   Gleanvolt never assigns the roles itself, and says so when you choose the role a device did not
   claim. A swapped pair both answer on port 502, and only show themselves once running, as a frozen
   battery and no sun. Addresses can also be typed in. Saving reads one value from each device to
   check it.
2. **Site.** Location and time zone (recommended: Gleanvolt starts without them), the home battery's
   usable capacity and charge efficiency, the array's details.
3. **Forecast** (optional). The Solcast account. In setup mode a save only stores it; the forecast is
   fetched when Gleanvolt starts, so the setup spends none of the day's Solcast calls.
4. **Car** (optional). The car's battery and limits, and the account its state of charge comes from.
5. **Extras** (optional). The tariff, the OpenWeatherMap key, the Home Assistant (MQTT) link and the
   API with its keys.
6. **Finish.** What is still missing, each with a link to its step; the recommended settings not yet
   set (location, time zone), which do not hold the start back; the optional settings not yet set;
   and **Start Gleanvolt**, which restarts it out of setup mode. Once the restarted Gleanvolt answers,
   the browser goes to the **Overview**.

Optional settings never block anything. A page that needs one says it is not configured, with the way
to set it up: Forecast (Solcast), Car (the car), Charging plan (a tariff). Health and Settings list
every optional setting still missing.

## Setting up from the command line

The same setup without a browser ([issue #303](https://github.com/mpospisil/gleanvolt/issues/303)), for
a headless install, a script, or a site described once in a file. It is an optional route beside the
wizard, and it saves through the same path, with the same checks and to the same files. None of these
commands starts Gleanvolt; each saves for the next start and says to restart.

| Command | Does |
|---|---|
| `setup --file site.json` | Checks the file and applies it. Lists every problem and exits 1 if there is any; exits 0 when saved. |
| `setup --validate site.json` | Checks the file only. Writes nothing. |
| `setup --print-schema` | Prints the JSON schema of what a setup file may hold. |
| `setup` | Asks for each setting in turn, as the wizard does: the password (when none is set), a device search, the addresses, the location, the time zone, the home battery, and the Solcast and OpenWeatherMap keys. Passwords and keys are not echoed. |
| `set-password`, `reset-password` | Sets a new web UI password, signs every browser out on the next start, and prints a new recovery key. Being able to run this as the service user is the proof a recovery key would otherwise be. |
| `discover [--json]` | The device search, as `gleanvolt-discover` (see [Finding your devices](DISCOVERY.md)). |

A setup file is shaped like `appsettings.json`, with only the keys the wizard sets:

```json
{
  "Pv": {
    "Id": "home-roof",
    "Latitude": 50.0755,
    "Longitude": 14.4378,
    "Inverter": { "Host": "192.168.1.10" },
    "Chargers": [ { "Host": "192.168.1.11" } ]
  },
  "Controller": { "TimeZone": "Europe/Prague" },
  "ChargeControl": { "Forecast": { "BatteryCapacityKWh": 9 } },
  "Web": { "Password": "at least 8 characters" },
  "Solcast": { "ApiKey": "…", "ResourceId": "…" },
  "HomeAssistant": { "Enabled": true, "BrokerHost": "192.168.1.5", "Username": "gleanvolt", "Password": "…" },
  "Api": { "Enabled": true, "Keys": { "claude": "…" } }
}
```

Anything else is refused by name. An empty value removes a saved one, so `.env` applies again.
**Secrets may be in the file**: the web password is saved as a hash, and the rest go to the secret
store, never to the overrides file. Because of that, a file holding secrets that every user on the
machine can read is refused until it is private (`chmod 600`), and after applying it the command
reminds you that the file still holds them.

Where to run it:

- **Docker**, from `/opt/gleanvolt`, with the file in the mounted `data` directory:
  `docker compose run --rm --no-deps gleanvolt-controller setup --file /app/data/site.json`. It runs as
  the image's own user, so the files it writes are ones the controller can read. Restart the controller
  afterwards: `docker compose restart gleanvolt-controller`.
- **The .deb**, as the service user, which the command insists on, since the service could not read
  files written by anyone else:
  `sudo -u gleanvolt /opt/gleanvolt/Gleanvolt.Worker setup --file /var/lib/gleanvolt/site.json`. It
  reads the service's own environment (the unit's `Environment=` lines and
  `/etc/gleanvolt/gleanvolt.env`), so it writes where the service looks. Then
  `sudo systemctl restart gleanvolt`.
- **Windows**: `gleanvolt setup --file site.json`, as the user Gleanvolt runs as. With DPAPI, the
  default there, secrets are sealed to the user who saved them, and the command says which.

The command line and the web UI never write the overrides file at the same time: both take a lock on
`pv-system.json.lock` beside it.

## Deploying the web UI

The compose stack in [`deploy/`](../deploy) needs **nothing in `.env`** for the UI: `docker-compose.yml`
publishes port 8090 and leaves `Web__Enabled` at its default, so a fresh Pi ends a deploy at a working
`http://<pi>:8090`.

The host port is published unconditionally, which is the one thing to understand about
`WEB_ENABLED=false` there: the port stays bound on the Pi, but nothing inside the container listens,
so connections are refused. Earlier releases published it from a separate `docker-compose.web.yml`
that had to be merged via `COMPOSE_FILE`; that second step is gone, and the file is kept only so an
existing `COMPOSE_FILE` line doesn't break — merging it is now a no-op.

## Browsing charging session history

Phase 4 adds `/sessions`: a list of recorded sessions (date, duration, driving strategy, energy
delivered, solar share), each linking to a detail page with the per-source energy split, the day's
forecast total with its p10–p90 band, the weather at each end of the session with the day's daylight
window, and **a chart of the session** underneath them. It reads through `IChargingSessionStore`'s existing query methods —
nothing here reaches past the interface into SQLite — and degrades to "isn't available right now"
rather than an error page when `SessionStore:Enabled` is off or the file can't be opened. See
[Charging session history](DATA.md#charging-session-history-the-sessionstore-section) below for what is
actually recorded.

The chart speaks the same vocabulary as [the energy day chart](#browsing-the-energy-history) below —
solar and the forecast that expected it, grid with import above the line and export below, the
battery charging above and discharging below, what the wall delivered, and SOC on the right-hand axis
— over the session's own window rather than a calendar day. Three of its lines answer questions only a
session asks:

- **The car's own SOC steps at the time the car captured it**, not at the sample that carried it. The
  vehicle feed routinely lags by hours; plotted where it arrived it would draw the car standing still
  and then jumping. A session the car never reported in has no such line at all.
- **A stretch with no samples is a hole**, not a line sloped across it — the same rule the energy
  chart applies to a day. The car's line is the one exception, because the car timestamps its own
  readings and a reading taken before an outage is still where the car was during it.
- **A shaded band marks the stretches the battery discharge hold was armed for**, with the battery's
  own line inside it. Whether a hold actually held is a question about what the pack did while it was
  armed, and this is where that is now visible rather than inferred.
- **The plan's SOC floor is drawn beside the SOC it judges**, and the **battery loan** is shaded under
  the surplus the controller decided on. Without them, "why did it stop while the pack was full?" could
  not be answered from the picture at all, though every figure was already on the rows
  ([#223](https://github.com/mpospisil/gleanvolt/issues/223)). A session run in another mode carries no
  plan, and one that never borrowed carries no loan, so neither line is offered as a flat zero.

A dashed rule marks each moment that changed the shape — a mode change, a pause, a plan going
unusable or usable again — and the list under the chart says what each one was. The setpoint changes
are deliberately not ruled: they fire most cycles on a sunny afternoon, and a chart ruled once a
minute is a hatched chart.

The chart uses [uPlot](https://github.com/leeoniya/uPlot) (MIT licensed), vendored into
`Gleanvolt.Web/wwwroot/lib/` rather than fetched from a CDN — issue #44's decision, so the history stays
readable during an internet outage, which is exactly when a locally controlled system is most worth
looking at.

## Looking at the forecast

`/forecast` is **today and tomorrow, drawn and then printed**
([issue #219](https://github.com/mpospisil/gleanvolt/issues/219)) — the two days the controller is
deciding on, period by period, in the shape `/energy` uses for a recorded day. One list feeds the
chart and the table, so the picture cannot disagree with the numbers.

The **chart** is watts against one 48-hour time axis, 30-minute periods: the **median** as the line,
**p10–p90 as a band** behind it, a solid rule at the midnight between the two days and a dashed one at
now. The band is the reason the page is a picture — on an uncertain day the width of that gap is the
whole story, and a single line hides it.

The **table** below it is a row per period, grouped into the two days: the half hour it covers,
expected, p10, p90 and the energy that power amounts to over thirty minutes. Each day opens with its
own figures — expected, p10, p90 and peak — read from the same summary the dashboard's table reads, so
the two can never disagree. **The night rows are kept**: a 0 W row at 02:00 *is* the forecast, and a
table that drops rows is one you have to learn the rules of before a gap in it can mean anything.

Three surfaces show forecast and each owns a different question:

| Surface | Owns |
|---|---|
| The dashboard's **Solar forecast** table | **The glance** — two rows, four numbers, in a place somebody is already looking. Unchanged, plus a link here. |
| **`/forecast`** | **The detail** — the shape of both days. The only place tomorrow has a shape. |
| The **Forecasted** tab | **The car's share of today** — surplus after the house and the battery, the SOC floor, the charge window. Untouched. |

Worth knowing, because each of these reads as a bug if it isn't written down:

- **It never fetches.** The page reads `ISolarForecastService` in-process, the same cached forecast the
  poll loop is deciding on. Solcast's free tier is about ten calls a day and the refresh worker already
  spends half of them; a page that fetched on every visit is exactly what would exhaust that. What it
  shows is what was last fetched, and it says when.
- **After a restart, today's morning is missing.** A provider only ever answers *what is still to
  come*, so the controller can say what this morning was expected to bring only if it was running
  through it — `SolarForecastHistory` keeps what each refresh carried, and nothing more. A controller
  started at 10:44 has nothing for today before then, and the page **says so** above the table rather
  than leaving an empty morning to read as darkness.
- **Two days, not seven.** Solcast returns about 97 half-hourly periods, so the day after tomorrow is
  partial — and a chart that trails off mid-afternoon on its third day invites the question "why did
  the sun stop?".
- **p10 and p90 are per-period optional.** Where the provider sent no band, the chart's band collapses
  onto the line and the table shows an em dash rather than repeating the median. The chart note says so
  whenever it happened, because a band no wider than the line must not read as confidence.
- **A period is counted under the day it *ends* on**, which is how the dashboard and the day plan count
  it too — so each day's first row is the half hour that ends at its own midnight.
- **Tomorrow's figures move.** Every refresh re-forecasts them until the day arrives.

Nothing outside the web UI changed: the arithmetic behind the chart lives in `ForecastChartSeries`
beside `EnergyChartSeries`, where the day boundary, a missing band and a half-covered day are tested
rather than eyeballed. Weather (`GET /api/v1/forecast?weather=true`) is deliberately not here — it is a
live third-party call with a quota, and a page that renders on every visit is the wrong caller for it.

## Browsing the energy history

`/energy` shows one recorded day at a time, twice: **the day drawn, then the day printed**. A date
picker and prev/next buttons move both together; the "next" button stops at today. One query feeds
them, so the picture can never disagree with the rows.

The **table** is a row per interval with solar, forecast, grid each way, energy to the car, the
battery each way, the house residual, SOC and coverage, and a day total beneath them. It is where the
figures are exact and where a partial row is *visibly* partial — a row that covers less than the full
interval is marked and counted in a note under the table, and a window no forecast covered shows an em
dash rather than `0.00`.

The **chart** above it is the shape of the day, which ninety-six rows of eleven columns cannot show:
the sun rising, the battery filling, the car taking the middle out of the afternoon. Worth knowing
about how it is drawn:

- **Watts, not kWh.** The store keeps energy per bucket; a chart against time is read as power, so
  each bucket is divided by **the part of the window that was actually observed**. A quarter hour the
  service saw four minutes of is drawn at the power it was really running at, not at a quarter of it —
  a restart at 09:07 is not the sun going out for seven minutes.
- **Gaps stay gaps.** A stretch with no rows at all is a hole in every line, never a line drawn
  straight across it, and a window observed for less than a tenth of its length is not drawn at all —
  a few seconds' energy divided by a few seconds is noise, not a spike.
- **Import and export share a line, and so do the battery's two directions.** Import and charging are
  above zero, export and discharging below. The columns stay separate where exactness matters, in the
  table underneath; folding them is what makes a *picture* readable, and the zero line is drawn for it.
- **Forecast over solar.** The forecast is a dashed line on the same axis as the production it was a
  forecast of, and a window nothing forecast is a break in it. A day nothing forecast at all has no
  forecast line rather than a flat zero.
- **The whole day, always.** Midnight to midnight in the site's own zone — which the axis is labelled
  in, not the browser's, so the chart and the table agree even when you are reading from elsewhere.
  A morning's rows look like a morning.
- **The short windows are marked** with a band along the bottom: the same ones the table shows in
  italics. Clicking a name in the legend takes that series out.

The page reads through `IEnergyIntervalStore.GetIntervalsAsync` and degrades to "isn't available right
now" when `EnergyMonitor:Enabled` is off or the file can't be opened, exactly as `/sessions` does. The
kWh-to-watts arithmetic lives in `EnergyChartSeries` rather than in the markup, because the coverage
division and the holes are the parts worth a test rather than an eyeball.

Nothing was added to Home Assistant. This is history, not telemetry — see
[Energy history](DATA.md#energy-history-the-energymonitor-section) below for what is recorded.

The published container image is now based on `dotnet/aspnet` rather than `dotnet/runtime` — about
25 MB more, on every platform, whether or not the UI is enabled. The framework reference is fixed at
build time, so there is no variant that avoids it.
