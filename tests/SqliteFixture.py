import os
import pathlib
import runpy
import sqlite3
import sys

mode, path = sys.argv[1:3]
with sqlite3.connect(path) as db:
    if mode == "stopped":
        # As `LocalExile.exe stop` leaves a database: ended outright, with what was saved still in the WAL.
        db.execute("PRAGMA journal_mode=WAL")
        db.execute("PRAGMA wal_autocheckpoint=0")
        db.execute("create table characters(name text)")
        db.execute("insert into characters values (?)", ("release-character",))
        db.commit()
        os._exit(0)
    elif mode == "brought":
        assert db.execute("select name from characters").fetchone()[0] == "release-character"
    elif mode in ("init", "wal"):
        if mode == "wal":
            db.execute("PRAGMA journal_mode=WAL")
            db.execute("PRAGMA wal_autocheckpoint=0")
        db.execute("create table characters(name text)")
        db.execute("insert into characters values (?)", ("fixture-character",))
        db.commit()
        if mode == "wal":
            helper, destination = sys.argv[3:5]
            assert pathlib.Path(path + "-wal").stat().st_size > 0
            sys.argv = [helper, path, destination]
            runpy.run_path(helper, run_name="__main__")
            with sqlite3.connect(destination) as snapshot:
                assert snapshot.execute("select name from characters").fetchone()[0] == "fixture-character"
    elif mode == "empty":
        assert db.execute("select count(*) from sqlite_master").fetchone()[0] == 0
    elif mode == "restored":
        assert db.execute("select name from characters").fetchone()[0] == "fixture-character"
