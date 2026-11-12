# Fetches the DepotDownloader release that Waypoint.exe carries (docs/launcher.md, "The game client from Steam"), once, at
# build time, and holds it to the SHA-256 written here: a user's Waypoint never downloads it. The zip goes to
# tools\bundled\, which git ignores; src\Waypoint\Waypoint.csproj embeds it when it is there. With -Source it also fetches
# the source of that same release, which the release workflow attaches beside Waypoint.exe (DepotDownloader is GPL-2.0:
# THIRD-PARTY-NOTICES.md).
#
#   pwsh tools/Get-DepotDownloader.ps1            the program, for a build
#   pwsh tools/Get-DepotDownloader.ps1 -Source    the program and its source, for a release
#
# A new version of the tool: change $version and both hashes here, and DepotTool's in src\Launcher.Core\SteamDownload.cs.
param([switch]$Source)
$ErrorActionPreference = 'Stop'

$version = '3.4.0'
$tag = "DepotDownloader_$version"
$files = @(
    @{ Name = 'DepotDownloader-windows-x64.zip'; Url = "https://github.com/SteamRE/DepotDownloader/releases/download/$tag/DepotDownloader-windows-x64.zip"
       Sha256 = '41c9e9f0df54b3ad02e67a11726756e5c73283bd7c2e1b04acfa5ae4c2ed3767' }
)
# GitHub makes a tag's source archive when it is asked for, and its bytes are not promised to stay the same: no hash.
if ($Source) { $files += @{ Name = "DepotDownloader-$version-source.zip"; Url = "https://github.com/SteamRE/DepotDownloader/archive/refs/tags/$tag.zip"; Sha256 = $null } }

$folder = Join-Path $PSScriptRoot 'bundled'
New-Item -ItemType Directory -Force $folder | Out-Null
foreach ($file in $files) {
    $path = Join-Path $folder $file.Name
    $have = (Test-Path $path) -and (-not $file.Sha256 -or (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $file.Sha256)
    if (-not $have) {
        Write-Host "fetching $($file.Name) ..."
        $partial = "$path.partial"
        Invoke-WebRequest -Uri $file.Url -OutFile $partial -UseBasicParsing
        if ($file.Sha256) {
            $hash = (Get-FileHash $partial -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($hash -ne $file.Sha256) {
                Remove-Item $partial -Force
                throw "$($file.Name) is not the release this script expects (SHA-256 $hash)"
            }
        }
        Move-Item $partial $path -Force
    }
    Write-Host "$($file.Name) is in $folder"
}
