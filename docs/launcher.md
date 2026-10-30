# Desktop launcher

`src/GameServer.Launcher` is an optional Windows window over the usual development workflow: buttons for
`scripts/dev/live.ps1`, the services' state, live performance charts, every log, the accounts, and a console for the
Gateway's chat commands. It changes nothing in how the game runs: every start, restart and stop is a `live.ps1` switch,
so the rules of [GettingStarted](../../GettingStarted.md) (one client, only through `live.ps1`, from the repository root)
still apply.

## Build and run

```powershell
dotnet publish src/GameServer.Launcher -o .     # one LocalExileLauncher.exe in the repository root (git-ignored)
.\LocalExileLauncher.exe                        # or double-click it
dotnet run --project src/GameServer.Launcher    # while working on it
```

The launcher works only from the repository root: its working directory or its own folder must hold `GameServer.slnx`
and `scripts/dev/live.ps1`. It never searches parent folders. It needs the .NET 10 Desktop Runtime, which the .NET 10 SDK
installs on Windows. Its own settings (the switches, pinned console lines, the history) are per Windows user in
`%LOCALAPPDATA%\LocalExile\launcher.json`, never in the repository.

## Tabs

| Tab | What it does | How |
|---|---|---|
| Control | Servers: Start, Stop, Restart Gateway/Auth/Patch and the Walk maps, Skip build and Skip data check switches; Game: Play, Start game only, Close game; Stop all; the services' state; the setup checks; the script's output | `live.ps1`, `-NoClient`, `-Stop -NoClient`, `-ClientOnly`, `-Stop`, `-Only <server>`, `-Walkmaps`, `-NoBuild`, `-NoExtract`; Close game ends the client's process, Stop all also the dev host |
| Performance | CPU and memory of every service; for the chosen server, GC heap, allocation rate, collections, time in GC, thread pool, lock contention and exceptions | the processes once a second; the runtime's `System.Runtime` counters through its diagnostics port (what `dotnet-counters` reads), with nothing changed in the servers |
| Logs | every server's `logs\live-*.log` with its `.err`, the walk-map recorder, the dev host, the client's `Client\logs\Client.txt`, the launcher's own lines, and "All errors" from all of them | follows each file twice a second; a file `live.ps1` recreates starts over; a large file from its last 512 KB |
| Accounts | the accounts and their characters; registering an account; creating a character | reads `data/saves.db` (read only); writes through the dev account service (`GameServer.Host`, [dev-server](../dev-server.md)), which it builds and starts when needed |
| Console | the Gateway's chat commands for the character in game, with suggestions, usages, pinned lines and history | the [client control bridge](client-control.md) (`client_control.py --stdio`); the list comes from the built Gateway (`GameServer.Gateway commands`) |

### Control

- The buttons sit in three cards, coloured by what they do (green starts, red stops, blue is Play): **Servers**,
  **Game** and **Everything**. Each card's lamp says how many servers listen and whether the game runs.
- **Play** is plain `live.ps1`: it builds, restarts the servers and starts the client. It asks first if a client is
  running, because `live.ps1` closes it.
- **Start servers** is `live.ps1 -NoClient`, **Stop servers** `live.ps1 -Stop -NoClient`: the servers and the walk-map
  recorder, the client staying (it drops to the login screen).
- **Start game only** is `live.ps1 -ClientOnly`, with the unreachable proxy the control bridge also sets for it. It
  needs the servers running, and `live.ps1` refuses a second client.
- **Stop all** is `live.ps1 -Stop` (the client, the walk-map recorder and the three servers, then it waits until their
  ports are free) and the dev host the Accounts tab started. The tray's Stop all and closing with "Yes" do the same.
- **Restart** is `live.ps1 -Only <server>`. With Walk maps on, the restarted Gateway announces area loads to the recorder
  that is already running.
- Only one `live.ps1` runs at a time; the buttons wait while one does. The launcher runs it with Windows PowerShell and
  turns off MSBuild's reused nodes for it, the fix the troubleshooting table names for a script that never returns.
  It takes the script's end from the process's exit, not from the end of its output.
- The **Setup** list checks GettingStarted's one-time steps: the client copy, the keys, the patched client, the gateway
  list, the extracted data, Node and ooz, the Predictive network mode in `production_Config.ini`, Python with Frida, and
  whether an account exists. **Game data** runs `extract-data.ps1 -List`.
- The services are found by process: a dotnet process with `GameServer.<Name>.dll` on its command line, the client by
  `Game.patched.exe`, the recorder by `walkmaps.py`. A port that listens without such a process shows as held by another
  program.
