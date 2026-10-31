# Desktop launcher

A Windows window over the development workflow of a [LocalExile](https://github.com/LocalExile/LocalExile) server
checkout: buttons for its `scripts/dev/live.ps1`, the services' state, live performance charts, every log, the
accounts, and a console for the Gateway's chat commands. It changes nothing in how the game runs: every start, restart
and stop is a `live.ps1` switch, so the rules of the server's
[GettingStarted](https://github.com/LocalExile/LocalExile/blob/main/Agents/GettingStarted.md) (one client, only through
`live.ps1`, from the checkout's root) still apply.

The launcher references no server project. It drives the checkout through what the server offers outside tools:
`live.ps1` and its `-Stop`, the Gateway's `commands` and `values <kind>` modes, the dev host's JSON-lines protocol
(`accounts`, `register`, `create_character`), the client control bridge and the server's logs. A checkout without them
(older than the server's launcher hooks) shows as too old in the Setup list.

## Releases and updates

A release is a git tag `v<major>.<minor>.<patch>` pushed to GitHub. The release workflow
(`.github/workflows/release.yml`) runs the tests, publishes `LocalExileLauncher.exe` with the tag's version
(`-p:Version`), writes its SHA-256 to `LocalExileLauncher.exe.sha256`, and attaches both to a GitHub release of the tag
with generated notes. A tag that is not `v1.2.3` fails the workflow. The CI workflow (`ci.yml`) builds and tests every
push to `main` and every pull request.

```powershell
git tag v0.2.0
git push origin v0.2.0
```

The version shows in the window's title. A build from source is 0.0.0 ("built from source") and never offers updates.

A release build asks GitHub's public API for the latest release of `Updates.Repository` (jetstream1128/LocalExile-Launcher until the move to the LocalExile organization) when it starts (**Look for
updates at start** in the header's settings; **Check for updates** there asks at once and says what it found). A newer
version shows a strip under the header. **Update now**, after a confirmation:

1. downloads the release's `.sha256`, then its exe beside the running one (`LocalExileLauncher.exe.new`);
2. refuses an exe whose SHA-256 differs, changing nothing;
3. renames the running exe to `.old` (Windows lets a running exe be renamed, not replaced) and gives the new one its
   name, renaming the old one back if that fails;
4. starts the new launcher and closes this one without asking about the servers, which keep running;
5. the new launcher deletes the `.old` when it starts.

Only the published single-file exe updates itself; under `dotnet run` the bar offers the release page only. A transferred
repository's old name keeps answering, so builds made before a transfer still find the releases.

## Build and run

```powershell
dotnet publish src/GameServer.Launcher -o .     # one LocalExileLauncher.exe in this repository's root (git-ignored)
.\LocalExileLauncher.exe                        # or double-click it
dotnet run --project src/GameServer.Launcher    # while working on it
dotnet test LocalExileLauncher.slnx             # the tests
```

To update it after a pull or a change, close the launcher first (a running `LocalExileLauncher.exe` is locked and the
publish fails to replace it; answer "No" to keep the servers and the game running), run the same `dotnet publish`, and
start it again.

It needs the .NET 10 Desktop Runtime, which the .NET 10 SDK installs on Windows.

### The server folder

On its first start the launcher asks for the server checkout: the folder with `GameServer.slnx` and
`scripts\dev\live.ps1`. The picker starts at a `LocalExile` folder beside this repository when there is one (the usual
layout: `LocalExile\` and `LocalExile-Launcher\` side by side). A folder that is not a checkout is refused. The choice is
saved; **Change…** in the Control section's Setup panel picks another one and starts the launcher again on it. Started from a
checkout's root (`dotnet run` there), the launcher uses that checkout when none is saved.

At startup the launcher makes the checkout its working directory, so every path it uses (`logs\`, `data\`, `Client\`,
`src\…\bin\Debug`) is the checkout's, as for the server's own tools.

Its own settings (the server folder, the switches, the theme, pinned console lines, the history) are per Windows user in
`%LOCALAPPDATA%\LocalExile\launcher.json`, never in a repository.

## Look

The window is a blue sidebar with the sections, a header (the section's name, the checkout's branch, the theme, the
launcher's settings) and a status strip on every section: whether the servers listen, whether the game runs, whether a
`live.ps1` run is going on, and a link to the logs. A newer release shows as a strip above it.

The controls are [Material Design in XAML](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit)'s,
restyled flat in `Shell.xaml` (which `App.xaml` merges): 30 px buttons with 4 px corners and no shadows, an outlined one by default and one blue
primary button per view, thin borders, tables with small grey column titles. The launcher's own colours are the
`Shell.*` brushes: Shell.xaml holds the light ones and `Theme.cs` swaps every one of them, with Material's base theme and
primary colour, for the dark theme. Views use them through `DynamicResource` (code through `Theme.Brush`), so a switch
reaches everything at once; a colour written into a view would stay light in the dark theme.

The theme button in the header goes System → Light → Dark. System follows the Windows app theme, also when it changes
while the launcher runs; the title bar turns dark with the dark theme (`DWMWA_USE_IMMERSIVE_DARK_MODE`). The choice is
saved. The branch is read from the checkout's `.git/HEAD` (a short commit when detached); clicking it opens the
Versions section. The settings button holds **Tray while the game runs**, **Look for updates at start** and **Check for
updates**.

Questions and notices are the launcher's own message boxes (`Dialog.cs`), in its look and theme, never Windows'
MessageBox: the kind's icon, a title, the text, details such as a list of files in a scrolling box, and buttons named
after what they do ("Stash and switch", not OK). Enter presses the main one, Esc or closing the box cancels; a risky
question (a fork's pull request) has Cancel as its default.

## Sections

| Section | What it does | How |
|---|---|---|
| Control | Play, Start servers, Stop servers, Stop all and the Hot reload, Skip build, Skip data check and Walk maps switches; every service as a table row with its state and one Start/Stop button; the script's output; the server folder and the setup checks | `live.ps1`, `-NoClient`, `-Stop -NoClient`, `-ClientOnly`, `-Stop`, `-Only <server>`, `-Walkmaps`, `-NoBuild`, `-NoExtract`, `-NoHotReload`; Close game ends the client's process, Stop all also the dev host |
| Versions | what the checkout runs: origin's branches and the open pull requests, switching between them, updating the current one, going back to the default branch, restoring stashed changes, deleting the branches of closed pull requests | git in the checkout (`fetch --prune`, `switch`, `pull --ff-only`, `stash`, `fetch origin pull/<n>/head`); the open pull requests from GitHub's public API |
| Performance | CPU and memory of every service; for the chosen server, GC heap, allocation rate, collections, time in GC, thread pool, lock contention and exceptions | the processes once a second; the runtime's `System.Runtime` counters through its diagnostics port (what `dotnet-counters` reads), with nothing changed in the servers |
| Server timings | what each Gateway operation costs over a window (30 s to 15 min, 1 min by default): calls per second, average, p95 and p99 (the worst second's), milliseconds per second; client messages by id, server phases (input queue, handler, send and wire gates, combat lock, surround, blocker build), level generation by area; the five busiest charted (p95 and ms per second) | the Gateway's `LocalExile.Gateway` meter (`localexile.gateway.duration`, ms, tags `phase` and `operation`) through the runtime's `System.Diagnostics.Metrics` event source, in the Gateway's counter session, published once a second; a checkout older than the `Message` and `LevelGen` phases shows only the phases |
| Logs | every server's `logs\live-*.log` with its `.err`, the walk-map recorder, the dev host, the client's `Client\logs\Client.txt`, the launcher's own lines, and "All errors" from all of them | follows each file twice a second; a file `live.ps1` recreates starts over; a large file from its last 512 KB |
| Accounts | the accounts and their characters; registering an account; creating a character | the server's dev account service (`GameServer.Host`, its `Agents/docs/dev-server.md`): `accounts`, `register`, `create_character`; the launcher builds and starts it when needed |
| Console | the Gateway's chat commands for the character in game, with suggestions, usages, pinned lines and history | the server's client control bridge (`scripts/dev/client_control.py --stdio`); the lists come from the built Gateway (`GameServer.Gateway commands` and `values`) |

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
- **Play** is plain `live.ps1`: it builds, restarts the servers and starts the client. It asks first if a client is
  running, because `live.ps1` closes it.
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
- The **Setup** list checks the server's one-time steps: the client copy, the keys, the patched client, the gateway
  list, the extracted data, Node and ooz, whether the checkout has the launcher hooks, the Predictive network mode in
  `production_Config.ini`, and Python with Frida. **Game data** runs `extract-data.ps1 -List`.
- The services are found by process: a hot-reloaded Gateway by its own `GameServer.Gateway.exe` (rather than the
  `dotnet watch` around it), the other servers and a Gateway without hot reload by `GameServer.<Name>.dll` on a dotnet
  command line, the client by `Game.patched.exe`, the recorder by `walkmaps.py`. A port that listens without such a
  process shows as held by another program.
- Closing the launcher asks whether to stop the servers and the game; "No" leaves them running.
- **Tray while the game runs** (on by default, in the header's settings): when a client starts, the launcher hides in the tray, and when no
  client is left it comes back. A client that Play closes and starts again keeps it in the tray, and a launcher opened
  while the game already runs stays open. The tray icon's menu shows the window, stops everything or closes the
  launcher.
- A failure on a background thread (the process sampling, a runtime counter session, the log reading) is reported in the
  launcher's log, never ends the launcher: a server that `live.ps1` stops ends its counter stream anywhere, even inside
  a block. Anything that still ends the process is written to the checkout's `logs\launcher-crash.log`.

### Versions

- The section lists origin's branches (the default one first) and the open pull requests of the GitHub repository
  origin points at, with their last commit or title, author, age, and a **draft** or **fork** label. Showing it
  fetches (`git fetch --prune origin`) when the lists are older than five minutes; **Fetch** does at once. GitHub's API
  is used without a token (60 requests an hour), so the pull requests are read again only on Fetch or after a minute.
- **Switch** to a branch switches to the local branch of that name, moved forward to origin's when that loses nothing
  (a local branch with commits of its own is left as it is), or makes one that tracks origin's. **Switch** to a pull
  request fetches its head (`pull/<n>/head`) into the local branch `pr/<n>`, again on each switch and on **Update**, so
  a force-pushed pull request comes in whole. It refuses when `pr/<n>` has commits of the player's own: the launcher
  remembers the head it fetched last (`refs/launcher/pr/<n>`), so commits a force-push left behind are not taken for
  the player's.
- Every switch has the same guards, in this order. A pull request from a fork (or a deleted one) asks once, with the
  author's repository named: its code, `live.ps1` and the scripts included, runs on this computer with the player's
  rights. The agreed ones are kept in the settings. Uncommitted changes (the ignored `data\` and `Client\` are not
  any) are listed and stashed as `launcher: <branch> before switching to <target>`; going back to that branch offers
  them back, and **Restore stashed changes** next to the branch's name does it later. Running servers or a running game
  are stopped first (`live.ps1 -Stop` and the dev host), or a watched Gateway would rebuild halfway through. A switch
  that fails puts the stashed changes back.
- git and `live.ps1` never run at once: a switch takes the same lock as a `live.ps1` run, and the status strip says
  which one runs. After a switch the header's branch and the setup checks are read again, and the launcher offers Play,
  which builds the new version (`live.ps1` regenerates the game data the version's manifest changed).
- **Update** is `git pull --ff-only` on a branch, a new fetch on a pull request. **Back to main** switches to origin's
  default branch. **Clean up** deletes the `pr/<n>` branches of pull requests that are no longer open.
- git runs with `GIT_TERMINAL_PROMPT=0`: the server repository is public, and a credential prompt would hang a hidden
  process. git's output goes to the section's output and the launcher's log.

### Logs and errors

Lines are classified by what they report. A server's `.err` is all errors. Elsewhere an exception, an error, a failure
or a crash is an error, and a warning or a slow handler ("took 73 ms") is a warning. MSBuild's "0 Error(s)" is not an
error. A server that exits while no `live.ps1` run is going on is reported as an error in the launcher's log and in
"All errors"; a client closed by its player is not.

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

### Accounts

The list is the dev host's `accounts`: every account with its characters, nothing secret. Showing the section reads it
when the host already runs; **Refresh** starts the host first. Registering and creating a character are the dev
protocol's `register`, `login` and `create_character` (the server's GettingStarted step 8). The character is created in
Standard; the game's own creation screen offers the other leagues. The dev host stays running afterwards (it shares
`data\saves.db` with Auth and neither caches it); **Stop dev host** ends it, and so do Stop all and closing the launcher
with "Yes". Its output is the checkout's `logs\launcher-host.log`.

## Code

| File | Role |
|---|---|
| `App.xaml.cs`, `Repo.cs` | the server folder (saved, chosen, changed) and the checkout's paths |
| `LiveScript.cs` | `LiveRequest` (the buttons as `live.ps1` switches) and the PowerShell runner |
| `ServiceMonitor.cs` | the services, found by WMI every two seconds, sampled every second |
| `Shell.xaml`, `Theme.cs`, `MainWindow.xaml` | the look: Material Design's controls restyled, the `Shell.*` colours of each theme, the sidebar, header and status strip |
| `Dialog.cs`, `DialogWindow.xaml` | the launcher's message boxes |
| `RuntimeCounters.cs`, `Metrics.cs`, `TimeSeries.cs`, `LineChart.cs` | the runtime counter sessions, the history, the charts (drawn directly: the WPF chart libraries pull in .NET Framework builds of SkiaSharp) |
| `Git.cs`, `PullRequests.cs`, `VersionsTab.xaml.cs` | git in the checkout and the parsing of its output, the open pull requests, the Versions section and its guards |
| `OperationTimings.cs`, `TimingsTab.xaml.cs` | the Gateway's operation timings (its meter's histogram intervals) and their window |
| `LogTail.cs`, `LogHub.cs` | following the logs, the severities |
| `CommandCatalog.cs`, `Completion.cs`, `ClientBridge.cs` | the Gateway's command list and argument values, the suggestions, the bridge process |
| `DevHost.cs`, `AccountList.cs` | the dev host and its account list |
| `SetupChecks.cs`, `Settings.cs` | the setup checks, the per-user settings |
| `Tray.cs` | the tray icon (Windows Forms' NotifyIcon, the game's icon from the checkout's client copy) |
| `Updates.cs`, `MainWindow.xaml.cs` | the version, the release check, the self-update; the update strip |

Tests: `tests/GameServer.Launcher.Tests` (the `live.ps1` command lines, the server folder, following logs, severities,
command and argument suggestions, the dev host's account list, finding the services, versions and releases, counter
payloads, histogram tags and quantiles, the timings window, chart scales, origin's branches, remote URLs, the launcher's
stashes, pull requests and forks, ages).
The switches themselves (branch, pull request, a force-pushed one, the refusal over a local commit, stash and restore,
update) were run against a throwaway repository with its own origin; they change a real checkout, so they have no unit
test.
The server's side of these hooks has its own tests there (`ChatCommandCatalogTests`, `HostTests`).
