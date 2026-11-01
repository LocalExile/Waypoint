# Agent instructions

Waypoint is a Windows desktop tool for a [LocalExile](https://github.com/LocalExile/LocalExile) server checkout. Read
[README.md](README.md) and [docs/launcher.md](docs/launcher.md) first; what to build next is in
[docs/roadmap.md](docs/roadmap.md).

## Working on both repositories

Waypoint drives a server checkout, usually beside this repository (`..\LocalExile`). To see both, open them together (a
VS Code workspace with both folders, or Claude Code with `--add-dir ..\LocalExile`). Each keeps its own git history and
rules: the server's are in its `AGENTS.md`.

## Rules

- **Waypoint never starts a server or the game client itself.** Every start, restart and stop on the Control section is a
  `scripts/dev/live.ps1` switch of the checkout (the server's rule: the client starts only through `live.ps1`, one at a
  time). Stopping the client's process or the dev host Waypoint started is fine. A release install has no `live.ps1`: there
  every start and stop is its own `LocalExile.exe --json` (the release's rule: the client starts only through it). A Gateway
  ignores a listing mode it doesn't know and starts for real, so a new mode needs a check that the build has it first
  (`CrafterCatalog.HasCrafter`), on a release's too.
- **No reference to server projects.** Waypoint talks to the server only through its outside-tool hooks: `live.ps1`,
  `GameServer.Gateway commands` / `values <kind>` / `crafter`, the dev host's JSON-lines protocol, the client control
  bridge, the logs. The Versions section also runs git in the checkout and reads GitHub's public API; the adapter in `scripts/` reads
  and writes the checkout's saves and mod choices, with the guards in [docs/runtime-contract.md](docs/runtime-contract.md).
  A feature that needs more from the server adds a small hook there first, in the server repository, with its own tests
  and docs.
- **One thing holds the checkout at a time.** A `live.ps1` run, git work and an adapter operation share one lock
  (`Shared.RunExclusiveAsync`); only reading (inspection, the account list) runs beside them.
- **A switch of the checkout never loses work.** It backs the saves up, stashes uncommitted changes, stops what runs, asks
  before running a fork's pull request, and refuses what would drop a local commit; keep those guards when changing
  `VersionsTab`.
- **Waypoint moves a checkout by itself only in Simple mode, and only a plain copy of LocalExile**
  (`CheckoutUpdates.PlainCopy`): anything of the player's own stays as it is, and Advanced mode never moves one. A release
  install is updated only when the player accepts the prompt, with the game closed; a zip that names `data\` (other than
  `data\schema\`) or `Client\` is refused.
- **Saves are never overwritten in place.** Restoring or starting fresh moves the current database aside first, and a
  change to accounts or mods is backed up unless the player turned that off. Never force-kill a Gateway as an automatic
  fallback; it needs the chance to save. A release and a source copy never share one database: bringing saves copies them
  with the online backup, because a stopped release still has saves in its WAL.
- **Work from the checkout's root.** Waypoint makes the chosen checkout its working directory at startup; keep its paths
  relative to that root, never absolute or searched for. Searching happens only when the player asks for it.
- **No server data or secrets here**: nothing from the checkout's `Client/` or `data/`, no keys, accounts or passwords.
  Waypoint's own settings live in `%LOCALAPPDATA%\Waypoint\settings.json`.
- **All interface text goes through `L.Text` or `{l:Loc '…'}`** with a Russian translation in the same change
  ([docs/localization.md](docs/localization.md)); `tools/missing-translations.py` finds what is missing. Logs, commands,
  paths and protocol values stay as they are.
- **Colours come from the theme**: `{DynamicResource Shell.*}` in views, `Theme.Brush` in code. A colour written into a view
  would stay the same in every scheme.
- **Write all commit messages and pull request titles and descriptions in English.**
- **Keep `dotnet build Waypoint.slnx` warning-free, and `dotnet test Waypoint.slnx`, `tests/Launcher.Checks` and the
  adapter's `tests/*.Tests.ps1` passing**, and check a change in the running app against a real checkout before calling it
  done.
- **Keep [docs/launcher.md](docs/launcher.md) current** in the same change: what a button or section does and how. A shipped
  [roadmap](docs/roadmap.md) item leaves the roadmap in that change.
- **Releases come only from a `v<major>.<minor>.<patch>` tag** (the release workflow builds and publishes them). Never
  attach a locally built exe to a release: the self-update trusts the published SHA-256 next to it. Push the one tag by name,
  never `git push --tags`: every `v*` tag that reaches the repository publishes at once. The workflow refuses a tag that is not
  on `main` or not above the latest release (`tools/check-release-tag.ps1`), but it runs as it was in the tagged commit, so a
  tag on a commit older than that check is not stopped.
