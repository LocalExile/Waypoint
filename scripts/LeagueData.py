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

from AccountData import AccountError, inspect_schema, physical, record, require


EMPTY_SOURCE = "public sealed class WaypointLeague : League { }\n"


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate configuration key.")
        result[key] = value
    return result


def read_json(path):
    path = physical(path)
    require(path.is_file() and path.stat().st_size <= 1024 * 1024, "Configuration is missing or too large.")
    text = path.read_text(encoding="utf-8-sig")
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


def review(modules, leagues, choices):
    result = {"choices": digest(choices)}
    for file in manifests(modules, "module.json"):
        result["module/" + file.relative_to(modules).as_posix()] = digest(file)
    for manifest in manifests(leagues, "league.json"):
        folder = manifest.parent
        for name in ("league.json", "modules.json", "settings.json", ".waypoint-league.json"):
            file = folder / name
            if file.exists():
                result["league/" + file.relative_to(leagues).as_posix()] = digest(file)
        source = physical(folder / "Source")
        if source.is_dir():
            for file in source.iterdir():
                physical(file)
                if file.is_file():
                    result["league/" + file.relative_to(leagues).as_posix()] = digest(file)
                elif file.is_dir():
                    result["nested/" + folder.name] = "unsupported"
        require(len(result) <= 10000, "Content inventory is too large.")
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


def catalogue(modules, leagues):
    systems = {}
    for file in manifests(modules, "module.json"):
        data = read_json(file)
        identifier = data.get("id", file.parent.name)
        require(isinstance(identifier, str) and identifier == file.parent.name and identifier.casefold() not in systems, "Invalid or duplicate module ID.")
        systems[identifier.casefold()] = {"path": file.parent.relative_to(modules).as_posix(), "depends": data.get("depends", [])}
        require(isinstance(systems[identifier.casefold()]["depends"], list)
                and all(isinstance(d, str) for d in systems[identifier.casefold()]["depends"]), "Invalid module dependencies.")
    profiles = []
    names = set()
    for file in manifests(leagues, "league.json"):
        data = read_json(file)
        name = data.get("name", file.parent.name)
        require(isinstance(name, str) and name and name.casefold() not in names, "Invalid or duplicate league.")
        names.add(name.casefold())
        for key in ("ssf", "hardcore", "ended", "creatable"):
            require(key not in data or isinstance(data[key], bool), "Invalid league flag.")
        for key in ("base", "form", "dataSet", "parent", "entryOf", "entryKind", "deathLeague"):
            require(data.get(key) is None or isinstance(data[key], str), "Invalid league field.")
        profiles.append({"data": data, "name": name, "folder": file.parent, "base": data.get("base") or name,
                         "mode": (data.get("hardcore", False), data.get("ssf", False), data.get("form") or "", data.get("dataSet") or ""),
                         "modules": paths(read_json(file.parent / "modules.json").get("modules", [])),
                         "custom": (file.parent / ".waypoint-league.json").exists(),
                         "template": read_json(file.parent / ".waypoint-league.json").get("template", "") if (file.parent / ".waypoint-league.json").exists() else ""})
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


def validate_modules(profile, selected, systems, profiles):
    require(playable(profile), "Ended or non-playable league definitions are read-only.")
    active = {path.rsplit("/", 1)[-1].casefold(): path for path in selected}
    require("game" in active, "The Game module is required.")
    for identifier, path in active.items():
        require(identifier in systems and systems[identifier]["path"].casefold() == path.casefold(), "A selected module is missing.")
    required = [p for p in profile["modules"] if not p.casefold().startswith("core/") or p.rsplit("/", 1)[-1].casefold() == "game"]
    require(all(p.casefold() in {s.casefold() for s in selected} for p in required), "Keep the required gameplay bundle enabled.")
    require(any(not p["custom"] and playable(p) and p["base"].casefold() == profile["base"].casefold()
                and family_name(p, profiles).casefold() == family_name(profile, profiles).casefold() and p["mode"] == profile["mode"]
                and {s.casefold() for s in selected} <= {s.casefold() for s in p["modules"]} for p in profiles),
            "This module combination is not defined for this league and mode. Unknown combinations are blocked.")
    visiting, done = set(), set()

    def visit(identifier):
        if identifier in done:
            return
        require(identifier not in visiting, "The selected modules contain a dependency cycle.")
        visiting.add(identifier)
        for dependency in systems[identifier]["depends"]:
            require(dependency.casefold() in active, identifier + " needs " + dependency + ".")
            visit(dependency.casefold())
        visiting.remove(identifier)
        done.add(identifier)

    for identifier in active:
        visit(identifier)


def unused_saved_name(database, name):
    database = physical(database)
    if not database.exists():
        return
    with closing(sqlite3.connect(database.as_uri() + "?mode=ro", uri=True, timeout=2, isolation_level=None)) as db:
        db.execute("BEGIN")
        require(inspect_schema(db), "The save layout or pending import cannot be safely inspected. League creation is blocked.")
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
    text = re.sub(r"/\*[\s\S]*?\*/|//[^\r\n]*", "", files[0].read_text(encoding="utf-8-sig"))
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


def apply_visibility(changes, visible, expected, modules, leagues, choices, history):
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
            require(evolving == review(modules, leagues, choices), "Definitions changed during visibility editing. Refresh before saving.")
            os.replace(temporary, file)
            applied.append((file, old, new))
            evolving["league/" + file.relative_to(leagues).as_posix()] = hashlib.sha256(new.encode("utf-8")).hexdigest()
        require(evolving == review(modules, leagues, choices), "Definitions changed during visibility editing. Refresh before saving.")
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


