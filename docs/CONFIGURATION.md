# Configuration

All settings live in `src/Gleanvolt.Worker/appsettings.json`; secrets are supplied out-of-band (see
below). **The installation itself — where the array is and which boxes it is made of — is described
in one place, the `Pv` section**, documented in [The PV system](#the-pv-system-the-pv-section) below.
The vendor-named `Solax` section is gone; the poll cadence it used to hold is a controller setting.

The `Controller` section holds the settings that belong to no single feature:

```jsonc
"Controller": {
  "TimeZone": "",           // "" = ask the OS; an IANA or Windows zone id overrides it
  "PollIntervalSeconds": 5  // one poll/decide cycle per this many seconds
}
```

`TimeZone` is the zone every *local* decision is made in: the forecast day boundary, the daily loan
budget reset, and the zone id recorded on each charging session. Leave it empty on Linux, where the
`TZ` environment variable already sets it — that is what the deploy stack does. **Set it explicitly
on Windows**, where .NET ignores `TZ` and the process would otherwise run in UTC; on the Nano Server
image it must be a Windows id (`Central Europe Standard Time`), because resolving IANA ids needs ICU
and Nano Server has none. An id that cannot be resolved stops the worker at startup rather than
quietly reverting to UTC.

`PollIntervalSeconds` is how quickly the controller reacts, not a fact about the hardware — the
inverter has no opinion about how often it is asked — which is why it outlived the section the device
addresses moved out of.

The feature sections — `Solcast`, `ChargeControl`, `BatteryHold`, `Tariff`, `SessionStore` and
`HomeAssistant` — are documented in the subsections that follow.

## The data directory holds secrets (the `Secrets` section)

**The data directory is bearer-equivalent. Whoever can read it can read — and charge — your car.**
Two files in it are passwords in all but name ([issue #215](https://github.com/mpospisil/gleanvolt/issues/215)):

| File | What it holds | What it lets someone do |
|---|---|---|
| `vw-website-session.json` | a live volkswagen.de session, including the *remember this browser* grant | read the car as you, with no password and no emailed code |
| `skoda-api-key.json` | the MyŠkoda Public API key | read the car, and start and stop charging |

They live in one place, behind one seam, so there is one answer to "what protects these?":

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
| Windows zip / winget | `data\` next to the exe | Windows DPAPI, `CurrentUser` scope |
| Windows container | `C:pp\data` | the volume's ACLs — DPAPI deliberately **not** used, see below |
| `dotnet run` | `src/Gleanvolt.Worker/data` | mode `0600`; already gitignored |

**Nothing here is encryption, and the controller never says it is.** The service has to come back
from a restart with nobody present — that is the point of `Restart=on-failure` — so whatever protects
a stored secret must be undoable by this process, on this machine, at boot, unattended. A key file
beside the data, or ASP.NET Data Protection (whose Linux keyring is plaintext XML *in the same
directory*), would encrypt file A with a key in file B in the same backup. The startup log and
`/health` therefore report what is true — `owner-only files (0600) in the data directory`, or
`Windows DPAPI, this user` — and never the word *encrypted*. What genuinely protects a stolen SD card
is full-disk encryption, which is the installer's to arrange and not this program's to claim.

**Back the directory up accordingly.** It belongs on an encrypted volume; it does not belong in a
casual backup, an issue attachment or a shared drive; and `docker cp` of it hands over the car. To
hand it over deliberately, don't — use **Sign out** on the [Car](VEHICLE.md#the-car-the-ev-section)
page first, which deletes the value rather than leaving a tombstone holding it.

**Windows is two deployments, not one.** On the zip and winget installs DPAPI earns its place: the
key is the operating system's, there is no key file to lose or back up by accident, and `data\` sits
inside a package directory that an upgrade may replace and that people copy around wholesale. Scope
is `CurrentUser`, never `LocalMachine` — machine scope is readable by every account on the box, which
is weaker than the file permissions it was meant to improve on. In the **Windows container** it is
deliberately off: Nano Server runs under a built-in account that does not survive the container being
recreated, and a secret sealed to it would be *unrecoverable* rather than merely stale. The choice is
explicit and on the startup log, never inferred from "this is Windows".

A secret that cannot be read back — truncated, or sealed for a Windows profile this machine no longer
has — is reported as *sign in again*, which is a page to visit, not a crypto exception in the log.

Out of scope, and staying out: secrets that arrive as environment variables (the Solcast key, the
MQTT password, the `VW_*` and `SKODA_*` values in `.env`) are the deployment's to protect; this is
about what the process writes. `Web:PasswordHash` is a one-way hash, not a secret.

## The PV system (the `Pv` section)

One section describes the installation: where the array is, what it faces, what it is made of, and what
to call it ([issue #111](https://github.com/mpospisil/gleanvolt/issues/111)). Everything that needs a
coordinate, a device address or a name reads it from here, so there is one answer rather than one per
feature.

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

**`Driver` picks how the inverter is spoken to; `Model` is only a label.** `Driver` names the inverter
family — `solax` or `huawei` — and decides which registers are read and how the battery is held.
Empty means `solax`, so an existing configuration needs no change; a value no driver has is a startup
failure that lists the ones there are (and a problem on the setup page, in setup mode). The driver also
supplies the port and unit id when `Port` and `UnitId` are left out. On Settings it is the **Inverter
brand** drop-down. `Model` selects nothing: one driver covers every model of a family that shares its
registers, so writing another model here does not make the controller speak that device's dialect. It
answers "what is on the other end of 192.168.2.10?" from a log line rather than an ssh session.

The charger has its own `Driver` (issue #349), the **Charger brand** drop-down on Settings, with the same
rules: empty means `solax`, a value no driver has stops the start, and the driver fills in an empty `Port`
and `UnitId`. The charge-control limits are checked against what the selected charger can do:
`ChargeControl:MinChargingCurrentAmps` below its lowest setpoint, `MaxChargingCurrentAmps` above its
highest, or `ChargeControl:Phases` above the phases it supplies is refused at startup and on save.

**`AzimuthDegrees` is stored, not sent.** Nothing computes from it today: the forecast is fetched by
Solcast resource id, which already encodes the orientation inside your Solcast account. Before it is
ever passed to a provider, that provider's own azimuth convention has to be checked against the compass
bearing used here — an array described 180° from where it points still produces a plausible-looking
forecast, which is what makes this the one field that goes silently wrong. Written as `-90` or `270`,
it is stored the same way; the resolved value is always in `[0, 360)`.

**One charger, in a shape that can hold more.** `Chargers` is a list from the start so that the
configuration does not have to change shape the day the control logic can drive two — which it cannot
today: there is one charge mode, one set of Home Assistant controls and one surplus to divide. A second
entry is therefore **a startup failure**, not a silently ignored one.

**`UnmeteredGridPhase` is a workaround for a grid meter missing a phase clamp**, and stays empty on every
correctly wired installation. House load is worked out as PV + grid − EV − battery, so a meter blind on
one phase makes the house appear to use whatever the inverter exports there — nearly all the sun on every
sunny afternoon, so no solar mode can start the car — and to generate while the car charges. Name the
phase (`L1`–`L3`, or SolaX's `R`/`S`/`T`) and the grid flow on it is estimated from the inverter's own
output on that phase, the car's share, and half the house load the other two phases carry. Everything
downstream reads the estimate, Home Assistant's **Grid power** included; the poll log prints the meter's
own figure beside it, and the startup line says the correction is on. To confirm a blind phase, read the
inverter's per-phase meter registers (0x82/0x84/0x86): one reads 0 W while the inverter's output register
for the same phase (0x6C/0x70/0x74) does not. The real fix is the clamp; once it is fitted, empty this.
A step-by-step guide for owners, linked from the field on Settings, is
[docs/UNMETERED_GRID_PHASE.md](UNMETERED_GRID_PHASE.md) ([česky](UNMETERED_GRID_PHASE.cs.md)).

**What is validated at startup.** A site that cannot be described stops the worker with every problem
listed at once, each naming its key: an id that is not a slug, a latitude without a longitude, a tilt
outside 0–90, a loss factor outside (0, 1], an unparsable install date, a missing inverter or charger
address, an unmetered grid phase that is not a phase, two chargers, or two chargers sharing an id.

### Keys that have moved

The `Solax` section is gone: an inverter and a wallbox are what a PV system is made of, and the one
setting left in it — the poll interval — is not a fact about the hardware but a choice about how often
we ask, so it belongs to the controller.

**A retired key that is still set stops the worker at startup**, naming its replacement in both
spellings. That is deliberate and it is the whole point: each of these decided something real, and a
build that ignored the old spelling would start, poll and charge — against a default, while your
configuration file names something else. That failure is invisible; a refused startup costs one
restart.

| Retired | Replaced by |
| --- | --- |
| `Solax:Inverter` | `Pv:Inverter` |
| `Solax:EvCharger` | `Pv:Chargers:0` |
| `Solax:PollIntervalSeconds` | `Controller:PollIntervalSeconds` |
| `Weather:Latitude` | `Pv:Latitude` |
| `Weather:Longitude` | `Pv:Longitude` |
| `HomeAssistant:DeviceName` | `Pv:Name` |

`Weather:ApiKey` and `Solcast:ResourceId` did **not** move: a provider's key and a provider's handle
for your roof belong beside that provider's other settings. The `Pv` section says what the array is; a
provider section says how to reach that provider about it.

Deployments are unaffected in the place it would cost most: `INVERTER_HOST` and `EV_CHARGER_HOST` keep
their names in `deploy/.env`, and the compose file maps them onto the new keys. See
[the upgrade note](../deploy/README.md#upgrading-a-pi-deployed-before-the-pv-system-had-its-own-settings).

## Solcast solar forecast

The worker fetches a solar-generation forecast for your site from [Solcast](https://solcast.com/) and caches it locally, refreshing on a configurable interval (default 12 hours). Non-secret settings live in `appsettings.json` under the `Solcast` section:

```jsonc
"Solcast": {
  "BaseUrl": "https://api.solcast.com.au/",
  "ResourceId": "your-solcast-resource-id", // the rooftop site id from your Solcast account
  "RefreshInterval": "12:00:00"             // hh:mm:ss between refreshes
}
```

The simplest route is the **Solar forecast** section of **Settings** in the web UI (issue #286): paste the API key and the rooftop site's resource id, and **Save and fetch**. The account is used from that fetch on, with no restart, and the status line under the heading says what Solcast made of it — a refused key, an unknown site, a spent quota. It is kept in the secret store as `solcast-account`, the key is never rendered into the form, and the section is read-only when the UI has no login. An account saved there wins over the environment; **Remove** hands back to `Solcast__ApiKey` / `Solcast__ResourceId` if they are set. A key Solcast refused (401/403) or a site it does not know (404) is not asked about again until the account changes, so a typo does not burn the day's calls.

Otherwise the **API key is a secret and must not be committed**. Provide it out-of-band, using whichever of these fits how you run the app:

- **`.env` file (recommended for local dev)** — copy `.env.example` to `.env` (which is gitignored) in the repo root and set your key. On startup the worker loads the nearest `.env` into the process environment, so it works both from `dotnet run` **and** the VS Code debugger without any shell setup:

  ```bash
  cp .env.example .env
  # then edit .env:  Solcast__ApiKey=<your-api-key>
  ```

- **Environment variable** — set it in your shell/service manager (double underscore separates config sections). A real environment variable always takes precedence over `.env`:

  ```bash
  export Solcast__ApiKey="<your-api-key>"
  ```

- **.NET user-secrets** — the `Gleanvolt.Worker` project has a `UserSecretsId`, so this works in Development too:

  ```bash
  cd src/Gleanvolt.Worker
  dotnet user-secrets set "Solcast:ApiKey" "<your-api-key>"
  ```

If no account is configured either way, the worker logs a warning and skips forecast refreshes; the rest of the service continues to run. The free Solcast hobbyist tier caps daily API calls, which is why the forecast is cached and refreshed only every 12 hours by default — keep the interval within your plan's quota.

## Weather (the `Weather` section, optional)

Records **what the sky was actually doing** while a session ran, so a finished session can be read against the day it happened on rather than only against the forecast. Entirely optional and **off unless configured**: with no key and no coordinates the controller makes no weather calls at all, and every session is simply recorded without weather.

```jsonc
"Weather": {
  "BaseUrl": "https://api.openweathermap.org/",
  "Units": "metric",            // °C and metres; changing this changes what the stored numbers mean
  "RequestTimeout": "00:00:05"  // a slow provider is abandoned, never waited for
}
```

**Which site the weather is fetched for is [the `Pv` section's](#the-pv-system-the-pv-section) business**, not this one's: `Pv:Latitude` and `Pv:Longitude` are where the coordinates belong. `Weather:Latitude` and `Weather:Longitude` are retired, and setting either stops the worker at startup rather than being ignored.

The **API key is a secret**, on exactly the same terms as `Solcast:ApiKey` above — `.env`, an environment variable (`Weather__ApiKey`), or user-secrets. Deployments set `WEATHER_API_KEY` in `deploy/.env`, and the site's coordinates as `PV_LATITUDE` / `PV_LONGITUDE`. Or paste it on **Settings → Installation → Weather** (or the setup wizard's Extras step): a key saved there goes to the secret store, wins over the environment's, and is used from the next session without a restart. The save checks it with one call; a new OpenWeatherMap key can take a couple of hours to start working, so a refusal right after creating one is expected and the key is kept anyway. **Remove** goes back to the environment's key.

**Two calls per charging session**, one when it opens and one when it closes, and none in between. There is no refresh worker and no cached forecast: weather is decoration on a record, not an input to any decision, so spending the provider's quota on hours when nothing is recording would buy nothing. Any free OpenWeatherMap plan covers a few sessions a day comfortably.

It uses the **current-weather endpoint** (`data/2.5/weather`), not One Call 3.0 — One Call needs its own paid subscription and answers `401` without one, and everything recorded here comes back from the free endpoint anyway. See [The weather a session ran in](DATA.md#the-weather-a-session-ran-in) below for what is stored.

## Grid tariff (the `Tariff` section)

What grid energy costs at each hour, for a two-rate tariff: in Czech terms, the distributor's **HDO**
switching between the low rate (**NT**) and the high rate (**VT**). This part only makes the tariff
known and visible — on the Overview, in Home Assistant and on the HTTP API. **On its own it changes
nothing about how the car charges.** That is the **Low tariff only** option of
[`Fast`](MODE_FAST_NO_BATTERY.md#fastnobattery-on-low-tariff-only) and [`SolarGrid`](MODE_SOLAR_GRID.md#solargrid-on-low-tariff-only), which waits for low tariff before
buying from the grid.

It is set up on the web UI's **Settings** page, under *Tariff*, rather than in configuration. A save
applies at once, with no restart. Like the rest of Settings it is read-only until the UI has a login
(`Web__PasswordHash`).

**The provider.** Where the low-tariff times come from. Two are built in:

- **None** — no tariff, the default. The grid costs the same at every hour, the Overview says nothing
  about a tariff, and the Home Assistant entities read *unknown*. None never means "always low".
- **Table** — the periods you type in, described below.

More can follow (a distributor's feed, a spot price); each is a strategy of its own, chosen in the same
control, and switching to another and back keeps the first one's settings.

**The table.** Enter **only the low-tariff periods**; every other minute is high tariff. There are two
lists: **weekdays** (Monday to Friday) and the **weekend** (Saturday and Sunday). Public holidays follow
the weekday list for now. Times are local `HH:mm`, and an end may be `24:00`, meaning the end of the day.

- **A period belongs to one day.** A night tariff of 22:00–06:00 is entered as two rows, **22:00–24:00**
  and **00:00–06:00**. The controller joins them across midnight, so the Overview says *"Low tariff,
  until 06:00"* at 23:00 rather than *"until 24:00"*. The same join works from Friday into Saturday and
  from Sunday into Monday, where the rows come from different lists.
- **Rows in one list must not overlap**; touching is fine (06:00–08:00 and 08:00–10:00). They are sorted
  by start when saved, whatever order they were added in.
- **The clock changes are handled for you.** On the spring-forward night a time between 02:00 and 03:00
  does not exist, and an edge there moves to 03:00; in autumn that hour happens twice, and an edge in it
  is the first time round.
- Under the lists, **today and tomorrow are drawn as 24-hour bars** with low tariff shaded, from what the
  form holds rather than what is saved: a `16:00` typed for `06:00` shows up as a wrong-looking day
  before you press Save.

**Where to find your times.** Your distributor publishes them for your HDO code (printed on the HDO
receiver beside the meter, and in your contract): EG.D, ČEZ Distribuce and PREdistribuce each have an *HDO* or
*časy spínání* page. If your times change with the season, edit the table when they do.

**Prices.** A price per kWh for each rate, in the configured currency. Optional: without them the rate
is still known and shown, just without a price. A low price above the high one is accepted with a
warning, since nothing acts on prices yet.

**The file.** Everything above is kept in `data/tariff.json`, beside `pv-system.json`, so under Docker it
is on the same `./data` bind mount and survives a new image. You do not need to edit it; if you do, it
looks like this:

```jsonc
{
  "provider": "Table",              // "None", "Table", or a later provider's id
  "lowPricePerKwh": 2.10,           // shared by every provider; optional
  "highPricePerKwh": 4.80,
  "table": {                        // the Table provider's own settings, kept while another is selected
    "weekday": [ { "start": "00:00", "end": "06:00" }, { "start": "13:00", "end": "15:00" } ],
    "weekend": [ { "start": "00:00", "end": "08:00" }, { "start": "20:00", "end": "24:00" } ]
  }
}
```

A missing file is no tariff. One that cannot be read is no tariff too, with a warning in the log; a
table in it that breaks a rule is read as no tariff until it is fixed on Settings, which still shows it.

**Configuration.** Two keys, neither of them on Settings:

```jsonc
"Tariff": {
  "Path": "data/tariff.json",  // relative to the content root unless absolute
  "Currency": "CZK"            // ISO 4217; labels the price fields and every price shown
}
```

The `.deb` package points `Tariff__Path` at `/var/lib/gleanvolt/tariff.json`. `Currency` labels prices
and converts nothing: change it only if your tariff is priced in something else.

**Where it shows.** The Overview has a line under its headline — *"High tariff, 4.80 CZK/kWh, until
22:00"* — and marks low tariff as a strip along the bottom of today's plan chart. Home Assistant gets
**Low tariff**, **Tariff change** and **Grid price** ([the entity table](HOME_ASSISTANT.md#what-each-entity-means)). The
HTTP API has `GET /tariff`, `GET /tariff/providers` and `GET`/`PUT /tariff/settings`; see its OpenAPI
document.
