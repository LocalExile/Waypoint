# Mod management validation (0.4.0)

Tested against LocalExile main `89b7aa908213ac8eb9b5ac5aa7ae54c14189c3e9` on Windows, with .NET 10.
The main revision was rechecked before packaging. The independent standard-layout validation installation has
its own physical client, keys, profile, extracted data and save database. No LocalExile code change is required by
Waypoint; the temporary source edit described below was restored byte for byte.

## Automated coverage

The documented check commands cover the Core/desktop build, Core checks, PowerShell process ownership, source/save
operations, mod startup, mod control and native transport. Mod control tests use real disposable .NET builds and
SQLite backups with simulated inventories. Transport tests use private Python stand-ins, not Frida or a real game.
Deliberate compiler/SQLite failures prove that changes stop on failure; their error output is expected.
The release gate passed 161 assertions: 78 Core, 18 ownership/commands, 25 source/save, 13 mod startup,
18 mod control and 9 mod transport checks. The solution built with zero warnings/errors.

Cases include JSONC manifests, free-text versions, safe IDs/paths, missing dependencies, source mapping, cached
fingerprint invalidation, no false loaded state from disk files, explicit runtime status parsing, backup history,
old branches, preserved choices and third-party files, incremental startup, dependency reuse beside running services,
compiler failure/recovery, default-on and explicitly disabled backups, manual backup, separate choice restore,
empty-choice restore, changed-output retirement refusal, recoverable removed-source outputs, native EOF/error,
acknowledgement without a list, bounded helper cleanup and PID/file/time cache invalidation.

The Core preparation fixture expects the game/server environment stopped; running it during a live validation
correctly hit runtime conflict guards instead of that fixture's intended missing-key failure. It was rerun after
graceful shutdown. Shared runtime-mutex suites run sequentially.

## Real runtime

- Play used the production adapter, automatic save backup and normal upstream client-only launch. All six source
  mods built. Gateway's log confirmed the default Example plus the deliberately enabled Hello.
- The existing private validation account signed in through the checkout's native helper and entered its character.
  The launcher never automated an authentication dialog or retained credentials.
- QueryMods returned the actual `/mod` list. DisableMod, EnableMod and ReloadMod for Hello all completed with
  Gateway PID 29876 and client PID 47736 unchanged.
- Changed Hello's greeting temporarily, then BuildMod rebuilt just the mod with dependencies reused. The reply
  changed in the native client, and the same character stayed in the world. Gateway/client PIDs stayed unchanged.
- A deliberate `#error` reported Building mod failure. The prior working greeting still replied in game.
  Restored Hello's original bytes, rebuilt, and confirmed its original greeting with the same character still in game.
- Added a uniquely named binary-only fixture with a nonexistent dependency. Gateway and the UI reported the
  missing dependency. Reloading it through the UI reported failure despite the command's generic acknowledgement.
  The fixture was moved into private validation artifacts afterward; no third-party mod was removed.
- Restored a real pre-change backup's empty enable overrides with Gateway stopped. The resulting empty state file
  represented the manifest defaults. Save/DLL replacement was not part of that operation.
- Switched the real installation to pre-mod-host `5fc27fa2` and back to `89b7aa9`. All six recorded mods were
  retained in recovery storage on the older revision, then rebuilt on return. An unmanaged disabled mod fixture
  stayed byte-for-byte unchanged across both switches and was retained in private validation artifacts afterward.
  This exposed the older revision's missing binary-folder ignore rule; the switch guard now permits those untracked
  runtime files while separately rejecting staged/working tracked edits and other untracked source.
- Graceful CloseGame and Stop ended the validation client and all three verified services. The original mod-choice
  file state and launcher settings were restored; the validation source remained clean.

## Desktop checks

Inspected Mods and Backups at the minimum 1020×730 window size. Search, selected details, scrolling, source-build
availability, disabled live controls with Gateway stopped, visible dependency errors, failed-operation messaging,
compact backup rows, mod-history details and separate choice restore were exercised. Full hashes retain hover
identities through the existing code badges. The backup switch was turned off and on through Mods and its saved
preference verified; it was restored to on before restoring the original settings file.
With no checkout selected, mod actions start disabled and automatic backups still display as enabled. Repository
lookup found multiple verified matches and retained the manual choice. The temporary first-run settings were restored.

An actual desktop child exposed missing Get-FileHash module discovery. The adapter now computes SHA-256 through
.NET directly, and the control suite deliberately hides Get-FileHash to protect that path. Real UI enable then
completed with a consistent backup containing all six mod manifests and binary fingerprints.

Checked the source-only review checkout at pre-mod-host `5fc27fa2370ffdef7981e253a054bb36bee59450`: Mods explains
that the revision has no supported host and disables its controls. The scratch checkout was restored to `89b7aa9`.
This old-revision UI check does not claim a fresh native gameplay run on that revision.

## Preservation and limits

The isolation audit passed: protected original executable/key/database/profile files unchanged, no validation
reparse points, separate archive file identity and keys, no local launch wrapper/profile override, two test accounts,
one character, and SQLite integrity `ok`. Validation backups remain available locally.

Loaded status is the last explicit native `/mod` report, tied to process identity and a file metadata stamp and
expired after two minutes. It is not an immutable runtime attestation. Hook/gameplay correctness remains LocalExile's
responsibility; this validation checks management and reload controls, not every mod's balance or gameplay hook.
