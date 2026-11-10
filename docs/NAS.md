# Running on a NAS

A NAS that is on all the time, on the same LAN as the inverter and the charger, is as good a host as the
Pi. The controller uses about 75 MB of RAM, so any NAS that runs containers has room for it.

**What it needs:** a **64-bit** NAS, x86-64 (Intel, AMD) or arm64, that runs Docker containers. The image
has no 32-bit build: **32-bit ARM (armv7) is not supported**, and on such a NAS the pull fails with
`no matching manifest for linux/arm/v7`. Older ARM models from Synology and QNAP, and most from before
2020, are 32-bit; the vendor's spec sheet names the CPU.

**What it runs:** the controller and its web UI, from its own compose file,
[`docker-compose.nas.yml`](../deploy/docker-compose.nas.yml). `deploy.sh` and `deploy-controller-only.sh` are for
the Pi: they work over ssh from a developer machine and assume `/opt/gleanvolt`. On a NAS the vendor's
container UI does their job, and the compose file does the one step they did on the Pi, giving the data
directory to the controller's user.

## The compose file

Copy [`docker-compose.nas.yml`](../deploy/docker-compose.nas.yml) into the NAS's container UI as it is. It differs
from the Pi's `docker-compose.yml` in four ways:

- **Only the controller.** No broker, no Home Assistant, no profiles. If you already run Home Assistant
  and a broker elsewhere, add their settings as in
  [To a Home Assistant you already run](HOME_ASSISTANT.md#to-a-home-assistant-you-already-run).
- **A one-shot `gleanvolt-init` container** runs before the controller, as root, and changes the owner
  of `data/` and `logs/` to uid 1654, the controller's user. It then exits, and a NAS UI shows it as
  *stopped* or *exited (0)*. That is its normal state, not a fault.
- **`GLEANVOLT_DIR`** says where `data/` and `logs/` are. The default, `.`, is the folder the compose
  file is in.
- **Few settings.** The setup wizard asks for everything else. Any other setting in
  [`.env.example`](../deploy/.env.example) can still be added as a line under `environment:`, in the same form as
  in `docker-compose.yml`.

Set these three before the first start, either in an `.env` file beside the compose file, in the UI's
environment variables, or by editing the default after `:-` in the file itself:

| Variable | Default | Why it matters on a NAS |
|---|---|---|
| `TZ` | `Europe/Prague` | Containers run in UTC unless told otherwise, and a site on the wrong zone files every charging session against the wrong day. Any IANA name, e.g. `Europe/Berlin`. |
| `GLEANVOLT_DIR` | `.` | Where the data lives. Use an absolute path on a share that is backed up, e.g. `/volume1/docker/gleanvolt`, `/mnt/user/appdata/gleanvolt`. |
| `WEB_PORT` | `8090` | The web UI's port on the NAS. Change it if another app already has 8090. |

Then start it. The web UI is at `http://<nas-address>:8090` and opens on the setup wizard: choose a
password, let it find the inverter and the charger, and restart from the last step. See
[The setup wizard](WEB_UI.md#the-setup-wizard) for what to expect after that.

## Things that go wrong on a NAS

**Bridge networking.** The file uses `network_mode: host`, and that has to survive whatever the NAS UI
does with it. In a bridge network the setup wizard's device search scans Docker's 172.x network and
finds nothing, and the web UI is not on the NAS's port at all. If a UI offers a network choice, pick
**host**. The search's own message says when it is looking at a container network.

**The port is taken.** The log says `address already in use` and the container restarts in a loop. Set
`WEB_PORT` to a free port. NAS admin pages usually sit on 80/443, 5000/5001 (Synology) or 8080 (QNAP),
so 8090 is free on most of them.

**The time zone.** Set `TZ`. A controller on UTC runs, but its days start at 01:00 or 02:00 local time,
and the setup wizard lists the zone as a recommended fix. The zone can also be set later on
**Settings → Installation**.

**Ownership.** The controller cannot write to a directory it doesn't own. The symptom is a log line
`Could not open the charging session store`, or no files in `logs/`. `gleanvolt-init` fixes the
ownership on every start. If the controller still can't write, the share has ACLs that override the
owner (common on SMB shares): put the data on a share or dataset without them, or give uid 1654 write
access in the NAS's own permissions editor.

**The data is private.** `data/` holds the session databases, the settings saved in the web UI and the
car-account secrets (see [The data directory holds secrets](CONFIGURATION.md#the-data-directory-holds-secrets-the-secrets-section)).
Back the share up, and don't make it readable to every user on the NAS.

**Restart policy.** Keep `restart: on-failure`. With `always` or `unless-stopped`, the default in many
NAS UIs, **Stop service** in the web UI no longer stops it, because Docker starts it again at once.

## Synology (Container Manager, DSM 7.2 or later)

1. In **File Station**, create a folder for it, e.g. `docker/gleanvolt` on `volume1`.
2. **Container Manager → Project → Create.** Name it `gleanvolt`, set the path to that folder, choose
   **Create docker-compose.yml**, and paste [`docker-compose.nas.yml`](../deploy/docker-compose.nas.yml). Change `TZ`
   in it if you're not on Prague time.
3. Skip the web-portal step. The UI is on the NAS's own port 8090, not behind DSM's web portal.

`.` is the project folder here, so `GLEANVOLT_DIR` can stay as it is. If DSM's firewall is on, allow
port 8090 from the LAN.

## QNAP (Container Station 3)

1. Create a folder on a share for the data, e.g. `/share/Container/gleanvolt`.
2. **Container Station → Applications → Create.** Name it `gleanvolt` and paste the compose file.
3. Set `GLEANVOLT_DIR` to the folder's absolute path. Replace each `${GLEANVOLT_DIR:-.}` with it, because
   an application keeps its files in Container Station's own directory otherwise. Set `TZ` too.

## Unraid

Use the **Compose Manager** plugin from Community Applications. Unraid's single-container templates
can't run the one-shot ownership step.

1. **Docker → Compose → Add new stack**, named `gleanvolt`.
2. **Edit stack → Compose file:** paste the compose file.
3. **Edit stack → Env file:** add `GLEANVOLT_DIR=/mnt/user/appdata/gleanvolt` and your `TZ`. A stack's
   own folder is on the boot USB stick, which is not where data should go.
4. **Compose up.**

## TrueNAS SCALE (24.10 or later)

1. Create a dataset for it, e.g. `tank/apps/gleanvolt`, with the **Generic** preset. An SMB preset has
   ACLs that can keep the controller out.
2. **Apps → Discover Apps → ⋮ → Install via YAML.** Name it `gleanvolt` and paste the compose file.
3. Replace each `${GLEANVOLT_DIR:-.}` with the dataset's path, e.g. `/mnt/tank/apps/gleanvolt`, and set
   `TZ`. An app installed from YAML has no `.env` file to set them in.

Versions before 24.10 run apps on Kubernetes and don't take a compose file. Upgrade first.

## OpenMediaVault: the .deb instead

OpenMediaVault 7 and later is Debian 12 or newer underneath, so the native package is supported there,
with no Docker at all. Use the one-line install from
[`packaging/linux/README.md`](../packaging/linux/README.md). OpenMediaVault 6 is Debian 11, which lacks
the OpenSSL 3 the package needs. Use the compose file there, through the openmediavault-compose plugin.

The .deb is not supported on the other NAS systems. TrueNAS SCALE is Debian-based but doesn't allow
installing packages, and Synology, QNAP and Unraid aren't Debian.

## Updating on a NAS

An update means pulling the new image and recreating the controller's container. Everything that
matters is in `data/`, so nothing is lost. Over ssh, in the compose file's folder:

```bash
docker compose pull && docker compose up -d
```

In the UIs: in Synology Container Manager, update the image under **Image** (it shows *Update
available*), then rebuild the project so the container is recreated from it. In QNAP and Unraid, use the
application's or stack's update or pull. In TrueNAS, use the app's **Update** or **Pull image**.

To stay on a release rather than following `main`, set `IMAGE_TAG` to a version (e.g. `1.1.0`, with
no `v`). Changing it is also how you roll back.

**Automatic updaters** such as Watchtower work, but think about when they run. Every update restarts
the controller, and a restart returns charge control to **Off**
([why](../deploy/README.md#the-controller-restarts-so-charge-control-returns-to-off)). An update during a charge stops the
charge. Schedule it for the night, or update by hand.
