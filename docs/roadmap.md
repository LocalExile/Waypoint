# Launcher roadmap

What to build next, from the first round of feedback on the launcher. Each item says where it stands, what it needs
from the server (a hook in the server repository, per [AGENTS.md](../AGENTS.md)) and what is still undecided.

To pick an item up, mark it **in progress** with your name or PR here; when it ships, describe it in
[launcher.md](launcher.md) and remove it from this file in the same change.

Order: the new look (built, being checked), then branches and pull requests, the riskiest item; the collaboration
items can happen at any time.

## 1. The new look: dark mode and one row per service

**In progress**: built, waiting for a check on a real setup. Material Design controls in blue and white, Light, Dark and
System themes, a sidebar instead of tabs, and the Control section as a table of services with one Start/Stop button
each; [launcher.md](launcher.md) ("Look", "Control") describes it. Checked: every section in both themes against a real
checkout, and the published single-file exe. No server change.

Left to do:

- Press each row's button against a real setup: start and stop Patch, Auth and Gateway alone (`-Only`, `-Stop -Only`),
  the client (Start game only, close), the dev host, stopping the walk-map recorder, and a server's Restart; then Play,
  Stop all and Stop servers as before. Then this item leaves the roadmap.

## 2. Pick a branch or a pull request

Choose what the checkout runs: a branch of the server repository, or an open pull request. The most requested item,
and the one that can lose work or run foreign code, so it needs these guards:

- A checkout with uncommitted changes is never switched silently: offer to stash them, or refuse.
- The servers and the game stop (`live.ps1 -Stop`) before a checkout.
- Pull requests from forks run someone else's code on this machine: say so and ask for a separate confirmation.
- No `gh` dependency: list branches and pull requests through the public GitHub API, fetch a pull request with
  `git fetch origin pull/<n>/head:pr-<n>`. A **Back to main** button returns to the default branch.
- Game data needs nothing special: `live.ps1` runs `extract-data.ps1`, whose manifest regenerates what the branch
  changed.

Switches the one checkout in place. A worktree per branch would leave the main checkout alone, but each needs its own
`data/` and `Client/`; not planned.

## 3. Collaboration

- Decide with the LocalExile maintainers whether the repository moves to the LocalExile organisation. If it does, change
  the release lookup (`Updates.cs`, now `jetstream1128/LocalExile-Launcher`) and publish a release before relying on
  GitHub's redirect.
- `CONTRIBUTING.md`: the rules from [AGENTS.md](../AGENTS.md), how to test against a real checkout, and that releases
  come only from a maintainer's tag.
- Protect `main`: changes arrive through reviewed pull requests.

## 4. Ideas, not planned yet

- Server timings: message names instead of hex ids (the server has no complete id-to-name table yet), a reset button,
  CSV export for bug reports.
