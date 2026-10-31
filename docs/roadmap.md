# Roadmap

What to build next. Each item says where it stands, what it needs from the server (a hook in the server repository, per
[AGENTS.md](../AGENTS.md)) and what is still undecided.

To pick an item up, mark it **in progress** with your name or PR here; when it ships, describe it in
[launcher.md](launcher.md) and remove it from this file in the same change.

Order: the collaboration items, then Simple mode, then isolated environments, then the ideas.

## 1. Collaboration

- Protect `main`: changes arrive through reviewed pull requests.
- Ask the LocalExile maintainers to mark good commits, with a moving `stable` tag or releases, and have Simple mode's Play
  follow that. It follows `main` now, which changes many times a day with no test results, so a player can get a bug
  inside the game.

## 2. Simple mode

- Leagues on Simple mode's sections, from JetStream's plan for one simple view.

## 3. Isolated environments

From the first Waypoint's roadmap, not started:

1. Persisted environment manifests separating repository/ref/commit, built package, prepared client, generated data, keys,
   configuration, logs and saves.
2. Separate source checkouts and builds for a chosen branch or pull request, with immutable build metadata. Selecting source
   must never be confused with selecting a built binary.
3. Independent test environments with separate keys and client trust; verified readiness, rollback, resumable preparation,
   disk-space reporting, and restore with a safety backup.
4. A versioned upstream control protocol in place of parsing `live.ps1`: machine-readable readiness, shutdown and runtime
   identity. Needs a hook in the server.

## 4. Ideas, not planned yet

- The setup guide opens download pages for a missing .NET, Git, Python or Node; it could install them with `winget`.
- Server timings: message names instead of hex ids (the server has no complete id-to-name table yet), a reset button,
  CSV export for bug reports.
- Further account tools beyond the offline manager, such as character editing and recovery.
- An installer and code signing.
