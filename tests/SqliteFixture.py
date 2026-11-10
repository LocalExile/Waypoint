from contextlib import closing
import os
import pathlib
import runpy
import sqlite3
import sys

mode, path = sys.argv[1:3]
if mode.startswith("split"):
    # LocalExile from the database split on: characters.db (path) and auth.db, each holding one label.
    auth, label = sys.argv[3], sys.argv[4] if len(sys.argv) > 4 else ""
    with closing(sqlite3.connect(path)) as characters, closing(sqlite3.connect(auth)) as accounts:
        if mode == "split":
            # As a stopped server leaves them: what was saved is still in each WAL.
            for db, table in ((characters, "characters"), (accounts, "accounts")):
                db.execute("PRAGMA journal_mode=WAL")
                db.execute("PRAGMA wal_autocheckpoint=0")
                db.execute("create table " + table + "(name text)")
                db.execute("insert into " + table + " values (?)", (label,))
                db.commit()
            assert pathlib.Path(path + "-wal").stat().st_size > 0 and pathlib.Path(auth + "-wal").stat().st_size > 0
            os._exit(0)
        elif mode == "split-rename":
            # A label only this pair holds.
            characters.execute("update characters set name=?", (label,))
            accounts.execute("update accounts set name=?", (label,))
            characters.commit()
            accounts.commit()
        elif mode == "split-has":
            assert characters.execute("select name from characters").fetchone()[0] == label
            assert accounts.execute("select name from accounts").fetchone()[0] == label
        elif mode == "split-empty":
            for db in (characters, accounts):
                assert db.execute("select count(*) from sqlite_master").fetchone()[0] == 0
    sys.exit(0)
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
    elif mode == "rename":
        # A character only this database holds.
        db.execute("update characters set name=?", (sys.argv[3],))
    elif mode == "has":
        assert db.execute("select name from characters").fetchone()[0] == sys.argv[3]
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
