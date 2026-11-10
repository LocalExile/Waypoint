"""League configuration integration on disposable files only. No game processes or production saves."""
from contextlib import closing
import importlib.util
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
import LeagueData as data
from AccountData import SCHEMA


class LeagueDataTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="Waypoint-league-")
        self.root = Path(self.temp.name)
        self.modules, self.leagues = self.root / "Modules", self.root / "Leagues"
        self.choices, self.database, self.history = self.root / "data/league-modules.json", self.root / "data/saves.db", self.root / "history"
        for path, dependencies in (("Core/Game", []), ("Core/Vendors", ["Game"]), ("Core/Harvest", ["Game"]),
                                   ("Survivors/Skills", ["Game"]), ("Survivors/Run", ["Skills"]), ("Extra/X", ["Game"]), ("Extra/Y", ["Game"])):
            self.write(self.modules / path / "module.json", {"id": path.split("/")[-1], "depends": dependencies})
        self.profile("Standard", ["Core/Game", "Core/Vendors", "Core/Harvest"], base="Standard")
        self.profile("SSF Standard", ["Core/Game", "Core/Vendors"], base="Standard", ssf=True, entryOf="Standard", entryKind="ssf")
        self.profile("Hardcore", ["Core/Game", "Core/Vendors"], base="Standard", hardcore=True, deathLeague="Standard")
        self.profile("Survivors", ["Core/Game", "Survivors/Skills", "Survivors/Run"], base="Survivors")
        self.profile("Void", [], creatable=False)

    def tearDown(self):
        self.temp.cleanup()

    def write(self, path, value):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value), encoding="utf-8")

    def profile(self, name, modules, **flags):
        folder = self.leagues / name
        self.write(folder / "league.json", {"name": name, "parent": None if name == "Void" else "Void", "description": "Fixture profile.", **flags})
        self.write(folder / "modules.json", {"modules": modules})
        (folder / "Source").mkdir()
        (folder / "Source/Fixture.cs").write_text("// marker\npublic sealed class FixtureLeague : League { }", encoding="utf-8")

    def review(self):
        return data.review(self.modules, self.leagues, self.choices)

    def request(self, template="Standard", name="My League"):
        return {"template": template, "name": name, "description": "My custom league.",
                "modules": data.read_json(self.leagues / template / "modules.json")["modules"], "review": self.review()}

    def run_request(self, request, action="CreateLeague", apply=True):
        data.run(self.modules, self.leagues, self.choices, self.database, self.history, action, request, apply)

    def run_cli(self, payload, action="CreateLeague", phase="apply"):
        return subprocess.run([sys.executable, str(Path(data.__file__)), *map(str, (
            self.modules, self.leagues, self.choices, self.database, self.history)), action, phase],
            input=payload, capture_output=True, env=dict(os.environ, PYTHONIOENCODING="utf-8"), check=False)

    def test_cli_runs_under_a_python_that_does_not_look_beside_the_script(self):
        # A release's bundled Python is the embeddable one, which leaves the script's folder off its module path; -I does
        # the same to this Python. The helper finds AccountData beside itself all the same.
        completed = subprocess.run([sys.executable, "-I", str(Path(data.__file__)), *map(str, (
            self.modules, self.leagues, self.choices, self.database, self.history)), "Inspect", "check"],
            input=b"{}", capture_output=True, cwd=self.root)
        self.assertEqual(completed.returncode, 0, completed.stderr.decode("utf-8", "replace"))
        self.assertTrue(json.loads(completed.stdout)["Ok"])

    def test_cli_accepts_utf8_requests_with_or_without_a_bom_and_preserves_unicode(self):
        for prefix, name in ((b"", "Plain Event"), (b"\xef\xbb\xbf", "BOM Event")):
            with self.subTest(bom=bool(prefix)):
                request = self.request(name=name)
                request["description"] = "Событие · 自定义赛季"
                result = self.run_cli(prefix + json.dumps(request, ensure_ascii=False).encode("utf-8"))
                reply = json.loads(result.stdout)
                self.assertEqual(result.returncode, 0, reply)
                self.assertTrue(reply["Ok"])
                self.assertEqual(data.read_json(self.leagues / name / "league.json")["description"], request["description"])

    def test_cli_bom_support_keeps_duplicate_and_malformed_json_rejected(self):
        before = self.review()
        for payload in (b'{"review":{},"review":{}}', b'{"broken":', b'\xef\xbb\xbf{}'):
            with self.subTest(payload=payload):
                result = self.run_cli(b"\xef\xbb\xbf" + payload, action="Inspect", phase="check")
                self.assertEqual(result.returncode, 1)
                self.assertFalse(json.loads(result.stdout)["Ok"])
        self.assertEqual(self.review(), before)
        self.assertFalse(self.history.exists())

    def database_fixture(self):
        self.database.parent.mkdir(exist_ok=True)
        with closing(sqlite3.connect(self.database)) as db:
            for table, columns in SCHEMA.items():
                definitions = []
                primary = {"accounts": ["id"], "account_characters": ["id"], "characters": ["name"],
                           "stashes": ["account"], "stash_tabs": ["account", "tab"], "meta": ["key"]}.get(table, [])
                for column in columns:
                    definitions.append('"' + column + '" ' + ("INTEGER" if column == "tab" else "TEXT"))
                if primary:
                    definitions.append("PRIMARY KEY(" + ",".join(primary) + ")")
                db.execute('CREATE TABLE "' + table + '" (' + ",".join(definitions) + ")")
            db.execute("INSERT INTO meta VALUES ('accounts_imported','1')")
            db.commit()

    def test_creation_publishes_complete_isolated_league_and_preserves_templates(self):
        before = (self.leagues / "Standard/league.json").read_bytes()
        request = self.request()
        self.run_request(request, apply=False)
        self.assertFalse((self.leagues / "My League").exists())
        self.run_request(request)
        created = data.read_json(self.leagues / "My League/league.json")
        self.assertEqual(created["name"], "My League")
        self.assertEqual(created["parent"], "Void")
        self.assertNotIn("entryOf", created)
        self.assertEqual((self.leagues / "My League/Source/WaypointLeague.cs").read_text(), data.EMPTY_SOURCE)
        self.assertEqual(before, (self.leagues / "Standard/league.json").read_bytes())
        self.assertFalse(self.database.exists())

    def test_ssf_and_hardcore_modes_are_copied_only_from_defined_profiles(self):
        self.run_request(self.request("SSF Standard", "My SSF"))
        created = data.read_json(self.leagues / "My SSF/league.json")
        self.assertTrue(created["ssf"])
        self.assertNotIn("entryOf", created)
        self.run_request(self.request("Hardcore", "My Hardcore"))
        self.assertEqual(data.read_json(self.leagues / "My Hardcore/league.json")["deathLeague"], "Void")
        request = self.request("Survivors", "SSF Survivors")
        request["ssf"] = True
        with self.assertRaisesRegex(data.AccountError, "Modes must"):
            self.run_request(request)

    def test_isolated_void_can_keep_only_its_core_game_foundation(self):
        self.write(self.leagues / "Void/modules.json", {"modules": ["Core/Game"]})
        self.run_request(self.request())
        self.assertEqual(data.read_json(self.leagues / "My League/league.json")["parent"], "Void")
        self.write(self.leagues / "Void/modules.json", {"modules": ["Core/Game", "Core/Vendors"]})
        with self.assertRaisesRegex(data.AccountError, "Void"):
            self.run_request(self.request(name="Other League"))

    def standard_as_shipped(self):
        # As LocalExile ships it, Standard has no parent, and a league that leads to it is not isolated. The other
        # leagues of this fixture lead to Void, as Survivors does.
        self.write(self.leagues / "Standard/league.json", {"name": "Standard", "parent": None, "description": "Fixture profile.", "base": "Standard"})

    def choose(self, league, add=(), remove=()):
        current = data.read_json(self.choices) if self.choices.exists() else {}
        return {"league": league, "review": self.review(), "choices": {**current, league: {"add": list(add), "remove": list(remove)}}}

    def test_any_module_goes_with_any_league_but_one_made_for_an_isolated_league(self):
        self.standard_as_shipped()
        self.profile("Ruthless", ["Core/Game", "Core/Vendors", "Extra/X"], base="Standard", parent="Standard", form="ruthless")
        # A core module the league does not ship with, and another league's own module: a module has no knowledge of
        # leagues, and any league can list it.
        self.run_request(self.choose("Ruthless", ["Core/Harvest"]), action="SaveLeagueModules")
        self.run_request(self.choose("Standard", ["Extra/X"]), action="SaveLeagueModules")
        self.run_request(self.choose("Survivors", ["Core/Vendors", "Extra/X", "Extra/Y"]), action="SaveLeagueModules")
        saved = data.read_json(self.choices)
        self.assertEqual((saved["Ruthless"]["add"], saved["Standard"]["add"]), (["Core/Harvest"], ["Extra/X"]))
        # A module LocalExile ships only in an isolated league stays out of the leagues that lead to Standard...
        for league in ("Standard", "Ruthless"):
            with self.assertRaisesRegex(data.AccountError, "isolated"):
                self.run_request(self.choose(league, ["Survivors/Skills"]), action="SaveLeagueModules")
        # ...goes with another isolated league, and with a new league, which is isolated whatever it is made from.
        self.run_request(self.choose("Hardcore", ["Survivors/Skills"]), action="SaveLeagueModules")
        request = self.request()
        request["modules"].append("Survivors/Skills")
        self.run_request(request)
        # What a module needs has to be on.
        with self.assertRaisesRegex(data.AccountError, "needs"):
            self.run_request(self.choose("Hardcore", ["Survivors/Run"]), action="SaveLeagueModules")

    def test_a_module_no_shipped_league_lists_is_an_add_on_that_is_never_required(self):
        # Extra/X is in no league's list: an add-on, as PlayerBots is. LocalExile's own guide turns it on this way.
        shipped = (self.leagues / "Standard/modules.json").read_text(encoding="utf-8")
        self.run_request(self.choose("Standard", ["Extra/X"]), action="SaveLeagueModules")
        self.assertEqual(data.read_json(self.choices)["Standard"]["add"], ["Extra/X"])
        self.assertEqual(shipped, (self.leagues / "Standard/modules.json").read_text(encoding="utf-8"))
        # A league made in Waypoint with an add-on ticked lists it in its own file, and can still untick it.
        self.profile("My Bots", ["Core/Game", "Core/Vendors", "Extra/X"], base="Standard")
        self.write(self.leagues / "My Bots/.waypoint-league.json", {"version": 1, "template": "Standard"})
        self.run_request(self.choose("My Bots", remove=["Extra/X"]), action="SaveLeagueModules")
        self.assertEqual(data.read_json(self.choices)["My Bots"]["remove"], ["Extra/X"])
        # Its dependencies still count.
        self.write(self.modules / "Extra/Needy/module.json", {"id": "Needy", "depends": ["Harvest"]})
        with self.assertRaisesRegex(data.AccountError, "needs"):
            self.run_request(self.choose("Survivors", ["Extra/Needy"]), action="SaveLeagueModules")

    def test_a_league_that_lists_a_module_that_is_not_installed_can_still_be_edited(self):
        # A typo in a hand edit, or a module taken away since: LocalExile logs it and loads the league without it.
        self.write(self.leagues / "Standard/modules.json", {"modules": ["Core/Game", "Core/Vendors", "Core/Harvest", "PlayerBot"]})
        # Another change goes through with the entry left as it is, and the entry itself can be taken off the list.
        self.run_request(self.choose("Standard", remove=["Core/Harvest"]), action="SaveLeagueModules")
        self.run_request(self.choose("Standard", remove=["PlayerBot"]), action="SaveLeagueModules")
        self.assertEqual(data.read_json(self.choices)["Standard"]["remove"], ["PlayerBot"])
        # What the user's saved choices added earlier counts as listed too; a new one that is not installed is refused.
        self.write(self.choices, {"Standard": {"add": ["Gone"], "remove": []}})
        self.run_request(self.choose("Standard", ["Gone", "Extra/X"]), action="SaveLeagueModules")
        with self.assertRaisesRegex(data.AccountError, "missing"):
            self.run_request(self.choose("Standard", ["Gone", "Nowhere"]), action="SaveLeagueModules")
        # A new league starts from installed modules only.
        with self.assertRaisesRegex(data.AccountError, "missing"):
            self.run_request(self.request())

    def test_hidden_profiles_remain_valid_for_customization_and_existing_characters(self):
        self.run_request(self.request())
        self.run_request(self.visibility("Standard", False), action="SetLeagueVisibility")
        request = {"league": "My League", "review": self.review(), "choices": {"My League": {"add": [], "remove": ["Core/Harvest"]}}}
        self.run_request(request, action="SaveLeagueModules")
        self.assertEqual(data.read_json(self.choices)["My League"]["remove"], ["Core/Harvest"])
        self.run_request(self.request("SSF Standard", "New Solo League"))

    def test_the_game_cannot_be_removed_and_a_leagues_own_bundle_can(self):
        # The game is what LocalExile requires.
        request = self.request("Standard")
        request["modules"].remove("Core/Game")
        with self.assertRaisesRegex(data.AccountError, "Game"):
            self.run_request(request)
        with self.assertRaisesRegex(data.AccountError, "Game"):
            self.run_request(self.choose("Standard", remove=["Core/Game"]), action="SaveLeagueModules")
        # A league's own bundle can be left out: LocalExile loads the league without it and keeps its saved data. The
        # interface asks first; here the choice is simply taken. What a module needs still has to be on.
        self.run_request(self.choose("Survivors", remove=["Survivors/Run"]), action="SaveLeagueModules")
        self.assertEqual(data.read_json(self.choices)["Survivors"]["remove"], ["Survivors/Run"])
        with self.assertRaisesRegex(data.AccountError, "needs"):
            self.run_request(self.choose("Survivors", remove=["Survivors/Skills"]), action="SaveLeagueModules")
        request = self.request("Survivors")
        request["modules"].remove("Survivors/Run")
        self.run_request(request)

    def test_dependency_errors_and_cycles_block_writes(self):
        self.write(self.modules / "Core/Harvest/module.json", {"id": "Harvest", "depends": ["Vendors"]})
        request = self.request()
        request["modules"].remove("Core/Vendors")
        with self.assertRaisesRegex(data.AccountError, "needs Vendors"):
            self.run_request(request)
        self.write(self.modules / "Core/Vendors/module.json", {"id": "Vendors", "depends": ["Harvest"]})
        with self.assertRaisesRegex(data.AccountError, "cycle"):
            self.run_request(self.request())

    def test_stale_review_and_new_conflicting_definition_block_creation(self):
        request = self.request()
        self.profile("Additional", ["Core/Game"], base="Standard")
        with self.assertRaisesRegex(data.AccountError, "changed"):
            self.run_request(request)
        request = self.request()
        (self.leagues / "Standard/Source/Fixture.cs").write_text("public sealed class Changed : League {}")
        with self.assertRaisesRegex(data.AccountError, "changed"):
            self.run_request(request)

    def test_existing_folders_paths_and_reserved_names_are_never_overwritten(self):
        for name in ("../outside", "Standard", "standard", "CON", "Event.", " Event", "A/B", "Void", "My\nLeague"):
            with self.subTest(name=name), self.assertRaises(data.AccountError):
                self.run_request(self.request(name=name))
        (self.leagues / "My League").mkdir()
        with self.assertRaises(data.AccountError):
            self.run_request(self.request())

    def test_unreviewed_custom_code_and_prebuilt_templates_are_not_copied(self):
        source = self.leagues / "Standard/Source/Fixture.cs"
        source.write_text("public sealed class Fixture : League { public override string ToString() => \"unsafe assumption\"; }")
        with self.assertRaisesRegex(data.AccountError, "custom code"):
            self.run_request(self.request())
        source.unlink()
        with self.assertRaises(data.AccountError):
            self.run_request(self.request())

    def test_retained_characters_and_stashes_reserve_removed_league_names(self):
        self.database_fixture()
        before = self.database.read_bytes()
        self.run_request(self.request(), apply=False)
        self.assertEqual(before, self.database.read_bytes())
        for table, column in (("characters", "save"), ("deleted_characters", "save"), ("account_characters", "character")):
            with closing(sqlite3.connect(self.database)) as db:
                db.execute('INSERT INTO "' + table + '" (name,"' + column + '") VALUES (?,?)', ("Fixture", json.dumps({"League": "My League"})))
                db.commit()
            with self.assertRaisesRegex(data.AccountError, "retained characters"):
                self.run_request(self.request())
            with closing(sqlite3.connect(self.database)) as db:
                db.execute('DELETE FROM "' + table + '"')
                db.commit()
        with closing(sqlite3.connect(self.database)) as db:
            db.execute("INSERT INTO stash_tabs VALUES ('guild:Fixture (My League)',0,'[]')")
            db.commit()
        with self.assertRaisesRegex(data.AccountError, "retained stash"):
            self.run_request(self.request())

    def test_unknown_save_schema_and_pending_import_fail_closed(self):
        self.database_fixture()
        with closing(sqlite3.connect(self.database)) as db:
            db.execute("DELETE FROM meta")
            db.commit()
        with self.assertRaisesRegex(data.AccountError, "save layout"):
            self.run_request(self.request())

    def test_failed_creation_leaves_no_partially_published_league(self):
        request = self.request()
        with patch.object(data.os, "link", side_effect=OSError("fixture link failure")), self.assertRaises(OSError):
            self.run_request(request)
        self.assertFalse((self.leagues / "My League").exists())
        self.assertTrue((self.leagues / "Standard/league.json").is_file())

    def test_membership_edit_preserves_other_leagues_and_keeps_previous_configuration(self):
        self.write(self.choices, {"Future": {"add": [], "remove": []}})
        request = {"league": "Standard", "review": self.review(), "choices": {"Future": {"add": [], "remove": []},
                   "Standard": {"add": [], "remove": ["Core/Harvest"]}}}
        self.run_request(request, action="SaveLeagueModules")
        self.assertIn("Future", data.read_json(self.choices))
        self.assertEqual(len(list(self.history.rglob("league-modules.json"))), 1)
        self.assertTrue(data.read_json(self.leagues / "Standard/modules.json")["modules"])
        request["review"] = self.review()
        request["choices"]["Future"] = {"add": ["Core/Vendors"], "remove": []}
        with self.assertRaisesRegex(data.AccountError, "another league"):
            self.run_request(request, action="SaveLeagueModules")

    def test_jsonc_and_string_comment_markers_are_read_correctly(self):
        file = self.leagues / "Standard/league.json"
        file.write_text('{// comment\n"name":"Standard", "description":"https://example.com/,}", /* block */ "base":"Standard",}')
        self.assertEqual(data.read_json(file)["description"], "https://example.com/,}")
        self.run_request(self.request())

    def test_broken_parent_chains_and_nonisolated_void_are_rejected(self):
        file = self.leagues / "Standard/league.json"
        definition = data.read_json(file)
        definition["parent"] = "Standard"
        self.write(file, definition)
        with self.assertRaises(data.AccountError):
            self.run_request(self.request())
        definition["parent"] = None
        self.write(file, definition)
        self.write(self.leagues / "Void/league.json", {"name": "Void", "parent": "Standard", "creatable": False})
        with self.assertRaisesRegex(data.AccountError, "Void"):
            self.run_request(self.request())

    def test_profile_defaults_keep_the_original_family_when_base_is_omitted(self):
        definition = data.read_json(self.leagues / "Standard/league.json")
        definition.pop("base")
        self.write(self.leagues / "Standard/league.json", definition)
        self.run_request(self.request())
        self.assertEqual(data.read_json(self.leagues / "My League/league.json")["base"], "Standard")

    def test_redirected_content_paths_are_refused(self):
        target = self.root / "outside"
        target.mkdir()
        link = self.modules / "Redirected"
        try:
            link.symlink_to(target, target_is_directory=True)
        except OSError:
            self.skipTest("Creating symlinks is not permitted on this host.")
        with self.assertRaisesRegex(data.AccountError, "Redirected"):
            self.request()

    def visibility(self, name, visible):
        return {"league": name, "visible": visible, "review": self.review()}

    def test_hiding_a_card_hides_linked_modes_and_preserves_existing_save_data(self):
        self.database.parent.mkdir(exist_ok=True)
        self.database.write_bytes(b"existing saves must not be opened for visibility")
        before = self.database.read_bytes()
        self.run_request(self.visibility("Standard", False), action="SetLeagueVisibility")
        self.assertFalse(data.read_json(self.leagues / "Standard/league.json")["creatable"])
        self.assertFalse(data.read_json(self.leagues / "SSF Standard/league.json")["creatable"])
        self.assertNotIn("ended", data.read_json(self.leagues / "Standard/league.json"))
        self.assertEqual(before, self.database.read_bytes())
        self.run_request(self.visibility("SSF Standard", True), action="SetLeagueVisibility")
        self.assertTrue(data.read_json(self.leagues / "Standard/league.json")["creatable"])
        self.assertTrue(data.read_json(self.leagues / "SSF Standard/league.json")["creatable"])

    def test_the_last_creation_card_and_orphaned_mode_options_cannot_be_published(self):
        self.run_request(self.visibility("Survivors", False), action="SetLeagueVisibility")
        self.run_request(self.visibility("Hardcore", False), action="SetLeagueVisibility")
        with self.assertRaisesRegex(data.AccountError, "at least one"):
            self.run_request(self.visibility("Standard", False), action="SetLeagueVisibility")
        self.assertTrue(data.read_json(self.leagues / "Standard/league.json").get("creatable", True))
        self.write(self.leagues / "SSF Standard/league.json", {"name": "SSF Standard", "base": "Standard", "parent": "Standard", "entryOf": "missing", "entryKind": "ssf", "creatable": False})
        with self.assertRaisesRegex(data.AccountError, "parent"):
            self.run_request(self.visibility("SSF Standard", True), action="SetLeagueVisibility")

    def test_visibility_preserves_comments_and_changes_only_the_top_level_flag(self):
        file = self.leagues / "Standard/league.json"
        text = '{\r\n // keep this\r\n "name":"Standard", "base":"Standard", "description":"creatable", "nested":{"creatable":true}, "creatable": true,\r\n}'
        file.write_bytes(text.encode("utf-8"))
        self.run_request(self.visibility("Standard", False), action="SetLeagueVisibility")
        self.assertEqual(file.read_bytes().decode("utf-8"), text.replace('"creatable": true', '"creatable": false'))
        self.assertEqual(len(list(self.history.rglob("league.json"))), 2)

    def test_a_failed_visibility_batch_rolls_back_its_own_writes(self):
        files = [self.leagues / p / "league.json" for p in ("Standard", "SSF Standard")]
        before = [p.read_bytes() for p in files]
        replace, calls = data.os.replace, 0

        def fail_second(*args):
            nonlocal calls
            calls += 1
            if calls == 2:
                raise OSError("fixture replacement failure")
            return replace(*args)

        with patch.object(data.os, "replace", side_effect=fail_second), self.assertRaises(OSError):
            self.run_request(self.visibility("Standard", False), action="SetLeagueVisibility")
        self.assertEqual(before, [p.read_bytes() for p in files])

    def test_visibility_rejects_stale_reviews_and_ended_leagues(self):
        request = self.visibility("Standard", False)
        self.write(self.leagues / "Survivors/league.json", {"name": "Survivors", "parent": "Void", "ended": True})
        with self.assertRaisesRegex(data.AccountError, "changed"):
            self.run_request(request, action="SetLeagueVisibility")
        with self.assertRaises(data.AccountError):
            self.run_request(self.visibility("Survivors", True), action="SetLeagueVisibility")


if __name__ == "__main__":
    unittest.main(verbosity=2)
