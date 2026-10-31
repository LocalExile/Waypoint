# Production startup with disposable real .NET mod builds and simulated service consoles. Never starts a server/game.
$ErrorActionPreference = 'Stop'
$bridge = Join-Path $PSScriptRoot '../scripts/RuntimeBridge.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($bridge, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($function in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    . ([scriptblock]::Create($function.Extent.Text))
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('Launcher-mods-' + [guid]::NewGuid().ToString('N'))
$script:root = Join-Path $fixture 'Checkout with spaces'
$script:dotnetPath = (Get-Command dotnet -CommandType Application).Source
$oldLocation = Get-Location
$script:events = [Collections.Generic.List[string]]::new()
$script:buildArguments = [Collections.Generic.List[string]]::new()
$script:passed = 0
function Check($value, $message) { if (-not $value) { throw "FAIL: $message" }; $script:passed++; Write-Host "PASS: $message" }
function dotnet {
    $script:buildArguments.Add(($args -join '|'))
    $script:events.Add('build:' + [IO.Path]::GetFileNameWithoutExtension($args[1]))
    & $script:dotnetPath @args
    $script:LASTEXITCODE = $LASTEXITCODE
}
function Set-Stage([string]$stage, [string]$detail = '') { $script:stage = $stage; $script:events.Add("stage:$stage|$detail") }
function Backup-Saves($snapshot) { $script:events.Add('backup') }
function Get-Snapshot { return $script:snapshot }
function git {
    # Source switching/dirty-worktree behavior has real Git fixtures in Source.Tests; simulate its read-only guard here.
    if (($args -join '|') -ne ("-C|$script:root|diff|--quiet|HEAD")) { throw ('Unexpected fixture Git operation: ' + ($args -join '|')) }
    $script:LASTEXITCODE = 0
}
function Start-Process {
    param($FilePath, $ArgumentList, $WorkingDirectory, $WindowStyle)
    if ($FilePath -ne 'cmd.exe' -or $WindowStyle -ne 'Hidden' -or $WorkingDirectory -ne $script:root) { throw 'Unexpected simulated launch' }
    if ($ArgumentList -notmatch 'GameServer\.(Patch|Gateway|Auth)') { throw 'Unexpected service command' }
    $name = $Matches[1]
    $script:events.Add("start:$name")
    $service = $script:snapshot.Services | Where-Object Name -eq $name
    $service.Status = 'Ready'; $service.ProcessId = 123
}
function Reset-Startup([string[]]$ready = @()) {
    $script:events.Clear(); $script:buildArguments.Clear()
    $services = foreach ($name in @('Patch','Gateway','Auth')) {
        [pscustomobject]@{ Name=$name; Status=$(if ($name -in $ready) { 'Ready' } else { 'Stopped' }); ProcessId=$(if ($name -in $ready) { 123 } else { 0 }); WatchProcessId=0 }
    }
    $script:snapshot = [pscustomobject]@{ Requirements=@(); Services=@($services); Conflicts=@(); SourceCommit='fixture' }
}
function Add-Mod([string]$name) {
    $folder = At "Mods/Source/$name"
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputPath>../../Binaries/$name/</OutputPath><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath></PropertyGroup>
  <ItemGroup><ProjectReference Include="../../../src/GameServer.Modding/GameServer.Modding.csproj" Private="false"/><None Include="mod.json" CopyToOutputDirectory="PreserveNewest"/></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $folder "$name.csproj")
    Set-Content -LiteralPath (Join-Path $folder 'mod.json') -Value ('{"id":"' + $name + '","enabled":false}')
    Set-Content -LiteralPath (Join-Path $folder 'Mod.cs') -Value 'public class Mod : ModApi {}'
}
try {
    New-Item -ItemType Directory -Path $script:root -Force | Out-Null
    Set-Location -LiteralPath $script:root
    foreach ($name in @('Patch','Gateway','Auth','Modding')) {
        $folder = At "src/GameServer.$name"
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $folder "GameServer.$name.csproj") -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><InformationalVersion>fixture</InformationalVersion></PropertyGroup></Project>'
        Set-Content -LiteralPath (Join-Path $folder 'Fixture.cs') -Value 'public class ModApi {}'
    }
    New-Item -ItemType Directory -Path (At 'scripts/dev') -Force | Out-Null
    # The simulated extractor/launch entry never starts a process or reads any real client data.
    Set-Content -LiteralPath (At 'scripts/dev/extract-data.ps1') -Value 'Write-Host "Simulated extraction (no client data)"'
    Set-Content -LiteralPath (At 'scripts/dev/live.ps1') -Value '# $NoHotReload watch'
    Reset-Startup
    $NoHotReload = $false
    Start-Services $script:snapshot @('Gateway')
    Check ($script:events.Contains('start:Gateway') -and -not @($script:events | Where-Object { $_ -like 'stage:Building mods*' }).Count) 'older checkout without mod sources starts normally'

    Add-Mod 'alpha'; Add-Mod 'beta'
    New-Item -ItemType Directory -Path (At 'Mods/Binaries/external') -Force | Out-Null
    Set-Content -LiteralPath (At 'Mods/Binaries/mod-state.txt') -Value "alpha off`nbeta on"
    Set-Content -LiteralPath (At 'Mods/Binaries/external/custom.dll') -Value 'external mod fixture'
    $stateHash = (Get-FileHash -LiteralPath (At 'Mods/Binaries/mod-state.txt')).Hash
    $externalHash = (Get-FileHash -LiteralPath (At 'Mods/Binaries/external/custom.dll')).Hash
    Reset-Startup
    Start-Services $script:snapshot @('Patch','Gateway','Auth')
    $firstStart = $script:events.IndexOf('start:Patch')
    Check ($script:events.IndexOf('build:alpha') -lt $firstStart -and $script:events.IndexOf('build:beta') -lt $firstStart) 'all source mods build before any missing service starts'
    Check ((Test-Path -LiteralPath (At 'Mods/Binaries/alpha/alpha.dll')) -and (Test-Path -LiteralPath (At 'Mods/Binaries/beta/beta.dll'))) 'real mod builds produce binaries with checkout paths containing spaces'
    Check ($script:events.Contains('stage:Building mods|alpha') -and $script:events.Contains('stage:Building mods|beta')) 'progress names each mod being built'
    Check ((Get-FileHash -LiteralPath (At 'Mods/Binaries/mod-state.txt')).Hash -eq $stateHash -and (Get-FileHash -LiteralPath (At 'Mods/Binaries/external/custom.dll')).Hash -eq $externalHash) 'saved enable choices and binary-only mods remain byte-for-byte unchanged'
    $modTime = (Get-Item -LiteralPath (At 'Mods/Binaries/alpha/alpha.dll')).LastWriteTimeUtc
    Reset-Startup
    Start-Services $script:snapshot @('Gateway')
    Check ((Get-Item -LiteralPath (At 'Mods/Binaries/alpha/alpha.dll')).LastWriteTimeUtc -eq $modTime) 'unchanged mods use incremental builds'

    Reset-Startup @('Auth')
    $NoHotReload = $true
    Start-Services $script:snapshot @('Gateway')
    Check (@($script:buildArguments | Where-Object { $_ -like '*--no-dependencies*' }).Count -eq 2 -and $script:events.Contains('start:Gateway')) 'Gateway start beside Auth builds mods without rebuilding shared dependencies, even with hot reload off'

    Reset-Startup @('Gateway')
    Start-Services $script:snapshot @('Gateway')
    Check ($script:buildArguments.Count -eq 0 -and $script:events.Count -eq 0) 'already-ready Gateway is not rebuilt or restarted'
    Reset-Startup
    Start-Services $script:snapshot @('Auth')
    Check (-not @($script:events | Where-Object { $_ -like 'stage:Building mods*' }).Count) 'Auth-only start does not build mods'

    $oldBinaryHash = (Get-FileHash -LiteralPath (At 'Mods/Binaries/alpha/alpha.dll')).Hash
    Set-Content -LiteralPath (At 'Mods/Source/alpha/Mod.cs') -Value '#error Intentional mod build failure fixture'
    Reset-Startup @('Auth')
    $failure = ''
    try { Start-Services $script:snapshot @('Patch','Gateway') } catch { $failure = $_.Exception.Message }
    Check ($failure.Contains('Build failed for mod alpha') -and $script:stage -eq 'Building mods') 'a compiler failure identifies the mod and failed stage'
    Check (-not @($script:events | Where-Object { $_ -like 'start:*' }).Count -and ($script:snapshot.Services | Where-Object Name -eq 'Auth').Status -eq 'Ready') 'failed mod build blocks every missing service launch and preserves existing services'
    Check ((Get-FileHash -LiteralPath (At 'Mods/Binaries/alpha/alpha.dll')).Hash -eq $oldBinaryHash) 'an old DLL can remain on disk but cannot make failed startup succeed'
    Set-Content -LiteralPath (At 'Mods/Source/alpha/Mod.cs') -Value 'public class Mod : ModApi { public int Fixed => 1; }'
    Reset-Startup
    Start-Services $script:snapshot @('Gateway')
    Check ($script:events.Contains('start:Gateway')) 'correcting a failed mod allows the same startup path to recover'
    Write-Host "$script:passed mod startup checks passed."
} finally {
    Set-Location -LiteralPath $oldLocation.Path
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -match '^Launcher-mods-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
