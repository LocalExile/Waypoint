"""Waypoint lifecycle adapter. Loads the selected checkout's recorder; contains no game hooks.

Only the verified patched client in this Windows session may be attached. A private
stop file requests detachment, never termination of the game. Gateway exit also ends
the recorder. Upstream owns hook signatures, data formats and preservation of captures.
"""
import argparse
import ctypes
from ctypes import wintypes
import importlib.util
import json
import os
from pathlib import Path
import re
import sys
import threading
import time
from datetime import datetime, timezone

sys.dont_write_bytecode = True  # Loading upstream must not write into its source tree.


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def physical(path):
    """Reject symlinks/junctions on output and coordination paths, including ancestors."""
    for part in (Path(path), *Path(path).parents):
        try:
            attributes = getattr(part.lstat(), "st_file_attributes", 0)
        except FileNotFoundError:
            continue
        if part.is_symlink() or attributes & 0x400:
            raise RuntimeError(f"Redirected capture path: {part}")


class Status:
    def __init__(self, path):
        self.path = Path(path)
        self.lock = threading.RLock()
        self.value = dict(State="Waiting", Detail="Waiting for the selected game client.",
                          LastArea="", LastCaptureUtc="", ProcessId=os.getpid())
        if self.path.exists():
            physical(self.path)
            previous = json.loads(self.path.read_text(encoding="utf-8-sig"))
            if previous.get("ProcessId") == os.getpid():
                self.value.update(previous)

    def update(self, **values):
        with self.lock:
            self.value.update(values)
            self.value["UpdatedUtc"] = utc_now()
            physical(self.path)
            temp = self.path.with_suffix(".tmp")
            physical(temp)
            temp.write_text(json.dumps(self.value), encoding="utf-8")
            # Readers share delete access on Windows; briefly retry other transient readers.
            for attempt in range(20):
                try:
                    os.replace(temp, self.path)
                    break
                except PermissionError:
                    if attempt == 19:
                        raise
                    time.sleep(.05)


class WindowsProcesses:
    """Read-only process identity checks. Handles are retained across the attach race."""
    def __init__(self):
        self.api = ctypes.WinDLL("kernel32", use_last_error=True)
        self.api.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.api.OpenProcess.restype = wintypes.HANDLE
        self.api.CloseHandle.argtypes = [wintypes.HANDLE]
        self.api.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
        self.api.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
        self.api.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        self.api.ProcessIdToSessionId.argtypes = [wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]

    def open(self, pid):
        return self.api.OpenProcess(0x1000 | 0x100000, False, pid)

    def close(self, handle):
        self.api.CloseHandle(handle)

    def alive(self, handle):
        return self.api.WaitForSingleObject(handle, 0) == 0x102

    def identity(self, handle):
        size = wintypes.DWORD(32768)
        name = ctypes.create_unicode_buffer(size.value)
        times = [wintypes.FILETIME() for _ in range(4)]
        if not self.api.QueryFullProcessImageNameW(handle, 0, name, ctypes.byref(size)):
            raise RuntimeError("Could not verify the game executable.")
        if not self.api.GetProcessTimes(handle, *(ctypes.byref(t) for t in times)):
            raise RuntimeError("Could not verify process start time.")
        ticks = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
        return os.path.normcase(os.path.abspath(name.value)), ticks

    def session(self, pid):
        value = wintypes.DWORD()
        if not self.api.ProcessIdToSessionId(pid, ctypes.byref(value)):
            raise RuntimeError("Could not verify the Windows session.")
        return value.value

    def clients(self, expected):
        # Frida enumerates process IDs; Windows verifies full path and Windows session.
        import frida
        matches = []
        for process in frida.get_local_device().enumerate_processes():
            if process.name.lower() != "game.patched.exe":
                continue
            handle = self.open(process.pid)
            if not handle:
                raise RuntimeError("A patched client could not be inspected. Use the same privilege level.")
            try:
                path, ticks = self.identity(handle)
                if self.session(process.pid) == self.session(os.getpid()):
                    if path != os.path.normcase(os.path.abspath(expected)):
                        raise RuntimeError("Another checkout's patched client is running.")
                    matches.append((process.pid, ticks))
            finally:
                self.close(handle)
        if len(matches) > 1:
            raise RuntimeError("Multiple patched clients are running.")
        return matches


def load_recorder(repo):
    path = Path(repo) / "scripts/walkmaps/walkmaps.py"
    physical(path)
    spec = importlib.util.spec_from_file_location("localexile_walkmaps", path)
    recorder = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(recorder)
    if not callable(getattr(recorder, "on_message", None)):
        raise RuntimeError("This checkout's recorder interface is unsupported.")
    return recorder


