"""Validate public league configuration; never build or run LocalExile. RuntimeBridge owns the writer lock and backups."""
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import sys
import uuid

# A release runs this with its own Python, the embeddable one, which does not look for modules beside the script it
# runs: without this line the import below fails there, and every league change with it.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from AccountData import AccountError, inspect_schema, is_relational, open_saves, physical, record, require


EMPTY_SOURCE = "public sealed class WaypointLeague : League { }\n"


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate configuration key.")
        result[key] = value
    return result


def decode(raw):
    """Text as .NET's File.ReadAllText reads it (GameSystems, LocalExile): the encoding its byte order mark names, else
    UTF-8, a byte that is no UTF-8 read as a replacement character."""
    for mark, codec in ((b"\x00\x00\xfe\xff", "utf-32-be"), (b"\xff\xfe\x00\x00", "utf-32-le"), (b"\xef\xbb\xbf", "utf-8"),
                        (b"\xfe\xff", "utf-16-be"), (b"\xff\xfe", "utf-16-le")):
        if raw.startswith(mark):
            return raw[len(mark):].decode(codec, errors="replace")
    return raw.decode("utf-8", errors="replace")


def read_json(path):
    path = physical(path)
    require(path.is_file() and path.stat().st_size <= 1024 * 1024, "Configuration is missing or too large.")
    text = decode(path.read_bytes())
    # JSONC: preserve quoted strings while removing comments and trailing commas.
    tokens = re.findall(r'"(?:\\.|[^"\\])*"|//[^\r\n]*|/\*[\s\S]*?\*/|[^"/]+|/', text)
    text = "".join(" " if token.startswith(("//", "/*")) else token for token in tokens)
    # Match an entire quoted string before considering a comma outside it.
    text = re.sub(r'"(?:\\.|[^"\\])*"|,(?=\s*[}\]])', lambda m: m[0] if m[0].startswith('"') else "", text)
    value = json.loads(text, object_pairs_hook=unique_object)
    require(isinstance(value, dict), "Configuration must be a JSON object.")
    return value


def digest(path):
    path = physical(path)
    if not path.exists():
        return "missing"
    require(path.is_file() and path.stat().st_size <= 1024 * 1024, "Configuration is too large.")
    return hashlib.sha256(path.read_bytes()).hexdigest()


def manifests(folder, filename):
    folder = physical(folder)
    if not folder.exists():
        return
    pending = [(folder, 0)]
    count = 0
    while pending:
        at, depth = pending.pop()
        physical(at)
        count += 1
        require(count <= 5000 and depth <= 16, "Content folders are too large or deeply nested.")
        file = at / filename
        if file.is_file():
            physical(file)
            yield file
            continue
        for child in at.iterdir():
            if child.is_dir() and child.name not in ("bin", "obj", ".git", "Source", "Binaries"):
                pending.append((child, depth + 1))


def mod_folders(mods):
    """The folders of the installed mods that hold a mod.json, by name: at most 512, as GameSystems reads them."""
    if mods is None or not physical(mods).is_dir():
        return []
    folders = sorted((f for f in mods.iterdir() if f.is_dir()), key=lambda f: f.name)[:512]
    return [f for f in folders if re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}", f.name) and (f / "mod.json").is_file() and is_physical(f)
            and is_physical(f / "mod.json") and (f / "mod.json").stat().st_size <= 1024 * 1024]


def is_physical(path):
    try:
        physical(path)
        return True
    except AccountError:
        return False


def on_mods(mods):
    """The mods that are on, as LocalExile reads them (its ModFolders.On) and as GameSystems.OnMods does: a readable
    mod.json, not turned off by it or by mod-state.txt. Each is (id, folder, mod.json with lower-case keys)."""
    folders = mod_folders(mods)
    choices = {}
    state = mods / "mod-state.txt" if folders else None
    if state is not None and physical(state).is_file():
        require(state.stat().st_size <= 128 * 1024, "Mod choices are too large.")
        for line in decode(state.read_bytes()).split("\n"):
            words = line.rstrip("\r").split(" ")
            words = [w for w in words if w]
            if len(words) == 2 and words[1] in ("on", "off") and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}", words[0]):
                choices[words[0]] = words[1] == "on"
    result = []
    for folder in folders:
        # A key in two cases: the first, as GameSystems and LocalExile read it.
        data = {}
        try:
            for key, value in read_json(folder / "mod.json").items():
                data.setdefault(key.casefold(), value)
        except (AccountError, ValueError, OSError):
            continue  # No readable mod.json: no mod.
        if choices.get(folder.name, data.get("enabled") is not False):
            result.append((folder.name, folder, data))
    return result


