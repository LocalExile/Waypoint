"""Disposable account/storage integration. Never opens the owner's game database."""
import base64
from contextlib import closing, contextmanager
import hashlib
import importlib.util
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
import uuid

spec = importlib.util.spec_from_file_location("account_data", Path(__file__).resolve().parents[1] / "scripts" / "AccountData.py")
data = importlib.util.module_from_spec(spec)
spec.loader.exec_module(data)


@contextmanager
def connect(path):
    with closing(sqlite3.connect(path)) as db:
        with db:
            yield db


class AccountDataTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="waypoint-accounts-")
        self.root = Path(self.temp.name)
        self.path = self.root / "saves #test.db"
        service = self.root / "src/GameServer.Core/Accounts/AccountService.cs"
        service.parent.mkdir(parents=True)
        service.write_text("Pbkdf2(pw, Convert.FromBase64String(salt), 50_000, HashAlgorithmName.SHA256, 32)\n"
                           "SHA256.HashData(Encoding.Unicode.GetBytes(password))\nHash(Convert.ToHexString(digest), salt)", encoding="utf-8")
        table = self.root / "data/tables/Ascendancy.json"
        table.parent.mkdir(parents=True)
        table.write_text(json.dumps([{"Id": "Witch3", "Name": "Elementalist", "LeagueName": "Harbinger"}]), encoding="utf-8")
        self.account, self.other, self.char = (str(uuid.uuid4()) for _ in range(3))
        with connect(self.path) as db:
            db.executescript('''CREATE TABLE accounts(id TEXT PRIMARY KEY,name TEXT UNIQUE COLLATE NOCASE,account TEXT);
                CREATE TABLE account_characters(id TEXT PRIMARY KEY,account_id TEXT,name TEXT UNIQUE COLLATE NOCASE,created_utc TEXT,character TEXT);
                CREATE TABLE characters(name TEXT PRIMARY KEY COLLATE NOCASE,save TEXT);
                CREATE TABLE stashes(account TEXT PRIMARY KEY COLLATE NOCASE,save TEXT);
                CREATE TABLE stash_tabs(account TEXT COLLATE NOCASE,tab INTEGER,items TEXT,PRIMARY KEY(account,tab));
                CREATE TABLE deleted_characters(name TEXT,deleted_utc TEXT,save TEXT);
                CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT);
                INSERT INTO meta VALUES('accounts_imported','fixture');''')
            for identity, name in ((self.account, "Alpha"), (self.other, "AlphaOther")):
                record = {"Id": identity, "Name": name, "PasswordHash": "private-verifier", "PasswordSalt": "private-salt", "CreatedUtc": "2026-01-01T00:00:00Z", "ClientDigestHash": None}
                db.execute("INSERT INTO accounts VALUES(?,?,?)", (identity, name, json.dumps(record)))
            char = {"Id": self.char, "AccountId": self.account, "Name": "OriginalHero", "Class": 3, "Level": 77,
                    "League": "Hardcore SSF Allflame", "AreaId": "1_1_1", "CreatedUtc": "2026-01-01T00:00:00Z", "LastPlayedUtc": "2026-02-01T00:00:00Z", "HardcoreDeath": None}
            db.execute("INSERT INTO account_characters VALUES(?,?,?,?,?)", (self.char, self.account, char["Name"], char["CreatedUtc"], json.dumps(char)))
            save = {"Inventories": {"1": [{"Id": 12, "Item": {"Type": 9}}]}, "MainAscendancy": {"Selection": {"Family": "Normal", "Id": "Witch3", "Revision": "3.29.3b"}},
                    "VitalsOwner": {"CharacterId": self.char, "SessionId": str(uuid.uuid4())}, "FutureModData": {"keep": [1, 2, 3]}}
            db.execute("INSERT INTO characters VALUES(?,?)", (char["Name"], json.dumps(save)))
            for key in ("Alpha", "Alpha (Allflame)", "guild:Alpha", "guild:Alpha (Hardcore SSF Allflame)", "AlphaOther"):
                db.execute("INSERT INTO stashes VALUES(?,?)", (key, '{"Gold":123,"CustomHeader":true}'))
                db.execute("INSERT INTO stash_tabs VALUES(?,?,?)", (key, 0, '[{"Item":{"Type":999}}]'))

    def tearDown(self):
        self.temp.cleanup()

    def query(self):
        return data.run(self.path, self.root, {"Action": "List"})

    def account_view(self):
        return next(a for a in self.query()["Accounts"] if a["Id"] == self.account)

    def change(self, action, **extras):
        request = {"Action": action, "AccountId": self.account, "ExpectedRevision": self.account_view()["Revision"]}
        request.update(extras)
        return data.run(self.path, self.root, request)

    def logical(self):
        with connect(self.path) as db:
            return {name: db.execute('SELECT * FROM "' + name + '" ORDER BY 1').fetchall() for name in data.SCHEMA}

    def test_read_only_listing_and_no_credential_exposure(self):
        before = self.path.read_bytes()
        view = self.query()
        self.assertTrue(view["Writable"])
        self.assertEqual(view["Accounts"][0]["Characters"][0]["Ascendancy"], "Elementalist")
        self.assertEqual(view["Accounts"][0]["Characters"][0]["Class"], "Witch")
        self.assertEqual(view["Accounts"][0]["Characters"][0]["Level"], 77)
        self.assertNotIn("private-verifier", json.dumps(view))
        self.assertNotIn("PasswordSalt", json.dumps(view))
        self.assertEqual(before, self.path.read_bytes())

    def test_missing_database_is_not_created(self):
        path = self.root / "missing.db"
        self.assertFalse(data.run(path, self.root, {"Action": "List"})["Exists"])
        self.assertFalse(path.exists())

    def test_ascendancy_fallback_and_phrecia(self):
        with connect(self.path) as db:
            value = json.loads(db.execute("SELECT save FROM characters").fetchone()[0])
            value["MainAscendancy"]["Selection"]["Family"] = "Phrecia"
            db.execute("UPDATE characters SET save=?", (json.dumps(value),))
        self.assertEqual(self.account_view()["Characters"][0]["Ascendancy"], "Harbinger")
        (self.root / "data/tables/Ascendancy.json").unlink()
        self.assertEqual(self.account_view()["Characters"][0]["Ascendancy"], "Witch3")
        with connect(self.path) as db:
            db.execute("UPDATE characters SET save='{}'")
        self.assertEqual(self.account_view()["Characters"][0]["Ascendancy"], "")

    def test_rename_moves_all_owned_league_and_guild_stashes_only(self):
        before = self.logical()
        self.change("Rename", Name="Renamed")
        after = self.logical()
        self.assertEqual({r[0] for r in after["stashes"]}, {"Renamed", "Renamed (Allflame)", "guild:Renamed", "guild:Renamed (Hardcore SSF Allflame)", "AlphaOther"})
        self.assertEqual(before["characters"], after["characters"])
        self.assertEqual(before["account_characters"], after["account_characters"])
        self.assertEqual(self.account_view()["Name"], "Renamed")

    def test_case_only_rename_is_supported(self):
        self.change("Rename", Name="aLPHA")
        self.assertEqual(self.account_view()["Name"], "aLPHA")

    def test_duplicate_full_copies_progress_and_stash_with_fresh_identities(self):
        original = self.logical()
        view = self.change("Duplicate", Name="Copy", Password="new password", IncludeCharacters=True, IncludeStash=True,
                           CharacterNames={self.char: "CopiedHero"})
        copy = next(a for a in view["Accounts"] if a["Name"] == "Copy")
        self.assertNotEqual(copy["Id"], self.account)
        self.assertNotEqual(copy["Characters"][0]["Id"], self.char)
        self.assertEqual(copy["Characters"][0]["Level"], 77)
        self.assertEqual(copy["Characters"][0]["Ascendancy"], "Elementalist")
        self.assertEqual(copy["StashTabs"], 4)
        with connect(self.path) as db:
            progress = json.loads(db.execute("SELECT save FROM characters WHERE name='CopiedHero'").fetchone()[0])
            source = json.loads(db.execute("SELECT save FROM characters WHERE name='OriginalHero'").fetchone()[0])
        self.assertIsNone(progress["VitalsOwner"])
        self.assertEqual(progress["FutureModData"], source["FutureModData"])
        self.assertEqual(progress["Inventories"], source["Inventories"])
        self.assertTrue(all(row in self.logical()["accounts"] for row in original["accounts"]))

    def test_duplicate_empty(self):
        view = self.change("Duplicate", Name="Empty", Password="another", IncludeCharacters=False, IncludeStash=False)
        copy = next(a for a in view["Accounts"] if a["Name"] == "Empty")
        self.assertEqual(copy["Characters"], [])
        self.assertEqual(copy["StashTabs"], 0)

    def test_password_matches_both_upstream_verifier_formats(self):
        password = "Тест 🔑 password"
        self.change("Password", Password=password)
        with connect(self.path) as db:
            account = json.loads(db.execute("SELECT account FROM accounts WHERE id=?", (self.account,)).fetchone()[0])
        salt = base64.b64decode(account["PasswordSalt"])
        expected = base64.b64encode(hashlib.pbkdf2_hmac("sha256", password.encode(), salt, 50000, 32)).decode()
        digest = hashlib.sha256(password.encode("utf-16le")).hexdigest().upper().encode()
        client = base64.b64encode(hashlib.pbkdf2_hmac("sha256", digest, salt, 50000, 32)).decode()
        self.assertEqual(account["PasswordHash"], expected)
        self.assertEqual(account["ClientDigestHash"], client)

    def test_delete_removes_owned_data_but_archives_progress(self):
        self.change("Delete", Name="Alpha")
        remaining = self.logical()
        self.assertEqual(len(remaining["accounts"]), 1)
        self.assertEqual(remaining["accounts"][0][0], self.other)
        self.assertEqual(remaining["account_characters"], [])
        self.assertEqual(remaining["characters"], [])
        self.assertEqual([r[0] for r in remaining["stashes"]], ["AlphaOther"])
        self.assertEqual(remaining["deleted_characters"][0][0], "OriginalHero")

    def test_stale_revision_rolls_back(self):
        revision = self.account_view()["Revision"]
        with connect(self.path) as db:
            db.execute("UPDATE characters SET save='{}'")
        before = self.logical()
        with self.assertRaisesRegex(data.AccountError, "changed after"):
            data.run(self.path, self.root, {"Action": "Delete", "AccountId": self.account, "Name": "Alpha", "ExpectedRevision": revision})
        self.assertEqual(before, self.logical())

    def test_collision_or_bad_confirmation_never_changes_data(self):
        for action, values in (("Rename", {"Name": "AlphaOther"}), ("Delete", {"Name": "alpha"}),
                               ("Duplicate", {"Name": "Copy", "Password": "abcd", "IncludeCharacters": True, "CharacterNames": {self.char: "OriginalHero"}})):
            with self.subTest(action=action):
                before = self.logical()
                with self.assertRaises(data.AccountError):
                    self.change(action, **values)
                self.assertEqual(before, self.logical())

    def test_unsupported_storage_and_credentials_are_read_only(self):
        with connect(self.path) as db:
            db.execute("CREATE TABLE future_account_links(account TEXT)")
        self.assertFalse(self.query()["Writable"])
        with self.assertRaises(data.AccountError):
            self.change("Delete", Name="Alpha")

    def test_unfamiliar_password_contract_does_not_write(self):
        (self.root / "src/GameServer.Core/Accounts/AccountService.cs").write_text("new credential format", encoding="utf-8")
        before = self.logical()
        with self.assertRaisesRegex(data.AccountError, "format is unfamiliar"):
            self.change("Password", Password="password")
        self.assertEqual(before, self.logical())

    def test_duplicate_rolls_back_if_late_stash_insert_fails(self):
        # The transaction must undo the inserted account/characters if a later write fails.
        original = data.change
        def fail_late(db, accounts, request, root):
            original(db, accounts, request, root)
            raise data.AccountError("simulated commit failure")
        before = self.logical()
        data.change = fail_late
        try:
            with self.assertRaises(data.AccountError):
                self.change("Duplicate", Name="Rollback", Password="password", IncludeCharacters=True,
                            IncludeStash=True, CharacterNames={self.char: "RollbackHero"})
        finally:
            data.change = original
        self.assertEqual(before, self.logical())

    def test_sql_names_are_parameters(self):
        before = self.logical()
        with self.assertRaises(data.AccountError):
            self.change("Rename", Name="'; DROP TABLE accounts; --")
        self.assertEqual(before, self.logical())

    def test_live_wal_browsing_and_writer_lock(self):
        with connect(self.path) as writer:
            writer.execute("PRAGMA journal_mode=WAL")
            writer.execute("BEGIN IMMEDIATE")
            self.assertTrue(self.query()["Writable"])
            before = self.logical()
            with self.assertRaisesRegex(sqlite3.OperationalError, "locked"):
                self.change("Delete", Name="Alpha")
            self.assertEqual(before, self.logical())

    def test_orphaned_destination_stash_is_never_overwritten(self):
        with connect(self.path) as db:
            db.execute("INSERT INTO stash_tabs VALUES('Copy',0,'[]')")
        before = self.logical()
        with self.assertRaisesRegex(data.AccountError, "retained stash"):
            self.change("Duplicate", Name="Copy", Password="password", IncludeCharacters=False, IncludeStash=True)
        self.assertEqual(before, self.logical())

    def test_pending_legacy_import_disables_writes(self):
        with connect(self.path) as db:
            db.execute("DELETE FROM meta")
        self.assertFalse(self.query()["Writable"])
        with self.assertRaises(data.AccountError):
            self.change("Rename", Name="Changed")

    def test_mismatched_identity_is_not_treated_as_an_empty_account(self):
        with connect(self.path) as db:
            db.execute("UPDATE accounts SET account='{}' WHERE id=?", (self.account,))
        with self.assertRaisesRegex(data.AccountError, "disagree"):
            self.query()

    def test_symlink_database_is_rejected(self):
        alias = self.root / "alias.db"
        try:
            alias.symlink_to(self.path)
        except OSError:
            self.skipTest("Creating symlinks is unavailable without changing Windows settings")
        with self.assertRaisesRegex(data.AccountError, "Redirected"):
            data.run(alias, self.root, {"Action": "List"})


