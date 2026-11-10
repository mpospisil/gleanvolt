# The car and its telemetry

## The car (the `Ev` section)

**What the car *is*, as distinct from the feed that reports on it.** The same arrangement
[the installation](CONFIGURATION.md#the-pv-system-the-pv-section) has: described in one place, validated once at
startup, and handed to everything that needs it — so nothing has to assemble the car from settings
that belong to something else.

```jsonc
"Ev": {
  "Vehicles": [                          // a list from day one; exactly one entry is supported
    {
      "Id": "id4",                       // stable identity: a slug, like Pv:Id
      "Name": "The ID.4",
      "Make": "Volkswagen",
      "Model": "ID.4 Pro",               // reported, never acted on
      "BatteryCapacityKWh": 77,          // the car's *usable* pack; 0 = unset, see below
      "ChargeEfficiency": 0.9,           // charger meter -> cells
      "Phases": 3,                       // what the CAR can use -- see the warning below
      "MinChargingCurrentAmps": 6,       // below this it will not start
      "MaxChargingCurrentAmps": 16,      // its on-board charger's ceiling
      "Telemetry": { "Topic": "gleanvolt/vehicle/id4/state" }
    }
  ]
}
```

**Nothing here is required, and an absent section changes nothing.** Every figure falls back to the
installation's, which is exactly how the controller behaved before this section existed.

**Or edit it on [`/car`](WEB_UI.md#car)**, which covers every key above and the feed
sections below it, checks a save against the same rules startup applies, and writes to the overrides
file that wins over `.env`. The routes below still work and are what a fresh install is seeded from.

### Where to put it

Three routes, and which one you want depends on where the controller is running. All of them end at
the same `Ev:Vehicles:0:*` keys, so nothing behaves differently depending on how you got there.

| Running | File | Notes |
|---|---|---|
| **A deployed Pi** (the compose stack) | `deploy/.env` | Set `EV_ID`, `EV_NAME`, `EV_MAKE`, `EV_MODEL`, `EV_PHASES`, `EV_MIN_CHARGING_CURRENT_AMPS`, `EV_MAX_CHARGING_CURRENT_AMPS` — plus `VEHICLE_BATTERY_CAPACITY_KWH` and `VEHICLE_CHARGE_EFFICIENCY`, which keep their old names so an existing `.env` goes on working. `deploy/.env.example` documents each one; `docker-compose.yml` maps them onto the keys above. |
| **Locally, from your IDE or `dotnet run`** | `src/Gleanvolt.Worker/appsettings.Development.json` | The launch profile sets `DOTNET_ENVIRONMENT=Development`, so this file is read on a development machine and **never** by the deployed container. The right place for a car you only want configured while debugging. |
| **A plain, non-Development run** | `src/Gleanvolt.Worker/appsettings.json` | The shipped defaults, where the section exists with every field blank. |

Any single value can also be overridden by an environment variable using the double-underscore form —
`Ev__Vehicles__0__BatteryCapacityKWh=77` — which is what the compose file does under the covers, and
what to reach for when you want to change one figure without editing a file.

A car that is described but has **no telemetry feed** is a perfectly ordinary installation: the car
shows on the dashboard with its pack and its limits, no reading appears, and every kilowatt-hour
target works exactly as it always did. The `Ev` section says what the controller is charging; the
[`Vehicle` section](#vehicle-telemetry-the-vehicle-section) below is a separate, optional convenience
that reports on it.


`BatteryCapacityKWh` is the **usable** capacity — the figure the car's own SOC is a percentage of, not
the gross pack on the brochure (an ID.4 Pro is 77 usable of 82 gross). Unset by default, and it affects
one thing: the **Battery target (%)** basis on a
[targeted](MODE_TARGETED.md) or
[fast](MODE_FAST_NO_BATTERY.md#how-much-the-amount) charge, which cannot turn "80%" into kilowatt-hours without it and is
simply not offered until it is set. Guessing a pack size would make every such target quietly wrong
instead of visibly unavailable, which is the worse of the two failures.

`ChargeEfficiency` is the AC-side loss between the charger's meter and the cells, applied to that
conversion because the target is metered at the charger. It is **not**
`ChargeControl:Forecast:ChargeEfficiency`, which is the *home* battery's PV → pack figure.

### The three that are the point: phases, and the two currents

`ChargeControl:Phases`, `MinChargingCurrentAmps` and `MaxChargingCurrentAmps` describe **the
installation** — the wallbox and the supply feeding it. Until now the controller had only those and
used them as if they described the car too.

> ⚠️ **`Phases` is the one that goes wrong quietly.** Every watts↔amps conversion in the controller
> runs through a phase count. If your car charges single-phase behind a three-phase wallbox and you do
> not say so, **every power figure the controller reasons with is overstated threefold** — a
> [deferred fast charge](MODE_FAST_NO_BATTERY.md#when-the-departure) starts hours late, and the day plan budgets energy the
> car can never take. Nothing looks broken; the numbers are simply wrong.

`MinChargingCurrentAmps` matters for a car that refuses low currents. Commanded 6 A when it needs 8, it
draws nothing at all — and a connected car taking no power is what the fast mode's completion dwell
reads as *finished*. A charge that never started, filed as one that completed.

**The controller works to the narrower of the two, always:**

```
effective minimum = max(charger minimum, car minimum)     // whichever refuses first
effective maximum = min(charger maximum, car maximum)     // whichever gives out first
effective phases  = min(charger phases,  car phases)      // whichever offers fewer
```

A car can only ever **lower** a limit. An installation limited to 16 A stays limited to 16 A behind a
car that would take 32, because that limit is the site's supply and the wiring in the wall — not a
preference. Anything the car leaves unstated is simply the installation's figure. All three columns
are on `/pv-system`, so "why is my 32 A car charging at 16?" is answerable in a browser.

A car whose minimum is above the installation's maximum could never charge, and every symptom of that
is silence — so it is **refused at startup** naming both keys, rather than discovered one evening as
"I pressed the button and nothing happened".

### `OnDemandOnly` — ask the car, don't watch it

```jsonc
"Vehicle": { "OnDemandOnly": true }
```

Off by default. On, **nothing polls**: the car is asked when you press *Ask the car* on the dashboard,
or when a plan is prepared, and at no other time. Until you ask, the dashboard shows no battery and no
range.

That sounds like less, and it is more honest. On the EU Data Act portal a reading is **2–4 hours
behind** — the car reports promptly and VW's pipeline sits on it. Measured across a 7h43m window on the
reference ID.4: best case 1h48m, worst 7h16m, with five consecutive deliveries carrying byte-identical
car data. A card showing that number continuously is confident and wrong, and the eye reads the figure
rather than the age beside it. **A number nobody requested is a number nobody checked the age of.**

Everything else is unchanged: the reading still lands in the holder, the API still serves it, and an
installation that wants an ambient card simply leaves this off.

**The cost, which is real.** With nothing polling, no state of charge is recorded against a charging
session — `vehicle_soc_percent` stays null for its whole length. On a feed hours behind, those samples
were the same pre-session number repeated rather than a charge curve, so the loss is smaller than it
looks; but if you want them, leave this off.

### `Vehicle:Website` — volkswagen.de, the live source

**Or set it on [`/car`](WEB_UI.md#car)**: pick **Volkswagen**, and fill in the VW ID,
the password and the VIN. The password goes to the secret store rather than to a file beside the
configuration, and whichever feed it replaces is disabled in the same save.

```jsonc
"Vehicle": { "Website": { "Enabled": true, "Vin": "WVGZZZ…", "PollInterval": "00:05:00", "IdlePollInterval": "00:15:00" } }
```

Off by default. On, the car is read **continuously**: every `PollInterval` (5 min) while a charging
session is open, so the state of charge is recorded as it moves, and every `IdlePollInterval` (15 min)
otherwise, so the SOC a plan starts from — after a drive, before any charge — is the car's own and
recent.

**With this configured, the [EU Data Act portal](VW_PORTAL_SETUP.md) is not used**
([#212](https://github.com/mpospisil/gleanvolt/issues/212)): one car, one feed. `Vehicle:DataAct:Enabled`
may stay on — an ID.4 `.env` that has carried both since #170 keeps booting — and the startup log says
*"Vehicle:Website is configured, so the Data Act portal is not used"*. Why this one wins, measured on
the reference ID.4, both asked within a minute of each other:

| Source | SOC | Captured | Age |
|---|---|---|---|
| `volkswagen.de` | 55% | 08:39Z | ~1 h |
| EU Data Act portal | 55% | 21:16Z the day before | **~12.5 h** |

The car had reported; the portal had not yet published it. Portal publication runs **1h48m–7h16m**
behind the car, so a charge is over before its first in-charge reading appears there. What is given up:
the car's own **target SOC**, which only the portal carries for the ID.4 — nothing plans on it, so it is
simply unknown.

**Any failure stops the feed and asks you to sign in again.** A one-time code wanted, an answer
that is not a reading, or no answer at all: the feed goes to *sign-in required*, stops asking, and
replays nothing at VW's identity provider on a clock — that is how accounts get locked. The dashboard's
card, the band across every page and the `Car feed` entity all say so, with a link to **Vehicle
portal**. Sign in there and the feed is back on its clock within a minute, **without a restart**.

**Signing in is a page, not a setting.** A cold login *always* wants a one-time code emailed to the
account owner. That makes it hostile to a background service and perfectly ordinary at the moment you
are preparing a charge, so it is done from **Car → Account → Sign in**, where the code box is. The
settings above only say which account and which car; nothing signs in on its own, and opening the page
does not either.

**The session is kept in the data directory**, as `vw-website-session.json`, and it carries the
*remember this browser* grant — that file is why a restart does not cost another code. **Treat it as a
password**: anyone holding it is signed in as you. It is never logged and never rendered, and what
protects it on disk is [the secret store](CONFIGURATION.md#the-data-directory-holds-secrets-the-secrets-section) —
owner-only on Linux, DPAPI on a Windows install, and named on the startup log and `/health`.

If the session lapses mid-charge, the charge is unaffected: the feed reports *sign-in required*, the
dashboard's card says *volkswagen.de wants a one-time code — sign in again on the Car page* with a
link to it, and nothing that writes to hardware depends on the feed.

| Setting | Default | |
|---|---|---|
| `PollInterval` | `00:05:00` | While a charging session is open (`VW_WEBSITE_POLL_INTERVAL`) |
| `IdlePollInterval` | `00:15:00` | Otherwise (`VW_WEBSITE_IDLE_POLL_INTERVAL`) |

### `Vehicle:Skoda` — the MyŠkoda Public API, the live source for a Škoda

**Or set it on [`/car`](WEB_UI.md#car)**: pick **Škoda** and fill in the VIN. The API
key is not asked for in the form — it is pasted under **Account** further down the same page,
because keys expire and renewing one is a recurring owner action.

```jsonc
"Ev":      { "Vehicles": [ { "Id": "enyaq", "Name": "The Enyaq", "Make": "Škoda", "Model": "Enyaq 85", "BatteryCapacityKWh": 77 } ] },
"Vehicle": {
  "Skoda":   { "Enabled": true, "Vin": "TMBJ…" }    // SKODA_ENABLED / SKODA_VIN in deploy/.env
}
```

The part `Vehicle:Website` plays for a Volkswagen, for a Škoda — which volkswagen.de cannot read. It uses
the [MyŠkoda Public API](https://public.api.connect.skoda-auto.cz/docs) Škoda published for owners in
2026-08 (MySkoda app 8.16). **Built from the published spec and not yet verified on a car** — see
[issue #193](https://github.com/mpospisil/gleanvolt/issues/193) for what only a real Škoda can settle.

| Setting | Default | |
|---|---|---|
| `Enabled`, `Vin` | off, empty | Both required. **This section chooses the feed** — `Ev:Vehicles[].Make` never does |
| `ChargingPollInterval` | `00:05:00` | While a charge is running |
| `IdlePollInterval` | `00:15:00` | Between charges: the state of charge before a charge starts is what a % target is planned from |
| `BaseUrl`, `SourceId` | Škoda's API, `skoda` | A hedge, not an abstraction: the test environment, or another VW Group brand if one ever publishes the same API |

**The key is pasted on a page, and is in no setting.** Create an API key for this car in the MySkoda
app, then paste it under **Account** on [`/car`](WEB_UI.md#car). One request (`?include=info`) proves the key, the VIN and that
one covers the other; the page then says *Key valid until … · your car's name*, and the feed starts
without a restart. An expired key, an unknown key, a key that does not cover the VIN and an unknown VIN
each get their own sentence and nothing is stored. Keys expire: from a week before, the feed is
*Degraded* and says when, and renewing is pasting a new key on the same page. **Sign out** deletes the
stored key outright — no tombstone keeps the value — although the key keeps working at Škoda until you
revoke it in the app, because the API has no revoke.

**The key is not read-only** (the same key starts and stops charging), so it is treated as a
password: never logged, never rendered, never in the REST API or Home Assistant, and kept in
[the secret store](CONFIGURATION.md#the-data-directory-holds-secrets-the-secrets-section) as `skoda-api-key.json`.
Gleanvolt's client has no method for any command endpoint.

**Twenty requests an hour, per VIN**, shared with *Ask the car* and the sign-in. The feed takes four an
hour idle and twelve while charging, stops the clock for the window once only three are left, and honours
`Retry-After`. The quota being spent and the car itself declining (low 12 V battery, deep sleep) are two
different *Degraded* sentences; a key problem is *sign-in required* until a new key is pasted.

**`Vehicle:Website` and `Vehicle:Skoda` both enabled stops startup**, naming both: they are live sources
for two different cars, and an installation has one. **With either configured, the Data Act portal is
not started** ([#212](https://github.com/mpospisil/gleanvolt/issues/212)) — skipped with one startup
log line rather than refused. With no key yet, or an expired one, the dashboard's card says *No MyŠkoda
API key yet, or it has expired — paste one on the Car page*, with a link to it.

## Vehicle telemetry (the `Vehicle` section)

**Or set it on [`/car`](WEB_UI.md#car)**: pick *A feed I publish myself* and name the
topic. The broker and its credentials stay here — they are the house broker's, not the car's.

The **feed** that reports on the car above — as distinct from the car itself, and from the home battery
the inverter reports. Off by default:

```jsonc
"Vehicle": {
  "Enabled": false,
  "BrokerHost": "localhost",
  "BrokerPort": 1883,
  "MaxAge": "12:00:00"                  // past this a reading is shown as stale, and a percentage
                                        //   target refuses to convert from it (#179)
}
```

The **topic** is not here: it lives on the vehicle, as `Ev:Vehicles:0:Telemetry:Topic`, because two
cars on one broker are two topics. The broker, the credentials and the staleness guard stay here
because they describe the feed rather than the car — which is the whole split this section and the one
above exist to make.

`Vehicle:Username` / `Vehicle:Password` are supported for an authenticated broker and are secrets —
supply them via `.env` or an environment variable (`Vehicle__Username`), never in `appsettings.json`.

The feed as configured — broker, username, client id, and the topic it actually subscribed to — is
shown at [`/pv-system`](WEB_UI.md#settings--installation), which is also where a feed switched on
with no topic reads as the misconfiguration it is rather than as a car that never reports.

> **Upgrading?** `Vehicle:BatteryCapacityKWh`, `Vehicle:ChargeEfficiency` and `Vehicle:Topic` have
> moved into the `Ev` section, and a build that still finds them **refuses to start**, naming the
> replacement. Silently ignoring one would leave a capacity your file says is set and the controller
> has stopped reading — which makes every SOC-based target quietly wrong. If you deploy with
> `deploy/docker-compose.yml`, the `.env` variable names are unchanged and there is nothing to do.

Nothing in `ChargeControl` or `BatteryHold` consumes it, and no charge decision depends on it: a feed
that dies changes nothing about how the charger is driven. It appears on the web UI dashboard and on
the [targeted plan](MODE_TARGETED.md), where its SOC can be *converted*
into a request the owner then confirms — an input to what you ask for, never to how it is delivered.
It is not republished to Home Assistant, since Home Assistant is where it comes from.

### It reads MQTT, not a car API

The default source is a topic, not a manufacturer. The controller subscribes to **one topic with one
JSON schema**, and each car is adapted onto that schema by a template or automation in Home Assistant:

```jsonc
{
  "captured_at":  "2026-08-17T10:44:23+00:00",   // required: the CAR's capture time
  "soc_percent":  28,                            // optional, 0-100
  "range_km":     176,                           // optional: the CAR's own range estimate
  "charge_time_remaining_minutes": 95,           // optional: the CAR's own estimate
  "charge_state": "charging",                    // optional: idle | charging | complete | unknown
  "plug_state":   "connected",                   // optional: connected | disconnected | unknown
  "source":       "id4"                          // optional, for display; defaults to "mqtt"
}
```

Every source spells things differently — the VW EU Data Act portal emits
`CHARGE_STATE_CHARGING_HV_BATTERY`, `volkswagen_connect` emits `notReadyForCharging`, an OBD dongle
emits raw CAN. Doing that mapping in Home Assistant rather than here means **a second car costs no
code**: a Škoda Elroq, a Tesla, a Kia — anything Home Assistant can see becomes a source by copying
one automation.

There is exactly one manufacturer integration in the codebase, and it is
[the VW Group portal below](#the-car-from-the-manufacturer-on-a-clock-the-vehicledataact-section). It
exists because the EU Data Act gives the *owner* their car's data directly, with no Home Assistant in
the middle — and it writes the same `VehicleState` this schema produces, through the same holder. It is
the exception, not the new default: anything that needs its own credentials, its own session and its
own failure modes has to earn a place here one manufacturer at a time.

Everything except `captured_at` is optional, and absent is a supported configuration rather than an
error. `captured_at` is required because it is the **car's** capture time, not the arrival time, and
without it staleness cannot be judged.

`source` names the feed on the dashboard (*"reported by the car 12 min ago via id4"*). It is optional: a payload that omits it is labelled `mqtt`, so a reading off this topic can always be
told apart from the manufacturer feed's.

`range_km` is the car's own estimate off its own recent consumption — nothing here could compute it, and
it is the figure that actually answers *"is 80% enough for the trip?"* while you are setting a target.
Display only: no plan reads it. `0` is a real reading (a flat car reports it) and absent is null, so the
two are never confused; a figure beyond 2000 km is rejected as a template publishing metres.

`charge_time_remaining_minutes` is the **car's own** estimate of how much longer it needs — it knows
its charge curve, its taper and its target, and nothing here does. Publish the key only when the car
actually reports it: `0` means "the car says it is finished", which is a different fact from "the car
didn't say", and the templates below omit the key rather than defaulting it. A value outside
0–10080 minutes is rejected as a broken template (usually seconds published as minutes) along with the
rest of that payload.

### Publishing from Home Assistant

For a VW ID.4 via the [`volkswagen_connect`](https://github.com/rafaelhutter/ha-volkswagen-connect)
integration. Publish **retained**, so a controller restart is handed the last known reading instead of
waiting up to a quarter of an hour for the next one.

> **Two shapes, one automation.** Home Assistant accepts automations in two places, and they are
> indented differently — pasting the wrong shape fails validation, which is the most common way this
> step goes wrong. Use the second block below if you are working in the UI, which most people are.

In `configuration.yaml`, under a top-level `automation:` key:

```yaml
automation:
  - alias: Publish ID.4 state to Gleanvolt
    trigger:
      - platform: state
        entity_id:
          - sensor.id_4_pro_performance_battery
          - sensor.id_4_pro_performance_charging_state
          - sensor.id_4_pro_performance_plug
    condition:
      - "{{ states('sensor.id_4_pro_performance_battery') | int(-1) >= 0 }}"
    action:
      - service: mqtt.publish
        data:
          topic: gleanvolt/vehicle/id4/state
          retain: true
          payload: >-
            {% set cs = states('sensor.id_4_pro_performance_charging_state') %}
            {% set left = states('sensor.id_4_pro_performance_charging_time_left') %}
            {"captured_at": "{{ states('sensor.id_4_pro_performance_last_vehicle_report') }}",
             "soc_percent": {{ states('sensor.id_4_pro_performance_battery') | float(0) }},
             "range_km": {{ states('sensor.id_4_pro_performance_range') | float(0) }},
             {% if left not in ['unknown', 'unavailable', 'none', ''] %}
             "charge_time_remaining_minutes": {{ left | float(0) }},
             {% endif %}
             "charge_state": "{{ 'charging' if cs == 'charging'
                                 else 'idle' if cs in ['notReadyForCharging','readyForCharging']
                                 else 'unknown' }}",
             "plug_state": "{{ states('sensor.id_4_pro_performance_plug') }}",
             "source": "id4"}
```

Or in the UI — **Settings → Automations & scenes → + Create automation → Create new automation**, then
the **⋮ menu → Edit in YAML**. That editor holds one automation's *body*, so there is no `automation:`
key and no leading `- `, and everything sits four spaces further left:

```yaml
alias: Publish ID.4 state to Gleanvolt
trigger:
  - platform: state
    entity_id:
      - sensor.id_4_pro_performance_battery
      - sensor.id_4_pro_performance_charging_state
      - sensor.id_4_pro_performance_plug
condition:
  - "{{ states('sensor.id_4_pro_performance_battery') | int(-1) >= 0 }}"
action:
  - service: mqtt.publish
    data:
      topic: gleanvolt/vehicle/id4/state
      retain: true
      payload: >-
        {% set cs = states('sensor.id_4_pro_performance_charging_state') %}
        {% set left = states('sensor.id_4_pro_performance_charging_time_left') %}
        {"captured_at": "{{ states('sensor.id_4_pro_performance_last_vehicle_report') }}",
         "soc_percent": {{ states('sensor.id_4_pro_performance_battery') | float(0) }},
         "range_km": {{ states('sensor.id_4_pro_performance_range') | float(0) }},
         {% if left not in ['unknown', 'unavailable', 'none', ''] %}
         "charge_time_remaining_minutes": {{ left | float(0) }},
         {% endif %}
         "charge_state": "{{ 'charging' if cs == 'charging'
                             else 'idle' if cs in ['notReadyForCharging','readyForCharging']
                             else 'unknown' }}",
         "plug_state": "{{ states('sensor.id_4_pro_performance_plug') }}",
         "source": "id4"}
mode: single
```

`alias` is the automation's name, so Home Assistant fills that in for you on save.

Then set `Ev:Vehicles:0:Telemetry:Topic` to `gleanvolt/vehicle/id4/state`. The `condition` matters: it stops a payload
being published while the integration's entities read `unavailable`, which happens whenever its cloud
session expires.

**Check the range entity name against your own install** too — `sensor.<car>_range` is what
`volkswagen_connect` exposes here, but it is named differently by other integrations. Drop the line
entirely if yours has none: an absent `range_km` is a supported configuration, and the tile reads "—".

**Check the remaining-time entity name against your own install** — it varies by integration and by
car, and `volkswagen_connect` does not expose one on every model. If yours has no such sensor, delete
the `{% if %}` block: the guard already omits the key when the entity is missing, and a feed that never
publishes it is a supported configuration, not a fault.

**Don't wait for the trigger to prove it works.** It fires on a *state change* of those three sensors,
and a parked car may not produce one for hours. Publish immediately with the automation's
**⋮ → Run actions**, which skips both the trigger and the condition. The controller logs the result:

```
[22:56:17 INF] First vehicle reading from gleanvolt/vehicle/id4/state:
               SOC=28% charge=Idle plug=Disconnected captured 2026-08-17T10:44:23+00:00
```

If instead you see `Ignoring vehicle telemetry ... 'soc_percent' was a JSON String, expected a number`,
the source entity was `unavailable` when the payload was built — which is what the `condition` prevents
on a real trigger but cannot prevent when you force it with *Run actions*.

Note the capture time comes from **`last_vehicle_report`**, not that integration's `data_captured`
sensor — the latter belongs to its EU Data Act source and reads `unknown` until that is separately
configured.

### Why it is advisory only, and always will be

Measured on the reference install, not assumed:

- A parked car's report was **2 hours** old on one reading and **3.5 hours** on another. Hours stale is
  the normal case, not a fault — and not a problem either, since a parked car's SOC does not drift.
- The upstream cloud session **expired after ~15 hours**, taking every entity to `unavailable`.
  Recovery needed a human re-entering a password plus an email OTP.
- **Target SOC is not reliably available at all**: the portal's field is null unless the car has an
  active charge plan, and the EU Data Act equivalent needs a separate request that can take days to
  start delivering.

So `MaxAge` exists to catch a **dead feed**, not to reject merely old numbers, and a charge target
stays a Gleanvolt setting rather than something read from the car. Anything that writes to hardware
must behave identically when this feed is absent, stale, or gone.

That last rule still holds, and `MaxAge` now does one thing beyond greying a number
([#179](https://github.com/mpospisil/gleanvolt/issues/179)): a target asked for **as a percentage**
refuses to convert from a reading older than it, on all three doors — the web tabs, the HTTP API and
the Home Assistant button. That is not a hardware dependency in disguise. It refuses to *compute an
amount* from a number it cannot vouch for, and the amount is exactly what nothing downstream ever
re-derives; asking in kilowatt-hours is unaffected, so no charge is ever gated on the feed being
alive. The full argument is under
[Setting a target](MODE_TARGETED.md#setting-a-target).

### The car from the manufacturer, on a clock (the `Vehicle:DataAct` section)

**Or set it on [`/car`](WEB_UI.md#car)**: the brand comes from the list the client
itself knows, so there is no GUID to fetch out of a redirect URL, and `ClientId` is behind a
disclosure for the day one of those ids goes stale.

The second way to feed the same card: the controller signs in to VW's own **EU Data Act portal** on a
schedule and writes what it finds into the same reading everything else reads. One car, one
manufacturer, and **off by default** — and **only for a car with no live feed**: with
[`Vehicle:Website`](#vehiclewebsite--volkswagende-the-live-source) or
[`Vehicle:Skoda`](#vehicleskoda--the-myškoda-public-api-the-live-source-for-a-škoda) configured, it is
not started ([#212](https://github.com/mpospisil/gleanvolt/issues/212)). It is the feed for a Cupra, a
SEAT or another Group brand:

```jsonc
"Vehicle": {
  "DataAct": {
    "Enabled": false          // read the portal on a clock, as opposed to only when the button is pressed
  }
}
```

The credentials are the four from
[docs/VW_PORTAL_SETUP.md](VW_PORTAL_SETUP.md) — `Vehicle__DataAct__Username`,
`__Password`, `__Brand` and, with more than one car on the account, `__Vin`. Secrets, supplied via
`.env` or an environment variable exactly as `Solcast__ApiKey` is; the shorter `VW_*` names are honoured
beside them and the sectioned form wins where both are set. Being *blunt about that password*: it is
not a read-only credential. The same account that reads the car's state also unlocks and locates it,
there is no scoped token to use instead, and what protects it here is that `.env` is gitignored and the
client never logs or renders it. That is genuinely all the protection a LAN appliance offers, and
[#137](https://github.com/mpospisil/gleanvolt/issues/137) took the trade knowingly rather than casually.

**Having credentials is not switching it on.** The **Ask the car** button on [`/car`](WEB_UI.md#car)
works as soon as they are set; reading the portal *on a clock* waits for `Enabled`. Press the
button first — that is what proves the credentials and that this car's fields are understood — and
switch the feed on afterwards.

**The interval belongs to the service, and is not a setting.** It asks every fifteen minutes, because
the portal is a batch delivery whose own continuous data request runs at that frequency and asking
faster achieves precisely nothing. No figure the host could pick would be right for both this and a car
that has to be woken up to answer, which is the reason the number lives in the service rather than in
your file.

**One reading is assembled from as many deliveries as it takes.** The portal sends *partial*
deliveries — each carries the reports that changed — so the newest one on its own is a coin toss over
which report type arrives. On the reference ID.4 it held the odometer, the charge target, the doors and
the climate, and no state of charge at all, while the car's SOC was perfectly well known elsewhere at
that moment. So a read takes the newest delivery and keeps merging older ones **while the reading is still short
of something** — the battery, the range, the charging state, the plug, the remaining time — up to four
(`Vehicle:DataAct:MaxDatasetsPerRead`, or `VW_MAX_DATASETS`). Stopping at the first state of charge was the first attempt and
was wrong: the reports are split by type, so it produced a card with a battery and four dashes. A delivery that carries a whole car costs exactly one
download; a car whose reports are split will spend most of the budget, and the last of it on a field
the car may simply never send — the log says what a read is still short of, which is how to tell
whether raising the number would buy anything. It bounds what a read costs over a domestic uplink and
nothing else: the portal delivers when the car reports rather
than on a clock — the reference install was offered thirty deliveries whose newest four covered most of
a day — so how old the answer is, is said by the reading's own capture time and judged by `MaxAge`.

The merge needs no new rules — several snapshots, sentinels filtered first, newest real value wins is
what the mapper already does — but it does make one thing load-bearing: a reading is dated by **its
state of charge's own snapshot**, not by the newest report downloaded. Everything else on the card is
display; the percentage is what a kWh target is computed from and what `MaxAge` is judged against, so
a battery reading from yesterday afternoon merged beside this morning's odometer reads as yesterday
afternoon — which is the only answer that lets you decide whether to trust it.

**It holds one session rather than replaying a password.** The button signs in afresh on every press,
which is right by hand and wrong on a schedule — repeatedly replaying a password at a real identity
provider is how accounts get locked. The feed keeps one session and signs in again only when the portal
bounces it, and it **times how long each session lasted** and logs it: nobody knows what a portal
session's life is, because until this nothing had ever kept one.

**A failure the owner has to fix stops the feed rather than slowing it.** A refused password, a consent
screen, an emailed one-time code, a CAPTCHA, or an account with no continuous data request at all: the
service stops asking. Ordinary failures — a 5xx, a timeout, an expired session, a delivery that has not
been filled yet — are `Degraded` instead: they back off (to at most an hour) and clear themselves.

**A stopped feed is said four ways, because it does not clear itself.**

- A band across the top of **every page** of the web UI: *Sign-in required*, the service's sentence, and
  a link to the portal page. It follows the poll, so a browser left open overnight grows one.
- The dashboard's vehicle card, next to the reading it explains.
- **Health**, where "is anything wrong?" is actually asked, as a `Car feed` row — beside three that
  say whether the feed is *running*: **Feed reads** (readings of attempts, and how long ago the last
  one landed), **Portal session** (its age, and how many sign-ins there have been — *one* is the
  healthy answer however long the controller has been up, and more says sessions are expiring), and
  **Next read**, which reads `never — the feed has stopped` when it is blocked.
- The log, as a **warning** — repeated every six hours for as long as it lasts, because a warning
  written once has scrolled out of `docker logs --tail` by the time anybody reads it, and a silent
  stopped feed is indistinguishable from a parked car. The reminder is a log line and never a request:
  nothing is fetched while the feed is blocked.

Plus the [**Car feed**](HOME_ASSISTANT.md#what-each-entity-means) entity going to `NeedsOwner`, which is what a Home
Assistant notification keys off. Clear it in a browser, press the portal button to check, and restart
the controller to put the feed back on its clock.

**About that emailed code.** If the portal ever answers with a one-time code, a CAPTCHA or an
authenticator prompt, the client recognises it *before* posting anything and never retries — a password
put into a code form tells nobody anything, and a screen a program cannot answer is not made answerable
by asking again. It is recognised two ways: by the words on the page, and by a field asking for a code
(`otp`, `emailOtp`, `securityCode` and their like), which is what still works on a page in a language
the word list does not cover. On the reference account this has not appeared — six cold sign-ins with
only an email and a password, no code and no CAPTCHA — but the *first* sign-in in a browser does show a
consent screen, and holding one session rather than signing in ninety-six times a day is partly there to
keep new-device challenges rare.

> **With this on, both feeds run and the freshest reading wins.** This used to exclude the MQTT worker
> outright — one holder, one writer, no race — until the reference install showed why picking a winner
> in advance is the wrong shape: the portal's state of charge for that car is coarser and lags, while
> the same manufacturer's app API through Home Assistant is live to the percent. Which source is better
> is a fact about a car and a moment, not something a configuration file can be right about once. So
> both write to one reading, whichever saw the car most recently is kept, and a feed that stops stops
> advancing — so the other takes over within one reading rather than by being configured. The
> controller says at startup when both are on, and the dashboard names which feed the reading on screen
> came from.

**This was the reference site's baseline vehicle source from 2026-09-08** until
[#212](https://github.com/mpospisil/gleanvolt/issues/212) settled on one feed per car: the reference
ID.4 now reads [`vw-website`](#vehiclewebsite--volkswagende-the-live-source) alone. What follows is the
history of that choice.

[#141](https://github.com/mpospisil/gleanvolt/issues/141) ran both feeds side by side for six days and
handed over on what they delivered. The finding that decided it: **the two feeds carry the same car
reports.** Six of seven MQTT capture times matched a portal capture within ninety seconds, four of them
within six — `volkswagen_connect` and the Data Act portal both surface the ID.4's own
`car_captured_time`, by different roads. Neither can be fresher than the car chooses to be, so the
choice between them is about which road is more dependable, not about the data.

What the portal gains: `settings.target_soc` (80%, on every read) and a charge-time-remaining, neither
of which the MQTT feed ever carried. What it loses: **plug state**, which it has never once reported —
the charger's own view of whether a car is connected is unaffected and is the one that matters. What it
costs: a portal session lasts about an hour, so it signs in roughly thirty times a day, unattended and
without an OTP. The numbers, including the ones that argue against it, are in
[docs/DECISIONS.md](DECISIONS.md).

**The handover is a setting, and nothing was deleted.** `Vehicle__Enabled=false`, the Home Assistant
vehicle automation stopped, `Vehicle__DataAct__Enabled=true`. `VehicleMqttWorker`, the payload
contract, `Vehicle__Broker*`, `Vehicle__Topic` and the per-car `Telemetry:Topic` all stay in the tree
and keep working. That is not sentiment about the old feed: **it is the only path that works for a car
nobody has written a service for**, which is still most of them, and it is what a second vehicle on
this installation would use. Turning it back on is one line.

Everything else about the feed is unchanged: `MaxAge` still judges staleness, nothing in `ChargeControl`
or `BatteryHold` reads it, and an installation with `Enabled: false` behaves exactly as it did before
any of this existed.
