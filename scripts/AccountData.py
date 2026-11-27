"""Offline LocalExile account-data adapter. Requests arrive on stdin; credentials never leave it.

Storage contract: LocalExile main 89b7aa908213ac8eb9b5ac5aa7ae54c14189c3e9 (one saves.db), from the database split on
(LocalExile/LocalExile#758) characters.db with the accounts in auth.db, and from relational saves on
(2026_11_11_181109_relational_saves.sql) relational character/item/stash tables in characters.db with accounts in auth.db,
which this helper attaches as `auth` the way the servers do. A change across both files commits in each file on its own:
RuntimeBridge backs both up first.
This helper never initializes schemas, runs emulator code, or manages processes/backups.
RuntimeBridge supplies ownership checks, writer serialization and the backup policy.
"""
import base64
from contextlib import closing
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import sqlite3
import sys
import uuid


class AccountError(Exception):
    pass


SCHEMA = {
    "accounts": ("id", "name", "account"),
    "account_characters": ("id", "account_id", "name", "created_utc", "character"),
    "characters": ("name", "save"), "stashes": ("account", "save"),
    "stash_tabs": ("account", "tab", "items"),
    "deleted_characters": ("name", "deleted_utc", "save"), "meta": ("key", "value"),
}
UPDATES = ("name", "hash", "state", "applied_utc", "speed_ms")
# The split layout by database: the servers' sql/base/, plus the updates table each database records its SQL files in.
SPLIT = {"main": {**{t: c for t, c in SCHEMA.items() if t != "accounts"}, "account_friends": ("owner", "friend"), "updates": UPDATES},
         "auth": {"accounts": SCHEMA["accounts"], "updates": UPDATES}}
KEYS = {"accounts": {"id": 1}, "account_characters": {"id": 1}, "characters": {"name": 1}, "stashes": {"account": 1},
        "stash_tabs": {"account": 1, "tab": 2}, "meta": {"key": 1}, "account_friends": {"owner": 1, "friend": 2}, "updates": {"name": 1}}
INTEGERS = {"tab", "speed_ms"}
# Relational saves layout (2026_11_11_181109_relational_saves.sql and later column updates).
RELATIONAL_ACCOUNT_CHARACTERS = (
    "id", "account_id", "name", "class", "level", "league", "area_id", "created_utc", "last_played_utc",
    "last_town", "portrait", "portrait_frame", "hardcore_death_league", "hardcore_death_utc", "hardcore_death_permanent",
)
RELATIONAL_CHARACTER_CHILDREN = (
    "character_ascendancies", "character_cosmetics", "character_hideout_placements", "character_inventories",
    "character_masteries", "character_module_data", "character_monster_statues", "character_skill_bar",
    "character_skill_cosmetics", "character_tattoo_keystones", "character_tattoos", "character_tutorials",
)
RELATIONAL_STASH_CHILDREN = ("stash_tab_headers", "stash_module_data")
RELATIONAL_ITEM_CHILDREN = (("item_mods", "item"), ("item_skin_transfers", "target"))
RELATIONAL_BROWSE_ACCOUNT_CHARACTERS = ("id", "account_id", "name", "class", "level", "league", "created_utc", "last_played_utc")
RELATIONAL_BROWSE_CHARACTER_SAVES = ("id", "name", "deleted_utc")
RELATIONAL_LEGACY_TABLES = ("characters", "deleted_characters", "stashes", "stash_tabs")
RELATIONAL_REQUIRED = {
    "account_characters": (RELATIONAL_ACCOUNT_CHARACTERS, {"id": 1}),
    "character_saves": (("id", "name", "deleted_utc", "vitals_character", "vitals_session", "vitals_retired"), {"id": 1}),
    "character_ascendancies": (("save", "family", "ascendancy"), {"save": 1}),
    "stash_saves": (("id", "account"), {"id": 1}),
    "stash_tab_headers": (("stash", "tab"), {"stash": 1, "tab": 2}),
    "items": (("id", "character", "stash", "parent"), {"id": 1}),
    "item_mods": (("item", "ord"), {"item": 1, "ord": 2}),
}
RELATIONAL_KNOWN_MAIN = (
    set(RELATIONAL_REQUIRED)
    | set(RELATIONAL_CHARACTER_CHILDREN)
    | set(RELATIONAL_STASH_CHILDREN)
    | {t for t, _ in RELATIONAL_ITEM_CHILDREN}
    | {"account_characters_json", "account_friends", "characters", "deleted_characters", "stashes", "stash_tabs", "meta", "updates"}
)
ACCOUNT_FIELDS = {"Id", "Name", "PasswordHash", "PasswordSalt", "CreatedUtc", "ClientDigestHash"}
CHARACTER_FIELDS = {"Id", "AccountId", "Name", "Class", "Level", "League", "AreaId", "CreatedUtc",
                    "LastPlayedUtc", "LastTown", "Portrait", "HardcoreDeath", "PortraitFrame"}
