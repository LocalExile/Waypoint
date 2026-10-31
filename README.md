# Waypoint

**Workspace Manager for LocalExile**

A separate Windows desktop launcher for a local LocalExile source checkout.
This repository has no dependency on a LocalExile project assembly.

Waypoint is an early alpha for local development and testing. It manages your checkout, builds, servers and save
backups. It does not download Path of Exile, bundle LocalExile or provide a remote/public server service.

## Download and run

1. Open this repository's **Releases** page and download `Waypoint-<version>-win-x64.zip`.
2. In File Explorer, right-click the ZIP, select **Extract All…** and extract it into a new folder.
3. Open the extracted `Waypoint` folder and double-click **Waypoint.exe**. Keep all files/subfolders together;
   opening just the EXE from inside the ZIP or moving it by itself will not work.
4. Select your LocalExile checkout in Settings, then follow **First setup** below.

No installer is required. The portable launcher includes its .NET Desktop runtime. It does not start LocalExile until
you press a runtime action. To upgrade, close Waypoint and extract the newer ZIP into a separate folder; your settings
and backups are retained. This alpha has no automatic update service.

For the latest changes, see [0.4.0 release notes](docs/releases/0.4.0.md).
The [0.3.1 hotfix notes](docs/releases/0.3.1.md) describe the mod startup compatibility fix.
The [first-alpha release notes](docs/releases/0.3.0.md) describe the original features and compatibility limits.

## Requirements

