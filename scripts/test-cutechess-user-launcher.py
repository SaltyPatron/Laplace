#!/usr/bin/env python3
"""Meaningful boundary controls for the owned user-session entry point."""
import importlib.util
from importlib.machinery import SourceFileLoader
import json
import os
from pathlib import Path
import shlex
import subprocess
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
OWNER = ROOT / "deploy/linux/laplace-cutechess-user-session"
spec = importlib.util.spec_from_file_location(
    "cutechess_user_session", OWNER,
    loader=SourceFileLoader("cutechess_user_session", str(OWNER)))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class UserSessionTests(unittest.TestCase):
    def setUp(self):
        # Private test parents keep the real permission/ownership checks active.
        self.temporary = tempfile.TemporaryDirectory(prefix=".cutechess-user-test-", dir=Path.home())
        self.addCleanup(self.temporary.cleanup)
        self.home = Path(self.temporary.name)
        (self.home / ".config/laplace").mkdir(parents=True, mode=0o700)
        self.config_path = self.home / ".config/laplace/cutechess-session.json"
        self.config = {"schema": module.SCHEMA, "runtime_root": str(module.RUNTIME_ROOT),
                       "transport": "unix"}
        self.selected = {"tools": {"python": "/usr/bin/python3",
            "xpra": "/owned/generation/usr/bin/xpra",
            "Xvfb": "/owned/x11/usr/bin/Xvfb"}}

    def write_config(self, value=None):
        self.config_path.write_text(json.dumps(self.config if value is None else value))
        self.config_path.chmod(0o600)

    def password(self, data=b"a"*64):
        path = self.home / ".config/laplace/session-password"
        path.write_bytes(data)
        path.chmod(0o600)
        return path

    def test_default_config_and_command_keep_one_persistent_application(self):
        self.write_config()
        config = module.configuration(self.home, os.getuid())
        command = module.session_command(self.home, self.selected, config)
        self.assertEqual(command[:4], ["/usr/bin/python3", "/owned/generation/usr/bin/xpra",
                                       "seamless", ":120"])
        self.assertIn("--start-child=/opt/laplace/bin/laplace-cutechess", command)
        self.assertIn("--exit-with-client=no", command)
        self.assertIn("--exit-with-children=yes", command)
        self.assertIn("--terminate-children=yes", command)
        self.assertIn("--start-new-commands=no", command)
        self.assertIn("--daemon=no", command)
        self.assertFalse(any(item.startswith("--bind-tcp") for item in command))

    def test_selected_xvfb_has_cookie_auth_and_no_tcp_listener(self):
        command = module.session_command(self.home, self.selected, self.config)
        text = next(item.split("=", 1)[1] for item in command if item.startswith("--xvfb="))
        arguments = shlex.split(text)
        self.assertEqual(arguments[0], self.selected["tools"]["Xvfb"])
        self.assertEqual(arguments[arguments.index("-auth")+1], "$XAUTHORITY")
        self.assertEqual(arguments[arguments.index("-nolisten")+1], "tcp")
        self.assertNotIn("-ac", arguments)
        self.assertEqual(arguments[arguments.index("-screen")+2], "1920x1080x24")

    def test_loopback_requires_file_auth_and_never_places_password_in_arguments(self):
        path = self.password()
        self.config.update(transport="loopback-password", password_file=str(path))
        self.write_config()
        checked = module.configuration(self.home, os.getuid())
        command = module.session_command(self.home, self.selected, checked)
        binds = [item for item in command if item.startswith("--bind-tcp")]
        self.assertEqual(binds, ["--bind-tcp=127.0.0.1:14501,auth=file(filename="+str(path)+")"])
        self.assertNotIn((b"a"*64).decode(), " ".join(command))
        self.assertIn("--html=no", command)
        self.assertIn("--mdns=no", command)

    def test_credential_newline_short_binary_and_empty_inputs_are_rejected(self):
        for data in (b"a"*64+b"\n", b"a"*63, b"", b"\xff"*64, b"A"*64):
            with self.subTest(size=len(data)):
                path = self.password(data)
                self.config.update(transport="loopback-password", password_file=str(path))
                self.write_config()
                with self.assertRaises(RuntimeError):
                    module.configuration(self.home, os.getuid())

    def test_credential_cannot_inject_another_binding_or_auth_module(self):
        for suffix in (",auth=none", ")", " secret", "/../secret", "\n"):
            self.config.update(transport="loopback-password",
                               password_file=str(self.home / "password")+suffix)
            self.write_config()
            with self.subTest(suffix=suffix), self.assertRaises(RuntimeError):
                module.configuration(self.home, os.getuid())

    def test_config_rejects_arbitrary_runtime_transport_and_extra_fields(self):
        cases = [
            {**self.config, "runtime_root": "/unrelated"},
            {**self.config, "transport": "tcp"},
            {**self.config, "transport": "127.0.0.1:9999"},
            {**self.config, "password_file": str(self.password())},
            {**self.config, "bind": "0.0.0.0"},
            {**self.config, "schema": "other"},
        ]
        for value in cases:
            self.write_config(value)
            with self.subTest(value=value), self.assertRaises(RuntimeError):
                module.configuration(self.home, os.getuid())

    def test_private_file_rejects_symlink_and_peer_read_permissions(self):
        path = self.password()
        alias = path.with_name("alias")
        alias.symlink_to(path)
        with self.assertRaises(OSError):
            module.read_private(alias, os.getuid(), 64)
        path.chmod(0o640)
        with self.assertRaises(RuntimeError):
            module.read_private(path, os.getuid(), 64)
        path.chmod(0o600)
        with self.assertRaises(RuntimeError):
            module.read_private(path, os.getuid()+1, 64)

    def test_existing_primary_group_home_keeps_its_mode_but_other_writers_are_rejected(self):
        before = self.home.stat().st_mode
        self.home.chmod(0o2770)
        module.physical_parents(self.home, os.getuid())
        self.assertEqual(self.home.stat().st_mode & 0o7777, 0o2770)
        with mock.patch.object(module.os, "getgid", return_value=os.getgid()+1):
            with self.assertRaises(RuntimeError):
                module.physical_parents(self.home, os.getuid())
        self.home.chmod(0o2772)
        with self.assertRaises(RuntimeError):
            module.physical_parents(self.home, os.getuid())
        self.home.chmod(before & 0o7777)

    def test_private_directory_preserves_existing_state_and_refuses_alias(self):
        path = self.home / "session"
        module.private_directory(path, os.getuid(), create=True)
        sentinel = path / "keep"
        sentinel.write_text("existing state")
        module.private_directory(path, os.getuid(), create=True)
        self.assertEqual(sentinel.read_text(), "existing state")
        alias = self.home / "alias"
        alias.symlink_to(path, target_is_directory=True)
        with self.assertRaises(RuntimeError):
            module.private_directory(alias, os.getuid())
        path.chmod(0o755)
        with self.assertRaises(RuntimeError):
            module.private_directory(path, os.getuid())

    def test_environment_removes_inherited_xpra_configuration_and_display(self):
        base = {"XPRA_DEBUG": "auth", "XPRA_PASSWORD": "do-not-retain",
                "XPRA_XVFB_EXTRA_ARGS": "-ac", "XPRA_BIND_TCP": "0.0.0.0:9999",
                "DISPLAY": ":4", "XAUTHORITY": "/other", "WAYLAND_DISPLAY": "wayland-0",
                "PGHOST": "/var/run/postgresql", "HOME": "/wrong"}
        env = module.environment(self.home, Path("/run/user/994"),
                                 Path("/run/user/994/laplace-cutechess"), base)
        self.assertEqual(env["HOME"], str(self.home))
        self.assertEqual(env["PGHOST"], "/var/run/postgresql")
        self.assertEqual(env["XDG_RUNTIME_DIR"], "/run/user/994")
        self.assertEqual(env["XPRA_SESSION_DIR"], "/run/user/994/laplace-cutechess")
        self.assertEqual(env["DBUS_SESSION_BUS_ADDRESS"], "unix:path=/run/user/994/bus")
        self.assertEqual(env["XPRA_PRIVATE_XAUTH"], "1")
        self.assertEqual(env["QT_QPA_PLATFORM"], "xcb")
        self.assertTrue(all(env[name] == "" for name in module.CONF_ENV))
        for name in ("DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "XPRA_PASSWORD",
                     "XPRA_BIND_TCP", "XPRA_XVFB_EXTRA_ARGS", "XPRA_DEBUG"):
            self.assertNotIn(name, env)

    def test_root_or_changed_effective_identity_refused_before_files_or_execution(self):
        for uid, euid in ((0, 0), (1000, 0), (1000, 1001)):
            with self.subTest(uid=uid, euid=euid), \
                 mock.patch.object(module.os, "getuid", return_value=uid), \
                 mock.patch.object(module.os, "geteuid", return_value=euid), \
                 mock.patch.object(module.pwd, "getpwuid") as account, \
                 mock.patch.object(module.os, "execve") as execute:
                with self.assertRaises(RuntimeError):
                    module.main()
                account.assert_not_called()
                execute.assert_not_called()

    def test_malformed_private_input_never_reaches_exec(self):
        self.config_path.write_bytes(b"not-json-secret-value")
        self.config_path.chmod(0o600)
        with mock.patch.object(module.os, "getuid", return_value=1000), \
             mock.patch.object(module.os, "geteuid", return_value=1000), \
             mock.patch.object(module.pwd, "getpwuid", return_value=mock.Mock(pw_dir=str(self.home))), \
             mock.patch.object(module, "physical_parents"), \
             mock.patch.object(module, "private_directory"), \
             mock.patch.object(module, "read_private", return_value=b"not-json-secret-value"), \
             mock.patch.object(module, "load_owner") as owner, \
             mock.patch.object(module.os, "execve") as execute:
            with self.assertRaises(ValueError):
                module.main()
            owner.assert_not_called()
            execute.assert_not_called()

    def test_user_unit_uses_existing_user_manager_and_owns_its_process_group(self):
        source = (ROOT / "deploy/linux/laplace-cutechess-user.service").read_text()
        self.assertIn("WantedBy=default.target", source)
        self.assertIn("RuntimeDirectory=laplace-cutechess", source)
        self.assertIn("RuntimeDirectoryMode=0700", source)
        self.assertIn("KillMode=control-group", source)
        self.assertIn("NoNewPrivileges=yes", source)
        self.assertIn("ExecStart=/usr/bin/python3 %h/.local/lib/laplace-cutechess/laplace-cutechess-user-session", source)
        for forbidden in ("User=", "PrivateTmp=", "multi-user.target", "laplace-postgresql.service"):
            self.assertNotIn(forbidden, source)


    def test_disabled_features_keep_flags_and_common_imports_enabled_after_sanitation(self):
        password = self.password()
        self.config.update(transport="loopback-password", password_file=str(password))
        self.write_config()
        application = self.home / "cutechess"
        application.write_text("fixture executable\n")
        application.chmod(0o700)
        owner = mock.Mock()
        owner.load.return_value = self.selected
        def sanitized(selected, base):
            self.assertEqual(selected, self.selected)
            # The production runtime owner removes arbitrary XPRA_ variables.
            result = {key: value for key, value in base.items()
                      if not key.startswith("XPRA_") or key in ("XPRA_SESSION_DIR", "XPRA_PRIVATE_XAUTH")}
            self.assertNotIn("XPRA_ENFORCE_FEATURES", result)
            return result
        owner.selected_environment.side_effect = sanitized
        with mock.patch.object(module.pwd, "getpwuid", return_value=mock.Mock(pw_dir=str(self.home))), \
             mock.patch.object(module, "private_directory"), \
             mock.patch.object(module, "load_owner", return_value=owner), \
             mock.patch.object(module, "LAUNCHER", application), \
             mock.patch.dict(module.os.environ, {"XPRA_ENFORCE_FEATURES": "1", "XPRA_PASSWORD": "discard"}, clear=True), \
             mock.patch.object(module.os, "chdir"), \
             mock.patch.object(module.os, "umask"), \
             mock.patch.object(module.os, "execve") as execute:
            module.main()
        execute.assert_called_once()
        _, command, environment = execute.call_args.args
        self.assertEqual(environment["XPRA_ENFORCE_FEATURES"], "0")
        self.assertNotIn("XPRA_PASSWORD", environment)
        self.assertIn("--mmap=no", command)
        self.assertIn("--file-transfer=no", command)
        self.assertIn("--webcam=no", command)
        self.assertIn("--speaker=no", command)
        self.assertIn("--microphone=no", command)
        self.assertIn("--printing=no", command)
        self.assertIn("--start-new-commands=no", command)
        self.assertEqual([value for value in command if value.startswith("--bind-tcp=")],
                         ["--bind-tcp=127.0.0.1:14501,auth=file(filename=" + str(password) + ")"])

if __name__ == "__main__":
    unittest.main()
