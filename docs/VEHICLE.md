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
      "MaxChargingCurrentAmps": 16       // its on-board charger's ceiling
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

**A car plugin, or nothing.** Gleanvolt reads no car itself: a car plugin does. On the **Car** page, *What reads
this car* is **A car plugin** or **Nothing — targets in kWh**.

| Choice | For | Freshness | Section |
|---|---|---|---|
| **A car plugin** | any car a program can read; gleanvolt-cars has Volkswagen, Škoda and the VW Group Data Act portal | whenever the plugin publishes | [`Vehicle:External`](#a-car-plugin-vehicleexternal) |
| **Nothing** | any car | — | targets in kWh only |

**The car's own MQTT topic is gone (#486).** `Vehicle:Enabled` (`VEHICLE_ENABLED`), the broker settings and
`Ev:Vehicles:0:Telemetry:Topic` are no longer read. A topic saved on the Car page is removed from the overrides
file at the next start, and `VEHICLE_ENABLED=true` gets a warning at every start. A car Home Assistant can see is
read by a car plugin instead.

**The feed is advisory.** Nothing that writes to the charger or the inverter reads it, so a feed that
is stale, failing or absent changes nothing about how a charge is driven. A reading older than
`Vehicle:MaxAge` (12 hours by default) is shown as **stale**, and a target asked for as a percentage
refuses to convert from it; a target in kWh is unaffected. See
[Setting a target](MODE_TARGETED.md#setting-a-target).

The feed's health is shown on the Overview's vehicle card, as a band across every page when it needs
you, on **Settings → System**, and as the [**Car feed**](HOME_ASSISTANT.md#what-each-entity-means)
entity in Home Assistant: `Ok`, `Degraded` (it retries and clears itself) or `NeedsOwner` (it has
stopped and waits for you).

### A car plugin (`Vehicle:External`)

For any car a program outside Gleanvolt can read: a Tesla over Bluetooth, another manufacturer's cloud,
an OBD dongle. The program is an [external provider](PROVIDERS.md#the-vehicle-kind): it connects to
Gleanvolt's web port at `/providers/vehicle` and publishes what the car says.

1. On the **Car** page choose *A car plugin*, save and restart.
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

## Only when asked: `OnDemandOnly`

```jsonc
"Vehicle": { "OnDemandOnly": true }
```

Off by default. On, **nothing polls**: the car is read only when you press *Ask the car* or prepare a
plan. Since #477 no feed is polled (a car plugin pushes), so it changes nothing today;
a plugin that reads a slow source, such as the Data Act portal, decides its own cadence.

## Old keys

`Vehicle:BatteryCapacityKWh` and `Vehicle:ChargeEfficiency` moved into the `Ev`
section. A configuration that still sets one **refuses to start**, naming its replacement. The
`deploy/.env` variable names did not change.
