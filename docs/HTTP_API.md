# HTTP API (the `Api` section)

A third control surface beside Home Assistant and the web UI, for **programs rather than people**:
everything the controller can see, and the three actions that change something, described by
[OpenAPI](https://www.openapis.org/) so a client is generated from the document rather than written
against a moving target.

It exists for one use case in particular — an **MCP server**, so an LLM can answer questions about
this installation and act on it. That server is a separate process in a separate repository, and
nothing here is specific to it: the test of whether this API is the right shape is that it would suit
a script, a dashboard or an agent equally.

Like the web UI, it is an adapter over the same Core seams the MQTT worker drives
(`ChargeControlStatusHolder`, `IChargeActions`, `ITargetedChargePreview`, `ITargetedChargeSelector`,
`IBatteryHoldSelector`) and owns no control logic of its own. It invents no capability: every action
below is a button the web UI already has.

**Every field is in a base unit, and its name says which** (issue #263): power in W (`…Watts`), energy in
Wh (`…Wh`), durations in seconds (`…Seconds`), distance in metres (`…Metres`), pressure in Pa
(`…Pascals`), percentages 0–100 (`…Percent`). Nothing is pre-scaled to kWh, kW or km for display: the
client formats, as the web UI does. The settings keys keep their own units (`…KWh`, `…Kwp`).

## Switched on knowingly, and never open

| Setting | Default | Meaning |
|---|---|---|
| `Api:Enabled` | `false` | Master switch. While false **no route is mapped and no document is served** — there is nothing to find, not merely nothing permitted. |
| `Api:Keys` | *(empty)* | The keys that may call it, as `name → secret`. Enabled with none configured is a **startup failure**. |
| `Api:MaxQueryRange` | `31.00:00:00` | The widest span one history query may ask for. |
| `Api:MaxSessions` | `500` | The most sessions one listing may return. |

The API shares the web UI's port (`Web:Port`, 8090) — this is one appliance, not two services that
happen to be co-hosted — and either surface being enabled is what makes the process listen at all.

Keys are stored as the secret itself and supplied out-of-band through `.env` or an environment
variable, exactly like the broker password and the Solcast key:

```bash
Api__Enabled=true
Api__Keys__claude-mcp=$(openssl rand -hex 32)
```

Or on **Settings → Installation → API access**, or in the setup wizard's Extras step: type the
client's name and **Create key** makes one — 24 random letters and digits, about 143 bits — keeps it in the secret
store, and lists it beside the environment's keys (shown behind the login, with Copy). A key saved
there under the same name as one in `.env` wins. The API cannot be turned on there without a key, and
its last key cannot be removed while it is on, so the next start never meets a keyless API. Like
everything the API reads, a change applies on the next start.

The web UI's password is hashed because it is a password a *human* chose and may have reused; these
are generated, single-purpose and high-entropy, and a slow KDF on every request would buy nothing
against an attacker who can already reach the port. The **name** is not a credential — it is what
reaches the log and the recorded charging session as the source of an action, so
`Api__Keys__claude-mcp` produces *"API (claude-mcp) started Targeted"* rather than an anonymous
write. Several clients therefore get several keys.

Present one as a bearer token on every call that does anything:

```bash
curl http://gleanvolt.local:8090/api/v1/            # no key: what this is, and what it serves
curl -H "Authorization: Bearer $API_KEY" http://gleanvolt.local:8090/api/v1/status
```

All of it is **readable back** at [`/pv-system`](WEB_UI.md#settings--installation), including
that second line with this installation's own address and key already substituted in. The key itself is
shown only when the UI is behind a [login](WEB_UI.md#authentication); without one the page shows the names and
says what makes the secrets readable, because a key is bearer-equivalent to the stop button on the
wallbox, and the UI is reachable by anything on the LAN once its password is set.

**Why the API defaults off when the UI defaults on.** Two of these endpoints write to hardware, and
the project's rule for anything that writes is that an operator switches it on knowingly. The UI can
afford to be an open LAN dashboard because it is a browser with a person in front of it; a
non-interactive control surface that any program on the network can drive cannot. A key is
bearer-equivalent to the stop button on the wallbox — treat it that way.

## What it exposes

Everything is under `/api/v1/`. **Two endpoints need no key**, because a browser cannot send one and
neither of them carries anything to act on: `/api/v1/` itself, which says what this is, which
operations exist and how to authenticate, and the OpenAPI document at **`/api/v1/openapi.json`**,
which is the thing a client is generated from. Everything else is behind the key.

| Endpoint | Answers |
|---|---|
| `GET /` | What this is, where the document is, how to authenticate, and every operation this build serves. **No key.** |
| `GET /site` | Which installation this is: name, id, address, coordinates, the array's bearing, tilt, capacity and loss factor, and the devices it is made of with their models and addresses. Ask it first — every other endpoint reports what the controller is *doing*, and two installations answer those identically. |
| `GET /status` | The live snapshot: mode, state, PV, grid, battery power and SOC, EV power and current, the hold, session energy, the running plan. 503 until the first poll completes. |
| `GET /health` | Which system is answering, the build version, age of the last poll, whether a forecast and a vehicle reading are in hand, whether the two history databases can be read, and what protects the secrets in the data directory (`secretStore`). |
| `GET /energy/intervals?from=&to=` | The recorded series — solar, forecast solar, import, export, EV, battery in and out, the SOC band, and each window's **coverage**. Defaults to the last 24 hours. |
| `GET /energy/days/{date}` | One local day added up, so "how was Tuesday?" is one call rather than 96 rows. |
| `GET /sessions?from=&to=&limit=` | Charging sessions, newest first, with the energy split by source. Defaults to the last 30 days. |
| `GET /sessions/{id}` | One session in full: every recorded poll and every notable moment. |
| `GET /forecast` | Today and tomorrow **in full** — period by period plus each day's own median, p10, p90 and peak — from the cached forecast the poll loop is deciding on. `?weather=true` also fetches current conditions. See [The forecast agrees with itself](#the-forecast-agrees-with-itself). |
| `GET /vehicle` | What the car last said — SOC, range, plug and charge state — **and how old the reading is**. |
| `POST /plans/targeted/preview` | What a targeted charge *would* do. Writes to nothing. |
| `POST /charging/start` | Start a mode (`solar`, `forecasted`, `fastNoBattery`, `targeted`). `targeted` needs a `target`; `fastNoBattery` takes an optional `fast` amount and departure. |
| `POST /charging/start/targeted` | Start a targeted charge under an edited plan's limits. Same body as the preview. |
| `POST /charging/stop` | Stop charging and clear any standing target. |
| `PUT /battery-hold` | Arm or release the battery discharge hold. |
| `GET /charging/low-tariff-only` | Whether `fastNoBattery` and `solarGrid` wait for low tariff, and whether there is a tariff for them to act on (`tariffKnown`). |
| `PUT /charging/low-tariff-only` | Set either or both; a field left out is left as it is. Applies from the next poll, to a running mode too, and doesn't survive a restart. Accepted with no tariff set up, which the answer's `tariffKnown` shows. |
| `GET /charging/until-full` | Whether `forecasted` runs [until full](MODE_FORECASTED.md#until-full), and the session running with it (`session`: `since` and `day`), if any. `/status` carries the same session as `untilFull`. |
| `PUT /charging/until-full` | `{"untilFull": true}` or `false`. Starts nothing; applies from the next poll, to a running session too. |
| `GET /tariff` | The [grid tariff](CONFIGURATION.md#grid-tariff-the-tariff-section): the low and high stretches as far ahead as the provider knows, the rate now with its price and next change, and the currency. Empty with no tariff — which means no restriction, never "always low". |
| `GET /tariff/providers` | The tariff providers this build has, and which one is selected. |
| `GET /tariff/settings` | The tariff settings as stored: the selection, the prices, and each provider's own settings by key. |
| `PUT /tariff/settings` | Store them, applied at once. Held to the same rules as Settings: a 400 lists every problem with its field and row under `problems`, and nothing is written. Writes no hardware. |

Two rules run across the read endpoints. **Ranges are bounded** — a caller will cheerfully ask for a
year of quarter-hours, and this runs on a Raspberry Pi. And **staleness is reported rather than
hidden**: `/vehicle` carries `ageSeconds` and `stale` beside the state of charge, because a cloud
reading arrives hours late as a matter of course and a caller that cannot see the clock will
otherwise treat it as current.

## The forecast agrees with itself

`GET /forecast` returns **two whole local days**, elapsed periods included, and every figure in the
response comes from the same two days of retained forecast — so
`sum(periods on a date) == days[date].expectedWh`, and `peakPowerWatts` is the peak of the periods
beside it ([issue #221](https://github.com/mpospisil/gleanvolt/issues/221)).

It did not used to. The period list was built from the **live cache** and the day totals from the
**retained history**, and those are not the same forecast: a provider answers only *what is still to
come*, so the cache at four in the afternoon has nothing to say about the morning while the history
does. A caller summing the periods for today got a smaller number than the response's own
`todayExpectedWh` beside it, and `peakPowerWatts` fell towards zero as the sun went down while the day
had demonstrably peaked at six kilowatts. Both now read the history, which is what the web UI's
`/forecast` page already read.

**`days`** is a block per local day, carrying the four figures the dashboard's forecast table shows —
worked out when the forecast landed rather than summed per request:

```jsonc
"days": [
  { "date": "2026-09-23", "expectedWh": 24800, "lowWh": 18100, "highWh": 31400,
    "peakWatts": 6200, "complete": false, "heldFrom": "2026-09-23T10:30:00+02:00" },
  { "date": "2026-09-24", "expectedWh": 17400, "lowWh": 9600, "highWh": 26100,
    "peakWatts": 4300, "complete": true, "heldFrom": null }
]
```

`lowWh` and `highWh` are the answer to *"how sure is tomorrow?"*, which previously could not be had
without summing forty-eight periods — the top-level `today…` fields carry the median alone.

**Read `complete` before treating a day's periods as the whole day.** False is ordinary, not a fault:
a controller started at 10:44 has nothing retained for today before then, and `heldFrom` says where
that day's periods really begin. A caller that reads an incomplete day as a whole one will under-report
production and be wrong about the day — which is why this is a field rather than something to be
inferred from the first period's timestamp.

A day nothing is held for is **absent from `days`** rather than present as zeroes, on the same terms as
the null totals: "we don't know" and "no sun" are different facts. `todayRemainingWh` is deliberately
the one figure that is not a whole day.

## Quoting a plan without starting one

The endpoint this API is worth building for:

```bash
curl -sS -X POST http://gleanvolt.local:8090/api/v1/plans/targeted/preview \
  -H "Authorization: Bearer $API_KEY" -H 'content-type: application/json' \
  -d '{"targetSocPercent": 80, "departBy": "2026-08-24T07:00:00+02:00", "priority": "cheapest"}'
```

It returns the same plan the running mode would report — the strategy, the pace the charger must
hold, the solar and grid shares, the forecast surplus in the window, when the import would start,
what will actually arrive and, when it cannot be met, how far short and the departure that *would*
have covered it. Ask in `energyWh` instead of `targetSocPercent` on an installation with no vehicle
feed; the conversion, the horizon check and the just-in-time split are the same
[`TargetedChargeRequestFactory`](../src/Gleanvolt.Core/Strategies/TargetedChargeRequestFactory.cs) the web
form goes through, so a quote and the promise made from it can never disagree.

## Editing a quoted plan, and charging to it

Every quote carries an **`editable`** object: the parts of the plan you may change, in the shape you
send back.

```jsonc
"editable": {
  "planId": "…",                     // advisory; see below
  "notBefore": null,                 // the charger may not run before this
  "notAfter": null,                  // ...nor after it
  "forbiddenWindows": null,          // stretches that must stay idle
  "maxGridEnergyWh": null            // the most that may be bought
}
```

Change a field, put the whole thing back under `editable`, and either **quote it again** (the same
preview endpoint) or **charge to it**:

```bash
curl -sS -X POST http://gleanvolt.local:8090/api/v1/charging/start/targeted   -H "Authorization: Bearer $API_KEY" -H 'content-type: application/json'   -d '{"energyWh": 22000, "departBy": "2026-08-27T07:00:00+02:00",
       "editable": {"notBefore": "2026-08-27T02:00:00+02:00", "maxGridEnergyWh": 8000}}'
```

The preview and the start take the **same body**, so what you quote is what you commit to — the limits
included. Sending an unedited quote straight back is a no-op: you get the plan you were shown.

> **What you edit are limits, not a schedule — and that is the feature, not a simplification of it.**
> The plan is rebuilt on every poll from a refreshed forecast and the measured delivery, which is what
> lets a sunnier afternoon than forecast shrink the grid block before any of it is bought. Hand back a
> list of blocks to replay and that stops happening — and worse, the blocks go on being executed
> against a delivered-energy figure that stopped being true the moment they were quoted, buying grid
> for energy already in the car. So the window is yours; what happens inside it is still the
> forecast's to decide.

- **A limit may reduce what is delivered. It may never make the plan lie about what will be.** Anything
  a limit puts out of reach comes back as `shortfallWh`, exactly as a departure that is too close
  already does. `maxGridEnergyWh: 0` is a real value and means *sun only* — the request is met from the
  roof or not at all, and the rest is reported rather than quietly imported.
- **Only the impossible is refused.** Limits leaving the charger no time at all to run are a 400 with
  the reason, before anything starts. Limits that merely make the request *partial* are accepted —
  "buy at most 8 kWh and I'll take what that gets me" is a legitimate thing to ask for.
- **`planId` is advisory.** Send it back and the response's `forecastMovedSinceQuote` says whether the
  forecast has been refreshed since you were shown that plan. Nothing is stored server-side, nothing
  expires, and a start never fails because of it.
- **The limits follow the request's rules**: cleared by `/charging/stop`, and not surviving a restart.

`POST /charging/start` with `mode: "targeted"` accepts the same `editable` inside its `target`, so an
existing caller gains this without moving endpoints.

It goes through `ITargetedChargePreview`, which **sets no request, selects no mode and writes to no
device**, and it does not disturb a plan already running. So *"what does 80% by seven cost, and would
leaving at eight make it free?"* is three calls and no hardware writes — a question that otherwise
needs a person to fill in a form three times. Under `justInTime` the response also carries the same
request priced as cheaply as possible, so what holding the last stretch back costs can be read off
the difference in `gridEnergyWh`.

## Starting one

```bash
curl -sS -X POST http://gleanvolt.local:8090/api/v1/charging/start \
  -H "Authorization: Bearer $API_KEY" -H 'content-type: application/json' \
  -d '{"mode": "targeted", "target": {"energyWh": 22000, "departBy": "2026-08-24T07:00:00+02:00"}}'
```

One call does what one button does: the charger's Fast use-mode is written and the mode is then
selected, so it works on a charger sitting in Green. For `targeted` the request is set **before** the
mode — the controller reads both in the same cycle — and dropped again if the charger refuses.

`fastNoBattery` takes an optional `fast` on the same terms — set first, dropped if the charger
refuses. It says **when to stop**, not how to charge:

```bash
# 20 kWh, flat out, then stop.
-d '{"mode": "fastNoBattery", "fast": {"basis": "energy", "energyWh": 20000}}'

# To 60% of the pack, converted once, here.
-d '{"mode": "fastNoBattery", "fast": {"basis": "soc", "targetSocPercent": 60}}'

# Until the car says stop -- the default, and what omitting "fast" entirely means.
-d '{"mode": "fastNoBattery"}'
```

Add `departBy` and the charge is deferred instead — it starts as late as it can and still finish in
time:

```bash
-d '{"mode": "fastNoBattery", "fast": {"basis": "soc", "targetSocPercent": 90,
     "departBy": "2026-08-27T07:00:00+02:00"}}'
```

An amount that cannot be honoured is a **400 with the reason** — a battery target on an installation
with no configured pack capacity, no reading from the car, a car already past the figure asked
for, a departure in the past or beyond the horizon, or a departure with `full` and so nothing to time
— and nothing is started. Progress comes back on the status as `fastCharge`, with the schedule under
`fastCharge.schedule`, and a `full` start clears any amount left standing from an earlier charge.

Every action returns what it did *and the controller's state afterwards*, so a caller never has to
poll to find out what happened. A refused hardware write comes back as **200 with
`succeeded: false`** and a message rather than an HTTP error: the call was understood and the
controller is in a well-defined state — exactly the one it was in before. Read the flag, not the
status code.

`PUT /battery-hold` is refused with 409 when `BatteryHold:Enabled` is false, rather than recording an
intent that silently does nothing. And what comes back as `batteryHold.active` is **what was last
written to the inverter, not a read-back** — the command register cannot be read — so judge whether a
hold is really in force by `batteryPowerWatts`, never by the flag.

## The document is the deliverable

For a human client the OpenAPI file is documentation; for a generated MCP tool surface it is the
*entire* interface, and the descriptions are what a model reads before choosing a tool. So:

- **The DTOs are the API's own**, not Core records serialised straight out. Core records change for
  internal reasons; the wire contract must not move when they do.
- **The XML comments are the descriptions.** What is written next to a type, a property or an enum
  member in C# is what reaches the document. `Directory.Build.props` turns the documentation file on
  for **every** project, unconditionally, because that is early enough for the SDK to act on — set in
  `Directory.Build.targets` instead, the flag reads `true`, the compiler writes the `.xml` into `obj/`,
  and nothing ever copies it next to the assembly. That is documentation which exists, reports itself
  as enabled, and cannot be found. It is how every enum in `Gleanvolt.Core` reached the document with
  no description at all (#126).
- **Enums carry a legend**, because .NET's generator does not give them one: a hoisted enum arrives as
  a bare list of values, and neither the type's summary nor any member's survives. A document
  transformer reads the same XML at runtime and adds both, naming each value as it appears **on the
  wire**. It only ever fills blanks — a description the generator produced is left untouched — so the
  day the SDK does this itself, it quietly stops having anything to do.
- **Units live in the names** (`...Wh`, `...Watts`, `...Percent`), because a model that has to guess
  whether a number is watts or kilowatts will guess wrong. Timestamps are ISO-8601 **with an offset**:
  a departure time here is a local-time promise.
- **Enums cross the wire as camel-cased names**, closed, so a reordered member cannot silently change
  what a stored request meant.
- **The contract is pinned by a test.**
  [`OpenApiContract.json`](../tests/Gleanvolt.Api.Tests/OpenApiContract.json) records every operation id,
  parameter, response code and schema property with its type and nullability. An intended change shows
  up as a reviewable diff (regenerate with
  `GLEANVOLT_UPDATE_OPENAPI_CONTRACT=1 dotnet test tests/Gleanvolt.Api.Tests`); an accidental one — a
  renamed property, a dropped nullable — fails the build. Formatting changes from a new SDK do not,
  which is what keeps the check honest rather than merely noisy.

## What it deliberately does not do

- **Stop the service.** Stopping the whole controller is a deliberate, physical act with a deploy-stack
  consequence (it stays stopped); it lives on the surfaces a person is looking at.
- **Write raw Modbus registers.** That is a debugging tool, and a way to brick an inverter over the
  network.
- **TLS, users, per-key scopes, rate limiting.** A LAN appliance with a shared secret, as the web UI
  already is. Scopes become interesting the day a read-only key is wanted.
- **Bundle a Swagger or Scalar viewer.** The document plus `curl` is enough to start.
