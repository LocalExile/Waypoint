# External runtime contract, adapter v1

The launcher owns its settings and UI. LocalExile owns preparation, extraction, build semantics, client launch,
account/storage formats and game behavior. The repositories are independent; no server source changes are required.

`Launcher.Core.RuntimeClient` starts `RuntimeBridge.ps1` with structured `ProcessStartInfo.ArgumentList` arguments.
No command is assembled from user text for a shell. The upstream script does assemble cmd strings, so the adapter
refuses checkout paths containing cmd metacharacters; ordinary spaces are supported.

Actions: Inspect, Build, Start, Play, Stop, Restart, CloseGame, PrepareClient, Dependencies, Register.
Service selection: All, Auth, Patch, Gateway.
Each invocation writes a JSON result containing `Ok`, `Message`, and a typed snapshot. Mutation operations also write
a local transcript except Register. Failures include `FailedStage`. Mutations replace a `progress.json` containing
the current named stage, detail and log path. Core also interprets upstream extraction messages into named subtasks.
Elapsed time is measured locally; no estimated percentage or remaining time is fabricated.
Read-only inspection never creates data/log directories inside the source checkout.

The helper receives no redirected stdout/stderr. Register alone receives a JSON request through redirected stdin;
it never invokes live.ps1. This preserves live.ps1's no-pipeline requirement.
A Windows named mutex serializes mutations across all launcher instances and checkouts, since this adapter uses fixed ports.
The desktop also serializes refresh/operations and waits for an active operation to finish before it closes.
The Core API's cancellation token only cancels read-only waiting, not an in-progress runtime mutation.

## Configuration

Repository discovery is a separate read-only Core capability. It queries the public upstream's advertised default
branch HEAD and compares actual local Git HEADs; it does not fetch, update branches or require GitHub authentication.
Only a sole verified match from a completed first-run lookup can populate an untouched selection automatically.
Saved/manual choices take priority; manual browsing is available before, during and after lookup. Candidate detection
does not imply setup/runtime readiness. See [repository discovery](repository-discovery.md) for bounds and validation.

Game_* variables inherited from the launcher host are cleared in the child process.
When the local wrapper is enabled and exists, inspect `config/runtime.local.ps1` as an AST, accepting only literal
Game_* string assignments. Do not execute configuration while inspecting. Complex profiles fail with an explanation.
Literal profile environment settings apply to server commands. Game launch uses `scripts/dev/live.local.ps1`
when enabled, which ultimately calls the supported live.ps1.
Existing-profile databases must exist; never silently fall back to a new empty database.

Only standard local listener, client and archive paths are supported. Custom TLS/CDN settings fail preflight.
Normal upstream operation uses `data/saves.db`; local-profile operation uses the profile's explicit existing database.
All production game-data paths remain relative to the selected checkout.

## Identity, readiness and stopping

Accept a server as owned only when:

- It is dotnet with exactly the expected Debug assembly argument (+ `serve` for Auth).
- Its live parent is a dedicated cmd created no later than the server, with the exact upstream command and both
  absolute per-checkout log destinations. Trailing Windows command-line whitespace is ignored.
- Its PID owns every required listening port before the status becomes Ready.

Do not adopt unrelated absolute build folders, identical relative DLL paths in another checkout, shared terminal
sessions or orphaned relative commands. Conflicting listeners block start/play; failed inventory queries fail closed.
This is a compatibility/ownership guard, not an isolation boundary against malicious same-user code forging command lines.

Gateway may instead be a verified `dotnet watch run --project src\GameServer.Gateway --non-interactive --` root,
with the same dedicated cmd and absolute checkout log destinations. Its app executable must have the exact Debug
path and a live, timestamp-ordered ancestry to that root. Watch ownership persists during child restart/build gaps;
such a service is Starting, not Stopped. Foreign matching server/watcher processes also block launch, even when they
do not hold a port, because upstream launch scripts may otherwise terminate them by name.

Graceful shutdown runs `ConsoleSignal.ps1`, which rechecks PID, parent and creation timestamp. It attaches only to
the verified dedicated console. Every console peer must be the cmd, signal helper or a verified descendant of that
server/watch root. Unknown peers fail closed. It sends Ctrl+C, then exits. The adapter waits up to 20 seconds for the
captured server/watch subtree to exit and reports failure without forcing termination.
The game is closed by its window and checked process identity; service shutdown requires the game to be closed first.

