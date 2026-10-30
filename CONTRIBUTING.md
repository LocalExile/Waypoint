# Contributing

Build and run the two check commands in README before committing.
Use English commit messages. Keep each change focused and record material compatibility changes in docs/runtime-contract.md.

The desktop project owns presentation only. Core owns settings, operation invocation and typed state.
The PowerShell adapter owns the current external LocalExile integration contract.
Do not reference emulator project DLLs or copy server implementation into the launcher.
New source/build/environment capabilities belong behind explicit model/interface boundaries.

Never launch a PoE client except through the selected checkout's supported `scripts/dev/live.ps1` workflow.
Do not pipe or redirect that script's stdout; it starts detached long-running processes.
Require one game client globally. Prove service ownership before stopping it or reusing a listener.
Keep runtime writes serialized across launcher instances.
Never force-kill a game or server as an automatic fallback; Gateway needs the opportunity to save.

Never commit client files, extracted game data, saves, credentials, signing keys, logs, third-party binaries or `.local/`.
Do not add hardcoded developer paths to production code or branding.
Save migration, client-archive changes and profile changes need separate, visible workflows.
Do not present process CPU or bridge elapsed time as game-system execution time.

For local UI validation, start the app with `--repo <checkout>`, inspect every changed page at the minimum supported
window size, and exercise both blocked and ready states. Source control cleanliness does not prove the running DLLs
match source; record built informational versions too.

No publishing is authorized for the initial local work. License, hosting and release/installer policy remain owner decisions.
