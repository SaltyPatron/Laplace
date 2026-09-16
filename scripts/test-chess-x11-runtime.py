#!/usr/bin/env python3
"""Selection/refusal controls using local fixtures; these do not prove Qt or apt acquisition."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location(
    "original_x11_owner", Path(__file__).with_name("chess-x11-runtime.py"))
owner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(owner)


class RuntimeSelectionControls(unittest.TestCase):
    def fixture(self, directory, *, private=False):
        root = directory / "runtime"
        binary = root / "usr/bin"
        binary.mkdir(parents=True)
        for relative in ("usr/lib/x86_64-linux-gnu", "lib/x86_64-linux-gnu"):
            (root / relative).mkdir(parents=True)
        tools = {}
        for name in owner.TOOLS:
            path = binary / name
            path.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
            path.chmod(0o755)
            tools[name] = str(path)
        observation = {"tools": tools, "loaded_files": {path: owner.digest(path) for path in tools.values()}}
        document = {"root": str(root), "runtime_id": "a" * 64, "manifest_sha256": "b" * 64,
                    "packages": [{"name": "xvfb", "version": "fixture", "sha256": "c" * 64}],
                    "tools": {name: tools[name] for name in owner.core.TOOLS},
                    "files": {"usr/bin/" + name: {"sha256": owner.digest(path)}
                              for name, path in tools.items()}}
        acquisition = root.parent / "acquisition"
        (acquisition / "logs").mkdir(parents=True)
        document["sources"] = "fixture authenticated source configuration\n"
        (acquisition / "sources.list").write_text(document["sources"], encoding="utf-8")
        document["apt_index_sha256"] = {}
        for name in ("archive_jammy_InRelease", "archive_updates_InRelease", "security_jammy_InRelease"):
            path = acquisition / name
            path.write_text("fixture signed index " + name, encoding="utf-8")
            document["apt_index_sha256"][name] = owner.digest(path)
        for name in ("update.log", "selection.txt"):
            (acquisition / "logs" / name).write_text("fixture " + name, encoding="utf-8")
        selection = {"schema": owner.SCHEMA, "status": "tools-selected", "mode": "private" if private else "host",
                     "gui_ready": False, **observation}
        if private:
            selection["private_runtime"] = {
                "root": document["root"], "manifest_root": str(directory),
                "runtime_id": document["runtime_id"], "manifest_sha256": document["manifest_sha256"],
                "packages": document["packages"]}
        return observation, document, selection

    def write_selection(self, path, selection):
        selection = dict(selection)
        selection.pop("selection_sha256", None)
        selection["selection_sha256"] = hashlib.sha256(owner.core.canonical(selection)).hexdigest()
        owner.save(path, selection)
        return selection

    def test_complete_host_tools_never_load_or_provision_private_packages(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            observed, _, _ = self.fixture(directory)
            with mock.patch.object(owner.shutil, "which", side_effect=lambda name: observed["tools"][name]), \
                 mock.patch.object(owner, "snapshot", return_value=observed), \
                 mock.patch.object(owner, "package_versions", return_value={"available": False}), \
                 mock.patch.object(owner.core, "load") as load, \
                 mock.patch.object(owner.core, "provision") as provision:
                first = owner.ensure(directory / "private-store", time.monotonic() + 30)
                second = owner.ensure(directory / "private-store", time.monotonic() + 30)
            self.assertEqual(first, second)
            self.assertEqual("host", first["mode"])
            self.assertFalse(first["gui_ready"])
            load.assert_not_called()
            provision.assert_not_called()


    def test_private_tools_already_on_path_still_require_authenticated_manifest(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            observed, document, _ = self.fixture(directory)
            with mock.patch.object(owner.shutil, "which", side_effect=lambda name: observed["tools"][name]), \
                 mock.patch.object(owner, "snapshot", return_value=observed), \
                 mock.patch.object(owner, "package_versions", return_value={"available": False}), \
                 mock.patch.object(owner.core, "load", return_value=document) as load, \
                 mock.patch.object(owner.core, "provision") as provision:
                selected = owner.ensure(directory, time.monotonic() + 30)
                self.assertEqual("private", selected["mode"])
                self.assertEqual([], selected["initially_missing_tools"])
                load.assert_called_once_with(directory)
                provision.assert_not_called()
                load.side_effect = RuntimeError("private package bytes changed")
                with self.assertRaisesRegex(RuntimeError, "bytes changed"):
                    owner.ensure(directory, time.monotonic() + 30)

    def test_cached_private_runtime_is_authenticated_and_script_retained_without_reprovision(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            observed, document, _ = self.fixture(directory)
            with mock.patch.object(owner.shutil, "which", return_value=None), \
                 mock.patch.object(owner, "snapshot", return_value=observed), \
                 mock.patch.object(owner, "package_versions", return_value={"available": False}), \
                 mock.patch.object(owner.core, "load", return_value=document) as load, \
                 mock.patch.object(owner.core, "provision") as provision:
                selected = owner.ensure(directory, time.monotonic() + 30, directory / "evidence")
            load.assert_called_once_with(directory)
            provision.assert_not_called()
            self.assertEqual("private", selected["mode"])
            self.assertEqual(document["manifest_sha256"], selected["private_runtime"]["manifest_sha256"])
            self.assertEqual(document, json.loads((directory / "evidence/current.json").read_text()))
            self.assertIn(observed["tools"]["xvfb-run"], selected["loaded_files"])
            self.assertEqual(document["sources"], (directory / "evidence/sources.list").read_text())
            self.assertEqual("fixture update.log", (directory / "evidence/logs/update.log").read_text())
            for name, sha in document["apt_index_sha256"].items():
                self.assertEqual(sha, owner.digest(directory / "evidence" / name))


    def test_cached_acquisition_copy_rejects_changed_signed_index_without_redownload(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            observed, document, _ = self.fixture(directory)
            acquisition = Path(document["root"]).parent / "acquisition"
            (acquisition / next(iter(document["apt_index_sha256"]))).write_text("changed", encoding="utf-8")
            with mock.patch.object(owner.shutil, "which", return_value=None), \
                 mock.patch.object(owner, "snapshot", return_value=observed), \
                 mock.patch.object(owner, "package_versions", return_value={"available": False}), \
                 mock.patch.object(owner.core, "load", return_value=document), \
                 mock.patch.object(owner.core, "provision") as provision:
                with self.assertRaisesRegex(RuntimeError, "index bytes differ"):
                    owner.ensure(directory, time.monotonic() + 30, directory / "evidence")
                provision.assert_not_called()

    def test_missing_runtime_uses_shared_provision_owner_and_cannot_hide_its_failure(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            observed, document, _ = self.fixture(directory)
            deadline = time.monotonic() + 30
            evidence = directory / "evidence"
            with mock.patch.object(owner.shutil, "which", return_value=None), \
                 mock.patch.object(owner, "snapshot", return_value=observed), \
                 mock.patch.object(owner, "package_versions", return_value={"available": False}), \
                 mock.patch.object(owner.core, "load", return_value=None), \
                 mock.patch.object(owner.core, "provision", return_value=document) as provision:
                selected = owner.ensure(directory, deadline, evidence)
                provision.assert_called_once_with(directory, deadline, evidence)
                self.assertEqual("private", selected["mode"])
                provision.side_effect = RuntimeError("signed index failed")
                with self.assertRaisesRegex(RuntimeError, "signed index"):
                    owner.ensure(directory, deadline, evidence)

    def test_xvfb_run_script_is_hashed_separately_from_elf_dependency_resolution(self):
        with tempfile.TemporaryDirectory() as temporary:
            observed, _, _ = self.fixture(Path(temporary))
            script = observed["tools"]["xvfb-run"]
            elf_tools = {name: path for name, path in observed["tools"].items() if name != "xvfb-run"}
            elf_files = {path: sha for path, sha in observed["loaded_files"].items() if path != script}
            environment = {"PATH": str(Path(script).parent)}
            with mock.patch.object(owner.core, "loaded_files", return_value=(elf_tools, elf_files)) as resolver:
                result = owner.snapshot(environment, time.monotonic() + 30)
            self.assertEqual(observed, result)
            self.assertEqual(environment, resolver.call_args.args[0])

    def test_tampered_script_or_receipt_or_failed_selection_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            observed, _, selection = self.fixture(directory)
            receipt = directory / "selection.json"
            sealed = self.write_selection(receipt, selection)
            self.assertEqual(sealed, owner.load_selection(receipt))
            script = Path(observed["tools"]["xvfb-run"])
            script.write_text("changed script", encoding="utf-8")
            with self.assertRaisesRegex(RuntimeError, "changed"):
                owner.load_selection(receipt)
            altered = {**sealed, "mode": "private"}
            owner.save(receipt, altered)
            with self.assertRaisesRegex(RuntimeError, "identity"):
                owner.load_selection(receipt)
            self.write_selection(receipt, {**selection, "status": "failed"})
            with self.assertRaisesRegex(RuntimeError, "status"):
                owner.load_selection(receipt)


    def test_private_manifest_tool_selection_cannot_be_shadowed_by_caller_path(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            observed, document, _ = self.fixture(directory)
            changed = {**observed, "tools": {**observed["tools"], "xauth": "/other/xauth"}}
            with mock.patch.object(owner.shutil, "which", return_value=None), \
                 mock.patch.object(owner, "snapshot", return_value=changed), \
                 mock.patch.object(owner, "package_versions", return_value={"available": False}), \
                 mock.patch.object(owner.core, "load", return_value=document):
                with self.assertRaisesRegex(RuntimeError, "private manifest"):
                    owner.ensure(directory, time.monotonic() + 30)

    def test_private_selection_requires_same_authenticated_current_manifest(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            _, document, selection = self.fixture(directory, private=True)
            receipt = directory / "selection.json"
            sealed = self.write_selection(receipt, selection)
            with mock.patch.object(owner.core, "load", return_value=document):
                self.assertEqual(sealed, owner.load_selection(receipt))
            for current in (None, {**document, "manifest_sha256": "d" * 64}):
                with self.subTest(current=current), \
                     mock.patch.object(owner.core, "load", return_value=current):
                    with self.assertRaisesRegex(RuntimeError, "selected manifest"):
                        owner.load_selection(receipt)

    def test_qt_first_environment_restores_private_libraries_after_gui_overlay(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            _, _, selection = self.fixture(directory, private=True)
            qt = directory / "qt"
            result = owner.selected_environment(
                selection, {"PATH": "/usr/bin", "LD_LIBRARY_PATH": str(qt / "lib") + ":/host/lib"},
                qt_prefix=qt)
            root = Path(selection["private_runtime"]["root"])
            self.assertEqual([str(qt / "lib"), str(root / "usr/lib/x86_64-linux-gnu"),
                              str(root / "lib/x86_64-linux-gnu"), "/host/lib"],
                             result["LD_LIBRARY_PATH"].split(os.pathsep))
            self.assertEqual([str(qt / "bin"), str(root / "usr/bin"), "/usr/bin"],
                             result["PATH"].split(os.pathsep))

    def test_changed_resolution_and_changed_tool_bytes_are_rejected(self):
        selected = {"tools": {"xvfb-run": "/selected/xvfb-run"},
                    "loaded_files": {"/selected/xvfb-run": "a" * 64}}
        with self.assertRaisesRegex(RuntimeError, "paths"):
            owner.check_tool_selection(selected, {"tools": {"xvfb-run": "/other/xvfb-run"}, "loaded_files": {}})
        with self.assertRaisesRegex(RuntimeError, "bytes"):
            owner.check_tool_selection(selected, {"tools": selected["tools"],
                                                  "loaded_files": {"/selected/xvfb-run": "b" * 64}})

    def test_dependency_resolution_failure_cannot_create_successful_selection(self):
        with mock.patch.object(owner.shutil, "which", return_value="/fixture/tool"), \
             mock.patch.object(owner, "snapshot", side_effect=RuntimeError("unresolved shared library")):
            with self.assertRaisesRegex(RuntimeError, "unresolved"):
                owner.ensure(Path("/fixture"), time.monotonic() + 30)

    def test_cli_preserves_failed_acquisition_receipt_without_gui_ready_claim(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "selection.json"
            with mock.patch.object(owner, "ensure", side_effect=RuntimeError("signed archive unavailable")), \
                 mock.patch("builtins.print"):
                result = owner.main(["--output", str(output)])
            self.assertEqual(1, result)
            retained = json.loads(output.read_text())
            self.assertEqual("failed", retained["status"])
            self.assertFalse(retained["gui_ready"])
            self.assertEqual("RuntimeError", retained["error_type"])
            self.assertIn("signed archive", retained["error"])


if __name__ == "__main__":
    unittest.main()