def review(modules, leagues, choices, mods=None):
    result = {"choices": digest(choices)}
    for file in manifests(modules, "module.json"):
        result["module/" + file.relative_to(modules).as_posix()] = digest(file)

    def add_leagues(root, mod=""):
        prefix = mod + ":" if mod else ""
        for manifest in manifests(root, "league.json"):
            folder = manifest.parent
            for name in ("league.json", "modules.json", "settings.json", ".waypoint-league.json"):
                file = folder / name
                if file.exists():
                    result["league/" + prefix + file.relative_to(root).as_posix()] = digest(file)
            source = physical(folder / "Source")
            if source.is_dir():
                for file in source.iterdir():
                    if file.is_dir():
                        result["nested/" + prefix + folder.name] = "unsupported"
                        continue
                    physical(file)
                    if file.is_file():
                        result["league/" + prefix + file.relative_to(root).as_posix()] = digest(file)
            require(len(result) <= 10000, "Content inventory is too large.")

    add_leagues(leagues)
    # Every mod's manifest, the choices that turn mods on and off, and the leagues the mods bring: which mods are on, and
    # what they are, follows from these. GameSystems.CaptureReview takes the same inventory.
    if mods is not None and physical(mods).is_dir():
        result["mods"] = digest(mods / "mod-state.txt")
        for folder in mod_folders(mods):
            result["mod/" + folder.name + "/mod.json"] = digest(folder / "mod.json")
            try:
                add_leagues(folder / "Leagues", folder.name)
            except AccountError:
                # A redirected or too deep Leagues/ is not read: not inventoried either.
                for key in [k for k in result if k.startswith(("league/" + folder.name + ":", "nested/" + folder.name + ":"))]:
                    del result[key]
    return result


def paths(value):
    require(isinstance(value, list) and all(isinstance(p, str) for p in value), "Invalid module list.")
    result = []
    for path in value:
        path = path.replace("\\", "/")
        require(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*(?:/[A-Za-z0-9][A-Za-z0-9_.-]*)*", path)
                and all(p not in (".", "..") for p in path.split("/")), "Invalid module path.")
        result.append(path)
    require(len({p.rsplit("/", 1)[-1].casefold() for p in result}) == len(result), "A module is listed twice.")
    return result


def canon(path, systems):
    """An entry names a mod by its id in any case, also as the last part of the path it had as a module."""
    system = systems.get(path.rsplit("/", 1)[-1].casefold())
    return system["path"] if system and system.get("mod") else path


def mod_list(value):
    """A league.json "mods", or a mod league's modules.json list, as GameSystems reads it: null is none."""
    if value is None:
        return []
    require(isinstance(value, list) and all(isinstance(p, str) for p in value), "Invalid module list.")
    result = [p.replace("\\", "/") for p in value]
    require(all(":" not in p and all(part not in ("", ".", "..") for part in p.split("/")) for p in result), "Invalid module path.")
    return result


def valid_league_folder(name):
    return 0 < len(name) <= 128 and name not in (".", "..") and not any(c in '/\\:*?"<>|' or ord(c) < 32 or 127 <= ord(c) < 160 for c in name)


def limits(data):
    """A manifest's "conflicts" and "only", as LocalExile's ModFolders.Limits reads them: these keys exactly, lists of ids,
    or no limits from it at all when one of them is something else (LocalExile logs that manifest)."""
    def ids(name):
        value = data.get(name)
        return value if isinstance(value, list) and all(isinstance(v, str) for v in value) else None
    conflicts, only = ids("conflicts"), ids("only")
    if any(name in data and ids(name) is None for name in ("conflicts", "only")) or (conflicts is None and only is None):
        return {}
    return {"conflicts": conflicts or [], "only": only}


