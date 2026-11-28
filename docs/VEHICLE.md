# The car and its telemetry

> **Using Gleanvolt?** The user manual has this step by step: [Your car and its battery level](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/car/). This page is the reference behind it.

Two things are configured separately:

- **The car** (the `Ev` section) — what the controller is charging: its pack, its phases and the
  currents it accepts. This makes charging correct, with or without any feed.
- **The feed** (the `Vehicle` section) — what reports the car's own state of charge, range and plug
  state. Optional. It shapes what you *ask* for — a target can be a battery percentage — and never
  how a charge is delivered.

**Set both on the [Car page](WEB_UI.md#car) of the web UI** (or in the setup wizard's **Car** step):
choose what reads the car and it shows the fields that feed needs. That is the recommended way. The
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

**One car, one feed.** Gleanvolt reads no car itself: a manufacturer's account is read by a car plugin.

| Feed | For | Freshness | Section |
|---|---|---|---|
| **A plugin** | any car a program can read; gleanvolt-cars has Volkswagen, Škoda and the VW Group Data Act portal | whenever the plugin publishes | [`Vehicle:External`](#a-plugin-vehicleexternal) |
| **Your own MQTT topic** | any car Home Assistant can see | whatever publishes it | [`Vehicle`](#your-own-feed-over-mqtt-the-vehicle-section) |
| **None** | any car | — | targets in kWh only |

A plugin and your own MQTT topic *can* both run; the most recent reading wins.

**The feed is advisory.** Nothing that writes to the charger or the inverter reads it, so a feed that
is stale, failing or absent changes nothing about how a charge is driven. A reading older than
`Vehicle:MaxAge` (12 hours by default) is shown as **stale**, and a target asked for as a percentage
refuses to convert from it; a target in kWh is unaffected. See
[Setting a target](MODE_TARGETED.md#setting-a-target).

The feed's health is shown on the Overview's vehicle card, as a band across every page when it needs
you, on **Settings → System**, and as the [**Car feed**](HOME_ASSISTANT.md#what-each-entity-means)
entity in Home Assistant: `Ok`, `Degraded` (it retries and clears itself) or `NeedsOwner` (it has
stopped and waits for you).

### A plugin (`Vehicle:External`)

For any car a program outside Gleanvolt can read: a Tesla over Bluetooth, another manufacturer's cloud,
an OBD dongle. The program is an [external provider](PROVIDERS.md#the-vehicle-kind): it connects to
Gleanvolt's web port at `/providers/vehicle` and publishes what the car says.

1. On the **Car** page choose *A plugin*, save and restart.
2. Start the provider: it asks to be paired, and you accept it on **Settings → Plugins**. (Or give it
   Gleanvolt's address and the token from that tab.)

The Car page then shows the provider's session, when it last published and anything Gleanvolt
refused. The provider learns the car (id, name, make, model, pack size) from Gleanvolt; the pack stays
in the `Ev` section.

**Its own page.** A provider that needs you (to sign in, enter an emailed code, pair a device) can
serve a page of its own. The Car page shows it in a frame under *Plugin*, with *Open in a
new tab* beside it, and only to someone signed in to Gleanvolt: the frame goes through Gleanvolt, which
adds a short-lived ticket the provider checks. How a provider does it is in
[PROVIDERS.md](PROVIDERS.md#the-providers-own-page).

**The car plugins.** [gleanvolt-cars](https://github.com/mpospisil/gleanvolt-cars) has three, each with its own
page for the account and the sign-in, its own image and its own release:

| Plugin | Compose profile, port | Reads |
|---|---|---|
| Volkswagen | `volkswagen`, 8095 | volkswagen.de, live; a first sign-in asks for an emailed code |
| MyŠkoda | `skoda`, 8101 | the MyŠkoda Public API, every 5–15 min, with an API key from the MySkoda app |
| VW Group Data Act | `vwgroup`, 8102 | the EU Data Act portal, 2–4 hours behind, any VW Group brand |

Each README says how to run it and what it needs from you. Compose hands each the VIN, the username, the brand
and the client id from the `.env` lines the built-in feeds read (`VW_WEBSITE_*`, `SKODA_*`, `VW_*`); a password
or a key is only ever entered on the plugin's page.

**The built-in feeds are gone (#477).** volkswagen.de, MyŠkoda and the Data Act portal were read by Gleanvolt
itself until then. An installation that had one switched on (`Vehicle:Website:Enabled`, `Vehicle:Skoda:Enabled`,
`Vehicle:DataAct:Enabled` or `VW_ENABLED`) is moved at its first start: `Vehicle:External:Enabled` is written to
the overrides file with `Vehicle:RetiredFeed` naming the plugin, so this happens once, and the Car page names the
plugin until a car plugin connects (`Vehicle:RetiredFeedNotice`). One warning a start names any flag still on;
the flags are ignored, not refused. The built-ins' secrets (`vehicle-account-password`, `vw-website-session`,
`skoda-api-key`) are deleted from the secret store at every start, whatever feed is chosen, and never handed to a
plugin. A setup document with a key of those sections is refused, naming the plugin.

| Key | Default | |
|---|---|---|
| `Enabled` | off | **Chooses the feed.** Written by the Car page |

**It pushes.** Nothing polls the provider: it publishes whenever the car has something new, and
Gleanvolt asks it only when you press *Ask the car* or a plan wants a fresh reading. A provider that
doesn't answer within a minute leaves the feed *Degraded* until its next reading, and the last
reading stays. `OnDemandOnly` changes nothing for it.

**Health.** *Ok* while the provider is connected and its last reading was accepted. *Degraded* while
it is disconnected (the reading ages as usual), when it reports a problem, or when a reading was
refused. *NeedsOwner* when the provider says only you can fix it, such as an expired login: the band
shows on every page until the provider reports it is fine again. There is no fallback to another
feed.

Your own MQTT topic may run beside it; the most recent reading wins, and the provider's older ones are
refused.

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
(`Vehicle__Username`), never `appsettings.json`. **Settings → Integrations** shows the broker, the user
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
plan. Since #477 no feed is polled (a plugin and your own topic both push), so it changes nothing today;
a plugin that reads a slow source, such as the Data Act portal, decides its own cadence.

## Old keys

`Vehicle:BatteryCapacityKWh`, `Vehicle:ChargeEfficiency` and `Vehicle:Topic` moved into the `Ev`
section. A configuration that still sets one **refuses to start**, naming its replacement. The
`deploy/.env` variable names did not change.
