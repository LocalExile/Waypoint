# LocalExile repository discovery

With no selected path, the launcher opens Settings at **Find your LocalExile checkout** and begins lookup in the
background. **Browse for a different location…** is available immediately. **Change location** is always visible in
the header. A saved checkout is reused; a missing/invalid saved location opens Settings and offers discovery without
silently replacing it. Find LocalExile is also available from Setup and Settings.

The public upstream query is `git ls-remote --symref https://github.com/LocalExile/LocalExile.git HEAD`. It reads the
default branch and its advertised HEAD, without fetching into a local repository, switching branches or changing
remotes. There is no GitHub sign-in flow. The checked branch, full commit and check time appear with the results.

Directory discovery checks launcher/workspace neighbors, common user source folders and shallow fixed-drive roots.
It identifies folders by GameServer.slnx, scripts/dev/live.ps1 and the Core/Gateway project files, independently of
the folder's name. It skips junctions/reparse points and generated/game/system directories. Search is bounded by
depth, 6,000 directory visits, 50 results and six seconds of directory scanning. Git inspection has separate timeouts.
This is a search of likely locations, not a guarantee that every checkout on every disk was found.

For candidate folders with Git metadata, the launcher reads actual HEAD, branch/detached state and tracked-file
changes with optional Git locks disabled. Both normal clones and linked worktrees are supported. It confirms HEAD
again after inspection, so a checkout changing commits during the lookup is not reported with an inconsistent match.
Forks are allowed: origin does not need to point to upstream. Source folders without readable Git metadata can still
be chosen, but have no verified commit comparison. A matching HEAD is distinct from unchanged tracked files or a
ready client/build; normal setup checks run after selection.

Exactly one verified match from a completed lookup is automatically selected only when no location was saved and
the user has not begun a manual choice. The result says **Found and using…**, with an immediate way to change it.
Multiple matches, partial searches and no matches leave the choice to the user. Older/other checkouts remain listed.
An offline or failed upstream query retains local results and manual browsing, and never claims a verified match.

Manual browsing immediately selects and checks a valid folder. The typed-path field and Save and check remain
available. Location changes wait for inspection to finish and are refused during a runtime mutation. A selection
clears stale runtime/build information before checking the new installation. Discovery cannot replace a location
chosen while lookup was in progress. Closing the launcher cancels its disposable discovery/Git queries.

## Validation, 30 October 2026

The executable checks cover renamed/nested folders, overlapping roots, generated-directory exclusion, depth/count
limits, cancellation, default-branch/HEAD parsing, source-only folders, packed refs, dirty tracked files, detached
HEAD, linked worktrees, single/multiple/partial-match selection rules, offline local choices and match ordering.
The normal executable checks do not make a network request. `Launcher.Checks --discover [folder]` is an optional
real, read-only discovery check and does query the public upstream.

The live upstream check returned main at `704bf1d2c2763f71ac174e7aaa6bcbf727f63159` during this validation. A normal
search found the existing renamed fork and worktrees without changing their source. Separate temporary source
clones were used for the first-run single/multiple-match cases; no client, saves or login keys were copied into them.
The first-run desktop automatically selected the sole matching clone and showed Found and using, upstream identity
and Change location. The discovery page/header were inspected at the 1020 × 730 minimum window size.
With two matching clones, the desktop showed both choices and kept the selection empty. The header's native folder
picker was then used to select the existing prepared standard validation checkout; the location was applied and
checked immediately. Automated checks passed: 43 launcher checks and 9 PowerShell ownership checks. The final
solution build passed with zero warnings/errors. No runtime service or game was started for these checks.

Desktop validation builds reported informational version `1.0.0+3b398e7977c56eaf36904c6c08594554e9c3f771`, compiled
from working changes on that base. Final source was rebuilt after validation. Launcher settings were backed up for
the first-run checks and restored afterward. No game/server was started by discovery and nothing was published.

## Commit display follow-up, 30 October 2026

Source/build identities, upstream identity and discovery result HEADs now render seven-character monospace code
badges. Each badge retains its full hash in a tooltip. Branches, versions, explanatory text and filesystem paths keep
normal text styling. Full identities remain in Core, comparisons, logs and exported diagnostic reports.

Builds and Settings were inspected at the 1020 × 730 minimum size, including a prepared installation, an unbuilt
source checkout and discovery entries with no readable Git HEAD. UI validation builds reported informational version
`1.0.0+218a67cebc00439801ad5cbd1a99619d7a02b8ee`, compiled from the working display changes. The solution built with
zero warnings/errors; all 43 launcher and 9 ownership checks passed. No LocalExile source/runtime changes were needed.
