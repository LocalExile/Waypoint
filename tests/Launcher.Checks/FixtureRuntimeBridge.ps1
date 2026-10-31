param([string]$Action,[string]$RepoRoot,[string]$ResultPath,[string]$Service='All',[switch]$UseLocalProfile,[switch]$NoBackup,[switch]$NoHotReload)
# Only the private client-preparation fixture uses empty inventory. Do not weaken production runtime gates.
$expected = Join-Path ([IO.Path]::GetTempPath()) 'Launcher checks '
if ($Action -ne 'PrepareClient' -or -not ([IO.Path]::GetFullPath($RepoRoot).StartsWith($expected,[StringComparison]::OrdinalIgnoreCase)) -or
    -not (Test-Path -LiteralPath (Join-Path $RepoRoot '.waypoint-test-fixture'))) { throw 'Expected a disposable preparation fixture.' }
function Get-CimInstance { param([string]$ClassName); if($ClassName -ne 'Win32_Process') { throw 'Unexpected fixture inventory' } }
function Get-NetTCPConnection { param([string]$State); if($State -ne 'Listen') { throw 'Unexpected fixture listener query' } }
& (Join-Path $PSScriptRoot 'RuntimeBridge.ps1') -Action $Action -RepoRoot $RepoRoot -ResultPath $ResultPath -Service $Service -UseLocalProfile:$UseLocalProfile -NoBackup:$NoBackup -NoHotReload:$NoHotReload
