#!/usr/bin/env python3
"""Exercise external-agent secret preservation and replacement."""
import importlib.util
import os
from pathlib import Path
import stat
import tempfile
import unittest

spec = importlib.util.spec_from_file_location(
    "agent_secret_owner", Path(__file__).with_name("update-agent-secrets.py"))
owner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(owner)


class AgentSecretsTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="agent-secret-owner-")
        self.path = Path(self.directory.name) / "agents.env"

    def tearDown(self):
        self.directory.cleanup()

    def seed(self):
        raw = b"# retained\nOPENAI_API_KEY=old\nXAI_API_KEY=keep\nOTHER=retained\n"
        self.path.write_bytes(raw)
        self.path.chmod(0o600)
        return raw

    def test_missing_inputs_preserve_existing_file_and_inode(self):
        raw = self.seed()
        before = self.path.stat()
        self.assertFalse(owner.update(self.path, {}))
        self.assertEqual(raw, self.path.read_bytes())
        self.assertEqual(before.st_ino, self.path.stat().st_ino)

    def test_selected_key_is_replaced_and_unselected_keys_are_retained(self):
        self.seed()
        before = self.path.stat()
        self.assertTrue(owner.update(self.path, {"OPENAI_API_KEY": "new"}))
        raw = self.path.read_text()
        self.assertEqual(1, raw.count("OPENAI_API_KEY="))
        self.assertIn("OPENAI_API_KEY=new\n", raw)
        self.assertIn("XAI_API_KEY=keep\n", raw)
        self.assertIn("OTHER=retained\n", raw)
        after = self.path.stat()
        self.assertEqual((before.st_uid, before.st_gid), (after.st_uid, after.st_gid))
        self.assertEqual(stat.S_IMODE(before.st_mode), stat.S_IMODE(after.st_mode))

    def test_new_file_contains_only_explicit_credentials(self):
        self.assertTrue(owner.update(self.path, {"GEMINI_API_KEY": "gemini"}))
        self.assertEqual("GEMINI_API_KEY=gemini\n", self.path.read_text())
        self.assertEqual(0o640, stat.S_IMODE(self.path.stat().st_mode))

    def test_multiline_value_is_rejected_without_write(self):
        raw = self.seed()
        with self.assertRaises(ValueError):
            owner.update(self.path, {"OPENAI_API_KEY": "bad\nXAI_API_KEY=changed"})
        self.assertEqual(raw, self.path.read_bytes())

    def test_symlink_is_refused(self):
        target = self.path.with_name("target")
        target.write_text("unique\n")
        self.path.symlink_to(target)
        with self.assertRaises(ValueError):
            owner.update(self.path, {"GEMINI_API_KEY": "gemini"})
        self.assertEqual("unique\n", target.read_text())


if __name__ == "__main__":
    unittest.main()
