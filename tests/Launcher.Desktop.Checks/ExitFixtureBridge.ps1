param([string]$Action, [string]$RepoRoot, [string]$ResultPath, [string]$Service='All', [switch]$UseLocalProfile, [switch]$NoBackup, [switch]$NoHotReload)
$ErrorActionPreference = 'Stop'
$prefix = Join-Path ([IO.Path]::GetTempPath()) 'Waypoint exit fixture '
if (-not ([IO.Path]::GetFullPath($RepoRoot).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) -or
    -not (Test-Path -LiteralPath (Join-Path $RepoRoot '.waypoint-test-fixture'))) { throw 'Expected a disposable exit fixture.' }
if ($Action -notin @('Inspect','Stop')) { throw 'Exit fixture accepts only Inspect and Stop.' }
Add-Content -LiteralPath (Join-Path $RepoRoot 'calls.txt') -Value $Action
$state = Get-Content -LiteralPath (Join-Path $RepoRoot 'state.json') -Raw | ConvertFrom-Json
$ok = $true
$message = 'Exit fixture inventory'
if ($Action -eq 'Stop') {
    if ($state.FailStop) { $ok = $false; $message = 'Fixture graceful stop failed' }
    else { $state.Running = $false; $state | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RepoRoot 'state.json') }
}
$services = @()
if ($state.Running) { $services += @{ Name='Gateway';Status='Ready';ProcessId=123;StartedUtc='';Detail='Fixture only';Ports=@();MemoryMb=0;CpuSeconds=0;Uptime='';WatchProcessId=0 } }
if ($state.Game) { $services += @{ Name='Game';Status='Running';ProcessId=124;StartedUtc='';Detail='Fixture only';Ports=@();MemoryMb=0;CpuSeconds=0;Uptime='';WatchProcessId=0 } }
# These synthetic IDs are presentation only. This fixture never inventories or signals real processes.
@{ Ok=$ok;Message=$message;Snapshot=@{ Root=$RepoRoot;Profile='Disposable exit fixture';SaveDatabase='';SourceCommit='';SourceBranch='fixture';BuildCommit='';Requirements=@();Services=$services;Conflicts=@() } } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
