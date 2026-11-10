# Deploying to a Raspberry Pi from your computer

> **Most people don't need this page.** The recommended way to run Gleanvolt on a Raspberry Pi is a
> clean install on the Pi itself — one command, see [Install on Linux](../packaging/linux/README.md) —
> and then the setup wizard in your browser. On a NAS, see [Running on a NAS](../docs/NAS.md).

This page is the **advanced** route: Gleanvolt as a Docker container on a Raspberry Pi that you deploy,
update and roll back **from another computer**, over ssh, with the scripts in this `deploy/` directory.
It is how the project's own reference Pi runs, and the route for running Home Assistant and an MQTT
broker beside Gleanvolt on the same Pi.

You need:

- a Raspberry Pi with Raspberry Pi OS Lite (64-bit);
- a computer — Linux, macOS, or Windows with WSL — with this repository checked out, and ssh access to
  the Pi.

Every command below runs on **your computer** unless it says *on the Pi*. The scripts connect to the
Pi over ssh, copy the compose files to `/opt/gleanvolt`, and start the container there; the Pi pulls
the image itself.

```
  your computer (this repository)
    │  ./deploy/deploy-controller-only.sh  ── ssh ──┐
                                                    ▼
              Raspberry Pi  (192.168.2.7, arm64)
    ┌────────────────────────────────────────────────┐
    │ /opt/gleanvolt                                 │
    │   gleanvolt-controller   (state on bind mounts,│
    │                           host network)        │
    │      └── :8090, the web UI                     │
    └────────────────────────────────────────────────┘
         │ Modbus TCP
         ▼
  inverter <INVERTER_HOST>:502
  charger  <EV_CHARGER_HOST>:502
```

After the first deploy, everything else is set up in the web UI's **setup wizard**, as on any other
install — see [Setting it up](#setting-it-up).

## What you deploy

One container, `gleanvolt-controller`. It uses about 75 MB of memory and needs 8 GB of disk, so any
64-bit Pi runs it.

> **Home Assistant?** Don't run the Home Assistant container on a low-powered Pi (less than 2 GB of
> RAM). To connect Gleanvolt to a Home Assistant you already run, or to run Home Assistant and a broker
> beside it on a bigger Pi with `./deploy/deploy.sh`, see
> [Gleanvolt with Home Assistant](../docs/HOME_ASSISTANT.md).

## Storage

Nothing here depends on a particular disk. 8 GB is enough. It grows slowly afterwards — the
charging-session and energy databases and the log files are the only things that accumulate, and all
are small.

## Prepare the Pi (once)

**1. Passwordless SSH.** The deploy script opens about a dozen SSH connections, so set up a key. From
your own computer:

```bash
ssh-keygen -t ed25519 -C solax-deploy    # only if you don't already have a key
ssh-copy-id martin@192.168.2.7           # asks for the Pi password once, and never again
ssh martin@192.168.2.7 true              # must return silently, with no prompt
```

