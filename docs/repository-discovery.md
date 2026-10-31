# LocalExile repository discovery

With no saved checkout, Waypoint opens with a chooser that begins the lookup in the background; **Browse…** is available
immediately. A saved checkout is reused, and a missing or invalid one brings the chooser back without silently replacing it.
**Find automatically…** on the Setup and Settings sections opens the same chooser. A found checkout is used only when the
player picks it.

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

The workspace heading shows **Workspace** with no valid selection. Selecting a checkout immediately displays its
folder name, then resolves the repository name from the locally configured Git origin. This name lookup is offline,
supports HTTPS/SSH origins and worktrees, and excludes remote credentials and query strings. A missing Git/origin
falls back to the folder name. Results from an earlier selection cannot replace the current heading; long names
are truncated visually with the complete name in a tooltip. The full selected path stays underneath.
