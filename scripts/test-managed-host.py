#!/usr/bin/env python3
"""Execute persistent host provisioning with isolated files and fake OS actions.

OpenSSL operates on disposable test keys/certificates. No sudo, live systemctl,
database connection, user/group mutation or production filesystem write occurs.
"""
import contextlib
import importlib.machinery
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]


def load():
    loader = importlib.machinery.SourceFileLoader("managed_host", str(ROOT / "deploy/linux/laplace-managed-deploy"))
    spec = importlib.util.spec_from_loader(loader.name, loader)
    module = importlib.util.module_from_spec(spec)
    loader.exec_module(module)
    return module


class HostTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="laplace-host-test-")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.host = load()
        for name, suffix in {"ROOT": "opt", "STATE": "state", "SYSTEMD": "units", "LIBEXEC": "libexec",
            "SUDOERS": "sudoers/policy", "NGINX_CONFIG": "nginx-available/managed",
            "NGINX_ENABLED": "nginx-enabled/managed"}.items():
            setattr(self.host, name, self.base / suffix)
        self.host.TRUSTED_UID = os.getuid()
        for path in (self.host.STATE, self.host.SYSTEMD, self.host.LIBEXEC,
            self.host.NGINX_CONFIG.parent, self.host.NGINX_ENABLED.parent):
            path.mkdir(parents=True)
        self.ident = self.host.ROOT / "pgsql-18/conf/pg_ident.conf"
        self.ident.parent.mkdir(parents=True)
        self.ident.write_text("laplace_map ahart laplace_admin\nother_map existing existing_role\n")
        self.ident.chmod(0o600)
        self.hba = self.ident.with_name("pg_hba.conf")
        self.hba.write_text("test HBA must remain byte-identical\n")
        self.calls, self.accounts, self.enabled, self.active = [], {}, set(), set()
        self.nginx_active = True
        self.fail_pg = self.fail_nginx = self.fail_timer_start = False
        self.host.run = self.run_command
        self.pwd_patch = patch.object(self.host.pwd, "getpwnam", side_effect=self.account)
        self.grp_patch = patch.object(self.host.grp, "getgrnam", side_effect=lambda name:
            types.SimpleNamespace(gr_gid=self.account(name).pw_gid))
        self.pwd_patch.start()
        self.grp_patch.start()
        self.addCleanup(self.pwd_patch.stop)
        self.addCleanup(self.grp_patch.stop)

    def account(self, name):
        if name not in self.accounts:
            raise KeyError(name)
        return self.accounts[name]

    def run_command(self, *argv):
        self.calls.append(argv)
        if argv[0] == "/usr/bin/openssl":
            return subprocess.run(argv, check=True, capture_output=True, text=True,
                stdin=subprocess.DEVNULL, timeout=30).stdout.strip()
        if argv[0] == "/usr/sbin/useradd":
            name = argv[-1]
            self.accounts[name] = types.SimpleNamespace(pw_uid=501 + len(self.accounts),
                pw_gid=501 + len(self.accounts), pw_dir="/var/lib/" + name, pw_shell="/usr/sbin/nologin")
        elif argv[0] == "/usr/bin/install":
            Path(argv[-1]).mkdir(parents=True, exist_ok=True)
        elif argv[0] == "/usr/sbin/runuser":
            self.assertIn("/var/run/postgresql", argv)
            self.assertEqual("SELECT pg_reload_conf()", argv[-1])
            if self.fail_pg:
                raise subprocess.CalledProcessError(1, "test peer reload")
            return "t"
        elif argv[0] == "/usr/sbin/nginx":
            self.assertEqual(("-t",), argv[1:])
            if self.fail_nginx:
                raise subprocess.CalledProcessError(1, "test nginx validation")
        elif argv[0] == "/usr/sbin/visudo":
            # The real parser validates only the explicitly supplied temp file.
            return subprocess.run(argv, check=True, capture_output=True, text=True, timeout=10).stdout.strip()
        elif argv[0] == "/usr/bin/systemctl":
            if argv[1] == "show":
                if "--property=UnitFileState" in argv:
                    return "enabled" if argv[2] in self.enabled else "disabled"
                if "--property=ActiveState" in argv:
                    if argv[2] == "nginx.service":
                        return "active" if self.nginx_active else "inactive"
                    return "active" if argv[2] in self.active else "inactive"
                self.fail("unexpected systemctl property")
            elif argv[1] == "enable":
                self.enabled.add(argv[2])
            elif argv[1] == "disable":
                self.enabled.discard(argv[2])
            elif argv[1] == "start":
                self.assertIn(argv[2], ("laplace-managed-host.timer", "nginx"))
                if argv[2] == "laplace-managed-host.timer" and self.fail_timer_start:
                    raise subprocess.CalledProcessError(1, "test timer start")
                if argv[2] == "nginx":
                    self.nginx_active = True
                self.active.add(argv[2])
            elif argv[1] == "stop":
                self.assertEqual("laplace-managed-host.timer", argv[2])
                self.active.discard(argv[2])
            else:
                self.assertIn(argv[1:], [("daemon-reload",), ("reload", "nginx")])
        else:
            self.fail("unexpected privileged command: " + argv[0])
        return ""

    def bootstrap(self, **settings):
        with contextlib.redirect_stdout(io.StringIO()):
            self.host.bootstrap(ROOT / "deploy/linux", **settings)

    def reconcile(self):
        with contextlib.redirect_stdout(io.StringIO()):
            self.host.reconcile_host()

    def test_setup_repeats_preserve_settings_keys_and_noop_reload_behavior(self):
        self.bootstrap(address="10.20.30.4", network="10.20.30.0/24", hostname="laplace-lan")
        self.assertTrue(self.host.host_status()["healthy"])
        ca = (self.host.STATE / "tls/ca.crt").read_bytes()
        key = (self.host.STATE / "tls/server.key").read_bytes()
        cert = (self.host.STATE / "tls/server.crt").read_bytes()
        ident = self.ident.read_bytes()
        self.calls.clear()
        self.bootstrap()
        self.reconcile()
        self.assertEqual("laplace-lan", self.host.host_settings()["hostname"])
        self.assertEqual("LAPLACE_MCP_ORIGIN=https://laplace-lan:8443\n", (self.host.STATE / "mcp-host.env").read_text())
        self.assertEqual(ca, (self.host.STATE / "tls/ca.crt").read_bytes())
        self.assertEqual(key, (self.host.STATE / "tls/server.key").read_bytes())
        self.assertEqual(cert, (self.host.STATE / "tls/server.crt").read_bytes())
        self.assertEqual(ident, self.ident.read_bytes())
        self.assertFalse(any(call[0] in ("/usr/sbin/useradd", "/usr/sbin/runuser") for call in self.calls))
        self.assertNotIn(("/usr/bin/systemctl", "reload", "nginx"), self.calls)
        self.assertTrue(self.host.host_status()["healthy"])
        self.assertEqual({"laplace-managed-host.service", "laplace-managed-host.timer"}, self.enabled)
        self.assertEqual({"laplace-managed-host.timer"}, self.active)
        result = subprocess.run(["systemd-analyze", "verify", "--man=no",
            *(str(self.host.SYSTEMD / name) for name in self.host.maintenance_units())],
            capture_output=True, text=True, timeout=15)
        self.assertEqual(0, result.returncode, result.stderr)

    def test_reconciliation_repairs_drift_but_never_resets_hba_or_operator_stop(self):
        self.bootstrap()
        (self.host.STATE / "mcp.stopped").touch()
        self.ident.write_text("laplace_map ahart laplace_admin\nother_map existing existing_role\n")
        (self.host.SYSTEMD / "laplace-managed-host.timer").write_text("broken timer")
        self.host.NGINX_CONFIG.write_text("broken managed proxy")
        (self.host.STATE / "mcp-host.env").unlink()
        self.enabled.clear()
        self.active.clear()
        before = self.hba.read_bytes()
        self.assertFalse(self.host.host_status()["healthy"])
        self.calls.clear()
        self.reconcile()
        self.assertTrue(self.host.host_status()["healthy"])
        self.assertEqual(before, self.hba.read_bytes())
        self.assertIn("other_map existing existing_role", self.ident.read_text())
        self.assertTrue((self.host.STATE / "mcp.stopped").exists())
        self.assertEqual(1, sum(call[0] == "/usr/sbin/runuser" for call in self.calls))
        self.assertNotIn("bootstrap", self.host.sudoers_policy())
        self.assertNotIn("ALL=(ALL)", self.host.sudoers_policy())
        self.assertNotIn("*", self.host.sudoers_policy())

    def test_reconciliation_restarts_inactive_nginx_before_reporting_healthy(self):
        self.bootstrap()
        self.nginx_active = False
        self.assertIn("nginx_inactive", self.host.host_status()["issues"])
        self.calls.clear()

        self.reconcile()

        self.assertIn(("/usr/bin/systemctl", "start", "nginx"), self.calls)
        self.assertTrue(self.host.host_status()["healthy"])

    def test_managed_tls_listener_cannot_take_down_primary_web_during_dhcp(self):
        config = self.host.nginx_config("192.168.1.2", "192.168.1.0/24", "hart-server")

        self.assertIn("listen 8443 ssl;", config)
        self.assertNotIn("listen 192.168.1.2:8443", config)
        self.assertIn("allow 192.168.1.0/24;", config)

    def test_reconciliation_restores_public_ca_from_private_authority_before_success(self):
        self.bootstrap()
        authority = self.host.STATE / "tls/ca.crt"
        public = self.host.ROOT / "share/laplace/managed-services-ca.crt"
        expected = authority.read_bytes()
        public.write_text("stale public certificate\n")
        self.assertIn("public_ca_copy_drift", self.host.host_status()["issues"])

        self.reconcile()

        self.assertEqual(expected, public.read_bytes())
        self.assertTrue(self.host.host_status()["healthy"])

    def test_status_is_readonly_and_reports_missing_maintenance_and_permissions(self):
        self.bootstrap()
        (self.host.STATE / "tls/ca.key").chmod(0o644)
        self.active.clear()
        self.calls.clear()
        report = self.host.host_status()
        self.assertFalse(report["healthy"])
        self.assertIn("tls_file_permissions:ca.key", report["issues"])
        self.assertIn("maintenance_timer_inactive", report["issues"])
        self.assertTrue(all(call[0] == "/usr/bin/openssl" or call[1] == "show" for call in self.calls))
        self.assertEqual(0o644, (self.host.STATE / "tls/ca.key").stat().st_mode & 0o777)

    def test_identity_reload_failure_restores_file_and_preserves_permissions(self):
        self.fail_pg = True
        before = self.ident.read_bytes()
        with self.assertRaises(subprocess.CalledProcessError):
            self.host.reconcile_peer_mappings()
        self.assertEqual(before, self.ident.read_bytes())
        self.assertEqual(0o600, self.ident.stat().st_mode & 0o777)
        self.assertTrue(list(self.host.STATE.glob("pg-ident-before-*.conf")))
        self.host.run = lambda *argv: "f"
        with self.assertRaisesRegex(ValueError, "acknowledge"):
            self.host.reconcile_peer_mappings()
        self.assertEqual(before, self.ident.read_bytes())

    def test_account_collision_and_deployment_transaction_fail_before_reconfiguration(self):
        self.accounts["laplace-mcp"] = types.SimpleNamespace(pw_uid=0, pw_gid=0,
            pw_dir="/root", pw_shell="/bin/bash")
        with self.assertRaises(ValueError):
            self.host.reconcile_accounts()
        self.assertEqual([], self.calls)
        (self.host.STATE / "transaction.json").write_text("{}")
        with self.assertRaises(BlockingIOError):
            self.reconcile()
        self.assertEqual([], self.calls)

    def test_unsafe_network_or_untrusted_settings_are_rejected_without_commands(self):
        for address, network in [("192.168.1.2", "0.0.0.0/0"), ("8.8.8.8", "8.8.8.0/24"),
            ("127.0.0.1", "127.0.0.0/8"), ("169.254.1.2", "169.254.0.0/16")]:
            with self.subTest(address=address), self.assertRaises(ValueError):
                self.bootstrap(address=address, network=network)
        self.assertEqual([], self.calls)
        settings = self.host.STATE / "lan.json"
        settings.write_text(json.dumps({"address":"192.168.1.2", "network":"192.168.1.0/24", "hostname":"hart-server"}))
        settings.chmod(0o666)
        self.assertFalse(self.host.host_status()["healthy"])
        with self.assertRaises(ValueError):
            self.reconcile()
        self.assertEqual([], self.calls)

    def test_certificate_renewal_keeps_ca_and_key_and_updates_address_binding(self):
        self.bootstrap()
        ca = (self.host.STATE / "tls/ca.crt").read_bytes()
        key = (self.host.STATE / "tls/server.key").read_bytes()
        cert = (self.host.STATE / "tls/server.crt").read_bytes()
        self.bootstrap(address="192.168.1.3", hostname="hart-managed")
        self.assertTrue(self.host.certificate_current("192.168.1.3", "hart-managed"))
        self.assertFalse(self.host.certificate_current("192.168.1.2", "hart-server"))
        self.assertEqual(ca, (self.host.STATE / "tls/ca.crt").read_bytes())
        self.assertEqual(key, (self.host.STATE / "tls/server.key").read_bytes())
        self.assertNotEqual(cert, (self.host.STATE / "tls/server.crt").read_bytes())

    def test_missing_ca_never_silently_rotates_desktop_trust(self):
        self.bootstrap()
        key = (self.host.STATE / "tls/ca.key").read_bytes()
        (self.host.STATE / "tls/ca.crt").unlink()
        self.calls.clear()
        with self.assertRaisesRegex(ValueError, "restore"):
            self.reconcile()
        self.assertEqual(key, (self.host.STATE / "tls/ca.key").read_bytes())
        self.assertFalse(any("-newkey" in call for call in self.calls))

    def test_scheduled_reconcile_renews_an_expiring_leaf_without_changing_ca_or_key(self):
        self.bootstrap()
        tls = self.host.STATE / "tls"
        ca, key = (tls / "ca.crt").read_bytes(), (tls / "server.key").read_bytes()
        csr, extensions = self.base / "test.csr", self.base / "test.extensions"
        extensions.write_text("basicConstraints=critical,CA:FALSE\nsubjectAltName=DNS:hart-server,IP:192.168.1.2\n")
        self.run_command("/usr/bin/openssl", "req", "-new", "-key", str(tls / "server.key"),
            "-out", str(csr), "-subj", "/CN=hart-server")
        self.run_command("/usr/bin/openssl", "x509", "-req", "-in", str(csr), "-CA", str(tls / "ca.crt"),
            "-CAkey", str(tls / "ca.key"), "-CAcreateserial", "-out", str(tls / "server.crt"),
            "-days", "1", "-extfile", str(extensions))
        self.assertIn("tls_certificate_invalid_or_renewal_due", self.host.host_status()["issues"])
        self.reconcile()
        self.assertTrue(self.host.host_status()["healthy"])
        self.assertEqual(ca, (tls / "ca.crt").read_bytes())
        self.assertEqual(key, (tls / "server.key").read_bytes())

    def test_late_setup_failure_restores_policy_config_and_existing_timer_state(self):
        self.bootstrap()
        tracked = [self.host.STATE / "lan.json", self.host.STATE / "mcp-host.env", self.host.SUDOERS, self.host.NGINX_CONFIG,
            self.host.STATE / "tls/server.crt", *(self.host.SYSTEMD / name for name in self.host.maintenance_units())]
        before = {path: path.read_bytes() for path in tracked}
        self.active.clear()
        self.fail_timer_start = True
        with self.assertRaises(subprocess.CalledProcessError):
            self.bootstrap(hostname="hart-renamed")
        self.assertEqual(before, {path: path.read_bytes() for path in tracked})
        self.assertFalse(self.active)
        self.assertEqual(set(self.host.maintenance_units()), self.enabled)

    def test_ci_cannot_change_saved_settings_through_reconcile_flags(self):
        with patch.object(self.host.sys, "argv", ["helper", "reconcile-host", "--address", "192.168.1.9"]), \
            patch.object(self.host.os, "geteuid", return_value=0):
            with self.assertRaises(ValueError):
                self.host.main()
        self.assertEqual([], self.calls)

    def test_nginx_failure_restores_previous_proxy_certificate_and_desired_settings(self):
        self.bootstrap()
        settings = (self.host.STATE / "lan.json").read_bytes()
        proxy = self.host.NGINX_CONFIG.read_bytes()
        cert = (self.host.STATE / "tls/server.crt").read_bytes()
        self.fail_nginx = True
        with self.assertRaises(subprocess.CalledProcessError):
            self.bootstrap(hostname="hart-renamed")
        self.assertEqual(settings, (self.host.STATE / "lan.json").read_bytes())
        self.assertEqual(proxy, self.host.NGINX_CONFIG.read_bytes())
        self.assertEqual(cert, (self.host.STATE / "tls/server.crt").read_bytes())