def record_message(recorder, status, out, message, data, failed):
    """Upstream writes the files. Ready means a hook event, never just a live Python PID."""
    try:
        recorder.on_message(message, data, str(out))
        payload = message.get("payload") or {}
        kind = payload.get("type")
        if message.get("type") == "error" or kind in ("refused", "error"):
            detail = message.get("description") or payload.get("reason") or payload.get("message") or "Recorder hook failed."
            status.update(State="Error", Detail=str(detail)[:1200])
            failed.set()
        elif kind == "ready" and not failed.is_set():
            status.update(State="Ready", Detail="Ready to capture newly loaded areas.")
    except Exception as error:
        status.update(State="Error", Detail=str(error)[:1200])
        failed.set()


def run(repo, directory, gateway_pid, gateway_ticks, wait=600):
    import frida
    directory = Path(directory)
    physical(directory)
    status = Status(directory / "status.json")
    status.update()
    out = Path(repo) / "data/walkability"
    physical(out)
    out.mkdir(parents=True, exist_ok=True)
    processes = WindowsProcesses()
    gateway = processes.open(gateway_pid)
    if not gateway:
        raise RuntimeError("Gateway exited before capture could start.")
    session = None
    client = None
    try:
        if processes.identity(gateway)[1] != gateway_ticks:
            raise RuntimeError("Gateway identity changed.")
        if processes.session(gateway_pid) != processes.session(os.getpid()):
            raise RuntimeError("Gateway belongs to another Windows session.")
        recorder = load_recorder(repo)
        original_log = recorder.log

        def log(line):
            original_log(line)
            # Only an upstream successful primary-grid save updates the capture summary.
            match = re.fullmatch(r"saved (.+)_seed(-?\d+)\.bin \((\d+)x(\d+)\)", line)
            if match:
                status.update(LastArea=match[1], LastCaptureUtc=utc_now())

        recorder.log = log
        failed, detached = threading.Event(), threading.Event()
        deadline = time.monotonic() + wait
        while time.monotonic() < deadline:
            if (directory / "stop").exists() or not processes.alive(gateway):
                status.update(State="Stopped", Detail="Capture stopped. Existing files were kept.")
                return
            matches = processes.clients(Path(repo) / "Client/Game.patched.exe")
            if matches:
                pid, ticks = matches[0]
                client = processes.open(pid)
                if not client or not processes.alive(client) or processes.identity(client)[1] != ticks:
                    raise RuntimeError("Game identity changed before attachment.")
                session = frida.attach(pid)
                if not processes.alive(client) or processes.identity(client)[1] != ticks:
                    raise RuntimeError("Game identity changed during attachment.")
                session.on("detached", lambda *_: detached.set())
                hook_path = Path(repo) / "scripts/walkmaps/walkmaps.js"
                physical(hook_path)
                hook = session.create_script(hook_path.read_text(encoding="utf-8"))
                hook.on("message", lambda message, data: record_message(recorder, status, out, message, data, failed))
                hook.load()
                print(f"[waypoint] Attached to verified client PID {pid}.", flush=True)
                # Give the hook a bounded interval to acknowledge installation.
                ready_deadline = time.monotonic() + 15
                while not detached.wait(.25):
                    if failed.is_set():
                        return
                    if (directory / "stop").exists() or not processes.alive(gateway):
                        break
                    if status.value["State"] != "Ready" and time.monotonic() > ready_deadline:
                        raise RuntimeError("Recorder did not confirm its hook. See the capture log.")
                if not failed.is_set():
                    status.update(State="Stopped", Detail="Capture stopped. Existing files were kept.")
                return
            time.sleep(.5)
        raise RuntimeError("The game did not start within 10 minutes. Start capture again when ready.")
    finally:
        if session:
            session.detach()
        if client:
            processes.close(client)
        processes.close(gateway)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True)
    parser.add_argument("--session", required=True)
    parser.add_argument("--gateway-pid", required=True, type=int)
    parser.add_argument("--gateway-ticks", required=True, type=int)
    args = parser.parse_args()
    # A byte-range lock prevents orphaned/second launchers from creating duplicate sessions.
    import msvcrt
    lock_path = Path(args.session).parent.parent / "recorder.lock"
    physical(lock_path)
    with open(lock_path, "a+b") as lock:
        lock.seek(0)
        if not lock.read(1):
            lock.write(b"0")
            lock.flush()
        lock.seek(0)
        try:
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError:
            Status(Path(args.session) / "status.json").update(State="Error", Detail="Another capture session is already active.")
            return 1
        try:
            run(args.repo, args.session, args.gateway_pid, args.gateway_ticks)
            return 0
        except Exception as error:
            print(f"[waypoint] {error}", file=sys.stderr, flush=True)
            Status(Path(args.session) / "status.json").update(State="Error", Detail=str(error)[:1200])
            return 1
        finally:
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)


if __name__ == "__main__":
    sys.exit(main())
