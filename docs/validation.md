# Validation

Validated locally on Windows on 2026-10-30 with .NET SDK 10.0.403 and Windows PowerShell 5.1.
LocalExile source: clean `main`, `bbb658a93f38eadc5894417676b667c74d88177f`.

## Automated checks

| Check | Result |
| --- | --- |
| `dotnet build Launcher.slnx` | Succeeded, 0 warnings / 0 errors |
| `dotnet run --project tests/Launcher.Checks --no-build` | 18 checks passed |
| `powershell -NoProfile -ExecutionPolicy Bypass -File tests/Bridge.Tests.ps1` | 9 checks passed |
| LocalExile `dotnet build GameServer.slnx` | Succeeded, 0 warnings / 0 errors |
| LocalExile Core tests, `--no-build` | 403 passed |
| LocalExile Data tests, `--no-build` | 34 passed |
| LocalExile Gateway tests, final full recheck, `--no-build` | 3,409 passed |

The first full Gateway run, performed while opening the real client, reported two failures:
`OrdinaryWandAttackTests.Object_default_wand_use_aims_at_the_target_but_hits_the_first_actual_collision` and
`ConditionalCriticalGatewayTests.Actual_attack_overleech_item_stat_retains_only_attack_instances`
(`Fireball`, `Rusted Sword`, `attack: false`).
Both passed in a focused seven-case rerun. With the game/services stopped, the next full Gateway run passed all 3,409.
No emulator source was changed to obtain that result; the cause of the first-run failures was not established.
The full rerun's TRX is retained in ignored `.local/validation/gateway-recheck/gateway.trx`.

Launcher checks cover settings persistence/corruption, paths with spaces, wrong folders, missing installation files,
read-only inspection, profile parsing without execution, protection against empty-profile saves, foreign process
rejection, expected process ownership, trailing Windows command whitespace, unexpected arguments, shared terminals,
reused parent PIDs and readiness distinct from process existence.

## Live runtime checks

- Existing build-folder listeners were reported as foreign; initial Play was blocked before mutation.
- Existing login keys reproduced the installed prepared executable exactly (SHA-256 match).
- Current source compiled binaries report the selected source commit for Patch, Gateway and Auth.
- Started all three services via the adapter and supported local wrapper. Required ports were verified as owned.
- Stopped/restarted Gateway with Ctrl+C and returned to Ready beside the unchanged Auth/Patch processes.
- Pressed **Play in the desktop UI**. The game reached its login menu displaying **PoE1 3.29.3b**.
  The actual process arguments included `--patching-server 127.0.0.1:12995`.
- Graceful **CloseGame** operation closed the actual game without a force kill.
- Pressed **Stop servers in the desktop UI**. Gateway/Auth/Patch stopped gracefully and their fixed ports were freed.
- Pressed **Build checkout in the desktop UI**. Operation completed successfully and its output was visible in Logs.
- Inspected Play, Builds, Performance, Logs and Settings pages in the running desktop app.
  The stopped process measurements display dashes/Stopped rather than retaining old samples.

Live testing found and corrected command-line trailing whitespace handling, PowerShell console-handle invalidation
during Ctrl+C, CIM versus Process.StartTime timestamp precision, and the WPF root theme application.
The console signal now runs in a disposable helper and rechecks the target/parent IDs and start timestamp.

No authentication or entry into a playable area was attempted. Startup/close/stop/build validation does not claim
that gameplay or every emulator feature works. No crash/rollback, account-management, benchmark, branch-switch or
save-restore feature is claimed for this milestone.

## Data preservation and final state

A consistent SQLite online backup was created before validation at
`.local/validation/saves.before-launcher.db` (ignored, never committed).
After validation, accounts, characters, account-character relationships, stashes, stash tabs and deleted characters
had identical rows to the backup. SQLite `integrity_check` returned `ok`.

The existing prepared executable was not replaced and still matched the verification copy byte for byte.
`Client/` remained its existing junction; no client/profile directories were deleted.
No tracked LocalExile files were modified. Ignored extraction data and the verified existing login keys were copied
into the source checkout so its supported launch workflow could run.

The game and managed LocalExile services were left stopped. The launcher remains usable against the selected checkout.
No Git remote, push, publication, deployment or upload was performed.
