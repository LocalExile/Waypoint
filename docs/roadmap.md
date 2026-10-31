# Launcher roadmap

What to build next, from the first round of feedback on the launcher. Each item says where it stands, what it needs
from the server (a hook in the server repository, per [AGENTS.md](../AGENTS.md)) and what is still undecided.

To pick an item up, mark it **in progress** with your name or PR here; when it ships, describe it in
[launcher.md](launcher.md) and remove it from this file in the same change.

Order: server timings (started), dark mode, the service list layout, branches and pull requests. Branches come last
because they are the riskiest; the collaboration items can happen at any time.

## 1. Server timings: what each Gateway operation costs

**In progress** (server hook written, not yet committed; launcher side not started).

The Performance tab shows how long the Gateway spends on each operation, as charts and a table over a sliding window
(the last 60 seconds by default): calls per second, average, p95 and p99 per operation.

- Server hook (server repository, `feat/launcher-support`): the `LocalExile.Gateway` meter's
  `localexile.gateway.duration` histogram (ms, tags `phase` and `operation`) gains the `Message` phase, one sample per
  client message with its id as the operation (`0x0025`), and `LevelGen`, one sample per generated area with its area
  id. The other phases (input queue, handler batch, send and wire gates, combat lock, surround, blocker build) were
  already there.
- Launcher: subscribe to the `System.Diagnostics.Metrics` event source for `LocalExile.Gateway` in the same diagnostics
  session as the runtime counters (`RuntimeCounters.cs`), with a refresh interval of 1 s and limits high enough for one
  histogram per message id. Keep each interval's count, sum and quantiles per (phase, operation) and build the window
  from them. The event source publishes p50/p95/p99 per interval, not a maximum, so the window's p95 is the highest
  per-interval p95 (stated in the tab).
- Undecided: message names instead of hex ids (the server has no complete id-to-name table yet); a reset button; CSV
  export for bug reports.

## 2. Dark mode

Light, Dark and System (the Windows app theme, `AppsUseLightTheme`), switched on the Control tab and remembered in the
settings. No server change.

- Move the colours hard-coded in `App.xaml` and the tabs into named brushes used through `DynamicResource`, with one
  dictionary per theme.
- `LineChart` draws in code: its background, grid and text colours come from the theme too.

## 3. Service list with one button per service (XAMPP-style)

The Control tab becomes a list of services (Patch, Auth, Gateway, dev host, client, walk-map recorder), each row with
its state and a single Start/Stop button that follows the state; restart and the service's log are in the row's
context menu. A large Start all / Stop all button sits at the top; the `live.ps1` switches (skip build, skip data
check, hot reload, walk maps) move into one settings row.

No server change: `live.ps1 -Only <server>` starts or restarts one server and `-Stop -Only <server>` stops one.

## 4. Pick a branch or a pull request

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

## 5. Collaboration

- Decide with the LocalExile maintainers whether the repository moves to the LocalExile organisation. If it does, change
  the release lookup (`Updates.cs`, now `jetstream1128/LocalExile-Launcher`) and publish a release before relying on
  GitHub's redirect.
- CI: build and test every pull request (`dotnet build` warning-free, `dotnet test`).
- `CONTRIBUTING.md`: the rules from [AGENTS.md](../AGENTS.md), how to test against a real checkout, and that releases
  come only from a maintainer's tag.
- Protect `main`: changes arrive through reviewed pull requests.