CLASSES = ("Scion", "Marauder", "Ranger", "Witch", "Duelist", "Templar", "Shadow")
ASCII_FOLD = str.maketrans("ABCDEFGHIJKLMNOPQRSTUVWXYZ", "abcdefghijklmnopqrstuvwxyz")


def require(value, message):
    if not value:
        raise AccountError(message)


def fold(value):
    # Match SQLite's built-in NOCASE, which folds ASCII rather than Unicode.
    return value.translate(ASCII_FOLD)


def physical(path):
    path = Path(os.path.abspath(path))
    for part in (path, *path.parents):
        if part.exists() or part.is_symlink():
            status = part.lstat()
            require(not part.is_symlink() and not getattr(status, "st_file_attributes", 0) & 0x400,
                    "Redirected database/data paths are not supported.")
    return path


def record(raw):
    require(isinstance(raw, str) and len(raw) <= 16 * 1024 * 1024, "An account or save record is too large.")
    value = json.loads(raw)
    require(isinstance(value, dict), "An account or save record is not an object.")
    return value


def identity(value):
    require(isinstance(value, str) and str(uuid.UUID(value)) == value, "An account identity is not canonical.")
    return value


def target_name(value, maximum=32):
    require(isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9_-]{3," + str(maximum) + r"}", value),
            "Use 3-" + str(maximum) + " letters, digits, underscores or hyphens for the new name.")
    return value


def stash_of(key, name):
    plain = key[6:] if fold(key).startswith("guild:") else key
    return fold(plain) == fold(name) or (fold(plain).startswith(fold(name) + " (") and plain.endswith(")"))


def stash_target(key, old, new):
    guild = "guild:" if fold(key).startswith("guild:") else ""
    plain = key[len(guild):]
    return guild + new + plain[len(old):]


def split(db):
    return any(r[1] == "auth" for r in db.execute("PRAGMA database_list"))


def open_saves(path, auth=None, write=False):
    """The save database, read-only unless write, with a split layout's auth.db attached as `auth`."""
    mode = "?mode=rw" if write else "?mode=ro"
    db = sqlite3.connect(physical(path).as_uri() + mode, uri=True, timeout=2, isolation_level=None)
    try:
        if auth is not None:
            db.execute("ATTACH DATABASE ? AS auth", (physical(auth).as_uri() + mode,))
    except BaseException:
        db.close()
        raise
    return db


def table_columns(db, schema, table):
    return list(db.execute('PRAGMA "' + schema + '".table_info("' + table + '")'))


def is_relational(db):
    tables = {r[0] for r in db.execute("SELECT name FROM main.sqlite_master WHERE type='table'")}
    if "character_saves" not in tables or "account_characters" not in tables:
        return False
    cols = {r[1] for r in table_columns(db, "main", "account_characters")}
    return "character" not in cols and "league" in cols


