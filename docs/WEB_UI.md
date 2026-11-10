# Self-hosted web UI (the `Web` section)

The controller serves its own web UI at **`http://gleanvolt.local:8090`** — or `http://<host>:8090`,
by the machine's address. It is on by default and needs no configuration: no broker, no Home
Assistant, no onboarding. It shows live telemetry, the charging
plan, session and energy history and the solar forecast, and it has every control Home Assistant has.

The web UI, [Home Assistant](HOME_ASSISTANT.md) and the [HTTP API](HTTP_API.md) drive the same
controller, so they cannot disagree about what the charger is doing: the last one to press wins, and
the others show it within a poll interval. The same rules apply on every surface:

- **The charge mode and the controls on Charging plan don't survive a restart**: the controller
  always starts in charge mode **Off**. Settings saved on **Settings** and **Car** are kept.
- `FastNoBattery`, `Targeted` and `SolarGrid` switch the mode back to `Off` by themselves when they
  finish.
- A charger that refuses the use-mode write says so on the page, instead of leaving a mode running
  that does nothing.
- The battery-hold switch shows the last command actually written to the inverter, not what was
  requested, so a write that fails shows the switch springing back.

## Finding it on your network

Gleanvolt announces itself on the LAN over multicast DNS (mDNS, also called Bonjour or zeroconf), so
you don't need to know the machine's address:

- **Type `http://gleanvolt.local:8090`** in a browser on any device on the same network.
- **Or browse for it**: it shows up as *Gleanvolt* in any Bonjour or "network services" browser
  (iOS and macOS, Android apps, `avahi-browse -rt _gleanvolt._tcp` on Linux), and Home Assistant's
  zeroconf sees it. The listing says whether the setup is still pending.

A second Gleanvolt on the same network becomes `gleanvolt-2.local`; the log says which name it got
(`Advertised on the LAN as http://gleanvolt-2.local:8090`).

**Where `.local` doesn't work**, use the machine's address instead. Your router's list of connected
devices shows it, and the Linux install prints it when it finishes:

- **Older Android** versions and some browsers don't resolve `.local` names; a Bonjour browser app
  still finds it.
- **Linux** without `nss-mdns` (`libnss-mdns`) doesn't resolve `.local` names.
- **Guest or IoT networks** often block multicast between devices.
- **Docker on a bridge network** never reaches the LAN with multicast: run the container with
  `--network host`, as the provided compose files do.
- **Windows**: allow Gleanvolt through the firewall when Windows asks, or UDP 5353 stays closed.

## Setup mode

A fresh install has none of the required settings: the web UI password and the inverter's and the
charger's addresses. Until all of them are set, Gleanvolt starts in **setup mode** instead of
stopping:

- The web UI and the settings run; the controller does not. Nothing polls the inverter, drives the
  charger, records sessions or energy, or talks to MQTT or a car.
- The UI shows the password page (if no password is set yet), then the [setup wizard](#the-setup-wizard).
  Pages that need the controller, such as the Overview, redirect to the wizard. both Settings tabs
  list what is still missing.
- The API answers every request with `503` and "Gleanvolt is not configured".
- The log starts with a `SETUP MODE` warning listing what is missing.

The site's location (`Pv:Latitude`, `Pv:Longitude`) and time zone are **recommended**: setup asks for
them, but Gleanvolt starts without them. Without a location there is no weather. Without a time zone
the controller runs in the host's zone, which on Windows or a host that reports UTC means days start
and sessions are filed at UTC midnight. Each recommended setting still missing is logged at startup.

When nothing required is missing, the notice offers **Start the controller**, which restarts
Gleanvolt normally. Docker and the .deb's systemd unit bring it back by themselves; with the Windows
zip, winget or `dotnet run`, start it again yourself.

Two problems keep the controller stopped even when everything else is set: a configuration key that
has moved (its old name is listed with the new one), and a `Controller:TimeZone` the host cannot
resolve (on the Nano Server image use a Windows id such as `Central Europe Standard Time`).

With the web UI disabled there is nothing to finish a setup in, so an unusable configuration stops
Gleanvolt at startup, with the reason in the console and the log and exit code 78 (`EX_CONFIG`).

## The setup wizard

**The recommended way to configure Gleanvolt.** After the password, a fresh install continues at
`/wizard`. Every step is one of the forms Settings uses, with the same checks and the same file, so a step skipped now can be done later on Settings.
Each step saves as it goes, so a reload or a closed browser loses nothing that was saved.

1. **Devices.** **Search** looks for inverters and chargers on this machine's own subnet (or a private
   range you type, at most a /22) and lists what each one identified as. Choose **Use as inverter** or
   **Use as charger**: Gleanvolt never assigns the roles itself, and warns when you pick a role the
   device did not claim. Addresses can also be typed in. Saving reads one value from each device to
   check it.
