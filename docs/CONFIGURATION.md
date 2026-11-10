# Configuration

Most settings can be changed in the web UI, on **Settings** and **Car**; the setup wizard covers the
ones a new installation needs. This page is the reference behind those forms.

**Where a setting comes from**, lowest to highest — a later one wins, key by key:

1. `src/Gleanvolt.Worker/appsettings.json`, the shipped defaults.
2. Environment variables, including `.env` (Docker: `/opt/gleanvolt/.env`; .deb:
   `/etc/gleanvolt/gleanvolt.env`). A key `A:B:C` is written `A__B__C`.
3. The overrides file, `data/pv-system.json`, written by the web UI and `gleanvolt setup`.
4. Command-line arguments.

**Secrets** — API keys, passwords, the MQTT password — never go in `appsettings.json`: put them in
`.env` or an environment variable, or enter them in the web UI, which keeps them in the
[secret store](#the-data-directory-holds-secrets-the-secrets-section).

The `Controller` section holds the settings that belong to no single feature:

```jsonc
"Controller": {
  "TimeZone": "",           // "" = ask the OS; an IANA or Windows zone id overrides it
  "PollIntervalSeconds": 5  // one poll/decide cycle per this many seconds
}
```

`TimeZone` is the zone every *local* decision is made in: the forecast's day boundary, the daily
battery-loan budget, and the date a charging session is filed under. Leave it empty on Linux, where
`TZ` sets it (the Docker stack does). **Set it on Windows**, which ignores `TZ` and would otherwise run
in UTC; on the Nano Server image it must be a Windows id (`Central Europe Standard Time`). An id that
cannot be resolved stops the controller at startup.

`PollIntervalSeconds` is how often the controller reads the devices and decides.

The other sections are documented with their features:

| Section | Page |
|---|---|
| `Pv`, `Secrets`, `Solcast`, `Weather`, `Tariff` | below |
| `ChargeControl` | [EV charge control](CHARGE_CONTROL.md) and the mode pages |
| `BatteryHold` | [Battery discharge hold](BATTERY_HOLD.md) |
| `Ev`, `Vehicle` | [The car and its telemetry](VEHICLE.md) |
| `Web` | [Web UI](WEB_UI.md) |
| `Api` | [HTTP API](HTTP_API.md) |
| `SessionStore`, `EnergyMonitor` | [Charging session and energy history](DATA.md) |
| `HomeAssistant` | [Gleanvolt with Home Assistant](HOME_ASSISTANT.md#settings-the-homeassistant-section) |

## The PV system (the `Pv` section)

One section describes the installation: where the array is, what it faces, what it is made of, and what
to call it. On the web UI it is **Settings → Installation**.

```jsonc
"Pv": {
  "Id": "home-roof",               // stable slug: the system's identity, on the broker and upstream
  "Name": "Home Roof",             // what a human sees
  "Address": "",                   // display only, never parsed
  "Latitude": 50.0755,
  "Longitude": 14.4378,
  "AzimuthDegrees": 172,           // compass bearing: 0 = north, 90 = east, 180 = south
  "TiltDegrees": 35,               // 0 = flat, 90 = vertical
  "CapacityKwp": 9.2,              // peak DC capacity of the array
  "InverterCapacityKw": 8.0,       // optional; AC side, where it clips the array
  "LossFactor": 0.9,               // fraction of DC yield that reaches the meter
  "InstallDate": "2024-05-01",

  "Inverter": {
    "Driver": "",                  // which inverter family it speaks to: "solax" (empty) or "huawei" (readings only)
    "Topology": "",                // huawei only: "dongle" (empty) or "emma"; UnitId is then the inverter's behind the EMMA
    "Model": "SolaX X3-HYB-G4 PRO",
    "Host": "192.168.2.10", "Port": 502, "UnitId": 1,
    "UnmeteredGridPhase": ""       // only for a grid meter missing a phase clamp: "L3" (or SolaX's "T")
  },

  "Chargers": [                    // a list; exactly one entry is supported
    {
      "Id": "charger",             // slug: the charger's identity within the system
      "Driver": "",                // which charger family it speaks to: "solax" (empty), "huawei-7ks", "huawei-22kt" or "goe" (untested)
      "Name": "Garage wallbox",
      "Model": "SolaX X1/X3-HAC",
      "Host": "192.168.2.6", "Port": 502, "UnitId": 1
    }
  ]
}
```

- **`Driver` picks how a device is spoken to; `Model` is only a label.** Empty means `solax`. The
  driver fills in `Port` and `UnitId` when they are left out. On Settings these are the **Inverter
  brand** and **Charger brand** drop-downs. Which models each driver covers is in
  [Supported PV systems](SUPPORTED_PV_SYSTEMS.md).
- **The charge-control limits must fit the charger.** `ChargeControl:MinChargingCurrentAmps` below its
  lowest setpoint, `MaxChargingCurrentAmps` above its highest, or `ChargeControl:Phases` above the
  phases it supplies is refused at startup and on save.
- **`AzimuthDegrees` is stored, not used.** The Solcast forecast already knows your roof's orientation
  from its resource id.
- **One charger.** `Chargers` is a list, but a second entry stops startup.
- **`UnmeteredGridPhase`** is only for a grid meter with a phase clamp missing; leave it empty on a
  correctly wired installation. Name the blind phase (`L1`–`L3`, or SolaX's `R`/`S`/`T`) and the grid
  flow on it is estimated from the inverter's output on that phase. See
  [Unmetered grid phase](UNMETERED_GRID_PHASE.md) ([česky](UNMETERED_GRID_PHASE.cs.md)).

**Checked at startup.** A configuration that cannot describe a site stops the controller with every
problem listed, each naming its key: an id that is not a slug, a latitude without a longitude, a tilt
outside 0–90, a loss factor outside (0, 1], an unparsable install date, a missing inverter or charger
address, an unknown driver, an unmetered phase that is not a phase, or two chargers. (In
[setup mode](WEB_UI.md#setup-mode) these are listed on the setup page instead.)

### Keys that have moved

A configuration that still sets a retired key **stops at startup**, naming the replacement:

| Retired | Replaced by |
| --- | --- |
| `Solax:Inverter` | `Pv:Inverter` |
| `Solax:EvCharger` | `Pv:Chargers:0` |
| `Solax:PollIntervalSeconds` | `Controller:PollIntervalSeconds` |
| `Weather:Latitude` | `Pv:Latitude` |
| `Weather:Longitude` | `Pv:Longitude` |
| `HomeAssistant:DeviceName` | `Pv:Name` |

The `deploy/.env` names (`INVERTER_HOST`, `EV_CHARGER_HOST`) did not change.

## The data directory holds secrets (the `Secrets` section)

**Whoever can read the data directory can read — and charge — your car.** Besides the databases, it
holds:

| File | What it holds | What it lets someone do |
|---|---|---|
| `vw-website-session.json` | a live volkswagen.de session | read the car as you, with no password and no emailed code |
| `skoda-api-key.json` | the MyŠkoda Public API key | read the car, and start and stop charging |
| the secret store's other entries | keys and passwords entered in the web UI (Solcast, OpenWeatherMap, MQTT, the car) | use those accounts |

```jsonc
"Secrets": {
  "Directory": "data",   // relative paths resolve against the content root; the .deb sets it absolutely
  "Store": "Auto"        // "Auto" | "File" | "Dpapi" -- Auto is right everywhere
}
```

| Deployment | Directory | What protects it |
|---|---|---|
| Docker (Linux) | `/app/data` on the `./data` bind mount | mode `0600`, plus the host directory's own mode |
| .deb / systemd | `/var/lib/gleanvolt` (mode `0750`, owner `gleanvolt`) | mode `0600`, plus `ProtectSystem=strict` and `ProtectHome=true` |
| Windows zip / winget | `data\` next to the exe | Windows DPAPI, current user |
| Windows container | `C:\app\data` | the volume's ACLs (DPAPI is not used: the container's account does not survive recreating it) |
| `dotnet run` | `src/Gleanvolt.Worker/data` | mode `0600`; gitignored |

**This is file permissions, not encryption** — the service has to start unattended after a reboot, so
it must be able to read its own secrets. The startup log and **Settings → Health** say which protection
is in use. Against a stolen SD card, use full-disk encryption on the host.

**Back the directory up accordingly**: onto an encrypted volume, and never into an issue attachment or a
shared drive. **Sign out** on the [Car](WEB_UI.md#car) page deletes the stored car credentials.

A secret that can't be read back (a truncated file, or a Windows profile that no longer exists) is
reported as *sign in again*.

Secrets that arrive as environment variables are the deployment's to protect. `Web:PasswordHash` is a
one-way hash, not a secret.

## Solcast solar forecast

The controller fetches your site's solar forecast from [Solcast](https://solcast.com/), caches it, and
refreshes it every 12 hours by default.

**On the web UI:** **Settings → Installation → Solar forecast** — paste the API key and the rooftop
site's resource id and press **Save and fetch**. It is used from that fetch on, with no restart, and the
status line says what Solcast made of it: a refused key, an unknown site, a spent quota. A refused key
or unknown site is not retried until the account changes, so a typo does not burn the day's calls. The
key goes to the secret store and wins over the environment; **Remove** hands back to the environment's.

**In configuration:**

```jsonc
"Solcast": {
  "BaseUrl": "https://api.solcast.com.au/",
  "ResourceId": "your-solcast-resource-id", // the rooftop site id from your Solcast account
  "RefreshInterval": "12:00:00"             // hh:mm:ss between refreshes
}
```

The **API key is a secret**: `Solcast__ApiKey` in `.env` or the environment, or, for development,
`dotnet user-secrets set "Solcast:ApiKey" "<your-api-key>"` in `src/Gleanvolt.Worker`. For
`dotnet run`, copy `.env.example` to `.env` in the repository root: it is gitignored and loaded at
startup.

With no account, the controller logs a warning and runs without a forecast. The free hobbyist tier
allows about ten calls a day, and every start spends one, so keep `RefreshInterval` within your plan.

## Weather (the `Weather` section, optional)

Records **what the sky was doing** at the start and end of each charging session, so a session can be
read against its day. Off unless configured: with no key and no
[coordinates](#the-pv-system-the-pv-section), no weather call is ever made.

```jsonc
"Weather": {
  "BaseUrl": "https://api.openweathermap.org/",
  "Units": "metric",            // °C and metres; changing this changes what the stored numbers mean
  "RequestTimeout": "00:00:05"  // a slow provider is abandoned, never waited for
}
```

The **OpenWeatherMap API key is a secret**: `WEATHER_API_KEY` in `deploy/.env`, `Weather__ApiKey` in
the environment, or **Settings → Installation → Weather** (or the wizard's Extras step), which checks it
with one call and is used from the next session without a restart. A new OpenWeatherMap key can take a
couple of hours to start working, so it is kept even when that first check fails. **Remove** goes back
to the environment's key.

**Two calls per charging session**, to the free current-weather endpoint. See
[The weather a session ran in](DATA.md#the-weather-a-session-ran-in) for what is stored.

## Grid tariff (the `Tariff` section)

When the grid is cheap, for a two-rate tariff — in Czech terms the distributor's **HDO**, switching
between the low rate (**NT**) and the high rate (**VT**). The tariff is shown on the Overview, in Home
Assistant and on the HTTP API. **On its own it changes nothing about charging**: that is the **Low
tariff only** option of [`FastNoBattery`](MODE_FAST_NO_BATTERY.md#fastnobattery-on-low-tariff-only) and
[`SolarGrid`](MODE_SOLAR_GRID.md#solargrid-on-low-tariff-only), which wait for low tariff before buying
from the grid.

It is set up on **Settings → Installation**, under *Tariff*. A save applies at once. Like the rest of
Settings it is read-only until the UI has a login.

**The provider** — where the low-tariff times come from:

- **None** — no tariff, the default. The Overview says nothing about a tariff and the Home Assistant
  entities read *unknown*. None never means "always low".
- **Table** — the periods you type in, below.

**The table.** Enter **only the low-tariff periods**; every other minute is high tariff. There are two
lists: **weekdays** (Monday to Friday) and the **weekend**. Public holidays follow the weekday list.
Times are local `HH:mm`, and an end may be `24:00`.

- **A period belongs to one day.** A night tariff of 22:00–06:00 is two rows, **22:00–24:00** and
  **00:00–06:00**. They are joined across midnight, so at 23:00 the Overview says *"Low tariff, until
  06:00"*. The same works from Friday into Saturday and Sunday into Monday.
- **Rows in one list must not overlap**; touching is fine (06:00–08:00 and 08:00–10:00).
- **Clock changes are handled.** A time in the missing spring hour moves to 03:00; in the repeated
  autumn hour it is the first one.
- **Today and tomorrow are drawn** under the lists, from what the form holds, so a mistyped time shows
  before you save.

**Where to find your times.** Your distributor publishes them for your HDO code (on the HDO receiver
beside the meter, and in your contract): EG.D, ČEZ Distribuce and PREdistribuce each have an *HDO* or
*časy spínání* page. If your times change with the season, edit the table when they do.

**Prices** per kWh for each rate, in the configured currency. Optional: without them the rate is still
shown, without a price.

**The file.** It is all kept in `data/tariff.json`, beside `pv-system.json`. You don't need to edit
it; if you do:

```jsonc
{
  "provider": "Table",              // "None" or "Table"
  "lowPricePerKwh": 2.10,           // optional
  "highPricePerKwh": 4.80,
  "table": {                        // kept while another provider is selected
    "weekday": [ { "start": "00:00", "end": "06:00" }, { "start": "13:00", "end": "15:00" } ],
    "weekend": [ { "start": "00:00", "end": "08:00" }, { "start": "20:00", "end": "24:00" } ]
  }
}
```

A missing or unreadable file, or a table that breaks a rule, is read as no tariff (with a warning in
the log); Settings still shows it for fixing.

**Configuration.** Two keys, neither on Settings:

```jsonc
"Tariff": {
  "Path": "data/tariff.json",  // relative to the content root unless absolute
  "Currency": "CZK"            // ISO 4217; labels the price fields and every price shown
}
```

The `.deb` sets `Tariff__Path` to `/var/lib/gleanvolt/tariff.json`. `Currency` is a label; it converts
nothing.

**Where it shows.** The Overview has a line under its headline — *"High tariff, 4.80 CZK/kWh, until
22:00"* — and shades low tariff along the bottom of today's plan chart. Home Assistant gets **Low
tariff**, **Tariff change** and **Grid price** ([the entity table](HOME_ASSISTANT.md#what-each-entity-means)).
The HTTP API has `GET /tariff`, `GET /tariff/providers` and `GET`/`PUT /tariff/settings`.
