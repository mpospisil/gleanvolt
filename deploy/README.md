# Deploying to a Raspberry Pi

The production stack for [issue #26](https://github.com/mpospisil/solax-controller/issues/26): the
controller — with its self-hosted web UI — Home Assistant, and an MQTT broker as up to three Docker
containers on a Raspberry Pi running Raspberry Pi OS Lite (64-bit), Debian 13 (Trixie).

**There are two ways to deploy this, and how much RAM your Pi has decides which one you want.** Home
Assistant is the expensive part by a wide margin; the controller and its own web UI are not. Start at
[Choose your deployment](#choose-your-deployment) — it is one table, and it tells you which of the
steps further down apply to you.

```
                  Raspberry Pi  (192.168.2.7, arm64)
    ┌────────────────────────────────────────────────────────────────────┐
    │ compose project "solax"        (all state on bind mounts)          │
    │                                                                    │
    │ solax-controller ──MQTT──▶ mosquitto ◀──MQTT── homeassistant       │
    │      │             (opt-in profile,     opt-in profile,            │
    │      │              no host port)          LAN :8123)              │
    │      └── LAN :8090, the web UI -- on by default                    │
    └────────────────────────────────────────────────────────────────────┘
         │ Modbus TCP
         ▼
  inverter 192.168.2.6:502
  charger  192.168.2.10:502
```

The Pi never builds anything. CI builds a `linux/arm64` image and pushes it to GHCR; the Pi pulls it.

> **Not the dev stack.** `dev/homeassistant/` is a separate, anonymous-broker environment for
> developing against `dotnet run`. Don't point one at the other; running both at once against the
> same inverter is confusing at best.

## Choose your deployment

Both workflows give you full control of the inverter and charger. They differ in whether Home
Assistant is part of it — and Home Assistant is what sets the hardware bar.

| | **A — Full stack** | **B — Controller only** |
|---|---|---|
| **RAM required** | **2 GB minimum, 4 GB recommended** | **1 GB is enough** |
| **Disk** | 16 GB minimum, 32 GB recommended | 8 GB |
| Containers | controller, broker, Home Assistant | controller |
| `mem_limit` total | 848 MB | 200 MB |
| Deploy script | `deploy.sh` | `deploy-controller-only.sh` |
| Control surfaces | Home Assistant `:8123` **and** web UI `:8090` | web UI `:8090` |
| Required in `.env` | site addresses, `HOMEASSISTANT_ENABLED`, MQTT credentials | site addresses only |
| Extra setup steps | broker password file, Home Assistant onboarding | none |

**RAM is the deciding factor, and Home Assistant is why.** It reserves 600 MB of the 848 MB the full
stack claims, and it genuinely uses most of that — around 485 MB in steady state, not a theoretical
ceiling. The controller with its UI is a single .NET process using roughly 70 MB against its 200 MB
limit, and the broker sits under 5 MB.

So on a **1 GB board the full stack commits 94% of the machine** before the OS and page cache get a
look in. It runs — that was the original reference install — but there is no headroom, and a board
with no headroom is one that swaps under load. Two gigabytes makes it comfortable; four makes it a
non-issue. If your Pi has 1 GB, **choose workflow B** rather than trying to squeeze Home Assistant
in beside it.

Disk follows the same split: the Home Assistant image alone is about 3.4 GB, against roughly 375 MB
for the controller.

> The web UI is **not** a separate container and has no `mem_limit` of its own — it runs inside the
> controller process either way. Turning it off with `WEB_ENABLED=false` frees no memory worth
> counting; it only closes the port. Home Assistant and the broker are the only things that move the
> numbers above.

### Workflow A — full stack, with Home Assistant

For a Pi with **2 GB of RAM or more**. Choose this if you already run Home Assistant, or want the
inverter and charger to sit alongside the rest of your home automation.

Required in `/opt/solax/.env`:

```bash
TZ=Europe/Prague               # your timezone
INVERTER_HOST=192.168.2.6      # your inverter's address
EV_CHARGER_HOST=192.168.2.10   # your charger's address

HOMEASSISTANT_ENABLED=true     # the controller's own switch to publish MQTT
MQTT_USERNAME=solax            # must match the broker's password file
MQTT_PASSWORD=<your password>  # must match the broker's password file
```

Then work through **every** step in [Prepare the Pi](#prepare-the-pi-once), including step 7
(broker credentials) — the broker refuses anonymous connections, so a missing or mismatched password
file is a stack that comes up looking healthy while the controller is refused on every connect.
Deploy with `./deploy/deploy.sh`, then complete Home Assistant's onboarding at `:8123` as described
under [First run](#first-run).

### Workflow B — controller and its web UI only

For a Pi with **1 GB of RAM**, or any board where you simply don't want Home Assistant. The
controller's own [web UI](../README.md#self-hosted-web-ui-the-web-section) shows live telemetry,
drives every control Home Assistant would, and browses charging-session history and the forecast
plan — so this is a smaller deployment, not a lesser one.

Required in `/opt/solax/.env` — this is the whole list:

```bash
TZ=Europe/Prague               # your timezone
INVERTER_HOST=192.168.2.6      # your inverter's address
EV_CHARGER_HOST=192.168.2.10   # your charger's address
```

Nothing about the web UI needs setting: it is on by default, on port 8090, and `docker-compose.yml`
publishes that port, so the deploy ends at a working `http://192.168.2.7:8090` with no login. Adding
a password is a [later, optional step](#putting-a-password-on-the-web-ui-optional).

Deploy with `./deploy/deploy-controller-only.sh`. In [Prepare the Pi](#prepare-the-pi-once), **skip
step 7** — this script never looks for a broker password file — and skip the Home Assistant
onboarding under [First run](#first-run). In step 5 you can create the `mosquitto/` and
`homeassistant/` directories anyway in case you switch later, or leave them out; neither script
requires them to exist until it actually needs them.

### Switching between the two

Which containers run is decided by **which script you run**, not by a setting that can go stale.
`docker-compose.yml` gives `mosquitto` and `homeassistant` a `profiles:` key, and both scripts set
`COMPOSE_PROFILES` explicitly for their own `docker compose` invocations, overriding whatever
happens to be sitting in `.env` on the Pi.

To move from B to A: set `HOMEASSISTANT_ENABLED=true`, add the MQTT credentials, create the broker
password file (step 7), and run `./deploy/deploy.sh`. To go the other way, just run
`./deploy/deploy-controller-only.sh` — the extra containers are removed and your data is untouched.

## Storage and the boot medium

Nothing here depends on a particular disk. Two things matter: having the space, and whether your
board can boot the medium you want to run from.

**Space.** Workflow A needs about 16 GB to be comfortable, mostly because the Home Assistant image is
around 3.4 GB; workflow B is happy with 8 GB. Both grow slowly afterwards — the charging-session
database and the log files are the only things that accumulate, and both are small.

**An SD card works, and USB-attached storage lasts longer.** The controller writes continuously: log
files, and a SQLite database with its write-ahead log. SD cards tolerate that poorly over years, so
if this is meant to run unattended, putting root on an SSD in a USB enclosure is the single upgrade
that most reduces the odds of a mystery failure eighteen months in. It is not required.

**Not every Pi can boot from USB, and that is worth checking before you buy anything.** Recent boards
boot USB mass storage directly, and then there is nothing to think about: one device holds both
`/boot/firmware` and `/`. Older boards may not boot from USB at all, or may fail with particular
USB-to-SATA/NVMe bridges — their boot ROM allows the device only a brief window to respond, which
some adapters miss, even though the Linux kernel drives the very same adapter without complaint once
it is running.

On a board like that, use a **split boot**: the SD card holds `/boot/firmware`, and `cmdline.txt`
hands root to the USB disk. That works well, at the cost of two things it is easy to get wrong:

- **`/boot/firmware` is then on the SD card.** Every instruction below that edits `cmdline.txt` — the
  cgroup step in particular — is editing the SD card, which is correct, because that is the partition
  the board actually boots. The USB disk usually keeps its own boot partition from whatever image was
  written to it; editing *that* one changes nothing at all, silently.
- **`/etc/fstab` must point `/boot/firmware` at the SD card's PARTUUID**, not at the USB disk's
  leftover boot partition. Get this wrong and kernel updates land somewhere that is never booted, so
  the machine breaks at some upgrade weeks later rather than at the moment of the mistake. Note that
  a first-boot filesystem resize rewrites the partition table signature and therefore every PARTUUID
  on the disk: the firstboot script repairs `cmdline.txt` and the root line in `fstab` by itself, but
  it knows nothing about the `/boot/firmware` line.

Neither applies if you boot and root on the SD card in the usual way, or on a board that boots USB
directly.

## Putting a password on the web UI (optional)

Out of the box the UI has **no login**: anyone who can reach `:8090` gets the dashboard and every
control on it, including the charge mode and the battery hold. On a household LAN that is usually
what you want, and it is what lets a fresh deploy work with nothing configured. It is the wrong
default if the LAN has guests on it, if the Pi's port is forwarded, or if you would simply rather it
asked.

Adding one is a single `.env` line. Generate the hash — this runs the image with no configuration and
no listening socket; it prints the hash and exits — then put the **hash**, never the password, in
`.env`:

```bash
docker run --rm ghcr.io/mpospisil/solax-controller:latest hash-password '<your password>'
```

```
WEB_PASSWORD_HASH=AQAAAAIAAYagAAAAE...
```

Redeploy (or `docker compose up -d` on the Pi) and every page redirects to a login form. There is no
second switch: the presence of a hash is what turns the login on, so a configured password can't end
up unenforced. One shared password gates the whole UI — there are no per-user accounts.

To take it off again, remove the line and redeploy. `WEB_REQUIRE_AUTHENTICATION` exists to override
the inference in either direction and is rarely worth setting; the root README's
[Authentication](../README.md#authentication) section has the full table, including the one
combination that refuses to start (`WEB_REQUIRE_AUTHENTICATION=true` with no hash — nobody could sign
in).

### Upgrading a Pi deployed before the UI was on by default

Earlier releases had the UI off, required `WEB_ENABLED=true`, and published its port only if `.env`
merged a second compose file. Deploying this release onto such a Pi works, but two lines in `.env`
are now stale and one of them is actively harmful:

- **`COMPOSE_FILE=docker-compose.yml:docker-compose.web.yml`** — delete it. `docker-compose.yml`
  publishes the port itself; the second file is kept only so this line doesn't break the deploy
  outright, and merging it does nothing.
- **`WEB_ENABLED=true`** — harmless, now the default. Delete it or leave it.
- **`WEB_REQUIRE_AUTHENTICATION=true`** — if you had this *and* a `WEB_PASSWORD_HASH`, nothing
  changes and you can delete the line: the hash alone requires the login. If you had it **without** a
  hash, the container refused to start before this release too, so it can't be the state you're
  running.

An `.env` from before the web UI existed at all needs nothing: no `WEB_*` line means UI on, no login.

## Prepare the Pi (once)

**1. Passwordless SSH.** Either deploy script opens close to a dozen separate SSH connections — with
password authentication you would be prompted for every one of them, and a deploy stops being a
single command. From your **developer machine**:

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

**3. Enable cgroup memory accounting.** Required on every board and in both workflows — how much
memory the Pi has has nothing to do with it. On a [split boot](#storage-and-the-boot-medium) this
edits the **SD card**; otherwise there is only one device to edit.

Without it the `mem_limit` settings in `docker-compose.yml` are silently ignored: `docker run
--memory=…` answers `WARNING: Your kernel does not support memory limit capabilities … Limitation
discarded`, `docker inspect` records `Memory=0`, and `docker stats` reports `0B / 0B` for every
container forever. On a 1 GB board that is the difference between one container being killed and the
whole box thrashing. On a 4 GB board it is milder but not harmless — you lose per-container memory
figures exactly when you need them, and a leak escalates to the kernel OOM killer picking a victim
by its own heuristic instead of the guilty container hitting its own ceiling.

**The parameters are not merely absent — the firmware actively disables the controller.** A fresh
`cmdline.txt` says nothing about cgroups, yet `/proc/cmdline` contains `cgroup_disable=memory`,
because the Raspberry Pi firmware prepends it. What you add to `cmdline.txt` lands *after* the
firmware's copy on the kernel command line, and the later parameter wins — which is why appending is
enough and there is nothing to delete.

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

Check the file before rebooting: a mangled `cmdline.txt` means a Pi that doesn't boot, and fixing it
means putting the boot medium — whichever one holds `/boot/firmware` — into another machine.
`cmdline.txt.bak` is the way back.

A fresh Raspberry Pi OS image may also leave `cmdline.txt` with **no trailing newline**, so `wc -l`
prints `0` rather than `1` before you start. That is not the second-line fault this step guards
against, and the snippet above fixes it in passing: `printf "%s\n"` writes the newline back.

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
cat /proc/swaps      # expect /dev/zram0 at priority 100, sized to about half your RAM
free -h
```

zram sizes itself to the board — roughly 905 MB on a 1 GB Pi, 2 GB on a 4 GB one — so the number
differs and neither is wrong. `swapon` and `zramctl` live in `/usr/sbin`, which a non-interactive
`ssh pi 'swapon --show'` does not have on its `PATH`; `cat /proc/swaps` answers the same question
from anywhere.

What you get out of the box is **zram**: a compressed block device in RAM (zstd), used as swap at
priority 100, with **writeback to `/var/swap`** on disk for pages too cold to be worth keeping
compressed in memory. `rpi-setup-loop@var-swap.service` puts that file on a loop device and
`systemd-zram-setup@zram0.service` builds the swap on top. So `/var/swap` existing is not a leftover
— it is zram's backing store, and it is why `swapon --show` lists only `/dev/zram0`.

This is strictly better than the old swapfile for this workload: compressing a page costs far less
than writing it out, and there is still a disk tier behind it. **Nothing to do here.**

> **Optional, and only on a 1 GB board running workflow A.** zram lives in RAM, so it competes for
> the resource that is already scarce: compression buys roughly 2–3× on typical data but cannot
> manufacture capacity, and the full stack's limits total 848 MB (600 + 200 + 48) against about
> 905 MB usable. A plain swapfile on the root disk, at a *lower* priority than zram, is a cheap
> overflow tier that only catches what zram cannot hold:
>
> ```bash
> sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile
> sudo mkswap /swapfile && sudo swapon --priority 10 /swapfile
> echo '/swapfile none swap sw,pri=10 0 0' | sudo tee -a /etc/fstab
> ```
>
> Do **not** do this if root is on an SD card — that is a write-amplification machine pointed at the
> one component most likely to fail. It is only reasonable with root on an SSD (see
> [Storage and the boot medium](#storage-and-the-boot-medium)). Leave `vm.swappiness` at its default
> 60 so the disk tier is a safety net rather than a first resort.
>
> **On 2 GB or more the whole argument collapses**, and on workflow B it never applied: 848 MB of
> limits against 4 GB is not a scarce resource, zram scales up with the board, and it already has
> `/var/swap` behind it. Adding a third tier below two that never fill is work for nothing. Enforced
> `mem_limit` values (step 3) are the better answer to the same worry, because they stop a leak at
> the container instead of absorbing it.

**5. Directories.** The containers hold no state; everything lives here. `logs/` and `data/` are
needed by every deployment; `mosquitto/` and `homeassistant/` only if you're deploying with
`deploy.sh` rather than `deploy-controller-only.sh` (workflow A — see
[Choose your deployment](#choose-your-deployment)) — skip those two and their `chown` if you're not;
neither script requires them to pre-exist:

```bash
sudo mkdir -p /opt/solax/{mosquitto/config,mosquitto/data,homeassistant/config,logs,data}
sudo chown -R "$USER" /opt/solax
sudo chown -R 1883:1883 /opt/solax/mosquitto/data         # the broker writes here
sudo chown -R 1654:1654 /opt/solax/logs /opt/solax/data   # the controller image's non-root uid
```

**Do not chown `mosquitto/config` to 1883.** Only `mosquitto/data` belongs to the broker. The config
directory is written by the deploy script (that is where `mosquitto.conf` lands, from either script —
see [`deploy/_lib.sh`](_lib.sh)) and only *read* by the broker, whose compose mount is read-only — so
handing it to uid 1883 locks the deploy out of it and fails with `tar: mosquitto/config/mosquitto.conf:
Cannot open: Permission denied`. The one file in there that does belong to 1883 is `passwd`, chowned
in step 7.

`data/` holds the charging-session SQLite database. SQLite writes its `-wal` and `-shm` files next to
the database, so that **directory** — not just the file — has to be writable by uid 1654.

`logs/` and `data/` are the two the *application* writes to, and both deploy scripts create them
themselves (and hand them to uid 1654) if they are missing or wrongly owned — which is what makes a
release that adds one, as `data/` did, deploy onto an existing Pi without manual work. They do that
through a throwaway `alpine` container, because the SSH user cannot chown to another uid but the
Docker daemon can; the image is pulled once, on the first deploy that needs it. Listing them above is
still worth doing on a fresh Pi, so the whole tree exists before anything runs.

**6. Secrets.** From your developer machine:

```bash
scp deploy/.env.example martin@192.168.2.7:/opt/solax/.env
ssh martin@192.168.2.7 'chmod 600 /opt/solax/.env && nano /opt/solax/.env'
```

**7. Broker credentials.** *Only if you're deploying with `deploy.sh` (the full stack) — skip this
entirely for a controller-only or controller-plus-UI deployment via `deploy-controller-only.sh`, which
never checks for a password file.* The broker refuses anonymous connections,
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
├── deploy.sh                       # full stack: controller, mosquitto, Home Assistant
├── deploy-controller-only.sh       # controller alone, with its web UI on :8090
└── _lib.sh                         # shared by both scripts above; not run directly
```

From a developer machine, with the repo checked out, pick the script for the workflow you chose in
[Choose your deployment](#choose-your-deployment):

```bash
./deploy/deploy.sh                    # full stack
./deploy/deploy-controller-only.sh    # controller only
```

Either copies `docker-compose.yml`, `docker-compose.web.yml` and `mosquitto.conf` to `/opt/solax`,
seeds Home Assistant's config files only if they don't already exist, then pulls and restarts —
setting `COMPOSE_PROFILES` itself for that restart (`mosquitto,homeassistant` or empty), which is
what actually decides whether the other two containers run, regardless of what's already in `.env`
on the Pi. Both create `logs/` and `data/` and hand them to uid 1654 if they are missing or wrongly
owned; for anything else they refuse to run rather than guess. Neither copies `.env` — which still
decides whether `docker-compose.web.yml` (the UI's published port) and `HomeAssistant:Enabled`
actually take effect.

| Variable | Default | |
|---|---|---|
| `PI_HOST` | `martin@192.168.2.7` | ssh target |
| `REMOTE_DIR` | `/opt/solax` | stack location on the Pi |
| `IMAGE_TAG` | from `.env` (`latest`) | which build to run |

All three work identically on either script.

## First run

**The web UI** is at `http://192.168.2.7:8090` — no login unless you configured one (see
[Putting a password on the web UI](#putting-a-password-on-the-web-ui-optional)). The dashboard, the
controls, session history and the forecast plan are all there; see the root README's
[Self-hosted web UI](../README.md#self-hosted-web-ui-the-web-section) section for what each page
does. This is true of either deploy script — the UI runs inside `solax-controller` itself, so Home
Assistant's presence or absence changes nothing about it.

**If you deployed with `deploy.sh`** (the full stack, Home Assistant included):

1. Open `http://192.168.2.7:8123` and complete Home Assistant onboarding (local account).
2. **Settings → Devices & Services → Add Integration → MQTT.** Broker `mosquitto`, port `1883`, and
   **the username and password from `.env`** — unlike the dev stack, this broker is authenticated.
3. The controller publishes MQTT discovery configs on connect; the SolaX device and its entities
   appear by themselves.

Deploying writes nothing to your hardware. Charge control boots in mode **Off** and takes control
only once you select a mode — Home Assistant or the web UI, whichever is enabled — and
`BatteryHold` is disabled and dry-run. Change either in `.env` only after verifying the register
addresses on your own device, per the root README's warnings.

## Updating a running deployment

**Updating is the same command as deploying.** Run the script again from your developer machine;
there is no separate update path, nothing to uninstall, and no step you perform on the Pi.

```bash
./deploy/deploy.sh                    # workflow A
./deploy/deploy-controller-only.sh    # workflow B
```

Use the script matching the workflow that is *already running*. Running the other one is how you
[switch between them](#switching-between-the-two) — which will add or remove containers, and is not
what you want if you only meant to pick up a new build.

### What the script does, in order

1. Checks `/opt/solax` exists and `.env` is there — it refuses rather than guessing.
2. Copies `docker-compose.yml`, `docker-compose.web.yml` and `mosquitto.conf` from your **local**
   `deploy/` directory, overwriting the Pi's copies.
3. Seeds Home Assistant's config files only if they don't already exist.
4. `docker compose pull` — every image in the active profiles.
5. `docker compose up -d --remove-orphans` — recreates only what actually changed.
6. Prints the container status and the URLs.

Step 5 is why an update is usually near-instant and mostly invisible: Compose compares each
container against the image and configuration it should have, and leaves alone the ones that already
match. A run that changes nothing prints `Container solax-controller Running` and touches nothing. A
run with a new image prints `Recreate` for that one container and leaves the others up.

### What survives an update

Everything that is state, because none of it lives inside a container:

| | |
|---|---|
| `/opt/solax/.env` | **never copied, never overwritten** — the deploy scripts do not touch secrets |
| `data/sessions.db` | charging-session history, with its SQLite WAL |
| `logs/` | the controller's own log files |
| `homeassistant/config/` | seeded once on first deploy, never overwritten afterwards |
| `mosquitto/config/passwd` | broker credentials, created by hand |

`docker compose down` and even `docker rm -f` on any single container are equally safe, for the same
reason. What *is* overwritten every deploy is `mosquitto.conf` and the compose files — so edit those
in the repo, not on the Pi, or your change disappears at the next update.

### Updating the controller also updates Home Assistant and the broker

Step 4 pulls **every** image in the active profiles, not just the controller. On workflow A that
means `ghcr.io/home-assistant/home-assistant:stable` and `eclipse-mosquitto:2` move to whatever those
tags point at now. That is usually what you want, but it means "I updated the controller" can also
mean "Home Assistant jumped a version" — worth knowing before you go looking for what changed.

To move only the controller, do that one step on the Pi instead:

```bash
ssh martin@192.168.2.7 'cd /opt/solax && docker compose pull solax-controller && docker compose up -d solax-controller'
```

That skips copying any updated compose files, so use it for a plain image bump, not after changing
`deploy/`.

### The controller restarts, so charge control returns to Off

An update recreates the container, and the worker always boots in charge mode **Off** with the
battery hold disabled — by design, so that a deployment never inherits control it wasn't given. If a
mode was active when you updated, **it is not active afterwards**; the charger is left exactly as it
was and waits for you to select a mode again in Home Assistant or the web UI.

Nothing is written to your hardware during the update itself, and a charging session in progress is
not lost: on a clean stop the recorder closes it with reason `ServiceStopped` and persists it, so it
appears in the history as a completed session. Charging after the restart is recorded as a *new*
session, so updating mid-session splits it in two. If the container is killed rather than stopped
cleanly, the next startup recovers the session as interrupted instead.

### Check what you are actually running

Before and after, from your machine:

```bash
ssh martin@192.168.2.7 'cd /opt/solax && docker compose logs solax-controller | grep "starting\."'
```

The worker logs its version and the commit it was built from — `SolaX Local Controller 1.0.0
(31bf347) starting.` — so you can confirm the new build is live rather than trusting that the pull
did something. Home Assistant shows the same string as the device's software version. `0.0.0-dev`
with no commit means somebody deployed a local build.

### Pinning a version, and rolling back

`IMAGE_TAG` selects the build, and works identically on both scripts:

```bash
./deploy/deploy.sh                              # latest from main
IMAGE_TAG=1.0.0 ./deploy/deploy.sh              # a released version -- no "v"
IMAGE_TAG=sha-abc1234 ./deploy/deploy.sh        # one specific build, immutable
```

A rollback is just an update pointed at an older tag; `sha-` tags are immutable, which makes them the
reliable thing to roll back *to*. Setting `IMAGE_TAG` in `/opt/solax/.env` pins it for every future
deploy that doesn't override it on the command line.

**The image tag has no `v`, though the git tag does.** Releases are cut as git tag `v1.0.0`, and the
publish workflow strips the prefix, so the image is `…/solax-controller:1.0.0`. `IMAGE_TAG=v1.0.0`
does not exist and the pull fails with `manifest unknown`.

**Deploy from a checked-out tag, not your working branch.** Either script copies the *local*
`deploy/` tree to the Pi, so otherwise the compose file and the image come from two different points
in history:

```bash
git switch --detach v1.0.0 && IMAGE_TAG=1.0.0 ./deploy/deploy.sh
```

Note the two forms in that one line: `v1.0.0` is the **git** tag you check out, `1.0.0` is the
**image** tag you pull.

### Changing settings rather than code

`.env` is never copied by the deploy scripts, so a settings change is a two-step job:

```bash
ssh martin@192.168.2.7 'nano /opt/solax/.env'
ssh martin@192.168.2.7 'cd /opt/solax && docker compose up -d'
```

`docker compose up -d` recreates only the containers whose environment actually changed. Re-running
the deploy script works too and is the better choice if you also changed anything in `deploy/`.

### If an update goes wrong

The scripts stop at the first failure rather than pressing on, and every step before the pull is
either a check or an idempotent repair — creating `logs/` and `data/` with the right owner, or fixing
`mosquitto/config` ownership. Re-running after fixing whatever it complained about is safe.

If a new build misbehaves, roll back to the previous tag with the same command. The database, logs
and configuration are all still there, because the container was never where they lived.

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

See [Updating a running deployment](#updating-a-running-deployment) for upgrades, rollbacks and
pinning.

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
> normal, and the log files simply never appear. Two things guard against it: both deploy scripts
> create it (and fix its ownership) before deploying, and the worker enables Serilog's `SelfLog` so the
> failure shows up in `docker logs` as `RollingFileSink: the target file could not be opened or
> created`. If you see that line, fix the ownership:
>
> ```bash
> sudo chown -R 1654:1654 /opt/solax/logs
> ```
>
> `/opt/solax/data` has the same requirement and the same guard from either script. It fails less quietly —
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

**Can't reach the web UI at `:8090`.** Nothing has to be configured for it to work, so this is a
fault rather than a missing setting. Start with what the container says about itself:

```bash
docker compose logs solax-controller | grep -i "web ui"
```

`Web UI enabled; listening on port 8090, login not required` means the *process* is listening. If the
browser still can't connect, check the port is actually published — `docker compose ps` should show
`0.0.0.0:8090->8090/tcp`, and a bare `8090/tcp` means it isn't. That is the symptom of an `.env` left
over from an older release: a `COMPOSE_FILE=` line that no longer matches the files on the Pi
overrides the base compose file rather than adding to it. Delete the `COMPOSE_FILE` line — it is
obsolete, `docker-compose.yml` publishes the port itself now — and redeploy.

No `Web UI enabled` line at all means `WEB_ENABLED=false` is set in `.env`. A startup failure instead
means `WEB_REQUIRE_AUTHENTICATION=true` was set with no `WEB_PASSWORD_HASH`; the log says so
explicitly, and the fix is to set a hash or drop the line (see
[Putting a password on the web UI](#putting-a-password-on-the-web-ui-optional)).

**No charging sessions are being recorded.** Look for this at startup:

```
[ERR] Could not open the charging session store; sessions will not be recorded this run.
      SQLite Error 14: 'unable to open database file'
```

That is `/opt/solax/data` not being writable by uid 1654. Everything else keeps running, which is why
it is easy to miss. Re-running whichever deploy script you used repairs the ownership on its own:

```bash
./deploy/deploy.sh                    # or deploy-controller-only.sh
```

Or, on the Pi directly:

```bash
sudo chown -R 1654:1654 /opt/solax/data && docker compose restart solax-controller
```

**The controller logs Modbus timeouts.** Check reachability from the Pi itself (`nc -vz`, step 8).
Bridge networking routes through the host, so if the Pi can reach the inverter, the container can.

**`docker compose` says permission denied.** The ssh user isn't in the `docker` group yet, or hasn't
logged out and back in since being added.

**It asks for a password (repeatedly).** SSH key authentication isn't set up — step 1. Either script
makes close to a dozen connections, so this is unusable without a key:

```bash
ssh-copy-id martin@192.168.2.7
```

If the account isn't `martin`, pass your own: `PI_HOST=<user>@192.168.2.7 ./deploy/deploy.sh`.

**Locked out over SSH.** <https://connect.raspberrypi.com/> gives you a shell without the LAN.
