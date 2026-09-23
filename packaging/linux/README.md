# Installing on Linux: the .deb package

Gleanvolt as a native systemd service on **Debian, Ubuntu and Raspberry Pi OS (64-bit)**, with no
Docker and no .NET installation: the runtime is inside the package. It installs **the controller and
its web UI only**. No MQTT broker or Home Assistant is installed or needed; for that stack, see
[`deploy/`](../../deploy/README.md).

## Install

```bash
curl -fsSL https://github.com/mpospisil/gleanvolt/releases/latest/download/install.sh | sudo sh
```

The script checks the architecture, downloads the matching `.deb` from the latest release, verifies it
against the release's `SHA256SUMS`, and installs it with apt. The service starts straight away, with
the default configuration, and the script prints where the web UI is:

```
  Web UI     http://192.168.2.7:8090   (plain HTTP, no login until you set one)
  Settings   /etc/gleanvolt/gleanvolt.env   -- set your inverter and charger addresses first
```

If you would rather not pipe a script into a root shell, download the package for your architecture
from the [releases page](https://github.com/mpospisil/gleanvolt/releases) and install it yourself:

```bash
sudo apt install ./gleanvolt_1.0.7_arm64.deb
```

### Supported systems

| Distribution | amd64 | arm64 |
|---|---|---|
| Raspberry Pi OS 64-bit (Bookworm, Trixie) | – | ✅ |
| Debian 12 / 13 | ✅ | ✅ |
| Ubuntu 22.04 / 24.04 and later | ✅ | ✅ |
| Distributions based on these (Mint, DietPi, Armbian, …) | ✅ | ✅ |

**32-bit Raspberry Pi OS is not supported**, and the installer says so rather than failing half way.
A Pi 3B or newer runs the 64-bit OS; reinstall it with Raspberry Pi Imager. Other distributions can
run the self-contained `linux-x64` / `linux-arm64` zip from the same release.

## What the default configuration does

Nothing is written to your hardware. Out of the box:

- the **web UI** is on at port 8090, plain HTTP, **with no login**: anyone on your LAN can change
  the charging mode. See [Settings](#settings) to set a password;
- **charge control** starts in mode **Off**, and touches the charger only when a mode is chosen in the UI;
- the **battery discharge hold** is disabled;
- **Home Assistant, MQTT and the vehicle feeds** are off;
- the **Solcast forecast and weather** are off until you give them an API key;
- the **inverter and charger addresses** are the reference installation's, `192.168.2.10` and
  `192.168.2.6`. On your network nothing answers there, so the dashboard stays empty until you set
  yours. That is the first thing to change.

## Settings

Everything is in **`/etc/gleanvolt/gleanvolt.env`**, one `KEY=VALUE` per line. Any setting in the
[README's configuration reference](../../README.md#configuration) can go here, with `__` (two
underscores) between the sections:

```bash
sudo nano /etc/gleanvolt/gleanvolt.env
```

```ini
Pv__Inverter__Host=192.168.1.50
Pv__Chargers__0__Host=192.168.1.51
Solcast__ApiKey=...
```

Then restart and check it came up:

```bash
sudo systemctl restart gleanvolt
systemctl status gleanvolt
```

The file is readable only by root and the service, because API keys and the web password hash live in
it. Upgrades never overwrite it.

**To put a password on the web UI**, generate the hash and set it:

```bash
/opt/gleanvolt/Gleanvolt.Worker hash-password 'your password'
# then in gleanvolt.env:  Web__PasswordHash=<the output>
```

## Where things are

| | |
|---|---|
| `/opt/gleanvolt/` | the program: replaced on every upgrade, never edit it |
| `/etc/gleanvolt/gleanvolt.env` | your settings |
| `/var/lib/gleanvolt/` | the data: `sessions.db` (charging-session history), `energy.db`, vehicle sign-in files. Back this up — **carefully**, see below |
| `/var/log/gleanvolt/` | the log files, one per day, kept 14 days |
| `journalctl -u gleanvolt` | the same log in the system journal. On a Raspberry Pi the journal is lost at reboot; the files above are not |

### The data directory holds secrets

`/var/lib/gleanvolt` is owned by the `gleanvolt` user and is mode `0750`, and two files in it are
passwords in all but name ([issue #215](https://github.com/mpospisil/gleanvolt/issues/215)):

| | |
|---|---|
| `vw-website-session.json` | a live volkswagen.de session — whoever holds it is signed in as you, with no password and no emailed code |
| `skoda-api-key.json` | the MyŠkoda API key — reads the car, and starts and stops charging |

Both are written mode `0600`, which is what the startup log and `/health` report:
`owner-only files (0600) in the data directory`. **That is a file permission, not encryption**, and
the controller never claims otherwise — it has to come back from a restart with nobody present, so
anything it can undo unattended, `root` can undo too.

So back the directory up, but back it up the way you would back up a password file: onto an encrypted
volume, not into a shared drive, an issue attachment or a support bundle. A copy of
`/var/lib/gleanvolt` hands over the car. Against a stolen SD card the effective answer is full-disk
encryption, which is yours to arrange and not something this package can do for you.

To stop handing it over, use **Sign out** on the web UI's Car page: it deletes the stored
value rather than leaving a tombstone holding it.

## Running it

```bash
systemctl status gleanvolt          # is it running, and since when
sudo systemctl stop gleanvolt       # stop it: the charger is released first
sudo systemctl start gleanvolt
sudo systemctl restart gleanvolt    # after changing settings
journalctl -u gleanvolt -f          # follow the log
```

It starts at boot and is restarted if it crashes. **Stop service** in the web UI stops it and it
**stays stopped**, including across an upgrade, until `sudo systemctl start gleanvolt`. A reboot
starts it again. This is the same exit-code contract the Docker deployment uses; see
[Stopping and starting the controller](../../deploy/README.md#stopping-and-starting-the-controller).

## Upgrade, pin, roll back

Run the installer again. Settings and data are kept, and a service that was running is started again
on the new version:

```bash
curl -fsSL https://github.com/mpospisil/gleanvolt/releases/latest/download/install.sh | sudo sh
```

A specific version, which is also how you roll back:

```bash
curl -fsSL https://github.com/mpospisil/gleanvolt/releases/latest/download/install.sh | sudo GLEANVOLT_VERSION=1.0.6 sh
```

## Uninstall

```bash
sudo apt remove gleanvolt   # removes the program; keeps settings, data and logs for a reinstall
sudo apt purge gleanvolt    # removes everything, including the charging history
```

---

## For maintainers: building and testing the package locally

`release.yml` builds, installs, smoke-tests and purges the package on both architectures on every
run, so this is only for working on the packaging itself.

```bash
# 1. A self-contained publish at the fixed path nfpm.yaml reads
dotnet publish src/Gleanvolt.Worker/Gleanvolt.Worker.csproj -c Release -r linux-x64 \
  --self-contained true -o publish/deb
chmod -R u=rwX,go=rX publish/deb

# 2. The package (nfpm: https://nfpm.goreleaser.com/install/)
GLEANVOLT_VERSION=1.0.0~dev GLEANVOLT_ARCH=amd64 \
  nfpm package --config packaging/linux/nfpm.yaml --packager deb --target deb/

# 3. Install it somewhere systemd runs. A throwaway container works:
docker run -d --name gv --privileged --cgroupns=host -v /sys/fs/cgroup:/sys/fs/cgroup:rw \
  -v "$PWD/deb:/deb:ro" <an image with systemd as PID 1>
docker exec gv apt-get install -y /deb/gleanvolt_1.0.0~dev_amd64.deb
```

The version uses `~dev`, not `-dev`: dpkg sorts `1.0.7~dev` below `1.0.7`, so a test build never
outranks the release that follows it.

`PackagingTests` guards the one list here that drifts: every `…Path` setting in `appsettings.json`
must be pointed at `/var/lib/gleanvolt` or `/var/log/gleanvolt` by `gleanvolt.service`, because the
content root in `/opt/gleanvolt` is read-only to the service.
