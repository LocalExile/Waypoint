import importlib.util
from pathlib import Path
import struct
import unittest

spec = importlib.util.spec_from_file_location("LeagueArt", Path(__file__).parents[1] / "scripts/LeagueArt.py")
art = importlib.util.module_from_spec(spec)
spec.loader.exec_module(art)


class LeagueArtTests(unittest.TestCase):
    def test_unknown_wire_contract_is_never_guessed(self):
        self.assertEqual(art.wire_selector("public LeagueEntry Entry() => new(Name, Flags, 0, 0, 0, 0xd1);"), 209)
        for source in ("public LeagueEntry Entry() => new(Name, Flags, 0, 59, 0, 0);", "unrecognised contract"):
            with self.assertRaises(ValueError):
                art.wire_selector(source)

    def table(self, text):
        row = bytearray(27)
        struct.pack_into("<Q", row, 18, 8)
        return struct.pack("<I", 1) + row + b"\xbb" * 8 + text.encode("utf-16-le") + b"\0\0"

    def test_explicit_sprite_and_out_of_range_fallback(self):
        table = self.table("Art/2DArt/UIImages/Login/BannerEvry")
        self.assertEqual(art.explicit_banner(table, 1), "Art/2DArt/UIImages/Login/BannerEvry")
        self.assertIsNone(art.explicit_banner(table, 209))
        self.assertIsNone(art.explicit_banner(table, 0))

    def test_malformed_table_never_selects_unrelated_art(self):
        for table in (b"", b"a" * 80, self.table("Art/Test")[:-2]):
            with self.assertRaises(ValueError):
                art.explicit_banner(table, 1)

    def test_atlas_bounds_are_inclusive_and_exact_key_is_required(self):
        line = '"Art/Banner" "Art/Textures/Interface/UI/2.dds" 0 1040 276 1551'
        self.assertEqual(art.atlas_sprite(line, "art/banner"), ("Art/Textures/Interface/UI/2.dds", (0, 1040, 277, 1552)))
        self.assertIsNone(art.atlas_sprite(line, "Art/BannerOther"))

    def test_unsafe_texture_paths_and_invalid_rectangles_are_rejected(self):
        for texture, bounds in (("../../private.dds", "0 0 1 1"), ("Art/Textures/../secret.dds", "0 0 1 1"),
                                ("Art/Textures/UI.dds", "5 5 1 1"), ("Art/Textures/UI.dds", "0 0 99999 1")):
            with self.assertRaises(ValueError):
                art.atlas_sprite(f'"Art/Banner" "{texture}" {bounds}', "Art/Banner")


if __name__ == "__main__":
    unittest.main()
