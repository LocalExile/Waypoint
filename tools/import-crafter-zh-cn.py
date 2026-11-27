#!/usr/bin/env python3
"""Build Waypoint's local zh-CN item catalog from files extracted with LocalExile's GgDump."""

import argparse
import ctypes
import json
from pathlib import Path
import re
from struct import unpack_from


SIZES = {"bool": 1, "i16": 2, "u16": 2, "i32": 4, "u32": 4, "f32": 4,
         "string": 8, "row": 8, "enumrow": 4, "foreignrow": 16, "array": 16}


def table(folder, schema, name, columns, language="traditional chinese"):
    prefix = f"data_{language}_" if language else "data_"
    path = folder / f"{prefix}{name.lower()}.datc64"
    data = path.read_bytes()
    rows, = unpack_from("<I", data)
    offsets = {}
    width = 0
    for column in schema[name]:
        kind = column["type"]
        offsets[column["name"]] = (width, kind)
        width += 16 if column.get("array") else SIZES[kind] * (2 if column.get("interval") else 1)
    variable = 4 + rows * width
    if data[variable:variable + 8] != b"\xbb" * 8:
        raise ValueError(f"{path.name}: row layout does not match the supplied PoE 1 schema")

    def field(row, name):
        offset, kind = offsets[name]
        value, = unpack_from("<I", data, row + offset)
        if kind != "string":
            return value
        if value == 0xfefefefe:
            return ""
        start = variable + value
        end = data.find(b"\0\0\0\0", start)
        while end >= 0 and (end - start) % 2:
            end = data.find(b"\0\0\0\0", end + 1)
        if end < 0:
            raise ValueError(f"{path.name}: unterminated {name} string")
        return data[start:end].decode("utf-16-le")

    return [tuple(field(4 + row * width, col) for col in columns) for row in range(rows)]


def simplify(text):
    """Use Windows' Chinese language mapping for tables that only have Traditional Chinese."""
    if not text or text.isascii():
        return text
    map_string = ctypes.windll.kernel32.LCMapStringEx
    flags = 0x02000000  # LCMAP_SIMPLIFIED_CHINESE
    args = (ctypes.c_wchar_p("zh-CN"), flags, ctypes.c_wchar_p(text), -1)
    size = map_string(*args, None, 0, None, None, 0)
    if not size:
        raise ctypes.WinError()
    output = ctypes.create_unicode_buffer(size)
    if not map_string(*args, output, size, None, None, 0):
        raise ctypes.WinError()
    return output.value


def descriptions(path, language):
    with path.open(encoding="utf-16") as source:
        lines = iter(line.strip() for line in source)
        stats = []
        for line in lines:
            if line == "description" or line.startswith("description "):
                header = next(lines).split()
                stats = header[1:1 + int(header[0])]
            elif stats and line == f'lang "{language}"':
                variants = []
                for _ in range(int(next(lines))):
                    row = next(lines)
                    start, end = row.find('"'), row.rfind('"')
                    if start < 0 or end <= start:
                        continue
                    conditions = row[:start].split()[:len(stats)]
                    text = re.sub(r"\[([^\]|]*)(?:\|([^\]]*))?\]", lambda m: m[2] or m[1], row[start + 1:end])
                    tail = row[end + 1:].split()
                    handlers = []
                    at = 0
                    while at + 1 < len(tail):
                        name, value = tail[at:at + 2]
                        at += 1
                        if name == "reminderstring":
                            at += 1
                        elif value.isdigit():
                            handlers.append([name, int(value)])
                            at += 1
                    if language == "Traditional Chinese":
                        text = simplify(text)
                    variants.append({"when": conditions, "text": text,
                                     "handlers": handlers})
                if variants:
                    yield {"type": "desc", "stats": stats, "variants": variants}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("extracted", type=Path, help="directory of GgDump exports")
    parser.add_argument("schema", type=Path, help="LocalExile's data/schema/schema.min.json")
    parser.add_argument("output", type=Path, help="Waypoint's localization/crafter.zh-CN.jsonl")
    parser.add_argument("--table-language", default="traditional chinese",
                        choices=["traditional chinese", "simplified chinese"], help="language of the exported DAT tables")
    args = parser.parse_args()
    schema = {t["name"]: t["columns"] for t in json.loads(args.schema.read_text(encoding="utf-8"))["tables"]
              if t["validFor"] & 1}
    name_fields = {"base": "BaseItemTypes", "class": "ItemClasses", "mod": "Mods"}
    def names(table_name, fields):
        return table(args.extracted, schema, table_name, fields, args.table_language)

    translate = simplify if args.table_language == "traditional chinese" else str
    records = [{"type": kind, "id": key, "name": translate(name)}
               for kind, name_table in name_fields.items()
               for key, name in names(name_table, ("Id", "Name")) if key and name]
    # Cluster themes carry the English passive name in the Gateway catalog. Only use unambiguous names.
    localized_passives = dict(names("PassiveSkills", ("Id", "Name")))
    themes = {}
    for key, name in table(args.extracted, schema, "PassiveSkills", ("Id", "Name"), ""):
        if name and (localized := localized_passives.get(key)):
            names_for_theme = themes.setdefault(name, set())
            names_for_theme.add(translate(localized))
    records.extend({"type": "theme", "id": name, "name": next(iter(matches))}
                   for name, matches in themes.items() if len(matches) == 1)
    records.extend({"type": "word", "list": kind, "key": key, "text": translate(word)}
                   for kind, key, word in names("Words", ("Wordlist", "HASH32", "Text"))
                   if kind in (1, 2) and word)
    stat_file = args.extracted / "metadata_statdescriptions_stat_descriptions.txt"
    # Prefer native Simplified Chinese variants, fill descriptions missing in it from Traditional Chinese.
    native = {tuple(d["stats"]): d for d in descriptions(stat_file, "Simplified Chinese")}
    for description in descriptions(stat_file, "Traditional Chinese"):
        native.setdefault(tuple(description["stats"]), description)
    records.extend(native.values())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    temp = args.output.with_suffix(".tmp")
    with temp.open("w", encoding="utf-8", newline="\n") as output:
        for record in records:
            print(json.dumps(record, ensure_ascii=False, separators=(",", ":")), file=output)
    temp.replace(args.output)
    print(f"Wrote {len(records):,} records to {args.output}")


if __name__ == "__main__":
    main()
