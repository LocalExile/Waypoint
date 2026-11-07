"""Read native league-banner sprites through an already-built extraction tool. Never writes the client.

Only immutable PNG previews and provenance in Waypoint's cache are produced. No builds, installs or game launches.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import sys
import tempfile


def signature(file):
    info = file.stat()
    return [info.st_size, info.st_mtime_ns]


def wire_selector(source):
    # This verified outside-tool source contract uses one constant selector and the generic family (C1=0).
    # Refuse a new presentation implementation instead of guessing what its per-league selectors mean.
    entry = re.search(r"public\s+LeagueEntry\s+Entry\(\)\s*=>\s*new\(.*?;", source, re.S)
    tail = re.search(r",\s*0\s*,\s*0\s*,\s*0\s*,\s*(0x[0-9a-f]+|\d+)\s*\)\s*;\s*$", entry[0], re.I) if entry else None
    if not tail:
        raise ValueError("The checkout's banner-selection contract is not supported.")
    return int(tail[1], 0)


def explicit_banner(table, selector):
    if len(table) < 12:
        raise ValueError("Incomplete LeagueFlag table.")
    count = struct.unpack_from("<I", table)[0]
    start = 4 + count * 27
    if count > 4096 or table[start:start + 8] != b"\xbb" * 8:
        raise ValueError("Unsupported LeagueFlag table layout.")
    if not 1 <= selector <= count:
        return None
    offset = struct.unpack_from("<Q", table, 4 + (selector - 1) * 27 + 18)[0]
    at = start + offset
    if at < start or at >= len(table) or at % 2 != start % 2:
        raise ValueError("Invalid banner string offset.")
    end = at
    while end + 2 <= len(table) and table[end:end + 2] != b"\0\0":
        end += 2
    if end + 2 > len(table) or end - at > 4096:
        raise ValueError("Unterminated banner key.")
    return table[at:end].decode("utf-16-le") or None


def atlas_sprite(atlas, key):
    for line in atlas.splitlines():
        match = re.fullmatch(r'"([^"]+)"\s+"([^"]+)"\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*', line)
        if match and match[1].casefold() == key.casefold():
            texture = match[2]
            if not re.fullmatch(r"Art/Textures/[A-Za-z0-9 _./-]+\.dds", texture, re.I) or ".." in texture.split("/"):
                raise ValueError("Unsafe atlas texture path.")
            x, y, right, bottom = map(int, match.group(3, 4, 5, 6))
            if not (0 <= x <= right < 16384 and 0 <= y <= bottom < 16384):
                raise ValueError("Invalid sprite rectangle.")
            return texture, (x, y, right + 1, bottom + 1)  # The game's atlas bounds are inclusive.
    return None


def extract(root, cache):
    import PIL
    from PIL import Image
    archive = root / "Client/Content.ggpk"
    source_file = root / "src/GameServer.Core/Accounts/LeagueCatalog.cs"
    source = source_file.read_bytes()
    if len(source) > 1024 * 1024:
        raise ValueError("Banner contract is too large.")
    selector = wire_selector(source.decode("utf-8-sig"))
    before = signature(archive)
    source_hash = hashlib.sha256(source).hexdigest()
    helper_hash = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    key = hashlib.sha256(json.dumps([str(root), before, source_hash, helper_hash, PIL.__version__]).encode()).hexdigest()
    destination = cache / key
    manifest = destination / "manifest.json"
    if manifest.is_file():
        try:
            result = json.loads(manifest.read_text())
            if all((destination / (kind + ".png")).is_file() and hashlib.sha256((destination / (kind + ".png")).read_bytes()).hexdigest()
                   == result["hashes"][kind] for kind in ("normal", "hardcore")):
                return {kind: str(destination / (kind + ".png")) for kind in ("normal", "hardcore")}
        except (ValueError, KeyError, TypeError):
            pass  # Recreate only Waypoint's own damaged preview cache.
    tool = next((root / f"Tools/PoEDataExtractor/bin/{mode}/net10.0/PoEDataExtractor.dll"
                 for mode in ("Debug", "Release") if (root / f"Tools/PoEDataExtractor/bin/{mode}/net10.0/PoEDataExtractor.dll").is_file()), None)
    program = root / "Tools/PoEDataExtractor/Program.cs"
    if tool is None or '"cat" => Cat(args[1], args)' not in program.read_text(encoding="utf-8-sig"):
        raise ValueError("No verified, built read-only asset extractor is available.")
    env = dict(os.environ, POE_OOZ_WORKER=str(root / "Tools/ooz/ooz_worker.mjs"))
    cache.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="staging-", dir=cache) as temporary:
        staging = Path(temporary)
        fetched = {}

        def read(path):
            if path not in fetched:
                output = staging / (str(len(fetched)) + ".asset")
                subprocess.run(["dotnet", str(tool), "cat", str(archive), path, "--out", str(output)],
                               cwd=root, env=env, check=True, capture_output=True, timeout=20,
                               creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
                if output.stat().st_size > 64 * 1024 * 1024:
                    raise ValueError("Asset exceeds the preview limit.")
                fetched[path] = output
            return fetched[path]

        table = read("Data/LeagueFlag.datc64").read_bytes()
        explicit = explicit_banner(table, selector)
        atlas = read("Art/UIImages1.txt").read_text(encoding="utf-16-le")
        provenance = {"archive": before, "contract": source_hash, "selector": selector, "sprites": {}, "hashes": {}}
        for kind, fallback in (("normal", "BannerStandard"), ("hardcore", "BannerHardcore")):
            chosen = explicit if explicit and atlas_sprite(atlas, explicit) else "Art/2DArt/UIImages/Login/" + fallback
            sprite = atlas_sprite(atlas, chosen)
            if sprite is None:
                raise ValueError("The native fallback sprite is missing.")
            texture, bounds = sprite
            with Image.open(read(texture)) as image:
                if image.width * image.height > 32 * 1024 * 1024 or bounds[2] > image.width or bounds[3] > image.height:
                    raise ValueError("Banner rectangle exceeds its texture.")
                image.convert("RGBA").crop(bounds).save(staging / (kind + ".png"))
            provenance["sprites"][kind] = {"key": chosen, "texture": texture, "bounds": bounds}
            provenance["hashes"][kind] = hashlib.sha256((staging / (kind + ".png")).read_bytes()).hexdigest()
        if before != signature(archive) or source_file.read_bytes() != source:
            raise ValueError("Client assets or banner contract changed while reading.")
        destination.mkdir(exist_ok=True)
        for kind in ("normal", "hardcore"):
            os.replace(staging / (kind + ".png"), destination / (kind + ".png"))
        # Publish last; an interrupted preview is never treated as a completed cache.
        staged_manifest = staging / "manifest.json"
        staged_manifest.write_text(json.dumps(provenance, indent=2))
        os.replace(staged_manifest, manifest)
    return {kind: str(destination / (kind + ".png")) for kind in ("normal", "hardcore")}


if __name__ == "__main__":
    try:
        print(json.dumps(extract(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve())))
    except Exception as error:
        print(json.dumps({"error": str(error)}))
        sys.exit(1)
