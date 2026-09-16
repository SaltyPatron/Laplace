#!/usr/bin/env python3
"""Exercise actual OAuth file preservation and replacement without service access."""
import importlib.util
import os
from pathlib import Path
import stat
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("identity_owner",
    Path(__file__).with_name("update-identity-secrets.py"))
owner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(owner)

class IdentitySecretsTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="identity-owner-")
        self.path = Path(self.directory.name) / "identity.env"
        self.microsoft = dict(zip(owner.PROVIDERS[0], ("ms-id", "ms-secret")))
        self.google = dict(zip(owner.PROVIDERS[1], ("google-id", "google-secret")))
    def tearDown(self):
        self.directory.cleanup()
    def seed(self):
        raw = b"# existing configuration\n" + "".join(
            key + "=" + value + "\n" for key, value in
            {**self.microsoft, **self.google}.items()).encode() + b"OTHER=retained\n"
        self.path.write_bytes(raw)
        self.path.chmod(0o640)
        return raw
    def test_missing_inputs_preserve_existing_bytes_and_inode(self):
        raw = self.seed()
        before = self.path.stat()
        self.assertFalse(owner.update(self.path, {}))
        self.assertEqual(self.path.read_bytes(), raw)
        self.assertEqual(self.path.stat().st_ino, before.st_ino)
    def test_empty_workflow_bindings_preserve_existing_providers(self):
        raw = self.seed()
        env = {key: "" for keys in owner.PROVIDERS for key in keys}
        self.assertFalse(owner.update(self.path, env))
        self.assertEqual(self.path.read_bytes(), raw)
    def test_one_explicit_provider_retains_other_provider_and_unknown_lines(self):
        self.seed()
        self.path.chmod(0o600)
        before = self.path.stat()
        next_ms = dict(zip(owner.PROVIDERS[0], ("next-id", "next-secret")))
        self.assertTrue(owner.update(self.path, next_ms))
        raw = self.path.read_text()
        for key, value in {**next_ms, **self.google}.items():
            self.assertEqual(raw.count(key + "="), 1)
            self.assertIn(key + "=" + value + "\n", raw)
        self.assertIn("# existing configuration\n", raw)
        self.assertIn("OTHER=retained\n", raw)
        after = self.path.stat()
        self.assertEqual((after.st_uid, after.st_gid), (before.st_uid, before.st_gid))
        self.assertEqual(stat.S_IMODE(after.st_mode), 0o600)
    def test_partial_provider_refused_before_any_write(self):
        raw = self.seed()
        with self.assertRaises(ValueError):
            owner.update(self.path, {owner.PROVIDERS[0][0]: "new-id"})
        self.assertEqual(self.path.read_bytes(), raw)
    def test_missing_initial_configuration_is_created_without_enabled_providers(self):
        self.assertTrue(owner.update(self.path, {}))
        self.assertEqual(self.path.read_bytes(), b"")
        self.assertEqual(stat.S_IMODE(self.path.stat().st_mode), 0o640)
    def test_symlink_retained_and_refused(self):
        target = self.path.with_name("retained")
        target.write_text("unique\n")
        self.path.symlink_to(target)
        with self.assertRaises(ValueError):
            owner.update(self.path, self.microsoft)
        self.assertTrue(self.path.is_symlink())
        self.assertEqual(target.read_text(), "unique\n")
    def test_multiline_value_refused_without_clobbering_existing_file(self):
        raw = self.seed()
        bad = dict(self.microsoft)
        bad[owner.PROVIDERS[0][1]] = "unsafe\nOTHER=changed"
        with self.assertRaises(ValueError):
            owner.update(self.path, bad)
        self.assertEqual(self.path.read_bytes(), raw)

if __name__ == "__main__":
    unittest.main()
