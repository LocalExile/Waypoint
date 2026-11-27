#!/usr/bin/env bash
# Builds Waypoint-x86_64.AppImage and its .sha256 (docs/launcher.md, "The Linux AppImage"): a self-contained linux-x64
# publish of src/Waypoint.Linux, with DepotDownloader's Linux release embedded (tools/Get-DepotDownloader.ps1 -Linux), the
# desktop entry and icon from packaging/linux and branding/, packed by appimagetool. Every tool it downloads is held to the
# SHA-256 written here, as DepotDownloader is; a file that differs stops the build. The release workflow runs it for a tag;
# CI runs it for every pull request. Never attach a locally built AppImage to a release: the self-update trusts the
# published SHA-256 next to it.
#
#   tools/build-appimage.sh [version] [output folder]      version: 1.2.3 (default 0.0.0-dev, a build from source)
#
# Needs the .NET 10 SDK, PowerShell 7 (for Get-DepotDownloader.ps1), curl and sha256sum. No FUSE: appimagetool runs
# extracted.
set -euo pipefail

version="${1:-0.0.0-dev}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${2:-$root/out/linux}"
bundled="$root/tools/bundled"

# A new appimagetool or runtime: change the release and its hash here.
appimagetool_url='https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage'
appimagetool_sha256='ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0'
runtime_url='https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64'
runtime_sha256='2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d'

# fetch <url> <sha256> <file>: once, held to its hash; a partial or different file is never kept.
fetch() {
    local url="$1" sha="$2" file="$3"
    if [ -f "$file" ] && echo "$sha  $file" | sha256sum --check --status; then return; fi
    echo "fetching $(basename "$file") ..."
    curl --fail --location --silent --show-error --output "$file.partial" "$url"
    if ! echo "$sha  $file.partial" | sha256sum --check --status; then
        rm -f "$file.partial"
        echo "$(basename "$file") is not the release this script expects" >&2
        exit 1
    fi
    mv "$file.partial" "$file"
}

mkdir -p "$bundled" "$out"
fetch "$appimagetool_url" "$appimagetool_sha256" "$bundled/appimagetool-x86_64.AppImage"
fetch "$runtime_url" "$runtime_sha256" "$bundled/runtime-x86_64"
chmod +x "$bundled/appimagetool-x86_64.AppImage"
pwsh -NoProfile -File "$root/tools/Get-DepotDownloader.ps1" -Linux

appdir="$out/Waypoint.AppDir"
rm -rf "$appdir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" "$appdir/usr/share/icons/hicolor/192x192/apps"
dotnet publish "$root/src/Waypoint.Linux" -c Release -r linux-x64 --self-contained true -p:Version="$version" -o "$appdir/usr/bin"
install -m 755 "$root/packaging/linux/AppRun" "$appdir/AppRun"
install -m 644 "$root/packaging/linux/waypoint.desktop" "$appdir/waypoint.desktop"
install -m 644 "$root/packaging/linux/waypoint.desktop" "$appdir/usr/share/applications/waypoint.desktop"
install -m 644 "$root/branding/waypoint-192.png" "$appdir/waypoint.png"
install -m 644 "$root/branding/waypoint-192.png" "$appdir/usr/share/icons/hicolor/192x192/apps/waypoint.png"
ln -sf waypoint.png "$appdir/.DirIcon"

image="$out/Waypoint-x86_64.AppImage"
rm -f "$image"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$bundled/appimagetool-x86_64.AppImage" --no-appstream --runtime-file "$bundled/runtime-x86_64" "$appdir" "$image"
( cd "$out" && sha256sum Waypoint-x86_64.AppImage > Waypoint-x86_64.AppImage.sha256 )
echo "$image"
cat "$image.sha256"
