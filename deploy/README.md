# Deploying to a Raspberry Pi

The production stack for [issue #26](https://github.com/mpospisil/solax-controller/issues/26): the
controller — with its self-hosted web UI — Home Assistant, and an MQTT broker as up to three Docker
containers on a **Raspberry Pi 3 Model B+** running Raspberry Pi OS Lite (64-bit), Debian 13 (Trixie).

Home Assistant and the broker are **opt-in** (issue #51): a fresh Pi can run the controller and its
own UI alone, at roughly a third of the memory the full stack needs. See
[Running without Home Assistant](#running-without-home-assistant-controller--web-ui-only) for the
combinations and their budgets; everything below still applies to whichever of them you choose.

```
              Raspberry Pi 3 Model B+  (192.168.2.7, arm64)
    ┌────────────────────────────────────────────────────────────────────┐
    │ compose project "solax"        (all state on bind mounts)          │
    │                                                                    │
    │ solax-controller ──MQTT──▶ mosquitto ◀──MQTT── homeassistant       │
    │      │             (opt-in profile,     opt-in profile,            │
    │      │              no host port)          LAN :8123)              │
    │      └── LAN :8080, only while Web:Enabled                         │
    └────────────────────────────────────────────────────────────────────┘
         │ Modbus TCP
         ▼
  inverter 192.168.2.6:502
  charger  192.168.2.10:502
```

The Pi never builds anything. CI builds a `linux/arm64` image and pushes it to GHCR; the Pi pulls it.

### Storage layout

The reference install has a **split boot**, and it is not optional on this hardware:

```
  SD card   /dev/mmcblk0p1  →  /boot/firmware   512MB FAT32, ~76MB used
  M.2 NVMe  /dev/sda2       →  /                the whole OS, Docker, /opt/solax
            (USB enclosure)
```

The Pi 3 boot ROM only speaks USB Bulk-Only Transport and allows the device roughly **two seconds**
to enumerate. An NVMe behind a USB bridge — the reference install uses a Realtek RTL9210 — does not
answer in time, so the board will not boot from it however correct the image is. The Linux kernel
drives the same adapter without complaint once it is running. So the SD card boots, and `cmdline.txt`
hands root straight to the M.2.

Two consequences that bite if you forget them:

- **`/boot/firmware` is the SD card.** Every instruction below that edits `cmdline.txt` — the cgroup
  step in particular — is editing the SD, which is correct, because that is the partition the board
  actually boots. The M.2 keeps its own boot partition from the original image; editing *that* one
  changes nothing at all, silently.
- **`/etc/fstab` must point `/boot/firmware` at the SD's PARTUUID**, not at the M.2's leftover boot
  partition. Get this wrong and kernel updates land somewhere that is never booted, and the machine
  breaks at some upgrade weeks later rather than at the moment of the mistake. Note that a first-boot
  resize rewrites the MBR signature and therefore every PARTUUID on the M.2: the firstboot script
  repairs `cmdline.txt` and the root line in `fstab` itself, but it has no idea about the
  `/boot/firmware` line.

None of this applies if you root the Pi on the SD card in the usual way. It is the price of putting
root on fast, durable storage on a board whose boot ROM predates the idea.

> **Not the dev stack.** `dev/homeassistant/` is a separate, anonymous-broker environment for
> developing against `dotnet run`. Don't point one at the other; running both at once against the
> same inverter is confusing at best.

## Running without Home Assistant (controller + web UI only)

Home Assistant and the broker are two more Docker containers competing for the same 1 GB, and on
the reference Pi 3 B+ Home Assistant alone is the binding constraint. If you don't need it — the
controller's own [self-hosted UI](../README.md#self-hosted-web-ui-the-web-section) already shows
telemetry, drives every control, and browses charging-session history and the forecast plan — leave
both off and get most of the board back.

| Deployment | `COMPOSE_PROFILES` | Containers running | `mem_limit` total | of 905 MB |
|---|---|---|---|---|
| Controller only | *(unset)* | `solax-controller` | 200 MB | 22% |
| Controller + web UI | *(unset)*, `Web:Enabled=true` | `solax-controller` | 200 MB | 22% |
| Controller + Home Assistant | `mosquitto,homeassistant` | all three | 848 MB | 94% |
| Everything | `mosquitto,homeassistant`, `Web:Enabled=true` | all three | 848 MB | 94% |

The web UI adds no container and no separate `mem_limit` of its own — it runs inside
`solax-controller`, the same process either way — so turning it on doesn't change the ceiling in
this table, only what's reachable at `:8080`. Home Assistant and the broker are the only lines that
move the number, which is exactly why they're the two gated by `COMPOSE_PROFILES`.

**To run controller-plus-UI only:** in `/opt/solax/.env` (see [.env.example](.env.example)), leave
`COMPOSE_PROFILES` unset and set:

```
WEB_ENABLED=true
COMPOSE_FILE=docker-compose.yml:docker-compose.web.yml
WEB_PASSWORD_HASH=<generate with the image, see .env.example>
```

Then, in [Prepare the Pi](#prepare-the-pi-once) below, step 7 (broker credentials) doesn't apply —
`deploy.sh` only checks for a password file when `COMPOSE_PROFILES` names `mosquitto` — and neither
does the Home Assistant onboarding under [First run](#first-run). You can still create
`mosquitto/config`, `mosquitto/data` and `homeassistant/config` in step 5 if you might turn either on
later, or skip them for now; `deploy.sh` never assumes they exist except when their profile is active.

**To add Home Assistant and the broker later** (or from the start): set
`COMPOSE_PROFILES=mosquitto,homeassistant` and `HOMEASSISTANT_ENABLED=true` in `.env` as well, and
follow every step below, including the broker credentials in step 7.

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

**3. Enable cgroup memory accounting.** On a split-boot Pi this edits the **SD card** — see
[Storage layout](#storage-layout). Raspberry Pi OS ships with it off, and without it the
`mem_limit` settings in `docker-compose.yml` are silently ignored — which on a 1 GB board is the
difference between one container being killed and the whole box thrashing.

`cmdline.txt` is **one single line**, and the parameters go at the end of it. The bootloader reads
only the first line, so appending them as a second line — the obvious thing to do in an editor —
leaves them completely inert while the file *looks* correct. This flattens the file to one line and
appends the parameters only if they are missing, so it repairs that case as well as a fresh one:

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

Check the file before rebooting: a mangled `cmdline.txt` means a Pi that doesn't boot, and fixing
that needs the SD card in another machine. `cmdline.txt.bak` is the way back.

> Do **not** guard this with `grep -q cgroup_enable=memory cmdline.txt`. That matches the parameters
> sitting uselessly on line 2, so the guard concludes there is nothing to do in exactly the case that
> needs fixing.

On Bullseye and older the file is `/boot/cmdline.txt` instead — `/boot/firmware/` is Bookworm's
layout.

After the reboot, check what the kernel actually booted with, which is the authoritative answer
(`cmdline.txt` only says what *should* have been passed):

```bash
cat /proc/cmdline | tr ' ' '\n' | grep cgroup   # expect cgroup_enable=memory and cgroup_memory=1
docker info 2>&1 | grep -i "memory limit"       # note 2>&1: these warnings go to stderr
```

The `2>&1` matters. Docker prints those warnings to stderr, so a plain `docker info | grep ...` lets
them bypass the pipe and reach your terminal anyway — they look like grep output but aren't, and the
command appears to "fail" even after the fix has worked.

> **`WARNING: No swap limit support` is expected — ignore it.** Swap accounting is a separate
> facility, it costs memory to enable, and nothing in this stack asks for it: the compose file sets
> `mem_limit` and never `memswap_limit`. Only `WARNING: No memory limit support` matters, and it
> should be gone once `/proc/cmdline` shows the two parameters above.

You will see `cgroup_disable=memory` in `/proc/cmdline` and it is **not** a mistake — the Pi firmware
injects it, and it appears in no file on the boot partition, so there is nothing to delete. The
firmware's parameters come first and `cmdline.txt`'s follow, so the `cgroup_enable=memory` you added
is processed later and wins. Both being present is the normal, working state.

If `/proc/cmdline` still lacks `cgroup_enable=memory` after a reboot, the edit landed somewhere the
boot process doesn't read. Check that `wc -l /boot/firmware/cmdline.txt` reports `1`, and that
`ls /boot/firmware/cmdline.txt /boot/cmdline.txt` confirms which file this OS actually boots from —
on Bookworm and later, `/boot/cmdline.txt` is a stub whose only content is "DO NOT EDIT THIS FILE".

**4. Check swap — on Trixie there is nothing to add.** 1 GB of RAM with no swap turns a transient
spike into an OOM kill, so this step used to build a `dphys-swapfile`. Raspberry Pi OS **Trixie
(Debian 13) does not ship `dphys-swapfile` at all** — those commands fail with `command not found` —
because the image now configures swap itself, and configures it better:

```bash
cat /proc/swaps      # expect /dev/zram0, ~905MB, priority 100
free -h
```

What you get out of the box is **zram**: a compressed block device in RAM (zstd), used as swap at
priority 100, with **writeback to `/var/swap`** on disk for pages too cold to be worth keeping
compressed in memory. `rpi-setup-loop@var-swap.service` puts that file on a loop device and
`systemd-zram-setup@zram0.service` builds the swap on top. So `/var/swap` existing is not a leftover
— it is zram's backing store, and it is why `swapon --show` lists only `/dev/zram0`.

This is strictly better than the old swapfile for this workload: compressing a page costs far less
than writing it out, and there is still a disk tier behind it. **Nothing to do here.**

> **Optional, and only on a disk-rooted Pi.** zram lives in RAM, so it competes for the resource
> that is already scarce: compression buys roughly 2–3× on typical data but cannot manufacture
> capacity, and the compose limits total 848 MB (600 + 200 + 48) on a 905 MB board. A plain swapfile
> on the root disk, at a *lower* priority than zram, is a cheap overflow tier that only catches what
> zram cannot hold:
>
> ```bash
> sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile
> sudo mkswap /swapfile && sudo swapon --priority 10 /swapfile
> echo '/swapfile none swap sw,pri=10 0 0' | sudo tee -a /etc/fstab
> ```
>
> Do **not** do this if root is on an SD card — that is a write-amplification machine pointed at the
> one component most likely to fail. It is only reasonable because the reference install roots on an
> NVMe (see *Storage layout* above). Leave `vm.swappiness` at its default 60 so the disk tier is a
> safety net rather than a first resort.

**5. Directories.** The containers hold no state; everything lives here. `logs/` and `data/` are
needed by every deployment; `mosquitto/` and `homeassistant/` only if you're using those profiles
(see [Running without Home Assistant](#running-without-home-assistant-controller--web-ui-only)) —
skip those two and their `chown` if you're not, `deploy.sh` doesn't require them to pre-exist:

```bash
sudo mkdir -p /opt/solax/{mosquitto/config,mosquitto/data,homeassistant/config,logs,data}
sudo chown -R "$USER" /opt/solax
sudo chown -R 1883:1883 /opt/solax/mosquitto/data         # the broker writes here
sudo chown -R 1654:1654 /opt/solax/logs /opt/solax/data   # the controller image's non-root uid
```

**Do not chown `mosquitto/config` to 1883.** Only `mosquitto/data` belongs to the broker. The config
directory is written by `deploy.sh` (that is where `mosquitto.conf` lands) and only *read* by the
broker, whose compose mount is read-only — so handing it to uid 1883 locks the deploy out of it and
fails with `tar: mosquitto/config/mosquitto.conf: Cannot open: Permission denied`. The one file in
there that does belong to 1883 is `passwd`, chowned in step 7.

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

**7. Broker credentials.** *Only if `COMPOSE_PROFILES` includes `mosquitto` — skip this entirely for
a controller-only or controller-plus-UI deployment; `deploy.sh` itself skips the check below when it
doesn't find `mosquitto` in `.env`'s `COMPOSE_PROFILES`.* The broker refuses anonymous connections,
so this must exist before the stack will work. The username has to match `MQTT_USERNAME` **and** the
password has to match `MQTT_PASSWORD` in `.env` — a broker password with an empty `MQTT_PASSWORD`
beside it is a stack that comes up looking healthy while the controller is refused on every connect.

`-c` **creates** the file and refuses to overwrite one that exists, reporting the bare errno
`Error: Unable to open file ... for writing. File exists.` — which reads like a permissions problem
and isn't. So create with `-c` the first time and update without it afterwards:

```bash
# first time (no passwd file yet)
docker run --rm -v /opt/solax/mosquitto/config:/mosquitto/config eclipse-mosquitto:2 \
    mosquitto_passwd -c -b /mosquitto/config/passwd solax '<password>'

# changing the password later, or adding another user -- note: no -c
docker run --rm -v /opt/solax/mosquitto/config:/mosquitto/config eclipse-mosquitto:2 \
    mosquitto_passwd -b /mosquitto/config/passwd solax '<password>'

sudo chown 1883:1883 /opt/solax/mosquitto/config/passwd
sudo chmod 600 /opt/solax/mosquitto/config/passwd
```

`mosquitto_passwd` warns that the file's owner is not root and that "future versions will refuse to
load" it. Ignore it here: the broker runs as uid 1883 and has to be able to read the file, and the
compose mount is read-only so the image's entrypoint cannot chown it back.

**Check the credentials actually work** before blaming the controller. This starts a throwaway broker
against the real password file, so it is valid even before `deploy.sh` has copied `mosquitto.conf`:

```bash
docker run --rm -v /opt/solax/mosquitto/config:/mosquitto/config:ro eclipse-mosquitto:2 sh -c '
  printf "listener 1883\nallow_anonymous false\npassword_file /mosquitto/config/passwd\n" > /tmp/t.conf
  mosquitto -c /tmp/t.conf -d && sleep 2
  mosquitto_pub -h 127.0.0.1 -u solax -P "<password>" -t solax/authtest -m ok && echo ACCEPTED
  mosquitto_pub -h 127.0.0.1 -t solax/authtest -m no 2>/dev/null && echo "ANONYMOUS ACCEPTED -- wrong" || echo "anonymous rejected -- correct"
'
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
├── docker-compose.web.yml          → /opt/solax/docker-compose.web.yml (published UI port; opt-in via .env)
├── mosquitto/config/mosquitto.conf → /opt/solax/mosquitto/config/    (overwritten each deploy)
├── homeassistant/config/*.yaml     → /opt/solax/homeassistant/config/ (seeded once, never overwritten)
├── .env.example                    → copied by hand, once, as /opt/solax/.env
└── deploy.sh
```

From a developer machine, with the repo checked out:

```bash
./deploy/deploy.sh
```

It copies `docker-compose.yml`, `docker-compose.web.yml` and `mosquitto.conf` to `/opt/solax`, seeds
Home Assistant's config files only if they don't already exist, then pulls and restarts. It creates
`logs/` and `data/` and hands them to uid 1654 if they are missing or wrongly owned; for anything
else it refuses to run rather than guess. It never copies `.env` — which is also what decides
whether `docker-compose.web.yml` and Home Assistant/the broker actually run; see
[Running without Home Assistant](#running-without-home-assistant-controller--web-ui-only).

| Variable | Default | |
|---|---|---|
| `PI_HOST` | `martin@192.168.2.7` | ssh target |
| `REMOTE_DIR` | `/opt/solax` | stack location on the Pi |
| `IMAGE_TAG` | from `.env` (`latest`) | which build to run |

## First run

**If `Web:Enabled=true`:** open `http://192.168.2.7:8080`, sign in with the password behind
`WEB_PASSWORD_HASH`, and the dashboard, the controls, session history and the forecast plan are all
there — see the root README's
[Self-hosted web UI](../README.md#self-hosted-web-ui-the-web-section) section for what each page
does.

**If Home Assistant is enabled** (`COMPOSE_PROFILES` includes `homeassistant`):

1. Open `http://192.168.2.7:8123` and complete Home Assistant onboarding (local account).
2. **Settings → Devices & Services → Add Integration → MQTT.** Broker `mosquitto`, port `1883`, and
   **the username and password from `.env`** — unlike the dev stack, this broker is authenticated.
3. The controller publishes MQTT discovery configs on connect; the SolaX device and its entities
   appear by themselves.

Deploying writes nothing to your hardware. Charge control boots in mode **Off** and takes control
only once you select a mode — Home Assistant or the web UI, whichever is enabled — and
`BatteryHold` is disabled and dry-run. Change either in `.env` only after verifying the register
addresses on your own device, per the root README's warnings.

## Everyday operations

```bash
ssh martin@192.168.2.7
cd /opt/solax

docker compose ps                          # what's running
docker compose logs -f solax-controller    # follow the poll loop
docker compose restart solax-controller
docker stats --no-stream                   # memory headroom -- the number that matters here

# which build is actually running -- version and the commit it came from
docker compose logs solax-controller | grep "starting\."
```

That last line is worth knowing before debugging anything. The worker logs its own version and
commit at startup (`SolaX Local Controller 1.0.0 (31bf347) starting.`), so a log file is traceable to
a build without matching it against image digests. Home Assistant shows the same string as the
device's software version. `0.0.0-dev` with no commit means somebody deployed a local build.

Upgrade to the latest build, or roll back to a known-good one:

```bash
./deploy/deploy.sh                              # latest from main
IMAGE_TAG=1.0.0 ./deploy/deploy.sh              # a released version -- no "v"
IMAGE_TAG=sha-abc1234 ./deploy/deploy.sh        # a specific build
```

**The image tag has no `v`, though the git tag does.** Releases are cut as git tag `v1.0.0`, and the
publish workflow strips the prefix, so the image is `…/solax-controller:1.0.0`. `IMAGE_TAG=v1.0.0`
does not exist and the pull fails with `manifest unknown`.

Both preserve all state. So does `docker compose down`, and so does `docker rm -f` on any single
container — that is the point of the layout below.

Deploy from a checked-out tag rather than your working branch. `deploy.sh` copies the **local**
`deploy/` tree to the Pi, so the compose file and the image otherwise come from two different places:

```bash
git switch --detach v1.0.0 && IMAGE_TAG=1.0.0 ./deploy/deploy.sh
```

Note the two forms in that one line: `v1.0.0` is the **git** tag you check out, `1.0.0` is the
**image** tag you pull.

## Which image you get

Everything publishes to one package, `ghcr.io/mpospisil/solax-controller`, as a multi-platform
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

The suffixed tags exist for pinning and for answering "which one did it actually pull"; day to day
you want the bare name. `docker buildx imagetools inspect ghcr.io/mpospisil/solax-controller:latest`
lists every platform behind a tag.

**If `latest` looks stale, check the publish workflow.** The bare tags are only created once *all*
platforms of that build exist, so one failed platform holds back the whole release — deliberately,
since a `latest` that silently lost a platform is worse. The Windows job is the fragile one, because
GitHub's Windows runners intermittently start without a Docker daemon
([actions/runner-images#13729](https://github.com/actions/runner-images/issues/13729)). The
single-platform tags are pushed regardless, so the Pi is never actually blocked:

```bash
IMAGE_TAG=sha-abc1234-linux-arm64 ./deploy/deploy.sh
```

### Running on Windows

The Nano Server image runs the same worker, with one difference that will bite silently if it is
missed: **.NET on Windows ignores the `TZ` environment variable.** The `TZ` line in
`docker-compose.yml` does nothing there, the container runs in UTC, and every recorded charging
session is filed against the wrong day. Set the zone explicitly instead, as a **Windows** id —
Nano Server ships no ICU, so IANA ids like `Europe/Prague` cannot be resolved on it:

```powershell
docker run -e Controller__TimeZone="Central Europe Standard Time" `
  ghcr.io/mpospisil/solax-controller:latest
```

The worker logs a warning at startup if it finds itself on Windows with the zone unset. On Linux the
setting stays empty and `TZ` keeps working exactly as before.

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

**Can't reach the web UI at `:8080`.** Two independent switches both have to be on, and it's usually
one of them:

```bash
docker compose logs solax-controller | grep -i "web ui"
```

`Web UI enabled; listening on port 8080` means `Web:Enabled` is true and the *process* is listening
— if the browser still can't connect, the port isn't published: check `.env`'s `COMPOSE_FILE`
includes `docker-compose.web.yml` (`docker compose config` on the Pi shows whether it actually got
picked up). No such log line at all means `WEB_ENABLED` itself is unset or `false` in `.env`. See
[Running without Home Assistant](#running-without-home-assistant-controller--web-ui-only).

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
