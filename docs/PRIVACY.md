# Gleanvolt privacy policy

**Applies to:** Gleanvolt 1.0.5 and later.
**Last updated:** 2026-09-28.

Gleanvolt is a self-hosted controller for a home solar inverter and an EV charger. You run it on your
own machine, on your own network. There is no Gleanvolt account, no Gleanvolt server, and no
Gleanvolt-operated service of any kind.

**Nothing is collected by the author or by anyone else on the author's behalf.** Gleanvolt sends no
telemetry, no analytics, no usage statistics and no crash reports. There is no code in it that uploads
your energy, charging, vehicle or household data anywhere.

Everything below describes data that stays on the machine you installed it on, or that goes to a
third party you configured yourself.

## Why this document exists at all

Gleanvolt reads and records how much electricity your house generates and uses, minute by minute. That
is genuinely revealing: it shows when someone is at home, when a car arrives and leaves, and when the
household is awake. The data is not sensitive because of what it is called — it is sensitive because
of what it implies. So it is worth being exact about where it goes.

## What is stored, where, and for how long

All of it is on the machine running Gleanvolt, under the working directory, in files you own.

| What | Where | Contains | Kept for |
|---|---|---|---|
| Charging sessions | `data/sessions.db` (SQLite) | Start and end time, timezone, charging mode, home-battery state of charge, energy delivered split by source (solar, grid, home battery), peak power, the plan in force, and — if a vehicle account is configured — the car's reported state of charge and when it was reported | **365 days** (`SessionStore:RetentionDays`) |
| Energy history | `data/energy.db` (SQLite) | One row per 15 minutes: solar generated, grid imported, grid exported, EV charging, home battery charged and discharged, and battery state of charge | **Forever by default** (`EnergyMonitor:RetentionDays` is `0`, meaning keep everything) |
| Logs | `logs/gleanvolt-*.log` | What the controller decided and why; Modbus and network errors | **14 daily files**, then deleted |
| Secrets | the secrets directory (`data` by default) | See [Credentials](#credentials) | Until you remove them |
| Site configuration | `pv-system.json` | Site settings changed through the web interface, which may include your installation's name, its coordinates, and the inverter and charger addresses | Until you remove it |

No personal name, email address, postal address, telephone number or payment information is ever
asked for or stored. Gleanvolt has no user accounts.

### Deleting it

Stop Gleanvolt and delete the file. `data/sessions.db` and `data/energy.db` are ordinary SQLite files
with no other copy; removing one deletes that history permanently. Deleting the whole `data` directory
removes the history, the site configuration and the stored credentials together.

Both databases also prune themselves on the retention settings above. Setting
`EnergyMonitor:RetentionDays` to a number makes the energy history expire too; it keeps everything only
because a long series is what makes a forecast useful.

## What leaves your machine

Only these, and every one of them is **off until you configure it**.

### Your local network

- **The inverter and the EV charger**, over Modbus TCP, at addresses you configure. This is the point
  of the program. Nothing leaves your network to do it.
- **An MQTT broker**, if you enable the Home Assistant integration or an MQTT vehicle feed. Gleanvolt
  publishes the live figures and its own status to topics under a prefix you choose. The broker
  address is yours: it defaults to `localhost`, and if you point it at a broker outside your network
  then that is where those figures go. Gleanvolt does not choose that for you.
- **The web interface**, on port 8090 by default, over plain HTTP. It shows your energy and charging
  history and can change how the charger is driven, so **it will not show any of that until a password
  is set**. A fresh installation serves one page — the one that asks you to choose a password — and
  nothing else. Once set, the UI behaves normally and asks you to sign in.

  The traffic is plain HTTP, so the password and the pages are readable by anything that can watch
  the network between your browser and the controller. Keep it on a network you trust; put it behind
  a reverse proxy with TLS if you need more than that.

  Authentication can be switched off deliberately with `Web:RequireAuthentication=false`, which
  restores the open behaviour for an isolated network. It is not the default, and the controller says
  so loudly in its log on every start.
- **The HTTP API**, which is **off by default**. When switched on it requires an API key, and it
  refuses to start if it is enabled without one.

### The internet

| Service | Host | What is sent | When |
|---|---|---|---|
| Solcast solar forecast | `api.solcast.com.au` | Your Solcast API key and the rooftop-site identifier you registered *with Solcast* | Only if you configure both. Every three hours by default |
| OpenWeatherMap | `api.openweathermap.org` | Your API key and **your site's latitude and longitude** | Only if you configure an API key and coordinates |
| Volkswagen Group portal | `identity.vwgroup.io`, `eu-data-act.drivesomethinggreater.com` | Your account credentials, to sign in; then requests for your car's charging state | Only if you configure a vehicle account |
| Volkswagen website | `www.volkswagen.de` | Your account credentials, to sign in; your car's VIN, to ask about that car | Only if you configure it |
| Škoda Connect | `public.api.connect.skoda-auto.cz` | Your account credentials, to sign in; your car's VIN | Only if you configure it |

Requests to a car manufacturer send your credentials and your VIN **to that manufacturer**, under
their privacy policy, not this one. Gleanvolt is acting as you, with credentials you supplied, against
an account you already have. It keeps only what it needs from the reply: state of charge, estimated
range, time remaining, whether the car is plugged in and charging, and when the car reported it. Your
VIN is used to address the request and is not written to either database or published over MQTT.

Your approximate home location leaves your network **only** if you configure OpenWeatherMap, which
needs coordinates to return weather for the right place. Solcast is addressed by a site identifier you
created on their side, so Gleanvolt does not send coordinates there.

There is no other outbound connection. Gleanvolt does not check for updates, does not phone home, and
contacts no host that is not in the table above.

## Credentials

Vehicle-account passwords, the MQTT broker password, the Solcast key and the OpenWeatherMap key are
secrets, and Gleanvolt is built so they never need to be in a configuration file you might commit or
copy.

- On **Windows**, they are sealed with the Data Protection API (DPAPI) and readable only by the Windows
  account that wrote them.
- On **Linux and macOS**, they are written `0600` — readable only by the user Gleanvolt runs as.
- They may instead be supplied as environment variables or through a local `.env` file, which is never
  committed.
- Whoever can read the secrets directory can read those credentials. It is stated in the code and it is
  worth repeating here: protect that directory as you would an SSH key.

The **web interface password** is stored only as a hash, never in plain text. **API keys** are stored
as the secret itself, deliberately — they are generated, single-purpose, high-entropy values rather
than passwords a human chose and might reuse elsewhere.

No credential is ever logged, and no credential is sent anywhere except to the service it belongs to.

## Children

Gleanvolt is a tool for operating electrical equipment. It is not directed at children and collects
nothing from anyone.

## Changes

This policy describes the version named at the top. It is versioned in the repository alongside the
code, so its history is public and any change to it is a commit you can read.

## Contact

Questions, or a problem with anything stated here:
https://github.com/mpospisil/gleanvolt/issues