def catalogue(modules, leagues, mods=None):
    systems = {}
    for file in manifests(modules, "module.json"):
        data = read_json(file)
        identifier = data.get("id", file.parent.name)
        require(isinstance(identifier, str) and identifier == file.parent.name and identifier.casefold() not in systems, "Invalid or duplicate module ID.")
        systems[identifier.casefold()] = {"path": file.parent.relative_to(modules).as_posix(), "depends": data.get("depends", []), **limits(data)}
        require(isinstance(systems[identifier.casefold()]["depends"], list)
                and all(isinstance(d, str) for d in systems[identifier.casefold()]["depends"]), "Invalid module dependencies.")
    # A mod of the leagues that list it is listed by its id. One with a module's id does not load (the module runs), nor
    # does one named exactly as a league's folder, nor one whose depends is not a list of ids (LocalExile's ModHost).
    active = on_mods(mods)
    roots = [(leagues, "")] + [(folder / "Leagues", identifier) for identifier, folder, _ in active]
    folder_names = {child.name for root, _ in roots if root.is_dir() for child in root.iterdir() if child.is_dir()}
    for identifier, folder, data in active:
        if data.get("leagues") == "listed" and identifier.casefold() not in systems and identifier not in folder_names:
            # A present "depends" that is no list: LocalExile's ModHost does not load the mod.
            depends = data.get("depends", [])
            if isinstance(depends, list) and all(isinstance(d, str) for d in depends):
                systems[identifier.casefold()] = {"path": identifier, "depends": depends, "mod": True}
    profiles = []
    names = set()

    def profile_of(file, mod):
        data = {key: value for key, value in read_json(file).items() if value is not None}
        name = data.get("name", file.parent.name)
        # A mod's league of a name that is taken is not loaded: the first of a name stays.
        if mod and isinstance(name, str) and name.casefold() in names:
            return None
        require(isinstance(name, str) and name.casefold() not in names and (not mod or valid_league_folder(file.parent.name)), "Invalid or duplicate league.")
        for key in ("ssf", "hardcore", "ended", "creatable"):
            require(key not in data or isinstance(data[key], bool), "Invalid league flag.")
        for key in ("base", "form", "dataSet", "parent", "entryOf", "entryKind", "description"):
            require(data.get(key) is None or isinstance(data[key], str), "Invalid league field.")
        # What it is made of: its modules, then the mods its league.json names (ids, as LocalExile reads them).
        listing = read_json(file.parent / "modules.json")
        require("modules" not in listing or isinstance(listing["modules"], list), "Invalid module list.")
        listed = [canon(p, systems) for p in (mod_list(listing.get("modules")) if mod else paths(listing.get("modules", [])))]
        own = mod_list(data.get("mods"))
        require(all("/" not in m and "\\" not in m for m in own), "Invalid module list.")
        ids = {p.rsplit("/", 1)[-1].casefold() for p in listed}
        listed += [canon(p, systems) for p in own if p.rsplit("/", 1)[-1].casefold() not in ids]
        names.add(name.casefold())
        return {"data": data, "name": name, "folder": file.parent, "base": data.get("base") or name,
                "mode": (data.get("hardcore", False), data.get("ssf", False), data.get("form") or "", data.get("dataSet") or ""),
                "modules": listed, "mod": mod, "own": own,
                "custom": (file.parent / ".waypoint-league.json").exists(),
                "template": read_json(file.parent / ".waypoint-league.json").get("template", "") if (file.parent / ".waypoint-league.json").exists() else ""}

    # The checkout's leagues as before. A mod's as LocalExile reads them: the folders directly in its Leagues/, with a
    # league.json and Source/ or Binaries/; one that cannot be read is not there, as for LocalExile, which loads the others.
    for file in manifests(leagues, "league.json"):
        profiles.append(profile_of(file, ""))
    for root, mod in roots[1:]:
        if not root.is_dir():
            continue
        for folder in sorted((d for d in root.iterdir() if d.is_dir()), key=lambda d: d.name):
            if not (folder / "league.json").is_file() or not ((folder / "Source").is_dir() or (folder / "Binaries").is_dir()):
                continue
            try:
                profile = profile_of(folder / "league.json", mod)
            except (AccountError, ValueError, OSError, TypeError, AttributeError):
                continue
            if profile is not None:
                profiles.append(profile)
    for profile in profiles:
        at, seen = profile, set()
        while at and at["name"].casefold() not in seen:
            seen.add(at["name"].casefold())
            parent = at["data"].get("parent")
            if not parent:
                profile["rooted"] = at["name"].casefold() in ("standard", "void")
                break
            at = next((p for p in profiles if p["name"].casefold() == parent.casefold()), None)
        else:
            profile["rooted"] = False
    return systems, profiles


def playable(profile):
    return profile["rooted"] and not profile["data"].get("ended", False) and profile["name"].casefold() != "void" \
        and any(p.rsplit("/", 1)[-1].casefold() == "game" for p in profile["modules"])


