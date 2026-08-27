#!/usr/bin/env python3
"""Execute the privilege/deployment policy with fake systemctl and isolated files.
No sudo, network requests, database writes, or host service actions occur here.
"""
import fcntl
import importlib.machinery
import importlib.util
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import types
import unittest

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]


def load(name, filename):
    loader = importlib.machinery.SourceFileLoader(name, str(ROOT / "deploy/linux" / filename))
    spec = importlib.util.spec_from_loader(name, loader)
    module = importlib.util.module_from_spec(spec)
    loader.exec_module(module)
    return module


control = load("service_control", "laplace-service-control")


class ControlTests(unittest.TestCase):
    def test_only_eight_fixed_commands_are_reachable(self):
        for service in ("mcp", "lichess"):
            for action in ("status", "start", "stop", "restart"):
                with self.subTest(service=service, action=action), tempfile.TemporaryDirectory() as tmp:
                    calls = []

                    def run(argv, **kwargs):
                        calls.append(argv)
                        self.assertNotIn("shell", kwargs)
                        self.assertLessEqual(kwargs["timeout"], 10)
                        self.assertEqual(kwargs["env"]["PATH"], "/usr/sbin:/usr/bin:/sbin:/bin")
                        return types.SimpleNamespace(stdout="LoadState=loaded\nActiveState=active\nSubState=running\nResult=success\nMainPID=123\nUnitFileState=enabled\n")

                    result = control.execute(service, action, run=run, state=Path(tmp))
                    self.assertEqual("laplace-" + service + ".service", result["unit"])
                    self.assertEqual(123, result["main_pid"])
                    if action != "status":
                        self.assertEqual(["/usr/bin/systemctl", "--no-block", action, result["unit"]], calls[0])
                    self.assertEqual(1 if action == "status" else 2, len(calls))

    def test_arbitrary_units_actions_and_injection_are_rejected_before_execution(self):
        for service, action in [("postgresql", "stop"), ("laplace-api", "restart"), ("mcp;id", "stop"),
            ("../mcp", "start"), ("mcp", "enable"), ("mcp", "stop;id"), ("mcp", "--help")]:
            with self.subTest(service=service, action=action):
                with self.assertRaises(ValueError):
                    control.execute(service, action, run=lambda *a, **k: self.fail("unsafe invocation"))

    def test_operator_stop_persists_and_start_clears_it(self):
        with tempfile.TemporaryDirectory() as tmp:
            state = Path(tmp)
            run = lambda *a, **k: types.SimpleNamespace(stdout="MainPID=0\n")
            control.execute("mcp", "stop", run, state)
            self.assertTrue((state / "mcp.stopped").exists())
            control.execute("mcp", "start", run, state)
            self.assertFalse((state / "mcp.stopped").exists())

    def test_failed_job_and_deploy_conflict_do_not_change_operator_intent(self):
        with tempfile.TemporaryDirectory() as tmp:
            state = Path(tmp)
            (state / "mcp.stopped").touch()

            def fail(*args, **kwargs):
                raise subprocess.CalledProcessError(1, "systemctl")

            with self.assertRaises(subprocess.CalledProcessError):
                control.execute("mcp", "start", fail, state)
            self.assertTrue((state / "mcp.stopped").exists())
            (state / "transaction.json").write_text("{}")
            with self.assertRaises(ValueError):
                control.execute("mcp", "start", lambda *a, **k: self.fail("deployment conflict"), state)

    def test_deployment_lock_rejects_racing_controls_before_state_change(self):
        with tempfile.TemporaryDirectory() as tmp:
            state = Path(tmp)
            with (state / "lifecycle.lock").open("a") as lock:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                with self.assertRaises(BlockingIOError):
                    control.execute("mcp", "stop", lambda *a, **k: self.fail("raced deployment"), state)
                self.assertFalse((state / "mcp.stopped").exists())


class DeploymentTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(self.temp.name)
        self.deploy = load("managed_deploy", "laplace-managed-deploy")
        self.deploy.ROOT = self.base / "opt"
        self.deploy.STATE = self.base / "state"
        self.deploy.SYSTEMD = self.base / "units"
        self.deploy.PROC = self.base / "proc"
        self.deploy.PROC.mkdir()
        self.payload = self.deploy.ROOT / "app/managed-services"
        self.payload.mkdir(parents=True)
        self.deploy.STATE.mkdir()
        self.deploy.SYSTEMD.mkdir()
        for name in self.deploy.UNITS:
            shutil.copyfile(ROOT / "deploy/linux/managed-services" / ("laplace-" + name + ".service"),
                self.payload / ("laplace-" + name + ".service"))
        shutil.copyfile(ROOT / "deploy/linux/laplace-managed-deploy", self.payload / "laplace-managed-deploy")
        self.calls = []

        def run(*argv):
            self.calls.append(argv)
            if "--property=LoadState" in argv:
                return "loaded" if (self.deploy.SYSTEMD / argv[2]).exists() else "not-found"
            if "--property=ActiveState" in argv:
                return "active" if argv[2] == "laplace-mcp.service" else "inactive"
            if "--property=UnitFileState" in argv:
                return "disabled"
            return ""

        self.deploy.run = run

    def tearDown(self):
        self.temp.cleanup()

    def test_units_are_installed_and_enabled_but_reconcile_never_starts_them(self):
        self.deploy.reconcile()
        for name in self.deploy.UNITS:
            self.assertEqual((self.payload / ("laplace-" + name + ".service")).read_text(),
                (self.deploy.SYSTEMD / ("laplace-" + name + ".service")).read_text())
            self.assertIn(("/usr/bin/systemctl", "enable", "laplace-" + name + ".service"), self.calls)
        self.assertFalse(any("start" in call or "restart" in call for call in self.calls))

    def test_deliberate_security_breaks_are_detected_before_any_unit_install(self):
        canonical = (self.payload / "laplace-mcp.service").read_text()
        mutations = [canonical.replace("User=laplace-mcp", "User=root"),
            canonical.replace("NoNewPrivileges=true\n", ""),
            canonical.replace("Type=simple", "Type=simple\nExecStartPre=/bin/sh -c id"),
            canonical.replace("ProtectSystem=strict", "ProtectSystem=false"),
            canonical.replace("CapabilityBoundingSet=", "CapabilityBoundingSet=CAP_SYS_ADMIN"),
            canonical.replace("Group=laplace-mcp", "Group=laplace-runner"),
            canonical.replace("ExecStart=/opt/laplace/app/laplace-mcp --http", "ExecStart=/bin/sh -c id")]
        for mutation in mutations:
            with self.subTest(mutation=mutation[:30]):
                (self.payload / "laplace-mcp.service").write_text(mutation)
                with self.assertRaises(ValueError):
                    self.deploy.reconcile()
                self.assertEqual([], self.calls)
                self.assertEqual([], list(self.deploy.SYSTEMD.iterdir()))

    def test_changed_installer_policy_requires_explicit_privileged_upgrade(self):
        (self.payload / "laplace-managed-deploy").write_text("untrusted policy")
        with self.assertRaises(ValueError):
            self.deploy.reconcile()
        self.assertEqual([], self.calls)

    def test_rollback_restores_prior_pointer_unit_and_running_state(self):
        old = (self.payload / "laplace-mcp.service").read_text().replace("Description=Laplace", "Description=Previous Laplace")
        (self.deploy.SYSTEMD / "laplace-mcp.service").write_text(old)
        link = self.deploy.ROOT / "app/laplace-mcp"
        link.symlink_to("mcp-runtime/Laplace.Endpoints.Mcp")
        self.deploy.begin()
        self.deploy.reconcile()
        link.unlink()
        link.symlink_to("releases/new/mcp/Laplace.Endpoints.Mcp")
        self.deploy.rollback()
        self.assertEqual("mcp-runtime/Laplace.Endpoints.Mcp", str(link.readlink()))
        self.assertEqual(old, (self.deploy.SYSTEMD / "laplace-mcp.service").read_text())
        self.assertFalse((self.deploy.SYSTEMD / "laplace-lichess.service").exists())
        self.assertIn(("/usr/bin/systemctl", "start", "laplace-mcp.service"), self.calls)
        self.assertIn(("/usr/bin/systemctl", "disable", "laplace-mcp.service"), self.calls)
        self.assertFalse((self.deploy.SYSTEMD / "laplace-api.service.d/managed-operator.conf").exists())
        self.assertFalse((self.deploy.STATE / "transaction.json").exists())
        self.assertTrue(list(self.deploy.STATE.glob("rollback-*.json")))

    def test_unresolved_transaction_cannot_be_overwritten(self):
        self.deploy.begin()
        before = (self.deploy.STATE / "transaction.json").read_bytes()
        with self.assertRaises(ValueError):
            self.deploy.begin()
        self.assertEqual(before, (self.deploy.STATE / "transaction.json").read_bytes())

    def test_activation_respects_explicit_stop(self):
        (self.deploy.STATE / "mcp.stopped").touch()
        self.deploy.verify = lambda: None
        self.deploy.activate()
        self.assertNotIn(("/usr/bin/systemctl", "restart", "laplace-mcp.service"), self.calls)
        self.assertIn(("/usr/bin/systemctl", "restart", "laplace-lichess.service"), self.calls)

    def test_active_legacy_bot_prevents_deployment_without_logging_argv_or_stopping_it(self):
        process = self.deploy.PROC / "1234"
        process.mkdir()
        (process / "cmdline").write_bytes(b"/opt/laplace/bin/laplace\0chess\0lichess\0--token\0never-print-this\0")
        with self.assertRaisesRegex(ValueError, "legacy Lichess CLI bot is active") as failure:
            self.deploy.begin()
        self.assertNotIn("never-print-this", str(failure.exception))
        self.assertFalse((self.deploy.STATE / "transaction.json").exists())
        self.assertFalse(any("stop" in call for call in self.calls))

    def test_bootstrap_refuses_public_addresses_and_nginx_injection_before_writes(self):
        for address, hostname in [("8.8.8.8", "hart-server"), ("192.168.1.2", "host; include /tmp/evil;")]:
            with self.assertRaises(ValueError):
                self.deploy.bootstrap(ROOT / "deploy/linux", address, "192.168.1.0/24", hostname)
            self.assertEqual([], self.calls)

    def test_api_restart_safety_net_runs_even_if_recovery_step_fails(self):
        workflow = (ROOT / ".github/workflows/laplace.yml").read_text()
        recovery = workflow.split("  restore-api:\n", 1)[1]
        self.assertIn("- name: Ensure API is running\n        if: always()\n", recovery)


if __name__ == "__main__":
    unittest.main()
