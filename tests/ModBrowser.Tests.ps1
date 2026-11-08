# Portable, real mod file operations in a disposable checkout. No game, server, downloads or user saves.
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('Waypoint-mod-browser-' + [guid]::NewGuid().ToString('N'))
$script:root = Join-Path $fixture 'checkout with spaces'
$script:isRelease = $false
$script:backups = 0
$script:failBackup = $false
$script:passed = 0
$previousModFolder = $env:Game_MODS_FOLDER
$env:Game_MODS_FOLDER = $null
. (Join-Path $PSScriptRoot '../scripts/ModBridge.ps1')
# Use the production guards without running RuntimeBridge's startup or process inspection.
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../scripts/RuntimeBridge.ps1'), [ref]$tokens, [ref]$parseErrors)
foreach ($name in 'Require','Assert-Physical') {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    . ([scriptblock]::Create($function.Extent.Text))
}
function At([string]$path) { Join-Path $script:root $path }
function Get-WaypointData { Join-Path $fixture 'waypoint data' }
function Get-SourceSelectionPath { Join-Path $fixture 'history/selection.json' }
function Same-Path($a, $b) { [IO.Path]::GetFullPath($a) -ceq [IO.Path]::GetFullPath($b) }
function Backup-Saves($snapshot) { if ($script:failBackup) { throw 'fixture backup failed' }; $script:backups++ }
function Set-Stage($name, $detail) { }
function Check($condition, $message) {
    if (-not $condition) { throw "FAIL: $message" }
    $script:passed++; Write-Host "PASS: $message"
}
function Refused([scriptblock]$run, [string]$message) {
    $failed = $false
    try { & $run | Out-Null } catch { $failed = $true }
    Check $failed $message
}
function Stage([string]$id, [string]$version) {
    $folder = Join-Path (Get-WaypointData) ('mod-downloads/' + [guid]::NewGuid().ToString('N') + '/' + $id)
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $folder 'mod.json'), ('{"id":"' + $id + '","version":"' + $version + '"}'))
    [IO.File]::WriteAllText((Join-Path $folder ($id + '.dll')), ('inert fixture ' + $version))
    return $folder
}
function Operation([string]$action, [string]$id = '', [string]$staged = '', [string]$target = '') {
    $script:Action = $action; $script:ModId = $id; $script:ModStaged = $staged; $script:ModTarget = $target
    Invoke-ModOperation $snapshot
}
try {
    New-Item -ItemType Directory -Force (At 'src/GameServer.Gateway/Modding'), (At 'src/GameServer.Modding'), (At 'Mods/Binaries') | Out-Null
    Set-Content -LiteralPath (At 'src/GameServer.Gateway/Modding/Program.Mods.cs') '// fixture'
    Set-Content -LiteralPath (At 'src/GameServer.Modding/GameServer.Modding.csproj') '<Project />'
    $snapshot = [pscustomobject]@{ Conflicts=@(); Services=@([pscustomobject]@{ Name='Gateway'; Status='Stopped'; ProcessId=0; WatchProcessId=0 }); SourceCommit='fixture' }
    $staged = Stage 'alpha' '1'
    Check ((Get-StagedModPath $staged) -ceq [IO.Path]::GetFullPath($staged)) 'native paths with spaces pass the staging guard'
    $downloads = Join-Path (Get-WaypointData) 'mod-downloads'
    Check (-not (Test-ModChildPath $downloads $downloads)) 'the downloads directory itself is not a staged child'
    Check (-not (Test-ModChildPath ($downloads + '-outside/alpha') $downloads)) 'a sibling sharing the prefix is rejected'
    Refused { Get-StagedModPath (Join-Path $downloads '../outside') } 'parent traversal is rejected'
    Refused { Get-StagedModPath 'relative/alpha' } 'relative staging paths are rejected'
    if ([IO.Path]::DirectorySeparatorChar -eq '/') {
        Check (-not (Test-ModChildPath ($downloads.ToUpperInvariant() + '/alpha') $downloads)) 'Linux containment is case-sensitive'
    }
    $link = Join-Path $downloads 'redirect'
    $kind = if ([IO.Path]::DirectorySeparatorChar -eq '/') { 'SymbolicLink' } else { 'Junction' }
    New-Item -ItemType $kind -Path $link -Target $script:root | Out-Null
    try { Refused { Get-StagedModPath $link } 'redirected staging folders are rejected' }
    finally { [IO.Directory]::Delete($link) }
    Operation 'InstallMod' 'alpha' $staged 'Binaries'
    Check ((Test-Path -LiteralPath (At 'Mods/Binaries/alpha/alpha.dll')) -and -not (Test-Path -LiteralPath $staged)) 'install copies the prepared mod and consumes staging'
    Check ($script:backups -eq 1) 'install backs up before writing'
    Set-Content -LiteralPath (At 'Mods/Binaries/mod-state.txt') "alpha off`nbeta on"
    $settings = Join-Path (Get-WaypointData) 'mod-downloads/settings/alpha.json'
    New-Item -ItemType Directory -Force (Split-Path $settings) | Out-Null
    [IO.File]::WriteAllText($settings, '{"quantity":2,"ownerValue":"keep"}')
    Operation 'SaveModSettings' 'alpha' $settings
    Check ((Get-Content -LiteralPath (At 'data/mods/alpha.json') -Raw | ConvertFrom-Json).quantity -eq 2) 'settings save accepts native staging paths'
    Check (-not (Test-Path -LiteralPath $settings)) 'settings staging is consumed after saving'
    [IO.File]::WriteAllText($settings, '[]')
    Refused { Operation 'SaveModSettings' 'alpha' $settings } 'non-object settings are refused'
    Check ([IO.File]::ReadAllText((At 'data/mods/alpha.json')).Contains('ownerValue')) 'refused settings preserve the previous values'
    New-Item -ItemType Directory -Force (At 'Mods/Source/alpha') | Out-Null
    Set-Content -LiteralPath (At 'Mods/Source/alpha/owner.cs') 'owner source'
    Operation 'InstallMod' 'alpha' (Stage 'alpha' '2') 'Binaries'
    Check ((Get-Content -LiteralPath (At 'Mods/Binaries/alpha/mod.json') -Raw | ConvertFrom-Json).version -eq '2') 'update installs the new version'
    Check (-not (Test-Path -LiteralPath (At 'Mods/Source/alpha'))) 'binary update retires the source that could overwrite it'
    Check (@(Get-ChildItem -LiteralPath (At 'Mods/.replaced') -Recurse -Filter owner.cs).Count -eq 1) 'retired source remains recoverable'
    Check (@(Get-ChildItem -LiteralPath (At 'Mods/.replaced') -Recurse -Filter alpha.dll | Where-Object { [IO.File]::ReadAllText($_.FullName) -eq 'inert fixture 1' }).Count -eq 1) 'previous binary remains recoverable'
    Check ([IO.File]::ReadAllText((At 'Mods/Binaries/mod-state.txt')).Contains('alpha off')) 'update preserves disabled choices'
    $script:failBackup = $true
    $blocked = Stage 'alpha' '3'
    Refused { Operation 'InstallMod' 'alpha' $blocked 'Binaries' } 'backup failure blocks an update'
    Check ([IO.File]::ReadAllText((At 'Mods/Binaries/alpha/alpha.dll')) -eq 'inert fixture 2') 'failed backup preserves installed files'
    $script:failBackup = $false
    $script:ModIds = 'beta;gamma'
    $script:ModStageds = (Stage 'beta' '1') + ';' + (Stage 'gamma' '1')
    $script:ModTargets = 'Binaries;Invalid'
    Refused { Operation 'InstallMod' } 'an invalid batch is refused before installing its first mod'
    Check (-not (Test-Path -LiteralPath (At 'Mods/Binaries/beta'))) 'batch validation leaves the first destination untouched'
    $script:ModTargets = 'Binaries;Binaries'
    $before = $script:backups
    Operation 'InstallMod'
    Check ((Test-Path -LiteralPath (At 'Mods/Binaries/beta/beta.dll')) -and (Test-Path -LiteralPath (At 'Mods/Binaries/gamma/gamma.dll')) -and $script:backups -eq $before + 1) 'batch install installs both mods with one backup'
    $script:ModIds = ''; $script:ModStageds = ''; $script:ModTargets = ''
    Operation 'UninstallMod' 'alpha'
    Check (-not (Test-Path -LiteralPath (At 'Mods/Binaries/alpha'))) 'uninstall removes the active folder'
    Check ((Test-Path -LiteralPath (At 'data/mods/alpha.json')) -and [IO.File]::ReadAllText((At 'data/mods/alpha.json')).Contains('ownerValue')) 'uninstall retains the player settings'
    Check ([IO.File]::ReadAllText((At 'Mods/Binaries/mod-state.txt')).Trim() -eq 'beta on') 'uninstall removes only its own choice'
    Check (@(Get-ChildItem -LiteralPath (At 'Mods/.replaced') -Recurse -Filter alpha.dll | Where-Object { [IO.File]::ReadAllText($_.FullName) -eq 'inert fixture 2' }).Count -eq 1) 'uninstalled binary remains recoverable'
    $script:ModIds = 'beta;gamma'
    $before = $script:backups
    Operation 'UninstallMod'
    Check (-not (Test-Path -LiteralPath (At 'Mods/Binaries/beta')) -and -not (Test-Path -LiteralPath (At 'Mods/Binaries/gamma')) -and $script:backups -eq $before + 1) 'batch uninstall removes both with one backup'
    $script:ModIds = ''
    Check (-not (Test-LegacyReleaseMods)) 'a source checkout is never a legacy release'
    $script:isRelease = $true
    try {
        foreach ($case in @(@('0.1.0-beta', $true), @('0.2.0-alpha', $true), @('0.2.0-beta', $false), @('0.2.0', $false), @('0.3.1', $false), @('nonsense', $true))) {
            [IO.File]::WriteAllText((At 'release.json'), '{ "version": "' + $case[0] + '" }')
            Check ((Test-LegacyReleaseMods) -eq $case[1]) "release $($case[0]) legacy mods: $($case[1])"
        }
    } finally { $script:isRelease = $false; Remove-Item -LiteralPath (At 'release.json') -ErrorAction SilentlyContinue }
    Write-Host "$script:passed mod browser checks passed."
} finally {
    $env:Game_MODS_FOLDER = $previousModFolder
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ((Test-ModChildPath $resolved ([IO.Path]::GetTempPath())) -and (Split-Path $resolved -Leaf) -match '^Waypoint-mod-browser-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
