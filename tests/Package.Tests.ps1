# Verifies the actual portable ZIP, then extracts it to a NEW external test folder.
param([Parameter(Mandatory=$true)][string]$ZipPath, [Parameter(Mandatory=$true)][string]$ExtractTo)
$ErrorActionPreference = 'Stop'
$ZipPath = (Resolve-Path -LiteralPath $ZipPath).Path
$ExtractTo = [IO.Path]::GetFullPath($ExtractTo)
if (Test-Path -LiteralPath $ExtractTo) { throw 'Choose a new extraction folder. Existing files will not be overwritten.' }
$expectedHash = ((Get-Content -LiteralPath ($ZipPath + '.sha256') -Raw).Trim() -split '\s+')[0]
if ((Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash -ine $expectedHash) { throw 'ZIP checksum mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    foreach ($entry in $archive.Entries) {
        $target = [IO.Path]::GetFullPath((Join-Path $ExtractTo $entry.FullName))
        if (-not $target.StartsWith($ExtractTo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP entry escapes the extraction folder.' }
    }
} finally { $archive.Dispose() }
Expand-Archive -LiteralPath $ZipPath -DestinationPath $ExtractTo
$manifests = @(Get-ChildItem -LiteralPath $ExtractTo -Filter release-manifest.json -Recurse -File)
if ($manifests.Count -ne 1) { throw 'Expected exactly one release manifest.' }
$package = $manifests[0].Directory.FullName
$manifest = Get-Content -LiteralPath $manifests[0].FullName -Raw | ConvertFrom-Json
if ($manifest.Configuration -ne 'Release' -or -not $manifest.SelfContained -or $manifest.Runtime -ne 'win-x64' -or $manifest.ModifiedSource) {
    throw 'Expected a clean-source, self-contained Windows x64 Release package.'
}
foreach ($record in $manifest.Files) {
    $file = [IO.Path]::GetFullPath((Join-Path $package $record.Path))
    if (-not $file.StartsWith($package + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escapes the package.' }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -ne $record.Bytes) { throw "Missing/changed package file: $($record.Path)" }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $record.Sha256) { throw "Package hash mismatch: $($record.Path)" }
}
$actual = @(Get-ChildItem -LiteralPath $package -Recurse -File)
if ($actual.Count -ne @($manifest.Files).Count + 1) { throw 'Unexpected files were extracted beyond the manifest and its listed files.' }
if (@($actual | Where-Object { $_.Extension -in @('.pdb','.db','.ggpk','.pfx') -or $_.Name -match '(?i)login-keys|credentials|test-account' }).Count) {
    throw 'Development/private game data must not be included in the portable package.'
}
$brand = Get-Content -LiteralPath (Join-Path $package 'brand.json') -Raw | ConvertFrom-Json
$satellite = Join-Path $package 'ru/Launcher.Core.resources.dll'
if (-not (Test-Path -LiteralPath $satellite -PathType Leaf) -or
    -not @($manifest.Files | Where-Object Path -eq 'ru/Launcher.Core.resources.dll').Count) {
    throw 'The Russian interface satellite must be included and covered by the manifest.'
}
$satelliteIdentity = [Reflection.AssemblyName]::GetAssemblyName($satellite)
if ($satelliteIdentity.Name -ne 'Launcher.Core.resources' -or $satelliteIdentity.CultureName -ne 'ru') {
    throw 'Expected the Russian Launcher.Core satellite assembly.'
}
if (-not (Test-Path -LiteralPath (Join-Path $package 'LICENSE.txt')) -or
    -not (Get-Content -LiteralPath (Join-Path $package 'LICENSE.txt') -Raw).StartsWith('MIT License') -or
    -not (Test-Path -LiteralPath (Join-Path $package 'RELEASE-NOTES.md'))) { throw 'Application license or release notes are missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $package 'licenses/Microsoft.NETCore.App-LICENSE.TXT')) -or
    -not (Test-Path -LiteralPath (Join-Path $package 'licenses/Microsoft.WindowsDesktop.App-LICENSE.txt'))) { throw 'Bundled runtime license files are missing.' }
if ($brand.displayName -ne $manifest.Product -or ($brand.executableName + '.exe') -ne $manifest.Executable -or $brand.version -ne $manifest.Version) { throw 'Branding and executable metadata do not agree.' }
$appDll = Join-Path $package ($brand.executableName + '.dll')
$identity = [Diagnostics.FileVersionInfo]::GetVersionInfo($appDll).ProductVersion
if (-not $identity.StartsWith($manifest.Version + '+') -or -not $identity.Contains($manifest.SourceCommit)) {
    throw 'The application assembly must identify the manifest version and source commit.'
}
if ($satelliteIdentity.Version -ne [Reflection.AssemblyName]::GetAssemblyName((Join-Path $package 'Launcher.Core.dll')).Version) {
    throw 'Russian resources must match the packaged Core assembly version.'
}
$exe = Join-Path $package $manifest.Executable
$stream = [IO.File]::OpenRead($exe)
try {
    $reader = [IO.BinaryReader]::new($stream)
    if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'Missing Windows executable header.' }
    $stream.Position = 0x3C; $offset = $reader.ReadInt32(); $stream.Position = $offset
    if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'Expected a Windows x64 executable.' }
} finally { $stream.Dispose() }
Write-Host "PASS: ZIP checksum; all $(@($manifest.Files).Count) payload hashes/sizes; Russian satellite; no extra/private game files; matching branding; Windows x64 EXE."
Write-Host "Extracted package: $package"
Write-Host "Source commit: $($manifest.SourceCommit)"
