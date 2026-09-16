#!/usr/bin/env python3
"""Filesystem/acquisition controls for the pinned CMake owner, not compiler qualification."""
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("cmake_owner", ROOT / "scripts/provision-cmake.py")
owner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(owner)


class CMakeProvisionTests(unittest.TestCase):
    def setUp(self):
        workspace = os.environ.get("TMPDIR")
        if not workspace or not Path(workspace).is_absolute() or not Path(workspace).is_dir():
            self.fail("TMPDIR must select an existing permanent test workspace")
        self.temporary = tempfile.TemporaryDirectory(prefix="cmake-owner-", dir=workspace)
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        self.root = self.base / "tools"
        self.work = self.base / "work"
        self.lock = json.loads(owner.LOCK.read_text())
        for name, value in (("system", "Linux"), ("machine", "x86_64")):
            patcher = mock.patch.object(owner.platform, name, return_value=value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def archive(self, invalid=False, executable_failure=False):
        output = io.BytesIO()
        with tarfile.open(fileobj=output, mode="w:gz") as archive:
            rows = [(f"bin/{tool}", f"#!/bin/sh\n{'exit 8' if executable_failure else 'echo '+tool+' version '+self.lock['version']}\n".encode(), 0o755)
                    for tool in owner.TOOLS]
            rows.append(("share/cmake/module.cmake", b"# fixture module\n", 0o644))
            if invalid:
                rows.append(("../outside", b"refuse", 0o644))
            for name, content, mode in rows:
                entry = tarfile.TarInfo(self.lock["top_directory"] + "/" + name)
                entry.size, entry.mode = len(content), mode
                archive.addfile(entry, io.BytesIO(content))
        raw = output.getvalue()
        self.lock.update(bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest())
        return raw

    def select(self, raw):
        with mock.patch.object(owner.urllib.request, "urlopen", return_value=io.BytesIO(raw)) as response:
            result = owner.select(self.root, self.work, self.lock, ensure=True)
            self.assertEqual(1, response.call_count)
            return result

    def test_complete_verified_generation_reuses_without_network(self):
        selected = self.select(self.archive())
        self.assertEqual(self.root / self.lock["version"] / "bin", selected)
        with mock.patch.object(owner.urllib.request, "urlopen", side_effect=AssertionError("network")):
            self.assertEqual(selected, owner.select(self.root, self.work, self.lock, ensure=True))

    def test_missing_generation_read_only_selection_does_not_acquire(self):
        with mock.patch.object(owner.urllib.request, "urlopen") as network:
            with self.assertRaisesRegex(RuntimeError, "not installed"):
                owner.select(self.root, self.work, self.lock)
            network.assert_not_called()
        self.assertFalse(self.root.exists())

    def test_hash_or_byte_count_failure_never_publishes(self):
        raw = self.archive()
        for name, value in (("sha256", "0" * 64), ("bytes", len(raw) - 1), ("bytes", len(raw) + 1)):
            with self.subTest(field=name, value=value):
                previous = self.lock[name]
                self.lock[name] = value
                with mock.patch.object(owner.urllib.request, "urlopen", return_value=io.BytesIO(raw)):
                    with self.assertRaises(RuntimeError):
                        owner.select(self.root, self.work, self.lock, ensure=True)
                self.assertFalse((self.root / self.lock["version"]).exists())
                self.lock[name] = previous

    def test_cached_module_mutation_refuses_without_repair(self):
        selected = self.select(self.archive())
        module = selected.parent / "share/cmake/module.cmake"
        module.write_text("# changed\n")
        with mock.patch.object(owner.urllib.request, "urlopen") as network:
            with self.assertRaisesRegex(RuntimeError, "bytes or links"):
                owner.select(self.root, self.work, self.lock, ensure=True)
            network.assert_not_called()
        self.assertEqual("# changed\n", module.read_text())

    def test_archive_traversal_never_publishes_or_writes_outside(self):
        with self.assertRaisesRegex(RuntimeError, "escapes"):
            self.select(self.archive(invalid=True))
        self.assertFalse((self.root / self.lock["version"]).exists())
        self.assertFalse((self.root / "outside").exists())

    def test_failed_real_tool_process_prevents_publication(self):
        with self.assertRaisesRegex(RuntimeError, "did not report"):
            self.select(self.archive(executable_failure=True))
        self.assertFalse((self.root / self.lock["version"]).exists())

    def test_selected_generation_link_and_recipe_drift_refuse(self):
        selected = self.select(self.archive())
        lock = {**self.lock, "url": "https://example.invalid/other"}
        with self.assertRaisesRegex(RuntimeError, "selected release"):
            owner.select(self.root, self.work, lock)
        alias = self.base / "alias"
        alias.mkdir()
        (alias / self.lock["version"]).symlink_to(selected.parent, target_is_directory=True)
        with self.assertRaisesRegex(RuntimeError, "link"):
            owner.select(alias, self.work, self.lock)

    def test_failed_network_retains_no_pending_generation(self):
        with mock.patch.object(owner.urllib.request, "urlopen", side_effect=TimeoutError("fixture timeout")):
            with self.assertRaises(TimeoutError):
                owner.select(self.root, self.work, self.lock, ensure=True)
        self.assertFalse((self.root / self.lock["version"]).exists())
        self.assertEqual([], list(self.work.iterdir()))


if __name__ == "__main__":
    unittest.main()
