# Branch, hot reload and backup validation

Validated locally on 2026-10-30 with displayed launcher version 0.3.0. Nothing was pushed or published.

## Installations and identities

The independent standard installation from the earlier validation was updated from `c748f5f` to upstream main
`5fc27fa2370ffdef7981e253a054bb36bee59450` (Gateway hot reload). A local branch retains the earlier validation source.
The physical client, keys, extracted data and saves were reused in their default `Client`, `data` and Debug build
locations. No external-path PR, junction, runtime wrapper or emulator source patch is required by the launcher.

The source-only upstream review clone exercised the missing-client/empty-backup states. The owner's working fork
was not used for source switching or runtime testing. All client/profile and save operations used disposable validation
assets. Credentials and raw private runtime evidence remain outside tracked source.

During the test, Patch, Auth and Gateway informational versions were all
`1.0.0+5fc27fa2370ffdef7981e253a054bb36bee59450`. Launcher validation builds reported
`1.0.0+7cac338527d2f81d3d11e832f834a415c7b013d1` before committing the feature; that metadata identifies the base commit,
not the uncommitted feature changes. The final local commit is rebuilt afterward.

## Live runtime checks

| Check | Result |
| --- | --- |
| Start current upstream with standard folders | Patch/Auth ready; Gateway ready under a verified watch tree |
| Client launch | Opened only through upstream `live.ps1 -ClientOnly` |
| Existing validation account/character | Native password login, character list, world entry and `/pos` passed |
| Compatible source edit | Gateway confirmed hot reload #1 in 0.63 seconds; same Gateway/client PIDs; client stayed in game |
| Deliberate compile error | Gateway reported the compiler error; old Gateway and in-game client continued running |
| Incompatible edit | Watcher reported restart required, retained its PID and started a new Gateway process |
| Failed rebuild during restart | Watcher stayed owned/Starting while Gateway had no child; recovery edit resumed it |
| Return to original validation source | Temporary source edits restored byte-for-byte; source working tree clean |
| Game-data overrides | Launcher backed up saves, sent `/game_data reload`, required and received revision 2 confirmation |
| Graceful shutdown after child restarts | Watched Gateway, Auth and Patch stopped through verified console Ctrl+C; no forced server termination |

An overlapping external test initially stopped the validation Gateway using upstream's broad process cleanup.
It was paused before the final live checks. The launcher now starts only verified missing services with the standard
commands and never invokes that broad cleanup. This also avoids upstream main's `Auth.dllserve` argument-join bug.
The launcher retains upstream extraction and game/client-only launch; no LocalExile file is changed to fix startup.

## Source switching and backups

- Fetched and built real upstream PR #393 at `1de4103e86d0eddd9290708f5cbb589ce5a98800`, using detached source.
- Switched back through the GUI to fetched upstream main `5fc27fa`, with current characters selected and automatic
  backup enabled. The existing client and keys were reused; full builds completed successfully.
- Persisted remote/PR selection labels are bound to the selected full commit. Local branch names take precedence.
- Actual save backups showed last launched Gateway `5fc27fa` separately from source at backup, including a backup
  made while source was at PR commit `1de4103`. Older launch history correctly showed unknown.
- The GUI preference immediately persisted automatic backups off, then a manual backup still succeeded. The backup
  list increased from six to seven entries. Automatic backups were turned on again afterward.
- Backup date, size, source label, seven-character commit badges and selection controls were inspected at the
  minimum 1020 x 730 outer window size (1006 x 723 captured client region). Empty, populated and blocked states were
  checked. Restore/delete are disabled without a selection. Native operation stages/elapsed time remained visible.

The automated source/save suite uses private Git and SQLite fixtures with an empty mocked runtime inventory;
it does not depend on the owner's live ports or control any actual runtime process. It covers normal local and
detached remote activation, friendly labels, default/fresh backups, explicit backup disable, WAL-consistent online
backup, integrity checking, restore, selected deletion, invalid backup IDs, dirty-tree refusal, failed-build save
preservation, switching back after build failure and backup failure blocking activation.

## Verification and final state

- Solution build: zero warnings/errors.
- Core executable checks: 64 passed.
- PowerShell ownership/start-command checks: 18 passed.
- Source/save integration checks: 21 passed. Compiler/database errors intentionally produced by its failure fixtures
  are expected; the assertions verify those failures preserve data and block subsequent changes.
- Original protected executables, keys, database and personal settings passed the existing hash audit; the copied
  archive has a distinct physical file ID, no reparse points exist, and the validation database retains two disposable
  accounts, one character and SQLite integrity `ok`.
- Validation source was returned to current upstream `5fc27fa`; game and server processes were left stopped.
- The owner's original launcher settings file is restored byte-for-byte after GUI preference/location checks.

Branch/PR activation deliberately stops/builds instead of promising seamless multi-file hot replacement. Automatic
save backups cover explicit launcher operations, not every external code save or watcher-triggered restart. Save
restore does not restore source, client trust/keys or game-data overrides; those boundaries remain visible in the UI.