2. **Site.** Location and time zone (recommended), the home battery's usable capacity and charge
   efficiency, the array's details.
3. **Forecast** (optional). The Solcast account. In setup mode a save only stores it, so setup spends
   none of the day's Solcast calls.
4. **Car** (optional). The car's battery and limits, and the account its state of charge comes from.
5. **Extras** (optional). The tariff, the OpenWeatherMap key, the Home Assistant (MQTT) link and the
   API with its keys.
6. **Finish.** What is still missing, each with a link to its step; the recommended and optional
   settings not yet set; and **Start Gleanvolt**, which restarts it out of setup mode and then opens
   the Overview.

Optional settings never block anything. A page that needs one says it is not configured and how to
set it up.

## The pages

| Page | What it is for |
|---|---|
| **Overview** (`/`) | What is happening now. Reports only; it has no controls. |
| **Charging plan** (`/charging-plan`) | Where charging is decided: a tab per mode. |
| **Sessions** (`/sessions`) | Every recorded charging session, each with its chart. |
| **Energy** (`/energy`) | One recorded day at a time, drawn and printed. |
| **Forecast** (`/forecast`) | Today's and tomorrow's solar forecast, drawn and printed. |
| **Car** (`/car`) | The car, its account, and editing them. |
| **Settings → Installation** (`/settings`) | The PV system and its devices, Solcast, weather, the grid tariff, the Home Assistant (MQTT) link, the API and the password. |
| **Settings → Health** (`/settings/health`) | The running build, time zone and last poll; **Restart** and **Stop service**. |

`/pv-system` and `/health` still open the two Settings tabs. The header and the browser tab carry the
installation's name (`Overview — Home Roof`), so two tabs onto two installations can be told apart.
The sign-in page keeps the product name.

### Overview

Three sections, in the order the questions are asked:

- **Energy** — solar power against what the forecast expected for this moment, today's whole forecast,
  solar surplus, battery SOC and power, grid power.
- **Vehicle** — the configured car, the charger's view of whether it is connected, and what its feed
  reports. The card says which of four situations it is in: no feed configured (nothing is wrong), a
  reading and its age, a reading marked **stale** (older than `Vehicle:MaxAge`), or **sign-in
  required**, with a link to **Car**. *Stale* clears itself; *sign-in required* never does.
- **Charging session** — charge mode, control state, charger status, session energy, the car's power
  and current, target and active current, battery loan power. Shown only while a mode is driving, or
  while the car draws power under no mode (someone set the charger to `Fast` by hand). Otherwise it is
  one line with the mode, the charger's state and a link to start charging.