The scripts default to the `martin@192.168.2.7` account. For a different user or host, either set
`PI_HOST` (see the table under [Deploy](#deploy)) or give the Pi a `~/.ssh/config` entry:

```
Host solax-pi
    HostName 192.168.2.7
    User martin
```

...and then `PI_HOST=solax-pi ./deploy/deploy-controller-only.sh`.

**2. Docker.**

```bash
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker "$USER"        # log out and back in for this to take effect
sudo systemctl enable --now docker     # survive a reboot
docker compose version                 # v2 plugin, included by the script above
```

**3. Enable cgroup memory accounting.** Without it Docker ignores the container's memory limit. The
parameters go at the end of the **single line** in `/boot/firmware/cmdline.txt` (`/boot/cmdline.txt`
on Bullseye and older); a second line is ignored. This appends them if they are missing:

```bash
sudo cp /boot/firmware/cmdline.txt /boot/firmware/cmdline.txt.bak

sudo sh -c '
  line=$(tr "\n" " " < /boot/firmware/cmdline.txt | tr -s " " | sed "s/[[:space:]]*$//")
  case $line in
    *cgroup_enable=memory*) ;;
    *) line="$line cgroup_enable=memory cgroup_memory=1" ;;
  esac
  printf "%s\n" "$line" > /boot/firmware/cmdline.txt
'

wc -l /boot/firmware/cmdline.txt      # must print 1
cat  /boot/firmware/cmdline.txt       # check it before rebooting
sudo reboot
```

Check the file before rebooting: a broken `cmdline.txt` means a Pi that doesn't boot, and
`cmdline.txt.bak` is the way back. After the reboot:

```bash
cat /proc/cmdline | tr ' ' '\n' | grep cgroup   # expect cgroup_enable=memory and cgroup_memory=1
docker info 2>&1 | grep -i "memory limit"       # no "No memory limit support" warning
```

`cgroup_disable=memory` also appears in `/proc/cmdline`; the firmware adds it, and the parameter you
added comes later and wins. `WARNING: No swap limit support` is expected; ignore it.

**4. Directories.** The container holds no state; everything lives here:

```bash
sudo mkdir -p /opt/gleanvolt/{logs,data}
sudo chown -R "$USER" /opt/gleanvolt
sudo chown -R 1654:1654 /opt/gleanvolt/logs /opt/gleanvolt/data   # the controller image's non-root uid
```

The deploy script also creates both and fixes their ownership if needed.

**5. The settings file.** The deploy script needs `/opt/gleanvolt/.env` to exist. Copy the example
from your computer; editing it is optional, since the setup wizard asks for everything:

```bash
scp deploy/.env.example martin@192.168.2.7:/opt/gleanvolt/.env
ssh martin@192.168.2.7 'chmod 600 /opt/gleanvolt/.env'
```

**6. Check the devices are reachable** from the Pi, before blaming the container:

```bash
nc -vz <inverter address> 502 && nc -vz <charger address> 502
```

## Deploy

This directory mirrors `/opt/gleanvolt` on the Pi, so what you edit here is what lands there:

```
deploy/
├── docker-compose.yml              → /opt/gleanvolt/docker-compose.yml
├── mosquitto/config/mosquitto.conf → /opt/gleanvolt/mosquitto/config/    (overwritten each deploy)
├── homeassistant/config/*.yaml     → /opt/gleanvolt/homeassistant/config/ (seeded once, never overwritten)
├── .env.example                    → copied by hand, once, as /opt/gleanvolt/.env
├── deploy-controller-only.sh       # the controller, with its web UI on :8090
├── deploy.sh                       # the same, plus Mosquitto and Home Assistant (docs/HOME_ASSISTANT.md)
└── _lib.sh                         # shared by both scripts above; not run directly
```

From a developer machine, with the repo checked out:

```bash
./deploy/deploy-controller-only.sh
```

It copies the compose files to `/opt/gleanvolt`, then pulls and restarts. It creates `logs/` and
`data/` and hands them to uid 1654 if they are missing or wrongly owned; for anything else it refuses
to run rather than guess. It never copies `.env`.

| Variable | Default | |
|---|---|---|
| `PI_HOST` | `martin@192.168.2.7` | ssh target |
| `REMOTE_DIR` | `/opt/gleanvolt` | stack location on the Pi |
| `IMAGE_TAG` | from `.env` (`latest`) | which build to run |

`deploy.sh` takes the same three.

## Setting it up

After the deploy, open **`http://192.168.2.7:8090`** (your Pi's address). The first page asks you to
choose a password; then the **setup wizard** finds the inverter and the charger on your network and
asks for the site — location, time zone, home battery — and, optionally, the Solcast forecast, the
car, the grid tariff, weather, Home Assistant and the HTTP API. **This is the recommended way**: every
value is checked as you save it, and nothing needs editing on the Pi. See
[The setup wizard](../docs/WEB_UI.md#the-setup-wizard).

Later changes are made the same way, on **Settings** and **Car**, and a **Restart** on
**Settings → Health** applies them. They are kept in `/opt/gleanvolt/data/pv-system.json` and the
secret store, so they survive updates.

Nothing is written to your hardware yet: charging is **off** until a charging button is pressed, and
the battery hold is disabled and in dry-run. Check the readings on the Overview against your devices'
own displays first. Every page is described in [Web UI](../docs/WEB_UI.md).

**Forgot the password?** Use **Forgot your password?** on the sign-in page, or reset it from the Pi,
which prints a new recovery key:

```bash
cd /opt/gleanvolt
docker compose run --rm --no-deps gleanvolt-controller set-password
docker compose restart gleanvolt-controller
```

### Optional: setting it in `.env` instead

Everything the wizard sets can also be put in `/opt/gleanvolt/.env` ahead of time, for example to
prepare an installation without a browser. The wizard then has less to ask. What is saved in the web
UI **wins over `.env`**, key by key. [`.env.example`](.env.example) lists every variable.

```bash
TZ=Europe/Prague               # your time zone
INVERTER_HOST=192.168.1.10     # your inverter's address (an example; there is no default)
EV_CHARGER_HOST=192.168.1.11   # your charger's address
```

The rest of the installation:

```bash
PV_ID=home-roof                       # a slug: this system's id, and its MQTT topic segment
PV_NAME=Home Roof                     # what a human sees, in the web UI
PV_ADDRESS=Street 1, Town, Country    # display only, never parsed
PV_LATITUDE=49.267803                 # with PV_LONGITUDE: the site the weather is fetched for
PV_LONGITUDE=16.529486
PV_AZIMUTH=180                        # compass bearing: 0 north, 90 east, 180 south. -180 accepted
PV_TILT=15                            # degrees from horizontal
PV_CAPACITY_KWP=8.5                   # peak DC capacity of the array
PV_INVERTER_CAPACITY_KW=8             # AC side, where it is smaller than the array
PV_LOSS_FACTOR=0.9                    # fraction of DC yield that reaches the meter
PV_INSTALL_DATE=2026-04-01

PV_INVERTER_MODEL=SolaX X3-HYB-G4 PRO # what the box is; documentation, not a selector
PV_CHARGER_MODEL=SolaX X1/X3-HAC
```


Keys and switches, each of which the wizard's **Forecast** and **Extras** steps also cover:

```bash
SOLCAST_API_KEY=...            # the solar forecast
SOLCAST_RESOURCE_ID=...
WEATHER_API_KEY=...            # weather at each end of a session
API_ENABLED=true               # the HTTP API, with one key (generate one with: openssl rand -hex 32)
API_KEY=...                    # becomes Api__Keys__client
```

The web UI password can be set ahead of time too, as a hash — never the password itself:

```bash
docker run --rm ghcr.io/mpospisil/gleanvolt:latest hash-password '<your password>'
# then in .env:  WEB_PASSWORD_HASH=<the output>
```

`.env` is read at startup, so after editing it run `docker compose up -d` on the Pi, or deploy again.

## Updating a running deployment

**Updating is the same command as deploying.** Run the script again from your developer machine;
there is no separate update path, nothing to uninstall, and no step you perform on the Pi.

```bash
./deploy/deploy-controller-only.sh
```

If you deployed Home Assistant beside it with `deploy.sh`, run that one instead: running the other
script adds or removes containers, which is not what you want if you only meant to pick up a new
build.

### What the script does, in order

1. Checks `/opt/gleanvolt` exists and `.env` is there — it refuses rather than guessing.
2. Copies `docker-compose.yml` from your **local** `deploy/` directory, overwriting the Pi's copy
   (with `deploy.sh`, `mosquitto.conf` too).
3. With `deploy.sh` only, seeds Home Assistant's config files if they don't already exist.
4. `docker compose pull`.
5. `docker compose up -d --remove-orphans` — recreates only what actually changed.
6. Prints the container status and the URLs.

### What survives an update

Everything that is state, because none of it lives inside a container:

| | |
|---|---|
| `/opt/gleanvolt/.env` | **never copied, never overwritten** — the deploy scripts do not touch secrets |
| `data/sessions.db` | charging-session history, with its SQLite WAL |
| `data/energy.db` | the site's 15-minute energy history, with its SQLite WAL |
| `data/pv-system.json`, `data/tariff.json` | what was saved on **Settings**: the installation's edits, and the [grid tariff](../docs/CONFIGURATION.md#grid-tariff-the-tariff-section) |
| `data/vw-website-session.json`, `data/skoda-api-key.json` | **the car's sign-in.** Bearer-equivalent, mode `0600` — see below |
| `logs/` | the controller's own log files |

`docker compose down` and even `docker rm -f` on any single container are equally safe, for the same
reason. What *is* overwritten every deploy is the compose files — so edit those
in the repo, not on the Pi, or your change disappears at the next update.

**`data/` holds secrets**: the car's sign-in and the keys entered in the web UI, protected by file
permissions, not encryption. Copy it the way you would copy `.env`: onto an encrypted volume, and
nowhere public. See [The data directory holds secrets](../docs/CONFIGURATION.md#the-data-directory-holds-secrets-the-secrets-section).

### The controller restarts, so charge control returns to Off

An update restarts the controller, which always starts in charge mode **Off** with the battery hold
off. **A mode active before the update is not active after it**: start charging again. The charger is
left as it was. A session in progress is closed as `ServiceStopped`, and charging after the restart is
a new session.

### Check what you are actually running

```bash
ssh martin@192.168.2.7 'cd /opt/gleanvolt && docker compose logs gleanvolt-controller | grep "starting\."'
```

The controller logs its version and commit at startup (`Gleanvolt 1.1.0 (31bf347) starting.`); **Settings
→ Health** and Home Assistant's device page show the same.

### Pinning a version, and rolling back

`IMAGE_TAG` selects the build, and works identically on both scripts:

```bash
./deploy/deploy-controller-only.sh                         # latest from main
IMAGE_TAG=1.0.0 ./deploy/deploy-controller-only.sh         # a released version -- no "v"
IMAGE_TAG=sha-abc1234 ./deploy/deploy-controller-only.sh   # one specific build, immutable
```

A rollback is just an update pointed at an older tag; `sha-` tags are immutable, which makes them the
reliable thing to roll back *to*. Setting `IMAGE_TAG` in `/opt/gleanvolt/.env` pins it for every future
deploy that doesn't override it on the command line.

**The image tag has no `v`**, though the git tag does: `IMAGE_TAG=1.0.0`. `IMAGE_TAG=v1.0.0` fails with
`manifest unknown`.

**Deploy from a checked-out tag**, since the script copies your *local* `deploy/` files to the Pi:

```bash
git switch --detach v1.0.0 && IMAGE_TAG=1.0.0 ./deploy/deploy-controller-only.sh
```

### Changing settings rather than code

Change them in the web UI, on **Settings** or **Car**, and press **Restart** on **Settings → Health**.

A setting kept in `.env` instead is changed on the Pi; the deploy scripts never copy `.env`:

```bash
ssh martin@192.168.2.7 'nano /opt/gleanvolt/.env'
ssh martin@192.168.2.7 'cd /opt/gleanvolt && docker compose up -d'
```

Remember that a value saved in the web UI wins over `.env`.

### If an update goes wrong

The scripts stop at the first failure rather than pressing on, and every step before the pull is
either a check or an idempotent repair — creating `logs/` and `data/` with the right owner (and, with `deploy.sh`,
fixing `mosquitto/config` ownership). Re-running after fixing whatever it complained about is safe.

If a new build misbehaves, roll back to the previous tag with the same command. The database, logs
and configuration are all still there, because the container was never where they lived.

## Upgrading from an older release

### Upgrading to the host network

Since [issue #268](https://github.com/mpospisil/gleanvolt/issues/268) all three services run on the
Pi's own network instead of a compose network. Redeploying is the whole upgrade, with nothing to change
in `.env` or in Home Assistant:

- **Home Assistant's MQTT integration** still says broker `mosquitto`. That name used to be resolved by
  the compose network; the compose file now maps it to this Pi, so it keeps working. Changing it to
  `localhost` (Settings → Devices & Services → MQTT → Reconfigure) is tidier and optional.
- **The controller** dials `localhost`, or `MQTT_BROKER_HOST` when set. An `.env` with
  `VEHICLE_BROKER_HOST=mosquitto` would break the car's feed, because the controller has no such name;
  delete that line.
- **The broker is now on the Pi's `:1883`**, reachable from the LAN. It always required a password, so
  this exposes nothing that was open; it is also what lets another device use it. If the Pi already ran
  a second broker on 1883, see [Troubleshooting](#troubleshooting).
- **The `solax_default` network** the old layout used may be left behind, unused and harmless;
  `docker network rm solax_default` clears it.

## Stopping and starting the controller

### Stopping it

Ask the service to stop; don't kill it. There are three ways, and they do the same thing:

| From | How |
|---|---|
| The web UI | **Health** page → **Stop service**, then confirm |
| Home Assistant | the device's **Stop service** button (in the device's *Configuration* section) |
| A shell on the Pi | `docker compose stop gleanvolt-controller` |

All three run the host's graceful shutdown: the charger's setpoint is returned to the pause current,
the open charging session is closed and written as `ServiceStopped` rather than left to be recovered
as interrupted, the session store is flushed, and the Modbus and MQTT connections are closed (Home
Assistant sees the device go unavailable).

**Killing the process instead — `docker kill`, `docker rm -f`, pulling the plug — skips all of it**,
and nothing pauses the charger: the car keeps drawing at the last current written.

### Starting it again

A stop from the UI or Home Assistant stays stopped. Starting it again needs a shell on the Pi:

```bash
ssh martin@192.168.2.7
cd /opt/gleanvolt

docker compose start gleanvolt-controller      # start the container that is already there
docker compose ps                          # confirm: State should be "running"
docker compose logs -f gleanvolt-controller    # watch it come up
```

`docker compose up -d` works too, and is what a deploy runs anyway — so a deploy also starts a
service that was stopped this way. Either way the controller comes back in charge mode **Off** with
the battery hold disabled, like any other restart.

> **Installed from the .deb instead of Docker?** The same contract holds under systemd
> (`Restart=on-failure`), and the commands are `sudo systemctl start gleanvolt`,
> `systemctl status gleanvolt` and `journalctl -u gleanvolt -f`. A stop from the UI stays stopped
> across an upgrade of the package too. See [packaging/linux/README.md](../packaging/linux/README.md).

### Restarting it from the web UI

**Settings → Health → Restart** (or **Restart** in the *Saved, not applied* banner) stops the controller
gracefully and Docker starts it again within seconds, reading its configuration afresh, in mode
**Off**. The page reloads by itself. Each restart spends one Solcast call. A `docker run` without a
restart policy stays down.

### Settings saved from the web UI

What is saved on **Settings** and **Car** is kept in `/opt/gleanvolt/data/pv-system.json`, which
**overrides `.env` key by key**. If an `.env` edit seems to be ignored, the startup log names the keys
the file overrode:

```
PV system: …; overridden by /app/data/pv-system.json (saved from the web UI): Pv:Inverter:Host.
```

Use **Revert** on the page to take a key out. Passwords typed in the web UI go to the
[secret store](../docs/CONFIGURATION.md#the-data-directory-holds-secrets-the-secrets-section) instead.

### How long a stop takes

Usually a second or two; longer when a device has stopped answering. Two things keep it from becoming
a killed process:

- `stop_grace_period: 60s` in `docker-compose.yml` — how long Docker waits before SIGKILL.
- A 10-second deadline on the charger-release itself. If the charger isn't answering, the service
  gives up and says so rather than spending the whole grace period on it:

  ```
  [WRN] Gave up pausing the charger on shutdown after 00:00:10 — it is not answering. It may
        still be charging under our last setpoint until something else changes it.
  ```

  **That line means a car may still be drawing.** The charger keeps the last current it was given, so
  check it at the charger or in its own app.

### Reading it back from the log

Every run that ends properly says so on its last line, whichever way it ended:

```
Gleanvolt stopped cleanly at the request of Web UI. Exiting with code 0: it will
NOT be restarted, and stays down until it is started again.

Gleanvolt stopped cleanly after a termination signal. Exiting with code 143:
where a restart policy is watching, it will be started again.

Gleanvolt stopped cleanly for a restart requested by Web UI. Exiting with code 75:
where a restart policy is watching, it will be started again and read its
configuration afresh; where none is, start it again by hand.
```

**A log that ends without one of those lines is a run that died** — killed, out of memory, or a power
cut.

```bash
# how the last few runs ended
grep -h -E "starting\.|stopped cleanly" /opt/gleanvolt/logs/solax-*.log | tail
```

### Why it stays stopped, and why a reboot doesn't

`docker-compose.yml` runs the controller under `restart: on-failure`, and the worker chooses its exit
code deliberately so that policy can tell the two cases apart:

| How the run ended | Exit code | What Docker does |
|---|---|---|
| Somebody pressed **Stop** (UI, HA) | `0` | leaves it down until you start it |
| Somebody pressed **Restart** (UI) — e.g. to apply a `/pv-system` edit | `75` | starts it again |
| `docker compose stop` | — | leaves it down (a manual stop always wins) |
| SIGTERM: Pi reboot, Docker daemon restart, `docker compose restart` | `143` | starts it again |
| Crash, OOM kill, power cut | non-zero | starts it again |

So a power cut, a reboot or a Docker upgrade brings the controller back; only a stop somebody asked
for keeps it down. Don't change it to `restart: unless-stopped` or `always`: **Stop service** would
then restart it at once.

## Everyday operations

```bash
ssh martin@192.168.2.7
cd /opt/gleanvolt

docker compose ps                          # what's running
docker compose logs -f gleanvolt-controller    # follow the poll loop
docker compose restart gleanvolt-controller    # comes back by itself; see "Stopping and starting"
docker compose stop gleanvolt-controller       # stays down until you start it
docker compose start gleanvolt-controller
docker stats --no-stream                   # memory use

# which build is actually running -- version and the commit it came from
docker compose logs gleanvolt-controller | grep "starting\."
```

See [Updating a running deployment](#updating-a-running-deployment) for upgrades, rollbacks and
pinning.

## Which image you get

Everything publishes to one package, `ghcr.io/mpospisil/gleanvolt`, as a multi-platform
manifest list. The Pi pulls arm64 and an x64 host pulls amd64 from the *same* tag — there is nothing
platform-specific to configure, and `IMAGE_TAG` never needs a suffix.

| Tag | What it is |
|---|---|
| `latest` | newest build of `main` — the default in `.env` |
| `1.0.0`, `1.0`, `1` | a released version, from a `v*` git tag |
| `sha-abc1234` | one specific build, immutable |
| `1.0.0-linux-arm64` | that release, Raspberry Pi only |
| `1.0.0-linux-amd64` | that release, x64 Linux only |
| `1.0.0-nanoserver-ltsc2022` | that release, Windows Nano Server only |

Day to day you want the bare name.

If a new release's bare tag isn't there yet, its single-platform tag usually is:
`IMAGE_TAG=1.1.0-linux-arm64`.

### Running on Windows

The Nano Server image runs the same worker, with one difference that will bite silently if it is
missed: **.NET on Windows ignores the `TZ` environment variable.** The `TZ` line in
`docker-compose.yml` does nothing there, the container runs in UTC, and every recorded charging
session is filed against the wrong day. Set the zone explicitly instead, as a **Windows** id —
Nano Server ships no ICU, so IANA ids like `Europe/Prague` cannot be resolved on it:

```powershell
docker run -e Controller__TimeZone="Central Europe Standard Time" `
  ghcr.io/mpospisil/gleanvolt:latest
```

The controller logs a warning at startup if it runs on Windows with the zone unset.

## Where the logs go

**Every log lands on the Pi's drive**; nothing is written inside the container.

| Who | Written to | Retention |
|---|---|---|
| Controller's log files | `/opt/gleanvolt/logs/solax-<date>.log` | 14 daily files |
| Controller's console | `docker compose logs gleanvolt-controller` | capped at 3 × 10 MB |

Check it after a deploy — a file should appear within one poll cycle:

```bash
ls -l /opt/gleanvolt/logs/
```

> **No log files?** `/opt/gleanvolt/logs` isn't writable by uid 1654, the image's user, and
> `docker compose logs` shows `RollingFileSink: the target file could not be opened or created`.
> Re-run the deploy script, or fix it by hand: `sudo chown -R 1654:1654 /opt/gleanvolt/logs`.

## Where the data lives

Nothing that matters is inside a container. Every path is a bind mount under `/opt/gleanvolt`:

| Host path | In the container | What it is | Back up? |
|---|---|---|---|
| `/opt/gleanvolt/.env` | (environment) | secrets, `chmod 600` | yes |
| `/opt/gleanvolt/data` | `/app/data` | `sessions.db` — charging-session history; `energy.db` — the site's 15-minute energy history; `pv-system.json` and `tariff.json` — what was saved on Settings | **critical** |
| `/opt/gleanvolt/logs` | `/app/logs` | controller log files | no |
| `/opt/gleanvolt/docker-compose.yml` | — | redeployed from git | no |

**Back up** `data/` above all: `sessions.db` and `energy.db` **cannot be regenerated**. Stop the
controller first so the databases aren't mid-write:

```bash
cd /opt/gleanvolt && docker compose stop gleanvolt-controller
sudo tar czf "solax-backup-$(date +%F).tar.gz" -C /opt/gleanvolt .env data
docker compose start gleanvolt-controller
```

**Restore** onto a prepared Pi:

```bash
cd /opt/gleanvolt && docker compose down
sudo tar xzf solax-backup-2026-08-09.tar.gz -C /opt/gleanvolt
sudo chown -R 1654:1654 /opt/gleanvolt/data          # tar restores the archive's ownership
docker compose up -d
```

The Solcast forecast is not stored: every restart fetches it again and spends one call from the daily
quota, so a controller stuck in a restart loop uses the quota up.

## Troubleshooting

**A container is `Restarting` in a loop.** `docker compose logs <service>`. If there's nothing but
an abrupt stop, suspect memory: `dmesg -T | grep -i oom`.

**Can't reach the web UI at `:8090`.** Start with what the container says:

```bash
docker compose logs gleanvolt-controller | grep -i "web ui"
```

`Web UI enabled; listening on port 8090, login not required` means the *process* is listening, and
because the controller runs on the host's network that is the Pi's own port: `ss -ltnp | grep 8090`
on the Pi shows it. If the browser still can't connect, look at the Pi's firewall, if it has one.

A crash loop with `address already in use` in the log means something else on the Pi already listens on
that port; on the host's network the port is simply the Pi's, so `ss -ltnp | grep 8090` names the
other program. Move it, or move the UI with
`WEB_PORT` in `.env`.

No `Web UI enabled` line at all means `WEB_ENABLED=false` is set in `.env`. A startup failure instead
means `WEB_REQUIRE_AUTHENTICATION=true` was set with no `WEB_PASSWORD_HASH`; the log says so
explicitly, and the fix is to set a hash or drop the line (see
[Setting it up](#setting-it-up)).

**No charging sessions are being recorded.** Look for this at startup:

```
[ERR] Could not open the charging session store; sessions will not be recorded this run.
      SQLite Error 14: 'unable to open database file'
```

That is `/opt/gleanvolt/data` not being writable by uid 1654. Re-running the deploy script repairs it:

```bash
./deploy/deploy-controller-only.sh    # or deploy.sh, if that is what you deployed with
```

Or, on the Pi directly:

```bash
sudo chown -R 1654:1654 /opt/gleanvolt/data && docker compose restart gleanvolt-controller
```

**The controller logs Modbus timeouts.** Check reachability from the Pi itself (`nc -vz`, step 6).
The controller is on the Pi's own network, so if the Pi can reach the inverter, the controller can.

**`docker compose` says permission denied.** The ssh user isn't in the `docker` group yet, or hasn't
logged out and back in since being added.

**It asks for a password (repeatedly).** SSH key authentication isn't set up — step 1:

```bash
ssh-copy-id martin@192.168.2.7
```

If the account isn't `martin`, pass your own: `PI_HOST=<user>@192.168.2.7 ./deploy/deploy-controller-only.sh`.

**Locked out over SSH.** <https://connect.raspberrypi.com/> gives you a shell without the LAN.
