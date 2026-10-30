# LocalExile build and launch review

Reviewed 2026-10-30 against `F:\evry\evryPath\evryPath`, clean `main`, commit
`bbb658a93f38eadc5894417676b667c74d88177f`.
The review is a local-source assessment, not an independent check that remote upstream still has the same head.

## Build

`GameServer.slnx` contains the server/library projects, extractor and tests. `Directory.Build.props` enables nullable,
implicit usings, warnings as errors and invariant globalization. Projects target .NET 10.
Normal Debug server executables are `src/GameServer.<name>/bin/Debug/net10.0/GameServer.<name>.dll`.

`dotnet build GameServer.slnx` builds the solution. `live.ps1` normally builds only selected Auth/Patch/Gateway projects,
including their dependencies. Switching Git branches does not rebuild `bin/`; the docs explicitly require checking
the embedded informational Git versions and actual running executable locations.

## Required installation

Windows, .NET 10 SDK, the standalone 3.29.3b client at `Client/Game.exe` plus `Content.ggpk`, matching
`data/login-keys.json` and `Client/Game.patched.exe`, Node/ooz-wasm and Python for extraction.
The documented fresh setup copies a client, generates keys, prepares a separate executable, selects the local
gateway and extracts data. Steam layout is not supported as supplied.
Frida/walk-map tooling is optional and is not required by this launcher for normal play.

The launcher currently checks presence/layout/dependencies. Upstream key generation/preparation validates client
signatures. It does not claim a structural check alone proves the executable/archive are the correct patch level.

## Startup contract

`scripts/dev/live.ps1` is the only permitted client launch entry point. Commands run with the checkout as working directory.
The game always needs `--patching-server 127.0.0.1:12995` and only one client may run.
Do not pipe or capture live.ps1 output: server descendants may hold inherited streams open.

1. Validate the client/key files.
2. Run `extract-data.ps1` unless `-NoExtract` is explicit. The manifest/stamp scheme regenerates missing/outdated
   outputs and their dependents; it reads the GGPK. Extraction has separate build/output logs.
3. Select services. `-Only Auth,Patch,Gateway` rebuilds/restarts the chosen services and implies `-NoClient`.
4. The script can stop existing dotnet listeners on selected fixed ports and archives/prunes prior run logs.
   This broad behavior is why the launcher checks ownership/conflicts before invoking it.
5. Build the selected projects unless `-NoBuild` is explicit.
6. Start Patch, Gateway and Auth through hidden dedicated `cmd /c` processes with absolute per-checkout log destinations.
7. Wait for the patch readiness log (30-second deadline), then check stderr for the other services.
8. When opening the game, select the local GGPK gateway and start `Game.patched.exe` with the patch-server argument.

`-ClientOnly` is the supported way to open a closed game beside running services. It checks the three game-facing
ports, selects the local gateway before launching and returns idempotently for an existing client at the expected path.
It does not rebuild/extract/restart servers. It can write the cached gateway list: Play retains this existing upstream
behavior but adds no unrelated client-archive installation or modification.

`-NoClient` alone still restarts all services. The launcher instead starts only missing services through `-Only` and
opens the game separately through `-ClientOnly`, after verifying every service's listeners belong to the expected process.

| Component | Standard listeners | Log |
| --- | --- | --- |
| Patch | 12995 patch TCP, 8080 HTTP | `logs/live-patch.log` / `.err` |
| Auth | 20493 login, 20495 instance control | `logs/live-auth.log` / `.err` |
| Gateway | 6112 instance | `logs/live-gateway.log` / `.err` |
| Host (optional, not managed in v1) | 7000 JSON-lines account service | separate process |

Gateway supports Ctrl+C cancellation and saves unsaved progress during its shutdown path.
The launcher uses a separate console-signal helper, waits for exit and does not automatically force-kill.
Readiness here proves process identity and listening ports, not successful in-game mechanics or a completed login.

## This machine's runtime

The checkout has a local `scripts/dev/live.local.ps1` wrapper which sources `config/runtime.local.ps1` before live.ps1.
It requires the existing save database. Saves and instance secret point to `F:\evry\evryPath\build\data\`.
`Client/` is a junction to `F:\evry\evryPath\Client`; it is not a disposable client copy.

At the start of this task the active servers used absolute DLLs under `F:\evry\evryPath\build\bin\`, while the
source checkout's Debug binaries had an older embedded commit. The source data folder held only the tracked schema,
and its login-key file was missing. These are separate source/runtime identities despite being one developer workspace.

For initial validation:

- Created a consistent online SQLite backup in the launcher's ignored `.local/validation/` folder.
- Proved the existing runtime key recreated the installed prepared executable byte for byte before copying that key
  into the source checkout's ignored data folder. No key regeneration or client executable replacement.
- Copied existing generated extraction outputs/tables/stamps into the source data folder. The normal extractor
  reported `game data up to date` against the current manifest.
- Stopped the old build-folder services with owner authorization and graceful console signals.
- Built and exercised the current source checkout using its local wrapper and existing save database/instance secret.

No tracked LocalExile files were edited. These preparation steps were one-time local environment work, not implicit
launcher features. The client junction and other profiles/installations were preserved.

## Existing extension points

`GameServer.Host` offers accounts/characters via JSON lines over TCP. Native client control supports persistent
`client_control.py --stdio` requests through Frida; bridge elapsed time is not server execution time.
`ConnectionPerformance.cs` publishes `LocalExile.Gateway` duration histograms (`ms`), pending batches and allocations.
Phase timing collection is a later launcher feature; the first version measures processes only.