class EntryPointTests(unittest.TestCase):
    def test_targeted_setup_dispatch_never_runs_full_host_or_database_setup(self):
        with tempfile.TemporaryDirectory(prefix="laplace-setup-test-") as directory:
            base = Path(directory)
            scripts, binaries = base / "scripts", base / "bin"
            scripts.mkdir()
            binaries.mkdir()
            shutil.copyfile(ROOT / "scripts/setup-host.sh", scripts / "setup-host.sh")
            for name, content in {
                "id": "#!/bin/bash\nprintf '0\\n'\n",
                "python3": "#!/bin/bash\nprintf '%s\\n' \"$@\" >> \"$TEST_CALLS\"\n",
                "sudo": "#!/bin/bash\nexit 99\n",
            }.items():
                path = binaries / name
                path.write_text(content)
                path.chmod(0o755)
            calls = base / "calls"
            environment = {"PATH": str(binaries) + ":/usr/bin:/bin", "SUDO_USER": "ahart", "TEST_CALLS": str(calls)}
            for _ in range(2):
                result = subprocess.run(["bash", str(scripts / "setup-host.sh"), "managed-services", "--hostname", "hart-server"],
                    env=environment, capture_output=True, text=True, timeout=10)
                self.assertEqual(0, result.returncode, result.stderr)
            expected = [str(base / "deploy/linux/laplace-managed-deploy"), "bootstrap", "--hostname", "hart-server"]
            self.assertEqual(expected * 2, calls.read_text().splitlines())
        setup = (ROOT / "scripts/setup-host.sh").read_text()
        self.assertIn("    layer1_up\n    managed_services_setup\n", setup)

    def test_ci_repairs_host_before_live_install_and_never_installs_root_code(self):
        workflow = (ROOT / ".github/workflows/laplace.yml").read_text()
        policy = (ROOT / "scripts/ci-policy.sh").read_text()
        registry = json.loads((ROOT / "scripts/test-profiles.json").read_text())
        self.assertIn("run: bash scripts/ci-policy.sh", workflow)
        self.assertIn("test-profile-registry.py run --profile policy", policy)
        self.assertNotIn("python3 scripts/test-managed-host.py", policy)
        managed_host = [suite for suite in registry["suites"] if suite["id"] == "policy-managed-host"]
        self.assertEqual(1, len(managed_host))
        self.assertEqual(["python3", "scripts/test-managed-host.py"], managed_host[0]["command"])
        publish = (ROOT / "deploy/linux/managed-publish.sh").read_text()
        self.assertIn('sudo -n "$HELPER" reconcile-host', publish)
        self.assertIn('sudo -n "$HELPER" host-status', publish)
        self.assertIn("  preflight) ensure_host", publish)
        application_publish = (ROOT / "scripts/publish-applications.sh").read_text()
        host_check = application_publish.split("application_host_check() {", 1)[1].split("application_managed()", 1)[0]
        self.assertIn("application_managed preflight", host_check)
        self.assertNotIn("laplace-managed-deploy host-status", host_check)
        self.assertNotIn('sudo python3 deploy/linux/laplace-managed-deploy bootstrap', publish)
        deploy = workflow.split("  deploy:\n", 1)[1].split("  db-ops:\n", 1)[0]
        reconciliations = [match.start() for match in re.finditer("managed-publish.sh preflight", deploy)]
        install = deploy.index("pipeline.sh install")
        self.assertEqual(2, len(reconciliations))
        self.assertLess(deploy.index("wait-for-quiet-substrate.sh"), reconciliations[0])
        self.assertLess(reconciliations[0], install)
        self.assertLess(install, reconciliations[1])

    def test_native_install_cleanup_preserves_root_managed_public_ca(self):
        cmake = (ROOT / "CMakeLists.txt").read_text()
        cleanup = cmake.split('message(STATUS \\"Laplace pre-install cleanup', 1)[1].split('add_subdirectory(engine)', 1)[0]
        self.assertIn("-maxdepth 1 -type f -name 'laplace_*.bin' -delete", cleanup)
        self.assertNotIn("managed-services-ca.crt", cleanup)
        self.assertNotIn("find \\\"\\$share\\\" -mindepth 1", cleanup)


if __name__ == "__main__":
    unittest.main()
