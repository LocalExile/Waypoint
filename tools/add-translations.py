"""Add or update many reviewed English/Russian pairs using only Python's standard library.

Example: python tools/add-translations.py batch.json
The file is a UTF-8 JSON object, {"English source": "Russian text"}. It works as tools/update-translation.py does for one
pair (same keys, same catalogs, same ordering), and refuses the whole batch when a pair is empty or its placeholders differ.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

PLACEHOLDER = re.compile(r"(?<!\{)\{(\d+)(?=[,:}])")


def key_of(source):
    return "Text_" + hashlib.sha256(source.encode("utf-8")).hexdigest()[:12]


def normalized(text):
    return text.replace("\r\n", "\n").replace("\r", "\n")


def problems(pairs):
    for source, translation in pairs.items():
        if not isinstance(translation, str) or not source.strip() or not translation.strip():
            yield f"empty text: {source!r}"
        elif sorted(PLACEHOLDER.findall(source)) != sorted(PLACEHOLDER.findall(translation)):
            yield f"placeholders differ: {source!r} / {translation!r}"


def update(path, values):
    tree = ET.parse(path)
    root = tree.getroot()
    items = {d.attrib["name"]: d for d in root.findall("data")}
    added = changed = 0
    for key, value in values.items():
        item = items.get(key)
        if item is None:
            item = ET.SubElement(root, "data", {"name": key, "xml:space": "preserve"})
            ET.SubElement(item, "value").text = value
            items[key] = item
            added += 1
        elif item.find("value").text != value:
            item.find("value").text = value
            changed += 1
    headers = [e for e in root if e.tag != "data"]
    root[:] = headers + sorted(root.findall("data"), key=lambda d: d.attrib["name"])
    ET.indent(tree, space="  ")
    tree.write(path, encoding="utf-8", xml_declaration=True)
    return added, changed


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("batch", type=Path, help="UTF-8 JSON object: {english: russian}")
    args = parser.parse_args()
    pairs = json.loads(args.batch.read_text(encoding="utf-8-sig"))
    if not isinstance(pairs, dict):
        parser.error("The batch must be a JSON object.")
    pairs = {normalized(source): normalized(translation) if isinstance(translation, str) else translation
             for source, translation in pairs.items()}
    wrong = list(problems(pairs))
    if wrong:
        print("\n".join(wrong), file=sys.stderr)
        return 1
    directory = Path(__file__).resolve().parents[1] / "src/Launcher.Core/Resources"
    update(directory / "Strings.resx", {key_of(source): source for source in pairs})
    added, changed = update(directory / "Strings.ru.resx", {key_of(s): t for s, t in pairs.items()})
    print(f"{len(pairs)} pairs: {added} added, {changed} changed, {len(pairs) - added - changed} already there")
    return 0


if __name__ == "__main__":
    sys.exit(main())
