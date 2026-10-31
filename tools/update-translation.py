"""Add/update one reviewed English/Russian pair using only Python's standard library.

Example: python tools/update-translation.py --source "Play" --ru "Играть"
For multiline text, use --source-file / --ru-file with UTF-8 files.
"""
import argparse
import hashlib
from pathlib import Path
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    english = parser.add_mutually_exclusive_group(required=True)
    english.add_argument("--source")
    english.add_argument("--source-file", type=Path)
    russian = parser.add_mutually_exclusive_group(required=True)
    russian.add_argument("--ru")
    russian.add_argument("--ru-file", type=Path)
    args = parser.parse_args()
    source = args.source if args.source is not None else args.source_file.read_text(encoding="utf-8")
    translation = args.ru if args.ru is not None else args.ru_file.read_text(encoding="utf-8")
    source = source.replace("\r\n", "\n").replace("\r", "\n")
    if not source.strip() or not translation.strip():
        parser.error("Both texts must be nonempty.")
    key = "Text_" + hashlib.sha256(source.encode("utf-8")).hexdigest()[:12]
    directory = Path(__file__).resolve().parents[1] / "src/Launcher.Core/Resources"
    for name, value in [("Strings.resx", source), ("Strings.ru.resx", translation)]:
        path = directory / name
        tree = ET.parse(path)
        root = tree.getroot()
        item = next((d for d in root.findall("data") if d.attrib["name"] == key), None)
        if item is None:
            item = ET.SubElement(root, "data", {"name": key, "xml:space": "preserve"})
            ET.SubElement(item, "value")
        item.find("value").text = value
        headers = [e for e in root if e.tag != "data"]
        root[:] = headers + sorted(root.findall("data"), key=lambda d: d.attrib["name"])
        ET.indent(tree, space="  ")
        tree.write(path, encoding="utf-8", xml_declaration=True)
    print(key)


if __name__ == "__main__":
    main()
