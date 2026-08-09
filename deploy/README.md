# Deploying to a Raspberry Pi

The production stack for [issue #26](https://github.com/mpospisil/solax-controller/issues/26): the
controller, Home Assistant, and an MQTT broker as three Docker containers on a **Raspberry Pi 3 B**
running Raspberry Pi OS Lite (64-bit).

```
                 Raspberry Pi 3 B  (192.168.2.7, arm64)
    ┌───────────────────────────────────────────────────────────────┐
    │  compose project "solax"          (all state on bind mounts)  │
    │                                                               │
    │   solax-controller ──MQTT──▶ mosquitto ◀──MQTT── homeassistant│
    │          │                   (no host port)          │ :8123  │
    └──────────┼──────────────────────────────────────────┼─────────┘
               ▼ Modbus TCP                               ▼
    inverter 192.168.2.6:502                        LAN browsers
    charger  192.168.2.10:502
```

The Pi never builds anything. CI builds a `linux/arm64` image and pushes it to GHCR; the Pi pulls it.

> **Not the dev stack.** `dev/homeassistant/` is a separate, anonymous-broker environment for
> developing against `dotnet run`. Don't point one at the other; running both at once against the
> same inverter is confusing at best.

## Prepare the Pi (once)

**1. Passwordless SSH.** `deploy.sh` opens about eight separate SSH connections — with password
authentication you would be prompted for every one of them, and a deploy stops being a single
command. From your **developer machine**:

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

...and then `PI_HOST=solax-pi ./deploy/deploy.sh`.

**2. Docker.**

```bash
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker "$USER"        # log out and back in for this to take effect
sudo systemctl enable --now docker     # survive a reboot
docker compose version                 # v2 plugin, included by the script above
```

**3. Enable cgroup memory accounting.** Raspberry Pi OS ships with it off, and without it the
`mem_limit` settings in `docker-compose.yml` are silently ignored — which on a 1 GB board is the
difference between one container being killed and the whole box thrashing. Append to the **single
line** in `/boot/firmware/cmdline.txt`, then reboot:

```
cgroup_enable=memory cgroup_memory=1
```

Verify after the reboot with `docker info | grep -i "memory limit"` — no warning means it worked.

**4. Add swap.** 1 GB of RAM with no swap turns a transient spike into an OOM kill:

```bash
sudo dphys-swapfile swapoff
sudo sed -i 's/^CONF_SWAPSIZE=.*/CONF_SWAPSIZE=1024/' /etc/dphys-swapfile
sudo dphys-swapfile setup && sudo dphys-swapfile swapon
free -h
```

**5. Directories.** The containers hold no state; everything lives here:

```bash
sudo mkdir -p /opt/solax/{mosquitto/config,mosquitto/data,homeassistant/config,logs,data}
sudo chown -R "$USER" /opt/solax
sudo chown -R 1883:1883 /opt/solax/mosquitto        # the eclipse-mosquitto uid
sudo chown -R 1654:1654 /opt/solax/logs /opt/solax/data   # the controller image's non-root uid
```

`data/` holds the charging-session SQLite database. SQLite writes its `-wal` and `-shm` files next to
the database, so that **directory** — not just the file — has to be writable by uid 1654.

`logs/` and `data/` are the two the *application* writes to, and `deploy.sh` creates them itself (and
hands them to uid 1654) if they are missing or wrongly owned — which is what makes a release that
adds one, as `data/` did, deploy onto an existing Pi without manual work. It does that through a
throwaway `alpine` container, because the SSH user cannot chown to another uid but the Docker daemon
can; the image is pulled once, on the first deploy that needs it. Listing them above is still worth
doing on a fresh Pi, so the whole tree exists before anything runs.

**6. Secrets.** From your developer machine:

```bash
scp deploy/.env.example martin@192.168.2.7:/opt/solax/.env
ssh martin@192.168.2.7 'chmod 600 /opt/solax/.env && nano /opt/solax/.env'
```

**7. Broker credentials.** The broker refuses anonymous connections, so this must exist before the
stack will work. The username has to match `MQTT_USERNAME` in `.env`:

```bash
docker run --rm -v /opt/solax/mosquitto/config:/mosquitto/config eclipse-mosquitto:2 \
    mosquitto_passwd -c -b /mosquitto/config/passwd solax '<password>'
sudo chown 1883:1883 /opt/solax/mosquitto/config/passwd
```

**8. GHCR access.** Only needed if the package is private — a public package needs no login:

```bash
echo '<github-pat-with-read:packages>' | docker login ghcr.io -u mpospisil --password-stdin
```

**9. Check the devices are reachable** from the Pi, before blaming the container:

```bash
nc -vz 192.168.2.6 502 && nc -vz 192.168.2.10 502
```

## Deploy

This directory mirrors `/opt/solax` on the Pi, so what you edit here is what lands there:

```
deploy/
├── docker-compose.yml              → /opt/solax/docker-compose.yml
├── mosquitto/config/mosquitto.conf → /opt/solax/mosquitto/config/    (overwritten each deploy)
├── homeassistant/config/*.yaml     → /opt/solax/homeassistant/config/ (seeded once, never overwritten)
├── .env.example                    → copied by hand, once, as /opt/solax/.env
└── deploy.sh
```

From a developer machine, with the repo checked out:

```bash
./deploy/deploy.sh
```

It copies `docker-compose.yml` and `mosquitto.conf` to `/opt/solax`, seeds Home Assistant's config
files only if they don't already exist, then pulls and restarts. It creates `logs/` and `data/` and
hands them to uid 1654 if they are missing or wrongly owned; for anything else it refuses to run
rather than guess. It never copies `.env`.

| Variable | Default | |
|---|---|---|
| `PI_HOST` | `martin@192.168.2.7` | ssh target |
| `REMOTE_DIR` | `/opt/solax` | stack location on the Pi |
| `IMAGE_TAG` | from `.env` (`latest`) | which build to run |

## First run

1. Open `http://192.168.2.7:8123` and complete Home Assistant onboarding (local account).
2. **Settings → Devices & Services → Add Integration → MQTT.** Broker `mosquitto`, port `1883`, and
   **the username and password from `.env`** — unlike the dev stack, this broker is authenticated.
3. The controller publishes MQTT discovery configs on connect; the SolaX device and its entities
   appear by themselves.

Deploying writes nothing to your hardware. Charge control boots in mode **Off** and takes control
only once you select a mode in Home Assistant, and `BatteryHold` is disabled and dry-run. Change
either in `.env` only after verifying the register addresses on your own device, per the root
README's warnings.

## Everyday operations

```bash
ssh martin@192.168.2.7
cd /opt/solax

docker compose ps                          # what's running
docker compose logs -f solax-controller    # follow the poll loop
docker compose restart solax-controller
docker stats --no-stream                   # memory headroom -- the number that matters here
```

Upgrade to the latest build, or roll back to a known-good one:

```bash
./deploy/deploy.sh                              # latest from main
IMAGE_TAG=sha-abc1234 ./deploy/deploy.sh        # a specific build
```

Both preserve all state. So does `docker compose down`, and so does `docker rm -f` on any single
container — that is the point of the layout below.

## Where the logs go

**Every log lands on the Pi's drive; nothing is written inside a container.** Verified with
`docker diff`, which stays empty on all three services during normal operation.

| Who | Written to | Retention |
|---|---|---|
| Controller (Serilog file sink) | `/opt/solax/logs/solax-<date>.log` — bind mount over `/app/logs` | 14 daily files (`retainedFileCountLimit`) |
| Controller / broker / HA (stdout) | Docker's `json-file` logs, `/var/lib/docker/containers/...` on the Pi | capped at 3 × 10 MB per service (5 MB for the broker) |
| Home Assistant | `/opt/solax/homeassistant/config/home-assistant.log` — bind mount over `/config` | HA rotates it itself |
| Mosquitto | stdout only (`log_dest stdout`) — no second file on the card | as above |

Check it after a deploy — a file should appear within one poll cycle:

```bash
ls -l /opt/solax/logs/
```

> **The one way this breaks is silent.** If `/opt/solax/logs` isn't writable by uid 1654 (the
> image's non-root user — most easily caused by letting Docker auto-create the directory as root),
> Serilog's file sink fails and *keeps running*: the container is healthy, `docker logs` looks
> normal, and the log files simply never appear. Two things guard against it: `deploy.sh` refuses to
> creates it (and fixes its ownership) before deploying, and the worker enables Serilog's `SelfLog` so the
> failure shows up in `docker logs` as `RollingFileSink: the target file could not be opened or
> created`. If you see that line, fix the ownership:
>
> ```bash
> sudo chown -R 1654:1654 /opt/solax/logs
> ```
>
> `/opt/solax/data` has the same requirement and the same `deploy.sh` guard. It fails less quietly —
> the session worker logs an error and then records nothing for the rest of the run — but the result
> is the same: a stack that looks healthy while quietly keeping no history.

## Where the data lives

Nothing that matters is inside a container. Every path is a bind mount under `/opt/solax`:

| Host path | In the container | What it is | Back up? |
|---|---|---|---|
| `/opt/solax/.env` | (environment) | secrets, `chmod 600` | yes |
| `/opt/solax/data` | `/app/data` | `sessions.db` — the charging-session history | **critical** |
| `/opt/solax/homeassistant/config` | `/config` | HA `.storage` (account, entity registry, MQTT integration) + recorder DB | **critical** |
| `/opt/solax/mosquitto/config` | `/mosquitto/config` | `mosquitto.conf`, password file | yes |
| `/opt/solax/mosquitto/data` | `/mosquitto/data` | retained messages, sessions | no |
| `/opt/solax/logs` | `/app/logs` | controller log files | no |
| `/opt/solax/docker-compose.yml` | — | redeployed from git | no |

**Back up** — two directories are irreplaceable, for different reasons. `homeassistant/config/.storage`
costs you onboarding, the account and the MQTT integration; `data/sessions.db` is charging history
that **cannot be regenerated** — telemetry can be re-polled, a session that already happened cannot be
re-lived. Stop the stack first so SQLite isn't mid-write:

```bash
cd /opt/solax && docker compose stop solax-controller
sudo tar czf "solax-backup-$(date +%F).tar.gz" -C /opt/solax .env data homeassistant/config mosquitto/config
docker compose start solax-controller
```

Backing it up hot mostly works — WAL journalling makes a torn copy unlikely rather than impossible —
but a stopped writer makes it certain, and the gap costs one poll cycle.

**Restore** onto a prepared Pi:

```bash
cd /opt/solax && docker compose down
sudo tar xzf solax-backup-2026-08-09.tar.gz -C /opt/solax
sudo chown -R 1883:1883 /opt/solax/mosquitto
sudo chown -R 1654:1654 /opt/solax/data          # tar restores the archive's ownership
docker compose up -d
```

One thing the controller deliberately doesn't persist: the **Solcast forecast cache is in-memory**,
so every restart re-fetches and spends one call from the daily quota. Harmless normally — but a
container stuck in a restart loop will burn through the free tier, which is a reason to watch
`docker compose ps` restart counts rather than trusting `unless-stopped` to paper over a failure.

## Troubleshooting

**A container is `Restarting` in a loop.** `docker compose logs <service>`. If there's nothing but
an abrupt stop, suspect memory: `dmesg -T | grep -i oom`.

**Home Assistant is killed during startup.** It's the hungriest of the three. Raise `HA_MEM_LIMIT`
in `.env`, confirm swap is on, and trim the recorder further in
`/opt/solax/homeassistant/config/configuration.yaml`. If it can't be made to fit alongside the other
two, the intended fallback is to move Home Assistant to another host — the three services are
independent, so that's a compose edit, not a redesign.

**Nothing connects to the broker.** `docker compose logs mosquitto` shows the rejected connections.
Almost always the password file and `MQTT_USERNAME`/`MQTT_PASSWORD` disagreeing, or the file not
being readable by uid 1883.

**No charging sessions are being recorded.** Look for this at startup:

```
[ERR] Could not open the charging session store; sessions will not be recorded this run.
      SQLite Error 14: 'unable to open database file'
```

That is `/opt/solax/data` not being writable by uid 1654. Everything else keeps running, which is why
it is easy to miss. Re-running the deploy repairs the ownership on its own:

```bash
./deploy/deploy.sh
```

Or, on the Pi directly:

```bash
sudo chown -R 1654:1654 /opt/solax/data && docker compose restart solax-controller
```

**The controller logs Modbus timeouts.** Check reachability from the Pi itself (`nc -vz`, step 8).
Bridge networking routes through the host, so if the Pi can reach the inverter, the container can.

**`docker compose` says permission denied.** The ssh user isn't in the `docker` group yet, or hasn't
logged out and back in since being added.

**It asks for a password (repeatedly).** SSH key authentication isn't set up — step 1. `deploy.sh`
makes roughly eight connections, so this is unusable without a key:

```bash
ssh-copy-id martin@192.168.2.7
```

If the account isn't `martin`, pass your own: `PI_HOST=<user>@192.168.2.7 ./deploy/deploy.sh`.

**Locked out over SSH.** <https://connect.raspberrypi.com/> gives you a shell without the LAN.
