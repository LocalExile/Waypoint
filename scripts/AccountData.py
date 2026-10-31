"""Offline LocalExile account-data adapter. Requests arrive on stdin; credentials never leave it.

Storage contract: LocalExile main 89b7aa908213ac8eb9b5ac5aa7ae54c14189c3e9.
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
ACCOUNT_FIELDS = {"Id", "Name", "PasswordHash", "PasswordSalt", "CreatedUtc", "ClientDigestHash"}
CHARACTER_FIELDS = {"Id", "AccountId", "Name", "Class", "Level", "League", "AreaId", "CreatedUtc",
                    "LastPlayedUtc", "LastTown", "Portrait", "HardcoreDeath"}
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


def inspect_schema(db, check_import=True):
    tables = {r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")}
    require({"accounts", "account_characters", "characters"} <= tables,
            "This database has no supported account tables. Use Create account to initialize LocalExile's account store.")
    compatible = tables == set(SCHEMA)
    for table, expected in SCHEMA.items():
        info = list(db.execute('PRAGMA table_info("' + table + '")'))
        columns = tuple(r[1] for r in info)
        if table in ("accounts", "account_characters", "characters"):
            require(set(expected) <= set(columns), "The account storage layout is not supported.")
        compatible &= columns == expected
        keys = {"accounts": {"id": 1}, "account_characters": {"id": 1}, "characters": {"name": 1},
                "stashes": {"account": 1}, "stash_tabs": {"account": 1, "tab": 2}, "meta": {"key": 1}}
        compatible &= all(r[2].upper() == ("INTEGER" if r[1] == "tab" else "TEXT") and
                          r[5] == keys.get(table, {}).get(r[1], 0) for r in info)
    compatible &= not bool(db.execute("SELECT 1 FROM sqlite_master WHERE type IN ('trigger','view') LIMIT 1").fetchone())
    imported = "meta" in tables and db.execute("SELECT 1 FROM meta WHERE key='accounts_imported'").fetchone()
    if check_import:
        compatible &= bool(imported)
    return bool(compatible)


def snapshot(db):
    writable = inspect_schema(db)
    accounts = [dict(r) for r in db.execute("SELECT id,name,account FROM accounts ORDER BY name COLLATE NOCASE,id LIMIT 2001")]
    require(len(accounts) <= 2000, "Too many accounts for this version of the account manager.")
    chars = [dict(r) for r in db.execute("SELECT id,account_id,name,created_utc,character FROM account_characters ORDER BY created_utc,id LIMIT 48001")]
    require(len(chars) <= 48000, "Too many characters for this version of the account manager.")
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
    tables = {r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
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
        return {}
    require(path.stat().st_size <= 16 * 1024 * 1024, "Ascendancy table is too large.")
    try:
        rows = json.loads(path.read_text(encoding="utf-8-sig"))
        return {r["Id"]: r for r in rows if isinstance(r, dict) and isinstance(r.get("Id"), str)}
    except (ValueError, KeyError, TypeError):
        return {}  # Account browsing remains available without extracted display-name data.


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
                       "Revision": a["revision"], "Characters": characters, "StashTabs": len(a["tabs"])})
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


def change(db, accounts, request, root):
    action = request.get("Action")
    require(action in ("Rename", "Password", "Duplicate", "Delete"), "Unknown account action.")
    a = next((a for a in accounts if a["id"] == request.get("AccountId")), None)
    require(a is not None, "The selected account no longer exists. Refresh the account list.")
    require(request.get("ExpectedRevision") == a["revision"], "This account changed after you selected it. Refresh and review it again.")
    old = a["name"]
    now = dt.datetime.now(dt.timezone.utc).isoformat()
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
    elif action == "Delete":
        require(request.get("Name") == old, "Enter the exact account name to confirm deletion.")
        for ch in a["characters"]:
            if ch["save"] is not None:
                db.execute("INSERT INTO deleted_characters VALUES(?,?,?)", (ch["name"], now, ch["save"]))
            db.execute("DELETE FROM characters WHERE name=?", (ch["name"],))
        db.execute("DELETE FROM account_characters WHERE account_id=?", (a["id"],))
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


def run(path, root, request):
    require(sqlite3.sqlite_version_info >= (3, 37, 0), "Account management needs Python with SQLite 3.37 or newer. Update Python and try again.")
    path = physical(path)
    action = request.get("Action")
    if not path.exists():
        require(action == "List", "No save database exists. Create an account through LocalExile first.")
        return {"Exists": False, "Writable": False, "CanRegister": True, "Notice": "No save database exists yet. Create your first account to initialize LocalExile.", "Accounts": []}
    require(path.is_file(), "The selected save database is not a file.")
    with closing(sqlite3.connect(path.as_uri() + ("?mode=ro" if action == "List" else "?mode=rw"), uri=True, timeout=2, isolation_level=None)) as db:
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
        require(len(sys.argv) == 3, "Expected database and checkout paths.")
        request = json.loads(sys.stdin.readline(1024 * 1024))
        require(isinstance(request, dict), "Invalid account request.")
        result = run(sys.argv[1], sys.argv[2], request)
        print(json.dumps({"Ok": True, "Catalog": result}, ensure_ascii=True))
    except (AccountError, OSError, ValueError, TypeError, KeyError, sqlite3.Error) as error:
        print(json.dumps({"Ok": False, "Message": str(error)}, ensure_ascii=True))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
