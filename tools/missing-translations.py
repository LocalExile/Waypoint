"""List interface text that is missing from a catalog; exit 1 when there is any.

Example: python tools/missing-translations.py
Scans src/**/*.cs for L.Text("...") and src/**/*.xaml for {l:Loc '...'}. A first argument made of literals joined with + is
one source, and each literal branch of a ?: counts. L.Text with an interpolated string is reported too: its source can never
match a key. Anything else non-literal (L.Text(stage)) cannot be read from the code: add its possible values by hand.
"""
import hashlib
import html
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
CALL = re.compile(r"(?<![\w.])L\.Text\(")
LOC = re.compile(r"\{l:Loc\s+(?:'((?:\\.|[^'\\])*)'|([^'}][^}]*))\s*\}", re.S)
ESCAPES = {"n": "\n", "r": "\r", "t": "\t", "0": "\0", "a": "\a", "b": "\b", "f": "\f", "v": "\v", "\\": "\\", '"': '"', "'": "'"}


def key_of(source):
    return "Text_" + hashlib.sha256(source.replace("\r\n", "\n").encode("utf-8")).hexdigest()[:12]


def literal(code, at):
    """The regular or verbatim string literal at code[at:]: its value and where it ends, or None."""
    if code.startswith('@"', at):
        end = at + 2
        value = []
        while end < len(code):
            if code[end] == '"':
                if code[end + 1:end + 2] != '"':
                    return "".join(value), end + 1
                end += 1
            value.append(code[end])
            end += 1
        return None
    if code[at:at + 1] != '"':
        return None
    end = at + 1
    value = []
    while end < len(code) and code[end] not in '"\n':
        if code[end] != "\\":
            value.append(code[end])
            end += 1
        elif code[end + 1] == "u":
            value.append(chr(int(code[end + 2:end + 6], 16)))
            end += 6
        else:
            value.append(ESCAPES.get(code[end + 1], code[end + 1]))
            end += 2
    return ("".join(value), end + 1) if code[end:end + 1] == '"' else None


def skip_space(code, at):
    while at < len(code) and code[at].isspace():
        at += 1
    return at


def joined(code, at):
    """Literals joined with + from code[at:]: their value and where they end, or None."""
    found = literal(code, at)
    if found is None:
        return None
    value, end = found
    while code[skip_space(code, end):skip_space(code, end) + 1] == "+":
        found = literal(code, skip_space(code, skip_space(code, end) + 1))
        if found is None:
            return None
        value += found[0]
        end = found[1]
    return value, end


def first_argument(code, at):
    """Where the call's first argument ends, tracking brackets and skipping strings."""
    depth = 0
    while at < len(code):
        char = code[at]
        if char in '"@$':
            start = at + (char != '"') + (code[at + 1:at + 2] in ('"', "@", "$") and code[at + 1] != '"')
            found = literal(code, at if char == "@" else start)
            at = found[1] if found else at + 1
            continue
        if char == "'":
            at = code.index("'", at + 2 + (code[at + 1] == "\\")) + 1
            continue
        if char in "([{":
            depth += 1
        elif char in ")]}":
            if depth == 0:
                return at
            depth -= 1
        elif char == "," and depth == 0:
            return at
        at += 1
    return at


def branches(code, at, end):
    """The literal results of the ?: chain in code[at:end]."""
    found = joined(code, at)
    if found is not None and skip_space(code, found[1]) >= end:
        yield found[0]
        return
    depth = 0
    inner = at
    while inner < end:
        char = code[inner]
        if char in '"@':
            found = literal(code, inner)
            inner = found[1] if found else inner + 1
            continue
        if char in "([{":
            depth += 1
        elif char in ")]}":
            depth -= 1
        elif char in "?:" and depth == 0 and code[inner + 1:inner + 2] not in "?.":
            start = skip_space(code, inner + 1)
            found = joined(code, start)
            if found is not None and code[skip_space(code, found[1]):skip_space(code, found[1]) + 1] in ":?)," or found and found[1] >= end:
                yield found[0]
        inner += 1


def sources_in_code(code):
    for match in CALL.finditer(code):
        line = code.rfind("\n", 0, match.start()) + 1
        if code[line:match.start()].lstrip().startswith("//"):
            continue
        at = skip_space(code, match.end())
        if code[at:at + 2] in ('$"', "$@") or code[at:at + 2] == '@$':
            yield line, None
            continue
        for source in branches(code, at, first_argument(code, at)):
            yield line, source


def sources_in_xaml(text):
    for match in LOC.finditer(text):
        quoted, bare = match.groups()
        raw = quoted if quoted is not None else bare.strip()
        yield match.start(), re.sub(r"\\(.)", r"\1", html.unescape(raw), flags=re.S)


def catalog(name):
    root = ET.parse(ROOT / "src/Launcher.Core/Resources" / name).getroot()
    return {d.attrib["name"] for d in root.findall("data")}


def main():
    # A Windows console's code page cannot print every source, and CI's pipe has no better one.
    sys.stdout.reconfigure(encoding="utf-8")
    english, russian = catalog("Strings.resx"), catalog("Strings.ru.resx")
    missing = {}
    interpolated = []
    for path in sorted((ROOT / "src").rglob("*")):
        parts = path.relative_to(ROOT).parts
        if "bin" in parts or "obj" in parts or path.suffix not in (".cs", ".xaml"):
            continue
        text = path.read_text(encoding="utf-8-sig")
        found = sources_in_code(text) if path.suffix == ".cs" else sources_in_xaml(text)
        for offset, source in found:
            place = f"{path.relative_to(ROOT).as_posix()}:{text.count(chr(10), 0, offset) + 1}"
            if source is None:
                interpolated.append(place)
            else:
                lacking = [n for n, keys in (("en", english), ("ru", russian)) if key_of(source) not in keys]
                if lacking:
                    missing.setdefault(source, (lacking, place))
    for source, (lacking, place) in missing.items():
        print(f"{place} [{'/'.join(lacking)}] {source!r}")
    for place in interpolated:
        print(f"{place} interpolated source")
    return 1 if missing or interpolated else 0


if __name__ == "__main__":
    sys.exit(main())
