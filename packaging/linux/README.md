# Installing on Linux: the .deb package

Gleanvolt as a native systemd service on **Debian, Ubuntu and Raspberry Pi OS (64-bit)**, with no
Docker and no .NET installation: the runtime is inside the package. It installs **the controller and
its web UI only**. No MQTT broker or Home Assistant is installed or needed; to connect one, see
[Gleanvolt with Home Assistant](../../docs/HOME_ASSISTANT.md).

## Install

```bash
curl -fsSL https://github.com/mpospisil/gleanvolt/releases/latest/download/install.sh | sudo sh
```

The script checks the architecture, downloads the matching `.deb` from the latest release, verifies it
against the release's `SHA256SUMS`, and installs it with apt. The service starts straight away, with
the default configuration, and the script prints where the web UI is:

```
  Web UI     http://192.168.2.7:8090   (plain HTTP; a new install opens on the setup wizard)
  Settings   /etc/gleanvolt/gleanvolt.env
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
| OpenMediaVault 7 and later (Debian 12+) | ✅ | ✅ |
| OpenMediaVault 6 (Debian 11: no OpenSSL 3) | ❌ | ❌ |

**32-bit Raspberry Pi OS is not supported**, and the installer says so rather than failing half way.
Other NAS systems (Synology, QNAP, Unraid, TrueNAS SCALE) run the container image instead, see
[Running on a NAS](../../deploy/README.md#running-on-a-nas).
A Pi 3B or newer runs the 64-bit OS; reinstall it with Raspberry Pi Imager. Other distributions can
run the self-contained `linux-x64` / `linux-arm64` zip from the same release.

## What the default configuration does

Nothing is written to your hardware. Out of the box:

- the **web UI** is on at port 8090, plain HTTP. It requires a login, and until a password exists it
  serves only the setup wizard, whose first step sets one;
- **charge control** starts in mode **Off**, and touches the charger only when a mode is chosen in the UI;
- the **battery discharge hold** is disabled;
- **Home Assistant, MQTT and the vehicle feeds** are off;
- the **Solcast forecast and weather** are off until you give them an API key;
- the **inverter and charger addresses** are not set, so Gleanvolt starts in **setup mode**: the web
  UI shows the setup wizard, and nothing is polled or controlled until the setup is finished and the
  service restarted. The wizard finds the devices on your network for you. No browser? See
  [Setting it up from the command line](#setting-it-up-from-the-command-line).

## Settings

**Set it up in the web UI.** Open `http://<device>:8090`: the setup wizard sets the password, finds the
inverter and the charger, and asks for the site and the optional extras. Later changes go on the web
UI's **Settings** and **Car** pages, applied with **Restart** on **Settings → Health**. That is the
recommended way; the values are checked as you save them. See
[The setup wizard](../../docs/WEB_UI.md#the-setup-wizard).

Forgot the password? Use **Forgot your password?** on the sign-in page, or `set-password` from the
[command line](#setting-it-up-from-the-command-line).

### Optional: the settings file

Any setting in the [configuration reference](../../docs/CONFIGURATION.md) can also go in
**`/etc/gleanvolt/gleanvolt.env`**, one `KEY=VALUE` per line with `__` (two underscores) between the
sections — for settings the web UI doesn't cover, or to prepare an installation without a browser.
What is saved in the web UI wins over this file, key by key.

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

The file is readable only by root and the service, and upgrades never overwrite it. The web password
can be set here ahead of time, as a hash:

```bash
/opt/gleanvolt/Gleanvolt.Worker hash-password 'your password'
# then in gleanvolt.env:  Web__PasswordHash=<the output>
```

## Where things are

| | |
|---|---|
| `/opt/gleanvolt/` | the program: replaced on every upgrade, never edit it |
| `/etc/gleanvolt/gleanvolt.env` | your settings |
| `/var/lib/gleanvolt/` | the data: `sessions.db` (charging-session history), `energy.db`, what was saved on Settings (`pv-system.json`, `tariff.json`), vehicle sign-in files. Back this up — **carefully**, see below |
| `/var/log/gleanvolt/` | the log files, one per day, kept 14 days |
| `journalctl -u gleanvolt` | the same log in the system journal. On a Raspberry Pi the journal is lost at reboot; the files above are not |

### The data directory holds secrets

`/var/lib/gleanvolt` (owner `gleanvolt`, mode `0750`) holds the car's sign-in — a volkswagen.de
session, a MyŠkoda API key — and the keys entered in the web UI, each file mode `0600`. **That is a file
permission, not encryption**: a copy of the directory hands over the car. Back it up onto an encrypted
volume, never into a shared drive or an issue attachment. **Sign out** on the web UI's Car page deletes
the stored car sign-in. See
[The data directory holds secrets](../../docs/CONFIGURATION.md#the-data-directory-holds-secrets-the-secrets-section).

## Setting it up from the command line

The web UI's setup wizard is the main route. Without a browser, the same setup runs from a shell, as the
service user (the command refuses anyone else, since the service could not read what they wrote):

```bash
sudo -u gleanvolt /opt/gleanvolt/Gleanvolt.Worker setup                      # asks for each setting
sudo -u gleanvolt /opt/gleanvolt/Gleanvolt.Worker setup --file /var/lib/gleanvolt/site.json
sudo -u gleanvolt /opt/gleanvolt/Gleanvolt.Worker set-password               # forgot the password
sudo systemctl restart gleanvolt
```

It reads the unit's `Environment=` lines and `/etc/gleanvolt/gleanvolt.env`, so it saves where the
service looks. A setup file holding secrets must not be world-readable (`chmod 600`). See
[Setting up from the command line](../../docs/WEB_UI.md#setting-up-from-the-command-line) for the
file's shape.

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
