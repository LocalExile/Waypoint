# Disposable source/save integration with empty mocked runtime inventory. No real processes are controlled or started.
$ErrorActionPreference = 'Stop'
$bridge = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../scripts/RuntimeBridge.ps1'))
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('Launcher-source-' + [guid]::NewGuid().ToString('N'))
$oldAppData = $env:LOCALAPPDATA
$oldLocation = Get-Location
$env:LOCALAPPDATA = Join-Path $fixture 'launcher-data'
$root = Join-Path $fixture 'LocalExile'
$script:passed = 0
function Check($value, $message) { if (-not $value) { throw "FAIL: $message" }; $script:passed++; Write-Host "PASS: $message" }
function Fixture-Git([string[]]$arguments) { & git -c core.hooksPath=NUL -c commit.gpgsign=false -C $root @arguments | Out-Null; if ($LASTEXITCODE) { throw 'Fixture Git failed' } }
function Operation([string]$action, [string[]]$extra = @()) {
    # Source/save behavior is independent of the owner's live fixed ports. The production process/port
    # ownership path has separate Bridge.Tests and real-install validation; these actions never launch servers.
    function Get-CimInstance { param([string]$ClassName); if ($ClassName -ne 'Win32_Process') { throw 'Unexpected fixture inventory query' } }
    function Get-NetTCPConnection { param([string]$State); if ($State -ne 'Listen') { throw 'Unexpected fixture listener query' } }
    $directory = Join-Path $fixture ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory | Out-Null
    $parameters = @{ Action=$action; RepoRoot=$root; ResultPath=(Join-Path $directory 'result.json') }
    for ($i=0; $i -lt $extra.Count; $i++) {
        $key = $extra[$i].TrimStart('-')
        if ($key -in @('NoBackup','StartFresh')) { $parameters[$key] = $true }
        else { $i++; $parameters[$key] = $extra[$i] }
    }
    # These fixture actions never call live.ps1 or start detached runtime processes.
    & $bridge @parameters | Out-Host
    return Get-Content -LiteralPath (Join-Path $directory 'result.json') -Raw | ConvertFrom-Json
}
try {
    New-Item -ItemType Directory -Force "$root/scripts/dev", "$root/src/GameServer.Gateway", "$root/data" | Out-Null
    Set-Content "$root/.gitignore" "data/`nbin/`nobj/"
    Set-Content "$root/GameServer.slnx" '<Solution><Project Path="src/GameServer.Gateway/GameServer.Gateway.csproj" /></Solution>'
    Set-Content "$root/scripts/dev/live.ps1" 'param([switch]$ClientOnly)'
    Set-Content "$root/src/GameServer.Gateway/GameServer.Gateway.csproj" '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'
    Set-Content "$root/src/GameServer.Gateway/Test.cs" 'public class Fixture { public int Value => 1; }'
    Fixture-Git @('init','-b','main'); Fixture-Git @('config','user.name','Launcher fixture'); Fixture-Git @('config','user.email','fixture@example.invalid')
    Fixture-Git @('add','.'); Fixture-Git @('commit','-m','initial')
    $initial = [string](& git -C $root rev-parse HEAD)
    Fixture-Git @('switch','-c','feature'); Set-Content "$root/src/GameServer.Gateway/Test.cs" 'public class Fixture { public int Value => 2; }'
    Fixture-Git @('add','.'); Fixture-Git @('commit','-m','feature'); Fixture-Git @('switch','main')
    & python (Join-Path $PSScriptRoot 'SqliteFixture.py') wal (Join-Path $fixture 'wal.db') (Join-Path $PSScriptRoot '../scripts/SaveBackup.py') (Join-Path $fixture 'wal-backup.db')
    Check ($LASTEXITCODE -eq 0) 'online save backup includes committed WAL pages while the original database is still open'
    & python (Join-Path $PSScriptRoot 'SqliteFixture.py') init "$root/data/saves.db"
    Check ($LASTEXITCODE -eq 0) 'private SQLite save fixture created'
    $switched = Operation 'Switch' @('-SourceKind','Branch','-SourceValue','refs/heads/feature')
    Check $switched.Ok ('branch activates and builds without external client/build paths: ' + $switched.Message)
    New-Item -ItemType Directory -Path "$root/mods/binaries/external" -Force | Out-Null
    Set-Content "$root/mods/binaries/external/mod.json" '{"id":"external","version":"untracked external runtime fixture"}'
    $roundTrip = Operation 'Switch' @('-SourceKind','Branch','-SourceValue','refs/heads/main','-NoBackup')
    Check ($roundTrip.Ok -and [IO.File]::ReadAllText("$root/mods/binaries/external/mod.json").Contains('runtime fixture')) 'untracked mod binaries survive switching to and from a revision without their gitignore rule'
    $backToFeature = Operation 'Switch' @('-SourceKind','Branch','-SourceValue','refs/heads/feature','-NoBackup')
    Check $backToFeature.Ok 'runtime mod files do not block returning to the mod-host branch'
    Check (([string](& git -C $root branch --show-current)) -eq 'feature') 'local branch switch uses normal Git without force/reset'
    $metaFiles = @(Get-ChildItem "$env:LOCALAPPDATA/EmulatorWorkspaceLauncher/backups" -Filter backup.json -Recurse)
    Check ($metaFiles.Count -eq 1) 'default branch activation automatically backs up current characters'
    $meta = Get-Content $metaFiles[0].FullName -Raw | ConvertFrom-Json
    Check ($meta.SourceCommit -eq $initial -and -not $meta.LastBuildCommit) 'backup records full source identity and honestly leaves unknown launch identity empty'
    $firstId = $metaFiles[0].Directory.Name
    $fresh = Operation 'Switch' @('-SourceKind','Branch','-SourceValue','refs/heads/main','-StartFresh')
    Check $fresh.Ok ('fresh start builds before replacing saves: ' + $fresh.Message)
    & python (Join-Path $PSScriptRoot 'SqliteFixture.py') empty "$root/data/saves.db"
    Check ($LASTEXITCODE -eq 0) 'fresh option starts an empty save database while retaining the old database for recovery'
    Check (@(Get-ChildItem "$env:LOCALAPPDATA/EmulatorWorkspaceLauncher/backups" -Filter backup.json -Recurse).Count -eq 2) 'fresh and current-character choices both use automatic backups'
    $restored = Operation 'RestoreBackup' @('-BackupId',$firstId)
    Check $restored.Ok ('restore validates snapshot and backs up the current database first: ' + $restored.Message)
    & python (Join-Path $PSScriptRoot 'SqliteFixture.py') restored "$root/data/saves.db"
    Check ($LASTEXITCODE -eq 0) 'restore recovers the selected characters'
    $before = @(Get-ChildItem "$env:LOCALAPPDATA/EmulatorWorkspaceLauncher/backups" -Filter backup.json -Recurse).Count
    $disabled = Operation 'Switch' @('-SourceKind','Branch','-SourceValue','refs/heads/feature','-NoBackup')
    Check ($disabled.Ok -and @(Get-ChildItem "$env:LOCALAPPDATA/EmulatorWorkspaceLauncher/backups" -Filter backup.json -Recurse).Count -eq $before) 'backup preference can be disabled explicitly'
    Fixture-Git @('update-ref','refs/remotes/fixture/main',$initial)
    $remote = Operation 'Switch' @('-SourceKind','Remote','-SourceValue','refs/remotes/fixture/main')
    Check ($remote.Ok -and $remote.Snapshot.SourceLabel -eq 'fixture/main' -and -not $remote.Snapshot.SourceBranch) 'detached remote activation retains a friendly source label bound to the actual commit'
    $localAgain = Operation 'Switch' @('-SourceKind','Branch','-SourceValue','refs/heads/feature')
    Check ($localAgain.Ok -and $localAgain.Snapshot.SourceLabel -eq 'feature') 'returning to a local branch replaces the detached source label'
    Fixture-Git @('switch','-c','broken')
    Set-Content "$root/src/GameServer.Gateway/Test.cs" 'invalid source fixture'
    Fixture-Git @('add','src/GameServer.Gateway/Test.cs'); Fixture-Git @('commit','-m','broken fixture'); Fixture-Git @('switch','feature')
    $failedBuild = Operation 'Switch' @('-SourceKind','Branch','-SourceValue','refs/heads/broken')
    Check (-not $failedBuild.Ok -and $failedBuild.Message.Contains('did not build')) 'a failed target build reports the source switch and leaves services stopped'
    & python (Join-Path $PSScriptRoot 'SqliteFixture.py') restored "$root/data/saves.db"
    Check ($LASTEXITCODE -eq 0) 'failed target builds preserve characters'
    $recovered = Operation 'Switch' @('-SourceKind','Branch','-SourceValue','refs/heads/feature')
    Check $recovered.Ok ('a failed target build can be switched back through the same workflow: ' + $recovered.Message)
    $saveBytes = [IO.File]::ReadAllBytes("$root/data/saves.db")
    Set-Content "$root/data/saves.db" 'not a database'
    $backupFailure = Operation 'Switch' @('-SourceKind','Branch','-SourceValue','refs/heads/main')
    Check (-not $backupFailure.Ok -and ([string](& git -C $root branch --show-current)) -eq 'feature') 'backup failure blocks source activation before Git changes'
    [IO.File]::WriteAllBytes("$root/data/saves.db", $saveBytes)
    Set-Content "$root/src/GameServer.Gateway/Test.cs" 'uncommitted owner work'
    $dirty = Operation 'Switch' @('-SourceKind','Branch','-SourceValue','refs/heads/main')
    Check (-not $dirty.Ok -and $dirty.Message.Contains('local changes') -and (Get-Content "$root/src/GameServer.Gateway/Test.cs" -Raw).Contains('uncommitted owner work')) 'dirty checkout refuses switching without stashing or discarding work'
    Fixture-Git @('restore','--','src/GameServer.Gateway/Test.cs')
    Set-Content "$root/OwnerSource.cs" 'untracked owner source'
    $untracked = Operation 'Switch' @('-SourceKind','Branch','-SourceValue','refs/heads/main')
    Check (-not $untracked.Ok -and (Test-Path "$root/OwnerSource.cs")) 'untracked owner source outside mod runtime folders still blocks switching'
    Remove-Item -LiteralPath "$root/OwnerSource.cs"
    if (-not (Test-Path -LiteralPath "$root/mods/binaries/external/mod.json")) { throw 'The preserved runtime fixture disappeared before the staged-change test.' }
    Fixture-Git @('add','-f','--',':(icase)mods/binaries/external/mod.json')
    $trackedRuntime = Operation 'Switch' @('-SourceKind','Branch','-SourceValue','refs/heads/main')
    Check (-not $trackedRuntime.Ok -and ([string](& git -C $root branch --show-current)) -eq 'feature') 'tracked edits inside mod runtime folders still block source switching'
    $invalid = Operation 'RestoreBackup' @('-BackupId','../../other')
    Check (-not $invalid.Ok) 'backup identifiers cannot escape the selected save store'
    $deleted = Operation 'DeleteBackup' @('-BackupId',$firstId)
    Check ($deleted.Ok -and -not (Test-Path -LiteralPath $metaFiles[0].Directory.FullName)) 'only the selected backup can be deleted; current saves remain'
    Write-Host "$script:passed source/save integration checks passed."
} finally {
    Set-Location -LiteralPath $oldLocation.Path
    $env:LOCALAPPDATA = $oldAppData
    # Only this test's new GUID directory. Git objects can be read-only on Windows.
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -match '^Launcher-source-[0-9a-f]{32}$') {
        Get-ChildItem -LiteralPath $resolved -File -Recurse -Force | ForEach-Object { $_.IsReadOnly = $false }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
