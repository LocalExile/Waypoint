"""SQLite online backup includes committed WAL pages; never copy an open database as a plain file."""
import pathlib
import sqlite3
import sys

source, destination = map(pathlib.Path, sys.argv[1:3])
if destination.exists():
    raise RuntimeError("Backup destination already exists")
with sqlite3.connect(source.resolve().as_uri() + "?mode=ro", uri=True, timeout=10) as src:
    with sqlite3.connect(destination, timeout=10) as dst:
        src.backup(dst)
        if dst.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
            raise RuntimeError("Save backup failed its integrity check")
