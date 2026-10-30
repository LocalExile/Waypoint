# Roadmap

## Complete: usable source-checkout launcher

Checkout settings, dark UI/icon, structural prerequisite checks, normal/local-wrapper launch selection, build operation,
service ownership/readiness, service start/stop/restart, Play/game close, source/build identities, process measurements,
service/operation logs, and local automated safety checks.

## Complete: setup and troubleshooting

Actionable prerequisite guidance, explicit client-copy preparation, locked extraction dependency installation,
first local account creation, named operation stages/elapsed time, failed-stage log links/retry, game logs/search,
and a redacted diagnostic preview with explicit copy. Standard upstream source and client/build paths remain the contract.

## Complete: source switching, hot reload and save recovery

Searchable local/remote branch and upstream PR picker, serialized fetch/activation, controlled stop/build workflow,
current characters by default with one-time fresh start, configurable automatic consistent SQLite backups, commit
metadata, manual backup/restore/delete, watched Gateway ownership/graceful shutdown, explicit hot-reload events and
confirmed native game-data override reload. Standard client/build paths are reused; no emulator changes are required.

## Next: isolated environments

1. Introduce persisted environment manifests separating repository/ref/commit, built package, prepared client,
   generated data, keys, configuration, logs and saves. Keep the launcher's own installation independent.
2. Prepare separate source checkouts and builds for a chosen branch/PR, with immutable build metadata including
   dirty-tree status and artifact hashes. Selecting source must never be confused with selecting a built binary.
3. Extend the current backup/fresh workflow to independent test environments with separate keys and client trust.
   Current branch activation intentionally uses the selected installation and its saves when the owner chooses that mode.
4. Keep the last working environment. Add verified readiness, rollback, resumable preparation, disk-space reporting,
   and explicit restore with a safety backup.
5. Replace the v1 launch-script assumptions with a versioned upstream control protocol when available, including
   machine-readable readiness, shutdown and runtime identity. Multiple environments remain sequential until ports,
   the single-client rule and client trust are explicitly supported.

## Complete: local portable release

Waypoint branding, self-contained Windows x64 Release ZIP, generated quick-start README, bundled runtime notices,
source/runtime/file-hash manifest and archive checksum. The extracted release was tested outside the development
workspace through first use, backup, Play/world entry, data reload, graceful shutdown and reopen. No public release
has been made. Waypoint is MIT licensed; installer, signing and distribution remain separate decisions.

## Then: measurements and contributor tools

- Collect the existing `LocalExile.Gateway` meter through a supported diagnostics session. Show operation/phase,
  measurement window, sample counts, durations and waiting separately from process CPU.
- Record repeatable workloads with area, seed, build hash and scenario metadata; compare duration distributions
  and allocations between environments. No inferred pathfinding/combat timing from process measurements.
- Broader account management beyond first account creation, including separately designed recovery workflows.
- Native console through `client_control.py --stdio`; represent submission, timeout and no-reply outcomes correctly.
- Bounded on-disk operation-log retention, optional light/system themes, installer/signing and release automation.

## Product decisions still open

Artwork, release distribution, installer identity and supported platform expansion. The current product name is Waypoint; its application license is MIT.
WPF is currently Windows-only. Public upstream PR discovery is implemented; private GitHub authentication remains separate.
Public server directories, mod marketplaces and official-server modes are not part of this milestone.
