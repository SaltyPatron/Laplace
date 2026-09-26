#!/usr/bin/env python3

import importlib.util
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("configure-laplace-environment.py")
SPEC = importlib.util.spec_from_file_location("configure_laplace_environment", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class ConfigureLaplaceEnvironmentTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.home = self.root / "home"
        self.home.mkdir()

    def tearDown(self):
        self.temp.cleanup()

    def configure(self):
        return MODULE.configure_operator(
            self.home,
            Path("/srv/Laplace-Legacy"),
            Path("/srv/Laplace-Refactor"),
            Path("/opt/laplace"),
            "https://hart-server:8443/",
            "gemini",
        )

    def configure_runner(self, runner):
        return MODULE.configure_runner(
            runner,
            Path("/srv/Laplace-Legacy"),
            Path("/srv/Laplace-Refactor"),
            Path("/opt/laplace"),
            "https://hart-server:8443",
            "gemini",
        )

    def test_operator_loader_sources_one_secret_file_and_exports_contract(self):
        self.configure()
        loader = self.home / ".config/shell/laplace.env"
        text = loader.read_text()
        self.assertIn('. "$HOME/.config/shell/secrets.env"', text)
        self.assertIn("export LAPLACE_AGENT_DEFAULT=gemini", text)
        self.assertIn("export LAPLACE_PUBLIC_BASE_URL=https://hart-server:8443", text)
        self.assertEqual(0o600, stat.S_IMODE(loader.stat().st_mode))

    def test_profiles_are_idempotent_and_preserve_existing_text(self):
        profile = self.home / ".zshrc"
        profile.write_text("export KEEP_ME=yes\n")
        self.configure()
        first = profile.read_text()
        self.configure()
        self.assertEqual(first, profile.read_text())
        self.assertIn("export KEEP_ME=yes", first)
        self.assertEqual(1, first.count(MODULE.PROFILE_BEGIN))

    def test_runner_block_is_idempotent_and_does_not_copy_secrets(self):
        runner = self.root / "runner.env"
        runner.write_text("STRIPE_API_SECRET=preserved\n")
        self.assertTrue(
            self.configure_runner(runner)
        )
        first = runner.read_text()
        self.assertFalse(
            self.configure_runner(runner)
        )
        self.assertEqual(first, runner.read_text())
        self.assertIn("STRIPE_API_SECRET=preserved", first)
        self.assertIn("LAPLACE_AGENTS_CONFIG=/opt/laplace/app/agents.json", first)
        self.assertIn("LAPLACE_ROOT=/srv/Laplace-Legacy", first)
        self.assertIn("LAPLACE_REFACTOR_ROOT=/srv/Laplace-Refactor", first)

    def test_unclosed_managed_block_is_rejected(self):
        with self.assertRaises(ValueError):
            MODULE.replace_block(MODULE.PROFILE_BEGIN, MODULE.PROFILE_BEGIN, MODULE.PROFILE_END, "x")

    def test_runner_only_cli_does_not_require_operator_paths(self):
        runner = self.root / "runner.env"
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--skip-operator", "--runner-env", str(runner),
             "--laplace-root", "/srv/Laplace-Legacy", "--refactor-root", "/srv/Laplace-Refactor"],
            text=True,
            capture_output=True,
            check=False,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("LAPLACE_AGENT_DEFAULT=gemini", runner.read_text())
        self.assertIn("LAPLACE_ROOT=/srv/Laplace-Legacy", runner.read_text())

    def test_runner_env_requires_the_roots_it_exports(self):
        runner = self.root / "runner.env"
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--skip-operator", "--runner-env", str(runner)],
            text=True,
            capture_output=True,
            check=False,
        )
        self.assertEqual(2, result.returncode)
        self.assertIn("--runner-env requires --laplace-root and --refactor-root", result.stderr)
        self.assertFalse(runner.exists())

    def test_runner_can_update_existing_file_without_directory_write(self):
        runner_dir = self.root / "locked-runner"
        runner_dir.mkdir()
        runner = runner_dir / ".env"
        runner.write_text("KEEP=yes\n")
        runner.chmod(0o600)
        runner_dir.chmod(0o500)
        try:
            self.assertTrue(
                self.configure_runner(runner)
            )
        finally:
            runner_dir.chmod(0o700)
        self.assertIn("KEEP=yes", runner.read_text())


if __name__ == "__main__":
    unittest.main()
