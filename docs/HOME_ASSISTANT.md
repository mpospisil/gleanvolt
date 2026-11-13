# Gleanvolt with Home Assistant

Gleanvolt doesn't need Home Assistant: its own [web UI](WEB_UI.md)
and [HTTP API](HTTP_API.md) cover every control and every reading. If you use
Home Assistant anyway, there are two ways to connect them, and an installation should use **one or the
other**. With both on, it shows up in Home Assistant as two devices with the same entities.

- **[The Gleanvolt integration (HACS)](#the-gleanvolt-integration)**: recommended when you have
  no MQTT broker. Gleanvolt 1.2.0 or later. A custom integration installed through HACS that talks to
  the HTTP API. It can show a refusal to the person who pressed the button, has a real date-time
  entity for the departure, and has service actions with typed fields.
- **MQTT discovery**: Gleanvolt publishes itself to a broker as one device: a button per charge mode,
  switches and numbers for the settings you change day to day, and sensors for what it measures and
  plans. It suits a home that already runs a broker, and it is the only way to change *Minimum battery
  SOC*, *SOC resume margin* and *Min solar surplus* from Home Assistant until Gleanvolt 1.3. There are
  two ways to set it up:
  - **[To a Home Assistant you already run](#to-a-home-assistant-you-already-run)**: Gleanvolt is
    installed any way (the one-line Linux install, a NAS, Windows, Docker) and connects to your broker;
    you set the link in the web UI.
  - **[Home Assistant and a broker on the same Pi](#home-assistant-and-a-broker-on-the-same-pi)**:
    advanced. `./deploy/deploy.sh`, run from your computer, puts the controller, Mosquitto and Home
    Assistant on one Pi as three containers.

> **Don't run the Home Assistant container on a low-powered device.** On a board with less than 2 GB
> of RAM, run the controller alone, with its web UI, and connect it to a Home Assistant running on
> another machine, or leave Home Assistant out.

Either way, charge control boots in mode **Off** and nothing is written to your hardware until
charging is started.

## The Gleanvolt integration

For Gleanvolt **1.2.0 and later**. The integration lives in its own repository,
[gleanvolt-ha](https://github.com/mpospisil/gleanvolt-ha), and uses only the [HTTP API](HTTP_API.md),
the same way the MCP server does. It needs no broker, and it adds no capability of its own: everything
it does, the web UI and the API already do. The [user manual](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/home-assistant/)
walks through the setup with pictures.

1. **A key.** On **Settings → Installation → API access**, switch the API on, create a key named
   `home-assistant`, and **Restart** on **Settings → Health**. The key's name is what the log and each
   session record as the source of an action: *"API (home-assistant) started Targeted"*.
2. **Install it.** On Home Assistant 2026.11 and later, open **Settings → Marketplace → Add from
   link**, enter `mpospisil/gleanvolt-ha` (type *Integration*), install **Gleanvolt**, and restart Home
   Assistant. The Marketplace replaced HACS in 2026.11 and reads the same `hacs.json`. On an older Home
   Assistant, use **HACS → ⋮ → Custom repositories** with the same address.
3. **Settings → Devices & services.** Gleanvolt advertises `_gleanvolt._tcp` on the LAN, so Home
   Assistant offers *Gleanvolt discovered* with the host and port filled in. Only the key is asked
   for. Without discovery, add it by hand: host, port (8090) and key.

Setup calls `GET /api/v1/health` with the key. A 401 means the key is wrong. No answer means the host
can't be reached. A 404 means the API is switched off, and the form names the setting. A `version`
older than 1.2.0 is refused, and the form names the version it found. Later, a 401 starts
*re-authenticate*. *Reconfigure* changes the host or port, and *Configure* sets the poll interval.
*Download diagnostics* redacts the key.

**One device per installation**, identified by `systemId` from `/health`. Every entity's unique id is
derived from it, so a new address or a renamed entity keeps its history. The device's software version
is the build from `/health`, and *Visit* opens the web UI.

**It polls; nothing is pushed.** `GET /status` runs every 10 s by default (5 to 60 s under *Configure*),
and the device goes unavailable when that fails, including the 503 before the controller's first poll.
`/vehicle`, `/tariff`, `/charging/low-tariff-only` and `/charging/until-full` are read every minute,
and `/health` every five. It never asks for `/forecast?weather=true`, since that fetches weather.

**Charge mode is a select.** It shows `mode` from `/status`. Picking a mode calls
`POST /charging/start` with that mode and the parameters held in the integration's own entities (see
[The integration's entities](#the-integrations-entities)). Picking `Off` calls `POST /charging/stop`. A
400, or a 200 with `succeeded: false`, is raised to the person who picked it with the controller's own
message, for example *"Gleanvolt refused: …"*, and the select shows the real mode again. The held
parameters are drafts kept in Home Assistant and restored across a Home Assistant restart. They are
sent only when a mode is started. They are not a controller setting: Gleanvolt still boots in `Off`.

**Starting a mode, like the web UI's tabs.** On the web UI's Charging plan you open a mode's tab, set
what it needs, and press Start. The integration mirrors that. **Mode to start** is the tab: a held
select that starts nothing. **Start** starts that mode with its held parameters. **Charge mode** stays
what is actually running, and picking a mode there starts it straight away.

**The Gleanvolt panel.** The integration ships its own front-end module and adds a **Gleanvolt** entry
to the sidebar, with no setup. It shows a card per installation: Charge mode and the status, then
*Mode to start*, and under it only the picked mode's controls and figures, with its *Start* button. In
*Fast* and *Targeted*, the energy or SOC field follows the basis, and *Target rest SOC* appears only
with *JustInTime*, as in the web forms. The same view is in the card picker as **Gleanvolt**, with no
configuration, for any dashboard. The card finds its entities by the integration's own keys, so
renamed entities, an area in the entity id or a Home Assistant in Czech make no difference. The device
page still lists every entity at once.

**Plan figures follow their mode.** `/status` carries `dayPlan`, `targetedPlan` and `fastCharge` in
every mode, because the controller keeps their meters running across a switch. The integration shows
each only while its mode drives the charger, as MQTT does, and reports `unknown` otherwise.

**Service actions** let a script start a charge with its own fields, so nothing depends on held state:

- `gleanvolt.start_charging`: `mode`, plus the fast fields (`basis`, `energy_kwh`, `target_soc`,
  `depart_by`) or the targeted ones (`energy_kwh` or `target_soc`, `depart_by`, `priority`, `rest_soc`,
  `sun_only`, `low_tariff_only`). Fails with the controller's reason.
- `gleanvolt.stop_charging`.
- `gleanvolt.preview_targeted`: the targeted fields. It **returns the plan** as response data, the
  same answer as `POST /plans/targeted/preview`, and starts nothing.

**Not in the integration yet:** *Minimum battery SOC*, *SOC resume margin* and *Min solar surplus*.
The 1.2 API can't change them. 1.3 adds them to the API (#392), and the integration follows. Settings,
the tariff table, Solcast, the setup wizard and **Stop service** stay in the web UI.

## To a Home Assistant you already run

This and the next section are the MQTT way. Home Assistant and its broker stay where they are — on another machine, or on this one outside the
Docker stack — and the controller connects to the broker.

**In the web UI**, on **Settings → Installation → Home Assistant (MQTT)** (or in the setup wizard's
**Extras** step): switch it on, and enter the broker's address and port, the user name and the password.
**Test connection** checks the broker before you save. The installation needs an id (`Pv:Id`, on the
same page); the form says so if it is missing. Then **Restart** on **Settings → Health**. The entities
appear in Home Assistant through its MQTT integration's discovery.

**Optionally, in `/opt/gleanvolt/.env`** instead — what is saved in the web UI wins over it:

```bash
HOMEASSISTANT_ENABLED=true
PV_ID=home-roof                # the topic segment; required with Home Assistant
MQTT_BROKER_HOST=192.168.2.20  # the broker's address; unset is this machine (localhost)
MQTT_BROKER_PORT=1883          # only if it is not 1883
MQTT_USERNAME=solax            # an account on *that* broker
MQTT_PASSWORD=<its password>
```

These lines also set the broker for the [car's own MQTT feed](VEHICLE.md#your-own-feed-over-mqtt-the-vehicle-section),
which the web UI form does not cover. The older `VEHICLE_BROKER_HOST`/`VEHICLE_BROKER_PORT` still work
for that link, and win when set.

## Home Assistant and a broker on the same Pi

For a Pi with **2 GB of RAM or more**, managed from your computer with the deploy scripts — see
[Deploying to a Raspberry Pi from your computer](../deploy/README.md). Prepare the Pi
[as described there](../deploy/README.md#prepare-the-pi-once), then add the three steps below, and
deploy with `./deploy/deploy.sh` instead of `deploy-controller-only.sh`.

### 1. The settings in `.env`

In `/opt/gleanvolt/.env`, beside the controller's own settings:

```bash
HOMEASSISTANT_ENABLED=true     # the controller's own switch to publish MQTT
PV_ID=home-roof                # required with Home Assistant: the topics hang off it
MQTT_USERNAME=solax            # must match the broker's password file
MQTT_PASSWORD=<your password>  # must match the broker's password file
```

### 2. Directories for the broker and Home Assistant

```bash
sudo mkdir -p /opt/gleanvolt/{mosquitto/config,mosquitto/data,homeassistant/config}
sudo chown -R 1883:1883 /opt/gleanvolt/mosquitto/data         # the broker writes here
```

**Do not chown `mosquitto/config` to 1883.** Only `mosquitto/data` belongs to the broker. The config
directory is written by the deploy script (that is where `mosquitto.conf` lands) and only *read* by
the broker, whose compose mount is read-only — so handing it to uid 1883 locks the deploy out of it
and fails with `tar: mosquitto/config/mosquitto.conf: Cannot open: Permission denied`. The one file in
there that does belong to 1883 is `passwd`, chowned in the next step.

### 3. Broker credentials

The broker refuses anonymous connections, so the password file must exist before the stack will work. The username has to match `MQTT_USERNAME` **and** the
password has to match `MQTT_PASSWORD` in `.env` — a broker password with an empty `MQTT_PASSWORD`
beside it is a stack that comes up looking healthy while the controller is refused on every connect.

`-c` **creates** the file and refuses to overwrite one that exists, reporting the bare errno
`Error: Unable to open file ... for writing. File exists.` — which reads like a permissions problem
and isn't. So create with `-c` the first time and update without it afterwards:

```bash
# first time (no passwd file yet)
docker run --rm -v /opt/gleanvolt/mosquitto/config:/mosquitto/config eclipse-mosquitto:2 \
    mosquitto_passwd -c -b /mosquitto/config/passwd solax '<password>'

# changing the password later, or adding another user -- note: no -c
docker run --rm -v /opt/gleanvolt/mosquitto/config:/mosquitto/config eclipse-mosquitto:2 \
    mosquitto_passwd -b /mosquitto/config/passwd solax '<password>'

sudo chown 1883:1883 /opt/gleanvolt/mosquitto/config/passwd
sudo chmod 600 /opt/gleanvolt/mosquitto/config/passwd
```

`mosquitto_passwd` warns that the file's owner is not root and that "future versions will refuse to
load" it. Ignore it here: the broker runs as uid 1883 and has to be able to read the file, and the
compose mount is read-only so the image's entrypoint cannot chown it back.

**Check the credentials actually work** before blaming the controller. This starts a throwaway broker
against the real password file, so it is valid even before `deploy.sh` has copied `mosquitto.conf`:

```bash
docker run --rm -v /opt/gleanvolt/mosquitto/config:/mosquitto/config:ro eclipse-mosquitto:2 sh -c '
  printf "listener 1883\nallow_anonymous false\npassword_file /mosquitto/config/passwd\n" > /tmp/t.conf
  mosquitto -c /tmp/t.conf -d && sleep 2
  mosquitto_pub -h 127.0.0.1 -u solax -P "<password>" -t solax/authtest -m ok && echo ACCEPTED
  mosquitto_pub -h 127.0.0.1 -t solax/authtest -m no 2>/dev/null && echo "ANONYMOUS ACCEPTED -- wrong" || echo "anonymous rejected -- correct"
'
```

### Deploy, then onboard Home Assistant

```bash
./deploy/deploy.sh
```

It does everything `deploy-controller-only.sh` does, and also copies `mosquitto.conf`, seeds Home
Assistant's config files if they don't exist yet, and starts the two extra containers. Then:

1. Open `http://<pi>:8123` and complete Home Assistant onboarding (local account).
2. **Settings → Devices & Services → Add Integration → MQTT.** Broker `localhost` (Home Assistant
   and the broker share the Pi's network; `mosquitto` also works, and is what older installs have),
   port `1883`, and **the username and password from `.env`** — this broker is authenticated.
3. The controller publishes MQTT discovery configs on connect; the device and its entities appear by
   themselves.

### Switching to and from it

Which containers run is decided by **which script you run**, not by a setting that can go stale:
`docker-compose.yml` gives `mosquitto` and `homeassistant` a `profiles:` key, and both scripts set
`COMPOSE_PROFILES` themselves. To add Home Assistant to a controller-only Pi, do the three steps above
and run `./deploy/deploy.sh`. To remove it, run `./deploy/deploy-controller-only.sh` — the extra
containers go and your data stays.

### Updating

Run `./deploy/deploy.sh` again; everything in
[Updating a running deployment](../deploy/README.md#updating-a-running-deployment) applies.

`deploy.sh` pulls **every** image in the stack, not just the controller. That
means `ghcr.io/home-assistant/home-assistant:stable` and `eclipse-mosquitto:2` move to whatever those
tags point at now. That is usually what you want, but it means "I updated the controller" can also
mean "Home Assistant jumped a version" — worth knowing before you go looking for what changed.

To move only the controller, do that one step on the Pi instead:

```bash
ssh martin@192.168.2.7 'cd /opt/gleanvolt && docker compose pull gleanvolt-controller && docker compose up -d gleanvolt-controller'
```

That skips copying any updated compose files, so use it for a plain image bump, not after changing
`deploy/`.

`homeassistant/config/` is seeded once and never overwritten; `mosquitto/config/passwd` is yours and
never touched. `mosquitto.conf` is overwritten on every deploy, so change it in the repository.

### Where its logs and data are

| Host path | What it is | Back up? |
|---|---|---|
| `/opt/gleanvolt/homeassistant/config` | Home Assistant's `.storage` (account, entity registry, MQTT integration), recorder database and `home-assistant.log` | **critical** |
| `/opt/gleanvolt/mosquitto/config` | `mosquitto.conf`, the password file | yes |
| `/opt/gleanvolt/mosquitto/data` | retained messages, sessions | no |

The broker logs to stdout only (`docker compose logs mosquitto`). Losing `homeassistant/config/.storage`
costs onboarding, the account and the MQTT integration, so add both config directories to the backup
in [Where the data lives](../deploy/README.md#where-the-data-lives):

```bash
sudo tar czf "solax-backup-$(date +%F).tar.gz" -C /opt/gleanvolt .env data homeassistant/config mosquitto/config
```

and, after restoring, give the broker its files back with `sudo chown -R 1883:1883 /opt/gleanvolt/mosquitto`.

### Troubleshooting

**Home Assistant is killed during startup.** Raise `HA_MEM_LIMIT` in `.env` and trim the recorder in
`/opt/gleanvolt/homeassistant/config/configuration.yaml`. If it still doesn't fit, move Home Assistant
to another machine and [connect to it there](#to-a-home-assistant-you-already-run).

**Nothing connects to the broker.** `docker compose logs mosquitto` shows the rejected connections.
Almost always the password file and `MQTT_USERNAME`/`MQTT_PASSWORD` disagreeing, or the file not
being readable by uid 1883. `/pv-system` in the web UI shows what the controller is dialling and as
whom — including the client id to search the broker's log for — so the two sides can be compared
without an ssh session.

**Port 8123 or 1883 is already in use.** Something else on the Pi — usually a Home Assistant or
Mosquitto you already run — has it. Use that one instead:
[connect to a Home Assistant you already run](#to-a-home-assistant-you-already-run).

## Settings (the `HomeAssistant` section)

Disabled by default. **In the web UI**, on **Settings → Installation → Home Assistant (MQTT)** or in
the wizard's **Extras** step: on/off, the broker's address and port, the user name, the password and the
discovery prefix. They apply on the next start. **Test connection** connects once with what the form
holds, without disturbing the running link. The same page shows what the controller is publishing and
where, including the topic prefix; the password is shown only behind a login.

**Optionally, in configuration** — the web UI's values win over these:

```jsonc
"HomeAssistant": {
  "Enabled": false,
  "BrokerHost": "localhost",
  "BrokerPort": 1883,
  "DiscoveryPrefix": "homeassistant", // HA's discovery prefix
  "BaseTopic": "gleanvolt",           // {BaseTopic}/{Pv:Id}/... is where everything is published
  "DeviceId": "",                     // the unique-id root; "" = take Pv:Id. See the warning below
  "RetireDeviceIds": [],              // former DeviceId values whose retained configs to blank
  "RetireTopicPrefixes": [],          // former {BaseTopic}/{id} prefixes whose retained state to clear
  "StatusInterval": "00:00:15"
}
```

The broker credentials are secrets: `HomeAssistant__Username` and `HomeAssistant__Password` in `.env`
or the environment, never in `appsettings.json`.

## Names, topics and the device id

**The PV system's id namespaces the topics.** Everything this controller publishes hangs off
`{BaseTopic}/{Pv:Id}` — `gleanvolt/home-roof/state`, `/availability`, `/{entity}/set` — so two
installations can share one broker without overwriting each other. `Pv:Id` is therefore **required once `HomeAssistant:Enabled` is true**; a
controller-only deployment publishes nothing and needs none. The device page in Home Assistant is named
after `Pv:Name`.

**`HomeAssistant:DeviceId` is the unique-id root, and changing it costs the history.** Home Assistant
keys an MQTT entity to its `unique_id`: a new one is a *new entity*, created as `sensor.…_2` because the
old entity id is still taken, and every graph on your dashboard starts again. Nothing else here behaves
like that — topics, the device name and the device identity can all change freely, and the entities
follow — which is precisely why this one value does **not** follow `Pv:Id` automatically.

- Empty means "take `Pv:Id`" — one id, configured once, which is what a fresh installation wants.
- An installation that already has history **pins the id its entities were created with**.
  `deploy/docker-compose.yml` pins `solax_controller` for exactly that reason.
- To change it deliberately, set `RetireDeviceIds` to the old value in the same deploy, so the retained
  discovery configs left on the broker are blanked rather than re-creating yesterday's device on the
  next restart. The procedure that keeps the history is under
  [Changing the device id](#changing-the-device-id).

**`RetireTopicPrefixes`** clears retained *state* under a prefix you no longer publish on — set it to
`solax/solax_controller` once after upgrading from a release that predates `Pv:Id`, then remove it. It
changes nothing functional: it is the difference between a broker where `mosquitto_sub -t '#' -v` shows
one of everything and one where it shows two.

### Changing the device id

In the deploy stack's `.env`, three names reach Home Assistant, and only one of them is expensive to
change.

| What | Set by | Changing it |
|---|---|---|
| The topics everything is published on | `PV_ID` | **Free.** The discovery configs are republished pointing at the new topics and Home Assistant re-subscribes. |
| The device page's name | `PV_NAME` | **Free.** Entity ids are assigned when an entity is first created and are not recomputed. |
| Every entity's `unique_id` | `HA_DEVICE_ID` | **Discards the history.** |

If you want to change it anyway — say, to match a `PV_ID` on a system whose history you do not mind
losing — set both in the same deploy:

```bash
HA_DEVICE_ID=home-roof
HA_RETIRE_DEVICE_ID=solax_controller     # blanks the retained configs the old id left behind
```

Without the second line, the old device comes back from the broker's retained messages on the next
restart, permanently unavailable, and you have two of everything.

**To change it and keep the history**, the procedure is manual and worth doing carefully:

1. Note the current entity ids (Settings → Devices & Services → MQTT → the device).
2. Stop the controller, and set `HA_RETIRE_DEVICE_ID` to the old id so the retained configs go.
3. Delete the old device from Home Assistant's registry. The recorder's rows are keyed by the entity
   id *string* and survive this; deleting frees those ids for reuse.
4. Deploy with the new `HA_DEVICE_ID` and let discovery create the entities.
5. Rename each new entity's entity id back to the exact old one, accepting Home Assistant's prompt to
   migrate long-term statistics. History and statistics then continue on the same key.

Automations and dashboards reference entity ids, so if step 5 is done they need no edits.

## What each entity means

Entity names are kept short so a dashboard card stays readable — and Home Assistant has nowhere to put
a longer explanation anyway: hovering an entity shows its friendly name, and MQTT discovery has no
description or tooltip field. The meanings live here instead.

**The service always starts in `Off`**, whatever is in the config, and nothing persists a mode or a
runtime setting across restarts. After a crash, a power cut or a deploy the charger is therefore left
exactly as its owner set it. Charging is started by a button and by nothing else: each writes the
charger's use-mode `Fast` and then selects its strategy.

| Entity | Unit | What it means |
| --- | --- | --- |
| **Charge mode** | sensor | Which strategy is driving the charger: `Off`, `Solar`, `Forecasted`, `FastNoBattery`, `Targeted` or `SolarGrid`. Read-only — it reports what a button did, and it moves on its own when `FastNoBattery`, `Targeted` or `SolarGrid` finish. Always `Off` after a restart. |
| **Charge solar** | button | Starts the `Solar` strategy: writes the charger's use-mode `Fast`, then selects the mode. A charger that refuses the write leaves the mode untouched and logs a warning, rather than reporting a strategy that is doing nothing. |
| **Charge forecasted** | button | The same, for `Forecasted`. |
| **Charge fast** | button | The same, for `FastNoBattery`, and it applies the three **Fast** entities below as it goes. Read the warning about `MaxChargingCurrentAmps` before pressing it — this one draws the site's supply limit for hours. An amount it cannot honour (a battery target with no configured capacity, a car already past it) logs a warning and starts nothing, rather than charging to full instead. |
| **Charge solar + grid** | button | The same, for `SolarGrid`. Reads **Min solar surplus** on every poll rather than at the press, so the number can be moved while it runs. Switches itself off at the end of the day's useful sun, when the car stops drawing, or when it is unplugged. |
| **Charge off** | button | Writes the charger's use-mode `Stop` and returns the mode to `Off`, releasing any hold a mode had armed. Always writes, even when the controller was already `Off` and never took control: the button says stop charging, so it stops charging. The current setpoint is left wherever the last cycle put it. This stops *the car*, not the controller — that is **Stop service**. |
| **Battery discharge hold** | switch | Stops the home battery serving household load, so the car charges from PV and grid while the battery can still charge from surplus. Shows the last command written successfully, not a read-back — the register can't be read, so a failed write shows up as the switch springing back to `OFF`. `FastNoBattery` turns it **on** when it starts and **off** when it ends, whatever ended it — and in between this switch is yours: turning it off really releases the hold, and the car goes on charging at maximum. `Targeted` and `SolarGrid` are different: they arm their own hold only while the car draws more than the sun gives — `Targeted`'s grid block, the grid bridge, or a cloud the 3-minute average has not caught up with — keep it for `BatteryHold:BridgeReleaseDwell` after the last of those while charging, and never touch this switch. |
| **Minimum battery SOC** | % | The hard floor the forecast plan may never take the home battery below, however good the forecast looks. Doesn't persist across restarts. |
| **SOC resume margin** | % | How far above the floor the battery must recover before a paused session restarts — charging continues down to the floor itself, only coming back costs the margin. Raise it if the car starts and stops repeatedly on a marginal day. Doesn't persist across restarts. |
| **Forecasted until full** | switch | With it `ON`, one `Forecasted` session runs over several days: stood down at night where it would have ended, re-armed on the next day's sun, until the car is full, unplugged, or switched **Off**. Starts nothing on its own. A session running with it on is resumed after a restart; the switch alone, with no session running, goes back to `ChargeControl:Forecast:UntilFull`. See [Until full](MODE_FORECASTED.md#until-full). |
| **Fast basis** | select | What a fast charge is aiming at: `Full` (the car decides — the default), `Energy` (reads **Fast energy**) or `Soc` (reads **Fast target SOC**). Held until **Charge fast** is pressed; a basis chosen and not pressed changes nothing. Doesn't persist across restarts. |
| **Fast energy** | kWh | How much to deliver before the fast charge stops itself, measured at the charger and metered from the press. Read only under the `Energy` basis. Doesn't persist across restarts. |
| **Fast target SOC** | % | The state of charge to stop a fast charge at. Read only under the `Soc` basis, and only honoured with `Ev:Vehicles:0:BatteryCapacityKWh` configured and a reading from the car — converted to kilowatt-hours once, at the press, and not re-derived from a later reading. A reading older than `Vehicle:MaxAge` is **refused**, not converted: the press logs why and starts nothing, since a button has nowhere to show a message (#179). Ask in **Fast energy**, or press `Full`. Doesn't persist across restarts. |
| **Fast departure** | text | When the car has to be ready, as `HH:mm` (the next one) or `yyyy-MM-dd HH:mm`. Empty means charge straight away, which is the default. With a time the charge is held back and starts as late as it still can — see [When? (the departure)](MODE_FAST_NO_BATTERY.md#when-the-departure). Needs an amount to work back from, so a departure with the basis on `Full` is refused. Doesn't persist across restarts. |
| **Fast start** | sensor | When the deferred charge will begin, as `HH:mm`, or `none` when it starts immediately. Moves later as energy goes in, and earlier if the car turns out to draw less than the charger offers. Absent unless `FastNoBattery` is driving with an amount set. |
| **Fast delivered** | kWh | Energy delivered against the fast charge's amount, since it was started. Absent unless `FastNoBattery` is driving with an amount set. |
| **Fast target** | kWh | The amount that fast charge is working to — the number **Fast delivered** is counting towards. Absent on the same terms. |
| **Min solar surplus** | W | The smoothed surplus below which `SolarGrid` pauses the car instead of topping it up from the grid — and the line the rest of today's forecast has to clear for the mode to keep waiting. Applied at once, including to a running charge. The most the grid ever adds is the charger's floor (~4.14 kW on three phases) less this; at or above the floor the grid never helps. Doesn't persist across restarts. |
| **Fast low tariff only** | switch | With it `ON`, `FastNoBattery` waits through high tariff and charges flat out once low tariff starts. The wait is never read as a full car, and the discharge hold is released while it lasts so the battery can serve the house. The charger starts a car on its own at plug-in, so one plugged in during high tariff draws for a moment until the next poll stops it. A departure time does not know about the tariff, so a deferred charge can run out of time, and says so. Ignored, and said so on the Overview, while no tariff is set up on Settings. Doesn't persist across restarts. |
| **SolarGrid low tariff only** | switch | With it `ON`, high tariff pauses `SolarGrid`, sun or not: the car charges only in low tariff, and a sunny high-tariff afternoon's surplus goes to the home battery or the grid instead. The mode still ends with the day's useful sun, so a low tariff that starts after dark charges nothing here. Ignored while no tariff is set up. Doesn't persist across restarts. |
| **Sun until** | sensor | When today's forecast surplus last clears **Min solar surplus**, as `HH:mm` — the time `SolarGrid` will switch itself off if the live surplus agrees. `none` when the forecast has no such sun left; absent with no usable forecast, and whenever another mode is driving. |
| **Low tariff** | on/off | `ON` while the grid is in the low (cheap) rate of the tariff selected under *Tariff* on Settings, `OFF` in the high rate. **Unknown** with no tariff selected, or one that cannot answer — no tariff is not the same as high tariff. Follows a save on Settings by the next status publish, with no restart. |
| **Tariff change** | timestamp | When the rate next changes. A night tariff entered as 22:00–24:00 and 00:00–06:00 changes at 06:00, not at midnight. Unknown with no tariff, and when the next change lies beyond what the provider knows (the `Table` provider knows today and tomorrow). |
| **Grid price** | `Tariff:Currency`/kWh | What a kWh from the grid costs right now: the low or high price entered on Settings, or a provider's own price for the moment. Unknown without prices or a tariff. Shaped for Home Assistant's energy dashboard as an *entity with the current price*. |
| **Control state** | — | What charge control is doing right now. `Disabled`: nothing is running, the charger is the owner's. `Idle`: a strategy is running but not acting — most often because the charger has been taken out of Fast at the wallbox since it was started. `Charging`: a current is being commanded. `Paused`: the setpoint was dropped to the pause current, typically because the surplus fell below what the charger's 6 A floor needs. |
| **Charger status** | — | The charger's own state, straight from its register. `Available`: no car. `Preparing`: plugged in, not yet drawing. `Charging`. `SuspendedEv`: the *car* stopped the draw, usually at its own charge limit. `SuspendedEvse` / `ChargePaused`: the *charger* stopped it — what our pause write produces. `Finishing`: session closing. `Faulted` / `Unavailable`: not usable. |
| **Solar power** | W | PV production measured at the inverter, before the house, the battery or the car take any of it. |
| **Forecast solar power** | W | What the forecast expected the roof to be making **at this instant** — the direct counterpart to **Solar power**, so the two chart against each other. `0` when no forecast covers this moment: none fetched yet, the provider is down, or the instant is past the horizon. Published in every mode, not only `Forecasted`, because the comparison is how you decide whether to select that mode at all. |
| **Solar surplus** | W | PV left after household consumption, counting neither battery charging nor EV charging as house load — the power the car can take without importing or discharging. This is the **smoothed 3-minute average** the solar modes decide on, not the instantaneous value, so a passing cloud can't interrupt a session. `Unknown` when the selected mode doesn't decide on surplus. |
| **EV charging power** | W | What the charger reports the car actually drawing right now — not a setpoint. |
| **EV charging current** | A | Charging current derived from the charger's measured power, phase-aware: what the car really takes. A car may draw less than the setpoint allows, never more. |
| **Target charging current** | A | The current the controller decided on this cycle, before it was written. `Unknown` whenever it isn't charging — paused, idle, or in `Off`. |
| **Active charging current** | A | The charger's setpoint **read back** from the hardware: what it was actually left at. Compare it with the target — if they disagree for more than a poll or two, a write isn't landing (or the controller is in dry run). |
| **Battery SOC** | % | Home battery state of charge as the inverter reports it. 0–100% spans the capacity the pack will actually cycle — its usable energy, not its nameplate. |
| **Battery power** | W | Positive while the battery charges, negative while it discharges. With the discharge hold armed this should sit at or above roughly −60 W: a working hold still leaves a small standby trickle, but the pack is no longer serving the house. |
| **Grid power** | W | Positive while importing from the grid, negative while exporting. This is the opposite of the sign the SolaX register uses; it's negated on read so positive always means power flowing into the house. With `Pv:Inverter:UnmeteredGridPhase` set it is an **estimate** — the meter's two seen phases plus the blind one worked out from the inverter's output — and will disagree with the meter by design; see [the `Pv` section](CONFIGURATION.md#the-pv-system-the-pv-section). |
| **Battery hold target** | W | The power target commanded at the inverter's grid connection point to keep the battery out of house load: minus whichever is smaller, house load or PV. `Unknown` when no hold is armed. |
| **Car connected** | on/off | `ON` while a vehicle is plugged in — the charger reporting `Preparing`, `Charging`, `Suspended*`, `ChargePaused` or `Finishing`. Says nothing about whether the car is drawing. |
| **Car feed** | — | How the installation's one [manufacturer vehicle feed](VEHICLE.md#eu-data-act-portal-vehicledataact) — volkswagen.de, MyŠkoda or the Data Act portal, whichever is configured ([#212](https://github.com/mpospisil/gleanvolt/issues/212)) — is doing, with the sentence as a `reason` attribute. `Ok`: the last read produced a reading. `Degraded`: it is trying and not currently succeeding — a 5xx, a timeout, an expired session, or a delivery not filled yet; it backs off and clears itself. `NeedsOwner`: a refused password, a consent screen, an OTP or a portal setting only you can make — **the feed has stopped asking** and will not resume until you have cleared it and restarted the controller. That last state is the one worth a notification; the other two are not. Absent entirely on an installation with no such feed. |
| **Charging now** | on/off | `ON` while *the controller* is commanding a charging current, as opposed to having paused or never taken control. This is our own decision, not the car's behaviour — a car can be plugged in and idle while this is `ON`. See **EV charging power** for what's actually flowing. |
| **Stop service** | button | Shuts the controller down gracefully: the charger is returned to its pause current, the open session is closed and written, and the store is flushed — none of which happens if the process is killed. **One-way from here.** The service is what speaks MQTT, so this device goes unavailable and nothing in Home Assistant can start it again; that needs a shell on the Pi (`docker compose start gleanvolt-controller`). Lives in the device's *Configuration* section rather than on the dashboard card, and only an exact `PRESS` on its topic triggers it. See [Stopping and starting the controller](../deploy/README.md#stopping-and-starting-the-controller). |

The rest are populated only while the **Forecasted** mode is driving; in the other modes they report
nothing rather than stale numbers from a plan nobody is acting on.

| Entity | Unit | What it means |
| --- | --- | --- |
| **Plan state** | — | One line on why the day plan says what it says — the same explanation the log carries. |
| **Charge window** | — | The next stretch of today in which the surplus is forecast to clear the charger's minimum power for long enough to be worth starting. `none` when today offers no such window. |
| **EV energy budget** | kWh | How much of today's remaining sun the car may have: what's left once the house and a 100% battery by evening are served, then restricted to the periods where the surplus actually clears the charger's minimum power. The restriction matters because the car can't sip a budget slowly. |
| **Projected shortfall** | kWh | How far today's forecast falls short of the house plus a full home battery. Above zero means the evening 100% is at risk. The car is **not** in this sum — it only ever gets what is left once both are served, so it can neither cause this shortfall nor be measured against it. |
| **Surplus spill** | kWh | Remaining surplus the home battery has no room for: what is still coming, less what it can absorb by the evening deadline. At 100% it is all of it, and every watt of it leaves the house unless the car takes it. Above zero it is what lets the pack lend against a much thinner surplus than usual, because the energy lent is not energy it would have kept. |
| **Loan headroom** | kWh | How much the pack may lend right now for free: the room between its SOC and the floor, capped by the spill. Zero when SOC sits on the floor, and zero when every remaining watt has a home in the pack — which is the answer to "why is nothing being lent while the sun is out?". |
| **Required SOC floor** | % | The SOC the battery must not fall below right now if the sun still to come is to return it to 100% by the evening deadline. It climbs towards 100% as the day runs out, which is what squeezes the car out of the late afternoon without any scheduling. Battery SOC dropping to this line is what pauses the car. |
| **Trajectory SOC floor** | % | The same figure before the **Minimum battery SOC** clamp is applied: what the forecast on its own says the battery could be drawn down to. Well below the floor in force on a sunny morning, equal to it once the day is short. The pair is the first thing to read when a session pauses — it says whether the car is being held back by the forecast or merely by your configured minimum, and the controller stops the session harder in the first case. |
| **Forecast remaining today** | kWh | Forecast PV still to come today, at the configured confidence band and already scaled by **Forecast accuracy**. |
| **Tomorrow forecast** | kWh | Tomorrow's forecast production. Purely informational: context for whether a shortfall today is worth waiting out. |
| **Forecast accuracy** | % | Actual production against forecast so far today. 100% is on the nose, above means the roof is beating the forecast. The rest of the day's forecast is scaled by this, and a sustained large miss makes the plan untrusted, so the mode falls back to live-solar behaviour. |
| **Session energy** | kWh | Energy delivered to the car since it was plugged in. Starts afresh when it's unplugged. |
| **Battery loaned today** | kWh | Energy the home battery has lent the car today so that a surplus below the charger's floor could still reach it. The loan is repaid from sun that would otherwise have been exported. Resets at local midnight. |
| **Battery loan power** | W | How much of what the car is drawing right now is being covered by the home battery rather than by live sun. Zero outside the `Forecasted` mode. |

The targeted-charge entities. The six controls are always live — a request can be prepared before
the mode is selected — while the nine sensors are populated only while **Targeted** is driving, by the
same rule the forecast plan follows.

| Entity | Unit | What it means |
| --- | --- | --- |
| **Target energy** | kWh | How much energy the car needs by the departure time, measured at the charger from the moment **Activate target** is pressed. Nothing happens until it is. |
| **Departure time** | text | When that energy has to be there. A bare `07:00` means the **next** 07:00 — which is what somebody typing it at 22:00 means by it; `2026-08-11 07:00` means exactly that. MQTT discovery has no datetime platform, which is why this is text. Read back as the resolved timestamp, so a day later it is still unambiguous. **Empty means no departure** — which is what it holds whenever no target is set, and clearing the box clears the pending departure. Anything else is refused with a warning in the log rather than guessed at. |
| **Charge priority** | select | `Cheapest` (the default, and what every request did before this existed) or `JustInTime`. Under `JustInTime` the last stretch of the target is held back so the car finishes shortly before departure instead of hours early. Applies to the next **Activate target**, like the two above. Needs the car's SOC and `Ev:Vehicles:0:BatteryCapacityKWh` to find the rest point; without both, the press logs a warning and charges with nothing held. |
| **Target rest SOC** | % | Where the car waits under `JustInTime` before the final stretch is released. Ignored under `Cheapest`. |
| **Target sun only** | switch | Nothing bought from the grid: what the sun can't deliver by the departure is reported as **Target shortfall** rather than imported. Off by default. Applies to the next **Activate target**, like the others, and doesn't persist across restarts. Overrides `JustInTime`, which with no grid can only lose sun — the press charges as `Cheapest` and logs a warning. Shows a running request's setting, wherever it was started. |
| **Target low tariff only** | switch | The car waits through high tariff, sun or not, as with **Fast low tariff only**, and the plan counts only on low-tariff hours: what doesn't fit is reported as **Target shortfall**. Off by default. Applies to the next **Activate target** and doesn't persist across restarts. With **Target sun only** also on, sun only wins. Ignored while no tariff is set up. Shows a running request's setting, wherever it was started. |
| **Activate target** | button | Applies the ones above: sets the request, then starts the `Targeted` mode — which writes the charger's use-mode `Fast`, like every other way of starting charging. Pressed with energy or departure missing, a departure already past, or one beyond `ChargeControl:Targeted:MaxHorizon`, it logs a warning and does nothing. |
| **Target plan state** | — | One line on what the plan is doing and why — the same explanation the log carries. |
| **Target forecast surplus** | kWh | All the surplus the window is forecast to hold, after the house and the home battery's booking — whether or not the car can charge on it unaided. Read it against **Target solar energy**: the gap between them is sun the roof will produce but the charger cannot run on by itself. |
| **Target solar energy** | kWh | The share of what is still needed that the roof is expected to supply, after the home battery's own booking — including surplus below the charger's 6 A minimum, which the car can still use while the grid holds it at that minimum. Read it against **Target forecast surplus**. |
| **Target grid energy** | kWh | The share planned to come from the grid, and an **upper bound** rather than a prediction. Rebuilt every poll, so a better day than forecast shrinks it before the rest is bought — and where the import sits over sub-minimum surplus the charger runs at maximum while the roof quietly covers part of it, which this figure does not model. Expect to import less than it says. |
| **Target expected** | kWh | What will actually reach the car by the departure. Equal to what was asked for unless there isn't enough time. |
| **Target shortfall** | kWh | How far **Target expected** falls short of the request. Anything above zero means the departure is too soon for the amount asked for; **Target plan state** names the departure that would have covered it. |
| **Target charge pace** | kW | The average the grid must sustain to keep the promise: what is still needed, less the sun forecast to reach the car, over the time left. The charger runs at this **plus** whatever the roof is giving. Zero means the forecast covers the target outright and nothing will be bought. |
| **Charging from** | — | When the first grid-funded charging starts. `none` while the forecast covers the whole request. |
| **Target hold until** | — | When the held last stretch is released, under `JustInTime`. `none` whenever nothing is being held — which is every `Cheapest` plan, and any `JustInTime` one where holding would have put the departure at risk. |

## The integration's entities

The [integration](#the-gleanvolt-integration) uses the same names as MQTT discovery wherever the
entity is the same, so the meanings above apply. This table lists each integration entity, what it is
read from or what it calls, and where it differs from MQTT.

| Integration entity | Type | From / calls | Differs from MQTT |
| --- | --- | --- | --- |
| **Charge mode** | select | `mode` on `/status`; `POST /charging/start` or `/charging/stop` | Replaces the five **Charge …** buttons, **Charge off** and **Activate target**. A refusal is shown to whoever picked it. |
| **Charge mode** | sensor | `mode` | The same. |
| **Mode to start** | select | held in Home Assistant | New. Which mode **Start** starts: the web UI's tab. Starts nothing by itself. |
| **Start** | button | `POST /charging/start` | New. Starts **Mode to start** with its held parameters. Replaces the five **Charge …** buttons and **Activate target**. |
| **Stop charging** | button | `POST /charging/stop` | **Charge off**. |
| **Control state**, **Charger status**, **Solar power**, **Forecast solar power**, **Solar surplus**, **Grid power**, **Battery power**, **Battery SOC**, **EV charging power**, **EV charging current**, **Target charging current**, **Active charging current**, **Session energy**, **Battery loan power**, **Battery loaned today**, **Tomorrow forecast** | sensor | `/status` | The same. Energies in kWh. Power, energy and SOC carry device and state classes for the energy dashboard. |
| **Car connected**, **Charging now** | binary sensor | `carConnected`, `state` = `charging` | The same. |
| **Battery hold active** | binary sensor | `batteryHold.active` | New: what was last written to the inverter. |
| **Dry run** | binary sensor (diagnostic) | `dryRun` | New. |
| **Battery discharge hold** | switch | `batteryHold.requested`; `PUT /battery-hold` | Unavailable while `BatteryHold:Enabled` is false (the API answers 409). |
| **Fast low tariff only**, **SolarGrid low tariff only** | switch | `PUT /charging/low-tariff-only` | The same. |
| **Forecasted until full** | switch | `PUT /charging/until-full` | The same. |
| **Fast basis**, **Fast energy**, **Fast target SOC** | select, number | held in Home Assistant | The same meaning. Restored across a Home Assistant restart. Sent when `FastNoBattery` is started by **Start** or **Charge mode**. |
| **Fast departure** | datetime | held | A real date-time instead of text. It can't be emptied, so a time already past means *charge now*. |
| **Target basis** | select | held | New: `Energy` sends **Target energy**, `Soc` sends **Target SOC**. |
| **Target energy**, **Target SOC**, **Charge priority**, **Target rest SOC**, **Target sun only**, **Target low tariff only** | number, select, switch | held | The same meaning, sent when **Charge mode** starts `Targeted`. Sun only is `editable.maxGridEnergyWh: 0`, and low tariff only is `editable.lowTariffOnly`. **Target SOC** is new. |
| **Departure** | datetime | held | **Departure time**, as a date-time. |
| **Plan state**, **EV energy budget**, **Projected shortfall**, **Surplus spill**, **Loan headroom**, **Required SOC floor**, **Trajectory SOC floor**, **Forecast remaining today**, **Forecast accuracy** | sensor | `dayPlan` | The same; `unknown` outside `Forecasted`. **Charge window** isn't in the API. |
| **Target plan state**, **Target forecast surplus**, **Target solar energy**, **Target grid energy**, **Target expected**, **Target shortfall**, **Target charge pace**, **Charging from**, **Target hold until** | sensor | `targetedPlan` | The same; `unknown` outside `Targeted`. **Charging from** and **Target hold until** are timestamps. |
| **Fast delivered**, **Fast target**, **Fast start** | sensor | `fastCharge` | The same. **Fast start** is a timestamp. |
| **Car SOC**, **Car range**, **Car reading age** | sensor | `/vehicle` | New. Range in km. The reading's age is the time the car reported it, shown as "… ago". |
| **Car reading stale** | binary sensor | `stale` on `/vehicle` | New: past `Vehicle:MaxAge`. |
| **Low tariff**, **Tariff change**, **Grid price** | binary sensor, sensor | `/tariff` | The same. **Grid price** is the energy dashboard's *current price* entity. |
| **Last poll age**, **Forecast available**, **Version** | diagnostic | `/health` | New. |

Not in the integration: **Minimum battery SOC**, **SOC resume margin**, **Min solar surplus** and **Sun
until** (not in the 1.2 API), **Car feed**, **Battery hold target**, **Charge window** and **Stop
service**.

The device also carries the running build as its **software version** (`1.0.0 (31bf347)`), shown on
HA's device page. That is device metadata rather than an entity, so it creates no row on any
dashboard and needs no automation. The same string is the first line of the worker's log at startup;
a version ending in `-dev` — `1.0.0-dev` — with no commit means a local build rather than anything
CI published.

An availability topic tells Home Assistant to mark the device unavailable while the controller is
stopped.