Start runs the upstream extraction script, builds missing selected services when no owned services are running,
then starts only verified missing services using upstream's standard Debug commands, root working directory,
dedicated cmd consoles and checkout-specific logs. It sets the upstream Gateway-enter, packet-dump, watch-log
and disabled-walkmaps environment defaults. Old logs for those services are archived without touching live services.
The launcher does not invoke live.ps1's broad name/port cleanup, which force-terminates matching processes across
checkouts. It never needs that cleanup to start a missing service. Normal and watched startup share the same ownership
and graceful-stop checks. Upstream extraction and the game/client-only launch workflow remain unchanged.
Starting another service
beside live services reuses existing binaries only when the selected source commit matches and tracked source is clean.
Before starting a missing Gateway, the adapter incrementally builds every `Mods/Source/**/*.csproj`, matching
upstream's mod preparation. Revisions without that folder require no extra step. A failed mod build blocks the
requested service launch before any missing service is started; existing services remain running. The named
Building mods stage retains the compiler output. Beside live services, mod builds use `--no-dependencies` to avoid
overwriting shared server assemblies; missing dependencies require stopping services and building the checkout.
The adapter does not edit mod manifests, `mod-state.txt`, binary-only mods or enable/disable preferences.
The 0.3.1 startup fix alone does not reconcile stale mod folders or provide live management; the 0.4.0 extension below does.
Stop all services and Build checkout after changing source. V1 does not attest all untracked/ignored build inputs.
The Builds page shows source and disk-binary identities separately; it is not a full immutable running-build manifest.
Failed/partial startup stays visible for diagnosis; it does not silently tear down processes that did successfully start.

Upstream 5fc27fa also has a scalar-array Auth argument join (`$command + $serverArgs[$s]`) that generates `Auth.dllserve`.
The explicit standard service commands preserve the intended Auth argument boundary. No LocalExile file is patched.

Game launch is two steps: start/wait for required servers, then live.ps1 -ClientOnly. Running is distinct from a native
menu being ready, a successful authentication or entry into a playable area.

## Setup and diagnostics

Setup writes require the selected installation's game/services to be stopped and no listener conflicts.
PrepareClient refuses redirected client folders and relevant files, retains existing keys/prepared executable,
refuses an unpaired prepared executable before writing, and uses the checkout's Auth and Patch commands for
missing key/executable preparation and the copied archive's gateway list. Users supply the complete client copy.
Dependencies runs `npm ci --ignore-scripts --no-audit --no-fund` in Tools/ooz with the checkout's lockfile.

Register builds the checkout's Host service, starts a dedicated temporary localhost:7000 console with operation-local
logs, verifies its process and listener, sends the upstream JSON-lines registration request, and shuts it down with
the same verified graceful console mechanism on success or failure. It refuses an occupied port and nonstandard
Host bind/port configuration. Credentials never enter command arguments, a request file or a transcript.
Registration does not perform native login or character creation, change existing passwords, or retain a retry request.

Log reads use fixed service/game filenames and bounded tails. Search and warning/error filtering are local.
Diagnostic reports include limited operation/selected-log lines and structural metadata; no recursive file collection.
Redaction covers common labelled credentials, long hex values, home/installation paths, email addresses and detected
or explicitly supplied names. Validated public source/build identifiers are retained separately. The user reviews
the preview and explicitly copies it; arbitrary private details can still require manual review. No upload exists.

## Future contract

Prefer an explicit versioned LocalExile control interface with runtime IDs, actual loaded build identity, working
directory, readiness and save-safe shutdown. Keep v1 assumptions isolated here so the UI need not change with that interface.
Immutable environment manifests, separate source/build paths, release builds and metrics sessions remain separate capabilities.

## Source activation, hot reload and save recovery

Core queries local refs with read-only Git and public upstream PR metadata with bounded unauthenticated GitHub GETs,
conditional ETags and a two-minute cache. Local refs are usable before GitHub finishes. Fetch/activation/backup/restore
run under the existing global runtime mutation mutex. Git uses argument boundaries, no prompts, disabled hooks for
launcher mutations, and no force checkout/reset/clean/stash. PR fetch refs are positive integers under `refs/launcher/pr/`;
PR/remote activation detaches at a verified full commit. Local branches use normal switch. Target source must contain
the supported solution, Gateway and launch markers. Tracked runtime-profile changes are refused.