def run(modules, leagues, choices, database, history, action, request, apply=False):
    modules, leagues, choices, history = map(physical, (modules, leagues, choices, history))
    fields = {"review", "league", "visible"} if action == "SetLeagueVisibility" else {"review", "league", "choices"} if action == "SaveLeagueModules" else {"review", "template", "name", "description", "modules"}
    require(set(request) <= fields,
            "Unsupported league request fields. Modes must come from an existing profile.")
    expected = request.get("review")
    require(isinstance(expected, dict) and expected == review(modules, leagues, choices), "League or module definitions changed. Refresh and review the draft before saving.")
    systems, profiles = catalogue(modules, leagues)
    current = read_json(choices) if choices.exists() else {}
    if action == "SetLeagueVisibility":
        visible = request.get("visible")
        require(isinstance(visible, bool), "Choose whether this league is available for new characters.")
        require("game" in systems, "The Game module is missing.")
        changes = visibility_plan(profiles, request.get("league"), visible)
        if apply:
            apply_visibility(changes, visible, expected, modules, leagues, choices, history)
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
        added, removed = paths(change.get("add")), paths(change.get("remove"))
        require(all(p.rsplit("/", 1)[-1].casefold() != "game" for p in removed), "The Game module cannot be removed.")
        gone = {p.rsplit("/", 1)[-1].casefold() for p in removed}
        selected = [p for p in profile["modules"] if p.rsplit("/", 1)[-1].casefold() not in gone]
        for path in added:
            if not any(p.rsplit("/", 1)[-1].casefold() == path.rsplit("/", 1)[-1].casefold() for p in selected):
                selected.append(path)
        validate_modules(profile, selected, systems, profiles)
        if apply:
            physical(choices.parent).mkdir(parents=True, exist_ok=True)
            if choices.exists():
                backup = history / uuid.uuid4().hex
                physical(backup).mkdir(parents=True)
                shutil.copyfile(choices, backup / choices.name)
            temporary = choices.parent / ("waypoint-" + uuid.uuid4().hex + ".tmp")
            try:
                write_exclusive(temporary, json.dumps(updated, ensure_ascii=False, indent=2))
                require(expected == review(modules, leagues, choices), "Configuration changed during the write. Nothing was replaced.")
                os.replace(temporary, choices)
            finally:
                if temporary.exists():
                    temporary.unlink()
        return
    require(action == "CreateLeague", "Unsupported league action.")
    template = next((p for p in profiles if p["folder"].name == request.get("template")), None)
    require(template is not None and not template["custom"] and playable(template) and empty_template(template["folder"]),
            "This league has custom code or no reusable source template. It cannot be safely copied.")
    name, description = request.get("name"), request.get("description")
    require(isinstance(name, str) and re.fullmatch(r"[A-Za-z][A-Za-z0-9 _-]{2,47}", name) and name == name.strip()
            and not re.fullmatch(r"CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9]", name, re.I), "Choose a safe, new league name.")
    require(isinstance(description, str) and description.strip() and len(description) <= 2000
            and all(ord(c) >= 32 or c in "\r\n\t" for c in description), "Enter a description of 1-2000 characters.")
    destination = physical(leagues / name)
    require(not destination.exists() and all(p["name"].casefold() != name.casefold() and p["folder"].name.casefold() != name.casefold() for p in profiles)
            and all(k.casefold() != name.casefold() for k in current), "That league name is already in use.")
    require(any(p["name"] == "Void" and not p["data"].get("parent") and not p["data"].get("creatable", True) and not p["modules"]
                and ((p["folder"] / "Source").is_dir() or (p["folder"] / "Binaries/league.dll").is_file()) for p in profiles), "The Void destination is missing or not isolated.")
    unused_saved_name(database, name)
    selected = paths(request.get("modules"))
    validate_modules(template, selected, systems, profiles)
    if not apply:
        return
    definition = dict(template["data"])
    definition.update(name=name, base=template["base"], parent="Void", description=description, creatable=True, ended=False)
    for key in ("entryOf", "entryKind", "deathLeague"):
        definition.pop(key, None)
    if definition.get("hardcore"):
        definition["deathLeague"] = "Void"
    # Reserve a new folder exclusively. Publish league.json last; never replace any existing league or source.
    destination.mkdir()
    created = []
    try:
        source = destination / "Source"
        source.mkdir()
        files = {source / "WaypointLeague.cs": EMPTY_SOURCE,
                 destination / "modules.json": json.dumps({"modules": selected}, indent=2),
                 destination / ".waypoint-league.json": json.dumps({"version": 1, "template": template["name"]})}
        settings = template["folder"] / "settings.json"
        if settings.exists():
            files[destination / "settings.json"] = settings.read_text(encoding="utf-8-sig")
        for file, text in files.items():
            write_exclusive(file, text, created)
        manifest = destination / "league.json"
        temporary = destination / "league.json.pending"
        write_exclusive(temporary, json.dumps(definition, ensure_ascii=False, indent=2), created)
        require(expected == review(modules, leagues, choices), "Definitions changed during creation. The new league was not published.")
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
        request = json.load(sys.stdin, object_pairs_hook=unique_object)
        if sys.argv[6] == "Inspect":
            print(json.dumps({"Ok": True, "Review": review(*map(Path, sys.argv[1:4]))}))
            return 0
        run(*sys.argv[1:6], sys.argv[6], request, apply=sys.argv[7] == "apply")
        print(json.dumps({"Ok": True}))
        return 0
    except (AccountError, OSError, ValueError, TypeError, KeyError, sqlite3.Error) as error:
        print(json.dumps({"Ok": False, "Message": str(error)}, ensure_ascii=True))
        return 1


if __name__ == "__main__":
    sys.exit(main())
