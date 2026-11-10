# Disposable configuration files and mocked save backup. No server or client is started.
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('Waypoint-content-' + [guid]::NewGuid().ToString('N'))
$script:root = Join-Path $fixture 'checkout with spaces'
$script:isRelease = $false
$script:passed = 0; $script:backups = 0; $script:failBackup = $false; $script:changeDuringBackup = $false
$oldModules = $env:Game_MODULES_FOLDER; $oldLeagues = $env:Game_LEAGUES_FOLDER; $oldChoices = $env:Game_LEAGUE_MODULES
$env:Game_MODULES_FOLDER = ''; $env:Game_LEAGUES_FOLDER = ''; $env:Game_LEAGUE_MODULES = ''
. (Join-Path $PSScriptRoot '../scripts/ModBridge.ps1')
. (Join-Path $PSScriptRoot '../scripts/ContentBridge.ps1')
$tokens=$null; $errors=$null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../scripts/RuntimeBridge.ps1'), [ref]$tokens, [ref]$errors)
foreach ($name in 'Require','Assert-Physical','Get-ContentEnvironment','Join-Relative','Get-AccountDatabases') {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    . ([scriptblock]::Create($function.Extent.Text))
}
function At([string]$path) { Join-Path $script:root $path }
$script:savePath = At "data/saves.db"
$script:splitSaves = $false
function Get-Python { if ([Environment]::OSVersion.Platform -eq 'Win32NT') { 'python' } else { 'python3' } }
function Get-WaypointData { Join-Path $fixture 'waypoint data' }
function Set-Stage($stage, $detail) { }
function Assert-AccountsStopped($snapshot) { Require (-not $script:running) 'fixture writer is running' }
function Get-Snapshot { $snapshot }
function Backup-Saves($snapshot) {
    if ($script:changeLeagueDuringBackup) { Add-Content -LiteralPath (At 'Leagues/Standard/league.json') ' ' }
    if ($script:startDuringBackup) { $script:running=$true }
    if ($script:failBackup) { throw 'fixture backup failed' }
    if ($script:changeDuringBackup) { [IO.File]::WriteAllText((At 'data/modules/Example.json'), '{"duringBackup":true}') }
    $script:backups++
}
function Check($condition, $text) { if (-not $condition) { throw "FAIL: $text" }; $script:passed++; Write-Host "PASS: $text" }
function Refused([scriptblock]$run, $text) { $blocked=$false; try { & $run } catch { $blocked=$true }; Check $blocked $text }
function StageLeague($choices) {
    $review = (Invoke-LeagueData (Get-ContentEnvironment) @{} "Inspect" "check").Review
    return Stage (@{ league="Standard"; choices=($choices | ConvertFrom-Json); review=$review } | ConvertTo-Json -Depth 30)
}
function Stage($json) {
    $folder = Join-Path (Get-WaypointData) ('mod-downloads/' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $folder | Out-Null
    $file = Join-Path $folder 'values.json'; [IO.File]::WriteAllText($file, $json); return $file
}
try {
    New-Item -ItemType Directory -Force (At 'Modules/Optional/Example'), (At 'Leagues/Standard'), (At 'src/GameServer.Core/Accounts'), (At 'src/GameServer.Gateway/Modding') | Out-Null
    Set-Content -LiteralPath (At 'Modules/Optional/Example/module.json') '{"id":"Example"}'
    Set-Content -LiteralPath (At 'Modules/Optional/Example/settings.json') '{"settings":{"count":{"type":"integer","default":1}}}'
    Set-Content -LiteralPath (At 'Leagues/Standard/league.json') '{"name":"Standard"}'
    New-Item -ItemType Directory -Force (At 'Modules/Core/Game') | Out-Null
    Set-Content -LiteralPath (At 'Modules/Core/Game/module.json') '{"id":"Game"}'
    Set-Content -LiteralPath (At 'Leagues/Standard/modules.json') '{"modules":["Core/Game","Optional/Example"]}'
    Set-Content -LiteralPath (At 'Leagues/Standard/settings.json') '{"settings":{}}'
    Set-Content -LiteralPath (At 'src/GameServer.Core/Accounts/LeagueCatalog.cs') '// Game_LEAGUE_MODULES'
    Set-Content -LiteralPath (At 'src/GameServer.Gateway/Modding/ModSettings.cs') '// settings fixture'
    $snapshot = [pscustomobject]@{ Conflicts=@() }
    $envInfo = Get-ContentEnvironment
    Check ($envInfo.CanChoose -and $envInfo.CanEditSettings) 'source hooks expose module and league capabilities'
    $Action='SaveContentSettings'; $ModId='Example'; $ModTarget='Modules'; $ModExpectedHash='missing'; $ModStaged=Stage '{"count":2}'
    Save-ContentFile $snapshot
    Check ([IO.File]::ReadAllText((At 'data/modules/Example.json')) -eq '{"count":2}' -and $script:backups -eq 1) 'module values save after backup to the module namespace'
    Check ([IO.File]::ReadAllText((At 'Modules/Optional/Example/settings.json')).Contains('"default":1')) 'shipped declarations remain unchanged'
    $ModExpectedHash=Get-ModFileHash (At 'data/modules/Example.json'); $ModStaged=Stage '{"count":3}'
    Save-ContentFile $snapshot
    $history = @(Get-ChildItem -LiteralPath (Join-Path (Get-WaypointData) 'config-history') -File -Recurse)
    Check ($history.Count -eq 1 -and [IO.File]::ReadAllText($history[0].FullName) -eq '{"count":2}') 'replaced settings remain recoverable'
    $ModStaged=Stage '{"count":4}'
    Refused { Save-ContentFile $snapshot } 'a stale form is refused'
    Check ([IO.File]::ReadAllText((At 'data/modules/Example.json')) -eq '{"count":3}') 'stale saves preserve current values'
    $ModExpectedHash=Get-ModFileHash (At 'data/modules/Example.json'); $script:failBackup=$true
    Refused { Save-ContentFile $snapshot } 'backup failure blocks settings writes'
    Check ([IO.File]::ReadAllText((At 'data/modules/Example.json')) -eq '{"count":3}') 'failed backup leaves module values untouched'
    $script:failBackup=$false; $script:changeDuringBackup=$true
    Refused { Save-ContentFile $snapshot } 'a change during backup is refused'
    Check ([IO.File]::ReadAllText((At 'data/modules/Example.json')).Contains('duringBackup')) 'concurrent tool changes are preserved'
    $script:changeDuringBackup=$false
    $ModId='Standard'; $ModTarget='Leagues'; $ModExpectedHash='missing'; $ModStaged=Stage '{"level":2}'
    Save-ContentFile $snapshot
    Check (Test-Path -LiteralPath (At 'data/leagues/Standard.json')) 'league settings use a separate namespace'
    $Action='SaveLeagueModules'; $ModId=''; $ModTarget=''; $ModExpectedHash='missing'; $ModStaged=StageLeague '{"Standard":{"add":["Optional/Example"],"remove":[]}}'
    Save-ContentFile $snapshot
    Check ([IO.File]::ReadAllText((At 'data/league-modules.json')).Contains('Optional/Example')) 'per-league choices write only the user override'
    $ModExpectedHash=Get-ModFileHash (At 'data/league-modules.json'); $ModStaged=StageLeague '{"Standard":{"add":[],"remove":["Core/Game"]}}'
    Refused { Save-ContentFile $snapshot } 'adapter rejects removal of core gameplay'
    $ModStaged=StageLeague '{"Standard":{"add":["../outside"],"remove":[]}}'
    Refused { Save-ContentFile $snapshot } 'adapter rejects escaping module paths'
    New-Item -ItemType Directory -Force (At 'Leagues/Standard/Source'), (At 'Leagues/Void/Source') | Out-Null
    Set-Content -LiteralPath (At 'Leagues/Standard/Source/Fixture.cs') 'public sealed class Fixture : League { }'
    Set-Content -LiteralPath (At 'Leagues/Void/league.json') '{"name":"Void","creatable":false}'
    Set-Content -LiteralPath (At 'Leagues/Void/modules.json') '{"modules":[]}'
    Set-Content -LiteralPath (At 'src/GameServer.Gateway/Modding/ModHost.cs') '// ReadLeagueImages fixture'
    function StageCreation($name) {
        $review=(Invoke-LeagueData (Get-ContentEnvironment) @{} 'Inspect' 'check').Review
        Stage (@{template='Standard'; name=$name; description='An isolated event.'; modules=@('Core/Game','Optional/Example'); review=$review} | ConvertTo-Json -Depth 30)
    }
    $env:Game_LEAGUE_MODULES=''
    $Action='CreateLeague'; $ModStaged=StageCreation 'My Event'
    Save-ContentFile $snapshot
    $definition=Get-Content -LiteralPath (At 'Leagues/My Event/league.json') -Raw | ConvertFrom-Json
    Check ($definition.parent -eq 'Void' -and (Test-Path -LiteralPath (At 'Leagues/My Event/Source/WaypointLeague.cs'))) 'creation publishes a complete isolated league'
    $ModStaged=StageCreation 'My Event'
    Refused { Save-ContentFile $snapshot } 'creation never replaces an existing league'
    $ModStaged=StageCreation 'Backup Failure'; $script:failBackup=$true
    Refused { Save-ContentFile $snapshot } 'failed backup prevents creating a league'
    Check (-not (Test-Path -LiteralPath (At 'Leagues/Backup Failure'))) 'failed creation preserves the original league catalogue'
    $script:failBackup=$false
    $ModStaged=StageCreation 'Concurrent Change'; $script:changeLeagueDuringBackup=$true
    Refused { Save-ContentFile $snapshot } 'definition changes during backup block creation'
    Check (-not (Test-Path -LiteralPath (At 'Leagues/Concurrent Change'))) 'concurrent changes do not leave a partially created league'
    $script:changeLeagueDuringBackup=$false
    $ModStaged=StageCreation 'Running Writer'; $script:running=$true
    Refused { Save-ContentFile $snapshot } 'running writers prevent league creation'
    $script:running=$false
    $ModStaged=StageCreation 'New Writer'; $script:startDuringBackup=$true
    Refused { Save-ContentFile $snapshot } 'writers are checked again after backup'
    Check (-not (Test-Path -LiteralPath (At 'Leagues/New Writer'))) 'a newly running writer cannot race league creation'
    $script:running=$false; $script:startDuringBackup=$false
    function StageVisibility($visible) {
        $review=(Invoke-LeagueData (Get-ContentEnvironment) @{} 'Inspect' 'check').Review
        Stage (@{league='Standard'; visible=$visible; review=$review} | ConvertTo-Json -Depth 30)
    }
    $Action='SetLeagueVisibility'; $ModStaged=StageVisibility $false
    Save-ContentFile $snapshot
    $hidden=Get-Content -LiteralPath (At 'Leagues/Standard/league.json') -Raw | ConvertFrom-Json
    Check (-not $hidden.creatable -and -not $hidden.ended) 'visibility hides only new character creation without ending the league'
    $ModStaged=StageVisibility $true; $script:running=$true
    Refused { Save-ContentFile $snapshot } 'visibility changes require stopped writers'
    $script:running=$false
    Save-ContentFile $snapshot
    Check ((Get-Content -LiteralPath (At 'Leagues/Standard/league.json') -Raw | ConvertFrom-Json).creatable) 'a hidden league can be offered again without changing its identity'
    $script:isRelease=$true
    $envInfo=Get-ContentEnvironment
    Check (-not $envInfo.CanChoose -and -not $envInfo.CanEditSettings -and -not $envInfo.CanCreate) 'release installs never infer new capabilities from files alone'
    Refused { Save-ContentFile $snapshot } 'legacy release configuration remains read-only'
    $script:isRelease=$false
    $env:Game_LEAGUE_MODULES='data/custom-choices.json'
    Check ((Get-ContentEnvironment).Choices -eq [IO.Path]::GetFullPath((At 'data/custom-choices.json'))) 'configured override paths are honored'
    $snapshot.Conflicts=@('fixture conflict')
    Refused { Save-ContentFile $snapshot } 'ownership conflicts block configuration writes'
    Write-Host "$script:passed content bridge checks passed."
} finally {
    $env:Game_MODULES_FOLDER=$oldModules; $env:Game_LEAGUES_FOLDER=$oldLeagues; $env:Game_LEAGUE_MODULES=$oldChoices
    $resolved=[IO.Path]::GetFullPath($fixture)
    if ((Test-ModChildPath $resolved ([IO.Path]::GetTempPath())) -and (Split-Path $resolved -Leaf) -match '^Waypoint-content-[0-9a-f]{32}$') { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
