---
name: gleanvolt-install
description: Install, update or reinstall Gleanvolt, the solar EV-charging controller, on a Raspberry Pi, a Debian or Ubuntu computer, a Docker host, a NAS or Windows, and check that its web UI answers on port 8090. Use when the user asks to install, set up, update, upgrade or reinstall Gleanvolt, or to find out why a fresh Gleanvolt install doesn't open.
compatibility: Runs shell commands on the machine Gleanvolt goes on (bash on Linux, PowerShell on Windows), or over ssh to it. Needs internet access to github.com and ghcr.io.
metadata:
  version: "1.3.0"
  gleanvolt-checked-against: "1.3"
  homepage: https://github.com/mpospisil/gleanvolt
  manual: https://mpospisil.github.io/gleanvolt/latest/en/
---

# Install Gleanvolt

Gleanvolt is a self-hosted controller that charges an electric car from the user's solar panels. It
talks to the inverter and the wallbox over the home network and has a web UI on port **8090**.

You install it, check that the web UI answers, and hand over to the browser. The user then sets a
password and runs the setup wizard. For that part, use the `gleanvolt-setup` skill if it is installed.
Otherwise follow the manual's
[setup wizard](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/setup-wizard/) page.

The user manual is the source of truth. Each step below links to its page. If the manual and this
skill disagree, follow the manual and tell the user that the skill is out of date.
Czech manual: replace `/en/` with `/cs/` in any link.

## Rules

- **Say what you will run before you run it,** especially anything with `sudo`. If `sudo` asks for a
  password you can't type, give the user the exact commands, complete with any `cd`, to run in a
  terminal of their own, and wait. Don't suggest your own shell passthrough (such as a `!` prefix):
  sudo has no terminal there to ask the password in. Gather the sudo commands into one hand-off where
  you can.
- **Never type a password, recovery key or API key**, and never put one in a command. The user enters
  them in the browser.
- **Don't pipe a downloaded script into a shell.** The Linux steps below download the package, verify
  its checksum and install it with apt.
- **Don't use `docker kill`** or the restart policies `always`/`unless-stopped`. See the Docker step.
- **Updating restarts Gleanvolt, and it always starts with charging Off.** If the car is charging,
  warn the user and offer to wait.

## 1. Find out where it goes

Ask the user, unless it is already clear:

1. **Which computer should run Gleanvolt?** It must stay on day and night, on the same home network as
   the inverter and the charger. If it is not the machine you are running on, ask whether you can reach
   it over ssh (`ssh user@host`). If you can't, give the user the commands and read back what they
   paste.
2. **Is this a new install, or an update of an existing one?** An update is step 6.

