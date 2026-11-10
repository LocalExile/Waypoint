"""SQLite online backup includes committed WAL pages; never copy an open database as a plain file.

SaveBackup.py <source> <destination> [<second source> <second destination>]
The second pair is LocalExile's auth.db beside characters.db. Both are read in one read transaction, so the copies hold the
same moment of both files: an account and its characters are written together.
"""
from contextlib import closing
import pathlib
import sqlite3
import sys

arguments = sys.argv[1:]
if len(arguments) not in (2, 4):
    raise SystemExit("Expected a source and a destination, and optionally a second pair.")
pairs = [tuple(map(pathlib.Path, arguments[i:i + 2])) for i in range(0, len(arguments), 2)]
for _, destination in pairs:
    if destination.exists():
        raise RuntimeError("Backup destination already exists")
with closing(sqlite3.connect(pairs[0][0].resolve().as_uri() + "?mode=ro", uri=True, timeout=10, isolation_level=None)) as src:
    schemas = ["main"]
    if len(pairs) == 2:
        src.execute("ATTACH DATABASE ? AS second", (pairs[1][0].resolve().as_uri() + "?mode=ro",))
        schemas.append("second")
    src.execute("BEGIN")
    for schema in schemas:
        src.execute('SELECT count(*) FROM "' + schema + '".sqlite_master').fetchone()
    for schema, (_, destination) in zip(schemas, pairs):
        with closing(sqlite3.connect(destination, timeout=10)) as dst:
            src.backup(dst, name=schema)
            # The copy keeps the source's WAL mode; a backup is one file, with no -wal or -shm beside it.
            dst.execute("PRAGMA journal_mode=DELETE")
            if dst.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
                raise RuntimeError("Save backup failed its integrity check")
    src.execute("COMMIT")
