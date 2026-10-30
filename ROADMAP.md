# Roadmap

## Complete: usable source-checkout launcher

Checkout settings, dark UI/icon, structural prerequisite checks, normal/local-wrapper launch selection, build operation,
service ownership/readiness, service start/stop/restart, Play/game close, source/build identities, process measurements,
service/operation logs, and local automated safety checks.

## Complete: setup and troubleshooting

Actionable prerequisite guidance, explicit client-copy preparation, locked extraction dependency installation,
first local account creation, named operation stages/elapsed time, failed-stage log links/retry, game logs/search,
and a redacted diagnostic preview with explicit copy. Standard upstream source and client/build paths remain the contract.

## Next: environments and recovery

1. Introduce persisted environment manifests separating repository/ref/commit, built package, prepared client,
   generated data, keys, configuration, logs and saves. Keep the launcher's own installation independent.
2. Prepare separate source checkouts and builds for a chosen branch/PR, with immutable build metadata including
   dirty-tree status and artifact hashes. Selecting source must never be confused with selecting a built binary.
3. Fresh saves or a consistent SQLite backup copied into a test environment. Handle WAL correctly. Never share a
   writable production database with an unverified test build. Include instance keys and client trust in the boundary.
4. Keep the last working environment. Add verified readiness, rollback, resumable preparation, disk-space reporting,
   and explicit restore with a safety backup.
5. Replace the v1 launch-script assumptions with a versioned upstream control protocol when available, including
   machine-readable readiness, shutdown and runtime identity. Multiple environments remain sequential until ports,
   the single-client rule and client trust are explicitly supported.

## Then: measurements and contributor tools

- Collect the existing `LocalExile.Gateway` meter through a supported diagnostics session. Show operation/phase,
  measurement window, sample counts, durations and waiting separately from process CPU.
- Record repeatable workloads with area, seed, build hash and scenario metadata; compare duration distributions
  and allocations between environments. No inferred pathfinding/combat timing from process measurements.
- Broader account management beyond first account creation, including separately designed recovery workflows.
- Native console through `client_control.py --stdio`; represent submission, timeout and no-reply outcomes correctly.
- Bounded on-disk operation-log retention, optional light/system themes and packaging.

## Product decisions still open

Product name/artwork, license, release distribution, installer identity and supported platform expansion.
WPF is currently Windows-only. PR discovery and any GitHub authentication need a separately designed integration.
Public server directories, mod marketplaces and official-server modes are not part of this milestone.
