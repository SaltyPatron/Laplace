#!/usr/bin/env python3
"""Filesystem/acquisition controls for the pinned CMake owner, not compiler qualification."""
import hashlib
import importlib.util
import io
import json
import os
import shlex
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

    def archive(self, invalid=False, executable_failure=False, command_fixture=False):
        output = io.BytesIO()
        with tarfile.open(fileobj=output, mode="w:gz") as archive:
            rows = [(f"bin/{tool}", f"#!/bin/sh\n{'exit 8' if executable_failure else 'echo '+tool+' version '+self.lock['version']}\n".encode(), 0o755)
                    for tool in owner.TOOLS]
            if command_fixture:
                rows = []
                for tool in owner.TOOLS:
                    program = (
                        f"#!{sys.executable}\n"
                        "import json, os, sys\n"
                        f"tool = {tool!r}\n"
                        "if sys.argv[1:] == ['--version']:\n"
                        f"    print(tool + ' version {self.lock['version']}')\n"
                        "    raise SystemExit(0)\n"
                        "print(json.dumps({'tool': tool, 'argv': sys.argv[1:], "
                        "'cwd': os.getcwd(), 'selected': sys.argv[0]}))\n"
                        "raise SystemExit(23)\n"
                    )
                    rows.append((f"bin/{tool}", program.encode(), 0o755))
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

    def test_restrictive_umask_keeps_new_namespace_and_package_runner_readable(self):
        self.root = self.base / "prefix" / "tools" / "cmake"
        raw = self.archive()
        previous = os.umask(0o077)
        try:
            selected = self.select(raw)
        finally:
            os.umask(previous)
        for path in (self.base / "prefix", self.root.parent, self.root,
                     selected.parent, *selected.parent.rglob("*")):
            if path.is_dir() and not path.is_symlink():
                self.assertEqual(0o555, path.stat().st_mode & 0o555, str(path))
        self.assertEqual(0o644, (selected.parent / owner.RECEIPT).stat().st_mode & 0o777)
        self.assertEqual(0o755, (selected / "cmake").stat().st_mode & 0o777)
        self.assertEqual(0o644, (selected.parent / "share/cmake/module.cmake").stat().st_mode & 0o777)
        with mock.patch.object(owner.urllib.request, "urlopen", side_effect=AssertionError("network")):
            self.assertEqual(selected, owner.select(self.root, self.work, self.lock))

    def test_new_namespace_does_not_chmod_existing_ancestor(self):
        prefix = self.base / "existing-prefix"
        prefix.mkdir()
        prefix.chmod(0o750)
        self.root = prefix / "tools" / "cmake"
        previous = os.umask(0o077)
        try:
            self.select(self.archive())
        finally:
            os.umask(previous)
        self.assertEqual(0o750, prefix.stat().st_mode & 0o777)
        for path in (prefix / "tools", self.root):
            self.assertEqual(0o555, path.stat().st_mode & 0o555)

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


    def execution_fixture(self):
        self.root = self.base / "prefix/tools/cmake"
        selected = self.select(self.archive(command_fixture=True))
        repository = self.base / "repository"
        (repository / "scripts").mkdir(parents=True)
        (repository / "deploy").mkdir()
        script = repository / "scripts/provision-cmake.py"
        script.write_bytes((ROOT / "scripts/provision-cmake.py").read_bytes())
        (repository / "deploy/cmake-release.json").write_text(json.dumps(self.lock))
        poison = self.base / "poison"
        poison.mkdir()
        marker = self.base / "ambient-tool-was-used"
        for tool in owner.TOOLS:
            path = poison / tool
            path.write_text(
                "#!/bin/sh\n"
                'printf "%s\\n" "$0" >> "$AMBIENT_TOOL_MARKER"\nexit 91\n')
            path.chmod(0o755)
        environment = dict(os.environ,
                           PATH=str(poison) + os.pathsep + os.environ.get("PATH", ""),
                           AMBIENT_TOOL_MARKER=str(marker), PYTHONDONTWRITEBYTECODE="1",
                           LAPLACE_INSTALL_PREFIX=str(self.base / "prefix"),
                           LAPLACE_WORK_ROOT=str(self.work.parent))
        return selected, repository, script, environment, marker

    def run_tool(self, script, environment, tool, arguments):
        return subprocess.run(
            [sys.executable, str(script), "--root", str(self.root),
             "--work", str(self.work), "--exec-tool", tool, "--", *arguments],
            cwd=self.base, env=environment, text=True, capture_output=True, timeout=30)

    def test_exec_tool_preserves_selected_executable_argv_stdout_and_status(self):
        selected, _repository, script, environment, marker = self.execution_fixture()
        arguments = ["--test-dir", "a path with spaces", "--", "-L", "α\nβ"]
        for tool in owner.TOOLS:
            with self.subTest(tool=tool):
                completed = self.run_tool(script, environment, tool, arguments)
                self.assertEqual(23, completed.returncode, completed.stderr)
                output = json.loads(completed.stdout)
                self.assertEqual({"tool": tool, "argv": arguments, "cwd": str(self.base),
                                  "selected": str(selected / tool)}, output)
                self.assertIn(str(selected / tool), completed.stderr)
                self.assertFalse(marker.exists())
        # The original selection-only CLI still returns only the bin path.
        completed = subprocess.run(
            [sys.executable, str(script), "--root", str(self.root), "--work", str(self.work)],
            cwd=self.base, env=environment, text=True, capture_output=True, timeout=30)
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertEqual(str(selected) + "\n", completed.stdout)

    def test_exec_tool_missing_or_corrupt_generation_never_uses_ambient_tool(self):
        selected, _repository, script, environment, marker = self.execution_fixture()
        generation = selected.parent
        retained = self.base / "retained-generation"
        generation.rename(retained)
        try:
            completed = self.run_tool(script, environment, "ctest", ["-N"])
            self.assertEqual(1, completed.returncode)
            self.assertIn("not installed", completed.stderr)
            self.assertEqual("", completed.stdout)
            self.assertFalse(generation.exists())
            self.assertFalse(marker.exists())
        finally:
            retained.rename(generation)
        module = generation / "share/cmake/module.cmake"
        module.write_text("# altered selected generation\n")
        completed = self.run_tool(script, environment, "ctest", ["-N"])
        self.assertEqual(1, completed.returncode)
        self.assertIn("bytes or links", completed.stderr)
        self.assertEqual("", completed.stdout)
        self.assertFalse(marker.exists())
        self.assertEqual("# altered selected generation\n", module.read_text())

    def test_private_database_and_registry_call_the_verified_companion_tools(self):
        selected, repository, _script, environment, marker = self.execution_fixture()
        # Run the exact shell helper used by private install/discovery/execution
        # against a private retained package; never start a database.
        source = (ROOT / "scripts/pr-db-proof.sh").read_text()
        start = source.index("cmake_tool() {\n")
        end = source.index("\n}\n", start) + len("\n}\n")
        function = source[start:end]
        shell = (f"ROOT={shlex.quote(str(repository))}\n"
                 f"INSTALL_PREFIX={shlex.quote(str(self.base / 'prefix'))}\n"
                 + function + '\ncmake_tool "$@"\n')
        for tool, arguments in (
                ("cmake", ["--install", "selected build"]),
                ("ctest", ["--test-dir", "selected build", "--show-only=json-v1", "-L", "regress"])):
            completed = subprocess.run(
                ["bash", "-c", shell, "fixture", tool, "--", *arguments],
                cwd=self.base, env=environment, text=True, capture_output=True, timeout=30)
            self.assertEqual(23, completed.returncode, completed.stderr)
            output = json.loads(completed.stdout)
            self.assertEqual(str(selected / tool), output["selected"])
            self.assertEqual(arguments, output["argv"])

        registry_spec = importlib.util.spec_from_file_location(
            "cmake_test_profile_fixture", ROOT / "scripts/test-profile-registry.py")
        registry = importlib.util.module_from_spec(registry_spec)
        registry_spec.loader.exec_module(registry)
        suite = {"runner": "ctest", "selector": {"include_label": "regress"}}
        with mock.patch.object(registry, "ROOT", repository), \
             mock.patch.object(registry.sys, "platform", "linux"), \
             mock.patch.dict(os.environ, environment, clear=True):
            for list_only in (True, False):
                command = registry.command_for_suite(suite, list_only=list_only)
                completed = subprocess.run(
                    command, cwd=self.base, env=environment, text=True,
                    capture_output=True, timeout=30)
                self.assertEqual(23, completed.returncode, completed.stderr)
                output = json.loads(completed.stdout)
                self.assertEqual(str(selected / "ctest"), output["selected"])
                self.assertEqual(["--test-dir", "build"], output["argv"][:2])
                self.assertEqual(["-L", "regress"], output["argv"][-2:])
                self.assertEqual(list_only, "-N" in output["argv"])
            with mock.patch.object(registry.sys, "platform", "win32"):
                self.assertEqual(["ctest"], registry.ctest_command_prefix())
        self.assertFalse(marker.exists())



if __name__ == "__main__":
    unittest.main()
