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
foreach ($name in 'Require','Assert-Physical','Get-ContentEnvironment','Join-Relative') {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    . ([scriptblock]::Create($function.Extent.Text))
}
function At([string]$path) { Join-Path $script:root $path }
function Get-WaypointData { Join-Path $fixture 'waypoint data' }
function Set-Stage($stage, $detail) { }
function Backup-Saves($snapshot) {
    if ($script:failBackup) { throw 'fixture backup failed' }
    if ($script:changeDuringBackup) { [IO.File]::WriteAllText((At 'data/modules/Example.json'), '{"duringBackup":true}') }
    $script:backups++
}
function Check($condition, $text) { if (-not $condition) { throw "FAIL: $text" }; $script:passed++; Write-Host "PASS: $text" }
function Refused([scriptblock]$run, $text) { $blocked=$false; try { & $run } catch { $blocked=$true }; Check $blocked $text }
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
    $Action='SaveLeagueModules'; $ModId=''; $ModTarget=''; $ModExpectedHash='missing'; $ModStaged=Stage '{"Standard":{"add":["Optional/Example"],"remove":[]}}'
    Save-ContentFile $snapshot
    Check ([IO.File]::ReadAllText((At 'data/league-modules.json')).Contains('Optional/Example')) 'per-league choices write only the user override'
    $ModExpectedHash=Get-ModFileHash (At 'data/league-modules.json'); $ModStaged=Stage '{"Standard":{"add":[],"remove":["Core/Game"]}}'
    Refused { Save-ContentFile $snapshot } 'adapter rejects removal of core gameplay'
    $ModStaged=Stage '{"Standard":{"add":["../outside"],"remove":[]}}'
    Refused { Save-ContentFile $snapshot } 'adapter rejects escaping module paths'
    $script:isRelease=$true
    $envInfo=Get-ContentEnvironment
    Check (-not $envInfo.CanChoose -and -not $envInfo.CanEditSettings) 'release installs never infer new capabilities from files alone'
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
