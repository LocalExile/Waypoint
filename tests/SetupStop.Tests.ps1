# The setup guide's Stop inside the adapter, on a disposable checkout with an empty mocked inventory: a stop request ends an
# operation between stages, and a stage that has begun finishes. No real processes are controlled or started.
$ErrorActionPreference = 'Stop'
$bridge = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../scripts/RuntimeBridge.ps1'))
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('Launcher-stop-' + [guid]::NewGuid().ToString('N'))
$oldAppData = $env:LOCALAPPDATA
$oldLocation = Get-Location
$oldPath = $env:PATH
$env:LOCALAPPDATA = Join-Path $fixture 'launcher-data'
$root = Join-Path $fixture 'LocalExile'
$script:passed = 0
function Check($value, $message) { if (-not $value) { throw "FAIL: $message" }; $script:passed++; Write-Host "PASS: $message" }
function Operation([string]$action, [scriptblock]$prepare = {}) {
    function Get-CimInstance { param([string]$ClassName); if ($ClassName -ne 'Win32_Process') { throw 'Unexpected fixture inventory query' } }
    function Get-NetTCPConnection { param([string]$State); if ($State -ne 'Listen') { throw 'Unexpected fixture listener query' } }
    $directory = Join-Path $fixture ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory | Out-Null
    & $prepare $directory
    & $bridge -Action $action -RepoRoot $root -ResultPath (Join-Path $directory 'result.json') | Out-Host
    return Get-Content -LiteralPath (Join-Path $directory 'result.json') -Raw | ConvertFrom-Json
}
try {
    New-Item -ItemType Directory -Force "$root/scripts/dev", "$root/Tools/ooz", "$root/data", (Join-Path $fixture 'bin') | Out-Null
    Set-Content "$root/GameServer.slnx" '<Solution />'
    Set-Content "$root/scripts/dev/live.ps1" 'param([switch]$ClientOnly)'
    Set-Content "$root/Tools/ooz/package-lock.json" '{}'
    # A stand-in for npm that says it ran, and asks to stop while it runs, as Stop does in a stage that must finish.
    Set-Content (Join-Path $fixture 'bin/npm.cmd') "@echo off`r`necho ran>installed.txt`r`nif defined STOP_FILE echo.>`"%STOP_FILE%`"`r`n"
    $env:PATH = (Join-Path $fixture 'bin') + ';' + $env:PATH

    $stopped = Operation 'Dependencies' { param($directory) Set-Content (Join-Path $directory 'stop.request') '' }
    Check (-not $stopped.Ok -and $stopped.Message -eq 'Stopped by the player.') ('a stop request ends an operation before its next stage: ' + $stopped.Message)
    Check (-not (Test-Path "$root/Tools/ooz/installed.txt")) 'a stopped operation does not start the work of the next stage'

    $finished = Operation 'Dependencies'
    Check $finished.Ok ('without a stop request the operation runs: ' + $finished.Message)
    Check (Test-Path "$root/Tools/ooz/installed.txt") 'the stage ran'

    Remove-Item "$root/Tools/ooz/installed.txt"
    $env:STOP_FILE = 'pending'
    $late = Operation 'Dependencies' { param($directory) $env:STOP_FILE = Join-Path $directory 'stop.request' }
    Check $late.Ok ('a stop that comes during the last stage lets it finish, and the operation reports what it did: ' + $late.Message)
    Check (Test-Path "$root/Tools/ooz/installed.txt") 'the stage that had begun finished'
    Write-Host "$script:passed setup stop checks passed."
} finally {
    Remove-Item Env:\STOP_FILE -ErrorAction SilentlyContinue
    $env:PATH = $oldPath
    $env:LOCALAPPDATA = $oldAppData
    Set-Location $oldLocation
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