def family_name(profile, profiles):
    if profile["custom"]:
        template = next((p for p in profiles if not p["custom"] and p["name"] == profile["template"]), None)
        return family_name(template, profiles) if template else profile["name"]
    at, seen = profile, set()
    while at["name"].casefold() not in seen:
        seen.add(at["name"].casefold())
        parent = at["data"].get("entryOf")
        if not parent and at["data"].get("hardcore", False) and at["data"].get("parent") != "Void":
            parent = at["data"].get("parent")
        next_profile = next((p for p in profiles if not p["custom"] and p["name"].casefold() == (parent or "").casefold()), None)
        if next_profile is None or next_profile["base"].casefold() != at["base"].casefold():
            return at["name"]
        at = next_profile
    return profile["name"]


def isolated(profile, profiles):
    """A league whose characters never reach Standard: its parents lead to Void. Survivors is one, and every league made
    in Waypoint."""
    at, seen = profile, set()
    while at and at["name"].casefold() not in seen:
        seen.add(at["name"].casefold())
        parent = at["data"].get("parent")
        if not parent:
            return at["name"].casefold() == "void"
        at = next((p for p in profiles if p["name"].casefold() == parent.casefold()), None)
    return False


def mod_limits(mods, systems):
    """Every mod that is on, by id, with what its mod.json declares (limits): as LocalExile reads them, of each mod that is on
    but one with a module's id, whether it is a mod of the leagues that list it or of every league."""
    result = {}
    for identifier, folder, _ in on_mods(mods):
        if identifier.casefold() not in systems or systems[identifier.casefold()].get("mod"):
            try:
                result[identifier.casefold()] = limits(read_json(folder / "mod.json"))
            except (AccountError, ValueError, OSError):
                result[identifier.casefold()] = {}
    return result


def validate_modules(profile, selected, systems, profiles, new=False, listed=(), on=None):
    """Whether these modules can run together for the league, by what LocalExile itself asks: the game, each module's own
    dependencies, and nothing made for an isolated league in one that leads to Standard. A module has no knowledge of
    leagues (LocalExile's modules.md), so any other module goes with any league, but the rules of a game mode, which go
    with that mode's leagues and with a league made in Waypoint on a base that has the mode. A league's own bundle (Ruthless in a
    Ruthless league) can be left out too: LocalExile loads the league without it and keeps its saved data, and the
    interface asks its user first. `new`: the modules are for a league made from this one, which is isolated whatever
    its template is. `listed`: what the league lists now, which may name a module that is not installed. `on`: the mods that
    are on, by id, with their limits (mod_limits)."""
    require(playable(profile), "Ended or non-playable league definitions are read-only.")
    on = on or {}

    def installed(path):
        identifier = path.rsplit("/", 1)[-1].casefold()
        return identifier in systems and systems[identifier]["path"].casefold() == path.casefold()

    # A listed module that is not installed: LocalExile logs it and loads the league without it. One the league already
    # lists stays until its user unticks it; one that is new here is refused.
    known = {p.casefold() for p in listed}
    # A mod of every league that is on (`on`) is not missing either: LocalExile runs it.
    require(all(installed(p) or p.casefold() in known or p.rsplit("/", 1)[-1].casefold() in on for p in selected), "A selected module is missing.")
    # What LocalExile asks of the whole list (its ModFolders.Clash): nothing beside one it conflicts with, and beside one
    # that names the only ones it allows ("only"), nothing else but the game.
    ids = [p.rsplit("/", 1)[-1] for p in selected]
    for identifier in ids:
        module = identifier.casefold() in systems and not systems[identifier.casefold()].get("mod")
        own = systems[identifier.casefold()] if module else on.get(identifier.casefold(), {})
        other = next((c for c in own.get("conflicts", []) if c.casefold() in {i.casefold() for i in ids}), None)
        require(other is None, f"{identifier} cannot run with {other}.")
        allowed = None if own.get("only") is None else {o.casefold() for o in own["only"]} | {identifier.casefold(), "game"}
        extra = next((i for i in ids if allowed is not None and i.casefold() not in allowed), None)
        require(extra is None, f"{identifier} allows only the modules and mods it names, not {extra}.")
    active = {path.rsplit("/", 1)[-1].casefold(): path for path in selected if installed(path)}
    require("game" in active, "The Game module is required.")
    # A module or mod LocalExile ships only in isolated leagues (Survivors': a run starts by itself, refuses ordinary exits
    # and hands out rewards) stays in isolated leagues.
    if not new and not isolated(profile, profiles):
        for path in active.values():
            users = [p for p in profiles if not p["custom"] and path.casefold() in {m.casefold() for m in p["modules"]}]
            require(not users or not all(isolated(p, profiles) for p in users),
                    path.rsplit("/", 1)[-1] + " is made for an isolated league and cannot be turned on in a league that leads to Standard.")
    # A module of a game mode: every shipped league that lists it has that form (Ruthless and "ruthless"). In a league of
    # that mode it is the league's own. In another it brings the mode's rules alone, which is for a league made in
    # Waypoint, on a game base LocalExile ships that mode for. One the league already lists (a hand edit) stays until its
    # user unticks it, so that another change still saves.
    own = (profile["data"].get("form") or "").casefold()
    for path in active.values():
        if not new and path.casefold() in known:
            continue
        forms = {(p["data"].get("form") or "").casefold() for p in profiles
                 if not p["custom"] and path.casefold() in {m.casefold() for m in p["modules"]}}
        if len(forms) != 1 or "" in forms or forms == {own}:
            continue
        form = next(iter(forms))
        require((new or profile["custom"]) and any(not p["custom"] and (p["data"].get("form") or "").casefold() == form
                                                   and p["base"].casefold() == profile["base"].casefold() for p in profiles),
                path.rsplit("/", 1)[-1] + " holds the rules of a game mode: it can be turned on for a league made in Waypoint, on a game base that has that mode.")
    visiting, done = set(), set()

    def visit(identifier):
        if identifier in done:
            return
        require(identifier not in visiting, "The selected modules contain a dependency cycle.")
        visiting.add(identifier)
        for dependency in systems[identifier]["depends"]:
            # A mod of every league that is on (`on`): LocalExile loads it in every league.
            if systems[identifier].get("mod") and dependency.casefold() in on and dependency.casefold() not in systems:
                continue
            require(dependency.casefold() in active, identifier + " needs " + dependency + ".")
            visit(dependency.casefold())
        visiting.remove(identifier)
        done.add(identifier)

    for identifier in active:
        visit(identifier)