def inspect_schema(db, check_import=True):
    is_split = split(db)
    schemas = ("main", "auth") if is_split else ("main",)
    tables = {schema: {r[0] for r in db.execute('SELECT name FROM "' + schema + '".sqlite_master WHERE type=\'table\' AND name NOT LIKE \'sqlite_%\'')}
              for schema in schemas}
    all_tables = set().union(*tables.values())
    if is_relational(db):
        require({"accounts", "account_characters", "character_saves"} <= all_tables,
                "This database has no supported account tables. Use Create account to initialize LocalExile's account store.")
        require(not is_split or ("accounts" in tables["auth"] and "accounts" not in tables["main"]),
                "The account storage layout is not supported.")
        auth_schema = "auth" if is_split else "main"
        acct_info = table_columns(db, auth_schema, "accounts")
        acct_cols = tuple(r[1] for r in acct_info)
        require(set(SCHEMA["accounts"]) <= set(acct_cols), "The account storage layout is not supported.")
        compatible = acct_cols == SCHEMA["accounts"] and all(
            r[2].upper() == "TEXT" and r[5] == KEYS["accounts"].get(r[1], 0) for r in acct_info
        )
        if is_split:
            compatible &= tables["auth"] == {"accounts", "updates"}
            upd_info = table_columns(db, "auth", "updates")
            compatible &= tuple(r[1] for r in upd_info) == UPDATES and all(
                r[2].upper() == ("INTEGER" if r[1] in INTEGERS else "TEXT") and r[5] == KEYS["updates"].get(r[1], 0)
                for r in upd_info
            )
        allowed_main = RELATIONAL_KNOWN_MAIN if is_split else RELATIONAL_KNOWN_MAIN | {"accounts"}
        compatible &= tables["main"] <= allowed_main
        for table, (required_cols, pk_map) in RELATIONAL_REQUIRED.items():
            if table not in tables["main"]:
                require(table not in ("account_characters", "character_saves"), "The account storage layout is not supported.")
                compatible = False
                continue
            info = table_columns(db, "main", table)
            cols = tuple(r[1] for r in info)
            if table == "account_characters":
                require(set(RELATIONAL_BROWSE_ACCOUNT_CHARACTERS) <= set(cols), "The account storage layout is not supported.")
            elif table == "character_saves":
                require(set(RELATIONAL_BROWSE_CHARACTER_SAVES) <= set(cols), "The account storage layout is not supported.")
            compatible &= set(required_cols) <= set(cols)
            compatible &= all(r[5] == pk_map.get(r[1], 0) for r in info if r[1] in pk_map or r[5] != 0)
        for schema in schemas:
            compatible &= not bool(db.execute('SELECT 1 FROM "' + schema + '".sqlite_master WHERE type IN (\'trigger\',\'view\') LIMIT 1').fetchone())
        if check_import:
            meta_keys = {r[0] for r in db.execute("SELECT key FROM main.meta")} if "meta" in tables["main"] else set()
            has_legacy_rows = any(
                table in tables["main"] and db.execute('SELECT 1 FROM main."' + table + '" LIMIT 1').fetchone() is not None
                for table in RELATIONAL_LEGACY_TABLES
            )
            compatible &= "accounts_imported" in meta_keys and ("relational_saves" in meta_keys or not has_legacy_rows)
        return bool(compatible)

    layout = SPLIT if is_split else {"main": SCHEMA}
    require({"accounts", "account_characters", "characters"} <= all_tables,
            "This database has no supported account tables. Use Create account to initialize LocalExile's account store.")
    # Unqualified names must find the accounts in auth.db: the characters database has none of its own.
    require("auth" not in layout or ("accounts" in tables["auth"] and "accounts" not in tables["main"]), "The account storage layout is not supported.")
    compatible = all(tables[schema] == set(expected) for schema, expected in layout.items())
    for schema, expected_tables in layout.items():
        for table, expected in expected_tables.items():
            info = table_columns(db, schema, table)
            columns = tuple(r[1] for r in info)
            if table in ("accounts", "account_characters", "characters"):
                require(set(expected) <= set(columns), "The account storage layout is not supported.")
            compatible &= columns == expected
            compatible &= all(r[2].upper() == ("INTEGER" if r[1] in INTEGERS else "TEXT") and
                              r[5] == KEYS.get(table, {}).get(r[1], 0) for r in info)
        compatible &= not bool(db.execute('SELECT 1 FROM "' + schema + '".sqlite_master WHERE type IN (\'trigger\',\'view\') LIMIT 1').fetchone())
    imported = "meta" in tables["main"] and db.execute("SELECT 1 FROM main.meta WHERE key='accounts_imported'").fetchone()
    if check_import:
        compatible &= bool(imported)
    return bool(compatible)


def snapshot_relational(db, accounts, owners, by_name, writable):
    tables = {r[0] for r in db.execute("SELECT name FROM main.sqlite_master WHERE type='table'")}
    chars = [dict(r) for r in db.execute("SELECT * FROM account_characters ORDER BY created_utc,id LIMIT 48001")]
    require(len(chars) <= 48000, "Too many characters for this version of the account manager.")
    has_asc = "character_ascendancies" in tables
    for ch in chars:
        identity(ch["id"])
        require(ch["account_id"] in owners, "A character points to an account that is missing.")
        ch["data"] = {
            "Id": ch["id"], "AccountId": ch["account_id"], "Name": ch["name"], "Class": ch["class"],
            "Level": ch["level"], "League": ch["league"], "AreaId": ch.get("area_id", ""), "CreatedUtc": ch["created_utc"],
            "LastPlayedUtc": ch["last_played_utc"], "LastTown": ch.get("last_town"), "Portrait": ch.get("portrait", 0),
            "PortraitFrame": ch.get("portrait_frame", 0),
        }
        save_row = db.execute("SELECT * FROM character_saves WHERE name=? COLLATE NOCASE AND deleted_utc IS NULL", (ch["name"],)).fetchone()
        ch["save_id"] = save_row["id"] if save_row is not None else None
        ch["save"] = dict(save_row) if save_row is not None else None
        asc_row = (db.execute("SELECT family, ascendancy FROM character_ascendancies WHERE save=?", (ch["save_id"],)).fetchone()
                   if has_asc and ch["save_id"] is not None else None)
        ch["progress"] = ({"MainAscendancy": {"Selection": {"Family": asc_row["family"], "Id": asc_row["ascendancy"]}}}
                          if asc_row is not None and asc_row["ascendancy"] else {})
        ch["item_count"] = (db.execute("SELECT count(*) FROM items WHERE character=?", (ch["save_id"],)).fetchone()[0]
                            if "items" in tables and ch["save_id"] is not None else 0)
        owners[ch["account_id"]]["characters"].append(ch)
    if "stash_saves" in tables:
        has_headers = "stash_tab_headers" in tables
        for row in db.execute("SELECT * FROM stash_saves ORDER BY account"):
            key = row["account"]
            plain = key[6:] if fold(key).startswith("guild:") else key
            candidates = {plain, plain.split(" (", 1)[0]} if plain.endswith(")") else {plain}
            matches = [by_name[fold(n)] for n in candidates if fold(n) in by_name]
            require(len(matches) <= 1, "A stash key has ambiguous account ownership.")
            if matches:
                stash_dict = dict(row)
                matches[0]["stashes"].append(stash_dict)
                if has_headers:
                    headers = [dict(h) for h in db.execute("SELECT * FROM stash_tab_headers WHERE stash=? ORDER BY tab", (row["id"],))]
                    matches[0]["tabs"].extend(headers)
    for a in accounts:
        revision = {"account": a["account"], "stashes": a["stashes"], "tabs": a["tabs"],
                    "characters": [{"row": {k: ch[k] for k in ch if k not in ("data", "save", "progress", "save_id", "item_count")},
                                    "save": ch["save"], "progress": ch["progress"], "items": ch["item_count"]}
                                   for ch in a["characters"]]}
        a["revision"] = hashlib.sha256(json.dumps(revision, sort_keys=True, ensure_ascii=True).encode()).hexdigest()
    return accounts, bool(writable)