Activation rejects tracked edits and untracked source (ignored runtime files are reused), requires the game closed, stops owned
services, backs up saves by default, switches source and builds. It does not automatically start afterward. Build failure
preserves saves and leaves explicit selected source/stopped services. This is not an atomic hot branch replacement.
Hot reload launch preference is passed as `-NoHotReload` only where supported. Gateway's explicit log confirmations
drive pending/applied/failed status; a successful build is not proof of application. Watcher health and child presence
remain separate from historical log events. Auth/Patch, static initializers and extracted data have no generic reload guarantee.

Save backups use Python's SQLite online backup API, including committed WAL pages, then `PRAGMA integrity_check`.
They are keyed by canonical database path under the launcher's stable local data directory. Metadata records source
commit/branch at backup, requested source/fresh choice, time, and last Gateway build recorded at a successful launcher
start. That build hash is not an attestation of later hot edits or external launches. Unknown history remains unknown.
Automatic backups default on for current/fresh activation, build, Gateway startup/restart, data reload and restore;
users may disable them in either relevant UI page. Manual backup bypasses that preference. No backup occurs per
externally saved edit or watcher-triggered restart. Backup failure blocks the subsequent requested change.

Restore validates the chosen backup and its save-store association, requires stopped owned runtime, backs up current
saves when enabled, and replaces only saves. Fresh saves are created only after successful build. Replaced SQLite
database/WAL/SHM files are moved to operation-local recovery even with automatic copying off. Redirected save/backup
paths are refused. Deletion accepts only a validated backup ID in the selected store and its two known files; no broad
recursive deletion occurs. Client, keys, source and game-data overrides are outside a save snapshot.

Explicit game-data reload uses upstream's optional native client helper for the owned client and ready Gateway. Only
that disposable helper has captured streams (never live.ps1). The adapter waits for `Game data reloaded: revision N`
in the helper's new Gateway replies. A timeout, missing dependency or missing confirmation reports failure/uncertainty;
the launcher never assumes submission means application. The helper never starts a game for this operation.

## Mod management (0.4.0)

The typed snapshot includes optional mod capability, effective folder/global enable preference and a bounded cached
runtime list. Older snapshots/branches remain valid. Core reads JSONC manifests and native saved choices without
loading assemblies. Source projects follow the documented Mods/Source/<Name>/<Name>.csproj convention.

BuildMod, BuildMods, EnableMod, DisableMod, ReloadMod, QueryMods and RestoreModChoices use the same named mutation
mutex and process-ownership/conflict checks. Source builds target only physical csproj paths inside Mods/Source,
reuse dependencies beside running servers, and preserve compiler failures. Mod IDs are bounded single command
tokens. A stopped Gateway permits atomic state-file replacement; a running Gateway requires native commands.
The helper receives JSON lines over stdin and captured output, never starts a client and can be terminated on timeout
without killing the client/server. Acknowledgement alone is insufficient: /mod must confirm the selected outcome.
Timeout/no-reply errors state that a submitted command may already have applied.

Runtime reports are tied to Gateway PID/start time and a binary/manifest/state-file metadata stamp, expire after
120 seconds, and are labelled last checked. Regular inspection never attaches to a client. Check live status explicitly
requests /mod. This external contract cannot attest arbitrary mod writes, forged replies from executable mods or all
runtime state; it avoids claiming otherwise.

Mod-changing operations use the existing default-on backup preference. Metadata embeds saved choices, manifests,
DLL SHA-256 hashes and confirmed runtime history, within a 4 MB limit. This adds no files to backup folders, so the
two-file deletion contract remains intact. RestoreModChoices validates the save association and recorded mod folder,
requires Gateway stopped, and replaces only native enable overrides after the optional current-save backup.

Standard first-party builds record only their manifest, primary DLL and PDB hashes in a per-checkout launcher
registry. After their source project disappears, unchanged recorded files are moved individually into new physical
local recovery folders before a stopped Gateway starts or after source activation. Changed files block preparation.
No recursive mod-folder deletion or third-party adoption exists. Unrecorded outputs and custom conventions remain
user-managed; an unchanged project path is not an attestation of third-party compatibility.
Untracked Mods/Binaries files are treated as runtime data even on older revisions lacking the ignore rule; tracked
edits anywhere and other untracked source still block activation. No repository ignore file is changed.
