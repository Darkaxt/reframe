"""Fictional histories only; exact retirement uses the installed Windows helper."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
import uuid
import zipfile

spec = importlib.util.spec_from_file_location("history_backend", Path(__file__).with_name("history_backend.py"))
backend = importlib.util.module_from_spec(spec)
spec.loader.exec_module(backend)


@unittest.skipUnless(os.name == "nt", "Windows exact-handle integration")
class HistoryContract(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="reframe-history-"))
        self.home = self.root / "home"
        self.tid = str(uuid.uuid4())
        self.sibling = str(uuid.uuid4())
        self.main = self.home / "projects" / "fixture" / (self.tid + ".jsonl")
        self.main.parent.mkdir(parents=True)
        self.main.write_text(json.dumps({"type": "user", "sessionId": self.tid, "cwd": "D:/fictional", "message": {"content": "fictional validation"}}) + "\n")
        self.other = self.main.with_name(self.sibling + ".jsonl")
        self.other.write_bytes(b"unrelated fictional history\n")
        self.child = self.main.with_suffix("") / "subagents" / "agent-fixture.jsonl"
        self.child.parent.mkdir(parents=True)
        self.child.write_bytes(b'{"type":"fixture"}\n')
        self.history = self.home / "history.jsonl"
        self.history.write_text(json.dumps({"sessionId": self.sibling, "display": "keep"}) + "\n" + json.dumps({"sessionId": self.tid, "display": "selected"}) + "\n")
        self.index = self.main.parent / "sessions-index.json"
        self.index.write_text(json.dumps({"version": 1, "entries": [{"sessionId": self.sibling}, {"sessionId": self.tid}]}))
        self.module = backend.helper()
        self.operation = self.root / "archive-operation"
        self.operation.mkdir()

    def prepared(self):
        return backend.prepare(self.module, self.home, self.tid, self.operation)

    def archive(self, prepared):
        return backend.archive_chat(self.module, self.home, self.tid, self.operation, prepared["plan"], prepared["sha256"], {"label": "Fictional", "provider": "chatgpt", "cwd": "D:/fictional"})

    def test_archive_restore_preserves_bytes_children_and_independent_rows(self):
        before = {str(file.relative_to(self.home)): backend.digest(file) for file in (self.main, self.child)}
        original_other = self.other.read_bytes()
        result = self.archive(self.prepared())
        self.assertFalse(self.main.exists())
        self.assertFalse(self.child.exists())
        self.assertEqual(self.other.read_bytes(), original_other)
        self.assertEqual([json.loads(line)["sessionId"] for line in self.history.read_text().splitlines()], [self.sibling])
        # A concurrent, unrelated append survives restoration too.
        with self.history.open("a") as stream:
            stream.write(json.dumps({"sessionId": self.sibling, "display": "new"}) + "\n")
        backend.restore(self.module, self.home, self.tid, result["archive"], result["sha256"])
        for name, digest in before.items():
            self.assertEqual(backend.digest(self.home / name), digest)
        self.assertEqual(len(self.history.read_text().splitlines()), 3)
        self.assertEqual(len(json.loads(self.index.read_text())["entries"]), 2)

    def test_changed_snapshot_refuses_retirement_and_original_survives(self):
        prepared = self.prepared()
        self.main.write_text(self.main.read_text() + '{"changed":true}\n')
        with self.assertRaises(Exception):
            self.archive(prepared)
        self.assertTrue(self.main.exists())
        self.assertTrue(self.child.exists())

    def test_active_reference_blocks_at_retirement_without_repeated_planning_checks(self):
        self.module.live_claude_ids = lambda _home: [self.tid]
        prepared = self.prepared()
        packet = backend.load_plan(self.module, self.home, self.tid, prepared["plan"], prepared["sha256"])
        with self.assertRaisesRegex(self.module.CleanupError, "live registration"):
            self.module.delete_claude(packet)
        self.assertTrue(self.main.exists())
        self.assertTrue(self.child.exists())

    def test_one_fresh_activity_census_per_delete(self):
        from unittest.mock import Mock
        self.module.live_claude_ids = Mock(return_value=[])
        prepared = self.prepared()
        packet = backend.load_plan(self.module, self.home, self.tid, prepared["plan"], prepared["sha256"])
        self.assertTrue(self.module.delete_claude(packet)["complete"])
        self.module.live_claude_ids.assert_called_once_with(self.home)

    def test_route_check_exposes_active_class_without_mutation(self):
        self.module.live_claude_ids = lambda _home: [self.tid]
        with self.assertRaises(self.module.ActiveClaudeError):
            backend.inactive(self.module, self.home, self.tid)
        self.assertTrue(self.main.exists())

    def test_ten_small_offline_chats_have_no_per_chat_shell_delay(self):
        import time
        started = time.perf_counter()
        for index in range(10):
            tid = str(uuid.uuid4())
            main = self.main.with_name(tid + ".jsonl")
            main.write_text(json.dumps({"sessionId": tid, "type": "user", "cwd": "D:/fictional"}) + "\n")
            operation = self.root / ("timed-" + str(index))
            operation.mkdir()
            prepared = backend.prepare(self.module, self.home, tid, operation)
            packet = backend.load_plan(self.module, self.home, tid, prepared["plan"], prepared["sha256"])
            self.assertTrue(self.module.delete_claude(packet)["complete"])
            self.assertFalse(main.exists())
        elapsed = time.perf_counter() - started
        print(json.dumps({"fictional_chats": 10, "elapsed_seconds": elapsed}), flush=True)
        self.assertLess(elapsed, 10, "Native Windows offline deletion regressed to slow per-chat process scans")

    def test_delete_exact_chat_without_backup(self):
        prepared = self.prepared()
        packet = backend.load_plan(self.module, self.home, self.tid, prepared["plan"], prepared["sha256"])
        result = self.module.delete_claude(packet)
        self.assertTrue(result["complete"])
        self.assertFalse(self.main.exists())
        self.assertTrue(self.other.exists())
        self.assertFalse(list(self.operation.glob("*.zip")))

    def test_corrupt_archive_and_conflicting_restore_preserve_current_files(self):
        result = self.archive(self.prepared())
        self.main.parent.mkdir(parents=True, exist_ok=True)
        self.main.write_bytes(b"concurrent replacement")
        with self.assertRaisesRegex(ValueError, "conflict"):
            backend.restore(self.module, self.home, self.tid, result["archive"], result["sha256"])
        self.assertEqual(self.main.read_bytes(), b"concurrent replacement")
        with Path(result["archive"]).open("ab") as stream:
            stream.write(b"changed")
        with self.assertRaisesRegex(ValueError, "digest"):
            backend.restore(self.module, self.home, self.tid, result["archive"], result["sha256"])

    def test_large_stream_and_zip_path_validation(self):
        big = self.child.with_name("large-fixture.bin")
        with big.open("wb") as stream:
            for _ in range(64):
                stream.write(b"x" * (1024 * 1024))
        expected = backend.digest(big)
        result = self.archive(self.prepared())
        backend.restore(self.module, self.home, self.tid, result["archive"], result["sha256"])
        self.assertEqual(backend.digest(big), expected)
        for name in ("../settings.json", "projects/a/../../settings.json", "tasks/other/data", "C:/credentials", "projects/a/" + self.sibling + ".jsonl"):
            self.assertFalse(backend.allowed_member(name, self.tid))
        with zipfile.ZipFile(result["archive"], "a") as archive:
            archive.writestr("payload/../settings.json", b"bad")
        with self.assertRaisesRegex(ValueError, "extra"):
            backend.verify_archive(result["archive"], self.home, self.tid)

    def test_unknown_index_format_does_not_retire_files(self):
        self.index.write_text('{"unknown":true}')
        with self.assertRaisesRegex(ValueError, "Unsupported"):
            self.archive(self.prepared())
        self.assertTrue(self.main.exists())
        self.assertTrue(self.other.exists())

    def test_archive_failure_leaves_every_original(self):
        prepared = self.prepared()
        original_write = backend.zipfile.ZipFile.write
        def fail_write(*_args, **_kwargs):
            raise OSError("fictional full archive drive")
        backend.zipfile.ZipFile.write = fail_write
        try:
            with self.assertRaisesRegex(OSError, "fictional"):
                self.archive(prepared)
        finally:
            backend.zipfile.ZipFile.write = original_write
        self.assertTrue(self.main.exists())
        self.assertTrue(self.child.exists())
        self.assertFalse((self.operation / "verified.json").exists())

    def test_partial_retirement_retains_verified_recovery_and_exact_receipt(self):
        self.module.delete_claude = lambda _packet: {"complete": False, "members": [{"outcome": "changed_identity", "path": str(self.main)}]}
        with self.assertRaisesRegex(ValueError, "partial"):
            self.archive(self.prepared())
        self.assertTrue((self.operation / "verified.json").exists())
        self.assertFalse(json.loads((self.operation / "retirement.json").read_text())["complete"])
        self.assertTrue(self.main.exists())

    def test_startup_recovers_verified_archive_after_activation_interruption(self):
        # Simulate a crash after the backend completed but before the Electron state saved its archive row.
        root = self.root / "catalog"
        self.operation = root / "operations" / str(uuid.uuid4())
        self.operation.mkdir(parents=True)
        result = self.archive(self.prepared())
        recovered = backend.recover(self.home, root, self.module)
        self.assertEqual(recovered["warnings"], [])
        self.assertEqual(recovered["entries"][0]["id"], self.tid)
        self.assertFalse(recovered["entries"][0]["partial"])
        restored = backend.restore(self.module, self.home, self.tid, result["archive"], result["sha256"])
        backend.save(self.operation / "restored-fixture.json", restored)
        again = backend.recover(self.home, root, self.module)
        self.assertEqual(again["entries"], [])
        self.assertEqual(again["restored"], [self.tid])


if __name__ == "__main__":
    unittest.main(verbosity=2)
