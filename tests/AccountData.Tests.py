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


if __name__ == "__main__":
    unittest.main(verbosity=2)
