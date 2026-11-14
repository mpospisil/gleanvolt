# Agent skills

Two [Agent Skills](https://agentskills.io) for AI agents that read `SKILL.md` (Claude Code, Codex CLI,
Cursor, Gemini CLI and others). They are also listed on [Agensi](https://www.agensi.io) (#390).

| Skill | What it does |
|---|---|
| [`gleanvolt-install`](gleanvolt-install/SKILL.md) | Picks the install path for the host (Linux package, Docker, NAS, Windows), checks the prerequisites, installs or updates, and checks that the web UI answers on port 8090 |
| [`gleanvolt-setup`](gleanvolt-setup/SKILL.md) | Prepares the inverter and charger, walks the user through the password page and the setup wizard, and ends with a health check on the Overview |

The [user manual](https://mpospisil.github.io/gleanvolt/latest/en/) is the source of truth. The
skills link to it for each step and don't replace it. The agent never types the password or an API
key, and the skills contain no scripts: every command is written out in `SKILL.md`.

## Using one without Agensi

Copy the skill's folder into your agent's skills folder, e.g. for Claude Code:

```bash
cp -r skills/gleanvolt-install skills/gleanvolt-setup ~/.claude/skills/
```

Then ask the agent to "install Gleanvolt on this machine".

## Changing a skill

- Keep each skill self-contained: one folder, `SKILL.md` at its root, no hidden files, no symlinks, no
  scripts.
- Link to the manual under `https://mpospisil.github.io/gleanvolt/latest/`, English pages.
- `metadata.version` is the Gleanvolt release the skill was last checked against.
- The manual's build lints this folder (`docs/manual/scripts/check-skills.mjs`): the frontmatter, the
  name and description limits, and every manual link against the built manual.
- Re-publishing to Agensi is a release step: see [Releasing](../docs/RELEASING.md#agent-skills).
