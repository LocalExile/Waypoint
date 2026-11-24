"""Client-state bridge: attach Frida to this checkout's game client and feed it the documents the server publishes.

The Gateway's ClientState hand-over writes one document per channel into data/clients/state/<channel>.json, only when
it changed (server repository: src/GameServer.Gateway/Modding/ClientState.cs). This bridge reads them and hands each to
scripts/StateBridge.js, whose handler for that channel installs it on the game's main thread. The first channel is
`affliction-rewards`, the Simulacrum wave's reward tiles. It is part of the game (Waypoint always runs it with the
client) and follows the client: when one ends it waits for the next, so a Play that restarts the game keeps the tiles.

usage: python StateBridge.py [--client <Game.patched.exe>] [--state-dir <data/clients/state>]
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import json
import os
import sys
import time
from pathlib import Path

import frida

HERE = Path(__file__).resolve().parent


def processes():
    """[(pid, exe path)] for the running processes (path via QueryFullProcessImageNameW)."""
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    class PE32(ctypes.Structure):
        _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                    ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)), ("th32ModuleID", wt.DWORD),
                    ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD), ("pcPriClassBase", ctypes.c_long),
                    ("dwFlags", wt.DWORD), ("szExeFile", wt.WCHAR * 260)]
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    entry = PE32(); entry.dwSize = ctypes.sizeof(PE32)
    out = []
    if snap == -1:
        return out
    try:
        ok = k32.Process32FirstW(snap, ctypes.byref(entry))
        while ok:
            pid = entry.th32ProcessID
            handle = k32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
            if handle:
                size = wt.DWORD(1024)
                buf = ctypes.create_unicode_buffer(1024)
                if k32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)):
                    out.append((pid, buf.value))
                k32.CloseHandle(handle)
            ok = k32.Process32NextW(snap, ctypes.byref(entry))
    finally:
        k32.CloseHandle(snap)
    return out


def find_client(path):
    want = path.resolve()
    matches = [pid for pid, exe in processes() if Path(exe).resolve() == want]
    return matches[0] if matches else None


def running(pid, path):
    """Whether that exact client is still there (several checkouts and sessions share the executable's path)."""
    want = path.resolve()
    return any(p == pid and Path(exe).resolve() == want for p, exe in processes())


def wait_for_client(path, seconds):
    deadline = time.time() + seconds
    while True:
        pid = find_client(path)
        if pid is not None:
            return pid
        if time.time() > deadline:
            return None
        time.sleep(0.5)


def serve(pid, args):
    """Feed one client until it ends. Returns when it is gone."""
    session = frida.attach(pid)
    script = session.create_script((HERE / "StateBridge.js").read_text(encoding="utf-8"))
    script.on("message", lambda m, d: print(json.dumps(m), flush=True))
    script.load()
    print(json.dumps({"stateBridge": "attached", "pid": pid}), flush=True)
    session.on("detached", lambda *_: None)
    seen = {}
    try:
        while running(pid, args.client):
            try:
                documents = list(os.scandir(args.state_dir))
            except OSError:
                documents = []
            for document in documents:
                if not document.name.endswith(".json"):
                    continue
                try:
                    info = document.stat()
                    stamp = (info.st_mtime_ns, info.st_size)
                    if seen.get(document.name) == stamp:
                        continue
                    payload = json.loads(Path(document.path).read_text(encoding="utf-8"))
                except (OSError, ValueError):
                    continue        # a half-written or missing file: the next pass reads it whole
                seen[document.name] = stamp
                try:
                    script.exports_sync.set_state(document.name[:-5], payload)
                except Exception as error:  # noqa: BLE001  (a bridge failure never ends the game)
                    print(json.dumps({"stateBridge": str(error)}), flush=True)
            time.sleep(0.25)
    finally:
        try:
            session.detach()
        except frida.InvalidOperationError:
            pass
    print(json.dumps({"stateBridge": "client ended", "pid": pid}), flush=True)


def main():
    root = Path.cwd()
    ap = argparse.ArgumentParser()
    ap.add_argument("--client", type=Path, default=root / "Client" / "Game.patched.exe")
    ap.add_argument("--state-dir", "--state", dest="state_dir", type=Path,
                    default=root / "data" / "clients" / "state")
    ap.add_argument("--wait", type=float, default=120, help="seconds to wait for the first client to start")
    ap.add_argument("--rewait", type=float, default=600,
                    help="seconds to wait for the next client after one ends (Play closes one and opens another)")
    args = ap.parse_args()

    # The bridge follows the client, not one live.ps1 run: Play closes the running client and opens a new one, and the
    # tiles must come back on it. Only a long stretch with no client at all ends the helper.
    wait = args.wait
    while True:
        pid = wait_for_client(args.client, wait)
        if pid is None:
            print(json.dumps({"stateBridge": f"client not running: {args.client}"}), flush=True)
            return 0 if wait == args.rewait else 1
        serve(pid, args)
        wait = args.rewait


if __name__ == "__main__":
    sys.exit(main())
