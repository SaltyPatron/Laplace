#!/usr/bin/env python3
"""Tests for the pinned CMake provisioner itself."""
from __future__ import annotations

import hashlib
import importlib.util
import io
import json
import os
import subprocess
import sys
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

    def archive(self, *, invalid=False, executable_failure=False, command_fixture=False):
        output = io.BytesIO()
        with tarfile.open(fileobj=output, mode="w:gz") as archive:
            rows = []
            for tool in owner.TOOLS:
                if command_fixture:
                    program = (
                        f"#!{sys.executable}\n"
                        "import json, os, sys\n"
                        f"tool={tool!r}\n"
                        "if sys.argv[1:] == ['--version']:\n"
                        f" print(tool + ' version {self.lock['version']}'); raise SystemExit(0)\n"
                        "print(json.dumps({'tool':tool,'argv':sys.argv[1:],'cwd':os.getcwd(),'selected':sys.argv[0]}))\n"
                        "raise SystemExit(23)\n"
                    ).encode()
                else:
                    command = "exit 8" if executable_failure else f"echo {tool} version {self.lock['version']}"
                    program = f"#!/bin/sh\n{command}\n".encode()
                rows.append((f"bin/{tool}", program, 0o755))
            rows.append(("share/cmake/module.cmake", b"# fixture module\n", 0o644))
            if invalid:
                rows.append(("../outside", b"refuse", 0o644))
            for name, content, mode in rows:
                entry = tarfile.TarInfo(self.lock["top_directory"] + "/" + name)
                entry.size = len(content)
                entry.mode = mode
                archive.addfile(entry, io.BytesIO(content))
        raw = output.getvalue()
        self.lock.update(bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest())
        return raw

    def select(self, raw):
        with mock.patch.object(owner.urllib.request, "urlopen", return_value=io.BytesIO(raw)) as network:
            result = owner.select(self.root, self.work, self.lock, ensure=True)
            self.assertEqual(1, network.call_count)
            return result

    def test_verified_generation_reuses_without_network(self):
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
            with self.subTest(field=name):
                previous = self.lock[name]
                self.lock[name] = value
                with mock.patch.object(owner.urllib.request, "urlopen", return_value=io.BytesIO(raw)):
                    with self.assertRaises(RuntimeError):
                        owner.select(self.root, self.work, self.lock, ensure=True)
                self.assertFalse((self.root / self.lock["version"]).exists())
                self.lock[name] = previous

    def test_archive_traversal_is_rejected(self):
        with self.assertRaisesRegex(RuntimeError, "escapes"):
            self.select(self.archive(invalid=True))
        self.assertFalse((self.root / self.lock["version"]).exists())

    def test_failed_real_tool_process_prevents_publication(self):
        with self.assertRaisesRegex(RuntimeError, "did not report"):
            self.select(self.archive(executable_failure=True))
        self.assertFalse((self.root / self.lock["version"]).exists())

    def test_exec_tool_runs_the_selected_companion_binary(self):
        selected = self.select(self.archive(command_fixture=True))
        arguments = ["--test-dir", "a path with spaces", "--", "-L", "regress"]
        script = ROOT / "scripts/provision-cmake.py"
        env = dict(os.environ, PYTHONDONTWRITEBYTECODE="1")
        for tool in owner.TOOLS:
            completed = subprocess.run(
                [sys.executable, str(script), "--root", str(self.root), "--work", str(self.work),
                 "--exec-tool", tool, "--", *arguments],
                cwd=self.base, env=env, text=True, capture_output=True, timeout=30)
            self.assertEqual(23, completed.returncode, completed.stderr)
            output = json.loads(completed.stdout)
            self.assertEqual(str(selected / tool), output["selected"])
            self.assertEqual(arguments, output["argv"])


if __name__ == "__main__":
    unittest.main()
