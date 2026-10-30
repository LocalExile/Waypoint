# Launcher

A separate Windows desktop launcher for a local LocalExile source checkout.
The product name is provisional. This repository has no dependency on a LocalExile project assembly.

## Run on this machine

Open PowerShell and enter:

```powershell
Set-Location 'F:\evry\evryPath\Launcher'
.\Start-Launcher.ps1 -RepositoryPath 'F:\evry\evryPath\evryPath'
```

The script builds the launcher and opens its window. It does not start LocalExile until you press a runtime action.
Alternatively, open `src\Launcher.Desktop\bin\Debug\net10.0-windows\Launcher.Desktop.exe` after building.

1. Open **Settings**. Choose the LocalExile folder containing `GameServer.slnx`.
2. Leave **Use this checkout's local runtime wrapper when present** checked if your checkout has
   `scripts/dev/live.local.ps1` and `config/runtime.local.ps1`. This preserves that installation's existing save path.
3. Click **Save and check**. Resolve failed checks before pressing **Play**. **Open setup guide** opens the checkout's
   `Agents/GettingStarted.md` in Notepad with the concrete client-preparation instructions.
4. On **Play**, use **Start servers** to run servers without the game, or **Play** for both.
5. Use **Close game** before **Stop servers** or a service restart. Closing the launcher leaves the game and services running.

The checkout must already contain its prepared standalone **3.29.3b** client, matching local login keys, and extraction
dependencies. This version does not install the game, generate keys, copy saves, or change branches for you.
It checks structural prerequisites; the upstream preparation tools enforce the supported client signatures.

## Included in 0.1

- Dark WPF desktop interface, keyboard focus indication, scalable text, and an original geometric application icon.
- Checkout selection and persistent settings; local runtime profile support without executing profile code on inspection.
- Play, start/stop servers, individual service controls, service restart, and graceful game close.
- Process ownership checks and readiness based on the verified process owning every required listener.
- Source commit and built Auth/Patch/Gateway informational versions displayed separately.
- CPU, working-set memory and uptime per process; stopped processes show no fabricated measurements.
- Readable live service logs, errors, operation output and links to their local folders.
- Build the selected LocalExile solution; launcher and emulator build independently.

The launcher uses Windows PowerShell 5.1, .NET 10, the standard Debug output layout and standard local ports.
Existing servers from other build folders are shown as port conflicts and are not adopted or stopped.
Custom ports, custom TLS/CDN settings, complex runtime scripts, remote servers and multiple simultaneous environments
need additional adapter support. See [the runtime contract](docs/runtime-contract.md).

## Build and check

Requirements: Windows 10/11 x64 and the **.NET 10 SDK**. The UI uses WPF, which is Windows-only.
There are no third-party NuGet dependencies in the launcher.

```powershell
dotnet build Launcher.slnx
dotnet run --project tests/Launcher.Checks --no-build
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Bridge.Tests.ps1
```

The executable checks use private temporary fixtures and never start the real game or servers.
Ownership checks use fake process inventories. For a read-only real checkout inspection:

```powershell
dotnet run --project tests/Launcher.Checks --no-build -- --inspect 'F:\evry\evryPath\evryPath'
```

Real lifecycle validation, observations and test limits are recorded in [validation](docs/validation.md).
The source review is in [LocalExile build and launch](docs/localexile-build-and-launch.md).
The fresh, default-path upstream installation test is in [standard installation validation](docs/standard-install-validation.md).

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
No telemetry, account integration, automatic updates or upload feature is included.

## Next work

See [ROADMAP.md](ROADMAP.md) for isolated branch/PR environments, consistent backup/restore, Gateway phase timing,
recorded comparisons, account management and the native development console.
See [CONTRIBUTING.md](CONTRIBUTING.md) for implementation boundaries and checks.
