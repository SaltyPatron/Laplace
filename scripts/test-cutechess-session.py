#!/usr/bin/env python3
"""Unit controls and optional real Xpra parser check for the fixed app owner."""
from __future__ import annotations

import importlib.util
from importlib.machinery import SourceFileLoader
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
OWNER = ROOT / "deploy/linux/laplace-cutechess-session"
spec = importlib.util.spec_from_file_location(
    "cutechess_session", OWNER, loader=SourceFileLoader("cutechess_session", str(OWNER)))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class CuteChessSessionTests(unittest.TestCase):
    def test_preserves_operator_environment_without_inheriting_display_or_xpra_config(self):
        with mock.patch.dict(os.environ, {"HOME": "/wrong", "PGHOST": "/var/run/postgresql",
             "DISPLAY": ":7", "XAUTHORITY": "/unrelated", "XPRA_USER_CONF_DIRS": "/unrelated",
             "XPRA_SOCKET_DIR": "/unrelated"}, clear=True):
            env = module.environment(Path("/home/operator"), Path("/run/owned"))
        self.assertEqual(env["HOME"], "/home/operator")
        self.assertEqual(env["PGHOST"], "/var/run/postgresql")
        self.assertEqual(env["QT_QPA_PLATFORM"], "xcb")
        self.assertNotIn("DISPLAY", env)
        self.assertNotIn("XAUTHORITY", env)
        self.assertNotIn("XPRA_SOCKET_DIR", env)
        self.assertTrue(all(env[name] == "" for name in module.CONF_ENV))

    def test_reuses_only_private_owned_physical_directory(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "session"
            module.private_directory(path, os.getuid(), create=True)
            sentinel = path / "keep"
            sentinel.write_text("existing session data")
            module.private_directory(path, os.getuid(), create=True)
            self.assertEqual(sentinel.read_text(), "existing session data")
            path.chmod(0o755)
            with self.assertRaises(RuntimeError):
                module.private_directory(path, os.getuid(), create=False)
            path.chmod(0o700)
            link = Path(temporary) / "link"
            link.symlink_to(path, target_is_directory=True)
            with self.assertRaises(RuntimeError):
                module.private_directory(link, os.getuid(), create=False)

    @unittest.skipUnless(os.environ.get("LAPLACE_TEST_XPRA_PARSER") == "1",
                         "requires the real selected Xpra package")
    def test_actual_xpra_parser_has_only_unix_transport_and_persistent_app(self):
        code = """
import json, sys
from xpra.scripts.parsing import parse_cmdline
o, args = parse_cmdline(json.loads(sys.argv[1]))
print(json.dumps({
  'args': args, 'start_child': o.start_child, 'bind': o.bind,
  'remote': [o.bind_tcp, o.bind_ssl, o.bind_ws, o.bind_wss, o.bind_ssh,
             o.bind_rfb, o.bind_rdp, o.bind_quic, o.bind_vsock],
  'daemon': o.daemon, 'exit_with_client': o.exit_with_client,
  'exit_with_children': o.exit_with_children,
  'start_new_commands': o.start_new_commands, 'html': o.html,
  'permissions': o.socket_permissions,
  'socket_dir': o.socket_dir, 'socket_dirs': o.socket_dirs
}, sort_keys=True))
"""
        home = Path("/home/qualified-operator")
        result = subprocess.run(["/usr/bin/python3", "-c", code,
                                 json.dumps(module.session_command(home))],
            env=module.environment(home, Path("/run/qualified-session")),
            check=True, capture_output=True, text=True, timeout=30)
        value = json.loads(result.stdout.splitlines()[-1])
        self.assertEqual(value["args"], ["seamless", ":120"])
        self.assertEqual(value["start_child"], [str(module.LAUNCHER)])
        self.assertEqual(value["bind"], ["auto"])
        self.assertTrue(all(not bindings for bindings in value["remote"]))
        self.assertFalse(value["daemon"])
        self.assertFalse(value["exit_with_client"])
        self.assertTrue(value["exit_with_children"])
        self.assertFalse(value["start_new_commands"])
        self.assertEqual(value["html"], "no")
        self.assertEqual(value["permissions"], "600")
        self.assertEqual(value["socket_dir"], str(home / ".xpra"))
        self.assertEqual(value["socket_dirs"], [str(home / ".xpra")])

BOOTSTRAP = ROOT / "deploy/linux/laplace-cutechess-bootstrap"
bootstrap_spec = importlib.util.spec_from_file_location(
    "cutechess_bootstrap", BOOTSTRAP,
    loader=SourceFileLoader("cutechess_bootstrap", str(BOOTSTRAP)))
bootstrap = importlib.util.module_from_spec(bootstrap_spec)
bootstrap_spec.loader.exec_module(bootstrap)


class CuteChessBootstrapTests(unittest.TestCase):
    def setUp(self):
        self.release = json.loads((ROOT / "deploy/cutechess-session-release.json").read_text())

    def record(self, package):
        return "\n".join([
            "Package: " + package["name"], "Version: " + self.release["debian_version"],
            "Architecture: amd64", "Size: " + str(package["size"]),
            "SHA256: " + package["sha256"],
            "Filename: dists/jammy/main/binary-amd64/" + package["name"] + "_"
                + self.release["debian_version"] + "_amd64.deb", "",
        ])

    def test_exact_authenticated_metadata_accepts_only_selected_payload(self):
        for package in self.release["packages"]:
            source = self.record(package)
            row = bootstrap.package_record(source, package, self.release)
            self.assertEqual(row["SHA256"], package["sha256"])
            for original, replacement in [
                (package["sha256"], "0" * 64),
                ("Architecture: amd64", "Architecture: arm64"),
                ("Version: 6.5.3-r0-1", "Version: 6.5.2-r0-1"),
                ("Size: " + str(package["size"]), "Size: 1"),
                ("Filename: dists/", "Filename: untrusted/"),
            ]:
                with self.subTest(package=package["name"], replacement=replacement):
                    with self.assertRaises(RuntimeError):
                        bootstrap.package_record(source.replace(original, replacement),
                                                 package, self.release)

    def test_key_may_have_subkeys_but_cannot_substitute_another_primary(self):
        expected = self.release["signing_fingerprint"]
        text = "pub:::::::::\nfpr:::::::::" + expected + ":\nsub:::::::::\nfpr:::::::::" + "F" * 40 + ":\n"
        bootstrap.key_fingerprint(text, expected)
        with self.assertRaises(RuntimeError):
            bootstrap.key_fingerprint(text.replace(expected, "0" * 40), expected)
        with self.assertRaises(RuntimeError):
            bootstrap.key_fingerprint(text + "pub:::::::::\nfpr:::::::::" + "0" * 40 + ":\n", expected)

    def test_existing_unit_prevents_any_new_mask_and_keeps_original_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            systemd = root / "systemd"
            systemd.mkdir()
            existing = systemd / bootstrap.UNITS[-1]
            existing.write_text("operator-owned service")
            before = existing.stat()
            with mock.patch.object(bootstrap, "SYSTEMD", systemd), \
                 mock.patch.object(bootstrap, "VENDOR_UNIT_DIRS", (root / "vendor",)), \
                 mock.patch.object(bootstrap, "run", return_value=subprocess.CompletedProcess([], 3, "inactive\n", "")):
                with self.assertRaises(RuntimeError):
                    bootstrap.own_proxy_masks(None)
            self.assertEqual(existing.read_text(), "operator-owned service")
            self.assertEqual(existing.stat().st_ino, before.st_ino)
            self.assertEqual(existing.stat().st_mtime_ns, before.st_mtime_ns)
            self.assertEqual(sorted(p.name for p in systemd.iterdir()), [bootstrap.UNITS[-1]])

    @unittest.skipUnless(os.getuid() == 0, "root mask ownership requires an isolated root test process")
    def test_fresh_masks_and_owned_retry_keep_all_public_proxy_units_disabled(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            systemd = root / "systemd"
            systemd.mkdir()
            with mock.patch.object(bootstrap, "SYSTEMD", systemd), \
                 mock.patch.object(bootstrap, "VENDOR_UNIT_DIRS", (root / "vendor",)), \
                 mock.patch.object(bootstrap, "run", return_value=subprocess.CompletedProcess([], 3, "inactive\n", "")):
                bootstrap.own_proxy_masks(None)
                originals = {name: (systemd / name).lstat().st_ino for name in bootstrap.UNITS}
                bootstrap.own_proxy_masks({"status": "packages-selected", "owned_proxy_masks": list(bootstrap.UNITS)})
            for name in bootstrap.UNITS:
                self.assertEqual(os.readlink(systemd / name), "/dev/null")
                self.assertEqual((systemd / name).lstat().st_ino, originals[name])

    @unittest.skipUnless(os.getuid() == 0, "root file-owner policy requires an isolated root test process")
    def test_install_file_reuses_exact_owner_and_refuses_local_edits(self):
        with tempfile.TemporaryDirectory() as temporary:
            target = Path(temporary) / "owner"
            bootstrap.install_file(target, b"qualified", 0o644)
            bootstrap.install_file(target, b"qualified", 0o644)
            target.write_bytes(b"operator changed")
            before = target.stat()
            with self.assertRaises(RuntimeError):
                bootstrap.install_file(target, b"replacement", 0o644)
            self.assertEqual(target.read_bytes(), b"operator changed")
            self.assertEqual(target.stat().st_ino, before.st_ino)
            self.assertEqual(target.stat().st_mtime_ns, before.st_mtime_ns)

    def test_preparing_receipt_does_not_claim_later_independent_installation(self):
        previous = {"schema": bootstrap.SCHEMA, "status": "preparing"}
        self.assertFalse(bootstrap.has_proxy_ownership(previous))
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            systemd = root / "systemd"
            vendor = root / "vendor"
            systemd.mkdir()
            vendor.mkdir()
            independent = vendor / bootstrap.UNITS[-1]
            independent.write_text("independent installation after preparation failure")
            with mock.patch.object(bootstrap, "SYSTEMD", systemd), \
                 mock.patch.object(bootstrap, "VENDOR_UNIT_DIRS", (vendor,)), \
                 mock.patch.object(bootstrap, "run", return_value=subprocess.CompletedProcess([], 3, "inactive\n", "")):
                with self.assertRaises(RuntimeError):
                    bootstrap.own_proxy_masks(previous)
            self.assertEqual(list(systemd.iterdir()), [])
            self.assertEqual(independent.read_text(), "independent installation after preparation failure")

    @unittest.skipUnless(os.getuid() == 0, "root file-owner policy requires an isolated root test process")
    def test_managed_source_update_replaces_only_recorded_prior_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            target = Path(temporary) / "owner"
            old = b"previous reviewed owner"
            new = b"new reviewed owner"
            bootstrap.install_file(target, old, 0o755)
            bootstrap.install_file(target, new, 0o755, bootstrap.sha256(old))
            self.assertEqual(target.read_bytes(), new)
            self.assertEqual(target.stat().st_mode & 0o777, 0o755)
            target.write_bytes(b"operator edit")
            with self.assertRaises(RuntimeError):
                bootstrap.install_file(target, b"third owner", 0o755, bootstrap.sha256(new))
            self.assertEqual(target.read_bytes(), b"operator edit")

    def test_failed_actual_child_retains_diagnostic_output_before_raising(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "failure.log"
            with self.assertRaisesRegex(RuntimeError, "deliberate failure"):
                bootstrap.run(["/usr/bin/python3", "-c",
                    "import sys; print('earlier progress'); print('deliberate failure', file=sys.stderr); sys.exit(7)"],
                    timeout=10, log=output)
            self.assertIn("earlier progress", output.read_text())
            self.assertIn("deliberate failure", output.read_text())

    @unittest.skipUnless(os.getuid() == 0, "root mask ownership requires an isolated root test process")
    def test_reload_failure_retains_actual_mask_ownership_for_retry(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            systemd = root / "systemd"
            systemd.mkdir()
            receipt = {"schema": bootstrap.SCHEMA, "status": "packages-selected"}
            def failed_reload(argv, **kwargs):
                if argv == ["systemctl", "daemon-reload"]:
                    raise RuntimeError("deliberate reload failure")
                return subprocess.CompletedProcess(argv, 3, "inactive\n", "")
            with mock.patch.object(bootstrap, "STATE", root), \
                 mock.patch.object(bootstrap, "SYSTEMD", systemd), \
                 mock.patch.object(bootstrap, "VENDOR_UNIT_DIRS", (root / "vendor",)), \
                 mock.patch.object(bootstrap, "run", side_effect=failed_reload):
                with self.assertRaisesRegex(RuntimeError, "deliberate reload failure"):
                    bootstrap.publish_proxy_masks(None, receipt)
            persisted = json.loads((root / "receipt.json").read_text())
            self.assertTrue(bootstrap.has_proxy_ownership(persisted))
            for name in bootstrap.UNITS:
                self.assertEqual(os.readlink(systemd / name), "/dev/null")
            with mock.patch.object(bootstrap, "STATE", root), \
                 mock.patch.object(bootstrap, "SYSTEMD", systemd), \
                 mock.patch.object(bootstrap, "VENDOR_UNIT_DIRS", (root / "vendor",)), \
                 mock.patch.object(bootstrap, "run", return_value=subprocess.CompletedProcess([], 3, "inactive\n", "")):
                bootstrap.publish_proxy_masks(persisted, receipt)


if __name__ == "__main__":
    unittest.main()