def snapshot(db):
    writable = inspect_schema(db)
    accounts = [dict(r) for r in db.execute("SELECT id,name,account FROM accounts ORDER BY name COLLATE NOCASE,id LIMIT 2001")]
    require(len(accounts) <= 2000, "Too many accounts for this version of the account manager.")
    owners = {identity(a["id"]): a for a in accounts}
    by_name = {fold(a["name"]): a for a in accounts}
    require(len(owners) == len(accounts) and len(by_name) == len(accounts), "Account identities or names are not unique.")
    for a in accounts:
        a["data"] = record(a["account"])
        require(a["data"].get("Id") == a["id"] and a["data"].get("Name") == a["name"], "Account identity columns and JSON disagree.")
        writable &= set(a["data"]) <= ACCOUNT_FIELDS
        a["characters"] = []
        a["stashes"] = []
        a["tabs"] = []
    if is_relational(db):
        return snapshot_relational(db, accounts, owners, by_name, writable)
    chars = [dict(r) for r in db.execute("SELECT id,account_id,name,created_utc,character FROM account_characters ORDER BY created_utc,id LIMIT 48001")]
    require(len(chars) <= 48000, "Too many characters for this version of the account manager.")
    for ch in chars:
        identity(ch["id"])
        require(ch["account_id"] in owners, "A character points to an account that is missing.")
        ch["data"] = record(ch["character"])
        require(ch["data"].get("Id") == ch["id"] and ch["data"].get("AccountId") == ch["account_id"]
                and ch["data"].get("Name") == ch["name"], "Character identity columns and JSON disagree.")
        writable &= set(ch["data"]) <= CHARACTER_FIELDS
        saved = db.execute("SELECT save FROM characters WHERE name=?", (ch["name"],)).fetchone()
        ch["save"] = saved[0] if saved else None
        ch["progress"] = record(ch["save"]) if ch["save"] is not None else {}
        owners[ch["account_id"]]["characters"].append(ch)
    tables = {r[0] for r in db.execute("SELECT name FROM main.sqlite_master WHERE type='table'")}
    for table, collection in (("stashes", "stashes"), ("stash_tabs", "tabs")):
        if table not in tables:
            continue
        for row in db.execute('SELECT * FROM "' + table + '" ORDER BY account' + (",tab" if table == "stash_tabs" else "")):
            key = row["account"]
            plain = key[6:] if fold(key).startswith("guild:") else key
            candidates = {plain, plain.split(" (", 1)[0]} if plain.endswith(")") else {plain}
            matches = [by_name[fold(n)] for n in candidates if fold(n) in by_name]
            require(len(matches) <= 1, "A stash key has ambiguous account ownership.")
            if matches:
                if table == "stashes":
                    record(row["save"])
                else:
                    require(isinstance(row["items"], str) and len(row["items"]) <= 16 * 1024 * 1024 and
                            isinstance(json.loads(row["items"]), list), "A stash tab is unreadable.")
                matches[0][collection].append(dict(row))
    for a in accounts:
        revision = {k: a[k] for k in ("account", "stashes", "tabs")}
        revision["characters"] = [{k: ch[k] for k in ("id", "account_id", "name", "created_utc", "character", "save")} for ch in a["characters"]]
        a["revision"] = hashlib.sha256(json.dumps(revision,
                                                  sort_keys=True, ensure_ascii=True).encode()).hexdigest()
    return accounts, bool(writable)


def ascendancies(root):
    path = physical(Path(root) / "data" / "tables" / "Ascendancy.json")
    if not path.is_file():
        return client_ascendancies(root)
    require(path.stat().st_size <= 16 * 1024 * 1024, "Ascendancy table is too large.")
    try:
        rows = json.loads(path.read_text(encoding="utf-8-sig"))
        return {r["Id"]: r for r in rows if isinstance(r, dict) and isinstance(r.get("Id"), str)}
    except (ValueError, KeyError, TypeError):
        return {}  # Account browsing remains available without extracted display-name data.


