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

## Claude Code

Node.js 22 is in the image, because Debian 12's own is too old. The first time:

```bash
npm install -g @anthropic-ai/claude-code
```

Claude Code is kept in the volume `gleanvolt-skill-test-npm` (`~/.npm-global`), so later runs already
have it. `npm update -g` updates it, and `docker volume rm gleanvolt-skill-test-npm` removes it. Gleanvolt
doesn't use Node, so this doesn't change what the skills find on the machine.

On every run, copy the skills from `/skills`, which is the repository's folder, into Claude Code's
folder. Then start Claude Code:

```bash
mkdir -p ~/.claude/skills && cp -r /skills/gleanvolt-* ~/.claude/skills/
claude
```

**Signing in.** Choose your Claude account. There is no browser in the container, so Claude Code prints
a URL: open it on the host, sign in, and paste the code back. The sign-in is kept in the volume
`gleanvolt-skill-test-claude` across runs, so this is needed once. `docker volume rm
gleanvolt-skill-test-claude` forgets it. With an API key instead, start `run.sh` with
`ANTHROPIC_API_KEY` set; it is passed into the container.

`/skills` in Claude Code lists both. Then ask it to "install Gleanvolt on this machine".

**The second terminal.** Claude Code can't type the sudo password, so expect the skill to hand you the
`sudo` commands. That is part of what is being tested. Run them in a second terminal attached to the
machine:

```bash
docker exec -it -u tester gleanvolt-skill-test bash -l
```

Attach it **after each `run.sh`**. A terminal attached to the previous machine ends when `run.sh`
removes that machine, and drops back to the host's shell. Commands typed there then run on the host,
and `hostname -I` can't tell the two apart, because the machine shares the host's network. **Check
the prompt before running anything**: `tester@gleanvolt-test`, not your host's name.

**Gemini CLI** no longer signs in with a personal Google account, a paid one included: Google ended
that on 2026-06-18. It still works with a Gemini API key, if it is installed and given
`GEMINI_API_KEY` by hand.

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

Remove the machine with `docker rm -f gleanvolt-skill-test`. The two volumes, the sign-in and Claude
Code, are kept until you remove them with `docker volume rm`.