Every figure has its meaning one tap away, in the (i) beside its label — the same wording as
[What each entity means](HOME_ASSISTANT.md#what-each-entity-means).

### Charging plan

A common header over a tab per mode. The header holds the running mode, the **Off** button that ends
it, and the **battery discharge hold** switch (shown while `BatteryHold:Enabled` is on). Every action's
note and every charger refusal appears there too.

| Tab | The button | What else is on it |
| --- | --- | --- |
| **Solar** | Charge from surplus | Nothing to configure. The surplus, the battery SOC that gates it, and what the car is drawing. |
| **Forecasted** | Charge to the forecast | The plan's runtime numbers, then the day plan and its timeline chart. See [`Forecasted`](MODE_FORECASTED.md). |
| **Solar + grid** | Charge from sun + grid | The minimum solar surplus. See [`SolarGrid`](MODE_SOLAR_GRID.md). |
| **Fast (no battery)** | Charge at maximum | How much to deliver (until the car stops, kWh, or a battery target) and optionally a departure time. Then the car's draw, the setpoint, grid power and the amount delivered so far. See [`FastNoBattery`](MODE_FAST_NO_BATTERY.md). |
| **Targeted** | Preview plan → Start charging / Cancel | What the car reports, what you want (kWh or a battery target), the charging priority, **Sun only**, the departure and the minimum battery SOC, then the plan in words *before* the charger moves. See [`Targeted`](MODE_TARGETED.md). |

The tab is in the URL (`/charging-plan/forecasted`), so bookmarks and refreshes come back to it;
`/charging-plan` alone opens the mode that is driving. A dot marks the running mode.

The controls of every tab stay usable whatever is running, because a target is prepared *before* its
mode is started. A tab whose mode is not running shows an empty state rather than a stale plan.

### Sessions

A list of recorded sessions — date, duration, mode, energy delivered, solar share — each linking to a
detail page: the energy split by source, the day's forecast with its p10–p90 band, the weather at each
end of the session, and a chart of the session. See
[Charging session history](DATA.md#charging-session-history-the-sessionstore-section) for what is
recorded.

The chart uses the same lines as the energy chart below, over the session's own window. It adds:

- **The car's own SOC**, stepped at the time the car captured each reading (feeds often lag by hours).
- **A shaded band** where the battery discharge hold was armed, with the battery's line inside it.
- **The plan's SOC floor** beside the SOC, and the **battery loan** under the surplus, for sessions
  that had them.
- **A dashed rule** at each mode change, pause, or plan becoming unusable or usable again, listed under
  the chart.

A stretch with no samples is a gap, not a line drawn across it.

### Energy

One recorded day, drawn and then printed; a date picker and prev/next move both. See
[Energy history](DATA.md#energy-history-the-energymonitor-section) for what is recorded.

The **table** has a row per 15-minute interval: solar, forecast, grid each way, energy to the car,
the battery each way, the house, SOC and coverage, with day totals. A row the service observed only
part of is marked and counted under the table; a window no forecast covered shows a dash, not `0.00`.

The **chart** shows the shape of the day:

- **Watts, not kWh.** Each interval's energy is divided by the part of it that was actually observed,
  so a restart at 09:07 is not drawn as the sun going out.
- **Gaps stay gaps.** No rows means a hole in every line; a window observed for less than a tenth of
  its length is not drawn.
- **Import and charging are above zero, export and discharging below.** The table keeps them separate.
- **The forecast** is a dashed line on the same axis as the solar it forecast.
- **The whole day, in the site's time zone**, whichever zone your browser is in.
- **Short windows are marked** with a band along the bottom. Clicking a legend entry hides that line.

### Forecast

Today and tomorrow, in 30-minute periods: a chart of watts with the **median** as the line and
**p10–p90 as a band**, then a table with a row per period and each day's expected, p10, p90 and peak.

- **It never fetches.** It shows the forecast the controller already holds, and says when it was
  fetched. Solcast's free tier allows about ten calls a day.
- **After a restart, this morning is missing.** A provider only forecasts what is still to come, so
  the controller knows this morning's forecast only if it was running through it. The page says so.
- **Two days.** The day after tomorrow would be partial.
- **A missing band** shows as a dash in the table, and the chart notes it.
- **A period counts under the day it ends on**, as on the Overview and in the day plan.
- **Tomorrow's figures change** with every refresh until the day arrives.

The Overview's **Solar forecast** table is the glance; this page is the detail; the **Forecasted**
tab is the car's share of today.

### Settings → Installation

What this controller is and what it talks to: the system's name, id and address, coordinates, the
array's bearing (with the compass point), tilt, capacity, loss factor and commissioning date, and the
devices — the inverter and each charger with model, address and unit id. See
[The PV system](CONFIGURATION.md#the-pv-system-the-pv-section) for each value.

**What it shows is what is running.** Every value is read once at startup.

**Edit** changes what the *next* start reads. The PV system form covers the `Pv` section — identity,
location and orientation, capacity, the inverter and the charger. The Solcast, weather, tariff, MQTT
and API forms below it work the same way, with their secrets going to the secret store.

- **A save is checked by the same rules as startup.** If they refuse it, nothing is written and the
  page says why, so a saved file can never stop the controller from starting.
- **It goes to the overrides file, `Pv:OverridesPath`** — `data/pv-system.json` by default
  (`/opt/gleanvolt/data/` on the Pi, `/var/lib/gleanvolt/pv-system.json` under the .deb). It holds
  only the keys that differ, and **wins over `.env`** key by key; a command-line argument still wins
  over it. Each field says where its value comes from, and a field set here has **Revert**. The
  startup log line `PV system: …` lists the keys the file overrode.
- **A changed device address is probed once** before saving. An inverter address with no believable
  battery SOC, or a charger address with no believable run mode, is flagged as a likely swap. It warns
  and never blocks: a charger that is switched off answers nothing and can still be the right address.
- **Changing `Id` or clearing the coordinates asks first.** The id moves every MQTT topic (see
  [Names, topics and the device id](HOME_ASSISTANT.md#names-topics-and-the-device-id)); without
  coordinates no weather is recorded.
- **Saved is not applied.** Until a restart the page shows *Saved, not applied — N changes take effect
  on restart*, each as *running → saved*, with **Restart** beside it.
- **With authentication switched off the form is read-only**: it holds the addresses the controller
  writes Modbus commands to.

Older configuration keys still in use are listed under *Configuration to move*.

Below the devices, the **MQTT** section shows the two links — to [Home Assistant](HOME_ASSISTANT.md)
and to the [vehicle feed](VEHICLE.md#where-the-cars-state-of-charge-comes-from) — each with the broker,
the user and client id, and the topics actually in use (for Home Assistant, the topic prefix built
from `HomeAssistant:BaseTopic` and `Pv:Id`). It shows the configuration read at startup, not whether a
link is up. The **API** section says whether the [HTTP API](HTTP_API.md) is on, its base URL as this
page was reached, the key names, and a ready `curl` command.

**Passwords and keys are shown only when a login is enforced**, and then masked, behind *Reveal* and
*Copy*. The broker account can press every button Home Assistant has.

### Car

The car, on the same mechanism as **Settings → Installation**. Its sections are **Edit · Account · Ask the car ·
Running · Feed · Diagnostics**, in the order the work is done: *save → restart → sign in → ask the
car*. (`/vehicle-portal` redirects here.)

**One choice decides the feed: the manufacturer.**

| Choice | Feed it configures | What you fill in | Freshness |
|---|---|---|---|
| **Volkswagen** | [`Vehicle:Website`](VEHICLE.md#volkswagende-vehiclewebsite) | VW ID e-mail, password, VIN | ~20 s, plug state, live while charging |
| **Škoda** | [`Vehicle:Skoda`](VEHICLE.md#myškoda-vehicleskoda) | VIN, then an API key under **Account** | 5–15 min, plug state |
| **Audi · SEAT · Cupra · Bentley · VW commercial** | [`Vehicle:DataAct`](VEHICLE.md#eu-data-act-portal-vehicledataact) | Brand, VW ID, password, VIN | hours behind; no plug state |
| **A feed I publish myself** | [`Vehicle`](VEHICLE.md#where-the-cars-state-of-charge-comes-from) | Topic | whatever publishes it |
| **Another manufacturer — charge only** | none | nothing | — |

**Charge only is not an error.** Every car with a Type 2 inlet charges without a feed. The one thing
it costs: targets are asked for in kWh instead of per cent.

**The car's own fields are always there** — id, name, make, model, phases, the two currents, the
usable pack and the charge efficiency — because they make charging correct whatever the feed. What
the installation allows is printed beside the currents.

**Same overrides file, same rules as Installation.** A save writes only what differs, is checked by the
startup rules (for example *"the car's minimum 8 A is above the installation's maximum 6 A"* is refused
at the form), and nothing is written when anything is refused. Read-only with authentication off.

**Passwords never go in that file.** They go to the
[secret store](CONFIGURATION.md#the-data-directory-holds-secrets-the-secrets-section), owner-only. A
password box is write-only: it is always empty, empty on save means *unchanged*, and **Remove** clears
it. volkswagen.de and the Data Act portal share one stored VW ID password.

**A save cannot prove an account.** The feed is built at startup, and a volkswagen.de sign-in may want
an emailed code, so: save, **Restart**, sign in under **Account**, then **Ask the car**.

**Account** says what applies to the chosen feed:

| Chosen feed | What Account shows |
|---|---|
| **Volkswagen** | **Sign in** → the one-time code box → **Signed in** / **Sign out** |
| **Škoda** | the API key box: a password input, empty on render, emptied after every submit |
| **Data Act** | nothing to sign in to here: the portal signs in on each read, with the VW ID and password above |
| **A feed I publish myself** | nothing signs in: you publish the readings |
| **Charge only** | no account: nothing reads this car |

**Ask the car** asks the configured feed now. For a live feed (volkswagen.de, MyŠkoda) the answer
goes to the Overview too. A Data Act read signs in afresh and feeds nothing, which makes it the thing
to press *before* switching that feed on; it also shows how far its reading is from the feed's.
Opening the page never triggers a read. Charge-only gets no button; an own topic shows what to publish
and what last arrived.

The answer is one card — battery, range, reading age, charge state and plug state, as on the
Overview — plus the car's own **target SOC** and **time left** where the Data Act portal sends them.

**Running** shows the resolved car and its limits, and the feed this process actually started.
**Diagnostics** (collapsed, Data Act reads only) lists every field the delivery held, recognised or
not, and opens by itself when a read failed.

Setting up the Data Act portal is in [VW_PORTAL_SETUP.md](VW_PORTAL_SETUP.md).

### Settings → Health: Restart and Stop service

**Restart** stops the controller gracefully and exits with code `75`, so Docker's
`restart: on-failure` or the .deb's systemd unit starts it again — this is how a change saved on **Settings**
or **Car** is applied. It pauses a charge in progress, comes back in mode **Off**, leaves the
UI unreachable for about half a minute and spends a Solcast call; the page reloads when the controller
is back. Where nothing supervises the process (`dotnet run`, a plain `docker run`, the Windows zip) it
just exits.

**Stop service** shuts the controller down gracefully: the charger returned to its pause current, the
open session closed and written, Modbus and MQTT closed. It takes two clicks, because the UI goes down
with it and only a shell on the host (`docker compose start gleanvolt-controller`) brings it back. See
[Stopping and starting the controller](../deploy/README.md#stopping-and-starting-the-controller).

## Authentication

**A login is required. An installation with no password serves only the page that sets one** — no
Overview, no history, no controls until a password exists.

One shared password gates the whole UI (hashed with ASP.NET Core's `PasswordHasher`); there are no
per-user accounts. The setup page can only set a *first* password. Change it on **Settings → Installation**,
which asks for the current one; resetting a forgotten one is [below](#forgot-your-password).

**Optionally**, set it ahead of time, so no setup page is ever shown, by supplying the hash as a secret
— in `.env` or an environment variable, never in `appsettings.json`:

```
Web__PasswordHash=<hash>
```

Generate the hash with the worker binary or the image, which print it and exit:

```bash
dotnet Gleanvolt.Worker.dll hash-password '<your password>'
docker run --rm ghcr.io/mpospisil/gleanvolt:latest hash-password '<your password>'
```

`Web:RequireAuthentication` overrides the default in either direction and is rarely worth setting:

| `RequireAuthentication` | `PasswordHash` | Result |
|---|---|---|
| unset (default) | not set | **only the setup page is served**, until a password is set there |
| unset (default) | set | login required — **the normal way to run it** |
| `true` | set | login required; identical to the row above |
| `true` | not set | **host refuses to start** — nobody could ever sign in |
| `false` | either | UI served openly, no setup page; a warning is logged at every startup |

With the UI open, anyone on the LAN can press every button, including **Stop service**.

## Forgot your password

The sign-in page links to **Forgot your password?** (`/forgot-password`). There is no email, so a reset
proves one of two things:

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

  On Windows, open `data\password-reset-code.txt` next to `Gleanvolt.Worker.exe`, or the log in `logs\`.
  Only the newest code works, once, for 15 minutes. Codes can be requested once a minute and five
  times an hour.

Every reset or change does three things:

- it signs out every browser, including any tab still open;
- it issues a new recovery key, and the old one stops working;
- it is written to the log.

Ten wrong keys, codes or current passwords in 15 minutes lock all three paths for the rest of that
window. From a shell, `set-password` does the same without a browser — see
[Setting up from the command line](#setting-up-from-the-command-line).

## Setting up from the command line

**Optional.** The same setup without a browser, for a headless install, a script, or a site described
once in a file. The wizard is the recommended route. It saves through the same path, with the same checks, to the same files. None of these commands
starts Gleanvolt; each saves for the next start and says to restart.

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
store, never to the overrides file. A file holding secrets that every user on the machine can read is
refused until it is private (`chmod 600`), and after applying it the command reminds you that the file
still holds them.

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

## Settings (the `Web` section, optional)

The web UI needs no configuration. These keys exist for the rare case of changing its port, its name
on the network, or switching it off:

```jsonc
"Web": {
  "Enabled": true,      // master switch; while false the process binds no socket at all
  "Port": 8090,         // listens on every interface, plain HTTP
  "Advertise": true,    // announce it on the LAN over mDNS as <AdvertiseName>.local
  "AdvertiseName": "gleanvolt" // letters, digits and hyphens; Gleanvolt's own name, not the machine's
  // "RequireAuthentication"  // unset -- follow the password; see below, rarely worth setting
  // "PasswordHash": ""       // secret -- see below, never set it here
}
```

- **A password before anything is served.** A fresh installation serves only the page that sets one;
  see [Authentication](#authentication).
- **`Web:Enabled=false` means nothing is listening**, not "listening but empty": the process is a
  headless worker. In the Docker stack the host port stays published either way, so connections are
  refused.
- **Plain HTTP.** This is a LAN appliance; putting TLS in front of it is your decision.
- **`Web:Advertise=false` sends nothing on UDP 5353.** The announcement carries the name, the port,
  the version and whether the setup is pending — nothing about your site or devices. In the Docker
  stack the variables are `WEB_ADVERTISE` and `WEB_ADVERTISE_NAME`.
