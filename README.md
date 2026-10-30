# Waypoint

**Workspace Manager for LocalExile**

A separate Windows desktop launcher for a local LocalExile source checkout.
This repository has no dependency on a LocalExile project assembly.

## Run on this machine

Open PowerShell and enter:

```powershell
Set-Location 'F:\evry\evryPath\Launcher'
.\Start-Launcher.ps1
```

The script builds the launcher and opens its window. It does not start LocalExile until you press a runtime action.
Alternatively, open `src\Launcher.Desktop\bin\Debug\net10.0-windows\Launcher.Desktop.exe` after building.

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
4. On **Play**, use **Start servers** to run servers without the game, or **Play** for both.
5. Use **Close game** before **Stop servers** or a service restart. Closing the launcher leaves the game and services running.
6. If this installation needs its first account, close the game, stop services, open **Setup**, scroll to **First local
   account**, and click **Create local account**. Enter an account name and password. After creation, press **Play**
   and sign in inside the game; create your character there. The launcher does not save the password.

The supported client is standalone **3.29.3b**. The launcher can prepare a separate copy already placed in `Client`;
it does not download the game. Branch/PR activation reuses that copy and the standard build/data folders.
It checks structural prerequisites; the upstream preparation tools enforce the supported client signatures.

## Included in 0.2

- Dark WPF desktop interface, keyboard focus indication, scalable text, and an original geometric application icon.
- Checkout selection and persistent settings; local runtime profile support without executing profile code on inspection.
- Play, start/stop servers, individual service controls, service restart, and graceful game close.
- Process ownership checks and readiness based on the verified process owning every required listener.
- Source commit and built Auth/Patch/Gateway commits displayed separately, without the .NET default version prefix.
- Commit labels use seven-character inline code badges; hover to see the full hash. Comparisons and diagnostic
  reports retain full identities.
- CPU, working-set memory and uptime per process; stopped processes show no fabricated measurements.
- Readable live service logs, errors, operation output and links to their local folders.
- Build the selected LocalExile solution; launcher and emulator build independently.
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
stops owned servers, makes a consistent SQLite save backup, switches source and builds it. It never forces checkout,
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
show **unknown**; hot edits do not become fictional Git commits. Select a backup and choose **Restore selected…** or
**Delete selected…**. Restore requires the game closed, stops owned services, validates the snapshot and automatically
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

## Build and check

Requirements: Windows 10/11 x64 and the **.NET 10 SDK**. The UI uses WPF, which is Windows-only.
There are no third-party NuGet dependencies in the launcher.
Git on PATH enables upstream/local commit discovery and is required for the Git-fixture automated checks;
manual folder selection remains available without Git. No GitHub sign-in is required for the public upstream query.

```powershell
dotnet build Launcher.slnx
dotnet run --project tests/Launcher.Checks --no-build
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Bridge.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Source.Tests.ps1
```

The executable checks use private temporary fixtures and never start the real game or servers.
Discovery checks use private temporary Git repositories and worktrees; they do not access GitHub.
Ownership checks use fake process inventories. For a read-only real checkout inspection:

```powershell
dotnet run --project tests/Launcher.Checks --no-build -- --inspect 'F:\evry\evryPath\evryPath'
```

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

The ZIP contains the complete Waypoint folder, a quick-start README and a manifest recording source commit, SDK,
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

## Next work

See [ROADMAP.md](ROADMAP.md) for isolated branch/PR environments, consistent backup/restore, Gateway phase timing,
recorded comparisons, broader account management and the native development console.
See [CONTRIBUTING.md](CONTRIBUTING.md) for implementation boundaries and checks.
