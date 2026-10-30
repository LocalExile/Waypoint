# Waypoint portable release validation

Validated locally on 2026-10-30. Nothing was uploaded, deployed or published.

## Artifact

- Product: **Waypoint — Workspace Manager for LocalExile**, version 0.3.0.
- Source: `81657cd55a1dcc815da9961afd942697d9675e57`, clean at build time.
- Package: `Waypoint-0.3.0-win-x64.zip`, 63,260,532 bytes (approximately 60.33 MiB).
- SHA-256: `88f1e053a927fcc45425f426e71f6f114c2a461a83f17eb8504cad2f991dfa85`.
- Build: Release, self-contained Windows x64, .NET SDK 10.0.403; bundled .NET and Windows Desktop runtimes 10.0.14.
- Payload: 408 hashed files plus the release manifest. Runtime license/notice files and a quick-start README are included.
- No game files, saves, signing keys, test credentials or debug symbols are packaged.

The internal development executable remains `Launcher.Desktop.exe`. The release builder reads `executableName`
from `branding/brand.json` and produces `Waypoint.exe`; it preserves the C# namespace and stable settings identity.
The release EXE's informational version is `0.3.0+81657cd55a1dcc815da9961afd942697d9675e57`.

## External extraction and launch

The exact ZIP above was verified and extracted to a new folder outside the entire development workspace:
`F:\Waypoint Release Validation\20261030-24687d37\Waypoint`.
It was not copied over an existing installation. The test working directory was its external parent, not the repository.

`tests/Package.Tests.ps1` checked the archive checksum, traversal-safe entry paths, all extracted hashes and sizes,
manifest/source/build metadata, absence of private game data, runtime licenses, matching brand/version/name and the
native Windows x64 executable header. No unexpected extracted files were present.

Process-module inspection confirmed `hostfxr.dll` and `coreclr.dll` were loaded from this extracted package.
Thus this launch used the bundled runtime, rather than relying on the development machine's shared .NET runtime.
This was an external-folder test on the existing Windows machine, not a fresh Windows VM test.

## Functional checks

| Check | Result |
| --- | --- |
| First use with empty launcher settings | Workspace heading, automatic lookup and manual location choices appeared |
| Branding at minimum 1020 x 730 outer size | Waypoint title/name, two-line subtitle and version rendered correctly |
| Manual selection of independent standard LocalExile | Setup inspection succeeded; repository title and remembered source label updated |
| Packaged save backup | Completed using the extracted RuntimeBridge/SaveBackup helpers; backup count increased |
| Play | Automatic save backup, extraction check, build, three ready services and copied client launch succeeded |
| Gateway watch mode | Owned watcher and actual Gateway child were identified; current watch state displayed |
| Disposable account/character | Existing native validation helper passed password login, character list, world entry and `/pos` |
| Branch/PR browser | Local refs appeared immediately; public GitHub PR results loaded afterward |
| Game-data overrides | Packaged adapter backed up saves and received `Game data reloaded: revision 2` |
| Game close | Graceful close completed before service shutdown |
| Server shutdown | Packaged ConsoleSignal helper stopped watched Gateway, Auth and Patch gracefully |
| Reopen | Remembered validation installation and source label were retained without another location prompt |

The existing source validation installation was reused with its independent physical client, keys, saves and isolated
personal profile. Its source remained at upstream `5fc27fa`. No LocalExile source changes were needed for packaging.
The original protected-file audit passed, with two disposable accounts, one character and SQLite integrity `ok`.

All 408 extracted payload hashes remained unchanged after runtime use. The original launcher settings are checkpointed
before the first-use simulation and restored byte-for-byte after the test. Game and server processes are left stopped.

## Build and repeatability

`Build-Release.ps1` creates local artifacts only and records source commit, clean/modified state, SDK, bundled runtime
versions and per-file hashes. It uses new build directories, keeps old packages, and emits an archive SHA-256 sidecar.
Modified-source packages require an explicit development flag and are rejected by the distributable-package checker.

The solution build passed with zero warnings/errors. The unchanged runtime/source implementation also passed its
64 core checks, 18 ownership/start-command checks and 21 source/save integration checks before packaging.
Failure messages from the deliberately broken compile/database fixtures are expected assertions, not build failures.

The package is a portable preview. An installer, signing, application license decision and public distribution remain
separate work; no publication was performed for this validation.