def unused_saved_name(database, name, auth=None):
    database = physical(database)
    if not database.exists():
        return
    with closing(open_saves(database, auth)) as db:
        db.execute("BEGIN")
        require(inspect_schema(db), "The save layout or pending import cannot be safely inspected. League creation is blocked.")
        if is_relational(db):
            for row in db.execute("SELECT league, hardcore_death_league FROM account_characters"):
                for value in row:
                    if value is not None:
                        require(isinstance(value, str) and value.casefold() != name.casefold(),
                                "That league name has retained characters. Choose a new name.")
            for row in db.execute("SELECT account FROM stash_saves"):
                require(isinstance(row[0], str) and not row[0].casefold().endswith(" (" + name.casefold() + ")"),
                        "That league name has retained stash data. Choose a new name.")
        else:
            for table, column in (("account_characters", "character"), ("characters", "save"), ("deleted_characters", "save")):
                for row in db.execute('SELECT "' + column + '" FROM "' + table + '"'):
                    values = [value for key, value in record(row[0]).items() if key.casefold() == "league"]
                    require(all(isinstance(value, str) and value.casefold() != name.casefold() for value in values), "That league name has retained characters. Choose a new name.")
            for table in ("stashes", "stash_tabs"):
                for row in db.execute('SELECT account FROM "' + table + '"'):
                    require(isinstance(row[0], str) and not row[0].casefold().endswith(" (" + name.casefold() + ")"),
                            "That league name has retained stash data. Choose a new name.")
        db.execute("COMMIT")


def empty_template(folder):
    source = physical(folder / "Source")
    if not source.is_dir():
        return False
    files = list(source.iterdir())
    if len(files) != 1 or files[0].suffix != ".cs" or not physical(files[0]).is_file() or files[0].stat().st_size > 16384:
        return False
    text = re.sub(r"/\*[\s\S]*?\*/|//[^\r\n]*", "", decode(files[0].read_bytes()))
    return re.fullmatch(r"\s*public\s+sealed\s+class\s+[A-Za-z_][A-Za-z0-9_]*\s*:\s*League\s*\{\s*\}\s*", text) is not None


def write_exclusive(path, text, created=None):
    physical(path)
    with path.open("x", encoding="utf-8", newline="\n") as output:
        if created is not None:
            created.append(path)
        output.write(text)
        output.flush()
        os.fsync(output.fileno())