# LocalExile's sql/base/ from the database split on (LocalExile/LocalExile#758), with the updates table its SqlUpdater keeps.
UPDATES = "CREATE TABLE updates(name TEXT PRIMARY KEY,hash TEXT NOT NULL,state TEXT NOT NULL,applied_utc TEXT NOT NULL,speed_ms INTEGER NOT NULL) STRICT;"
AUTH_SCHEMA = UPDATES + "CREATE TABLE accounts(id TEXT PRIMARY KEY,name TEXT NOT NULL UNIQUE COLLATE NOCASE,account TEXT NOT NULL) STRICT;"
CHARACTERS_SCHEMA = UPDATES + '''
    CREATE TABLE account_characters(id TEXT PRIMARY KEY,account_id TEXT NOT NULL,name TEXT NOT NULL UNIQUE COLLATE NOCASE,created_utc TEXT NOT NULL,character TEXT NOT NULL) STRICT;
    CREATE TABLE account_friends(owner TEXT NOT NULL,friend TEXT NOT NULL,PRIMARY KEY(owner,friend),CHECK(owner<>friend)) STRICT;
    CREATE TABLE characters(name TEXT PRIMARY KEY COLLATE NOCASE,save TEXT NOT NULL) STRICT;
    CREATE TABLE deleted_characters(name TEXT NOT NULL,deleted_utc TEXT NOT NULL,save TEXT NOT NULL) STRICT;
    CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT NOT NULL) STRICT;
    CREATE TABLE stash_tabs(account TEXT NOT NULL COLLATE NOCASE,tab INTEGER NOT NULL,items TEXT NOT NULL,PRIMARY KEY(account,tab)) STRICT;
    CREATE TABLE stashes(account TEXT PRIMARY KEY COLLATE NOCASE,save TEXT NOT NULL) STRICT;'''


