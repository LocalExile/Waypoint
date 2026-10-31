"""Lifecycle tests use an inert Frida double, never a game hook."""
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import types
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("capture", Path(__file__).parents[1] / "scripts/WalkmapCapture.py")
capture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(capture)


class CaptureTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="Waypoint capture ")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.session = self.root / "session"
        self.session.mkdir()
        self.status = capture.Status(self.session / "status.json")

    def test_atomic_status_preserves_capture_on_failure(self):
        self.status.update(LastArea="1_1_1", LastCaptureUtc="2026-10-31T00:00:00Z")
        capture.Status(self.status.path).update(State="Error", Detail="failed")
        data = json.loads(self.status.path.read_text())
        self.assertEqual(data["LastArea"], "1_1_1")
        self.assertEqual(data["State"], "Error")
        self.assertFalse(self.status.path.with_suffix(".tmp").exists())

    def test_ready_requires_hook_acknowledgement(self):
        recorder = types.SimpleNamespace(on_message=lambda *args: None)
        failed = threading.Event()
        capture.record_message(recorder, self.status, self.root, {}, None, failed)
        self.assertEqual(self.status.value["State"], "Waiting")
        capture.record_message(recorder, self.status, self.root, {"payload": {"type": "ready"}}, None, failed)
        self.assertEqual(self.status.value["State"], "Ready")

    def test_refusal_cannot_be_hidden_by_later_ready(self):
        recorder = types.SimpleNamespace(on_message=lambda *args: None)
        failed = threading.Event()
        capture.record_message(recorder, self.status, self.root, {"payload": {"type": "refused", "reason": "wrong client"}}, None, failed)
        capture.record_message(recorder, self.status, self.root, {"payload": {"type": "ready"}}, None, failed)
        self.assertTrue(failed.is_set())
        self.assertEqual(self.status.value["State"], "Error")
        self.assertEqual(self.status.value["Detail"], "wrong client")

    def test_script_error_is_failure(self):
        failed = threading.Event()
        capture.record_message(types.SimpleNamespace(on_message=lambda *args: None), self.status, self.root,
                               {"type": "error", "description": "bad hook"}, None, failed)
        self.assertTrue(failed.is_set())
        self.assertEqual(self.status.value["Detail"], "bad hook")

    def test_upstream_receives_message_data_and_destination(self):
        received = []
        message, data = {"payload": {"type": "grid", "w": 2, "h": 2}}, b"1234"
        capture.record_message(types.SimpleNamespace(on_message=lambda *args: received.append(args)), self.status,
                               self.root, message, data, threading.Event())
        self.assertEqual(received, [(message, data, str(self.root))])

    def test_upstream_writer_error_is_reported(self):
        def write(*args):
            raise PermissionError("capture folder is read-only")
        failed = threading.Event()
        capture.record_message(types.SimpleNamespace(on_message=write), self.status, self.root, {}, None, failed)
        self.assertTrue(failed.is_set())
        self.assertIn("read-only", self.status.value["Detail"])

    def prepare_repo(self):
        source = self.root / "scripts/walkmaps"
        source.mkdir(parents=True)
        (source / "walkmaps.py").write_text('def log(text): pass\ndef on_message(message, data, out): pass\n')
        (source / "walkmaps.js").write_text("inert test fixture; never interpreted")

    def processes(self, clients=None, ticks=123, alive=True):
        return types.SimpleNamespace(open=lambda pid: pid, close=lambda handle: None,
                                     identity=lambda handle: ("", ticks), session=lambda pid: 1,
                                     alive=lambda handle: alive, clients=lambda path: clients or [])

    def test_stop_while_waiting_never_attaches(self):
        self.prepare_repo()
        (self.session / "stop").touch()
        attached = []
        with patch.object(capture, "WindowsProcesses", return_value=self.processes()), patch.dict(sys.modules, {"frida": types.SimpleNamespace(attach=lambda pid: attached.append(pid))}):
            capture.run(self.root, self.session, 100, 123)
        self.assertEqual(attached, [])
        self.assertEqual(json.loads(self.status.path.read_text())["State"], "Stopped")

    def test_gateway_exit_stops_waiting(self):
        self.prepare_repo()
        with patch.object(capture, "WindowsProcesses", return_value=self.processes(alive=False)), patch.dict(sys.modules, {"frida": types.SimpleNamespace()}):
            capture.run(self.root, self.session, 100, 123)
        self.assertEqual(json.loads(self.status.path.read_text())["State"], "Stopped")

    def test_recycled_gateway_pid_refused(self):
        with patch.object(capture, "WindowsProcesses", return_value=self.processes(ticks=456)), patch.dict(sys.modules, {"frida": types.SimpleNamespace()}):
            with self.assertRaisesRegex(RuntimeError, "Gateway identity changed"):
                capture.run(self.root, self.session, 100, 123)

    def test_wrong_session_refused(self):
        processes = self.processes()
        processes.session = lambda pid: 1 if pid == 100 else 2
        with patch.object(capture, "WindowsProcesses", return_value=processes), patch.dict(sys.modules, {"frida": types.SimpleNamespace()}):
            with self.assertRaisesRegex(RuntimeError, "another Windows session"):
                capture.run(self.root, self.session, 100, 123)

    def test_recycled_game_pid_refused_before_attach(self):
        self.prepare_repo()
        processes = self.processes(clients=[(200, 456)])
        with patch.object(capture, "WindowsProcesses", return_value=processes), patch.dict(sys.modules, {"frida": types.SimpleNamespace()}):
            with self.assertRaisesRegex(RuntimeError, "Game identity changed"):
                capture.run(self.root, self.session, 100, 123)

    def test_stop_detaches_verified_client(self):
        self.prepare_repo()
        detached = []
        class Hook:
            def on(inner, name, handler):
                inner.handler = handler
            def load(inner):
                inner.handler({"payload": {"type": "ready"}}, None)
                (self.session / "stop").touch()
        session = types.SimpleNamespace(on=lambda *args: None, create_script=lambda text: Hook(), detach=lambda: detached.append(True))
        attached = []
        def attach(pid):
            attached.append(pid)
            return session
        with patch.object(capture, "WindowsProcesses", return_value=self.processes(clients=[(200, 123)])), patch.dict(sys.modules, {"frida": types.SimpleNamespace(attach=attach)}):
            capture.run(self.root, self.session, 100, 123)
        self.assertEqual(attached, [200])
        self.assertEqual(detached, [True])
        self.assertEqual(json.loads(self.status.path.read_text())["State"], "Stopped")

    def test_native_windows_identity(self):
        if os.name != "nt":
            self.skipTest("Windows process identity check")
        processes = capture.WindowsProcesses()
        handle = processes.open(os.getpid())
        try:
            path, ticks = processes.identity(handle)
            self.assertEqual(path, os.path.normcase(os.path.abspath(sys.executable)))
            self.assertGreater(ticks, 0)
            self.assertTrue(processes.alive(handle))
        finally:
            processes.close(handle)

    def test_foreign_client_refused(self):
        self.check_clients([111], "other", "Another checkout")

    def test_multiple_clients_refused(self):
        self.check_clients([111, 112], "expected", "Multiple patched clients")

    def check_clients(self, pids, path, error):
        processes = capture.WindowsProcesses.__new__(capture.WindowsProcesses)
        processes.open = lambda pid: pid
        processes.close = lambda handle: None
        processes.identity = lambda handle: (os.path.normcase(os.path.abspath(self.root / path)), 123)
        processes.session = lambda pid: 1
        frida = types.SimpleNamespace(get_local_device=lambda: types.SimpleNamespace(enumerate_processes=lambda: [types.SimpleNamespace(name="Game.patched.exe", pid=pid) for pid in pids]))
        with patch.dict(sys.modules, {"frida": frida}):
            with self.assertRaisesRegex(RuntimeError, error):
                processes.clients(self.root / "expected")


if __name__ == "__main__":
    unittest.main(verbosity=2)
