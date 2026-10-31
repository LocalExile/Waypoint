# Real adapter operations in a GUID checkout with empty process/port inventory. No native game/server is launched.
$ErrorActionPreference = 'Stop'
$bridge = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../scripts/RuntimeBridge.ps1'))
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('Launcher-mod-control-' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $fixture 'Checkout with spaces'
$oldLocation = Get-Location
$oldData = $env:LOCALAPPDATA
$env:LOCALAPPDATA = Join-Path $fixture 'launcher-data'
$script:passed = 0
function Check($value, $message) { if (-not $value) { throw "FAIL: $message" }; $script:passed++; Write-Host "PASS: $message" }
function Operation([string]$action, [hashtable]$extra = @{}) {
    function Get-FileHash { throw 'The adapter must hash mods without depending on PowerShell module discovery.' }
    function Get-CimInstance { param([string]$ClassName); if ($ClassName -ne 'Win32_Process') { throw 'Unexpected fixture process query' } }
    function Get-NetTCPConnection { param([string]$State); if ($State -ne 'Listen') { throw 'Unexpected fixture listener query' } }
    $directory = Join-Path $fixture ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory | Out-Null
    $parameters = @{ Action=$action; RepoRoot=$root; ResultPath=(Join-Path $directory 'result.json') }
    foreach ($key in $extra.Keys) { $parameters[$key] = $extra[$key] }
    & $bridge @parameters | Out-Host
    return Get-Content -LiteralPath $parameters.ResultPath -Raw | ConvertFrom-Json
}
function Backups { return @(Get-ChildItem -LiteralPath "$env:LOCALAPPDATA/EmulatorWorkspaceLauncher/backups" -Filter backup.json -Recurse -ErrorAction SilentlyContinue) }
try {
    New-Item -ItemType Directory -Force "$root/scripts/dev", "$root/src/GameServer.Gateway", "$root/src/GameServer.Modding", "$root/Mods/Source/Alpha", "$root/Mods/Binaries/alpha", "$root/data" | Out-Null
    Set-Content "$root/GameServer.slnx" '<Solution />'
    Set-Content "$root/scripts/dev/live.ps1" 'param([switch]$ClientOnly)'
    Set-Content "$root/src/GameServer.Gateway/Program.Mods.cs" '// capability fixture'
    Set-Content "$root/src/GameServer.Modding/GameServer.Modding.csproj" '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'
    Set-Content "$root/Mods/Source/Alpha/Alpha.csproj" '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputPath>../../Binaries/alpha/</OutputPath><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath></PropertyGroup><ItemGroup><None Include="mod.json" CopyToOutputDirectory="PreserveNewest"/></ItemGroup></Project>'
    Set-Content "$root/Mods/Source/Alpha/mod.json" '{"id":"alpha","version":"1","enabled":false}'
    Set-Content "$root/Mods/Binaries/alpha/mod.json" '{"id":"alpha","version":"1","enabled":false}'
    Set-Content "$root/Mods/Source/Alpha/Mod.cs" 'public class Mod {}'
    Set-Content "$root/Mods/Binaries/alpha/alpha.dll" 'inert fixture'
    Set-Content "$root/Mods/Binaries/mod-state.txt" "other on`nalpha off"
    & python (Join-Path $PSScriptRoot 'SqliteFixture.py') init "$root/data/saves.db"
    if ($LASTEXITCODE -ne 0) { throw 'SQLite fixture failed' }
    $inspected = Operation 'Inspect'
    Check ($inspected.Ok -and $inspected.Snapshot.ModEnvironment.Supported -and -not $inspected.Snapshot.ModEnvironment.RuntimeReport) 'mod capability inspection does not invent runtime status'
    $manifestHash = (Get-FileHash "$root/Mods/Binaries/alpha/mod.json").Hash
    $enabled = Operation 'EnableMod' @{ModId='alpha'}
    Check ($enabled.Ok -and [IO.File]::ReadAllText("$root/Mods/Binaries/mod-state.txt").Contains('alpha on')) 'stopped-Gateway enable persists in the native mod-state file'
    Check ([IO.File]::ReadAllText("$root/Mods/Binaries/mod-state.txt").Contains('other on') -and (Get-FileHash "$root/Mods/Binaries/alpha/mod.json").Hash -eq $manifestHash) 'other choices and manifest defaults are preserved'
    $first = (Backups)[0]
    $metadata = Get-Content -LiteralPath $first.FullName -Raw | ConvertFrom-Json
    Check ((Backups).Count -eq 1 -and $metadata.Mods.Choices.Contains('alpha off') -and $metadata.Mods.Inventory[0].Assemblies.'alpha.dll'.Length -eq 64) 'automatic backup precedes the change and records mod choices, manifests and DLL fingerprints'
    $disabled = Operation 'DisableMod' @{ModId='alpha';NoBackup=$true}
    Check ($disabled.Ok -and (Backups).Count -eq 1 -and [IO.File]::ReadAllText("$root/Mods/Binaries/mod-state.txt").Contains('alpha off')) 'automatic backup can be disabled explicitly for mod changes'
    $manual = Operation 'Backup' @{NoBackup=$true}
    Check ($manual.Ok -and (Backups).Count -eq 2) 'manual save backup still records mods when automatic backup is off'
    $again = Operation 'EnableMod' @{ModId='alpha';NoBackup=$true}
    $binaryHash = (Get-FileHash "$root/Mods/Binaries/alpha/alpha.dll").Hash
    $dbHash = (Get-FileHash "$root/data/saves.db").Hash
    $restored = Operation 'RestoreModChoices' @{BackupId=$first.Directory.Name;NoBackup=$true}
    Check ($again.Ok -and $restored.Ok -and [IO.File]::ReadAllText("$root/Mods/Binaries/mod-state.txt").Contains('alpha off')) 'recorded mod choices have an explicit separate restore operation'
    Check ((Get-FileHash "$root/Mods/Binaries/alpha/alpha.dll").Hash -eq $binaryHash -and (Get-FileHash "$root/data/saves.db").Hash -eq $dbHash) 'mod-choice restore does not replace DLLs or saves'
    $beforeEmpty = @((Backups) | ForEach-Object FullName)
    [IO.File]::WriteAllText("$root/Mods/Binaries/mod-state.txt", '')
    $emptyBackup = Operation 'Backup'
    $emptyMetadata = (Backups) | Where-Object { $_.FullName -notin $beforeEmpty } | Select-Object -First 1
    Set-Content "$root/Mods/Binaries/mod-state.txt" 'alpha on'
    $emptyRestore = Operation 'RestoreModChoices' @{BackupId=$emptyMetadata.Directory.Name;NoBackup=$true}
    Check ($emptyBackup.Ok -and $emptyRestore.Ok -and [IO.File]::ReadAllText("$root/Mods/Binaries/mod-state.txt").Length -eq 0) 'a backup with no enable overrides restores the manifest defaults'
    Set-Content "$root/Mods/Binaries/mod-state.txt" "other on`nalpha off"
    $badId = Operation 'EnableMod' @{ModId='../outside';NoBackup=$true}
    Check (-not $badId.Ok) 'mod IDs cannot escape the binary folder'
    $badProject = Operation 'BuildMod' @{ModProject='src/GameServer.Modding/GameServer.Modding.csproj';NoBackup=$true}
    Check (-not $badProject.Ok -and $badProject.Message.Contains('Mods/Source')) 'mod builds cannot target arbitrary checkout projects'
    $built = Operation 'BuildMod' @{ModProject='Mods/Source/Alpha/Alpha.csproj';NoBackup=$true}
    Check ($built.Ok -and (Get-Item "$root/Mods/Binaries/alpha/alpha.dll").Length -gt 100) 'selected source builds use the real .NET build and normal mod output directory'
    Set-Content "$root/Mods/Source/Alpha/Mod.cs" '#error Expected mod-control test failure'
    $failed = Operation 'BuildMod' @{ModProject='Mods/Source/Alpha/Alpha.csproj';NoBackup=$true}
    Check (-not $failed.Ok -and $failed.FailedStage -eq 'Building mod') 'a failed mod build reports failure without claiming a live reload'
    New-Item -ItemType Directory -Path "$root/Mods/Binaries/external" | Out-Null
    Set-Content "$root/Mods/Binaries/external/mod.json" '{"id":"external"}'
    Set-Content "$root/Mods/Binaries/external/external.dll" 'third-party output fixture'
    Set-Content "$root/Mods/Binaries/alpha/notes.txt" 'owner-created extra file'
    $externalHash = (Get-FileHash "$root/Mods/Binaries/external/external.dll").Hash
    Move-Item -LiteralPath "$root/Mods/Source/Alpha/Alpha.csproj" -Destination "$root/Mods/Source/Alpha/Alpha.csproj.saved"
    $registeredBytes = [IO.File]::ReadAllBytes("$root/Mods/Binaries/alpha/alpha.dll")
    Set-Content "$root/Mods/Binaries/alpha/alpha.dll" 'externally replaced binary'
    $changedOutput = Operation 'BuildMods' @{NoBackup=$true}
    Check (-not $changedOutput.Ok -and $changedOutput.Message.Contains('changed outside') -and (Test-Path "$root/Mods/Binaries/alpha/mod.json")) 'changed managed outputs block retirement and remain in place for review'
    [IO.File]::WriteAllBytes("$root/Mods/Binaries/alpha/alpha.dll", $registeredBytes)
    $retired = Operation 'BuildMods' @{NoBackup=$true}
    $recoveredManifests = @(Get-ChildItem "$env:LOCALAPPDATA/EmulatorWorkspaceLauncher/retired-mods" -Filter mod.json -Recurse)
    Check ($retired.Ok -and -not (Test-Path "$root/Mods/Binaries/alpha/mod.json") -and $recoveredManifests.Count -eq 1) 'a removed source project retires its recorded outputs into recoverable storage'
    Check ((Get-FileHash "$root/Mods/Binaries/external/external.dll").Hash -eq $externalHash -and [IO.File]::ReadAllText("$root/Mods/Binaries/alpha/notes.txt").Contains('owner-created')) 'retiring a managed mod preserves third-party mods and extra owner files'
    $query = Operation 'QueryMods'
    Check (-not $query.Ok -and $query.Message.Contains('Enter the game') -and (Backups).Count -eq 3) 'live controls without a client explain the requirement and read-only queries do not make backups'
    $databaseBytes = [IO.File]::ReadAllBytes("$root/data/saves.db")
    Set-Content "$root/data/saves.db" 'not SQLite'
    $blocked = Operation 'EnableMod' @{ModId='external'}
    Check (-not $blocked.Ok -and [IO.File]::ReadAllText("$root/Mods/Binaries/mod-state.txt").Contains('alpha off')) 'a failed automatic backup blocks a mod-choice change'
    [IO.File]::WriteAllBytes("$root/data/saves.db", $databaseBytes)
    Write-Host "$script:passed mod control checks passed."
} finally {
    Set-Location -LiteralPath $oldLocation.Path
    $env:LOCALAPPDATA = $oldData
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -match '^Launcher-mod-control-[0-9a-f]{32}$') { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