def client_ascendancies(root):
    # From the database split on, LocalExile keeps the client tables in data/client.db; data/tables/ comes only from a full extraction.
    path = physical(Path(root) / "data" / "client.db")
    if not path.is_file():
        return {}
    try:
        with closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True, timeout=2)) as db:
            return {r[0]: {"Id": r[0], "Name": r[1], "LeagueName": r[2]}
                    for r in db.execute("SELECT Id, Name, LeagueName FROM Ascendancy") if isinstance(r[0], str)}
    except sqlite3.Error:
        return {}  # Account browsing remains available without the display names.


def catalog(accounts, writable, root, can_register=False):
    lookup = ascendancies(root)
    result = []
    for a in accounts:
        characters = []
        for ch in a["characters"]:
            data = ch["data"]
            base = data.get("Class")
            base = CLASSES[base] if type(base) is int and 0 <= base < len(CLASSES) else str(base or "Unknown")
            progress = ch["progress"].get("MainAscendancy")
            selection = progress.get("Selection") if isinstance(progress, dict) else None
            asc = ""
            if isinstance(selection, dict) and isinstance(selection.get("Id"), str):
                entry = lookup.get(selection["Id"], {})
                asc = entry.get("LeagueName" if selection.get("Family") == "Phrecia" else "Name") or selection["Id"]
            characters.append({"Id": ch["id"], "Name": ch["name"], "Level": data.get("Level", 1), "Class": base,
                               "Ascendancy": asc, "League": data.get("League", ""), "LastPlayedUtc": data.get("LastPlayedUtc", ""),
                               "HasProgress": ch["save"] is not None})
        result.append({"Id": a["id"], "Name": a["name"], "CreatedUtc": a["data"].get("CreatedUtc", ""),
                       "Revision": a["revision"], "Characters": characters,
                       "StashTabs": sum(1 for t in a["tabs"] if not t.get("hidden"))})
    return {"Exists": True, "Writable": writable, "CanRegister": can_register, "Notice": "" if writable else
            "This storage layout can be browsed, but account changes are disabled because its schema or import state is unfamiliar.", "Accounts": result}


