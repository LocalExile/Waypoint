# Contributing

Read [AGENTS.md](AGENTS.md): its rules apply to people and agents alike. Use English commit messages and keep each change
focused. A material change in how the adapter treats a checkout is recorded in
[docs/runtime-contract.md](docs/runtime-contract.md).

## Checks

Before a pull request:

```powershell
dotnet build Waypoint.slnx
dotnet test Waypoint.slnx
dotnet run --project tests/Launcher.Checks
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Bridge.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Mods.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/ModControl.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/ModTransport.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Source.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Accounts.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Capture.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/SetupStop.Tests.ps1
python tests/AccountData.Tests.py
python tests/Capture.Tests.py
python tools/missing-translations.py
```

The scripts work on throwaway fixtures, never on a real checkout's saves. They do not replace looking at the running app:
start it with `--repo <checkout>` (and `--allow-multiple` beside another copy), look at every changed section at the
minimum window size, in light and dark, in both languages, and in the blocked and ready states.

## How the code is laid out

`src/Waypoint` is the WPF application: presentation, the `live.ps1` runs, the monitors. `src/Launcher.Core` holds the typed
state, the adapter's client, the mod, backup and account models, and the localization catalogs. `scripts/` is the PowerShell
adapter and its Python helpers, which `Launcher.Core` embeds. [docs/launcher.md](docs/launcher.md) has the file-by-file map.

Never commit client files, extracted game data, saves, credentials, signing keys or logs. Releases come only from a
maintainer's version tag.
