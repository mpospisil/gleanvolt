# Notes for AI coding agents

These instructions are for an AI assistant working in this repository. People contributing by hand
should read [CONTRIBUTING.md](CONTRIBUTING.md).

## Workflow and project management
You are authorized and expected to use the GitHub CLI (`gh`) to manage this project. 
When asked to manage tasks or submit code, use the following commands:
- `gh issue list`: To check current tasks.
- `gh issue view <id>`: To read the requirements of a specific task.
- `gh issue create -t "<title>" -b "<body>"`: To create new tasks.
- `gh pr create -t "<title>" -b "<body>"`: To submit your implemented code for review.
Do not use `git push` directly to the main branch; always create a branch and use `gh pr create`.

## Keeping the project notes
All project notes live in the `docs/` directory. You are responsible for keeping them updated:
1. `DECISIONS.md`: Append a record when we adopt a library or establish a core pattern — and when hardware verification contradicts a planned design, which is the more common case here. Include what was found, what was decided, and the consequences accepted.
2. `IMPLEMENTATION_LOG.md`: Before submitting a Pull Request via `gh pr create`, you MUST add a reverse-chronological entry to the top of this file detailing the implementation specifics, hardware quirks encountered (e.g. Modbus limitations), and the files changed. Use this entry to generate a detailed PR body that explains the architecture decisions, not just the changed files.
