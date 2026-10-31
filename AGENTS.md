# Agent instructions

LocalExile Launcher is a Windows desktop tool for a [LocalExile](https://github.com/LocalExile/LocalExile) server
checkout. Read [README.md](README.md) and [docs/launcher.md](docs/launcher.md) first.

## Working on both repositories

The launcher drives a server checkout, usually beside this repository (`..\LocalExile`). To see both, open them
together (a VS Code workspace with both folders, or Claude Code with `--add-dir ..\LocalExile`). Each keeps its own
git history and rules: the server's are in its `AGENTS.md`.

## Rules

- **The launcher never starts a server or the game client itself.** Every start, restart and stop is a
  `scripts/dev/live.ps1` switch of the checkout (the server's rule: the client starts only through `live.ps1`, one at a
  time). Stopping the client's process or the dev host the launcher started is fine.
- **No reference to server projects.** The launcher talks to the server only through its outside-tool hooks:
  `live.ps1`, `GameServer.Gateway commands` / `values <kind>`, the dev host's JSON-lines protocol, the client control
  bridge, the logs. A feature that needs more from the server adds a small hook there first, in the server repository,
  with its own tests and docs.
- **Work from the checkout's root.** The launcher makes the chosen checkout its working directory at startup; keep its
  paths relative to that root, never absolute or searched for.
- **No server data or secrets here**: nothing from the checkout's `Client/` or `data/`, no keys, accounts or passwords.
  The launcher's own settings live in `%LOCALAPPDATA%\LocalExile\launcher.json`.
- **Write all commit messages and pull request titles and descriptions in English.**
- **Keep `dotnet build LocalExileLauncher.slnx` warning-free and `dotnet test LocalExileLauncher.slnx` passing**, and
  check a change in the running launcher against a real checkout before calling it done.
- **Keep [docs/launcher.md](docs/launcher.md) current** in the same change: what a button or tab does and how.
