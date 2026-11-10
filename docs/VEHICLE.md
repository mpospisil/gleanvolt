# The car and its telemetry

> **Using Gleanvolt?** The user manual has this step by step: [Your car and its battery level](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/car/). This page is the reference behind it.

Two things are configured separately:

- **The car** (the `Ev` section) — what the controller is charging: its pack, its phases and the
  currents it accepts. This makes charging correct, with or without any feed.
- **The feed** (the `Vehicle` section) — what reports the car's own state of charge, range and plug
  state. Optional. It shapes what you *ask* for — a target can be a battery percentage — and never
  how a charge is delivered.

**Set both on the [Car page](WEB_UI.md#car) of the web UI** (or in the setup wizard's **Car** step):
choose the manufacturer and it shows the fields that feed needs. That is the recommended way. The
configuration keys below are what that page writes, given for reference and as an optional
alternative.

## The car (the `Ev` section)

On the **Car** page: the name, make and model, phases, the two currents, the usable battery and the
charge efficiency. In configuration:

```jsonc
"Ev": {
  "Vehicles": [                          // a list; exactly one entry is supported
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

**Nothing here is required.** Every figure the car leaves out falls back to the installation's.

Optionally, outside the web UI:

| Where it runs | Where to set it |
|---|---|
| **The Docker stack** | `deploy/.env`: `EV_ID`, `EV_NAME`, `EV_MAKE`, `EV_MODEL`, `EV_PHASES`, `EV_MIN_CHARGING_CURRENT_AMPS`, `EV_MAX_CHARGING_CURRENT_AMPS`, `VEHICLE_BATTERY_CAPACITY_KWH` and `VEHICLE_CHARGE_EFFICIENCY`. `deploy/.env.example` documents each. |
| **`dotnet run` / your IDE** | `src/Gleanvolt.Worker/appsettings.Development.json`, which the deployed container never reads. |
| **Anywhere** | An environment variable, such as `Ev__Vehicles__0__BatteryCapacityKWh=77`. |

The **Car** page writes to the overrides file, which wins over all of these.

**`BatteryCapacityKWh`** is the **usable** capacity — what the car's own SOC is a percentage of, not
the gross figure on the brochure (an ID.4 Pro is 77 usable of 82 gross). Without it, a target can't be
given as a battery percentage on a [targeted](MODE_TARGETED.md) or
[fast](MODE_FAST_NO_BATTERY.md#how-much-the-amount) charge; kWh targets still work.

**`ChargeEfficiency`** is the loss between the charger's meter and the car's cells, used for that same
conversion. It is not `ChargeControl:Forecast:ChargeEfficiency`, which is the *home* battery's.

### Phases and the two currents

`ChargeControl:Phases`, `MinChargingCurrentAmps` and `MaxChargingCurrentAmps` describe the
installation: the wallbox and its supply. The car's own figures can only narrow them:

```
effective minimum = max(charger minimum, car minimum)     // whichever refuses first
effective maximum = min(charger maximum, car maximum)     // whichever gives out first
effective phases  = min(charger phases,  car phases)      // whichever offers fewer
```

> ⚠️ **Set `Phases` if the car charges on fewer phases than the wallbox has.** Every watts↔amps
> conversion uses it. A single-phase car behind a three-phase wallbox, left unstated, makes every power
> figure three times too high: a [deferred fast charge](MODE_FAST_NO_BATTERY.md#when-the-departure)
> starts hours late, and the day plan budgets energy the car can't take. Nothing looks broken.

**Set `MinChargingCurrentAmps` for a car that refuses low currents.** A car that needs 8 A, given 6 A,
draws nothing — which looks like a finished charge.

All three effective values are shown on **Settings → Installation**. A car whose minimum is above the
installation's maximum could never charge, so it is **refused at startup**, naming both keys.

## Where the car's state of charge comes from

**One car, one feed.**

| Feed | For | Freshness | Section |
|---|---|---|---|
| **volkswagen.de** | Volkswagen | ~20 s; plug state; live while charging | [`Vehicle:Website`](#volkswagende-vehiclewebsite) |
| **MyŠkoda Public API** | Škoda | 5–15 min; plug state | [`Vehicle:Skoda`](#myškoda-vehicleskoda) |
| **EU Data Act portal** | Audi, SEAT, Cupra, Bentley, VW commercial (and any VW Group car) | hours behind; no plug state | [`Vehicle:DataAct`](#eu-data-act-portal-vehicledataact) |
| **Your own MQTT topic** | any car Home Assistant can see | whatever publishes it | [`Vehicle`](#your-own-feed-over-mqtt-the-vehicle-section) |
| **None** | any car | — | targets in kWh only |

- `Vehicle:Website` and `Vehicle:Skoda` both enabled **stops startup**: an installation has one car.
- With either of them configured, **the Data Act portal is not started** (one log line says so).
- The Data Act portal and your own MQTT topic *can* both run; the most recent reading wins.

**The feed is advisory.** Nothing that writes to the charger or the inverter reads it, so a feed that
is stale, failing or absent changes nothing about how a charge is driven. A reading older than
`Vehicle:MaxAge` (12 hours by default) is shown as **stale**, and a target asked for as a percentage
refuses to convert from it; a target in kWh is unaffected. See
[Setting a target](MODE_TARGETED.md#setting-a-target).

The feed's health is shown on the Overview's vehicle card, as a band across every page when it needs
you, on **Settings → Health**, and as the [**Car feed**](HOME_ASSISTANT.md#what-each-entity-means)
entity in Home Assistant: `Ok`, `Degraded` (it retries and clears itself) or `NeedsOwner` (it has
stopped and waits for you).

### volkswagen.de (`Vehicle:Website`)

On the **Car** page: choose **Volkswagen**, and enter the VW ID, the password and the VIN.

```jsonc
"Vehicle": { "Website": { "Enabled": true, "Vin": "WVGZZZ…", "PollInterval": "00:05:00", "IdlePollInterval": "00:15:00" } }
```

| Setting | Default | |
|---|---|---|
| `PollInterval` | `00:05:00` | While a charging session is open (`VW_WEBSITE_POLL_INTERVAL`) |
| `IdlePollInterval` | `00:15:00` | Otherwise (`VW_WEBSITE_IDLE_POLL_INTERVAL`) |

**Sign in on the Car page, under Account.** A first sign-in always asks for a one-time code emailed to
the account owner, so it is done by hand: **Car → Account → Sign in**, then the code. Nothing signs in
by itself.

**Any failure stops the feed until you sign in again** — a code wanted, an unexpected answer, or no
answer. It does not retry on a clock, because repeated sign-ins are how accounts get locked. Sign in
again on the Car page and the feed resumes within a minute, without a restart. A charge in progress is
unaffected.

**The session is kept in the data directory** as `vw-website-session.json`, which is why a restart
does not cost another code. **Treat it as a password**: anyone holding it is signed in as you. It is
protected by [the secret store](CONFIGURATION.md#the-data-directory-holds-secrets-the-secrets-section).

The Data Act portal is the only source of the car's own **target SOC**; with volkswagen.de it is
unknown, and nothing plans on it.

### MyŠkoda (`Vehicle:Skoda`)

On the **Car** page: choose **Škoda** and enter the VIN; then paste an API key under **Account**.

```jsonc
"Vehicle": {
  "Skoda": { "Enabled": true, "Vin": "TMBJ…" }    // SKODA_ENABLED / SKODA_VIN in deploy/.env
}
```

| Setting | Default | |
|---|---|---|
| `Enabled`, `Vin` | off, empty | Both required. **This section chooses the feed** — `Ev:Vehicles[].Make` never does |
| `ChargingPollInterval` | `00:05:00` | While a charge is running |
| `IdlePollInterval` | `00:15:00` | Between charges |
| `BaseUrl`, `SourceId` | Škoda's API, `skoda` | For Škoda's test environment |

It uses the [MyŠkoda Public API](https://public.api.connect.skoda-auto.cz/docs). **It is built from the
published spec and not yet verified on a car** — see
[issue #193](https://github.com/mpospisil/gleanvolt/issues/193).

**The API key.** Create one for this car in the MySkoda app and paste it under **Account** on the Car
page. One request checks the key and the VIN; the page then says *Key valid until … · your car's name*
and the feed starts without a restart. Problems with the key or the VIN each get their own message and
nothing is stored. From a week before the key expires the feed is *Degraded* and says when; renewing
is pasting a new key. **Sign out** deletes the stored key, but the key keeps working at Škoda until you
revoke it in the app.

**The key can also start and stop charging**, so it is treated as a password: never logged or shown,
kept in [the secret store](CONFIGURATION.md#the-data-directory-holds-secrets-the-secrets-section) as
`skoda-api-key.json`. Gleanvolt only reads with it.

**Twenty requests an hour per VIN**, shared with *Ask the car* and the key check. The feed uses four an
hour idle and twelve while charging, pauses when only three are left, and honours `Retry-After`.

### EU Data Act portal (`Vehicle:DataAct`)

For a VW Group car with no live feed above. On the **Car** page: choose the brand (Audi, SEAT, Cupra,
Bentley, VW commercial), and enter the VW ID, its password and the VIN. Setting up the portal itself —
the account, the consent and the continuous data request — is in [VW_PORTAL_SETUP.md](VW_PORTAL_SETUP.md).

```jsonc
"Vehicle": {
  "DataAct": {
    "Enabled": false          // read the portal on a clock, as opposed to only when the button is pressed
  }
}
```

Optionally, in configuration: `Vehicle__DataAct__Username`, `__Password`, `__Brand` and, with more
than one car on the account, `__Vin` — secrets, in `.env` or an environment variable (the shorter
`VW_*` names work too). **The password is not read-only**: the same account unlocks and locates the car, and there is no
scoped token instead.

**Press *Ask the car* first, then switch the feed on.** The button on the Car page works as soon as the
credentials are set and proves them; reading the portal on a clock waits for `Enabled`.

- **It reads every fifteen minutes.** The portal publishes in batches at that rate; asking faster gains
  nothing. Readings are typically **2–4 hours** behind the car.
- **One reading may combine several deliveries.** The portal sends partial deliveries, so a read merges
  up to four (`Vehicle:DataAct:MaxDatasetsPerRead`, or `VW_MAX_DATASETS`) until it has the battery,
  range, charging state, plug and remaining time. The log says what a read is still missing. A reading
  is dated by its **state of charge's** own capture time.
- **It keeps one session** and signs in again only when the portal ends it.
- **A failure you have to fix stops the feed**: a refused password, a consent screen, a one-time code,
  a CAPTCHA, or no continuous data request. Fix it in a browser, check with *Ask the car*, and restart
  the controller. While it is stopped, a warning is logged every six hours. Ordinary failures — a 5xx,
  a timeout, an expired session, a delivery not filled yet — are *Degraded* and back off (to at most an
  hour) and clear themselves.

### Your own feed over MQTT (the `Vehicle` section)

For any car Home Assistant (or anything else) can see: publish its state to a topic, in one JSON shape,
and the controller reads it. On the **Car** page choose *A feed I publish myself* and name the topic.

```jsonc
"Vehicle": {
  "Enabled": false,
  "BrokerHost": "localhost",
  "BrokerPort": 1883,
  "MaxAge": "12:00:00"                  // past this a reading is shown as stale, and a percentage
                                        //   target refuses to convert from it
}
```

The **topic** is set on the car, as `Ev:Vehicles:0:Telemetry:Topic`. `Vehicle:Username` and
`Vehicle:Password`, for an authenticated broker, are secrets: `.env` or an environment variable
(`Vehicle__Username`), never `appsettings.json`. **Settings → Installation** shows the broker, the user
and the topic actually subscribed to.

The payload:

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

- **`captured_at`** is required: it is when the *car* took the reading, not when it arrived, and
  staleness is judged from it.
- **`range_km`** is shown, never planned on. `0` is a real reading; leave the key out when unknown.
  Above 2000 km is rejected (a template publishing metres).
- **`charge_time_remaining_minutes`** — `0` means the car says it is finished, so leave the key out
  when the car doesn't report it. Outside 0–10080 the whole payload is rejected (usually seconds
  published as minutes).
- **`source`** names the feed on the Overview (*"reported by the car 12 min ago via id4"*).

#### Publishing from Home Assistant

An example for a VW ID.4 with the
[`volkswagen_connect`](https://github.com/rafaelhutter/ha-volkswagen-connect) integration. Mapping each
integration's values onto this shape in Home Assistant is what makes any car a source without new
code.

In **Settings → Automations & scenes → + Create automation → Create new automation → ⋮ → Edit in
YAML**:

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

(In `configuration.yaml`, the same automation goes under a top-level `automation:` key as a list
item, indented four spaces further.) Then set `Ev:Vehicles:0:Telemetry:Topic` to
`gleanvolt/vehicle/id4/state`.

- **Publish retained**, as above, so a restarted controller gets the last reading at once.
- **The `condition`** stops a payload being sent while the integration's entities are `unavailable`.
- **`captured_at` comes from `last_vehicle_report`**, not `data_captured`, which belongs to the
  integration's Data Act source and stays `unknown` unless that is set up.
- **Check the entity names against your install.** If yours has no range sensor, drop the `range_km`
  line; if it has no remaining-time sensor, drop the `{% if %}` block.
- **Test it with ⋮ → Run actions** rather than waiting for a state change. The controller logs:

  ```
  [22:56:17 INF] First vehicle reading from gleanvolt/vehicle/id4/state:
                 SOC=28% charge=Idle plug=Disconnected captured 2026-08-17T10:44:23+00:00
  ```

  `'soc_percent' was a JSON String, expected a number` means the source entity was `unavailable` when
  you ran it; *Run actions* skips the `condition`.

## Only when asked: `OnDemandOnly`

```jsonc
"Vehicle": { "OnDemandOnly": true }
```

Off by default. On, **nothing polls**: the car is read only when you press *Ask the car* or prepare a
plan, so a reading on screen is always one you asked for. Worth it for the Data Act portal, whose
readings are hours old. The cost: no state of charge is recorded during charging sessions.

## Old keys

`Vehicle:BatteryCapacityKWh`, `Vehicle:ChargeEfficiency` and `Vehicle:Topic` moved into the `Ev`
section. A configuration that still sets one **refuses to start**, naming its replacement. The
`deploy/.env` variable names did not change.
