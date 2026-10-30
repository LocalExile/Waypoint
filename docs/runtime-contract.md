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

Game_* variables inherited from the launcher host are cleared in the child process.
When the local wrapper is enabled and exists, inspect `config/runtime.local.ps1` as an AST, accepting only literal
Game_* string assignments. Do not execute configuration while inspecting. Complex profiles fail with an explanation.
The actual launch uses `scripts/dev/live.local.ps1`, which ultimately calls the supported live.ps1.
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

Graceful shutdown runs `ConsoleSignal.ps1`, which rechecks PID, parent and creation timestamp. It attaches only to
the verified dedicated console, requires that console to contain exactly the server, parent cmd and signal helper,
sends Ctrl+C, then exits. The adapter waits up to 20 seconds and reports failure without forcing termination.
The game is closed by its window and checked process identity; service shutdown requires the game to be closed first.

Start runs the upstream extraction script, builds missing selected services when no owned services are running,
then calls live.ps1 with NoBuild/NoExtract/NoClient and the selected missing services. Splitting the preparation
from launch makes those stages observable without replacing upstream extraction or process-launch semantics.
Starting another service
beside live services reuses existing binaries only when the selected source commit matches and tracked source is clean.
Stop all services and Build checkout after changing source. V1 does not attest all untracked/ignored build inputs.
The Builds page shows source and disk-binary identities separately; it is not a full immutable running-build manifest.
Failed/partial startup stays visible for diagnosis; it does not silently tear down processes that did successfully start.

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
Environment manifests, consistent backup/copy/restore, release builds, PR discovery and metrics sessions are separate capabilities.
