# A clean machine for trying the agent skills

A throwaway Debian 12 machine in a container, for running the skills in [`skills/`](../../skills/)
(#390) the way a user would. systemd runs inside it, so `gleanvolt-install` takes the real Linux path
(the `.deb` as a service), as on a Pi. It has the host's network, so the LAN, the devices and port 8090
are reachable.

```bash
dev/skill-test/run.sh                         # Debian 12
BASE=ubuntu:24.04 dev/skill-test/run.sh       # or another base
```

Each run removes the previous machine and starts from a clean one. It ends in a shell as `tester`
(password `tester`, in `sudo`). The skills are mounted read-only at `/skills`.

## Gemini CLI

Node.js 22 is in the image, because Debian 12's own is too old. Then:

```bash
npm install -g @google/gemini-cli
mkdir -p ~/.gemini/skills && cp -r /skills/gleanvolt-* ~/.gemini/skills/
gemini
```

To skip the browser sign-in, start `run.sh` with `GEMINI_API_KEY` set; it is passed into the container.
In Gemini, `/skills` should list both. If your version doesn't load skills from there, tell it
"read /skills/gleanvolt-install/SKILL.md and follow it". Then ask it to "install Gleanvolt on this
machine".

## What it shares with the host

- **`--privileged`**, which systemd needs. The units that could reach the host through it are masked in
  the image: the clock, udev, networkd, resolved, the consoles.
- **The network.** Gleanvolt listens on the host's port 8090 and announces itself over mDNS. With the
  Pi on the LAN, it is `gleanvolt-2.local`.
- **The devices.** Installing is harmless: Gleanvolt starts in setup mode and talks to nothing. **The
  setup skill is not.** The wizard's search and Save connect to the inverter and the charger. After
  **Restart**, a second controller polls them. The charger serves one Modbus client, so this
  knocks the Pi off. For the setup run, stop the controller on the Pi first, or stop at the Devices
  step. Skip the Solcast step: it spends the Pi's daily quota.

Remove the machine with `docker rm -f gleanvolt-skill-test`.
