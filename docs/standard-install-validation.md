# Current upstream installation validation

Validated on 2026-10-30 against a fresh HTTPS clone of `LocalExile/LocalExile`, branch `main`, commit
`c748f5fa8b315391a777c08ca184dc43768b60fa`.
Launcher source: `2230e55` (no launcher code changes were needed).

This tests current main's standard directory layout. The unmerged external build/client-folder PR was not applied.
External-folder support should be revisited after that interface is merged upstream.

## Installation and independence

Temporary root: `F:\evry\evryPath\LauncherValidation-20261030-7d19e778`.
The installation is its `LocalExile` subfolder. It has a new Git clone and uses:

| Item | Path relative to the new checkout |
| --- | --- |
| Client | `Client/` |
| Auth build | `src/GameServer.Auth/bin/Debug/net10.0/` |
| Patch build | `src/GameServer.Patch/bin/Debug/net10.0/` |
| Gateway build | `src/GameServer.Gateway/bin/Debug/net10.0/` |
| Generated data and new keys | `data/` |
| New save database | `data/saves.db` |
| Server and extraction logs | `logs/` |
| Launch entry point | `scripts/dev/live.ps1` |

The standalone client files were physically copied into the fresh `Client/` directory. No junctions, symlinks or
hard links were used. The original and copied GGPK have different NTFS file IDs; the copied archive has one hard-link path.
Old prepared executables, client config, logs, server keys, instance secrets, databases, extraction outputs/stamps,
walk maps and compiled server outputs were not imported.
`Game.exe` was copied from the copied original `PathOfExile.exe`, new login keys were generated, and a new
`Game.patched.exe` was prepared from them using upstream's documented tools. New keys differ from the normal installation.
The local gateway was selected in the copied archive using the documented upstream setup command.

`npm ci` installed the pinned extraction dependency in the new checkout. The launcher's first Start performed the
complete extraction, including the object-type cache, rather than reusing existing extracted data.
The report decoded 943 tables and reported zero schema mismatches. It also reported 25 missing tables; the upstream
extractor completed successfully. Monster animation extraction reported skipped animation-count mismatches in its log.
These upstream extraction observations are retained in the temporary installation's logs.

There is no `live.local.ps1`, `runtime.local.ps1`, custom server output directory or Game_* path override.
The launcher displayed **Upstream live.ps1** and the fresh checkout's default client and database paths.
All built Auth/Patch/Gateway informational versions contained the selected source commit.

Only the game's personal settings/credential profile was redirected, via the copied client's `config.json`, into
`LauncherStandardValidation-20261030-7d19e778` under My Games/Path of Exile. It was created from scratch, with Predictive
networking, to preserve the owner's ordinary profile. This does not change the server/build/client installation paths.

## Checks and results

| Check | Result |
| --- | --- |
| Fresh LocalExile solution build through the launcher adapter | Passed |
| Launcher build | Passed, zero warnings/errors on the final verification build |
| Launcher executable safety checks | 18 passed |
| PowerShell ownership checks | 9 passed |
| Upstream Core tests | 403 passed |
| Upstream Data tests | 34 passed |
| Upstream Gateway tests, final full recheck | 3,415 passed |
| First Start with no extraction cache | All required services became Ready |
| Gateway restart from the desktop UI | Gateway PID changed; Auth/Patch PIDs were preserved |
| Play beside already-running services | Copied client launched with local patch-server argument |
| Fresh account and character | Created through the standard local account service, in the new database |
| Native login, character list, Play and `/pos` | Passed; character entered The Twilight Strand (`1_1_1`) |
| Stop attempted while game was open | Refused before stopping services |
| Desktop Close game and Stop servers | Passed; Gateway logged that unsaved progress was saved |
| Cold Play from all processes stopped | Started services and the copied game successfully |
| Saved character after restart, explicit-password profile | Appeared in the list and entered the world; `/pos` replied |
| SQLite integrity and isolation audit | Passed |

The first full Gateway run passed 3,414 and failed one `BurningArrow`/`Crude Bow` case of
`ConditionalCriticalGatewayTests.Actual_attack_overleech_item_stat_retains_only_attack_instances`.
It timed out in `Contact.HitLands` with `TaskCanceledException`.
All six cases passed in a focused rerun, and the subsequent full run with the game/services stopped passed all 3,415.
No emulator or launcher code was edited to obtain that result. The initial timeout's underlying cause was not established.

The temporary optional native-test environment installed Frida 17.25.0 in the new checkout's `.venv`.
Frida is used by upstream's native client-control test helper, not by ordinary launcher play.

## Native helper limitation discovered

The first native password login succeeded and the new client created a remembered-credential file.
On a later fresh client launch, `client_control.py`'s explicit-password flow submitted that password digest while
the native login message still had `Flag95=True` (saved-credential mode), producing login result 6.
The saved-login helper then submitted an empty account name, producing result 5: the earlier injected request had
not populated the remembered native form's account field.

Read-only checks confirmed both persisted password verifiers still matched the generated test password, and the account
and character rows remained present. These failures concern the optional native automation path and do not establish
a failure of a person typing their credentials into the game's login screen.

For the repeatable cold-launch check, only the temporary profile was set to `save_login=false` and a fresh credential
filename. Previous test credential files were retained. Standard password login, the persisted character list, entry
into the world and `/pos` then all passed after a full service restart. Upstream source, server configuration, keys,
client executable and launcher code were unchanged. Saved-login UI behavior was not separately exercised by a person.
Account/console integration should account for this helper limitation before it becomes a launcher feature.

## Evidence and preservation

The temporary root retains build/start driver logs, `runtime-first-start.json`, `runtime-after-restart.json`, native
first/cold-launch results, `isolation-audit.json` and TRX files under `tests/`.
It also contains a generated `test-account.json` for local retesting; that file contains a test password and must not
be committed or shared. No credentials or raw authentication logs were added to this repository.

The isolation audit found no reparse points in the new checkout. Its database has one fresh account and one fresh
character and passes SQLite `integrity_check`. Hash checks confirmed the original executable, prepared executable,
normal login keys, normal save database and ordinary settings file were unchanged.
The original GGPK's size and last-write time remained unchanged; only its independent copy was prepared for this test.
Both the original fork and fresh upstream checkout retained clean tracked working trees.

The validation game and services were left stopped. The temporary installation/profile are retained for review.
No repository was pushed, no pull request was created, and nothing was published or deployed.