Then pick the path. This follows
[Which computer should I use?](https://mpospisil.github.io/gleanvolt/latest/en/installation/choosing/):

| The host | Path |
|---|---|
| Raspberry Pi (3B or newer, 64-bit OS), Debian 12+, Ubuntu 22.04+, OpenMediaVault 7 | **A. Linux package** (recommended) |
| A Linux server that already runs Docker, and the user wants a container | **B. Docker** |
| Synology, QNAP, Unraid, TrueNAS SCALE | **C. NAS** |
| Windows 10 or 11 that is never switched off | **D. Windows** |

If the user has nothing yet, recommend a Raspberry Pi 4 or 5 with Raspberry Pi OS (64-bit). On Windows,
tell them before installing that Gleanvolt runs in a terminal window and stops when the window closes,
they sign out, or Windows restarts for an update.

## 2. Check the prerequisites

Run the checks for the chosen path and report what you find before installing anything.

**Linux (A, B):**

```bash
uname -m                                   # x86_64 or aarch64. armv7l/armv6l means a 32-bit OS: stop.
getconf LONG_BIT                           # must be 64
. /etc/os-release && echo "$PRETTY_NAME"
[ -d /run/systemd/system ] && echo systemd || echo "no systemd"
ss -ltn 'sport = :8090'                    # only the header line means the port is free
timedatectl show -p Timezone --value       # the host's time zone
dpkg-query -W -f='${Version}\n' gleanvolt 2>/dev/null || echo "gleanvolt not installed"
```

- **32-bit Raspberry Pi OS** (`armv7l`, `LONG_BIT` 32): Gleanvolt won't run. The user has to reflash
  the card with **Raspberry Pi OS (64-bit)** using Raspberry Pi Imager. Stop here.
- **Port 8090 in use:** find out what has it (`sudo ss -ltnp 'sport = :8090'`). It may be an earlier
  Gleanvolt, so treat that as an update. Otherwise ask the user before going further. The Docker and NAS
  paths can use another port. The Linux package can't be moved without editing its settings, so ask the
  user to free the port.
- **Time zone `Etc/UTC` or `UTC`** on a home machine is usually a forgotten setting. Ask for theirs, e.g.
  `Europe/Prague`. On path A, offer to set it on the host:
  `sudo timedatectl set-timezone Europe/Prague`. On path B it goes into `TZ`.
- **Path A needs apt and systemd.** Without systemd (inside a container, WSL without systemd), use
  path B.

**Docker (B) also:**

```bash
docker version --format '{{.Server.Version}} {{.Server.Arch}}'   # amd64 or arm64
docker ps -a --filter name=gleanvolt                              # an existing container
```

If `docker` needs `sudo` on this host, prefix the Docker commands below with it.

**Windows (D), in PowerShell:**

```powershell
[Environment]::Is64BitOperatingSystem                          # must be True
winget --version
Get-NetTCPConnection -LocalPort 8090 -State Listen -ErrorAction SilentlyContinue   # nothing = free
```

## 3. Before installing: the devices

Ask whether **Modbus TCP is on** in the inverter and the charger (go-e: **Local HTTP API v2**). It is off
from the factory on most models, and the setup wizard can't find the devices without it. Installing can
go ahead either way. If the user doesn't know, point them to
[Preparing your inverter and charger](https://mpospisil.github.io/gleanvolt/latest/en/installation/preparing-devices/),
or let the `gleanvolt-setup` skill walk through it after the install.

## 4. Install

### A. Linux package (Raspberry Pi, Debian, Ubuntu)

Manual: [Linux and Raspberry Pi](https://mpospisil.github.io/gleanvolt/latest/en/installation/linux/).

The manual's one-line installer downloads the `.deb` for the machine's architecture, checks it against
the release's `SHA256SUMS`, and installs it with apt. Do the same steps one at a time, so each one can be
seen:

```bash
arch=$(dpkg --print-architecture)          # amd64 or arm64; armhf is a 32-bit OS: stop
base=https://github.com/mpospisil/gleanvolt/releases/latest/download
work=$(mktemp -d) && chmod 0755 "$work" && cd "$work"

curl -fsSLO "$base/SHA256SUMS"
pkg=$(grep -oE "gleanvolt_[^ *]+_${arch}\.deb" SHA256SUMS)
echo "$pkg"                                # exactly one name, e.g. gleanvolt_1.3.0_arm64.deb

curl -fsSLO "$base/$pkg"
grep -E "[ *]${pkg}\$" SHA256SUMS | sha256sum -c -    # must print "<name>: OK"

sudo apt-get update
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y --allow-downgrades \
  -o Dpkg::Options::=--force-confold "./$pkg"
```

- If `sha256sum -c` doesn't print `OK`, **stop and install nothing.** Delete the folder and try again
  later.
- To install a specific release instead of the latest, set
  `base=https://github.com/mpospisil/gleanvolt/releases/download/v1.2.0` (with the `v`).
- Download into the `mktemp -d` folder above, not into a private scratch or temp folder of your own.
  apt reads the `.deb` as its unprivileged `_apt` user, so the folder must be world-readable (`0755`).
  Otherwise apt prints a "performed unsandboxed as root" warning.
- `--force-confold` keeps an edited `/etc/gleanvolt/gleanvolt.env` on an upgrade.

The package installs a systemd service that starts at boot and restarts after a crash. Check it:

```bash
systemctl status gleanvolt --no-pager
```

Then go to step 5.

### B. Docker

Manual: [Docker](https://mpospisil.github.io/gleanvolt/latest/en/installation/docker/).

```bash
sudo mkdir -p /opt/gleanvolt/data /opt/gleanvolt/logs
sudo chown -R 1654:1654 /opt/gleanvolt/data /opt/gleanvolt/logs

docker run -d --name gleanvolt-controller --restart on-failure --init \
  --network host \
  -e TZ=Europe/Prague \
  -v /opt/gleanvolt/data:/app/data \
  -v /opt/gleanvolt/logs:/app/logs \
  ghcr.io/mpospisil/gleanvolt:latest
```

Replace `Europe/Prague` with the user's time zone. Explain these options if the user asks, and don't
drop them:

- **uid 1654** is the user Gleanvolt runs as inside the container. If the folders aren't owned by it,
  Gleanvolt can't save its settings.
- **`--network host`** puts it on the home network. Without it, the wizard's device search only sees
  Docker's own `172.x` network, and `gleanvolt.local` isn't announced.
- **`--restart on-failure`**, not `always` or `unless-stopped`: those restart it after **Stop service**
  in the web UI, so it could never be stopped from there.
- **`--init`** makes sure a failed start ends the container, so the restart policy can act.
- **`TZ`** makes each day's history start at the user's midnight.
- If port 8090 is taken, add `-e Web__Port=8091` and use that port below.

Stop it with `docker stop gleanvolt-controller`, which pauses the charger first. Never use
`docker kill`.

### C. NAS

Manual: [NAS](https://mpospisil.github.io/gleanvolt/latest/en/installation/nas/). It has a tab for each
of Synology, QNAP, Unraid and TrueNAS SCALE.

You usually can't run commands on a NAS. Guide the user through their NAS's container app instead:

1. Ask which NAS it is, and for its model or processor. A 32-bit ARM NAS can't run Gleanvolt.
2. Send them to the manual page. It shows the compose file with a copy button. The same file is in the
   repository at
   [`deploy/docker-compose.nas.yml`](https://github.com/mpospisil/gleanvolt/blob/main/deploy/docker-compose.nas.yml).
3. Help them change the three settings the page lists: `TZ`, the folder (`GLEANVOLT_DIR`, except on
   Synology, where it stays as it is) and, only if 8090 is taken, `WEB_PORT`. You can write out the
   edited lines for them to paste.
4. Walk them through their NAS's tab step by step, one step at a time.
5. Tell them a stopped container called `gleanvolt-init` is normal. It runs once at each start to set
   the folders' owner.

If the NAS offers a network choice, it must be **host**. Its restart policy must stay `on-failure`.

### D. Windows

Manual: [Windows](https://mpospisil.github.io/gleanvolt/latest/en/installation/windows/).

```powershell
winget install MartinPospisil.Gleanvolt
```

Then **the user starts it, not you.** Gleanvolt runs in the foreground of the terminal it is started
in, and would stop when your session ends. Ask them to:

1. Open a **new** terminal (so the new `gleanvolt` command is found) and type `gleanvolt`.
2. When Windows asks about the firewall, allow it on **private networks**. Without that, their phone
   can't reach it.
3. Leave that window open. Closing it stops Gleanvolt.

Without winget: download `gleanvolt-<version>-win-x64.zip` from
[the releases page](https://github.com/mpospisil/gleanvolt/releases), unzip it, and run
`Gleanvolt.Worker.exe` in the `win-x64` folder.

## 5. Check that it answers

Wait about 20 seconds after the start, then on the host:

```bash
curl -s -o /dev/null -w '%{http_code} %{redirect_url}\n' http://localhost:8090/
```

On Windows, use `curl.exe` with the same arguments.

| Answer | Meaning |
|---|---|
| `302 …/setup` | A fresh install, waiting for a password. **Done.** |
| `302 …/wizard` | A password is set, but the setup wizard isn't finished. Existing data was found. |
| `302 …/login` | An existing, configured install. Normal after an update. |
| `000` | Nothing is listening. Look at the log (below). |

If it doesn't answer, read the last lines of the log and explain what they say:

```bash
journalctl -u gleanvolt -n 60 --no-pager            # A. Linux package
docker logs --tail 60 gleanvolt-controller          # B. Docker
```

On a NAS, the log is in the container app. On Windows, it is the terminal window.
[Troubleshooting](https://mpospisil.github.io/gleanvolt/latest/en/troubleshooting/) covers the usual
causes. The most common ones: folders not owned by 1654 ("couldn't open the session store"), port 8090
already in use ("address already in use"), and a 32-bit OS.

Then find the address the user's phone or computer will use:

```bash
hostname -I | awk '{print $1}'
```

Tell the user to open **`http://gleanvolt.local:8090`**. If that doesn't open, they should use
`http://<that address>:8090`. Older Android phones, guest Wi-Fi and Docker without `--network host`
can't resolve `.local` names. On Windows, `http://localhost:8090` works on the same PC.

**Hand over.** In the browser the user sets a password of at least 8 characters, and saves the recovery
key it shows. It is shown only once. Then the setup wizard starts: suggest the `gleanvolt-setup` skill,
or the manual's [setup wizard](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/setup-wizard/)
page.

## 6. Updating an existing install

Manual: [Updating, backup and uninstalling](https://mpospisil.github.io/gleanvolt/latest/en/installation/updating/).
Settings and history are kept on every path.

1. Ask whether the car is charging. After an update charging is **Off**, so the user has to choose the
   mode again. Offer to wait until the charge is done.
2. Offer a backup first. The `data` folder holds the sign-in to the car's account, so the backup stays
   in the user's home folder, never in a shared folder.
   - A: `sudo tar czf ~/gleanvolt-backup.tar.gz /var/lib/gleanvolt /etc/gleanvolt`
   - B: `sudo tar czf ~/gleanvolt-backup.tar.gz /opt/gleanvolt/data`
3. Update:
   - **A. Linux package:** repeat step 4A. apt replaces the package and restarts the service.
   - **B. Docker:** first read the options the container was started with:
     `docker inspect gleanvolt-controller --format '{{.HostConfig.RestartPolicy.Name}} {{.HostConfig.NetworkMode}} {{.Config.Env}} {{.HostConfig.Binds}}'`.
     Then run `docker pull ghcr.io/mpospisil/gleanvolt:latest` and
     `docker rm -f gleanvolt-controller`, and repeat step 4B's `docker run` with the same port,
     folders and `TZ`. If the old container used `always`/`unless-stopped`, or no `--network host`, say
     so and use the options from 4B.
   - **C. NAS:** the NAS tab of the manual page: update the image, then rebuild or redeploy the project.
   - **D. Windows:** the user closes Gleanvolt's window. Copy the `data` folder somewhere safe (it is in
     `%LOCALAPPDATA%\Microsoft\WinGet\Packages\`, in the folder starting with
     `MartinPospisil.Gleanvolt`), run `winget upgrade MartinPospisil.Gleanvolt`, and the user starts
     `gleanvolt` again. If the settings are gone, copy `data` back next to the program.
4. Check it as in step 5. The answer should be `302 …/login`.
5. Remind the user to choose a charging mode again if the car needs charging.

## What to report at the end

A short summary for the user:

- where Gleanvolt is installed, how, and which version (`dpkg-query -W gleanvolt`, or the image tag);
- the address to open;
- how to stop, start and read its log on this path;
- what is next: the password page, then the setup wizard.
