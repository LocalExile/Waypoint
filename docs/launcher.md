# Waypoint

A Windows window over a [LocalExile](https://github.com/LocalExile/LocalExile) server checkout: buttons for its
`scripts/dev/live.ps1`, the services' state, guided setup, mods, save backups, live performance charts, every log, the
accounts, and a console for the Gateway's chat commands. It changes nothing in how the game runs: every start, restart
and stop is a `live.ps1` switch, so the rules of the server's
[GettingStarted](https://github.com/LocalExile/LocalExile/blob/main/Agents/GettingStarted.md) (one client, only through
`live.ps1`, from the checkout's root) still apply.

Waypoint references no server project. It drives the checkout through what the server offers outside tools:
`live.ps1` and its `-Stop`, the Gateway's `commands` and `values <kind>` modes, the dev host's JSON-lines protocol
(`accounts`, `register`, `create_character`), the client control bridge and the server's logs. A checkout without them
(older than the server's launcher hooks) shows as too old in the Setup list.

Beside those it has an adapter of its own, the PowerShell and Python scripts in `scripts/`: `RuntimeBridge.ps1` inspects
the checkout and runs the operations that need more care than a button can give them, the setup steps, the mod operations,
the save backups and the offline account tools. [runtime-contract.md](runtime-contract.md) describes its guarantees. The
scripts are embedded in the exe (`Launcher.Core`) and unpacked once per version to `adapter\<hash>` in Waypoint's data
folder ([Folders and tools](#folders-and-tools)), so a self-update, which replaces one file, carries them.
`Workspace.cs` is the app's side of it. The adapter still has its own start, stop and branch-switch actions, covered by its
tests; the app does not use them.

The Windows Tools section opens the map viewer when the selected source checkout has the terrain generator and extracted
game data. Waypoint carries its own read-only map helper, builds it against that checkout's existing generator libraries,
and closes it with the viewer. No LocalExile map-service hook or source changes are needed. The helper and all build outputs
stay in Waypoint's data folder; closing the viewer discards its private build.
The area list loads first; generation starts only when requested. Search
matches every space-separated term against the area name or ID; Enter selects the first match. The list can sort by name or
natural ID order. The native view displays the generated tiles and walkability at 45 degrees, plus distinct markers for
area transitions, internal teleports, bosses, NPCs and other known objects. Selecting a marker opens its details; known
connected areas can be opened and the previous map restored. Clicking overlapping markers opens a compact picker.
The default view hides grid lines, environment helpers, critters and unused exits; the tile list contains used, nonblank
tiles. **Display** controls walkability, landmarks, grid and teleport links, plus **Internal markers** and the **Full tile
catalog** for inspection. These choices are remembered; saved grid choices from earlier versions are preserved.
**Copy map details** beside the title copies the area, actual and substituted seed, actual level, layout dimensions,
generator checkout revision, tracked-change state and warnings. It describes the displayed map even after inputs change.
**Monster packs** is off by default. It shows one marker per authored pack anchor in a placed room, with configured rarity
when specified, possible monsters, base pack size and possible leaders from the extracted tables. It does not run the
server's population planner or show individual, scattered or live monsters. Generic room anchors are possibilities; the
server may use only some of them. Missing pack tables leave the map usable and are reported beside the optional layer.
**Encounters**, also off by default, shows explicit mechanic objects and named anchors in generated rooms: supported
classifications include shrines, strongboxes, tormented spirits, rogue exiles and several league encounters. It uses
specific object types and point names, not scenery names. An anchor or placed object does not establish live gameplay
support or activation. Client catalogs alone cannot predict random ghost, strongbox or exile locations from a map seed;
unidentified league-chance columns are not presented as probabilities.
Numbered boss points show their WorldAreas boss names and IDs when available; unnumbered points show candidates.
The catalog and placement coverage depend on the server's generator; release installs and
older checkouts without the generator do not offer this tool. An incompatible generator reports a preparation error.
When generation substitutes a seed, the viewer briefly displays the original and actual seed without interrupting the map.
The random seed button uses the server's normal instance seed range; manually entered seeds may use the full unsigned
32-bit range. Area level can affect generation and defaults to the area's data when left blank.

## Releases and updates

A release is a git tag `v<major>.<minor>.<patch>` pushed to GitHub. The release workflow
(`.github/workflows/release.yml`) runs the tests, publishes `Waypoint.exe` with the tag's version
(`-p:Version`), writes its SHA-256 to `Waypoint.exe.sha256`, and attaches both to a GitHub release of the tag
with generated notes. A tag that is not `v1.2.3` fails the workflow. The CI workflow (`ci.yml`) builds and tests every
push to `main` and every pull request, and runs the adapter's checks and the translation check.

```powershell
git tag v0.2.0
git push origin v0.2.0
```

The version shows in the window's title. A build from source is 0.0.0 ("built from source") and never offers updates.

A release build asks GitHub's public API for the latest release of `Updates.Repository` (LocalExile/Waypoint) when it starts (**Look for
updates at start** in Settings; **Check for updates** there asks at once and says what it found). A newer
version shows a green card floating over the bottom of the window. **Update now**, after a confirmation:

1. downloads the release's `.sha256`, then its exe beside the running one (`Waypoint.exe.new`);
2. refuses an exe whose SHA-256 differs, changing nothing;
3. renames the running exe to `.old` (Windows lets a running exe be renamed, not replaced) and gives the new one its
   name, renaming the old one back if that fails;
4. gives up its single-instance claim, starts the new Waypoint and closes this one without asking about the servers,
   which keep running;
5. the new Waypoint deletes the `.old` when it starts.

Only the published single-file exe updates itself; under `dotnet run` the bar offers the release page only. A transferred
repository's old name keeps answering, so builds made before a transfer still find the releases.

## Release installs

LocalExile publishes player releases: a zip with `LocalExile.exe`, `release.json` and everything else already built (the
server's `Agents/docs/dev/releases.md`). Such a folder (`LocalExileReleases.IsInstall`) is the other kind of root Waypoint can
work from, beside a source checkout. It has no `live.ps1`, source or git, so `Repo.IsInstall` changes what the sections do:

- **Play** (`InstallTab`) and **Versions** (`ReleasesTab`: the installed version and every release's notes) have views of their own.
  Branches and pull requests need git, so that Versions page leads to another folder or a new copy.
- **Control** starts and stops everything together, because one `LocalExile.exe` holds an install: no per-service actions, and
  the game cannot join servers started alone.
- **Backups, Accounts, Mods** use the adapter, which recognises a release (`$script:isRelease`): its servers in `app\`, the bundled
  Python. Create account goes through `LocalExile.exe account create`; the dev host's tools are hidden. Mods are the built ones,
  enabled or disabled while the game is closed.
- **Console** lists the release Gateway's commands and copies a line to paste into the game's chat, because a release has no
  client control bridge or Frida. Nothing can read the replies.
- **Tools** explains why it is unavailable. v0.1.0-beta's Gateway has no `crafter` mode, and asking a Gateway for a mode it lacks
  starts it serving, so a release's is never asked. Giving an item would need the bridge as well. **Get the item crafter…** starts
  the move to a source copy ([below](#from-a-release-to-a-source-copy)).

Waypoint drives it only through `LocalExile.exe --json` (`LocalExileTool`), one JSON event per line:

- **Play** is `run --no-update-check`. Below its button it shows the run: a row per stage with its latest message, the time so far,
  and the output with timestamps; these stay after the run, so a failure can be read there. It holds the install's lock until the `ready` event, so the window can close or go to the
  tray while the game runs; ending the process ends the servers and the client (the release's job object). A `needs-input`
  answer sends the player to the setup guide.
- **Stop playing** and **Settings'** stop on exit run `LocalExile.exe stop`. That ends the install's processes outright, so it is
  hidden during a step that writes the client or the keys (`MustFinish`). An update never uses it.
- A password goes to `--password-stdin` and nowhere else, never to a command line or a log. Standard input uses the console's
  code page, so the guide accepts only printable ASCII passwords: anything else could arrive as a different password.
- An event with a `schema` above `LocalExileEvent.KnownSchema` ends the run and asks for a newer Waypoint.

### Its updates

At start (**Keep LocalExile up to date**) and on **Check for updates**, Waypoint compares GitHub's latest release with
`release.json` in SemVer's order (`ReleaseVersion`: 0.1.0 is newer than 0.1.0-beta, which the release's own check, comparing
numbers only, cannot see). A newer one shows a green card beside Waypoint's own. **Update now** refuses while anything runs,
since nothing is force-stopped for an update, and after a confirmation:

1. copies `data\saves.db` and its journal to `backups\before-update-<version>-<time>\` (unless **Automatic backups** is off);
   a failed copy means no update;
2. downloads the zip into `.waypoint-update\` and checks it against the release's `.sha256`;
3. refuses a zip with a path outside its `LocalExile/` folder, or one naming `Client\` or `data\` (other than `data\schema\`);
4. extracts it into staging, then moves the files into place, the launcher and `release.json` last, so a cut-short update still
   reads as the old version and is offered again.

As with the release's own instructions, the new zip goes over the old one: a file a newer release dropped stays, and there is no
way back beyond the saves' copy. The next Play extracts whatever game data the new version needs.

## Build and run

```powershell
dotnet publish src/Waypoint -o .     # one Waypoint.exe in this repository's root (git-ignored)
.\Waypoint.exe                       # or double-click it
dotnet run --project src/Waypoint    # while working on it
dotnet test Waypoint.slnx            # the tests
```

To update it after a pull or a change, close Waypoint first (a running `Waypoint.exe` is locked and the publish fails to
replace it; leave the servers and the game running if you want), run the same `dotnet publish`, and start it again.

`--repo <folder>` starts it on a checkout for one run, without changing the saved one. `--allow-multiple` lets a second copy
run beside the first, for testing: normally a second start only brings the first window forward, because two copies would
fight over one checkout's services.

It needs the .NET 10 Desktop Runtime, which the .NET 10 SDK installs on Windows.

### The server folder

**Change folder…** on Play and on a release's Versions, and **Change…** on Control, take a checkout or a release install, and Waypoint
starts again on it. **Set up a new copy…** there and on Control opens the setup guide on its source route, which clones LocalExile
with git into a chosen new or empty folder and sets it up.

### From a release to a source copy

A source copy has what no release has yet, such as the item crafter. **Tools → Get the item crafter…**, or **Set up a new copy…** on a
release, opens the guide's source route with one more page, **Your characters**, after the client (`SwitchToSource.cs`). The release's
client comes first in the list, and is shared rather than copied when its keys fit. **Browse…** on this route refuses a release, which
has no source to build, and turns to the download instead.

- **Your characters**: **Bring my characters** copies the database, what a stopped release still holds in `saves.db-wal` included,
  with the same online backup Waypoint's backups use, and it is checked before anything here changes (the adapter's `ImportSaves`). The
  mod choices come along; mods the player added do not. Anything already in the folder is backed up and set aside, and
  `data\saves-brought.json` keeps the guide run again from copying over newer progress. **Start with a new database** copies nothing,
  and the account dialog opens after Waypoint starts on the copy. The two never share one database: a newer server may write saves
  an older release cannot read.
- **Your current LocalExile**: **Keep both** leaves it as it is. **Remove the old one later** leaves it until the player has pressed Play
  on the copy and closed the game; Play then asks. **Delete the old one…** moves a shared client into the copy when both are on one
  drive, and deletes the release's program files. Its `data` and `Mods` stay, and so does anything a release does not ship.
- **Updates**, for a download: **Keep an older version that does not change** puts the copy on its own branch `waypoint-tested` at
  `SwitchToSource.TestedCommit` (LocalExile v0.2.0-beta, e28909f6), which Play never moves, because only a plain copy on the default
  branch is updated. **Get the newest
  changes** leaves it on `main`. Changing the commit means testing it again: a release's database opened on it, and an item given.
- **Giving items**: **Set up Give item** installs Frida into the copy's `.venv`, which git ignores. Left out, the crafter offers it
  again the first time **Give item** is pressed.

The Run page lists what these choices will do instead of its steps until **Start setup**.

On its first start Waypoint asks for the server checkout: the folder with `GameServer.slnx` and
`scripts\dev\live.ps1`. It searches the usual folders and lists what it finds, marking those at the upstream's HEAD
([repository-discovery.md](repository-discovery.md)); the player picks one or browses. Nothing found is used without being
picked. The picker starts at a `LocalExile` folder beside this repository when there is one. A folder that is not a checkout
is refused. The choice is saved; **Find automatically…** and **Change…** in Setup, Settings and the Control section's Setup
panel pick another one and start Waypoint again on it. Started from a checkout's root (`dotnet run` there), Waypoint uses that
checkout when none is saved.

At startup Waypoint makes the checkout its working directory, so every path it uses (`logs\`, `data\`, `Client\`,
`src\…\bin\Debug`) is the checkout's, as for the server's own tools.

Its own settings (the server folder, the switches, the appearance, the language, pinned console lines, the history) are per
Windows user in `%LOCALAPPDATA%\Waypoint\settings.json`, never in a repository. The first start takes over
`%LOCALAPPDATA%\LocalExile\launcher.json` and `%LOCALAPPDATA%\EmulatorWorkspaceLauncher\settings.json`, and leaves both as
they are. The adapter's operation logs and the save backups stay under `%LOCALAPPDATA%\EmulatorWorkspaceLauncher` unless the
player chooses otherwise, so backups made by an earlier Waypoint are still listed.

### Folders and tools

**Settings → Folders and tools** (`PathChoices.cs`) chooses where Waypoint keeps its files and which programs it uses. It shows
with Developer Mode, and also whenever something there is chosen or a choice cannot be used, so what startup reports can always be
changed there. **Settings → Game settings**, for everyone, has the game settings profile (below). A field
left empty is the default, which is where every Waypoint before this setting kept things, so an existing setup changes nothing.
Each field takes a typed path as well as **Browse…** (on Linux a picker may be missing), and **Save** applies it.

| Row | Default | What it holds |
|---|---|---|
| Waypoint's data | `%LOCALAPPDATA%\EmulatorWorkspaceLauncher` | operation logs, the adapter's scripts, capture and mod notes, and the backups unless they have their own folder |
| Save backups | `backups` in the data folder | the save backups |
| Wine prefix (Linux) | `WINEPREFIX`, else Waypoint's own | the prefix the client runs in; nothing is moved, the game sets up a new one there |
| Game settings profile | the folder the client uses now | LocalExile's graphics, keys and saved login (below) |
| Git, .NET SDK, Node.js, Python, PowerShell | found on PATH as before | the program for that tool |

- **A new data or backups folder** must be empty or new: Waypoint copies everything there, checks every file's size, removes the
  old ones and starts again (`Folders.Move`). A failure removes the copies and changes nothing, and offers **Open both folders**
  to move them by hand. A folder that already has files (moved by hand, say) is used as it is, with **Use it as it is**. Only
  while nothing runs. A backups folder of its own inside the data folder must be moved first.
- **A chosen program**'s folder goes first on Waypoint's PATH (`ToolPaths`), which everything it starts inherits, `live.ps1`
  and LocalExile's scripts included. Each row shows the version and program in use. Versions are shown, never refused; a
  program that does not answer `--version` is used only on **Use it anyway**.
- **A choice that cannot be used now** (a drive not there, a program removed) is skipped at start with the default in its place,
  and said once in a dialog; the choice stays, for when it is back (`Settings.ApplyPaths`).
- The adapter is told the folders as `WAYPOINT_DATA` and `WAYPOINT_BACKUPS`, and falls back to the defaults without them.
  `WAYPOINT_HOME` moves Waypoint's own settings and saved items, the one folder Settings cannot choose since the choice is kept
  in it. The two variables can also be set by hand, as the folders' defaults.
- **Game settings profiles** (Windows), as Mod Organizer's profiles: a profile is a folder right under
  `Documents\My Games\Path of Exile` (graphics, keys, network mode and saved login), and the active one is the folder
  `Client\config.json` names (`ClientSettings.Profile`); the client reads those names relative to that folder, so a profile is
  always one plain folder there. The profile in use is the default: nothing changes until the player picks another.
  - **The player's own profiles** are the default (`LocalExile`), those made here and the active one (`PathChoices.IsYours`).
  - **On Play** (both views, `ProfilePicker`), **Game settings:** lists the player's own profiles, the active one selected (or
    **Your normal settings** while none is), and **Manage…**; picking one uses it from the game's next start. It shows only
    once there are two of them, so a player with one profile sees Play as before. **Settings → Game settings** shows the
    profile in use with **Manage…** and **Open**.
  - **The Profiles window** (`ProfilesWindow`) lists the player's own profiles, the active one marked, then the other folders
    there with settings under **Other folders**. **Select** uses one; **Create…** makes one from the normal settings (no login,
    Predictive); **Copy…** makes one from the selected profile as it is, without its saved login; each asks for the name
    (`NameDialog`), and lists the new profile without selecting it. **Rename…** asks for the new name. **Rename** and **Remove** are offered only for a profile made here (`MadeGameSettings`) that is not
    the active one, and Remove never for the last; Remove asks, then sends the folder to the Recycle Bin.
  - Only while the game is closed; a shared (linked) client is asked about first, since every copy that uses it switches too.
    Only `config.json`'s two names change on a switch, and the normal `production_Config.ini` is only read.
- On Linux the game settings are the Wine prefix's (`GameSettings`): the row shows that folder, and another prefix is
  another set.

## Look

Waypoint draws its own title bar, in the theme, on every window. In the main window the header is the title bar: it drags
the window, double-click maximizes, and it holds the minimize, maximize and close buttons, with Snap Layouts on Maximize on
Windows 11 (`WindowFrame.cs`). The dialogs with fields have a thin caption with their title and a close button
(`FormWindow.cs`); the message boxes are cards without one. Only Windows' own folder picker keeps the system's look.

The window is a coloured sidebar with the sections, a header (the section's name, the checkout's branch, the theme, the
settings) and a status strip on every section: a named lamp for Patch, Gateway, Auth and the game, and for the dev host and
the walk-map recorder while they run, since neither is needed to play (green running, amber starting, red when another
program holds the port, grey stopped; click one for the Control section), whether anything holds the checkout, and a link to
the logs. A running lamp glows, unless Increase text contrast or Windows High Contrast is on, and each state has its own mark
inside the lamp, so it reads without the colour. A newer release shows as a green card floating over the bottom of the whole window,
apart from the strip; **Later** hides it until the next start.

The controls are [Material Design in XAML](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit)'s,
restyled flat in `Shell.xaml` (which `App.xaml` merges): 30 px buttons with 4 px corners and no shadows, an outlined one by default and one
primary button per view, thin borders, tables with small grey column titles. Waypoint's own colours are the
`Shell.*` brushes: Shell.xaml holds the default scheme's light ones and `Theme.cs` replaces every one of them, with Material's
base theme and primary colour, from the chosen palette (`ThemePalette.cs`). Views use them through `DynamicResource` (code
through `Theme.Brush`), so a change reaches everything at once; a colour written into a view would stay the same in every
scheme. The default scheme, **Waypoint blue**, keeps the blue sidebar on a white or near-black page. The dialogs with fields
(`FormWindow.cs`) use the palette's role brushes (`Muted`, `Accent`, `Font12` and so on).

**Settings → Appearance** has light, dark and system modes, 19 color schemes, custom colors, reading fonts (OpenDyslexic is
bundled), text size, color-vision profiles, contrast protection, stronger keyboard focus and reduced motion
([appearance.md](appearance.md)). The text size zooms the whole window, so the fixed row and button sizes grow with the text.
The emblem is the artwork as painted on the default scheme, and recoloured with the scheme's accent on the others
(`WaypointArtwork.cs`). Windows High Contrast takes over from every palette.

The theme button in the header goes Light → Dark → System. System follows the Windows app theme, also when it changes
while Waypoint runs. The choice is saved. The branch is read from the checkout's `.git/HEAD` (a short commit when detached); clicking it opens the
Versions section. The settings button opens the Settings section.

The interface is in English, Russian or Simplified Chinese (**Settings → Language**, at once; until one is picked it
follows the system's display language; [localization.md](localization.md)).

Ctrl+1 to Ctrl+9 and Ctrl+Tab choose a section, F5 inspects the checkout again, and Ctrl+Shift+R restores the default
appearance.

Questions and notices are Waypoint's own message boxes (`Dialog.cs`), in its look and theme, never Windows'
MessageBox: the kind's icon, a title, the text, details such as a list of files in a scrolling box, and buttons named
after what they do ("Stash and switch", not OK). Enter presses the main one, Esc or closing the box cancels; a risky
question (a fork's pull request) has Cancel as its default.

## Simple and Advanced modes

A new player starts in **Simple mode**, with five sections (Play, Mods, Tools, Accounts, Settings) and no status strip or branch
chip: nothing to choose before playing. **Settings → Developer → Developer Mode**, off by default, adds the **Simple | Advanced** switch at the bottom of the sidebar:
**Simple** has Play, Mods, Backups, Accounts, Logs, Console, Tools and Settings. **Advanced** replaces Play with Control, as that section has it, and adds Versions, Performance and Server timings. Setup is reached from Control. Turning Developer Mode off goes back to Simple
mode. Someone who used an earlier launcher, or Waypoint before the modes, starts in Advanced mode with Developer Mode on
(`Settings.Load`).

**Play** in Simple mode (`PlayTab`) is the Control section's Play with an update before it:

1. When `CheckoutUpdates.Due` says so (at most once a day; **Settings → Updates → Keep LocalExile up to date**, on by
   default) and nothing runs, Waypoint fetches. No network is no reason not to play: the game starts as it is.
2. Only a plain copy is moved (`CheckoutUpdates.PlainCopy`): on the default branch, with LocalExile as origin, no uncommitted
   changes and no commits of its own. Anything else is the player's work and stays as it is, in either mode.
3. The saves are backed up (unless **Automatic backups** is off); a failed backup means no update, and the game starts as
   it is. Then the branch moves forward, fast-forward only, and the commit and backup before it are kept in the settings.
4. `ControlTab.PlayAsync` runs, so a start is the same `live.ps1` run with the same port question.
5. When that run fails after an update, Waypoint stops what runs, restores that backup, puts the checkout back
   (`git reset --keep`) and starts again, and says so.

**N new change(s) in LocalExile since your version** shows under the switch when a plain copy is behind LocalExile's branch.
Play looks when it opens and every half hour, while nothing runs: a fetch and a count only, which neither moves the checkout nor
counts as the day's update check. **Update now** beside it is that update at once, steps 2 and 3 above (backup, fast-forward, the
way back kept), without starting the game; Play then builds and starts the new version.

**Go back to the version you had** stays for a week after an update: the same way back, on request, and updates pause for a
week. Restoring keeps a copy of the current saves first. A checkout follows `main`, which has no test results, so an update is
`main` as it is: the way back catches a build or start that fails, not a bug in the game. A release install is different: see
[Release installs](#release-installs).

**Release | Latest** at the top of Simple mode's Play (`EditionSwitch`, `Editions.cs`) moves between a release install and a
source copy that follows `main`. Each is a folder of its own with its own characters; Waypoint remembers the last of each it ran on
(`ReleaseFolder`, `LatestFolder`). Switching needs nothing running, then asks one question, and Waypoint starts again on the
other folder. The saves of the folder left are backed up first while automatic backups are on (Backups), as for every other
change, and the question says so; with them off nothing is backed up or asked.

- Both ways to Latest say first that Latest has LocalExile's newest changes, reviewed, but possibly less stable than the release.
- **Release → Latest**: bring your characters? **Bring my characters** copies them in (the adapter's `ImportSaves`, as the guide's
  Your characters page: Latest's own are backed up and set aside first); **Keep Latest's own** leaves both as they are.
- **Latest → Release**: the release keeps its own characters, and Latest's stay for the way back (**Switch**). Characters never
  go from Latest to a release: a newer LocalExile may save what an older release can't read.
- A side Waypoint has no folder for opens the setup guide: the release route, or the source route with **Get the newest changes**
  chosen, which from a release has its Your characters page.
- On a release, **Tools → Get the item crafter…** switches to the Latest copy when Waypoint has one, with the same question, and
  otherwise opens the guide as before, with the older version chosen. A copy on that older version (`waypoint-tested`) is still the
  Latest side, and the switch says it does not update, since Play does not move it. **Get the newest changes** under it backs the saves
  up and switches the copy to LocalExile's default branch (`Editions.MoveToNewestAsync`; nothing is done with changes of the
  player's own), and clears the last update check, so the next Play updates it as it updates any plain copy, with its way back.

A link to a section Simple mode's list lacks (a log, from a failure) shows that section without selecting anything.

## Sections

| Section | What it does | How |
|---|---|---|
| Play | Simple mode's home: one sentence and one button, an update before the start, the way back | `PlayTab`, `CheckoutUpdates`; the Control section's Play |
| Control | Play, Start servers, Stop servers, Stop all and the Hot reload, Skip build, Skip data check and Walk maps switches; every service as a table row with its state and one Start/Stop button; the script's output; the server folder and the setup checks | `live.ps1` (or `live.local.ps1`, see below), `-NoClient`, `-Stop -NoClient`, `-ClientOnly`, `-Stop`, `-Only <server>`, `-Walkmaps`, `-NoBuild`, `-NoExtract`, `-NoHotReload`; Close game ends the client's process, Stop all also the dev host |
| Setup | every requirement of the checkout with its next step: instructions, Prepare client, Install dependency, Create account, the Client folder, the setup guide | the adapter's `Inspect`, `PrepareClient` and `Dependencies` |
| Versions | what the checkout runs and how far it is from LocalExile: LocalExile's branches and open pull requests, a fork's branches, the checkout's own, switching between them, updating the current one, going back to the default branch, restoring stashed changes, deleting the branches of closed pull requests; the commit and the built versions, Build checkout, Reload game data, fresh saves | git in the checkout (`fetch --prune`, `switch`, `merge --ff-only`, `pull --ff-only`, `stash`, `fetch <LocalExile> pull/<n>/head`); the open pull requests from GitHub's public API; the adapter's `Backup`, `Build`, `ReloadData`, `FreshSaves` |
| Mods | the checkout's mods, built from source or binary only: search, enable and disable, build one or all, reload, live status, the folders | the mods' manifests and the server's saved choices (`ModCatalog.cs`); the adapter's `BuildMod`, `BuildMods`, `EnableMod`, `DisableMod`, `ReloadMod`, `QueryMods` |
| Backups | the save database's backups: what each was made from, back up now, restore saves, restore mod choices, delete, the automatic backups switch | the adapter's `Backup`, `RestoreBackup`, `RestoreModChoices`, `DeleteBackup` and `SaveBackup.py` (SQLite's backup API); `BackupCatalog.cs` |
| Performance | CPU and memory of every service; for the chosen server, GC heap, allocation rate, collections, time in GC, thread pool, lock contention and exceptions | the processes once a second; the runtime's `System.Runtime` counters through its diagnostics port (what `dotnet-counters` reads), with nothing changed in the servers |
| Server timings | what each Gateway operation costs over a window (30 s to 15 min, 1 min by default): calls per second, average, p95 and p99 (the worst second's), milliseconds per second; client messages by id, server phases (input queue, handler, send and wire gates, combat lock, surround, blocker build), level generation by area; the five busiest charted (p95 and ms per second) | the Gateway's `LocalExile.Gateway` meter (`localexile.gateway.duration`, ms, tags `phase` and `operation`) through the runtime's `System.Diagnostics.Metrics` event source, in the Gateway's counter session, published once a second; a checkout older than the `Message` and `LevelGen` phases shows only the phases |
| Logs | every server's `logs\live-*.log` with its `.err`, the walk-map recorder, the dev host, the client's `Client\logs\Client.txt`, the launcher's own lines, and "All errors" from all of them | follows each file twice a second; a file `live.ps1` recreates starts over; a large file from its last 512 KB |
| Accounts | the accounts and their characters, searchable; registering an account; creating a character; creating, duplicating, renaming, setting a password and deleting with everything stopped | the list and those changes are the adapter's (`QueryAccounts`, `Register`, `ManageAccount`, `AccountData.py`; [account-management.md](account-management.md)); Register and Create go through the server's dev account service (`GameServer.Host`, its `Agents/docs/dev-server.md`): `register`, `create_character`; Waypoint builds and starts it when needed |
| Console | the Gateway's chat commands for the character in game, with suggestions, usages, pinned lines and history | the server's client control bridge (`scripts/dev/client_control.py --stdio`); the lists come from the built Gateway (`GameServer.Gateway commands` and `values`) |
| Tools | tools for the game itself, each in its own window: the map viewer, the item crafter, the lab layout | the server's item crafter hook (its `Agents/docs/dev/item-crafter.md`): `GameServer.Gateway crafter` and `/item_give` through the client control bridge; the Gateway's `labyrinth <yyyy-mm-dd>` mode for the lab layout |
| Settings | the appearance, the language, what closing the window does, updates, the checkout and its local launch profile (Advanced mode only) | saved as chosen; `Settings.cs` |

### Control

- The toolbar holds `live.ps1`'s switches and the actions on everything: **Stop all**, **Stop servers**, **Start
  servers** and **Play**, the one blue button. While a server runs, Start servers reads **Restart servers**: the same
  `live.ps1 -NoClient` rebuilds and restarts all three, which is what edits hot reload does not cover need (Auth,
  Patch, the shared projects, a pull).
- Below it every service is a row: its state (running, starting while its port is not open yet, stopped, or its port
  held by another program; a Gateway under `dotnet watch` is "Running · hot reload"), ports, process, uptime, CPU and memory, and one button that starts or stops that service
  alone. A server's is `live.ps1 -Only <server>` (build and start) or `live.ps1 -Stop -Only <server>`; the client's is
  Start game only or closing its process; the dev host's builds and starts it or ends it; the walk-map recorder's only
  stops it (it starts with a run while Walk maps is on). The ⋮ menu restarts a server and opens the service's log.
- A run that starts a Gateway (Play, Start servers, a Gateway's Start or Restart) first backs the saves up, unless **Automatic
  backups** is off on the Backups section. When the backup fails, Waypoint asks before starting without one.
- With **Use the checkout's local launch profile** on (Settings, on by default) a checkout's `scripts/dev/live.local.ps1`
  is what runs, in place of `live.ps1`.
- A run that starts a server first looks for anything else on its ports, because `live.ps1` would end it without asking.
  Waypoint shows the program, its PID, command line and start time and asks: **Check it first** opens its folder (or Task
  Manager) and starts nothing; **End it and continue** gives a console server its Ctrl+C and up to 20 seconds to save, then ends
  it, with its `dotnet watch` if it has one; **Cancel** starts nothing. A port held by Windows itself is never offered for
  ending. The setup guide asks the same, once a run.
- **Play** with the three servers already listening is `live.ps1 -ClientOnly`: only the game, against them, and a
  running game is closed first after a question (`-ClientOnly` refuses a second client). With Walk maps on it is
  `-ClientOnly -Walkmaps`, which also starts the walk-map recorder (it ends with each client). The servers are not
  rebuilt: the Gateway's edits apply by hot reload, and **Restart servers** rebuilds them for the rest. Otherwise Play
  is plain `live.ps1`: it builds, restarts the servers and starts the client, asking first if a client is running,
  because `live.ps1` closes it. A checkout whose `live.ps1` refuses `-ClientOnly -Walkmaps` (older than that hook) gets
  the full run while Walk maps is on and no recorder runs. Its tooltip says which one Play does.
- **Start servers** is `live.ps1 -NoClient`, **Stop servers** `live.ps1 -Stop -NoClient`: the servers and the walk-map
  recorder, the client staying (it drops to the login screen).
- The client row's **Start** is `live.ps1 -ClientOnly`, with the unreachable proxy the control bridge also sets for it. It
  needs the servers running, and `live.ps1` refuses a second client.
- **Stop all** is `live.ps1 -Stop` (the client, the walk-map recorder and the three servers, a watched Gateway with its
  `dotnet watch` included, then it waits until their ports are free) and the dev host the Accounts section started. The
  tray's Stop all and closing with "Yes" do the same.
- A server's **Restart** is `live.ps1 -Only <server>`. With Walk maps on, the restarted Gateway announces area loads to the recorder
  that is already running.
- **Hot reload** (on by default, as in `live.ps1`): the Gateway runs under `dotnet watch`, and saved edits to its code
  apply with the game still running. Off passes `-NoHotReload` to the runs that start a Gateway.
- The network mode is the game's own setting (`networking_type`), and there is no switch for it here: a current Gateway serves
  whichever the client picks (LocalExile #684), so a player changes it in game and enters again. Runs set no variable for it.
- Only one `live.ps1` runs at a time; the buttons wait while one does (the status strip says so). The launcher runs it with Windows PowerShell and
  turns off MSBuild's reused nodes for it, the fix the server's troubleshooting table names for a script that never
  returns. It takes the script's end from the process's exit, not from the end of its output.
- The **Setup** list checks the server's one-time steps (**Guided setup** opens the Setup section): the client copy, the keys, the patched client, the gateway
  list, the extracted data, Node and ooz, whether the checkout has the launcher hooks, whether the game settings are separate
  from the normal game's, the network mode in the settings file this client really uses (Predictive is preferred; Lockstep or
  "auto" passes with a hint on a server that serves any mode, and fails on an older one that serves only Predictive), and
  Python with Frida. **Game data** runs `extract-data.ps1 -List`.
- The services are found by process: a hot-reloaded Gateway by its own `GameServer.Gateway.exe` (rather than the
  `dotnet watch` around it), the other servers and a Gateway without hot reload by `GameServer.<Name>.dll` on a dotnet
  command line, the client by `Game.patched.exe`, the recorder by `walkmaps.py`. A port that listens without such a
  process shows as held by another program.
- Closing the window with something running asks what to do, once: keep Waypoint in the tray, exit and leave everything
  running, or stop everything (Stop all) and exit. **Remember this** saves the answer; **Settings → Window behavior** holds
  it, with **Always ask**. With nothing running the window just closes, unless the tray is the saved choice. Closing waits
  for a run that is going on.
- **Tray while the game runs** (on by default, in Settings): when a client starts, Waypoint hides in the tray, and when no
  client is left it comes back. A client that Play closes and starts again keeps it in the tray, and a window opened
  while the game already runs stays open. The tray icon's menu shows the window, stops everything or exits.
- A failure on a background thread (the process sampling, a runtime counter session, the log reading) is reported in
  Waypoint's log, never ends it: a server that `live.ps1` stops ends its counter stream anywhere, even inside
  a block. Anything that still ends the process is written to the checkout's `logs\launcher-crash.log`.

### Versions

- LocalExile is the reference whatever `origin` is: a contributor's origin is their fork, which knows nothing of LocalExile's
  newer commits or pull requests. Beside a fork, Waypoint fetches LocalExile by address into `refs/launcher/upstream/*`, so no
  remote of the player's changes (the old `Wrathen/LocalExile` address counts as LocalExile). The header says how far the
  branch is from LocalExile's: "195 commit(s) behind LocalExile".
- The section lists LocalExile's branches, then, beside a fork, the fork's that LocalExile has not (a **your fork** label,
  and LocalExile's get one too), the checkout's own (a **local** label: work not pushed yet; the `pr/<n>` ones stay in the
  pull requests' list) and LocalExile's open pull requests, with their last commit or title, author, age, and a **draft** or
  **fork** label. Showing it fetches when the lists are older than five minutes; **Fetch** does at once. GitHub's API
  is used without a token (60 requests an hour), so the pull requests are read again only on Fetch or after a minute.
- **Switch** to a branch switches to the local branch of that name, moved forward to what it follows (LocalExile's, else
  origin's) when that loses nothing (a local branch with commits of its own is left as it is; one neither has is only
  switched to), or makes one from origin's, which it tracks, or from LocalExile's beside a fork. **Switch** to a pull request fetches its head (`pull/<n>/head`, from LocalExile) into the local branch `pr/<n>`,
  again on each switch and on **Update**, so a force-pushed pull request comes in whole. It refuses when `pr/<n>` has commits of the player's own: the launcher
  remembers the head it fetched last (`refs/launcher/pr/<n>`), so commits a force-push left behind are not taken for
  the player's.
- Every switch has the same guards, in this order. A pull request from a fork (or a deleted one) asks once, with the
  author's repository named: its code, `live.ps1` and the scripts included, runs on this computer with the player's
  rights. The agreed ones are kept in the settings. Uncommitted changes (the ignored `data\` and `Client\` are not
  any) are listed and stashed as `launcher: <branch> before switching to <target>`; going back to that branch offers
  them back, and **Restore stashed changes** next to the branch's name does it later. Running servers or a running game
  are stopped first (`live.ps1 -Stop` and the dev host), or a watched Gateway would rebuild halfway through. When the
  stop fails, most often because the checkout's version is older than the launcher hooks and its `live.ps1` has no
  `-Stop`, the switch asks before going on with the servers running: switching to a version that has `-Stop` is the way
  back to stopping them. A switch
  that fails puts the stashed changes back.
- Before git changes anything the saves are backed up (unless **Automatic backups** is off; when the backup fails, Waypoint
  asks). **Start with fresh saves after the next switch** then moves the saves aside and starts an empty database, for that
  one switch; they stay in the operation's folder under Waypoint's data folder, and in the backup.
- A pull request that is not in the open list can be reached by number: type it in the search box, on the pull requests'
  list, and **Switch to #n** appears. The same guards apply, and the fork question, because its author is not known.
- git, `live.ps1` and the adapter's operations never run at once: a switch takes the same lock as a `live.ps1` run, and the
  status strip says which one runs. After a switch the header's branch and the setup checks are read again, and the launcher offers Play,
  which builds the new version (`live.ps1` regenerates the game data the version's manifest changed).
- **Update** fetches and fast-forwards a branch to what it follows, and refuses when it has commits of its own; on a pull
  request it is a new fetch. Nothing is pushed: a fork's `main` stays where it is. **Back to main** switches to the default
  branch. **Clean up** deletes the `pr/<n>` branches of pull requests that are no longer open.
- git runs with `GIT_TERMINAL_PROMPT=0`: the server repository is public, and a credential prompt would hang a hidden
  process. git's output goes to the section's output and the launcher's log.

- The **Build** panel shows the commit and what each server was built from, and the Gateway's last hot reload event. **Build
  checkout** needs the servers stopped; **Reload game data** needs a client in game.

### The setup guide

With no checkout or release install, the first start opens the guide (`SetupWizard.xaml`); **Run setup guide…** on the Setup
section, or **Finish setup** on a release install's Play, opens it any time. **Skip setup** leaves it: with nothing to open on,
Waypoint still needs a checkout, so the plain chooser follows.

Its second page chooses the route. **The ready-to-play release**, the default, asks for a new or empty folder (or an existing
install), the client, and an account when the install has no saves. Its run is two steps: the zip, as an update does it, then
`LocalExile.exe setup --json`, shown by its events' stage and progress. The client is copied unless the player chooses to use the
folder itself. A stop waits out a step that writes the client or the keys. A `needs-input` answer returns to the page that gives
that option, and the run starts again from where it was. **The source code** is everything below.

Until **Start setup** it only looks. It checks .NET 10, Git, Python and Node (a missing one has a button for its download page,
and **Check again** reads the PATH again, what an installer added in front, so Waypoint need not restart). Python is looked for on
PATH first and then, on Windows, through the py launcher, the installers' registry entries and python.org's default folders:
python.org's installer leaves its PATH option unticked, and `python` then answers with the Microsoft Store's shortcut, which is no
Python. A Python found that way goes first on Waypoint's own PATH, for itself and the scripts it runs (`PythonLookup`; the
player's PATH is not touched), and the line says which Python was found and where, or that the Store's shortcut answered, or that
the one on PATH is too old. The start does the same once, as do the Setup and Control sections' **Check again**, so a Python
installed while Waypoint runs counts without a restart. It lists the LocalExile checkouts it finds, or offers to
clone upstream into a new or empty folder, with the drive's free space. It lists the clients it finds (other checkouts'
`Client` folders, what the installers recorded in the registry, the default folders, a time-limited walk of the fixed drives),
and when there is none it says where it looked. Steam and Epic installs are listed struck out, so a player sees why theirs was
passed over; they have no `Content.ggpk` and cannot be chosen. **Browse…** takes any folder. `PathOfExile.exe` has no version
resource, so a build is recognized by its SHA-256 (`ClientLocator`), and the list shows it beside the size. A build the folder's
LocalExile cannot use is refused with the reason ([Game client builds](#game-client-builds)); one Waypoint does not know is warned
about before a 70 GB copy, but not refused. For a new folder the guide sets up what the client fits: the newest LocalExile when a
3.29.3c client was found, and only when the player has nothing but 3.29.3b, the last LocalExile made for it (release v0.1.0-beta,
or a source copy on its own branch `waypoint-3.29.3b` at the head of the server's `3.29.3b` branch), with the quiet note below.
**Set up the rest now** goes on without a client. The game is never downloaded.

### Game client builds

LocalExile works with one Path of Exile build at a time. v0.2.0-beta moved from 3.29.3b to **3.29.3c**: a 3.29.3b client no
longer logs in to it, and v0.1.0-beta (the server's `3.29.3b` branch) is the last LocalExile for 3.29.3b. `ClientLocator.Builds`
holds each build Waypoint knows (the SHA-256 and size of its `PathOfExile.exe`, and for an older build its last release and
commit); `ClientLocator.Needed` reads which build a folder's LocalExile needs (a release's `release.json`, a checkout's
`scripts/release/build-release.ps1`), so a folder pinned to an older version keeps its own.

The rule: a player who has a 3.29.3c client plays on 3.29.3c; one who has only 3.29.3b keeps the last LocalExile for it. Waypoint
looks when a folder is about to start the game (Play, and Control's Play or Start game; not Start servers, a restart or a stop)
and before Simple mode's daily update moves a source copy. It looks at that folder's client and, when it matters, takes a quick
look (two seconds: the player's other LocalExile folders and the usual places first, a plain client before one another LocalExile
prepared) for a client of another build. Clients it merely finds are never changed. `ClientUpdate` decides:

- **A move to a found 3.29.3c client** is asked once (`ClientUpdate.AskMove`): two short lines (what happens, what Not now does),
  then a box with each folder and what happens to it (the client found: only read; the current client: replaced, with its size,
  or for a link only the link), a button to open each so the player can back it up, then **Continue** (the default) or **Not
  now**. Not now changes nothing and never updates. When the old setup still works (a folder on the 3.29.3b version with its
  3.29.3b client), the game starts on it; otherwise nothing starts and the page says so. Either way the folder is not asked again
  while Waypoint runs. Continue is kept with the client found, so the steps after LocalExile's update ask nothing and use that
  client, not a second search's; a failed update forgets it, and the next Play asks again.
- **The client is older than the folder's LocalExile, and a 3.29.3c client was found**: the old client is taken out
  (`ClientLocator.RemoveClient`: its `config.json`, which names LocalExile's own game settings, is kept in
  `data\client-config.waypoint.json` first; a linked client loses only the link and the folder it points to is never touched; a
  copy is renamed first, which fails as a whole while the game holds a file, then deleted, without following a link inside it; a
  delete cut short is finished by the next start; saves, accounts and keys in `data\` stay). A release's Play then runs
  `LocalExile.exe setup --client <found> --client-mode copy` (always a copy: a link would let LocalExile write into the player's
  own install, which the official launcher may update under it; LocalExile copies into `Client.partial`, so a stopped copy is
  resumed, never used), drops the patched executable when the source was another LocalExile's prepared client (it trusts that
  folder's keys), puts the game settings back, and only then runs the game. A checkout opens the setup guide with the found client
  chosen; the guide copies it and puts the settings back (**Bring back your game settings**).
- **The client is older and no 3.29.3c client was found**: the client stays, and Waypoint asks once whether to use the version for
  it. A checkout moves to `waypoint-3.29.3b` (`Editions.MoveToOlderAsync`: saves backed up, nothing done with changes of the
  player's own; a copy without a client then gets the older one through the guide); a release cannot go back in place (a newer
  release's modules would stay behind), so the guide sets up v0.1.0-beta in a new folder and this one stays as it is.
- **The folder's LocalExile is older than its client, or a 3.29.3c client was found for a folder still on the 3.29.3b version**:
  after the question above (or, with no client found elsewhere, one that only says LocalExile needs the update), a release updates
  itself to the newest release on the Play page (`ClientUpdate.UpdateReleaseAsync`, saves copied first when Automatic backups is
  on), then moves to the found client as above. A copy on an older version of its own goes to the newest changes
  (`Editions.MoveToNewestAsync`); any other checkout is told how, since Waypoint moves one only on the player's word.
- **Simple mode's daily update** reads the client build the newer LocalExile needs (`git show <what the branch follows>:
  scripts/release/build-release.ps1`, `ClientUpdate.BeforeUpdate`) before the copy moves. A client that will not fit is settled
  first: with a 3.29.3c client found, the same question (Not now keeps the copy where it is); with only 3.29.3b, the copy stays and
  the page says why.
- **A client of no build Waypoint knows** is left to LocalExile's own check.

While a folder runs the 3.29.3b version and no 3.29.3c client is there, Play shows a quiet note (not a warning) that versions for
3.29.3b won't be offered after 3.30; it is looked for again only when the folder's LocalExile changes. A release's **Update now**
to a version for a newer client than the only one on the PC asks first, with **Keep this version** chosen (the build is read from
the release notes' "For the Path of Exile **3.29.3c** client"). The setup guide shows each client's build; a source copy that
needs 3.29.3c on a PC with only 3.29.3b can pick it, and the guide moves the copy to the version for it, while a release folder
says to go back and choose a new, empty folder for v0.1.0-beta. A new client build means a new row in `ClientLocator.Builds`, with
the old build's last release and commit.

Release files are downloaded through `GitHubDownload`: a release's download link (github.com/…/releases/download/…) that fails
with a server error or a time-out is fetched once more through GitHub's API (the release by its tag, the asset by its name, as
`application/octet-stream`). On 2026-11-08 the download links answered 504 for a long while as the API served the same files. It
serves LocalExile's updates and downloads, Waypoint's own update, and mod releases.

The run is a list of steps (`SetupSequence`): the server ports (the question on the Control section, asked first so that it does
not wait at the end of a long copy), download, copy, the extraction dependency, preparing the client, build, game data. Each
knows when it is already done or goes on from where it stopped, so **Stop**, **Try again**, **Finish later** and a guide opened
another day all come down to running the same list again.

**Stop** is always there, and closing the window is the same as pressing it. What it does depends on the step:

| Step | Stop | Next time |
|---|---|---|
| Download | ends git, removes the unfinished folder | starts over; the folder must be new or empty, so nothing of the player's is ever in it |
| Copy | stops within one 4 MB block | goes on from `name.partial`, after checking its last block against the source. A file takes its real name only when complete, and `Content.ggpk` goes last, so a checkout that has it has the whole client |
| Dependency, build | ends the tool | run again |
| Keys, patching, gateway list | finishes the stage, then stops before the next: a half-written `Game.patched.exe` or `Content.ggpk` would be worse than a few seconds' wait (`RuntimeClient.Interruptible`, `stop.request` in the adapter) | goes on from what exists |
| Game data | ends the extraction | an entry is stamped only when complete, so one cut short runs again |

A prepared client from another checkout brings that checkout's `login-keys.json`, because a patched client works only with the
keys it was patched for. A checkout with keys of its own keeps them, and its client is patched again.

**A prepared client can be shared instead of copied**, which saves about 70 GB. `Client` then is a junction, made by the guide
or by hand, and that state is a normal one:

- **Sharing** is offered, on by default, and chosen first among the clients found, only for a client already prepared, because preparing changes a client and a
  player's real install must not be changed. Its keys must fit: the public key is in the patched executable as it is, so
  `ClientCopy.KeysFit` reads whether they belong together. Keys come from the client's checkout or a checkout linked to it, and
  a checkout with other keys cannot share it. When that can't be shown, the option does not appear and the copy stands.
- **A working link** is used as it is. Setup says where it leads, and Prepare client does nothing: it never writes through a
  link, because other checkouts may share the folder.
- **A link that leads nowhere**, or to a folder without a client, is replaced by the guide's copy or link. Deleting a link
  removes the link and nothing it points at. A link to a working client is never replaced, and a `Client` folder with files in
  it, such as a stopped copy, stays.

A requirement that is met shows no action, so Setup offers nothing that has nothing to do.

The player's own install is only ever read.

**LocalExile gets game settings of its own.** Retail, Steam and Epic all use `production_Config.ini` and
`production_login.credentials` in `Documents\My Games\Path of Exile`, and so does any client whose folder has no `config.json`
naming other files. A client started by LocalExile, from either a checkout or a release, starts in `Client`, so a
`config.json` there decides (`ClientSettings`):

| What Waypoint finds in `Client` | The guide |
|---|---|
| A folder of its own, naming no other file | Adds **Give LocalExile its own game settings**: `LocalExile\production_Config.ini` and `LocalExile\production_login.credentials` |
| A `config.json` naming another file | Leaves it: the player has chosen |
| A link, or a `config.json` it can't read | Asks on **Game settings**: anything else started from a linked folder would use the same settings. Leaving it changes nothing |

A new settings file starts as a copy of the shared one without its `[LOGIN]` section, or empty on a PC that never ran the game,
with `networking_type=predictive`, the preferred mode, so the player no longer sets it by hand (they can still pick another in
game: the server follows it). A file that already exists there is
kept, with only that line changed. The shared files are only ever read. `config.json` is written last, so a stop leaves the
client as it was, and other keys in it stay; one that can't be parsed is kept as `config.json.old`.

An existing setup changes only when the player asks: Setup's **Use separate game settings…**, shown until they are separate, and
Play's card, which a Simple mode player or a release sees once (`SettingsOffer`, remembered in `GameSettingsOffered`). Either
answer ends the card, and a guide that finished with a client counts as answered. The Linux guide has no such step: a Linux
client's Documents are those of its Wine prefix. LocalExile on its own uses one per install (`data/wine-prefix`), so Waypoint
names one for everything it starts, `~/.local/share/LocalExile/wine-prefix`, through `WINEPREFIX` (`GameSettings`): every
checkout and release shares it, it outlives them, and no other Path of Exile uses it. Waypoint creates its first settings there
with Networking Mode Predictive, from the install's older `data/wine-prefix` when it has one, and leaves them alone after that. A
prefix the shell names with `WINEPREFIX` is the player's, and is only read. A Gateway asked for its commands, values or crafter
data gets a temporary folder of its own: on Linux its start would otherwise delete a running Gateway's module copies.

On Linux (`src/Waypoint.Linux/Views/SetupWizard.axaml`) the guide is the same, with what running there takes. The release route
also passes through the Tools page, which lists umu-launcher (or a runner named in `Game_CLIENT_RUNNER`): `LocalExile.exe` runs
under Proton, and Next waits until it is found. The source route's tools include PowerShell 7, which runs the checkout's scripts.
The client's folder goes to `LocalExile.exe` as a `Z:\` path: Proton gives the install a drive letter of its own, so an
unqualified Unix path would land on that drive, while `Z:` is the root in every prefix.

### Setup

The requirements come from the adapter's inspection, each with what to do about it. **Prepare client** creates what is
missing from the client copy and keeps what exists (keys, `Game.patched.exe`), and sets the copy's gateway list to localhost.
**Install dependency** is `npm ci` in `Tools/ooz`. Both want the game and the servers stopped. Each step shows its
instructions first. The **Python** requirement runs the Python the extraction would use (the checkout's `.venv`, else the one on
PATH) and wants a 3.9 or newer, so the Microsoft Store's python shortcut does not count.

Frida is optional for ordinary play. On Windows, direct console commands and game status, Give item, walk maps, live mod
controls and game-data reload offer **Install Frida** when their Python cannot load it. Declining leaves the requested action
unstarted; Console keeps the unsent text. A queued mod build ends when preparation is declined or fails; only a busy
workspace is retried, so **Not now** does not reopen the prompt. Installation uses the checkout's `.venv`, shows output in
the requesting tool and Logs, and verifies that Frida loads before continuing. It shares the checkout lock with other operations and changes no global
Python packages. Linux explains that its game-control integrations are unavailable and does not offer installation for them.

### Mods

Mods, Modules and Leagues share the same title hierarchy, themed cards, badges, icon frames, refresh position and settings
footer. The info button beside each title explains their relationship: Mods installs add-ons for the checkout, Modules
configures shared gameplay systems, and Leagues chooses the modules used by each player-facing flag and linked mode.
The explanation opens on demand so it does not crowd the working area.

Laid out like an addon manager, with **My Mods** and **Browse** beside Refresh and **Open folder** (the section has no
heading of its own). Both lists use themed cards with small semantic icons, clear names, descriptions and quiet
author/type metadata. Installed manifest names take precedence; repository names and descriptions fill missing metadata
without changing mod IDs or operation targets. An `icon.png` in a mod's folder (a PNG of at most 1 MB and 1024 pixels a
side) replaces its symbol in both lists and on its page: the installed mod's, or its source project's, then its
repository's (`mods/<id>/icon.png`, needing no field in `mods.json`), which Waypoint keeps in `mod-icons\` of its data
folder and refreshes each time it reads the repository. A mod without a usable one keeps its symbol; unknown mods use a
puzzle icon. Each column's heading is a button: a click
sorts the list by that column (My Mods: Use, Mod, Client version, Action; Browse: Mod, Client version, Package status,
Action), a second click reverses it, and an arrow marks the column in use. An **Update** button is green wherever it shows
(a row, Update all, a mod's page), and its tooltip in My Mods names the version change.

**Client version** shows the LocalExile versions a mod declares it works with (`compatibleClientVersions` in its
`mod.json`, carried into the repository's `mods.json`; an installed mod whose own manifest has none shows its repository's),
in place of the mod's own version. Waypoint compares them with the checkout's LocalExile version: a release's
`release.json`, a source checkout's newest version tag (`git describe --tags --abbrev=0 --match v[0-9]*`), read again when
the section opens and on Refresh. A mod whose list does not hold that version gets a yellow exclamation mark (its tooltip
names both) in My Mods, Browse and on its page. It can still be installed, updated or built, after "This mod is
incompatible with your client version: … Are you sure you want to install it?". A mod that declares no versions, or a
checkout whose version cannot be read, is not judged. Search also finds the displayed repository metadata; the installed count reflects its filter. **Details** or the name opens
the mod page, while package actions and progress stay on the row. Unmatched installed packages are labelled **Installed**,
not assumed **Up to date** without repository evidence. **Open folder**, Refresh, and the rarer **More** tools stay available.

**My Mods** lists the installed mods (from the manifests under
`Mods/Binaries` and `Mods/Source`, script mods included: loose `.cs` files in `Mods/Binaries/<id>/`) with their client
versions and author; the Action column says what it needs (Update, Build, Reinstall) or that it is up to date. A row's check box turns the mod on or off at once: Waypoint rewrites
the server's `mod-state.txt` (no backup, no client), which a stopped Gateway reads at its next start and a running one
applies within a second (LocalExile since 2026-11-07; an older Gateway applies it at its next start). While Gateway runs,
Windows and Linux show a restart notice when the checkout's source does not identify the live-choice hook; release installs
also show the notice because they have no source to verify. The checkbox represents the saved choice, not proof of a live
load. A switch holds the workspace lock and is refused while another operation holds it. **Update** and
**Update all** appear when a repository has a newer version. The ⋮ menu holds the rarer tools: Install from folder…,
Build all mods, Check live status (Python with Frida), Gateway log, the operation log, and the backup choice.

**Browse** reads a repository's `mods.json` index (the official one is `LocalExile/LocalExile-Mods`; others are added
from its ⋮ menu by URL and get a trust warning) and its GitHub releases. **Install** and **Update** prefer a release asset
`<id>-<version>.zip` from the tag `<id>-v<version>`, checked against its SHA-256; a mod without a release is
installed as source (a source checkout only, after asking) and built when the player presses **Build** in My Mods,
which needs the .NET 10 SDK. Downloads are staged in Waypoint's `mod-downloads` folder. Install and settings-save paths
are checked using the platform's separators and path casing; sibling directories and parent traversal are refused.

Each install, build or uninstall shows on its mod's row in place of the button: a box that fills with the download,
then "Installing…", "Building…" or "Uninstalling…"; then the row shows the mod's state again (Up to date, as text, once it is built). Several can start at once (Update all
starts every update): downloads run side by side, and the checkout operations wait their turn, since the adapter runs
one at a time. Installs, uninstalls and builds waiting together run as one operation of their kind: one save backup, and the builds
in parallel. A failed one opens the operation log.

Clicking a row opens the mod's page: its README (Overview, drawn as GitHub-flavoured Markdown with Markdig; images
show their alt text, HTML is left out, relative links open the mod's folder on GitHub), its shared settings editor
(all declared types below; written to `data/mods/<id>.json`, which holds only what differs from the defaults,
and a running Gateway reloads the mod), and its releases (Versions, each installable). The page scrolls as one, its heading
and tabs with it; the back button stays above it, and the settings' Save bar below it on the Settings tab. Right-clicking a
row shows its actions; **Uninstall** asks first. A replaced or uninstalled mod is moved to `Mods/.replaced/<time>/`, never
deleted.

The mod page shows both the mod's versions (installed and latest, not repeated when identical) and its client versions,
with the incompatibility warning under them when there is one. A checked enable box or a successful build is never
presented as a live load; **Check live status** stays in My Mods' ⋮ menu. An up-to-date package is text rather than a
disabled action button.

**Settings** remains visible so an uninstalled mod, an installed package without a declaration, a release without support,
an empty declaration and a read/validation failure each have an explanation. Missing declarations do not imply that the mod
has no separate configuration files. Save, Discard and Reset stay below the scrolling form. Save requires an edited form
and the existing operation capability; refresh preserves an unsaved form for the same installed mod. Release settings
show no editor and no save/reset actions. Error details expand on demand. These are presentation changes over the same
validated settings writer, backup, expected-hash checks and serialized operations.

A release from v0.2.0-beta on is made for downloadable mods (it ships an empty `Mods\Binaries\` for Waypoint to fill):
Install, Update, Uninstall and settings editing work there as in a source checkout, from a mod's release (a release cannot
build a project from source). An older release (v0.1.0-beta, or one whose `release.json` has no readable version) keeps
the mods it shipped with, built for the older mod API: Waypoint leaves its saved mod switches available (the server
applies them at its next start) but disables install, update, uninstall and settings editing there
(`Repo.LegacyReleaseMods`). This is checked in the adapter (`Test-LegacyReleaseMods`) as well as in the interface. Simple mode on a release keeps its existing sections; Modules and Leagues are available
for read-only inspection in Advanced mode.

### Modules and Leagues

**Modules** lists gameplay systems from the selected checkout's `Modules/`, without loading server assemblies. Select one
to see its dependencies, the leagues using it and its settings. **Manage in Leagues** opens Leagues. Module
settings are shared by every league using that module, rather than being separate per league.

The module navigator uses the same selectable cards and icon defaults as the module rows in Leagues. **Find a module**
searches its display name, ID, path and description; the count and empty state reflect the filter. Filtering preserves the
current form while its module still matches, and asks before discarding an unsaved form if the filter would switch modules.
The selected module has a clear heading and icon, a **Shared settings** card, **League usage**, and collapsible dependencies.
Modules with settings show that card first. **Settings not exposed to Waypoint** means there is no declaration for the
launcher; separate module configuration files may still exist. **Settings support unavailable** means a declaration exists
but the selected LocalExile version lacks the supported shared-settings hook. **No editable settings** is shown only when
a declaration loads successfully and is empty. **Settings could not be loaded** identifies a reading or validation failure
inside the settings card, with expandable error details and a Refresh suggestion. These states never enable save/reset.
League usage counts definitions, including linked modes, rather than claiming they are separate player flags. The first
five names are visible and the remaining definitions expand on demand. Save and Discard stay in the fixed footer.

**Choose icon…** opens a shared library of 184 symbols with search and category filters, covering existing and possible
future modules (combat, magic, loot, crafting, exploration, encounters, progression, companions, nature, building and
administration). All 24 known modules have distinct defaults; the seven original distinctive icons are retained. Unknown
modules use the generic modules icon until the player chooses another. **Apply** changes that module's icon throughout
Modules and Leagues immediately, without rebuilding or discarding an unsaved settings form. **Cancel** keeps the previous
choice; **Use module default** selects its default for review before Apply. Icons use theme resources in both front ends.
The preference is scoped by checkout and module ID and saved only in Waypoint's settings, never in module manifests,
league choices or the client. Existing releases keep their capability guards, and icon selection does not enable editing
unsupported gameplay settings.

The **Global mods** summary stays above the league workspace while switching flags or modes. It lists the checkout's
saved enabled mod names (up to three, with the full list in its tooltip), separately from the last-checked live load count.
Saved choices never imply a successful live load. An unavailable/not-checked report is identified explicitly; an existing
report is always labelled **Last checked**, even if the server has since stopped or restarted. The summary never runs a
live inspection itself. **Manage in Mods** opens Mods without changing league choices.
Mods apply across all leagues, so the summary is not repeated inside individual league cards. Refresh or a workspace
inspection updates the summary without rebuilding an unsaved league form. Existing release/capability guards still apply.

**Leagues** lists the separate flags declared for the game's character-creation screen. Only explicit `entryOf` / `entryKind`
links make another definition a mode option on that flag. Sharing a game base, migration destination or Hardcore death
destination never merges flags: Hardcore Allflame remains separate from Allflame when its definition has no `entryOf`.
The flag count and order follow the checkout's startup catalogue (Standard first). Names and descriptions come from the
definitions; a description beginning with `#` is identified as a game text key rather than pretending to resolve client text.
The list uses selectable flag tiles with the native Standard/Hardcore banner artwork when it can be verified from the
selected client. The flag image is literal; surrounding controls do not attempt to copy the whole character-creation screen.
This is not a query of a running server's cached catalogue.

Artwork is read through the checkout's already-built `PoEDataExtractor cat` tool, never through a build or a server/client
launch. The optional reader needs Python with Pillow and the checkout's existing Node/ooz dependency. It validates the known
banner-selection contract, reads `LeagueFlag.datc64` and the atlas, resolves explicit selectors or the native generic fallback,
and crops the inclusive sprite rectangle from the DDS texture. Thus a custom league uses the same generic flag the client
would use, rather than invented art. Unsupported contracts or unavailable tools show an explicitly labelled placeholder.
No dependency is installed automatically. Extracted artwork stays in Waypoint's `league-art` cache, outside its source;
the client GGPK is only read. Cache identity includes client size/time, the presentation contract, helper and decoder version;
the reader rejects a changing client and rechecks cached image hashes.

The selected flag has a distinct heading and banner; secondary text is muted, and module rows use small consistent icons
with separate selection/required states. Neutral badges avoid competing status colours. **League settings** is
expandable so the flag and gameplay modules remain the main focus. Save/Discard stay in a fixed footer.

The flag's gear button opens **League settings → Flag appearance in Waypoint**. Automatic uses the theme accent for normal
flags and theme red for Hardcore. Theme accent, theme red, neutral and a custom colour are available per flag and checkout.
These personal choices apply immediately and are stored only in Waypoint's settings, not league manifests. They colour the
frame/fallback emblem, never tint the original artwork or affect the game. Frames adapt for contrast, grayscale and Windows
High Contrast; theme changes update them without dropping a draft. In-game artwork editing remains separate future work.

Select a flag, expand **League settings**, then choose **Game mode** to manage its default or an explicitly linked combination. **In-game mode links
and combinations** shows each exact checkbox path and destination league, including nested SSF/Hardcore/Ruthless links.
Hardcore or SSF already fixed on a separate flag does not become an invented checkbox. **Show hidden definitions** exposes
hidden/ended/unloaded cards and modes for inspection, clearly marked **not shown**; broken, cyclic or ambiguous links never
appear as selectable player modes. Unlinked modes are labelled as definitions, not invented flags. A mode with a hidden
ancestor is not offered as reachable. The default list includes only offered flags and reachable modes; editing compatibility
families remain a separate safety concern and never determine the player-facing grouping.

Modes stay separate internally for characters, stashes and death rules. Choosing a mode here browses that existing variant;
it does not convert any characters. The Game module is required; saving refuses
missing dependencies and modules. Ended and non-playable leagues are read-only. Hidden leagues remain configurable and usable as templates; hiding affects only creation availability. Choices are saved
in the user's `data/league-modules.json` overrides (or the selected profile's `Game_LEAGUE_MODULES` path), preserving other
leagues and leaving shipped manifests unchanged. Auth and Gateway read these choices at startup: Waypoint says to restart
the servers when ready and never restarts them automatically. Existing characters stay in their league. Deleting or
migrating leagues is not part of these controls.

The module list appears below the mode selector. **Find a module** searches names, IDs and descriptions without changing
the selection. Required modules stay readable and cannot be disabled; modules not defined for the mode are grouped under
**Unavailable modules**. Save, discard and validation feedback stay visible below the scrolling panel. Details, the card
preview and league settings are expandable. Search hides an empty unavailable group and updates its matching count.
Refreshing or applying a change locks editing until the operation finishes;
switching league or mode asks before discarding a draft.

**Visible to new characters** / **Hidden from new characters** reflects LocalExile's global creation list (`creatable` and
not `ended`), not an account-specific permission. **Hide…** hides the selected league and its linked mode descendants;
**Show…** also restores any parent cards it needs. Independent cards stay independent. At least one
playable main card must remain visible, and visible mode options cannot lose their parent. Hiding changes only `creatable`:
it does not end the league or prevent existing characters from entering it. Existing saves and stashes are not opened or
changed by visibility edits. Void and ended leagues cannot be offered with these controls.

Visibility editing requires stopped services/game and the source league-definition hook. After the optional backup,
Waypoint checks writers and the full reviewed inventory again. Multi-file edits preserve comments and formatting, retain
the original files in configuration history, apply children before hiding parents (parents before showing children), and
roll back their own writes on failure without overwriting another tool's changes. Visibility takes effect at the next
server start. Per-account visibility requires a separate LocalExile hook and is not implied by this global control.

Module selection is conservative. The whole selection must fit at least one existing non-Waypoint league definition with
the same mode-linked family, game base and exact mode flags/data set, rather than combining modules merely because each appears somewhere.
Dependencies must be present and acyclic. Game is required; dedicated non-Core bundles from the starting profile, and
their dependencies, stay together. Unverified modules are labelled and cannot be added. An already selected incompatible
module can be deselected to repair a draft; invalid selections cannot be saved. Parent chains must lead to Standard or Void.
The backend repeats these checks, even if an outside caller bypasses the interface.

**New league** creates a separate league in two steps. First choose the starting league and game mode, then enter a new
name and description. **Next: modules** opens the module choices and review; **Back to name and mode** keeps the draft.
Starting profiles use the same explicit flag grouping. Review states that creation adds one independent flag with the
chosen mode fixed, rather than adding mode checkboxes or rewriting another flag's links.
**Customize as a new league** starts with the selected profile and its current module choices. Profiles with custom code
or no reusable source template disable this action and explain why. A family with no SSF, Hardcore or Ruthless variant says that the option is unavailable: Waypoint never
invents unsupported flags. Modes of an existing league are not changed underneath its characters; another mode creates
another identity and card. New cards are independent, so they cannot displace an existing card's mode checkboxes.

Custom leagues use Void as their parent and, for Hardcore, their death destination. A valid isolated Void definition must
exist, hidden and without a parent; its module list may be empty or contain only the core Game foundation. The review and confirmation explicitly warn that Hardcore death in a custom league is permanent. Existing leagues, characters, stashes, shipped definitions and source are preserved. Creation requires the game and
all save writers stopped, uses the normal optional save backup, and checks the writers again afterward. A read-only save
inspection refuses names retained by characters, deleted-character records or stashes, and blocks unfamiliar schemas and
pending imports rather than guessing. Names use 3–48 English letters, digits, spaces, underscores or hyphens.
The final Create action and availability controls explain when running services block them; a creation draft remains
editable while services run. Name feedback catches invalid or currently used names before proceeding; retained save names
are checked again by the adapter before creation.

Only an existing empty league marker class is reusable. A profile with custom league code or only prebuilt binaries is
not silently cloned. Waypoint writes a known empty marker into the new league's Source folder, its module list, optional
settings declarations, and its own `.waypoint-league.json` marker. It publishes the complete league manifest last, without
replacing an existing directory. It never builds or starts LocalExile. The new league is available on the next server start.

New league files and visibility edits are the player's own checkout changes. Simple mode's existing update guard keeps
such a customized checkout in place instead of automatically moving it to newer source. Module overrides in ignored data
files keep their existing behavior.

Drafts review the complete relevant manifest/source inventory and user overrides. Adding, removing or changing a definition
invalidates the draft; the backend checks again after backup. These checks enforce the declared structure, not every hidden
assumption in arbitrary module code. LocalExile currently has no complete conflicts/supported-modes metadata contract.
Broader cross-profile combinations need explicit compatibility declarations from LocalExile or the modules; unknown
combinations remain blocked. The implemented controls require no LocalExile source changes.

**League card preview** shows the definition's name, description, Hardcore/SSF/form flags and linked mode variants in a
collapsed, read-only panel. It reads files, not the running server's startup snapshot. LocalExile already reads descriptions
from `league.json` after a restart; no server code change is needed to edit that file. A name also identifies saved characters
and stashes, and mode links select real gameplay variants. There is no separate display-name override. Waypoint therefore
does not edit league presentation or rewrite these definitions.

The adapter supplies the selected profile's module, league and override paths. A source checkout must expose the shared
settings hook before editing settings, and the league-override hook before editing membership. An older checkout or release
with no verified hook remains read-only. No listing command or server startup is used to discover these capabilities.

### Shared settings editor

Mods, modules and leagues use the same editor on Windows and Linux. Each setting is a card, its name and description on the
left and its control in a column on the right; in a card under 520 pixels wide the control moves under the text.

- On/off switches; text with length and pattern checks; single and multiple choices, including typed game-value sources.
- Numbers with exact-value boxes and sliders when bounded; percentages and multipliers are displayed in their declared units,
  and durations are edited in seconds. Step sizes and integer restrictions are checked before saving.
- Ranges have separate minimum and maximum controls; low cannot exceed high.
- Lists and maps have expandable, typed rows with Add and Remove. Map keys must be non-empty and unique; declared key
  options and value types are checked. Groups can contain further controls, lists, maps and groups.
- Advanced controls are hidden until requested. A setting's `when` conditions follow the other fields as they are edited.
- Descriptions and apply timing stay with each setting. A module reload, next-area change and restart are distinguished.

The editor preserves unknown values, including inside nested groups, applies declared renames, and stores only overrides.
Unreadable existing user files block editing instead of being silently replaced. Forms stay intact during background refresh;
Modules and Leagues ask before discarding a draft when switching selection or refreshing. Saving checks the reviewed file's
hash, so another tool's changes require a refresh. Changes use the shared workspace lock and the normal optional save backup.
Previous configuration files are copied to Waypoint's `config-history/<id>/` before replacement, separately from save backups.
Reset to defaults asks first. Mod values use `data/mods/`, module values `data/modules/`, and league values `data/leagues/`.

### Backups

Automatic backups run before a Gateway start, a switch of the checkout, and an account or mod change. A backup is a
consistent SQLite copy, checked for integrity, with what it was made from (`backup.json`). **Restore saves** stops the
servers, checks the backup, backs the current saves up, and moves them aside before putting the copy in place. Saved mod
choices restore separately, with the Gateway stopped.

### Logs and errors

Lines are classified by what they report. A server's `.err` is all errors. Elsewhere an exception, an error, a failure
or a crash is an error, and a warning or a slow handler ("took 73 ms") is a warning. MSBuild's "0 Error(s)" is not an
error. A server that exits while no `live.ps1` run is going on is reported as an error in the launcher's log and in
"All errors"; a client closed by its player is not.

**Diagnostic report** builds a report for a bug: the versions, the services, what is missing, the recent operations and the
log on screen, with paths, account and character names and credentials redacted. It is previewed first, more names can be
removed there, and it goes to the clipboard only when asked.

### Console

- Typing `/` opens the command list: the commands whose name starts with what is typed, then those that contain it.
  ↑/↓ choose, Tab or Enter take one, and Enter on what the list already shows sends the line.
- After the command, each argument has its own list. An argument the Gateway names a kind for comes from the Gateway's
  catalogs (`GameServer.Gateway values <kind>`, loaded once per kind): `/goto` the areas, marked server-built,
  captured or client-only; `/spawn_mob` the monsters; `/item` the item types and uniques; `/gem` every creatable gem
  variant; `/set_stat` and `/stat` the stats; `/npc` the NPCs; `/quest_flag` the flags. A name several rows share
  (Hillock, Small Life Flask) is listed once per row, by the name with the row's Id beside it, and taking one puts the
  Id in the line. A value of several words counts as given once it and a space are typed, the shared name too;
  `/spawn_mob`'s `@level` belongs to the monster. Every other argument is suggested from the usage's own choices
  (`[normal|magic|rare]`, `[all | <id>]`, "/cosmetic list | /cosmetic off"); a value to supply (`<area id or name>`,
  `[seed]`) is not one.
- The usage of the command being typed shows under the line. Without the list, ↑/↓ go through the history.
- A line without `/` is a chat line.
- The list on the right has every command with its explanation. Double-click puts it in the line.
- **★ Pin** makes the line a button; right-click a button to remove it.
  A pinned button only puts its line in the box, and Enter or Send sends it: a command that can take the client down
  is never one click away.
- Commands go to the client as typed chat, so the character must be in game. Without a client the bridge answers
  "Client is not running".
- The lists come from the Gateway the servers run (`src\GameServer.Gateway\bin\Debug`). When that build changes (Play,
  Restart Gateway or a hot reload after a pull), the next `/` reads the commands and their values again; **Reload
  commands** does it at once. The Gateway's catalogs write their own log lines beside the values ("[gateway] atlas: …"),
  which the launcher skips. The loaded mods' commands are not in the list.

### Tools: the item crafter

**Open item crafter** opens its window (one at a time; the button brings it forward): a window of its own in the
taskbar, with Minimize and Maximize, not kept in front of Waypoint's. It makes any item the server can
write and gives it to the character in game.

- **Base** (left): the item classes as icons (Material Design's, no game artwork), a search and the bases with their
  class, drop level and attribute requirements. Choosing one puts its implicits on the item; explicit modifiers the new
  base can have stay.
- **Add modifiers** (left): one kind at a time: prefixes, suffixes, the bench's crafted modifiers, what each influence
  on the item adds, corruption and Eldritch implicits, or any modifier without rules. Each shows its tier (T1 the best),
  its range as the game words it, its affix name and the item level it needs; **Best tier only** keeps the best tier for
  the item level of each group, **Above item level** also lists those that need more. Double-click, Enter or **+** adds one.
- The item (middle): rarity, item level, quality, sockets as `/sockets` takes them (shown as coloured, linked circles;
  empty for what a new item gets), the states and influences, a rare's name from the game's two word lists (or random),
  and the modifiers. Each modifier has its kind, a tier list to change tier, its line with the current values, a slider
  and a box per value (any whole number is accepted), and up, down and remove. The prefix and suffix count for the rarity
  shows above them, and two modifiers of one group are marked; neither is refused.
- **Preview** (right): the item as text in the game's rarity and modifier colours (readable on every scheme), with its
  name, item level, requirements, sockets, implicits, modifiers and states.
- **Save** keeps it in **Saved** (left) with an optional label, in `crafted-items.json` beside Waypoint's settings:
  Waypoint's own file, never in the checkout. A saved item opens with a click; **New item** starts again. The icons
  beside it duplicate the selected item and delete the selected ones (Ctrl or Shift selects several, the Delete key
  deletes them too); each row also has its own delete icon under the mouse. Deleting asks first; the item on screen stays.
- **Import item** (or Ctrl+V outside a text box) opens a window with the item's text: what is on the clipboard when it
  looks like an item, editable. It takes Path of Building's Ctrl+C and the game's Ctrl+C or Ctrl+Alt+C. Under the text,
  every line shows what it becomes (a field, a modifier with its affix name and Id, or skipped); the lines left out are
  red, and so is the line that stops the import (a unique, a base this Gateway lacks). Clicking a line selects it in the
  text. **Add** opens the item as a new, unsaved one: its base, rarity, item level, quality, sockets, states and
  influences, a rare's name (as its label, and as the name when it is two of the game's words), and its modifiers.
  Each line is matched to the modifier the game words that way: the Mods.dat Ids PoB writes for an item crafted in it,
  and the game's tier ranges and affix names (Ctrl+Alt+C, a header per modifier) come first, then the base's own pools;
  the values are the ones that give the line's numbers (outside the tier's range only when no tier gives them).
  Enchantments and lines no modifier words are left out. Uniques are refused: the crafter makes normal, magic and rare
  items. `ItemImport.cs`, `ItemImportWindow`.
- **Give item** sends `/item_give <the item as JSON>` through the client control bridge, as the Console does, so the
  character must be in game; the item goes into the main inventory and the Gateway's reply shows in the status line.
  When Frida is missing, it offers to install it into the checkout's `.venv` before sending. The copy button puts that command
  on the clipboard, but the game's chat box stops at 175 characters and a rare's command is about 530, so it says so when the command is too long: the
  bridge sends the same message without that limit.
- **Reset** starts over as the window opens: an empty item, every search and filter cleared, the Base page. It asks
  first when the item on screen has changes no save holds; saved items stay.

The data is the Gateway's own (`GameServer.Gateway crafter`, read once per Gateway build): the bases, the modifiers each
can have (the server's own rolling rules), their stat descriptions in English (the checkout's
`data/stat-descriptions.txt`) and the name words. `CrafterCatalog.cs` reads it and words the modifiers.

A Gateway built before the hook does not know `crafter` and would start for real, so Waypoint first asks `commands`,
which is safe, and goes on only if that lists `/item_give`, which came with the hook. If not, the window says the
checkout needs updating and building; nothing starts.

### Tools: the lab layout

**Open lab layout** opens a window of its own (one at a time) with the day's Labyrinth as a map, as poelab.com draws it,
but built by the checkout's own Gateway: no layout is downloaded, from poelab.com or anywhere else. The tabs pick the
tier (Normal, Cruel, Merciless, Eternal); the arrows, the date and **Today** pick the day, a UTC day as the official
server's, and today's shows the time left until the next layout (UTC midnight), then moves on to it.

- The map: the rooms at their places on the in-game Labyrinth map (each section's LabyrinthSectionLayout positions,
  section after section), numbered left to right, with their connections and the way in from the plaza under the first
  room. The shortest way from the first room to Izaro's last fight is drawn in the accent colour, every one when
  several are as short. Over a room are what it holds, as Material Design icons (no game artwork): Izaro, Argus, a trap
  or escort gauntlet, a switch puzzle, a Darkshrine, a silver key or cache, the gold key and door, the Forgotten
  Reliquary, a secret passage. The wheel zooms at the pointer, a drag moves the map, and a double-click on an empty
  place or the fit button fits it again.
- Doors: a dot on a room's circle for each connection, and the connection drawn from dot to dot. The dots are a
  scheme, not the doors' places: in an Izaro room the way in is on the left and the way on on the right; in any other
  room the way in (the plaza's door, else the neighbour nearest the start) is at 6 o'clock, and the other doors each at
  one of 10, 12 and 2 o'clock, in the left-to-right order of their neighbours on the map and as near their direction
  as that allows (a fourth door shares the nearest dot, whose popup then lists both). Two doors on one side of an Izaro
  room are at 10 and 8 o'clock, or 2 and 4 (`LabTier.DoorSides`).
- Clicking a room, an icon or a door shows its details in a popup (the room, its area, Izaro's fight, what it holds,
  where a door leads). Clicking a connection marks it green over the rooms, as the game shows a way, and clicking it
  again unmarks it; **Clear marks** in the notes clears the tier's marks.
- The notes (right): the shortest way's length, each of Izaro's fights with the mechanics the layout gives it (a
  mechanic's companion carries it into the later fights; the official server may add one of its own), the gauntlets
  and puzzles by room, and the rooms of Argus, the Darkshrines, keys, caches and doors.

The layout is the Gateway's (`GameServer.Gateway labyrinth <yyyy-mm-dd>`, read once per Gateway build and day): the
client's own layout builder for the day's seed, the areas the official server picks, and each room's secrets
(LabyrinthSecrets: `Hidden` is a Darkshrine, `Payload` the escort gauntlet). `LabyrinthDay.cs` reads it.
When the server's seed is fixed (`Game_LAB_SEED`), its layout is the same on every day, and the window says so.
A Gateway built before the mode would start for real, so Waypoint first asks `commands` and goes on only if
`/lab_layout` takes a date, which came with the mode. Windows only, like the map viewer.

### Accounts

The list is read from the checkout's save database through the adapter, with nothing running or built: every account with
its characters, nothing secret. Showing the section reads it; **Reload accounts** reads it again.

**Register** and **Create** are the dev protocol's `register`, `login` and `create_character` (the server's GettingStarted
step 8), so they work beside running servers. The character is created in Standard; the game's own creation screen offers
the other leagues. The dev host stays running afterwards (it shares `data\saves.db` with Auth and neither caches it);
**Stop dev host** ends it, and so do Stop all and stopping everything on exit. Its output is the checkout's
`logs\launcher-host.log`.

**Create account…**, **Duplicate…**, **Rename…**, **Set password…** and **Delete account…** change the database directly, so
they want the game and every server stopped, are reviewed in their dialog, are backed up first, and are refused when the
account changed since the list was read ([account-management.md](account-management.md)). A database whose layout the
adapter does not know can be browsed but not changed.

## Code

| File | Role |
|---|---|
| `App.xaml.cs`, `Repo.cs` | startup, the single instance, the server folder (saved, chosen, changed) and the checkout's paths |
| `LiveScript.cs` | `LiveRequest` (the buttons as `live.ps1` switches) and the PowerShell runner |
| `ServiceMonitor.cs` | the services, found by WMI every two seconds, sampled every second |
| `Shell.xaml`, `Theme.cs`, `ThemePalette.cs`, `WaypointArtwork.cs`, `WindowFrame.cs`, `SnapCaption.cs`, `MainWindow.xaml` | the look: Material Design's controls restyled, the palette and the `Shell.*` colours, the emblem, the sidebar, header and status strip |
| `Dialog.cs`, `DialogWindow.xaml`, `FormWindow.cs` | the message boxes; the dialogs with fields (the first start's checkout chooser, the close choice, the diagnostic preview) |
| `AppearanceDialog.cs`, `ColorPickerDialog.cs`, `AccountActionDialog.cs`, `WindowBehaviorChoices.cs` | the appearance, an account change, the close choices |
| `Workspace.cs`, `OutputPanel.xaml` | the app's side of the adapter: the last inspection, the operations and their output |
| `SetupTab`, `ModsTab`, `BackupsTab`, `SettingsTab` | those sections |
| `ToolsTab`, `ItemCrafterWindow`, `CrafterCatalog.cs`, `CraftedItems.cs`, `ItemImport.cs`, `ItemImportWindow` | the Tools section; the item crafter, the Gateway's crafter data and the game's wording of modifiers, the saved items, importing an item copied from Path of Building or the game |
| `Loc.cs`, `src/Launcher.Core/Localization.cs`, `src/Launcher.Core/Resources/` | interface text in XAML and code, the languages (`L.Languages`) and their catalogs |
| `src/Launcher.Core/` | the adapter's client (`RuntimeClient.cs`, `BridgeScripts.cs`), the mods, backups, accounts, setup guidance, checkout discovery, diagnostics, the single instance |
| `scripts/` | the adapter: `RuntimeBridge.ps1` and its parts, `SaveBackup.py`, `AccountData.py` |
| `RuntimeCounters.cs`, `Metrics.cs`, `TimeSeries.cs`, `LineChart.cs` | the runtime counter sessions, the history, the charts (drawn directly: the WPF chart libraries pull in .NET Framework builds of SkiaSharp) |
| `Git.cs`, `PullRequests.cs`, `VersionsTab.xaml.cs` | git in the checkout and the parsing of its output, the open pull requests, the Versions section and its guards |
| `PlayTab.xaml.cs`, `CheckoutUpdates.cs` | Simple mode's home; when Play may update the checkout |
| `EditionSwitch.xaml.cs`, `Editions.cs` | Simple mode's Release \| Latest switch: the two folders, the saves questions |
| `OperationTimings.cs`, `TimingsTab.xaml.cs` | the Gateway's operation timings (its meter's histogram intervals) and their window |
| `LogTail.cs`, `LogHub.cs` | following the logs, the severities |
| `CommandCatalog.cs`, `Completion.cs`, `ClientBridge.cs` | the Gateway's command list and argument values, the suggestions, the bridge process |
| `DevHost.cs`, `AccountList.cs` | the dev host and its account list |
| `SetupChecks.cs`, `Settings.cs` | the setup checks, the per-user settings |
| `SetupWizard.xaml`, `PortCheck.cs` | the setup guide; what holds a server's port, and the question about it |
| `src/Launcher.Core/SetupGuide.cs`, `ClientLocator.cs`, `ClientCopy.cs` | the guide's engine: tools that can be stopped, the prerequisites, the steps, finding a client, the resumable copy |
| `src/Launcher.Core/SwitchToSource.cs` | moving from a release to a source copy: what the choices will do, the older version (`TestedCommit`), Frida, removing the release |
| `Tray.cs` | the tray icon (Windows Forms' NotifyIcon) |
| `branding\` | the emblem: `waypoint-master.png`, the reviewed source and how it was made (`waypoint-artwork.json`); `app.ico` of the exe, the window and the tray (16 to 256 px) and the 192 px image of the sidebar, both from `scripts\New-Icon.ps1` |
| `Updates.cs`, `MainWindow.xaml.cs` | the version, the release check, the self-update; the update strip |
| `InstallTab.xaml`, `LocalExileRelease.cs` (Launcher.Core) | a release install's home; its versions, download, safe extraction, saves copy and `--json` runner |

Tests: `tests/Launcher.Checks` and `tests/*.Tests.ps1` run the core and the adapter on throwaway fixtures (mods, backups,
accounts, settings, discovery, the catalogs, the single instance). `tests/Waypoint.Tests` (the palettes, the `live.ps1` command lines, the server folder, following logs, severities,
command and argument suggestions, the dev host's account list, finding the services, versions and releases, counter
payloads, histogram tags and quantiles, the timings window, chart scales, origin's branches, remote URLs, the launcher's
stashes, pull requests and forks, ages).
`GitUpstreamTests` runs real git on throwaway repositories: a fork as origin against LocalExile, where each branch comes
from, updating, the refusal over a local commit, a pull request, going back.
The other switches (a force-pushed pull request, stash and restore) were run against a throwaway repository with its own
origin; they have no unit test.
The server's side of these hooks has its own tests there (`ChatCommandCatalogTests`, `HostTests`).