class SplitLayoutTests(unittest.TestCase):
    """characters.db with the accounts in auth.db, as the servers keep them after the split."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="waypoint-split-")
        self.root = Path(self.temp.name)
        (self.root / "data").mkdir()
        self.characters, self.auth = self.root / "data/characters.db", self.root / "data/auth.db"
        self.account, self.other, self.char = (str(uuid.uuid4()) for _ in range(3))
        with connect(self.auth) as db:
            db.executescript(AUTH_SCHEMA)
            for identity, name in ((self.account, "Alpha"), (self.other, "Beta")):
                record = {"Id": identity, "Name": name, "PasswordHash": "h", "PasswordSalt": "s", "CreatedUtc": "2026-01-01T00:00:00Z"}
                db.execute("INSERT INTO accounts VALUES(?,?,?)", (identity, name, json.dumps(record)))
        with connect(self.characters) as db:
            db.executescript(CHARACTERS_SCHEMA + "INSERT INTO meta VALUES('accounts_imported','fixture');")
            char = {"Id": self.char, "AccountId": self.account, "Name": "Hero", "Class": 3, "Level": 12, "League": "Standard",
                    "AreaId": "1_1_1", "CreatedUtc": "2026-01-01T00:00:00Z", "LastPlayedUtc": "2026-01-02T00:00:00Z", "PortraitFrame": 2}
            db.execute("INSERT INTO account_characters VALUES(?,?,?,?,?)", (self.char, self.account, "Hero", char["CreatedUtc"], json.dumps(char)))
            db.execute("INSERT INTO characters VALUES(?,?)", ("Hero", json.dumps({"MainAscendancy": {"Selection": {"Family": "Normal", "Id": "Witch3"}}})))
            db.execute("INSERT INTO stashes VALUES(?,?)", ("Alpha", '{"Gold":1}'))
            db.execute("INSERT INTO stash_tabs VALUES(?,?,?)", ("Alpha", 0, "[]"))
            db.execute("INSERT INTO account_friends VALUES(?,?)", (self.account, self.other))
            db.execute("INSERT INTO account_friends VALUES(?,?)", (self.other, self.account))
        # The client tables are in client.db; data/tables/ comes only from a full extraction.
        with connect(self.root / "data/client.db") as db:
            db.execute("CREATE TABLE Ascendancy(_row INTEGER PRIMARY KEY, Id TEXT, Name TEXT, LeagueName TEXT)")
            db.execute("INSERT INTO Ascendancy VALUES(0,'Witch3','Elementalist','Harbinger')")

    def tearDown(self):
        self.temp.cleanup()

    def run_request(self, request):
        return data.run(self.characters, self.root, request, self.auth)

    def test_listing_and_changes_span_both_databases(self):
        before = (self.characters.read_bytes(), self.auth.read_bytes())
        view = self.run_request({"Action": "List"})
        self.assertEqual(before, (self.characters.read_bytes(), self.auth.read_bytes()))
        self.assertTrue(view["Writable"], view["Notice"])
        alpha = next(a for a in view["Accounts"] if a["Id"] == self.account)
        self.assertEqual(alpha["Characters"][0]["Ascendancy"], "Elementalist")
        alpha = next(a for a in self.run_request({"Action": "Rename", "AccountId": self.account, "ExpectedRevision": alpha["Revision"],
                                                  "Name": "Gamma"})["Accounts"] if a["Id"] == self.account)
        with connect(self.auth) as db:
            self.assertEqual(db.execute("SELECT name FROM accounts WHERE id=?", (self.account,)).fetchone()[0], "Gamma")
        with connect(self.characters) as db:
            self.assertEqual([r[0] for r in db.execute("SELECT account FROM stashes UNION ALL SELECT account FROM stash_tabs")], ["Gamma", "Gamma"])
        self.run_request({"Action": "Delete", "AccountId": self.account, "ExpectedRevision": alpha["Revision"], "Name": "Gamma"})
        with connect(self.auth) as db:
            self.assertEqual([r[0] for r in db.execute("SELECT name FROM accounts")], ["Beta"])
        with connect(self.characters) as db:
            for table in ("account_characters", "characters", "stashes", "stash_tabs", "account_friends"):
                self.assertEqual(db.execute('SELECT count(*) FROM "' + table + '"').fetchone()[0], 0, table)
            self.assertEqual(db.execute("SELECT name FROM deleted_characters").fetchone()[0], "Hero")

    def test_unfamiliar_split_layout_is_read_only(self):
        with connect(self.auth) as db:
            db.execute("CREATE TABLE future_sessions(id TEXT)")
        view = self.run_request({"Action": "List"})
        self.assertFalse(view["Writable"])
        alpha = next(a for a in view["Accounts"] if a["Id"] == self.account)
        with self.assertRaises(data.AccountError):
            self.run_request({"Action": "Delete", "AccountId": self.account, "ExpectedRevision": alpha["Revision"], "Name": "Alpha"})
        with connect(self.characters) as db:
            db.execute("CREATE TABLE accounts(id TEXT PRIMARY KEY,name TEXT,account TEXT)")
        with self.assertRaisesRegex(data.AccountError, "not supported"):
            self.run_request({"Action": "List"})


RELATIONAL_CHARACTERS_SCHEMA = UPDATES + '''
    CREATE TABLE account_characters(id TEXT PRIMARY KEY,account_id TEXT NOT NULL,name TEXT NOT NULL UNIQUE COLLATE NOCASE,class INTEGER NOT NULL,level INTEGER NOT NULL,league TEXT NOT NULL,area_id TEXT NOT NULL,created_utc TEXT NOT NULL,last_played_utc TEXT NOT NULL,last_town TEXT,portrait INTEGER NOT NULL DEFAULT 0,portrait_frame INTEGER NOT NULL DEFAULT 0,hardcore_death_league TEXT,hardcore_death_utc TEXT,hardcore_death_permanent INTEGER) STRICT;
    CREATE TABLE account_characters_json(id TEXT PRIMARY KEY,account_id TEXT NOT NULL,name TEXT NOT NULL UNIQUE COLLATE NOCASE,created_utc TEXT NOT NULL,character TEXT NOT NULL) STRICT;
    CREATE TABLE account_friends(owner TEXT NOT NULL,friend TEXT NOT NULL,PRIMARY KEY(owner,friend),CHECK(owner<>friend)) STRICT;
    CREATE TABLE characters(name TEXT PRIMARY KEY COLLATE NOCASE,save TEXT NOT NULL) STRICT;
    CREATE TABLE deleted_characters(name TEXT NOT NULL,deleted_utc TEXT NOT NULL,save TEXT NOT NULL) STRICT;
    CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT NOT NULL) STRICT;
    CREATE TABLE stash_tabs(account TEXT NOT NULL COLLATE NOCASE,tab INTEGER NOT NULL,items TEXT NOT NULL,PRIMARY KEY(account,tab)) STRICT;
    CREATE TABLE stashes(account TEXT PRIMARY KEY COLLATE NOCASE,save TEXT NOT NULL) STRICT;
    CREATE TABLE character_saves(id INTEGER PRIMARY KEY,name TEXT NOT NULL COLLATE NOCASE,deleted_utc TEXT,has_inventories INTEGER NOT NULL DEFAULT 1,vitals_character TEXT,vitals_session TEXT,vitals_retired INTEGER,future_col TEXT) STRICT;
    CREATE UNIQUE INDEX character_saves_name ON character_saves(name) WHERE deleted_utc IS NULL;
    CREATE TABLE character_ascendancies(save INTEGER PRIMARY KEY REFERENCES character_saves(id),family TEXT NOT NULL,ascendancy TEXT NOT NULL,revision TEXT) STRICT;
    CREATE TABLE character_module_data(save INTEGER NOT NULL REFERENCES character_saves(id),key TEXT NOT NULL,value TEXT NOT NULL,PRIMARY KEY(save,key)) STRICT;
    CREATE TABLE stash_saves(id INTEGER PRIMARY KEY,account TEXT NOT NULL UNIQUE COLLATE NOCASE,last_tab INTEGER NOT NULL DEFAULT 0,next_item_id INTEGER NOT NULL DEFAULT 1,gold INTEGER NOT NULL DEFAULT 0) STRICT;
    CREATE TABLE stash_tab_headers(stash INTEGER NOT NULL REFERENCES stash_saves(id),tab INTEGER NOT NULL,name TEXT NOT NULL,hidden INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(stash,tab)) STRICT;
    CREATE TABLE items(id INTEGER PRIMARY KEY,character INTEGER REFERENCES character_saves(id),stash INTEGER REFERENCES stash_saves(id),inventory INTEGER,role TEXT NOT NULL,parent INTEGER REFERENCES items(id),ord INTEGER NOT NULL,type INTEGER NOT NULL,CHECK((character IS NULL)<>(stash IS NULL))) STRICT;
    CREATE TABLE item_mods(item INTEGER NOT NULL REFERENCES items(id),ord INTEGER NOT NULL,mod TEXT NOT NULL,section INTEGER NOT NULL,stat_values TEXT NOT NULL,PRIMARY KEY(item,ord)) STRICT;
    CREATE TABLE item_skin_transfers(target INTEGER PRIMARY KEY REFERENCES items(id),source TEXT NOT NULL) STRICT;'''


class RelationalLayoutTests(unittest.TestCase):
    """Relational characters.db with accounts in auth.db (2026_11_11_181109_relational_saves.sql)."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="waypoint-relational-")
        self.root = Path(self.temp.name)
        (self.root / "data").mkdir()
        service = self.root / "src/GameServer.Core/Accounts/AccountService.cs"
        service.parent.mkdir(parents=True)
        service.write_text("Pbkdf2(pw, Convert.FromBase64String(salt), 50_000, HashAlgorithmName.SHA256, 32)\n"
                           "SHA256.HashData(Encoding.Unicode.GetBytes(password))\nHash(Convert.ToHexString(digest), salt)", encoding="utf-8")
        self.characters, self.auth = self.root / "data/characters.db", self.root / "data/auth.db"
        self.account, self.other, self.char = (str(uuid.uuid4()) for _ in range(3))
        with connect(self.auth) as db:
            db.executescript(AUTH_SCHEMA)
            for identity, name in ((self.account, "Alpha"), (self.other, "Beta")):
                record = {"Id": identity, "Name": name, "PasswordHash": "private-verifier", "PasswordSalt": "private-salt", "CreatedUtc": "2026-01-01T00:00:00Z"}
                db.execute("INSERT INTO accounts VALUES(?,?,?)", (identity, name, json.dumps(record)))
        with connect(self.characters) as db:
            db.executescript(RELATIONAL_CHARACTERS_SCHEMA +
                             "INSERT INTO meta VALUES('accounts_imported','fixture'),('relational_saves','1');")
            db.execute("INSERT INTO account_characters VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                       (self.char, self.account, "Hero", 3, 77, "Standard", "1_1_1",
                        "2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z", "1_1_town", 0, 2, None, None, None))
            # Deleted save with the same name (different casing) and an older ascendancy must not shadow the active save.
            old_save = db.execute("INSERT INTO character_saves(name,deleted_utc,has_inventories) VALUES(?,?,1)",
                                  ("hero", "2025-12-31T00:00:00Z")).lastrowid
            db.execute("INSERT INTO character_ascendancies VALUES(?,?,?,?)", (old_save, "Normal", "Witch1", "3.29.3c"))
            save_id = db.execute("INSERT INTO character_saves(name,deleted_utc,has_inventories,vitals_character,vitals_session,vitals_retired,future_col) VALUES(?,NULL,1,?,?,0,?)",
                                 ("Hero", self.char, str(uuid.uuid4()), "keep-me")).lastrowid
            db.execute("INSERT INTO character_ascendancies VALUES(?,?,?,?)", (save_id, "Normal", "Witch3", "3.29.3c"))
            db.execute("INSERT INTO character_module_data VALUES(?,?,?)", (save_id, "Core/Game", '{"x":1}'))
            body_id = db.execute("INSERT INTO items(character,stash,inventory,role,parent,ord,type) VALUES(?,NULL,1,'placed',NULL,0,100)", (save_id,)).lastrowid
            gem_id = db.execute("INSERT INTO items(character,stash,inventory,role,parent,ord,type) VALUES(?,NULL,1,'socketed',?,0,200)", (save_id, body_id)).lastrowid
            db.execute("INSERT INTO item_mods VALUES(?,?,?,?,?)", (body_id, 0, "ModA", 0, "[1]"))
            db.execute("INSERT INTO item_skin_transfers VALUES(?,?)", (body_id, '{"Type":999}'))
            self.assertGreater(gem_id, body_id)
            for acct_key in ("Alpha", "Alpha (Allflame)", "guild:Alpha", "Beta"):
                sid = db.execute("INSERT INTO stash_saves(account,last_tab,next_item_id,gold) VALUES(?,0,2,50)", (acct_key,)).lastrowid
                db.execute("INSERT INTO stash_tab_headers VALUES(?,0,'Main',0)", (sid,))
                if acct_key == "Alpha":
                    db.execute("INSERT INTO stash_tab_headers VALUES(?,1,'DeletedFolder',1)", (sid,))
                    s_item = db.execute("INSERT INTO items(character,stash,inventory,role,parent,ord,type) VALUES(NULL,?,0,'placed',NULL,0,300)", (sid,)).lastrowid
                    db.execute("INSERT INTO item_mods VALUES(?,?,?,?,?)", (s_item, 0, "StashMod", 0, "[7]"))
            db.execute("INSERT INTO account_friends VALUES(?,?)", (self.account, self.other))
        with connect(self.root / "data/client.db") as db:
            db.execute("CREATE TABLE Ascendancy(_row INTEGER PRIMARY KEY, Id TEXT, Name TEXT, LeagueName TEXT)")
            db.execute("INSERT INTO Ascendancy VALUES(0,'Witch1','Occultist','BogShaman'),(1,'Witch3','Elementalist','Harbinger')")

    def tearDown(self):
        self.temp.cleanup()

    def run_request(self, request):
        return data.run(self.characters, self.root, request, self.auth)

    def test_relational_list_rename_duplicate_and_delete(self):
        before = (self.characters.read_bytes(), self.auth.read_bytes())
        view = self.run_request({"Action": "List"})
        self.assertEqual(before, (self.characters.read_bytes(), self.auth.read_bytes()))
        self.assertTrue(view["Writable"], view["Notice"])
        self.assertTrue(view["CanRegister"])
        serialized = json.dumps(view)
        self.assertNotIn("private-verifier", serialized)
        self.assertNotIn("private-salt", serialized)
        alpha = next(a for a in view["Accounts"] if a["Id"] == self.account)
        self.assertEqual(alpha["Characters"][0]["Ascendancy"], "Elementalist")
        self.assertEqual(alpha["Characters"][0]["Level"], 77)
        self.assertEqual(alpha["StashTabs"], 3)

        # Duplicate with characters, nested socketed items, mods, skin transfers, and stash.
        dup_view = self.run_request({
            "Action": "Duplicate", "AccountId": self.account, "ExpectedRevision": alpha["Revision"],
            "Name": "Copy", "Password": "new-password", "IncludeCharacters": True, "IncludeStash": True,
            "CharacterNames": {self.char: "CopiedHero"},
        })
        copy = next(a for a in dup_view["Accounts"] if a["Name"] == "Copy")
        self.assertEqual(copy["Characters"][0]["Ascendancy"], "Elementalist")
        self.assertEqual(copy["StashTabs"], 3)
        with connect(self.characters) as db:
            row = db.execute("SELECT id, vitals_character, vitals_session, future_col FROM character_saves WHERE name='CopiedHero' AND deleted_utc IS NULL").fetchone()
            self.assertIsNotNone(row)
            self.assertIsNone(row[1])
            self.assertIsNone(row[2])
            self.assertEqual(row[3], "keep-me")
            items = db.execute("SELECT id, role, parent FROM items WHERE character=? ORDER BY id", (row[0],)).fetchall()
            self.assertEqual(len(items), 2)
            self.assertEqual(items[1][2], items[0][0])
            self.assertEqual(db.execute("SELECT count(*) FROM item_mods WHERE item=?", (items[0][0],)).fetchone()[0], 1)
            self.assertEqual(db.execute("SELECT count(*) FROM item_skin_transfers WHERE target=?", (items[0][0],)).fetchone()[0], 1)

        # Rename moves all owned stashes.
        alpha = next(a for a in dup_view["Accounts"] if a["Id"] == self.account)
        renamed_view = self.run_request({
            "Action": "Rename", "AccountId": self.account, "ExpectedRevision": alpha["Revision"], "Name": "Gamma",
        })
        with connect(self.characters) as db:
            stashes = {r[0] for r in db.execute("SELECT account FROM stash_saves")}
            self.assertTrue({"Gamma", "Gamma (Allflame)", "guild:Gamma", "Copy", "Beta"} <= stashes)

        # Delete archives character_saves via deleted_utc and removes account/stash/friends.
        gamma = next(a for a in renamed_view["Accounts"] if a["Id"] == self.account)
        self.run_request({"Action": "Delete", "AccountId": self.account, "ExpectedRevision": gamma["Revision"], "Name": "Gamma"})
        with connect(self.characters) as db:
            self.assertEqual(db.execute("SELECT count(*) FROM account_characters WHERE account_id=?", (self.account,)).fetchone()[0], 0)
            self.assertIsNotNone(db.execute("SELECT deleted_utc FROM character_saves WHERE name='Hero'").fetchone()[0])
            self.assertEqual(db.execute("SELECT count(*) FROM stash_saves WHERE account LIKE 'Gamma%' OR account LIKE 'guild:Gamma%'").fetchone()[0], 0)
            self.assertEqual(db.execute("SELECT count(*) FROM account_friends").fetchone()[0], 0)

    def test_relational_missing_migration_or_unknown_table_is_read_only(self):
        # Fresh relational DB created by GameServer.Host (accounts_imported set, no legacy JSON rows) stays writable
        # before Gateway first runs SaveRows.Migrate.
        with connect(self.characters) as db:
            db.execute("DELETE FROM meta WHERE key='relational_saves'")
        fresh = self.run_request({"Action": "List"})
        self.assertTrue(fresh["Writable"])
        self.assertTrue(fresh["CanRegister"])

        # Pending migration (unmigrated legacy JSON row present without relational_saves marker) degrades to read-only
        # and refuses writes without touching either database file.
        with connect(self.characters) as db:
            db.execute("INSERT INTO characters VALUES('LegacyHero','{}')")
        ro = self.run_request({"Action": "List"})
        self.assertFalse(ro["Writable"])
        alpha = next(a for a in ro["Accounts"] if a["Id"] == self.account)
        before = (self.characters.read_bytes(), self.auth.read_bytes())
        with self.assertRaises(data.AccountError):
            self.run_request({"Action": "Rename", "AccountId": self.account, "ExpectedRevision": alpha["Revision"], "Name": "Blocked"})
        self.assertEqual(before, (self.characters.read_bytes(), self.auth.read_bytes()))

        with connect(self.characters) as db:
            db.execute("DELETE FROM characters")
            db.execute("INSERT INTO meta VALUES('relational_saves','1')")
            db.execute("CREATE TABLE future_unknown(id INTEGER PRIMARY KEY)")
        self.assertFalse(self.run_request({"Action": "List"})["Writable"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
