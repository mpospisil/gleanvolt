# HTTP API (the `Api` section)

Everything the controller can see, and the actions that change something, for **programs rather than
people** — a script, a dashboard, or an MCP server that lets an LLM answer questions about this
installation and act on it. It is described by [OpenAPI](https://www.openapis.org/), so a client can be
generated from the document. Every action is one the web UI already has; the API adds no capability
of its own.

**Every field is in a base unit, and its name says which**: power in W (`…Watts`), energy in Wh
(`…Wh`), durations in seconds (`…Seconds`), distance in metres (`…Metres`), pressure in Pa
(`…Pascals`), percentages 0–100 (`…Percent`). Timestamps are ISO-8601 with an offset. Enums are
camel-cased names (`fastNoBattery`).

## Switching it on

**It is off by default**, because two of its endpoints write to hardware, and anything that writes is
switched on knowingly. It shares the web UI's port (8090).

**In the web UI**, on **Settings → Installation → API access** (or in the setup wizard's **Extras**
step): switch it on, type the client's name, and **Create key** makes a random key and keeps it in the
secret store. The API can't be switched on without a key. **Restart** on **Settings → Health** applies
it.

**Optionally, in configuration** — a key saved in the web UI wins over one with the same name here:

```bash
Api__Enabled=true
Api__Keys__claude-mcp=<a secret; e.g. from openssl rand -hex 32>
```

| Setting | Default | Meaning |
|---|---|---|
| `Api:Enabled` | `false` | Master switch. While false **no route is mapped and no document is served**. |
| `Api:Keys` | *(empty)* | The keys that may call it, as `name → secret`. Enabled with none is a **startup failure**. |
| `Api:MaxQueryRange` | `31.00:00:00` | The widest span one history query may ask for. |
| `Api:MaxSessions` | `500` | The most sessions one listing may return. |

**The key's name** is what the log and the charging session record as the source of an action —
`Api__Keys__claude-mcp` produces *"API (claude-mcp) started Targeted"* — so give each client its own
key.

Present one as a bearer token on every call that does anything:

```bash
curl http://gleanvolt.local:8090/api/v1/            # no key: what this is, and what it serves
curl -H "Authorization: Bearer $API_KEY" http://gleanvolt.local:8090/api/v1/status
```

**Settings → Installation** shows the API's address and keys, with that second command ready to copy.
**Treat a key like a password**: it can stop the charger.

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
| `GET /forecast` | Today and tomorrow **in full** — period by period plus each day's own median, p10, p90 and peak — from the cached forecast the poll loop is deciding on. `?weather=true` also fetches current conditions. See [The forecast agrees with itself](#the-forecast). |
| `GET /vehicle` | What the car last said — SOC, range, plug and charge state — **and how old the reading is**. |
| `POST /plans/targeted/preview` | What a targeted charge *would* do. Writes to nothing. |
| `POST /charging/start` | Start a mode (`solar`, `forecasted`, `solarGrid`, `fastNoBattery`, `targeted`). `targeted` needs a `target`; `fastNoBattery` takes an optional `fast` amount and departure. |
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

**Ranges are bounded** by `Api:MaxQueryRange` and `Api:MaxSessions`. **Staleness is reported**:
`/vehicle` carries `ageSeconds` and `stale` beside the state of charge, because a car's reading often
arrives hours late.

## The forecast

`GET /forecast` returns **two whole local days**, elapsed periods included, and every figure in it
comes from the same forecast: the periods on a date add up to that day's `expectedWh`, and
`peakPowerWatts` is the peak of the periods beside it.

**`days`** is a block per local day, with the four figures the Overview's forecast table shows:

```jsonc
"days": [
  { "date": "2026-09-23", "expectedWh": 24800, "lowWh": 18100, "highWh": 31400,
    "peakWatts": 6200, "complete": false, "heldFrom": "2026-09-23T10:30:00+02:00" },
  { "date": "2026-09-24", "expectedWh": 17400, "lowWh": 9600, "highWh": 26100,
    "peakWatts": 4300, "complete": true, "heldFrom": null }
]
```

`lowWh` and `highWh` (p10 and p90) say how sure the day is.

**Read `complete` before treating a day's periods as the whole day.** A controller started at 10:44 has
nothing for today before then; `heldFrom` says where the day's periods begin. A day nothing is held for
is **absent from `days`**, not zeroes. `todayRemainingWh` is the one figure that is not a whole day.

## Quoting a plan without starting one

```bash
curl -sS -X POST http://gleanvolt.local:8090/api/v1/plans/targeted/preview \
  -H "Authorization: Bearer $API_KEY" -H 'content-type: application/json' \
  -d '{"targetSocPercent": 80, "departBy": "2026-08-24T07:00:00+02:00", "priority": "cheapest"}'
```

It returns the same plan the running mode would report — the strategy, the pace the charger must
hold, the solar and grid shares, the forecast surplus in the window, when the import would start,
what will actually arrive and, when it cannot be met, how far short and the departure that *would*
have covered it. Ask in `energyWh` instead of `targetSocPercent` on an installation with no vehicle
feed. The checks and conversions are the same as the web form's, so a quote and the charge started
from it agree.

The preview **sets no request, selects no mode and writes to no device**, and does not disturb a plan
already running — so *"what does 80% by seven cost, and would leaving at eight make it free?"* is a few
calls and no hardware writes. Under `justInTime` the response also carries the same request priced as
cheaply as possible, so the difference in `gridEnergyWh` is what holding the last stretch back costs.

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

**You edit limits, not a schedule.** The plan is rebuilt on every poll from the refreshed forecast and
what has been delivered, so a sunnier afternoon than forecast still shrinks the grid block. The window
is yours; what happens inside it is the forecast's to decide.

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

`POST /charging/start` with `mode: "targeted"` accepts the same `editable` inside its `target`.

## Starting one

```bash
curl -sS -X POST http://gleanvolt.local:8090/api/v1/charging/start \
  -H "Authorization: Bearer $API_KEY" -H 'content-type: application/json' \
  -d '{"mode": "targeted", "target": {"energyWh": 22000, "departBy": "2026-08-24T07:00:00+02:00"}}'
```

One call does what one button does: the charger's Fast use-mode is written, then the mode is selected.
For `targeted` the request is set first, and dropped again if the charger refuses.

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

Every action returns what it did *and the controller's state afterwards*. A refused hardware write is
**200 with `succeeded: false`** and a message, not an HTTP error — the controller is in the state it
was in before. Read the flag, not the status code.

`PUT /battery-hold` is refused with 409 when `BatteryHold:Enabled` is false. `batteryHold.active` is
**what was last written to the inverter, not a read-back**, so judge whether a hold is in force by
`batteryPowerWatts`.

## What it does not do

- **Stop the service.** That is done from the web UI or Home Assistant.
- **Write raw Modbus registers.**
- **TLS, users, per-key scopes or rate limiting.** It is a LAN appliance with shared keys, like the web
  UI.
- **Serve a Swagger or Scalar viewer.** Use the document with a tool of your choice, or `curl`.
