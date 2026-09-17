#!/usr/bin/env python3
"""Isolated ownership, preservation, and activation tests for the user installer."""
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location(
    "cutechess_user_installer", ROOT / "scripts/setup-cutechess-user-session.py")
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class UserInstallerTests(unittest.TestCase):
    def setUp(self):
        previous_umask = os.umask(0o077)
        self.addCleanup(os.umask, previous_umask)
        work = Path(os.environ.get("TMPDIR", str(ROOT / "build/test-cutechess-user-install")))
        self.assertTrue(work.is_absolute())
        self.assertFalse(work == Path("/tmp") or Path("/tmp") in work.parents)
        self.assertFalse(work == Path("/var/tmp") or Path("/var/tmp") in work.parents)
        work.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="user-session-", dir=work)
        self.addCleanup(self.temporary.cleanup)
        self.home = Path(self.temporary.name) / "home"
        self.home.mkdir(mode=0o700)
        self.uid = os.getuid()
        self.runtime = {"runtime_id": "a" * 64}
        self.selected = {}
        for name, source in dict(installer.SOURCE_FILES,
                                 installer="scripts/setup-cutechess-user-session.py").items():
            data = (name + " fixture\n").encode()
            self.selected[name] = {"data": data, "source": source,
                                   "sha256": installer.sha256(data),
                                   "git_blob": installer.git_blob(data)}
        self.files, self.receipt = installer.make_plan(
            self.home, self.uid, self.selected, self.runtime)

    def snapshots(self):
        result = {}
        for path in self.home.rglob("*"):
            if path.is_file() and not path.is_symlink():
                info = path.stat()
                result[str(path.relative_to(self.home))] = (
                    path.read_bytes(), stat.S_IMODE(info.st_mode), info.st_ino)
        return result

    def write(self, path, data, mode=0o600):
        path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        path.write_bytes(data)
        path.chmod(mode)

    def test_initial_install_is_private_and_records_exact_sources(self):
        self.assertTrue(installer.install_files(self.home, self.uid, self.files))
        for path, (data, mode) in self.files.items():
            info = Path(path).stat()
            self.assertEqual(Path(path).read_bytes(), data)
            self.assertEqual(info.st_uid, self.uid)
            self.assertEqual(stat.S_IMODE(info.st_mode), mode)
        receipt_path = installer.layout(self.home)[0] / "install-receipt.json"
        receipt = json.loads(receipt_path.read_bytes())
        self.assertEqual(receipt["sources"], self.receipt["sources"])
        self.assertEqual(receipt["runtime"]["runtime_id"], self.runtime["runtime_id"])
        self.assertEqual(receipt["uid"], self.uid)
        self.assertEqual(len(receipt["files"]), 5)

    def test_identical_reinstall_preserves_bytes_modes_and_inodes(self):
        installer.install_files(self.home, self.uid, self.files)
        before = self.snapshots()
        self.assertFalse(installer.install_files(self.home, self.uid, self.files))
        self.assertEqual(self.snapshots(), before)

    def test_unknown_existing_unit_is_preserved_without_partial_install(self):
        unit = installer.layout(self.home)[2] / installer.UNIT_NAME
        self.write(unit, b"operator-owned existing unit\n")
        before = self.snapshots()
        with self.assertRaisesRegex(installer.SetupError, "no installation receipt"):
            installer.install_files(self.home, self.uid, self.files)
        self.assertEqual(self.snapshots(), before)

    def test_identical_unreceipted_file_is_not_adopted(self):
        path, (data, mode) = next(iter(self.files.items()))
        self.write(Path(path), data, mode)
        with self.assertRaisesRegex(installer.SetupError, "no installation receipt"):
            installer.install_files(self.home, self.uid, self.files)

    def test_local_change_is_preserved_with_receipt(self):
        installer.install_files(self.home, self.uid, self.files)
        launcher = installer.layout(self.home)[0] / "laplace-cutechess-user-session"
        launcher.write_bytes(b"local operator change\n")
        before = self.snapshots()
        with self.assertRaisesRegex(installer.SetupError, "differs"):
            installer.install_files(self.home, self.uid, self.files)
        self.assertEqual(self.snapshots(), before)

    def test_inconsistent_desired_release_is_rejected_without_publication(self):
        installer.install_files(self.home, self.uid, self.files)
        before = self.snapshots()
        changed = dict(self.files)
        path = next(iter(changed))
        changed[path] = (b"new release bytes\n", changed[path][1])
        with self.assertRaisesRegex(installer.SetupError, "differs"):
            installer.install_files(self.home, self.uid, changed)
        self.assertEqual(self.snapshots(), before)

    def test_incomplete_existing_install_is_preserved(self):
        installer.install_files(self.home, self.uid, self.files)
        next(iter(Path(path) for path in self.files)).unlink()
        before = self.snapshots()
        with self.assertRaisesRegex(installer.SetupError, "incomplete"):
            installer.install_files(self.home, self.uid, self.files)
        self.assertEqual(self.snapshots(), before)

    def test_symlink_directory_cannot_redirect_installation(self):
        outside = Path(self.temporary.name) / "outside"
        outside.mkdir(mode=0o700)
        (self.home / ".local").symlink_to(outside, target_is_directory=True)
        with self.assertRaisesRegex(installer.SetupError, "symlinks"):
            installer.install_files(self.home, self.uid, self.files)
        self.assertEqual(list(outside.iterdir()), [])

    def test_symlink_file_cannot_be_adopted(self):
        unit = installer.layout(self.home)[2] / installer.UNIT_NAME
        unit.parent.mkdir(parents=True, mode=0o700)
        outside = Path(self.temporary.name) / "operator-file"
        outside.write_bytes(b"preserve\n")
        unit.symlink_to(outside)
        with self.assertRaises(OSError):
            installer.install_files(self.home, self.uid, self.files)
        self.assertEqual(outside.read_bytes(), b"preserve\n")

    def test_hardlink_file_cannot_be_adopted(self):
        unit = installer.layout(self.home)[2] / installer.UNIT_NAME
        unit.parent.mkdir(parents=True, mode=0o700)
        outside = Path(self.temporary.name) / "operator-file"
        self.write(outside, b"preserve\n")
        os.link(outside, unit)
        with self.assertRaisesRegex(installer.SetupError, "hard-linked"):
            installer.install_files(self.home, self.uid, self.files)
        self.assertEqual(outside.read_bytes(), b"preserve\n")

    def test_private_prefix_mode_drift_is_not_repaired_silently(self):
        prefix = installer.layout(self.home)[0]
        prefix.mkdir(parents=True, mode=0o700)
        prefix.chmod(0o755)
        with self.assertRaisesRegex(installer.SetupError, "private"):
            installer.install_files(self.home, self.uid, self.files)
        self.assertEqual(stat.S_IMODE(prefix.stat().st_mode), 0o755)

    def test_writable_ancestor_is_rejected(self):
        local = self.home / ".local"
        local.mkdir(mode=0o700)
        local.chmod(0o777)
        with self.assertRaisesRegex(installer.SetupError, "writable"):
            installer.install_files(self.home, self.uid, self.files)

    def test_file_mode_drift_is_preserved(self):
        installer.install_files(self.home, self.uid, self.files)
        unit = installer.layout(self.home)[2] / installer.UNIT_NAME
        unit.chmod(0o644)
        before = self.snapshots()
        with self.assertRaisesRegex(installer.SetupError, "file mode"):
            installer.install_files(self.home, self.uid, self.files)
        self.assertEqual(self.snapshots(), before)

    def test_failed_creation_removes_only_files_created_by_this_attempt(self):
        original = installer.create_owned_file
        calls = []

        def fail_third(path, data, mode, uid):
            calls.append(path)
            if len(calls) == 3:
                raise OSError("injected write failure")
            return original(path, data, mode, uid)

        with mock.patch.object(installer, "create_owned_file", side_effect=fail_third):
            with self.assertRaises(OSError):
                installer.install_files(self.home, self.uid, self.files)
        self.assertFalse(any(Path(path).exists() for path in self.files))
        self.assertTrue(installer.install_files(self.home, self.uid, self.files))

    def test_root_and_setuid_invocations_are_rejected(self):
        for real, effective in ((0, 0), (1001, 0), (1001, 1002)):
            with self.subTest(real=real, effective=effective):
                with mock.patch.object(installer.os, "getuid", return_value=real), \
                     mock.patch.object(installer.os, "geteuid", return_value=effective):
                    with self.assertRaisesRegex(installer.SetupError, "nonroot"):
                        installer.current_identity()

    def test_wrong_file_owner_is_rejected(self):
        path = self.home / "owned"
        self.write(path, b"data\n")
        with self.assertRaisesRegex(installer.SetupError, "another account"):
            installer.regular_bytes(path, self.uid + 1, 0o600)

    def test_password_validation_checks_bounded_format_without_exposing_value(self):
        password = self.home / "password"
        self.write(password, b"a" * 64)
        with mock.patch.object(installer, "directory_chain") as directories, \
             mock.patch.object(installer.os, "read", wraps=os.read) as reads, \
             mock.patch.object(installer.Path, "read_bytes", side_effect=AssertionError("no content read")):
            installer.validate_password_file(password, self.uid)
        directories.assert_called_once_with(password.parent, self.uid)
        self.assertEqual(reads.call_count, 1)
        self.assertEqual(reads.call_args.args[1], 65)
        with mock.patch.object(installer, "directory_chain"):
            files, receipt = installer.make_plan(
                self.home, self.uid, self.selected, self.runtime, password)
        configuration = json.loads(files[str(self.home / ".config/laplace/cutechess-session.json")][0])
        self.assertEqual(configuration["transport"], "loopback-password")
        self.assertEqual(configuration["password_file"], str(password))
        self.assertNotIn("a" * 64, json.dumps(receipt["sources"]))

    def test_password_path_auth_syntax_and_metadata_fail_closed(self):
        for name in ("bad,password", "bad(password)", "bad password"):
            with self.subTest(name=name):
                with self.assertRaisesRegex(installer.SetupError, "safe"):
                    installer.validate_password_file(self.home / name, self.uid)
        password = self.home / "password"
        self.write(password, b"a" * 64 + b"\n")
        with mock.patch.object(installer, "directory_chain"):
            with self.assertRaisesRegex(installer.SetupError, "64"):
                installer.validate_password_file(password, self.uid)
        self.write(password, b"a" * 64, mode=0o644)
        with mock.patch.object(installer, "directory_chain"):
            with self.assertRaisesRegex(installer.SetupError, "private"):
                installer.validate_password_file(password, self.uid)

    def manager_probe(self, linger="yes", runtime=None, state="running", returncode=0):
        uid = 1001
        selected_runtime = runtime or "/run/user/1001"
        responses = [
            subprocess.CompletedProcess([], 0, "Linger=" + linger + "\nRuntimePath=" + selected_runtime + "\n", ""),
            subprocess.CompletedProcess([], returncode, state + "\n", ""),
        ]
        run = mock.Mock(side_effect=responses)
        socket_info = SimpleNamespace(st_mode=stat.S_IFSOCK | 0o600, st_uid=uid)
        with mock.patch.object(installer, "directory_chain"), \
             mock.patch.object(installer.Path, "lstat", return_value=socket_info):
            environment = installer.check_user_manager(self.home, uid, run=run)
        return environment, run

    def test_existing_linger_and_running_manager_are_required(self):
        environment, run = self.manager_probe()
        self.assertEqual(environment["DBUS_SESSION_BUS_ADDRESS"], "unix:path=/run/user/1001/bus")
        self.assertEqual(environment["XDG_RUNTIME_DIR"], "/run/user/1001")
        self.assertEqual(run.call_count, 2)
        self.assertTrue(all(call.kwargs["timeout"] == 8 for call in run.call_args_list))
        self.assertFalse(any("enable-linger" in call.args[0] for call in run.call_args_list))
        for options in ({"linger": "no"}, {"runtime": "/run/user/999"},
                        {"state": "offline", "returncode": 1}):
            with self.subTest(options=options):
                with self.assertRaises(installer.SetupError):
                    self.manager_probe(**options)
        self.manager_probe(state="degraded", returncode=1)

    def test_default_install_never_activates_a_service(self):
        run = mock.Mock(side_effect=AssertionError("no manager mutations without --start"))
        with mock.patch.object(installer, "sources", return_value=self.selected):
            result = installer.setup(
                identity=lambda: (self.uid, "fixture", self.home),
                manager=lambda home, uid, **kwargs: {},
                runtime_loader=lambda root: self.runtime, run=run)
        self.assertEqual(result["status"], "installed")
        run.assert_not_called()

    def test_explicit_start_reloads_enables_and_verifies_active_unit(self):
        run = mock.Mock(side_effect=[
            subprocess.CompletedProcess([], 3, "inactive\n", ""),
            subprocess.CompletedProcess([], 0, "", ""),
            subprocess.CompletedProcess([], 0, "", ""),
            subprocess.CompletedProcess([], 0, "active\n", ""),
        ])
        with mock.patch.object(installer, "sources", return_value=self.selected):
            result = installer.setup(
                start=True, identity=lambda: (self.uid, "fixture", self.home),
                manager=lambda home, uid, **kwargs: {},
                runtime_loader=lambda root: self.runtime, run=run)
        self.assertEqual(result["status"], "active")
        self.assertEqual([call.args[0] for call in run.call_args_list], [
            ["/usr/bin/systemctl", "--user", "is-active", installer.UNIT_NAME],
            ["/usr/bin/systemctl", "--user", "daemon-reload"],
            ["/usr/bin/systemctl", "--user", "enable", "--now", installer.UNIT_NAME],
            ["/usr/bin/systemctl", "--user", "is-active", installer.UNIT_NAME],
        ])

    def test_invalid_runtime_does_not_create_installation_files(self):
        with mock.patch.object(installer, "sources", return_value=self.selected):
            with self.assertRaisesRegex(installer.SetupError, "runtime rejected"):
                installer.setup(identity=lambda: (self.uid, "fixture", self.home),
                                manager=lambda home, uid, **kwargs: {},
                                runtime_loader=mock.Mock(side_effect=installer.SetupError("runtime rejected")))
        self.assertEqual(list(self.home.iterdir()), [])

    def test_activation_failure_keeps_reviewable_installed_receipt(self):
        run = mock.Mock(side_effect=[subprocess.CompletedProcess([], 3, "inactive\n", ""),
                                     subprocess.CompletedProcess([], 1, "", "private diagnostics")])
        with mock.patch.object(installer, "sources", return_value=self.selected):
            with self.assertRaisesRegex(installer.SetupError, "user-manager command failed"):
                installer.setup(start=True, identity=lambda: (self.uid, "fixture", self.home),
                                manager=lambda home, uid, **kwargs: {},
                                runtime_loader=lambda root: self.runtime, run=run)
        receipt_path = installer.layout(self.home)[0] / "install-receipt.json"
        self.assertEqual(json.loads(receipt_path.read_bytes()), self.receipt)
        self.assertEqual(run.call_count, 2)


    def test_runtime_error_message_cannot_escape_cli_output(self):
        output = io.StringIO()
        with mock.patch.object(installer.sys, "argv", ["setup-cutechess-user-session.py"]), \
             mock.patch.object(installer, "setup", side_effect=RuntimeError("private content")), \
             mock.patch.object(installer.sys, "stderr", output):
            self.assertEqual(installer.main(), 1)
        document = json.loads(output.getvalue())
        self.assertEqual(document["error"], "RuntimeError")
        self.assertNotIn("private content", output.getvalue())


    def loopback_setup(self, **options):
        with mock.patch.object(installer, "sources", return_value=self.selected):
            return installer.setup(
                loopback=True, identity=lambda: (self.uid, "fixture", self.home),
                manager=lambda home, uid, **kwargs: {},
                runtime_loader=lambda root: self.runtime, **options)

    def test_loopback_generates_once_and_reinstall_preserves_credential(self):
        password = self.home / ".config/laplace/cutechess-session-password"
        with mock.patch.object(installer.secrets, "token_hex", return_value="d" * 64) as generate:
            first = self.loopback_setup()
            before = self.snapshots()
            second = self.loopback_setup()
        generate.assert_called_once_with(32)
        self.assertTrue(first["changed"])
        self.assertFalse(second["changed"])
        self.assertEqual(first["transport"], "loopback-password")
        self.assertEqual(password.read_bytes(), b"d" * 64)
        self.assertEqual(stat.S_IMODE(password.stat().st_mode), 0o600)
        self.assertEqual(self.snapshots(), before)
        receipt = json.loads((installer.layout(self.home)[0] / "install-receipt.json").read_bytes())
        self.assertEqual(receipt["credential_file"], str(password))
        self.assertNotIn(str(password), receipt["files"])

    def test_loopback_reuses_valid_preexisting_password_without_generation(self):
        password = self.home / ".config/laplace/cutechess-session-password"
        self.write(password, b"e" * 64)
        inode = password.stat().st_ino
        with mock.patch.object(installer.secrets, "token_hex",
                               side_effect=AssertionError("must preserve existing credential")):
            self.loopback_setup()
        self.assertEqual(password.read_bytes(), b"e" * 64)
        self.assertEqual(password.stat().st_ino, inode)

    def test_loopback_preserves_malformed_existing_password(self):
        password = self.home / ".config/laplace/cutechess-session-password"
        for data, mode in ((b"A" * 64, 0o600), (b"a" * 63 + b"\n", 0o600),
                           (b"a" * 64, 0o644), (b"a" * 65, 0o600)):
            with self.subTest(mode=mode, length=len(data)):
                self.write(password, data, mode)
                before = self.snapshots()
                with mock.patch.object(installer.secrets, "token_hex",
                                       side_effect=AssertionError("must not replace malformed credential")):
                    with self.assertRaises(installer.SetupError):
                        self.loopback_setup()
                self.assertEqual(self.snapshots(), before)
                self.assertEqual(password.read_bytes(), data)

    def test_loopback_never_hashes_or_emits_secret_in_receipt_or_result(self):
        secret = b"f" * 64
        original_sha = installer.sha256
        original_git = installer.git_blob

        def reject_secret(function):
            def checked(data):
                self.assertNotEqual(data, secret, "credential must never enter a content hash")
                return function(data)
            return checked

        with mock.patch.object(installer.secrets, "token_hex", return_value=secret.decode()), \
             mock.patch.object(installer, "sha256", side_effect=reject_secret(original_sha)), \
             mock.patch.object(installer, "git_blob", side_effect=reject_secret(original_git)):
            result = self.loopback_setup()
        receipt = (installer.layout(self.home)[0] / "install-receipt.json").read_bytes()
        self.assertNotIn(secret, receipt)
        self.assertNotIn(original_sha(secret).encode(), receipt)
        self.assertNotIn(original_git(secret).encode(), receipt)
        self.assertNotIn(secret.decode(), json.dumps(result))

    def test_loopback_cli_output_contains_no_credential_value(self):
        secret = "b" * 64
        output = io.StringIO()
        identity = lambda: (self.uid, "fixture", self.home)
        manager = lambda home, uid, **kwargs: {}
        original_setup = installer.setup

        def setup(**options):
            return original_setup(identity=identity, manager=manager,
                                  runtime_loader=lambda root: self.runtime, **options)

        with mock.patch.object(installer.sys, "argv",
                               ["setup-cutechess-user-session.py", "--loopback"]), \
             mock.patch.object(installer.sys, "stdout", output), \
             mock.patch.object(installer, "sources", return_value=self.selected), \
             mock.patch.object(installer, "setup", side_effect=setup), \
             mock.patch.object(installer.secrets, "token_hex", return_value=secret):
            self.assertEqual(installer.main(), 0)
        self.assertEqual(json.loads(output.getvalue())["transport"], "loopback-password")
        self.assertNotIn(secret, output.getvalue())

    def test_loopback_failed_install_keeps_preexisting_or_new_credential(self):
        password = self.home / ".config/laplace/cutechess-session-password"
        with mock.patch.object(installer.secrets, "token_hex", return_value="c" * 64) as generate:
            for attempt in range(2):
                with self.subTest(attempt=attempt):
                    with mock.patch.object(installer, "create_owned_file",
                                           side_effect=OSError("injected write failure")):
                        with self.assertRaises(OSError):
                            self.loopback_setup()
                    self.assertEqual(password.read_bytes(), b"c" * 64)
            generate.assert_called_once_with(32)
        self.loopback_setup()
        self.assertEqual(password.read_bytes(), b"c" * 64)

    def test_existing_2770_home_is_preserved_and_new_directories_are_0700(self):
        self.home.chmod(0o2770)
        installer.install_files(self.home, self.uid, self.files)
        self.assertEqual(stat.S_IMODE(self.home.stat().st_mode), 0o2770)
        prefix, configuration, units = installer.layout(self.home)
        for path in (prefix, prefix / "lib", configuration, units):
            self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o700)

    def test_other_primary_group_write_is_rejected_without_chmod(self):
        self.home.chmod(0o2770)
        before = stat.S_IMODE(self.home.stat().st_mode)
        with mock.patch.object(installer.os, "getgid", return_value=os.getgid() + 1):
            with self.assertRaisesRegex(installer.SetupError, "trust boundary"):
                installer.install_files(self.home, self.uid, self.files)
        self.assertEqual(stat.S_IMODE(self.home.stat().st_mode), before)
        self.assertEqual(list(self.home.iterdir()), [])

    def test_loopback_and_explicit_password_are_mutually_exclusive(self):
        with self.assertRaisesRegex(installer.SetupError, "choose"):
            installer.setup(loopback=True, password_file=self.home / "password",
                            identity=mock.Mock(side_effect=AssertionError("no identity work")))


    def test_loopback_rejects_symlink_and_hardlink_credentials(self):
        password = self.home / ".config/laplace/cutechess-session-password"
        password.parent.mkdir(parents=True, mode=0o700)
        target = self.home / "operator-secret"
        self.write(target, b"e" * 64)
        password.symlink_to(target)
        with self.assertRaises(OSError):
            self.loopback_setup()
        self.assertTrue(password.is_symlink())
        self.assertEqual(target.read_bytes(), b"e" * 64)
        password.unlink()
        os.link(target, password)
        with self.assertRaisesRegex(installer.SetupError, "private regular"):
            self.loopback_setup()
        self.assertEqual(password.stat().st_ino, target.stat().st_ino)
        self.assertEqual(target.read_bytes(), b"e" * 64)


    def updated_selection(self):
        selected = {name: dict(item) for name, item in self.selected.items()}
        for name in ("launcher", "xpra"):
            data = (name + " updated fixture\n").encode()
            selected[name].update(data=data, sha256=installer.sha256(data),
                                  git_blob=installer.git_blob(data))
        return selected, {"runtime_id": "b" * 64}

    def bytes_and_modes(self):
        return {path: value[:2] for path, value in self.snapshots().items()}

    def test_clean_managed_update_replaces_exact_known_sources_and_runtime(self):
        installer.install_files(self.home, self.uid, self.files)
        unrelated = installer.layout(self.home)[0] / "operator-notes.txt"
        self.write(unrelated, b"preserve unrelated work\n")
        unrelated_inode = unrelated.stat().st_ino
        selected, runtime = self.updated_selection()
        updated, receipt = installer.make_plan(self.home, self.uid, selected, runtime)
        self.assertTrue(installer.install_files(self.home, self.uid, updated))
        for path, (data, mode) in updated.items():
            self.assertEqual(Path(path).read_bytes(), data)
            self.assertEqual(stat.S_IMODE(Path(path).stat().st_mode), mode)
        self.assertEqual(receipt["runtime"]["runtime_id"], "b" * 64)
        self.assertEqual(unrelated.read_bytes(), b"preserve unrelated work\n")
        self.assertEqual(unrelated.stat().st_ino, unrelated_inode)
        before = self.snapshots()
        self.assertFalse(installer.install_files(self.home, self.uid, updated))
        self.assertEqual(self.snapshots(), before)

    def test_update_refuses_local_source_edits_and_preserves_every_file(self):
        installer.install_files(self.home, self.uid, self.files)
        launcher = installer.layout(self.home)[0] / "laplace-cutechess-user-session"
        launcher.write_bytes(b"operator's local changes\n")
        selected, runtime = self.updated_selection()
        updated, _ = installer.make_plan(self.home, self.uid, selected, runtime)
        before = self.snapshots()
        with self.assertRaisesRegex(installer.SetupError, "differs"):
            installer.install_files(self.home, self.uid, updated)
        self.assertEqual(self.snapshots(), before)

    def test_update_rolls_back_exact_bytes_modes_and_receipt_after_publish_error(self):
        installer.install_files(self.home, self.uid, self.files)
        self.active_update(self.selected, self.runtime)
        selected, runtime = self.updated_selection()
        updated, _ = installer.make_plan(self.home, self.uid, selected, runtime)
        before = self.bytes_and_modes()
        original = installer.atomic_replace
        failed = False

        def fail_after_receipt(path, data, mode, uid):
            nonlocal failed
            original(path, data, mode, uid)
            if path.endswith("/install-receipt.json") and not failed:
                failed = True
                raise OSError("injected error after receipt rename")

        with mock.patch.object(installer, "atomic_replace", side_effect=fail_after_receipt):
            with self.assertRaises(OSError):
                installer.install_files(self.home, self.uid, updated)
        self.assertTrue(failed)
        self.assertEqual(self.bytes_and_modes(), before)
        self.assertFalse(any(path.name.startswith(".laplace-cutechess-update-")
                             for path in self.home.rglob("*")))
        self.assertTrue(installer.install_files(self.home, self.uid, updated))

    def test_update_retains_credential_value_mode_and_inode(self):
        self.loopback_setup()
        password = self.home / ".config/laplace/cutechess-session-password"
        before = (password.read_bytes(), password.stat().st_ino,
                  stat.S_IMODE(password.stat().st_mode))
        selected, runtime = self.updated_selection()
        with mock.patch.object(installer, "sources", return_value=selected), \
             mock.patch.object(installer.secrets, "token_hex",
                               side_effect=AssertionError("updates must retain the credential")):
            result = installer.setup(
                loopback=True, identity=lambda: (self.uid, "fixture", self.home),
                manager=lambda home, uid, **kwargs: {}, runtime_loader=lambda root: runtime)
        self.assertTrue(result["changed"])
        self.assertEqual((password.read_bytes(), password.stat().st_ino,
                          stat.S_IMODE(password.stat().st_mode)), before)

    def test_prior_receipt_rejects_unknown_paths_source_keys_and_ownership(self):
        installer.install_files(self.home, self.uid, self.files)
        receipt_path = installer.layout(self.home)[0] / "install-receipt.json"
        original = receipt_path.read_bytes()
        mutations = (
            lambda receipt: receipt.update(uid=self.uid + 1),
            lambda receipt: receipt.update(home="/another/account"),
            lambda receipt: receipt.update(schema="unknown/v1"),
            lambda receipt: receipt["files"].update({str(self.home / "unrelated"): {
                "sha256": "c" * 64, "mode": "0600"}}),
            lambda receipt: receipt["sources"].update({str(self.home / "credential"): {
                "sha256": "c" * 64, "git_blob": "d" * 40}}),
        )
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                receipt = json.loads(original)
                mutate(receipt)
                receipt_path.write_bytes(installer.canonical(receipt))
                before = self.snapshots()
                with self.assertRaises(installer.SetupError):
                    installer.install_files(self.home, self.uid, self.files)
                self.assertEqual(self.snapshots(), before)
        receipt_path.write_bytes(original)

    def test_desired_plan_cannot_add_unrelated_managed_paths(self):
        extra = self.home / "unrelated"
        self.write(extra, b"operator data\n")
        invalid = dict(self.files)
        invalid[str(extra)] = (b"replacement", 0o600)
        before = self.snapshots()
        with self.assertRaisesRegex(installer.SetupError, "managed paths"):
            installer.install_files(self.home, self.uid, invalid)
        self.assertEqual(self.snapshots(), before)

    def active_update(self, selected, runtime):
        def answer(arguments, **options):
            state = "active\n" if "is-active" in arguments else ""
            return subprocess.CompletedProcess(arguments, 0, state, "")
        run = mock.Mock(side_effect=answer)
        with mock.patch.object(installer, "sources", return_value=selected):
            result = installer.setup(
                start=True, identity=lambda: (self.uid, "fixture", self.home),
                manager=lambda home, uid, **kwargs: {},
                runtime_loader=lambda root: runtime, run=run)
        return result, [call.args[0] for call in run.call_args_list]

    def test_changed_active_installation_restarts_onto_updated_code(self):
        installer.install_files(self.home, self.uid, self.files)
        selected, runtime = self.updated_selection()
        result, calls = self.active_update(selected, runtime)
        self.assertTrue(result["changed"])
        self.assertEqual(sum("restart" in call for call in calls), 1)
        self.assertEqual(calls, [
            ["/usr/bin/systemctl", "--user", "is-active", installer.UNIT_NAME],
            ["/usr/bin/systemctl", "--user", "daemon-reload"],
            ["/usr/bin/systemctl", "--user", "enable", "--now", installer.UNIT_NAME],
            ["/usr/bin/systemctl", "--user", "restart", installer.UNIT_NAME],
            ["/usr/bin/systemctl", "--user", "is-active", installer.UNIT_NAME],
        ])

    def test_exact_active_reinstall_preserves_running_gui_without_restart(self):
        installer.install_files(self.home, self.uid, self.files)
        self.active_update(self.selected, self.runtime)
        before = self.snapshots()
        result, calls = self.active_update(self.selected, self.runtime)
        self.assertFalse(result["changed"])
        self.assertFalse(any("restart" in call for call in calls))
        self.assertEqual(self.snapshots(), before)


    def test_failed_loopback_update_restores_files_and_retains_credential_inode(self):
        self.loopback_setup()
        password = self.home / ".config/laplace/cutechess-session-password"
        credential = (password.read_bytes(), password.stat().st_ino,
                      stat.S_IMODE(password.stat().st_mode))
        before = self.bytes_and_modes()
        selected, runtime = self.updated_selection()
        updated, _ = installer.make_plan(self.home, self.uid, selected, runtime,
                                          password, generate_password=True)
        original = installer.atomic_replace
        attempts = 0

        def fail_second(path, data, mode, uid):
            nonlocal attempts
            attempts += 1
            if attempts == 2:
                raise OSError("injected update failure")
            return original(path, data, mode, uid)

        with mock.patch.object(installer, "atomic_replace", side_effect=fail_second), \
             mock.patch.object(installer.secrets, "token_hex",
                               side_effect=AssertionError("rollback must retain the credential")):
            with self.assertRaises(OSError):
                installer.install_files(self.home, self.uid, updated, generated_password=password)
        self.assertEqual(self.bytes_and_modes(), before)
        self.assertEqual((password.read_bytes(), password.stat().st_ino,
                          stat.S_IMODE(password.stat().st_mode)), credential)


    def test_files_only_update_is_activated_by_a_later_exact_start(self):
        installer.install_files(self.home, self.uid, self.files)
        self.active_update(self.selected, self.runtime)
        self.assertFalse(installer.pending_path(self.home).exists())
        selected, runtime = self.updated_selection()
        updated, _ = installer.make_plan(self.home, self.uid, selected, runtime)
        installer.install_files(self.home, self.uid, updated)
        pending = installer.pending_path(self.home)
        self.assertEqual(stat.S_IMODE(pending.stat().st_mode), 0o600)
        receipt_data = updated[str(installer.layout(self.home)[0] / "install-receipt.json")][0]
        self.assertEqual(pending.read_bytes(),
                         installer.pending_bytes(self.home, self.uid, receipt_data))
        result, calls = self.active_update(selected, runtime)
        self.assertFalse(result["changed"])
        self.assertEqual(sum("restart" in call for call in calls), 1)
        self.assertFalse(pending.exists())

    def test_failed_active_verification_retains_pending_and_retry_restarts(self):
        installer.install_files(self.home, self.uid, self.files)
        self.active_update(self.selected, self.runtime)
        selected, runtime = self.updated_selection()
        run = mock.Mock(side_effect=[
            subprocess.CompletedProcess([], 0, "active\n", ""),
            subprocess.CompletedProcess([], 0, "", ""),
            subprocess.CompletedProcess([], 0, "", ""),
            subprocess.CompletedProcess([], 0, "", ""),
            subprocess.CompletedProcess([], 3, "failed\n", ""),
        ])
        with mock.patch.object(installer, "sources", return_value=selected):
            with self.assertRaisesRegex(installer.SetupError, "user-manager command failed"):
                installer.setup(start=True, identity=lambda: (self.uid, "fixture", self.home),
                                manager=lambda home, uid, **kwargs: {},
                                runtime_loader=lambda root: runtime, run=run)
        pending = installer.pending_path(self.home)
        self.assertTrue(pending.exists())
        pending_before = pending.read_bytes()
        result, calls = self.active_update(selected, runtime)
        self.assertFalse(result["changed"])
        self.assertEqual(sum("restart" in call for call in calls), 1)
        self.assertFalse(pending.exists())
        self.assertIn(b"receipt_sha256", pending_before)

    def test_pending_marker_rejects_unknown_binding_mode_and_hardlink(self):
        installer.install_files(self.home, self.uid, self.files)
        pending = installer.pending_path(self.home)
        original = pending.read_bytes()
        for data, mode in ((b"unrelated operator state\n", 0o600), (original, 0o644)):
            with self.subTest(mode=mode):
                pending.write_bytes(data)
                pending.chmod(mode)
                before = self.snapshots()
                with self.assertRaises(installer.SetupError):
                    installer.install_files(self.home, self.uid, self.files)
                self.assertEqual(self.snapshots(), before)
        pending.write_bytes(original)
        pending.chmod(0o600)
        linked = self.home / "pending-link"
        os.link(pending, linked)
        with self.assertRaisesRegex(installer.SetupError, "hard-linked"):
            installer.install_files(self.home, self.uid, self.files)
        self.assertEqual(linked.read_bytes(), original)


if __name__ == "__main__":
    unittest.main(verbosity=2)
