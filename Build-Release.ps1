# Builds local portable artifacts only. Does not publish/upload anything to a remote service.
param([string]$OutputDirectory = '', [switch]$AllowDirty)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 10 SDK before building Waypoint.' }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Install Git before building a release so its source commit can be recorded.' }
$brand = Get-Content -LiteralPath (Join-Path $root 'branding/brand.json') -Raw | ConvertFrom-Json
$name = [string]$brand.executableName
$version = [string]$brand.version
if ($name -notmatch '^[A-Za-z][A-Za-z0-9_-]{0,63}$') { throw 'brand.json executableName must be a simple filename without an extension.' }
if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.-]+)?$') { throw 'brand.json version must be a three-part version with an optional prerelease suffix.' }

$oldGitEnvironment = @{}
foreach ($entry in @(Get-ChildItem Env: | Where-Object Name -like 'GIT_*')) {
    $oldGitEnvironment[$entry.Name] = $entry.Value
    [Environment]::SetEnvironmentVariable($entry.Name, $null)
}
try {
    $env:GIT_OPTIONAL_LOCKS = '0'
    $env:GIT_TERMINAL_PROMPT = '0'
    $commit = [string](& git -c core.fsmonitor=false -C $root rev-parse --verify HEAD)
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-f0-9]{40}([a-f0-9]{24})?$') { throw 'Cannot identify the release source commit.' }
    $changes = @(& git -c core.fsmonitor=false -C $root status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot check release source status.' }
    $dirty = $changes.Count -gt 0
    if ($dirty -and -not $AllowDirty) { throw 'Commit local changes before building a release, or use -AllowDirty for a development package explicitly marked as modified.' }
    $sdk = [string](& dotnet --version)
    if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^10\.') { throw 'Building this release requires the .NET 10 SDK.' }
    if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root '.local/releases' }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    $artifactName = "$name-$version-win-x64"
    $buildId = $commit.Substring(0,7) + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
    $buildDirectory = Join-Path $OutputDirectory $buildId
    $package = Join-Path $buildDirectory $name
    if (Test-Path -LiteralPath $buildDirectory) { throw 'The release build directory already exists. No files were overwritten.' }
    New-Item -ItemType Directory -Path $package -Force | Out-Null
    $project = Join-Path $root 'src/Launcher.Desktop/Launcher.Desktop.csproj'
    $arguments = @('publish', $project, '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true',
        '--output', $package, '--nologo', '-v', 'minimal', "-p:LauncherExecutableName=$name", "-p:Version=$version",
        '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false')
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed. No ZIP was created.' }
    foreach ($file in @("$name.exe", "$name.dll", "$name.runtimeconfig.json", "$name.deps.json", 'Launcher.Core.dll',
        'brand.json', 'ru/Launcher.Core.resources.dll', 'licenses/OpenDyslexic-OFL.txt', 'RuntimeBridge.ps1', 'ModBridge.ps1', 'ConsoleSignal.ps1', 'SaveBackup.py', 'hostfxr.dll', 'coreclr.dll', 'PresentationFramework.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $package $file) -PathType Leaf)) { throw "Release is missing required file: $file" }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $package "$name.runtimeconfig.json") -Raw | ConvertFrom-Json
    if (-not @($runtime.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App').Count) {
        throw 'Expected the self-contained Windows Desktop runtime in the release.'
    }
    # Runtime binaries are redistributable dependencies; preserve their supplied license/notice files.
    $assets = Get-Content -LiteralPath (Join-Path $root 'src/Launcher.Desktop/obj/project.assets.json') -Raw | ConvertFrom-Json
    $licenses = Join-Path $package 'licenses'
    New-Item -ItemType Directory -Path $licenses | Out-Null
    foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
        $id = switch ($framework.name) {
            'Microsoft.NETCore.App' { 'microsoft.netcore.app.runtime.win-x64' }
            'Microsoft.WindowsDesktop.App' { 'microsoft.windowsdesktop.app.runtime.win-x64' }
            default { throw "Unexpected runtime framework: $($framework.name)" }
        }
        $pack = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path (Join-Path $folder $id) $framework.version
            if (Test-Path -LiteralPath $candidate -PathType Container) { $pack = $candidate; break }
        }
        if (-not $pack) { throw "Cannot locate bundled runtime license files for $($framework.name)." }
        $notices = @(Get-ChildItem -LiteralPath $pack -File | Where-Object Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$')
        if (-not @($notices | Where-Object Name -match '^LICENSE').Count) { throw "Runtime license is missing for $($framework.name)." }
        foreach ($notice in $notices) {
            Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenses ($framework.name + '-' + $notice.Name + $(if (-not $notice.Extension) { '.txt' })))
        }
    }
    $readme = Get-Content -LiteralPath (Join-Path $root 'packaging/PORTABLE-README.txt') -Raw
    $readme = $readme.Replace('{{PRODUCT}}', [string]$brand.displayName).Replace('{{SUBTITLE}}', [string]$brand.subtitle).Replace('{{EXECUTABLE}}', ($name + '.exe'))
    $readme | Set-Content -LiteralPath (Join-Path $package 'README.txt') -Encoding UTF8
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $package 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $root "docs/releases/$version.md") -Destination (Join-Path $package 'RELEASE-NOTES.md')
    $files = @(Get-ChildItem -LiteralPath $package -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ Path=$_.FullName.Substring($package.Length + 1).Replace('\','/'); Bytes=$_.Length;
            Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    [ordered]@{ Product=$brand.displayName; Version=$version; Executable="$name.exe"; Runtime='win-x64';
        Configuration='Release'; SelfContained=$true; SourceCommit=$commit; ModifiedSource=$dirty;
        SdkVersion=$sdk; BundledFrameworks=@($runtime.runtimeOptions.includedFrameworks); BuiltUtc=[datetime]::UtcNow.ToString('o'); Files=$files } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $package 'release-manifest.json') -Encoding UTF8
    $zip = Join-Path $buildDirectory ($artifactName + '.zip')
    Compress-Archive -LiteralPath $package -DestinationPath $zip -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    ($hash + '  ' + [IO.Path]::GetFileName($zip)) | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ASCII
    Write-Host "Portable ZIP: $zip"
    Write-Host "SHA-256: $hash"
    Write-Host 'Local build only. Nothing was uploaded or published.'
} finally {
    foreach ($entry in @(Get-ChildItem Env: | Where-Object Name -like 'GIT_*')) { [Environment]::SetEnvironmentVariable($entry.Name, $null) }
    foreach ($key in $oldGitEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $oldGitEnvironment[$key]) }
}
