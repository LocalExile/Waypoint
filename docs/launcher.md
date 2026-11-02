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
scripts are embedded in the exe (`Launcher.Core`) and unpacked once per version to
`%LOCALAPPDATA%\EmulatorWorkspaceLauncher\adapter\<hash>`, so a self-update, which replaces one file, carries them.
`Workspace.cs` is the app's side of it. The adapter still has its own start, stop and branch-switch actions, covered by its
tests; the app does not use them.

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
- **Updates**, for a download: **Stay on a tested version** puts the copy on its own branch `waypoint-tested` at
  `SwitchToSource.TestedCommit`, which Play never moves, because only a plain copy on the default branch is updated. **Get the newest
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
they are. The adapter's operation logs and the save backups stay under `%LOCALAPPDATA%\EmulatorWorkspaceLauncher`, so
backups made by an earlier Waypoint are still listed.

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

The interface is in English or Russian (**Settings → Language**, at once; [localization.md](localization.md)).

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

**Go back to the version you had** stays for a week after an update: the same way back, on request, and updates pause for a
week. Restoring keeps a copy of the current saves first. A checkout follows `main`, which has no test results, so an update is
`main` as it is: the way back catches a build or start that fails, not a bug in the game. A release install is different: see
[Release installs](#release-installs).

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
| Tools | tools for the game itself, each in its own window: the item crafter | the server's item crafter hook (its `Agents/docs/dev/item-crafter.md`): `GameServer.Gateway crafter` and `/item_give` through the client control bridge |
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
- Only one `live.ps1` runs at a time; the buttons wait while one does (the status strip says so). The launcher runs it with Windows PowerShell and
  turns off MSBuild's reused nodes for it, the fix the server's troubleshooting table names for a script that never
  returns. It takes the script's end from the process's exit, not from the end of its output.
- The **Setup** list checks the server's one-time steps (**Guided setup** opens the Setup section): the client copy, the keys, the patched client, the gateway
  list, the extracted data, Node and ooz, whether the checkout has the launcher hooks, the Predictive network mode in
  `production_Config.ini`, and Python with Frida. **Game data** runs `extract-data.ps1 -List`.
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
and **Check again** reads the PATH again, so Waypoint need not restart). It lists the LocalExile checkouts it finds, or offers to
clone upstream into a new or empty folder, with the drive's free space. It lists the clients it finds (other checkouts'
`Client` folders, what the installers recorded in the registry, the default folders, a time-limited walk of the fixed drives),
and when there is none it says where it looked. Steam and Epic installs are listed struck out, so a player sees why theirs was
passed over; they have no `Content.ggpk` and cannot be chosen. **Browse…** takes any folder. `PathOfExile.exe` has no version
resource, so the 3.29.3b build is recognized by its SHA-256 (`ClientLocator`): another one is warned about before a 70 GB copy,
but not refused. **Set up the rest now** goes on without a client. The game is never downloaded.

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

On Linux (`src/Waypoint.Linux/Views/SetupWizard.axaml`) the guide is the same, with what running there takes. The release route
also passes through the Tools page, which lists umu-launcher (or a runner named in `Game_CLIENT_RUNNER`): `LocalExile.exe` runs
under Proton, and Next waits until it is found. The source route's tools include PowerShell 7, which runs the checkout's scripts.
The client's folder goes to `LocalExile.exe` as a `Z:\` path: Proton gives the install a drive letter of its own, so an
unqualified Unix path would land on that drive, while `Z:` is the root in every prefix.

### Setup

The requirements come from the adapter's inspection, each with what to do about it. **Prepare client** creates what is
missing from the client copy and keeps what exists (keys, `Game.patched.exe`), and sets the copy's gateway list to localhost.
**Install dependency** is `npm ci` in `Tools/ooz`. Both want the game and the servers stopped. Each step shows its
instructions first.

### Mods

The list comes from the mods' manifests under `Mods/Binaries` and `Mods/Source`. A choice made with the Gateway stopped
applies at its next start; with a client in a playable area, **Enable**, **Disable** and **Reload** act on the running
Gateway through the client control bridge, and affect every character on it. Live status needs Python with Frida.

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
- **Save** keeps it in **Saved** (left) with an optional label, in `%LOCALAPPDATA%\Waypoint\crafted-items.json`:
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
  Without Frida it offers to install it into the checkout's `.venv`. The copy button puts that command on the clipboard, but
  the game's chat box stops at 175 characters and a rare's command is about 530, so it says so when the command is too long: the
  bridge sends the same message without that limit.
- **Reset** starts over as the window opens: an empty item, every search and filter cleared, the Base page. It asks
  first when the item on screen has changes no save holds; saved items stay.

The data is the Gateway's own (`GameServer.Gateway crafter`, read once per Gateway build): the bases, the modifiers each
can have (the server's own rolling rules), their stat descriptions in English (the checkout's
`data/stat-descriptions.txt`) and the name words. `CrafterCatalog.cs` reads it and words the modifiers.

A Gateway built before the hook does not know `crafter` and would start for real, so Waypoint first asks `commands`,
which is safe, and goes on only if that lists `/item_give`, which came with the hook. If not, the window says the
checkout needs updating and building; nothing starts.

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
| `Loc.cs`, `src/Launcher.Core/Localization.cs`, `src/Launcher.Core/Resources/` | interface text in XAML and code, the English and Russian catalogs |
| `src/Launcher.Core/` | the adapter's client (`RuntimeClient.cs`, `BridgeScripts.cs`), the mods, backups, accounts, setup guidance, checkout discovery, diagnostics, the single instance |
| `scripts/` | the adapter: `RuntimeBridge.ps1` and its parts, `SaveBackup.py`, `AccountData.py` |
| `RuntimeCounters.cs`, `Metrics.cs`, `TimeSeries.cs`, `LineChart.cs` | the runtime counter sessions, the history, the charts (drawn directly: the WPF chart libraries pull in .NET Framework builds of SkiaSharp) |
| `Git.cs`, `PullRequests.cs`, `VersionsTab.xaml.cs` | git in the checkout and the parsing of its output, the open pull requests, the Versions section and its guards |
| `PlayTab.xaml.cs`, `CheckoutUpdates.cs` | Simple mode's home; when Play may update the checkout |
| `OperationTimings.cs`, `TimingsTab.xaml.cs` | the Gateway's operation timings (its meter's histogram intervals) and their window |
| `LogTail.cs`, `LogHub.cs` | following the logs, the severities |
| `CommandCatalog.cs`, `Completion.cs`, `ClientBridge.cs` | the Gateway's command list and argument values, the suggestions, the bridge process |
| `DevHost.cs`, `AccountList.cs` | the dev host and its account list |
| `SetupChecks.cs`, `Settings.cs` | the setup checks, the per-user settings |
| `SetupWizard.xaml`, `PortCheck.cs` | the setup guide; what holds a server's port, and the question about it |
| `src/Launcher.Core/SetupGuide.cs`, `ClientLocator.cs`, `ClientCopy.cs` | the guide's engine: tools that can be stopped, the prerequisites, the steps, finding a client, the resumable copy |
| `src/Launcher.Core/SwitchToSource.cs` | moving from a release to a source copy: what the choices will do, the tested version, Frida, removing the release |
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