def credentials(password):
    require(isinstance(password, str), "Password must contain 4-1024 characters.")
    try:
        encoded = password.encode("utf-16le")
        password.encode("utf-8")
    except UnicodeError:
        raise AccountError("Password contains unsupported Unicode text.") from None
    require(4 <= len(encoded) // 2 <= 1024, "Password must contain 4-1024 characters.")
    salt = secrets.token_bytes(16)
    def verifier(text):
        return base64.b64encode(hashlib.pbkdf2_hmac("sha256", text.encode("utf-8"), salt, 50000, 32)).decode("ascii")
    return {"PasswordSalt": base64.b64encode(salt).decode("ascii"), "PasswordHash": verifier(password),
            "ClientDigestHash": verifier(hashlib.sha256(password.encode("utf-16le")).hexdigest().upper())}


def check_credentials_contract(root):
    path = physical(Path(root) / "src" / "GameServer.Core" / "Accounts" / "AccountService.cs")
    require(path.is_file() and path.stat().st_size < 128 * 1024, "Cannot verify this checkout's password format.")
    text = path.read_text(encoding="utf-8-sig")
    require(re.search(r"Pbkdf2\(pw,\s*Convert.FromBase64String\(salt\),\s*50_000,\s*HashAlgorithmName.SHA256,\s*32\)", text)
            and "SHA256.HashData(Encoding.Unicode.GetBytes(password))" in text
            and "Hash(Convert.ToHexString(digest), salt)" in text,
            "This checkout's password format is unfamiliar. No credentials were changed.")


def encode(value):
    return json.dumps(value, ensure_ascii=True, separators=(",", ":"))


def clone_items(db, tables, owner_col, old_owner_id, new_owner_id):
    if "items" not in tables:
        return
    item_cols = [r[1] for r in table_columns(db, "main", "items") if r[1] != "id"]
    rows = [dict(r) for r in db.execute('SELECT * FROM items WHERE "' + owner_col + '"=? ORDER BY CASE WHEN parent IS NULL THEN 0 ELSE 1 END, id', (old_owner_id,))]
    if not rows:
        return
    id_map = {}
    remaining = rows
    cols_sql = ",".join('"' + c + '"' for c in item_cols)
    qs = ",".join("?" for _ in item_cols)
    # Insert parents before children (handles arbitrary nesting such as socketed/original items).
    while remaining:
        next_remaining = []
        progressed = False
        for row in remaining:
            parent = row["parent"]
            if parent is not None and parent not in id_map:
                next_remaining.append(row)
                continue
            values = []
            for c in item_cols:
                if c == owner_col:
                    values.append(new_owner_id)
                elif c == "parent":
                    values.append(id_map[parent] if parent is not None else None)
                else:
                    values.append(row[c])
            cur = db.execute('INSERT INTO items(' + cols_sql + ') VALUES(' + qs + ')', values)
            id_map[row["id"]] = cur.lastrowid
            progressed = True
        require(progressed, "Saved item parent links contain a cycle or missing parent.")
        remaining = next_remaining
    for child_table, fk_col in RELATIONAL_ITEM_CHILDREN:
        if child_table not in tables:
            continue
        child_cols = [r[1] for r in table_columns(db, "main", child_table)]
        c_sql = ",".join('"' + c + '"' for c in child_cols)
        c_qs = ",".join("?" for _ in child_cols)
        for old_item_id, new_item_id in id_map.items():
            for crow in db.execute('SELECT * FROM "' + child_table + '" WHERE "' + fk_col + '"=?', (old_item_id,)):
                vals = [new_item_id if c == fk_col else crow[c] for c in child_cols]
                db.execute('INSERT INTO "' + child_table + '"(' + c_sql + ') VALUES(' + c_qs + ')', vals)


def change_relational(db, a, action, request, old, now, name, hashes):
    tables = {r[0] for r in db.execute("SELECT name FROM main.sqlite_master WHERE type='table'")}
    if action == "Delete":
        require(request.get("Name") == old, "Enter the exact account name to confirm deletion.")
        for ch in a["characters"]:
            if ch["save_id"] is not None:
                db.execute("UPDATE character_saves SET deleted_utc=? WHERE id=?", (now, ch["save_id"]))
        db.execute("DELETE FROM account_characters WHERE account_id=?", (a["id"],))
        if "account_friends" in tables:
            db.execute("DELETE FROM account_friends WHERE owner=? OR friend=?", (a["id"], a["id"]))
        for stash in a["stashes"]:
            sid = stash["id"]
            if "items" in tables:
                for child_table, fk_col in RELATIONAL_ITEM_CHILDREN:
                    if child_table in tables:
                        db.execute('DELETE FROM "' + child_table + '" WHERE "' + fk_col + '" IN (SELECT id FROM items WHERE stash=?)', (sid,))
                # Clear parent links before deleting to satisfy self-referencing FK.
                db.execute("UPDATE items SET parent=NULL WHERE stash=?", (sid,))
                db.execute("DELETE FROM items WHERE stash=?", (sid,))
            for child_table in RELATIONAL_STASH_CHILDREN:
                if child_table in tables:
                    db.execute('DELETE FROM "' + child_table + '" WHERE stash=?', (sid,))
            db.execute("DELETE FROM stash_saves WHERE id=?", (sid,))
        db.execute("DELETE FROM accounts WHERE id=?", (a["id"],))
        return

    cloning = action == "Duplicate"
    include_characters = cloning and request.get("IncludeCharacters") is True
    include_stash = not cloning or request.get("IncludeStash") is True
    mapping = request.get("CharacterNames") or {}
    if include_characters:
        require(isinstance(mapping, dict) and set(mapping) == {ch["id"] for ch in a["characters"]}, "Review the new name of every copied character.")
        names = [target_name(mapping[ch["id"]], 23) for ch in a["characters"]]
        require(len({fold(n) for n in names}) == len(names), "Copied characters need unique names.")
        for n in names:
            require(not db.execute(
                "SELECT 1 FROM account_characters WHERE name=? UNION ALL SELECT 1 FROM character_saves WHERE name=? AND deleted_utc IS NULL",
                (n, n),
            ).fetchone(), "A copied character name is already in use, including retained progress. Review the names again.")
    if include_stash:
        for stash in a["stashes"]:
            dest = stash_target(stash["account"], old, name)
            if not cloning and fold(dest) == fold(stash["account"]):
                continue
            require(not db.execute("SELECT 1 FROM stash_saves WHERE account=?", (dest,)).fetchone(),
                    "The new account name has retained stash data. Choose another name.")
    if cloning:
        new_id = str(uuid.uuid4())
        data = dict(a["data"], Id=new_id, Name=name, CreatedUtc=now, **hashes)
        db.execute("INSERT INTO accounts VALUES(?,?,?)", (new_id, name, encode(data)))
        if include_characters:
            ac_cols = [r[1] for r in table_columns(db, "main", "account_characters")]
            ac_sql = ",".join('"' + c + '"' for c in ac_cols)
            ac_qs = ",".join("?" for _ in ac_cols)
            cs_cols = [r[1] for r in table_columns(db, "main", "character_saves") if r[1] != "id"]
            cs_sql = ",".join('"' + c + '"' for c in cs_cols)
            cs_qs = ",".join("?" for _ in cs_cols)
            for ch in a["characters"]:
                char_id, char_name = str(uuid.uuid4()), mapping[ch["id"]]
                ac_vals = [char_id if c == "id" else new_id if c == "account_id" else char_name if c == "name" else ch[c] for c in ac_cols]
                db.execute('INSERT INTO account_characters(' + ac_sql + ') VALUES(' + ac_qs + ')', ac_vals)
                if ch["save_id"] is not None:
                    save = ch["save"]
                    cs_vals = [
                        char_name if c == "name"
                        else None if c in ("vitals_character", "vitals_session", "vitals_retired")
                        else save[c]
                        for c in cs_cols
                    ]
                    new_save_id = db.execute('INSERT INTO character_saves(' + cs_sql + ') VALUES(' + cs_qs + ')', cs_vals).lastrowid
                    for child_table in RELATIONAL_CHARACTER_CHILDREN:
                        if child_table not in tables:
                            continue
                        ccols = [r[1] for r in table_columns(db, "main", child_table)]
                        c_sql = ",".join('"' + c + '"' for c in ccols)
                        c_qs = ",".join("?" for _ in ccols)
                        for crow in db.execute('SELECT * FROM "' + child_table + '" WHERE save=?', (ch["save_id"],)):
                            cvals = [new_save_id if c == "save" else crow[c] for c in ccols]
                            db.execute('INSERT INTO "' + child_table + '"(' + c_sql + ') VALUES(' + c_qs + ')', cvals)
                    clone_items(db, tables, "character", ch["save_id"], new_save_id)
        if include_stash:
            ss_cols = [r[1] for r in table_columns(db, "main", "stash_saves") if r[1] != "id"]
            ss_sql = ",".join('"' + c + '"' for c in ss_cols)
            ss_qs = ",".join("?" for _ in ss_cols)
            for stash in a["stashes"]:
                dest = stash_target(stash["account"], old, name)
                ss_vals = [dest if c == "account" else stash[c] for c in ss_cols]
                new_stash_id = db.execute('INSERT INTO stash_saves(' + ss_sql + ') VALUES(' + ss_qs + ')', ss_vals).lastrowid
                for child_table in RELATIONAL_STASH_CHILDREN:
                    if child_table not in tables:
                        continue
                    ccols = [r[1] for r in table_columns(db, "main", child_table)]
                    c_sql = ",".join('"' + c + '"' for c in ccols)
                    c_qs = ",".join("?" for _ in ccols)
                    for crow in db.execute('SELECT * FROM "' + child_table + '" WHERE stash=?', (stash["id"],)):
                        cvals = [new_stash_id if c == "stash" else crow[c] for c in ccols]
                        db.execute('INSERT INTO "' + child_table + '"(' + c_sql + ') VALUES(' + c_qs + ')', cvals)
                clone_items(db, tables, "stash", stash["id"], new_stash_id)
    else:
        db.execute("UPDATE accounts SET name=?,account=? WHERE id=?", (name, encode(dict(a["data"], Name=name)), a["id"]))
        for stash in a["stashes"]:
            dest = stash_target(stash["account"], old, name)
            db.execute("UPDATE stash_saves SET account=? WHERE id=?", (dest, stash["id"]))


def change(db, accounts, request, root):
    action = request.get("Action")
    require(action in ("Rename", "Password", "Duplicate", "Delete"), "Unknown account action.")
    a = next((a for a in accounts if a["id"] == request.get("AccountId")), None)
    require(a is not None, "The selected account no longer exists. Refresh the account list.")
    require(request.get("ExpectedRevision") == a["revision"], "This account changed after you selected it. Refresh and review it again.")
    old = a["name"]
    now = dt.datetime.now(dt.timezone.utc).isoformat()
    name = None
    hashes = None
    if action in ("Rename", "Duplicate"):
        name = target_name(request.get("Name"))
        require(not db.execute("SELECT 1 FROM accounts WHERE name=? AND id<>?", (name, a["id"] if action == "Rename" else "")).fetchone(),
                "The new account name is already in use.")
    if action in ("Password", "Duplicate"):
        check_credentials_contract(root)
        hashes = credentials(request.get("Password"))
    if action == "Password":
        data = dict(a["data"], **hashes)
        db.execute("UPDATE accounts SET account=? WHERE id=?", (encode(data), a["id"]))
    elif is_relational(db):
        change_relational(db, a, action, request, old, now, name, hashes)
    elif action == "Delete":
        require(request.get("Name") == old, "Enter the exact account name to confirm deletion.")
        for ch in a["characters"]:
            if ch["save"] is not None:
                db.execute("INSERT INTO deleted_characters VALUES(?,?,?)", (ch["name"], now, ch["save"]))
            db.execute("DELETE FROM characters WHERE name=?", (ch["name"],))
        db.execute("DELETE FROM account_characters WHERE account_id=?", (a["id"],))
        if db.execute("SELECT 1 FROM main.sqlite_master WHERE type='table' AND name='account_friends'").fetchone():
            db.execute("DELETE FROM account_friends WHERE owner=? OR friend=?", (a["id"], a["id"]))
        for key in {r["account"] for r in a["stashes"] + a["tabs"]}:
            db.execute("DELETE FROM stashes WHERE account=?", (key,))
            db.execute("DELETE FROM stash_tabs WHERE account=?", (key,))
        db.execute("DELETE FROM accounts WHERE id=?", (a["id"],))
    else:
        cloning = action == "Duplicate"
        include_characters = cloning and request.get("IncludeCharacters") is True
        include_stash = not cloning or request.get("IncludeStash") is True
        mapping = request.get("CharacterNames") or {}
        if include_characters:
            require(isinstance(mapping, dict) and set(mapping) == {ch["id"] for ch in a["characters"]}, "Review the new name of every copied character.")
            names = [target_name(mapping[ch["id"]], 23) for ch in a["characters"]]
            require(len({fold(n) for n in names}) == len(names), "Copied characters need unique names.")
            for n in names:
                require(not db.execute("SELECT 1 FROM account_characters WHERE name=? UNION ALL SELECT 1 FROM characters WHERE name=?", (n, n)).fetchone(),
                        "A copied character name is already in use, including retained progress. Review the names again.")
        keys = {r["account"] for r in a["stashes"] + a["tabs"]} if include_stash else set()
        for key in keys:
            dest = stash_target(key, old, name)
            if not cloning and fold(dest) == fold(key):
                continue
            require(not db.execute("SELECT 1 FROM stashes WHERE account=? UNION ALL SELECT 1 FROM stash_tabs WHERE account=?", (dest, dest)).fetchone(),
                    "The new account name has retained stash data. Choose another name.")
        if cloning:
            new_id = str(uuid.uuid4())
            data = dict(a["data"], Id=new_id, Name=name, CreatedUtc=now, **hashes)
            db.execute("INSERT INTO accounts VALUES(?,?,?)", (new_id, name, encode(data)))
            if include_characters:
                for ch in a["characters"]:
                    char_id, char_name = str(uuid.uuid4()), mapping[ch["id"]]
                    data = dict(ch["data"], Id=char_id, AccountId=new_id, Name=char_name)
                    db.execute("INSERT INTO account_characters VALUES(?,?,?,?,?)", (char_id, new_id, char_name, ch["created_utc"], encode(data)))
                    if ch["save"] is not None:
                        progress = dict(ch["progress"])
                        if "VitalsOwner" in progress:
                            progress["VitalsOwner"] = None
                        db.execute("INSERT INTO characters VALUES(?,?)", (char_name, encode(progress)))
        else:
            db.execute("UPDATE accounts SET name=?,account=? WHERE id=?", (name, encode(dict(a["data"], Name=name)), a["id"]))
        for table, rows, columns in (("stashes", a["stashes"], ("account", "save")), ("stash_tabs", a["tabs"], ("account", "tab", "items"))):
            if not include_stash:
                continue
            for row in rows:
                dest = stash_target(row["account"], old, name)
                if cloning:
                    values = [dest] + [row[c] for c in columns[1:]]
                    db.execute('INSERT INTO "' + table + '" VALUES(' + ','.join('?' for _ in values) + ')', values)
                elif table == "stashes":
                    db.execute("UPDATE stashes SET account=? WHERE account=?", (dest, row["account"]))
                else:
                    db.execute("UPDATE stash_tabs SET account=? WHERE account=? AND tab=?", (dest, row["account"], row["tab"]))


def run(path, root, request, auth=None):
    """auth: a split layout's auth.db, beside the characters database at path; None for one saves.db."""
    require(sqlite3.sqlite_version_info >= (3, 37, 0), "Account management needs Python with SQLite 3.37 or newer. Update Python and try again.")
    path = physical(path)
    auth = physical(auth) if auth else None
    action = request.get("Action")
    if not path.exists() and (auth is None or not auth.exists()):
        require(action == "List", "No save database exists. Create an account through LocalExile first.")
        return {"Exists": False, "Writable": False, "CanRegister": True, "Notice": "No save database exists yet. Create your first account to initialize LocalExile.", "Accounts": []}
    require(path.is_file() and (auth is None or auth.is_file()), "The selected save database is not a file.")
    with closing(open_saves(path, auth, write=action != "List")) as db:
        db.row_factory = sqlite3.Row
        db.execute("PRAGMA foreign_keys=ON")
        db.execute("BEGIN" if action == "List" else "BEGIN IMMEDIATE")
        try:
            accounts, writable = snapshot(db)
            if action != "List":
                require(writable, "Account changes are disabled for this unfamiliar storage layout or pending account import.")
                require(db.execute("PRAGMA quick_check").fetchone()[0] == "ok", "The database integrity check failed.")
                change(db, accounts, request, root)
                require(db.execute("PRAGMA quick_check").fetchone()[0] == "ok", "The changed database failed its integrity check.")
                accounts, writable = snapshot(db)
            result = catalog(accounts, writable, root, writable or (not accounts and inspect_schema(db, check_import=False)))
            db.execute("COMMIT")
            return result
        except BaseException:
            db.execute("ROLLBACK")
            raise


def main():
    try:
        require(len(sys.argv) in (3, 4), "Expected database and checkout paths, and a split layout's auth database.")
        request = json.loads(sys.stdin.readline(1024 * 1024))
        require(isinstance(request, dict), "Invalid account request.")
        result = run(sys.argv[1], sys.argv[2], request, sys.argv[3] if len(sys.argv) == 4 else None)
        print(json.dumps({"Ok": True, "Catalog": result}, ensure_ascii=True))
    except (AccountError, OSError, ValueError, TypeError, KeyError, sqlite3.Error) as error:
        print(json.dumps({"Ok": False, "Message": str(error)}, ensure_ascii=True))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