- Closing the launcher asks whether to stop the servers and the game; "No" leaves them running.
- **Tray while the game runs** (on by default): when a client starts, the launcher hides in the tray, and when no
  client is left it comes back. A client that Play closes and starts again keeps it in the tray, and a launcher opened
  while the game already runs stays open. The tray icon's menu shows the window, stops everything (`live.ps1 -Stop`) or
  closes the launcher.
- A failure on a background thread (the process sampling, a runtime counter session, the log reading) is reported in the
  launcher's log, never ends the launcher: a server that `live.ps1` stops ends its counter stream anywhere, even inside
  a block. Anything that still ends the process is written to `logs\launcher-crash.log`.

### Logs and errors

Lines are classified by what they report. A server's `.err` is all errors. Elsewhere an exception, an error, a failure
or a crash is an error, and a warning or a slow handler ("took 73 ms", `ClientInbox.SlowAfter`) is a warning. MSBuild's
"0 Error(s)" is not an error. A server that exits while no `live.ps1` run is going on is reported as an error in the
launcher's log and in "All errors"; a client closed by its player is not.

### Console

- Typing `/` opens the command list: the commands whose name starts with what is typed, then those that contain it.
  ↑/↓ choose, Tab or Enter take one, and Enter on what the list already shows sends the line.
- After the command, each argument has its own list. An argument the Gateway names a kind for comes from the Gateway's
  catalogs (`GameServer.Gateway values <kind>`, loaded once per kind): `/goto` the areas, marked server-built,
  captured or client-only; `/spawn_mob` the monsters; `/item` the item types and uniques; `/gem` every creatable gem
  variant; `/set_stat` the stats; `/npc` the NPCs; `/quest_flag` the flags. A name several rows share (Hillock, Small
  Life Flask) is listed once per row, by the name with the row's Id beside it, and taking one puts the Id in the line.
  A value of several words counts as given once it and a space are typed, the shared name too; `/spawn_mob`'s `@level`
  belongs to the monster. Every other argument is suggested from the usage's own choices (`[normal|magic|rare]`,
  `[all | <id>]`, "/cosmetic list | /cosmetic off"); a value to supply (`<area id or name>`, `[seed]`) is not one.
- The usage of the command being typed shows under the line. Without the list, ↑/↓ go through the history.
- A line without `/` is a chat line.
- The list on the right has every command with its explanation. Double-click puts it in the line.
- **★ Pin** makes the line a button; right-click a button to remove it.
  A pinned button only puts its line in the box, and Enter or Send sends it: a command that can take the client down
  is never one click away.
- Commands go to the client as typed chat, so the character must be in game. Without a client the bridge answers
  "Client is not running".

### Accounts

Registering and creating a character are the dev protocol's `register`, `login` and `create_character` (GettingStarted
step 8). The character is created in Standard; the game's own creation screen offers the other leagues. The dev host
stays running afterwards (it shares `data/saves.db` with Auth and neither caches it); **Stop dev host** ends it, and
closing the launcher with "Yes" does too. Its output is `logs\launcher-host.log`.

## Code

| File | Role |
|---|---|
| `LiveScript.cs` | `LiveRequest` (the buttons as `live.ps1` switches) and the PowerShell runner |
| `ServiceMonitor.cs` | the services, found by WMI every two seconds, sampled every second |
| `RuntimeCounters.cs`, `Metrics.cs`, `TimeSeries.cs`, `LineChart.cs` | the runtime counter sessions, the history, the charts (drawn directly: the WPF chart libraries pull in .NET Framework builds of SkiaSharp) |
| `LogTail.cs`, `LogHub.cs` | following the logs, the severities |
| `CommandCatalog.cs`, `Completion.cs`, `ClientBridge.cs` | the Gateway's command list and argument values, the suggestions, the bridge process |
| `DevHost.cs`, `AccountList.cs` | the dev host, the account list |
| `SetupChecks.cs`, `Settings.cs`, `Repo.cs` | the setup checks, the per-user settings, the repository paths |
| `Tray.cs` | the tray icon (Windows Forms' NotifyIcon, the game's icon from the local client copy) |

Tests: `tests/GameServer.Launcher.Tests` (the `live.ps1` command lines, the repository root, following logs, severities,
command and argument suggestions, counter payloads, chart scales). `GameServer.Gateway commands` and `values` have
`ChatCommandCatalogTests` in the Gateway tests.

The console's lists come from the Gateway the launcher's servers run (`src/GameServer.Gateway/bin/Debug`). When that
build changes (Play or Restart Gateway after a pull), the next `/` reads the commands and their values again; **Reload
commands** does it at once. The Gateway's catalogs write their own log lines beside the values ("[gateway] atlas: …"),
which the launcher skips.

The solution sets `InvariantGlobalization` for every project; the launcher turns it off, because WPF formats bindings
with the UI culture and fails without one.