def visibility_plan(profiles, name, visible):
    by_name = {p["name"].casefold(): p for p in profiles}
    target = by_name.get(name.casefold()) if isinstance(name, str) else None
    require(target is not None and not target["data"].get("ended", False) and target["rooted"], "This league cannot change creation visibility.")
    changes, seen = [], set()

    def show(profile):
        key = profile["name"].casefold()
        require(key not in seen, "The league card links contain a cycle.")
        seen.add(key)
        parent = profile["data"].get("entryOf")
        if parent:
            require(parent.casefold() in by_name and profile["data"].get("entryKind") in ("ssf", "hardcore", "form"), "A mode option has no valid parent card.")
            show(by_name[parent.casefold()])
        require(profile["rooted"] and not profile["data"].get("ended", False) and profile["name"].casefold() != "void", "An ended or unavailable card cannot be shown.")
        if not profile["data"].get("creatable", True):
            changes.append(profile)

    def hide(profile):
        key = profile["name"].casefold()
        require(key not in seen, "The league card links contain a cycle.")
        seen.add(key)
        for child in profiles:
            if (child["data"].get("entryOf") or "").casefold() == key:
                hide(child)
        if profile["data"].get("creatable", True):
            changes.append(profile)

    (show if visible else hide)(target)
    # A mod's leagues are the mod's files, which an update of the mod replaces.
    require(not target["mod"] and not any(p["mod"] for p in changes),
            "A league that a mod brings cannot be hidden or shown here: an update of the mod would undo it.")
    changed = {p["name"].casefold() for p in changes}
    available = {p["name"].casefold(): (visible if p["name"].casefold() in changed else p["data"].get("creatable", True))
                 and not p["data"].get("ended", False) and p["rooted"] for p in profiles}
    require(any(available[p["name"].casefold()] and not p["data"].get("entryOf")
                and any(m.rsplit("/", 1)[-1].casefold() == "game" for m in p["modules"]) for p in profiles),
            "Keep at least one playable league card available for new characters.")
    for profile in profiles:
        if available[profile["name"].casefold()] and profile["data"].get("entryOf"):
            require(available.get(profile["data"]["entryOf"].casefold(), False), "A visible mode option needs its parent card.")
    return changes


def visibility_text(text, visible):
    # Edit one top-level flag without dropping comments, formatting, or other fields.
    tokens = [m for m in re.finditer(r'"(?:\\.|[^"\\])*"|//[^\r\n]*|/\*[\s\S]*?\*/|[{}\[\]:,]|[^\s\ufeff{}\[\]:,]+', text)
              if not m[0].startswith(("//", "/*"))]
    depth = 0
    for index, token in enumerate(tokens):
        if depth == 1 and token[0].startswith('"') and json.loads(token[0]) == 'creatable' and tokens[index + 1][0] == ":":
            value = tokens[index + 2]
            require(value[0] in ("true", "false"), "The visibility flag is not a boolean.")
            return text[:value.start()] + str(visible).lower() + text[value.end():]
        if token[0] in ("{", "["):
            depth += 1
        elif token[0] in ("}", "]"):
            depth -= 1
    closing = tokens[-1]
    require(closing[0] == "}", "Invalid league definition.")
    comma = "" if tokens[-2][0] in ("{", ",") else ","
    return text[:closing.start()] + comma + '\n  "creatable": ' + str(visible).lower() + "\n" + text[closing.start():]


def apply_visibility(changes, visible, expected, modules, leagues, choices, history, mods=None):
    if not changes:
        return
    backup = physical(history / uuid.uuid4().hex)
    backup.mkdir(parents=True)
    prepared, applied = [], []
    try:
        for profile in changes:
            file = physical(profile["folder"] / "league.json")
            old = file.read_bytes().decode("utf-8")
            new = visibility_text(old, visible)
            destination = backup / profile["folder"].name
            destination.mkdir()
            (destination / "league.json").write_bytes(file.read_bytes())
            temporary = file.parent / ("waypoint-visibility-" + uuid.uuid4().hex + ".tmp")
            write_exclusive(temporary, new)
            prepared.append((file, temporary, old, new))
        evolving = dict(expected)
        for file, temporary, old, new in prepared:
            require(evolving == review(modules, leagues, choices, mods), "Definitions changed during visibility editing. Refresh before saving.")
            os.replace(temporary, file)
            applied.append((file, old, new))
            evolving["league/" + file.relative_to(leagues).as_posix()] = hashlib.sha256(new.encode("utf-8")).hexdigest()
        require(evolving == review(modules, leagues, choices, mods), "Definitions changed during visibility editing. Refresh before saving.")
    except BaseException:
        for file, old, new in reversed(applied):
            if physical(file).read_bytes().decode("utf-8") == new:
                temporary = file.parent / ("waypoint-rollback-" + uuid.uuid4().hex + ".tmp")
                write_exclusive(temporary, old)
                os.replace(temporary, file)
        raise
    finally:
        for _, temporary, _, _ in prepared:
            if temporary.exists():
                temporary.unlink()


