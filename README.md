# Launcher

A separate Windows desktop launcher for a local LocalExile source checkout.
The product name is provisional. This repository has no dependency on a LocalExile project assembly.

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
it does not download the game, copy saves, or change branches for you.
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

The launcher uses Windows PowerShell 5.1, .NET 10, the standard Debug output layout and standard local ports.
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
Replace `branding/app.ico` for a new window/taskbar/executable icon.
`scripts/New-Icon.ps1` regenerates the current geometric icon.

The window name, sidebar name and version label read `brand.json`.
The internal `Launcher.*` project/assembly names can stay unchanged.
Settings remain under `%LOCALAPPDATA%\EmulatorWorkspaceLauncher`, independent of branding, so renaming does not lose them.
Installer identity is deferred until an installer exists.

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