| Requirement | Used for |
| --- | --- |
| Windows x64 | Running the Waypoint desktop app |
| A [LocalExile](https://github.com/LocalExile/LocalExile) source checkout | The server implementation Waypoint manages; select its folder containing `GameServer.slnx` |
| [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) | Building/running LocalExile; the launcher bundles only its own runtime |
| [Git](https://git-scm.com/downloads/win) | Branch/PR management and checking upstream/local commits |
| [Python 3.9+](https://www.python.org/downloads/windows/) | LocalExile extraction and consistent SQLite save backups |
| [Node.js](https://nodejs.org/en/download) | LocalExile's bundle-decompression dependency |
| Your own standalone PoE **3.29.3b** client | A complete, separate client copy in the checkout's `Client` folder; Steam layout and newer client versions are unsupported |

Frida is optional. It is needed only for native game-data reload through LocalExile's client-control helper, not
ordinary play. See the [LocalExile setup guide](https://github.com/LocalExile/LocalExile/blob/main/Agents/GettingStarted.md)
for its environment and client requirements.

## First setup

If you do not have a LocalExile checkout, first install Git. In File Explorer, open the parent folder where you want
to keep LocalExile, right-click its empty background, choose **Open in Terminal**, and run:

```powershell
git clone https://github.com/LocalExile/LocalExile.git
```

Select the newly created `LocalExile` folder in Waypoint. If you already have a checkout, use that location instead.

On first use, **Settings → Find your LocalExile checkout** opens automatically. Lookup starts in the background,
but **Browse for a different location…** is available immediately. Git queries the current HEAD of the upstream
default branch, then compares local checkout HEADs. One verified match is selected and remembered as **Found and
using…**; multiple matches offer **Use this checkout** choices. Saved/manual selections are never silently replaced.
**Change location** stays visible at the top of every page. Browse selects and checks a folder immediately.
The heading starts as **Workspace** and changes to the selected repository's name from its local Git origin URL,
falling back to the folder name when Git metadata is unavailable. The full selected path remains underneath.
Offline lookup, missing Git, no match and bounded searches still allow manual selection or use of an older checkout.

1. Use the discovered checkout, select **Use this checkout** from the list, or click **Change location** to choose
   your LocalExile folder. You can also type its path in Settings and click **Save and check**.
2. Leave **Use this checkout's local runtime wrapper when present** checked if your checkout has
   `scripts/dev/live.local.ps1` and `config/runtime.local.ps1`. This preserves that installation's existing save path.
3. Click **Save and check**. Open **Setup** for instructions and an action beside each prerequisite. For a fresh
   installation, copy the complete supported standalone client into the selected checkout's `Client` folder using
   File Explorer, then click **Prepare local client**. Preparation retains existing keys and executables, creates
   missing files, and sets the copied archive's gateway to localhost. **Install extraction dependency** installs
   the checkout's locked Node dependency. The setup dialogs explain each change before it runs.
4. If this installation needs its first account, keep the game and servers stopped, open **Setup**, scroll to **First local
   account**, and click **Create local account**. Enter an account name and password. After creation, press **Play**
   and sign in inside the game; create your character there. The launcher does not save the password.
5. On **Play**, use **Start servers** to run servers without the game, or **Play** for both.
6. Use **Close game** before **Stop servers** or a service restart. Closing Waypoint leaves the game and services running.

The supported client is standalone **3.29.3b**. The launcher can prepare a separate copy already placed in `Client`;
it does not download the game. Branch/PR activation reuses that copy and the standard build/data folders.
It checks structural prerequisites; the upstream preparation tools enforce the supported client signatures.

## Features

- Dark WPF desktop interface with sidebar navigation, keyboard focus indication, scalable text, Windows High Contrast resources, and an original geometric application icon.
- Mods and Backups keep their list beside details, with actions above long history. Ctrl+1–8 chooses a page,
  Ctrl+Tab / Ctrl+Shift+Tab cycles pages, Ctrl+F focuses the page's search, and F5 refreshes local status.
- Checkout selection and persistent settings; local runtime profile support without executing profile code on inspection.
- Play, start/stop servers, individual service controls, service restart, and graceful game close.
- Process ownership checks and readiness based on the verified process owning every required listener.
- Source commit and built Auth/Patch/Gateway commits displayed separately, without the .NET default version prefix.
- Commit labels use seven-character inline code badges; hover to see the full hash. Comparisons and diagnostic
  reports retain full identities.
- CPU, working-set memory and uptime per process; stopped processes show no fabricated measurements.
- Readable live service logs, errors, operation output and links to their local folders.
- Build the selected LocalExile solution; launcher and emulator build independently.
- Searchable branch/PR selection, Gateway hot-reload monitoring and save backup management.
- Mod inventory, source builds, persisted enable choices, confirmed live status/reload and mod history in backups.
- Setup guidance with prerequisite actions, client preparation, extraction dependency installation and first account creation.
- Named operation stages and elapsed time; a failed operation retains its stage and offers log/output links and retry.
  Account creation requires re-entering details rather than retaining the password for retry.
- Game logs, case-insensitive search and a warnings/errors filter, alongside service and operation logs.
- A bounded diagnostic report with redaction, local preview, optional extra names to remove and an explicit Copy button.
  Review the preview for details the pattern-based redaction may miss. Nothing is automatically shared.

## Branches, hot reload and backups

Open **Builds** to browse local branches, fetched remote branches and public open PRs from LocalExile/LocalExile.
Local choices appear before the GitHub request finishes. Search by name, title, author or commit. **Fetch remote branches**
refreshes configured remotes without switching source; **Load / refresh list** refreshes PR metadata. Enter an upstream
PR number and choose **Use PR number** to preview a closed/unlisted PR, then **Switch and build** to activate it.
Remote branches and PRs use detached source so local branches are not reset. All operations are local; nothing is pushed.
The sidebar keeps the selected remote/PR label when its recorded commit still matches HEAD, including after reopening.

Close the game before **Switch and build**. The launcher refuses local tracked/untracked source changes, gracefully
stops owned servers, makes a consistent SQLite save backup, switches source and builds it. Untracked files under
`Mods/Binaries` are preserved as runtime data even on older revisions without the ignore rule. It never forces checkout,
cleans or stashes your work. A failed build leaves the selected source visible and services stopped; existing saves are
preserved. Press **Play** after a successful switch. This workflow uses the current standard LocalExile layout and
does not depend on the unmerged external client/build path PR. Branches changing tracked local runtime profiles are
refused until that profile change is handled separately.

Current accounts/characters are the default. **Start fresh** initializes an empty save database after a successful build;
create a new local account afterward. This is a one-switch choice and is cleared on success. **Automatic save backups**
is on by default for both choices and can be toggled directly in Builds or Backups. The preference also applies to
builds, service restarts, explicit game-data reload and save restore. **Back up now** always makes a manual backup.
Automatic backups do not run for each externally saved code edit or a restart performed internally by dotnet watch.
Starting Gateway also backs up an existing save database before startup can migrate it.

**Backups** lists date, size, source branch, source commit at backup and the last Gateway build this launcher recorded
launching with that database. Hashes use seven-character code badges with full hashes on hover. Older/outside launches
show **unknown**; hot edits do not become fictional Git commits. Select a backup and choose **Restore saves…** or
**Delete backup…**. Restore requires the game closed, stops owned services, validates the snapshot and automatically
backs up current saves when enabled. It changes only the selected save database; choose the matching source yourself
when restoring an older save. Backups stay in `%LOCALAPPDATA%\EmulatorWorkspaceLauncher\backups` until deleted.
Fresh/restore operations also move the replaced SQLite file set into that operation's `previous-saves` recovery folder,
including when automatic copying is off. Source/client/keys/game-data overrides are not restored from a save backup.

See [branch, hot reload and backup validation](docs/branch-reload-backup-validation.md) for the current-upstream
runtime, PR-switching, preservation and automated-check evidence.

**Watch Gateway code while playing** applies to the next Gateway start on revisions supporting hot reload. The hot
reload panel shows pending/applied/compile-failure events confirmed by Gateway, plus watcher/build/restart state.
Compatible code edits can keep the player in game. Incompatible edits may restart Gateway and return to login;
compile failures leave old code running. Auth/Patch and static initialization/data changes may need a full restart.
Branch/PR activation is a controlled stop/build workflow, not an atomic live multi-file source replacement.

**Reload game-data overrides** uses the selected checkout's optional `client_control.py` helper (Python with Frida,
supported client in game). It sends `/game_data reload` and requires a confirmed revision reply; submitting a command
alone is not reported as success. This reloads SQLite overrides, not extracted files or arbitrary C# static caches.

The launcher uses Windows PowerShell 5.1, .NET 10, the standard Debug output layout and standard local ports.
It starts only verified missing services with the standard upstream commands; it does not invoke upstream's broad
process cleanup. Game launch still uses `live.ps1 -ClientOnly`.
Existing servers from other build folders are shown as port conflicts and are not adopted or stopped.
Custom ports, custom TLS/CDN settings, complex runtime scripts, remote servers and multiple simultaneous environments
need additional adapter support. See [the runtime contract](docs/runtime-contract.md).

## Mods

Open **Mods** to search installed/source mods, inspect their versions, dependencies and load errors, and build source
mods individually or together. LocalExile owns `Mods/Binaries/mod-state.txt`; choices made in game and in Waypoint
use the same file. **Enable mod / Disable mod** saves the choice immediately when Gateway is stopped.
With Gateway running, live controls require the supported client in a playable area and LocalExile's Python/Frida
native helper. Otherwise use the command shown below the selected mod in game, or close the game and stop Gateway
before changing the saved choice. Waypoint never treats editing the state file as a live runtime change.

**Check live status** reads `/mod` through that helper. Loaded status means the last confirmed runtime reply;
it expires after two minutes, a Gateway process change or a mod binary/configuration change. A saved enabled choice
is separate from a successful load. Missing dependencies and load failures stay visible. A mod's version is free text;
the selected DLL's SHA-256 fingerprint is recorded in backup history. Listing mods never loads their DLLs into Waypoint.

LocalExile watches built mod files independently of **Watch Gateway code while playing**. **Reload selected** uses
`/mod_reload <id>` and checks the resulting runtime list. Rebuilds/reloads reset the mod's in-memory state and can
reload its dependents. Disabling a dependency can unload dependent mods. These actions affect the selected Gateway
and every connected character. Builds beside running services reuse already-built dependencies; stop services and
use **Build checkout** if a changed API or missing dependency needs rebuilding.

Automatic save backup is on by default before launcher-driven mod changes and can be toggled directly on Mods,
Builds or Backups. Backups record manifests/versions, DLL hashes, saved choices and a confirmed runtime list when
available. On Backups, **Restore recorded mod choices…** requires Gateway stopped and restores only enable overrides;
save restore remains separate, and neither operation installs mod binaries. External edits/watch-triggered reloads
do not create a backup for every file change.

Waypoint records standard output files it builds. After a source project disappears, its unchanged recorded outputs
are moved to `%LOCALAPPDATA%\EmulatorWorkspaceLauncher\retired-mods` during stopped-Gateway preparation or source
activation so they do not keep loading. Changed outputs require review. Unknown third-party mods and extra owner
files are preserved; their compatibility with another branch is not assumed. Custom output conventions are not
automatically reconciled. Source-mod build buttons require the normal `Mods/Binaries` runtime folder; binary-only
mod controls can use a literal `Game_MODS_FOLDER` in a supported local profile.

Mods are executable code: installing a mod grants it the same account access as LocalExile. This release manages
existing local mods and does not download mods or provide a marketplace, installer, script editor or league selector.
See [mod management validation](docs/mod-management-validation.md) for the real-client, branch-round-trip and UI checks.

## Build and check

These steps are for contributors building Waypoint from source. Download users can run the portable EXE directly.
Open PowerShell, replace the example path with the folder where you cloned Waypoint, and enter:

```powershell
Set-Location 'C:\Projects\Waypoint'
.\Start-Launcher.ps1
```

The script builds and opens the development app. Its internal executable is
`src\Launcher.Desktop\bin\Debug\net10.0-windows\Launcher.Desktop.exe`.

Requirements: Windows 10/11 x64 and the **.NET 10 SDK**. The UI uses WPF, which is Windows-only.
There are no third-party NuGet dependencies in the launcher.
Git on PATH enables upstream/local commit discovery and is required for the Git-fixture automated checks;
manual folder selection remains available without Git. No GitHub sign-in is required for the public upstream query.

```powershell
dotnet build Launcher.slnx
dotnet run --project tests/Launcher.Desktop.Checks --no-build
dotnet run --project tests/Launcher.Checks --no-build
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Bridge.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Mods.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/ModControl.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/ModTransport.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Source.Tests.ps1
```

The executable checks use private temporary fixtures and never start the real game or servers.
Discovery checks use private temporary Git repositories and worktrees; they do not access GitHub.
Ownership checks use fake process inventories. For a read-only real checkout inspection:

```powershell
dotnet run --project tests/Launcher.Checks --no-build -- --inspect 'C:\Projects\LocalExile'
```

Replace the inspection path with your own LocalExile checkout.

Real lifecycle validation, observations and test limits are recorded in [validation](docs/validation.md).
The source review is in [LocalExile build and launch](docs/localexile-build-and-launch.md).
The fresh, default-path upstream installation test is in [standard installation validation](docs/standard-install-validation.md).
The setup/progress/logging feature checks are in [setup and diagnostics validation](docs/setup-and-diagnostics-validation.md).
Repository discovery behavior and its validation are in [repository discovery](docs/repository-discovery.md).

## Rename the product

Edit `branding/brand.json` to change the display name and subtitle, then rebuild.
Its `executableName` controls the portable release EXE/folder name; the release README is generated from these values.
Replace `branding/app.ico` for a new window/taskbar/executable icon.
`scripts/New-Icon.ps1` regenerates the current geometric icon.

The window name, sidebar name and version label read `brand.json`.
The internal `Launcher.*` project/assembly names can stay unchanged.
Settings remain under `%LOCALAPPDATA%\EmulatorWorkspaceLauncher`, independent of branding, so renaming does not lose them.
Installer identity is deferred until an installer exists.

## Build a portable Release ZIP locally

From the repository folder in PowerShell, with the .NET 10 SDK and Git installed:

```powershell
.\Build-Release.ps1
```

This creates a self-contained Windows x64 **Release** build, `Waypoint.exe`, its support files and a portable ZIP
under `.local/releases/<commit-and-build-id>/`. It does not upload or publish anything. Commit changes first;
`-AllowDirty` is available only for development packages explicitly marked as modified. `-OutputDirectory <folder>`
selects a different output parent. Every build uses a new directory and preserves previous artifacts.

The ZIP contains the complete Waypoint folder, a quick-start README, MIT license, release notes and a manifest recording source commit, SDK,
configuration, architecture and SHA-256 hashes of every payload file. A separate `.zip.sha256` file checks the archive.
The .NET Desktop runtime is bundled; the launcher itself needs no .NET installation. LocalExile's SDK, Python, Node,
Git and supported-client prerequisites still apply. Keep all extracted files together; the EXE alone is insufficient.

Validate and extract an actual ZIP to a **new**, separate folder before running it:

```powershell
.\tests\Package.Tests.ps1 -ZipPath '<full path to the ZIP>' -ExtractTo '<new external folder>'
```

This checks the ZIP checksum, contained paths, every extracted file hash/size, clean Release metadata, branding and
x64 executable header. It rejects private game/save/key files and unexpected payload files. Open the extracted
`Waypoint\Waypoint.exe` to test the packaged UI/runtime integration. Development builds continue to use the stable
internal `Launcher.Desktop.exe` name. Portable and development builds share the same stable settings/backup identity.

The first local package's checksum, external-folder runtime tests and preservation results are recorded in
[portable release validation](docs/portable-release-validation.md).

## Local state

- Launcher preferences: `%LOCALAPPDATA%\EmulatorWorkspaceLauncher\settings.json`.
- Runtime operation logs/results: `%LOCALAPPDATA%\EmulatorWorkspaceLauncher\operations\<id>\`.
- LocalExile service/extraction logs: the selected checkout's `logs\` folder.
- Game, keys, save database and instance secret: remain in their installation; never enter this repository.
- `.local/` is ignored and holds this machine's development/validation artifacts only.

Operation logs contain PowerShell transcript metadata and local paths. They are local diagnostics, not redacted shareable reports.
Account creation has no transcript: its request travels through stdin and localhost, never command arguments or a request file.
Diagnostic preview includes only bounded operation output and the selected log, plus setup/service/build metadata; it does
not collect databases, key files, credential files or client archives. Account/character names can be added to its removal field.
No telemetry, automatic updates or upload feature is included.

## Troubleshooting and bug reports

| What you see | What to do |
| --- | --- |
| No checkout found, lookup offline, or multiple matches | In Settings choose **Browse for a different location…** and select the folder containing `GameServer.slnx` |
| Play disabled or setup missing | Open **Setup** and follow the instruction/action beside each missing requirement, then **Recheck setup** |
| Port occupied or another client/server detected | If you started them in Waypoint, select that checkout, use **Close game** then **Stop servers**, and return to your intended checkout; services started elsewhere need that tool's normal shutdown. Then **Refresh** |
| Branch switch refused due to local changes | Choose another clean checkout through **Change location**, or save the edits with your Git client's **Commit** action before retrying; Waypoint will not discard or stash your work |
| Build or startup failed | Use **Open failed stage log** or **View operation output**, correct the reported problem, then **Retry operation** |
| Building mods failed | Gateway was not started. Open the failed stage log, correct the named mod's build error, then retry. If dependencies need rebuilding, stop servers and use **Build checkout** first. Existing mod choices are preserved |
| Hot-reload compile failure | Fix the reported source error and save again; the old code keeps running |
| Need to undo a save change | Close the game, open **Backups**, select the intended snapshot and choose **Restore selected…**; source selection is separate |
| Native game-data reload unavailable | Follow the LocalExile native-helper setup, or enter `/game_data reload` in game on a revision that supports it |

For other problems, use this repository's **Issues** tab. Include the Waypoint version, LocalExile commit, what you
clicked and the actual result. **Logs → Diagnostic report…** offers a local preview you can review and copy. Remove
any remaining private details before sharing; do not attach client archives, save databases, signing keys or passwords.

## License and related projects

Waypoint is licensed under the [MIT License](LICENSE). Copyright and license notices for the bundled Microsoft .NET
runtime are included in the portable package's `licenses` folder and keep their own terms.

Waypoint is a separate companion project for [LocalExile](https://github.com/LocalExile/LocalExile). Its MIT license
applies to Waypoint's code, not LocalExile or Path of Exile. Neither LocalExile nor any game assets are bundled.
Waypoint is not affiliated with or endorsed by Grinding Gear Games.

## Next work

See [ROADMAP.md](ROADMAP.md) for isolated branch/PR environments, expanded recovery, Gateway phase timing,
recorded comparisons, broader account management and the native development console.
See [CONTRIBUTING.md](CONTRIBUTING.md) for implementation boundaries and checks.
