# Mod startup hotfix validation

Validated on Windows with the .NET 10 SDK against LocalExile
`89b7aa908213ac8eb9b5ac5aa7ae54c14189c3e9` (mod host merged in PR #401).
Waypoint changes are limited to pre-Gateway mod builds, version 0.3.1, regression coverage and documentation.

## Automated checks

- Solution build: zero warnings/errors.
- Core executable checks: 64 passed.
- PowerShell ownership/command checks: 18 passed.
- Source/save integration checks: 21 passed.
- New mod startup checks: 13 passed.

`tests/Mods.Tests.ps1` runs the production `Start-Services` and `Build-GatewayMods` functions with real disposable
.NET projects in a path containing spaces. Service consoles, inventories, extraction, backups and the clean-source
Git guard are simulated; no game/server is started by these tests. Existing suites cover those separate contracts.
The mod fixture verifies old branches, builds before any missing service launches, per-mod progress, real DLL
outputs, unchanged mod-state/binary-only files, incremental builds, hot reload off, an already-ready Gateway,
Auth-only startup, dependency reuse beside live services, build failure with stale output and recovery after a fix.
The compiler error in the failure fixture is intentional; the suite succeeds only when it blocks startup.

## Real standard installation

Used the existing independent standard-layout validation installation, with its own physical client, keys and saves.
Its clean detached source was advanced from `5fc27fa2370ffdef7981e253a054bb36bee59450` to the reviewed mod-host
revision. No owner fork or original client/profile was changed.

Invoked the production adapter's Start action with normal automatic backups and Gateway hot reload enabled.
The save backup was verified, extraction reported up-to-date, all three services and all six source mods built,
and Patch, Auth and Gateway reached Ready with verified listeners. Gateway had a verified watch parent.
The new Gateway log explicitly confirmed:

```text
[mods] example: ready (/example)
[mods] loaded example 1.0.0 (feature, 3 hook(s))
```

Only the upstream-default example was enabled. The native game was not opened for this startup-only check.
Stopped all validation services through the adapter's verified graceful Stop action afterward.
The validation checkout remained clean.

In the separate source review checkout, LocalExile's 19 ModHostTests also passed and all six bundled mod projects
built successfully. These host tests exercise fake players, not the native client's gameplay.

## Remaining scope

No new mod GUI, live command bridge or stale-output reconciliation is included. A failed mod build blocks the
requested startup, including when an older DLL remains on disk. Source switching still preserves ignored mod folders;
this hotfix does not attest third-party binaries or remove outputs for mods absent from a newly selected branch.