def run(modules, leagues, choices, database, history, action, request, apply=False, auth=None, mods=None):
    modules, leagues, choices, history = map(physical, (modules, leagues, choices, history))
    mods = physical(mods) if mods else None
    fields = {"review", "league", "visible"} if action == "SetLeagueVisibility" else {"review", "league", "choices"} if action == "SaveLeagueModules" else {"review", "template", "name", "description", "modules"}
    require(set(request) <= fields,
            "Unsupported league request fields. Modes must come from an existing profile.")
    expected = request.get("review")
    require(isinstance(expected, dict) and expected == review(modules, leagues, choices, mods), "League or module definitions changed. Refresh and review the draft before saving.")
    systems, profiles = catalogue(modules, leagues, mods)
    on = mod_limits(mods, systems)
    current = read_json(choices) if choices.exists() else {}
    if action == "SetLeagueVisibility":
        visible = request.get("visible")
        require(isinstance(visible, bool), "Choose whether this league is available for new characters.")
        require("game" in systems, "The Game module is missing.")
        changes = visibility_plan(profiles, request.get("league"), visible)
        if apply:
            apply_visibility(changes, visible, expected, modules, leagues, choices, history, mods)
        return
    if action == "SaveLeagueModules":
        target = request.get("league")
        profile = next((p for p in profiles if p["name"] == target), None)
        require(profile is not None, "The selected league no longer exists.")
        updated = request.get("choices")
        require(isinstance(updated, dict), "Invalid league choices.")
        other = lambda data: {k: v for k, v in data.items() if k.casefold() != target.casefold()}
        require(other(updated) == other(current), "A league edit cannot change another league's choices.")
        entries = [v for k, v in updated.items() if k.casefold() == target.casefold()]
        require(len(entries) == 1 and isinstance(entries[0], dict), "Invalid league choices.")
        change = entries[0]
        added, removed = [canon(p, systems) for p in paths(change.get("add"))], paths(change.get("remove"))
        require(all(p.rsplit("/", 1)[-1].casefold() != "game" for p in removed), "The Game module cannot be removed.")
        gone = {p.rsplit("/", 1)[-1].casefold() for p in removed}
        selected = [p for p in profile["modules"] if p.rsplit("/", 1)[-1].casefold() not in gone]
        for path in added:
            if not any(p.rsplit("/", 1)[-1].casefold() == path.rsplit("/", 1)[-1].casefold() for p in selected):
                selected.append(path)
        # What this league's saved choices add now, read as LocalExile reads it: a missing or odd list adds nothing.
        saved = next((v for k, v in current.items() if k.casefold() == target.casefold() and isinstance(v, dict)), {})
        adds = [canon(p.replace("\\", "/"), systems) for p in saved.get("add") or [] if isinstance(p, str)] if isinstance(saved.get("add"), list) else []
        validate_modules(profile, selected, systems, profiles, listed=profile["modules"] + adds, on=on)
        if apply:
            physical(choices.parent).mkdir(parents=True, exist_ok=True)
            if choices.exists():
                backup = history / uuid.uuid4().hex
                physical(backup).mkdir(parents=True)
                shutil.copyfile(choices, backup / choices.name)
            temporary = choices.parent / ("waypoint-" + uuid.uuid4().hex + ".tmp")
            try:
                write_exclusive(temporary, json.dumps(updated, ensure_ascii=False, indent=2))
                require(expected == review(modules, leagues, choices, mods), "Configuration changed during the write. Nothing was replaced.")
                os.replace(temporary, choices)
            finally:
                if temporary.exists():
                    temporary.unlink()
        return
    require(action == "CreateLeague", "Unsupported league action.")
    template = next((p for p in profiles if p["name"] == request.get("template")), None)
    require(template is not None and not template["custom"] and playable(template) and empty_template(template["folder"]),
            "This league has custom code or no reusable source template. It cannot be safely copied.")
    name, description = request.get("name"), request.get("description")
    require(isinstance(name, str) and re.fullmatch(r"[A-Za-z][A-Za-z0-9 _-]{2,47}", name) and name == name.strip()
            and not re.fullmatch(r"CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9]", name, re.I), "Choose a safe, new league name.")
    require(isinstance(description, str) and description.strip() and len(description) <= 2000
            and all(ord(c) >= 32 or c in "\r\n\t" for c in description), "Enter a description of 1-2000 characters.")
    destination = physical(leagues / name)
    require(not destination.exists() and all(p["name"].casefold() != name.casefold() and p["folder"].name.casefold() != name.casefold() for p in profiles)
            and all(k.casefold() != name.casefold() for k in current)
            # An installed mod's id: LocalExile would refuse that mod.
            and not (mods is not None and (mods / name).is_dir()), "That league name is already in use.")
    require(any(p["name"] == "Void" and not p["data"].get("parent") and not p["data"].get("creatable", True)
                and all(path.casefold() == "core/game" for path in p["modules"])
                and ((p["folder"] / "Source").is_dir() or (p["folder"] / "Binaries/league.dll").is_file()) for p in profiles), "The Void destination is missing or not isolated.")
    unused_saved_name(database, name, auth)
    selected = paths(request.get("modules"))
    validate_modules(template, selected, systems, profiles, new=True, on=on)
    if not apply:
        return
    definition = dict(template["data"])
    definition.update(name=name, base=template["base"], parent="Void", description=description, creatable=True, ended=False)
    for key in ("entryOf", "entryKind", "deathLeague", "mods"):
        definition.pop(key, None)
    # The template's own mods that stay chosen are the new league's own, so LocalExile does not load it without them; its
    # modules.json holds the rest.
    own = [m for m in template["own"] if any(p.rsplit("/", 1)[-1].casefold() == m.rsplit("/", 1)[-1].casefold() for p in selected)]
    if own:
        definition["mods"] = own
    own_ids = {m.rsplit("/", 1)[-1].casefold() for m in own}
    if definition.get("hardcore"):
        definition["deathLeague"] = "Void"
    # Reserve a new folder exclusively. Publish league.json last; never replace any existing league or source.
    destination.mkdir()
    created = []
    try:
        source = destination / "Source"
        source.mkdir()
        files = {source / "WaypointLeague.cs": EMPTY_SOURCE,
                 destination / "modules.json": json.dumps({"modules": [p for p in selected if p.rsplit("/", 1)[-1].casefold() not in own_ids]}, indent=2),
                 destination / ".waypoint-league.json": json.dumps({"version": 1, "template": template["name"]})}
        settings = template["folder"] / "settings.json"
        if settings.exists():
            files[destination / "settings.json"] = decode(settings.read_bytes())
        for file, text in files.items():
            write_exclusive(file, text, created)
        manifest = destination / "league.json"
        temporary = destination / "league.json.pending"
        write_exclusive(temporary, json.dumps(definition, ensure_ascii=False, indent=2), created)
        require(expected == review(modules, leagues, choices, mods), "Definitions changed during creation. The new league was not published.")
        # A hard link publishes a complete file atomically and refuses an existing target on both Windows and Linux.
        os.link(temporary, manifest)
        try:
            temporary.unlink()
        except OSError:
            pass  # A committed, complete league remains usable if the redundant temporary link cannot be removed.
    except BaseException:
        for file in reversed(created):
            if file.exists() and not file.is_symlink():
                file.unlink()
        for folder in (destination / "Source", destination):
            if folder.exists() and not list(folder.iterdir()):
                folder.rmdir()
        raise


def main():
    try:
        # Windows PowerShell pipelines can include a UTF-8 BOM. Accept one leading marker,
        # as read_json does for files, while retaining strict JSON and duplicate-key checks.
        request = json.loads(sys.stdin.read().removeprefix("\ufeff"), object_pairs_hook=unique_object)
        # The mods' folder follows the phase ("-" when the servers load no mods), then a split layout's auth.db.
        mods = Path(sys.argv[8]) if len(sys.argv) > 8 and sys.argv[8] != "-" else None
        if sys.argv[6] == "Inspect":
            print(json.dumps({"Ok": True, "Review": review(*map(Path, sys.argv[1:4]), mods)}))
            return 0
        run(*sys.argv[1:6], sys.argv[6], request, apply=sys.argv[7] == "apply", auth=sys.argv[9] if len(sys.argv) > 9 else None, mods=mods)
        print(json.dumps({"Ok": True}))
        return 0
    except (AccountError, OSError, ValueError, TypeError, KeyError, sqlite3.Error) as error:
        print(json.dumps({"Ok": False, "Message": str(error)}, ensure_ascii=True))
        return 1


if __name__ == "__main__":
    sys.exit(main())
