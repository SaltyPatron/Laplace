#!/usr/bin/env python3
from __future__ import annotations

import json
import importlib.util
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "web-artifact.py"
SPEC = importlib.util.spec_from_file_location("web_artifact", SCRIPT)
ARTIFACT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ARTIFACT)


class WebArtifactTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="web-artifact-")
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name) / "Laplace"
        (self.repo / "web/dist/assets").mkdir(parents=True)
        (self.repo / "web/openapi").mkdir()
        (self.repo / "web/package-lock.json").write_text('{"lockfileVersion":3}\n', encoding="utf-8")
        (self.repo / "web/openapi/openapi.json").write_text('{"openapi":"3.0.0"}\n', encoding="utf-8")
        (self.repo / "web/dist/index.html").write_text("<html>qualified</html>\n", encoding="utf-8")
        (self.repo / "web/dist/assets/app.js").write_text("console.log('qualified');\n", encoding="utf-8")
        subprocess.run(["git", "init", "-q"], cwd=self.repo, check=True)
        subprocess.run(["git", "config", "user.email", "ci@example.invalid"], cwd=self.repo, check=True)
        subprocess.run(["git", "config", "user.name", "CI"], cwd=self.repo, check=True)
        subprocess.run(["git", "add", "web/package-lock.json"], cwd=self.repo, check=True)
        subprocess.run(["git", "commit", "-qm", "source"], cwd=self.repo, check=True)
        self.manifest = self.repo / "build/.laplace-web-artifact.json"

    def run_artifact(self, operation: str):
        return subprocess.run(
            [
                sys.executable,
                str(SCRIPT),
                operation,
                "--root",
                str(self.repo),
                "--manifest",
                str(self.manifest),
            ],
            text=True,
            capture_output=True,
        )

    def test_seal_then_verify_exact_artifact(self):
        sealed = self.run_artifact("seal")
        self.assertEqual(0, sealed.returncode, sealed.stderr)
        payload = json.loads(self.manifest.read_text())
        self.assertEqual(
            subprocess.run(
                ["git", "rev-parse", "HEAD"], cwd=self.repo, text=True,
                capture_output=True, check=True,
            ).stdout.strip(),
            payload["source_sha"],
        )
        verified = self.run_artifact("verify")
        self.assertEqual(0, verified.returncode, verified.stderr)

    def test_dist_mutation_invalidates_qualified_artifact(self):
        self.assertEqual(0, self.run_artifact("seal").returncode)
        (self.repo / "web/dist/assets/app.js").write_text("console.log('changed');\n", encoding="utf-8")
        verified = self.run_artifact("verify")
        self.assertNotEqual(0, verified.returncode)
        self.assertIn("dist_sha256", verified.stderr)

    def test_installed_directory_must_match_sealed_dist_and_receipt(self):
        self.assertEqual(0, self.run_artifact("seal").returncode)
        installed = self.repo / "installed-wwwroot"
        installed.mkdir()
        for source in (self.repo / "web/dist").rglob("*"):
            if source.is_file():
                target = installed / source.relative_to(self.repo / "web/dist")
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(source.read_bytes())
        revision = subprocess.run(
            ["git", "rev-parse", "HEAD"], cwd=self.repo, text=True,
            capture_output=True, check=True,
        ).stdout.strip()
        (installed / ".laplace-web-source-revision").write_text(revision + "\n")

        verified = subprocess.run(
            [
                sys.executable, str(SCRIPT), "verify-installed",
                "--root", str(self.repo), "--manifest", str(self.manifest),
                "--directory", str(installed),
            ],
            text=True, capture_output=True,
        )
        self.assertEqual(0, verified.returncode, verified.stderr)

        (installed / "assets/app.js").write_text("corrupt\n", encoding="utf-8")
        rejected = subprocess.run(
            [
                sys.executable, str(SCRIPT), "verify-installed",
                "--root", str(self.repo), "--manifest", str(self.manifest),
                "--directory", str(installed),
            ],
            text=True, capture_output=True,
        )
        self.assertNotEqual(0, rejected.returncode)
        self.assertIn("installed web artifact differs", rejected.stderr)

    def test_openapi_mutation_invalidates_qualified_artifact(self):
        self.assertEqual(0, self.run_artifact("seal").returncode)
        (self.repo / "web/openapi/openapi.json").write_text('{"openapi":"3.1.0"}\n', encoding="utf-8")
        verified = self.run_artifact("verify")
        self.assertNotEqual(0, verified.returncode)
        self.assertIn("openapi_sha256", verified.stderr)

    def inputs(self, tools=None):
        return ARTIFACT.build_inputs(self.repo, tools or {"node": "v22", "npm": "10"})

    def test_reuse_depends_on_source_bytes_not_timestamps_or_git_commit(self):
        source = self.repo / "web/component.ts"
        source.write_text("export const version = 1;\n")
        initial = self.inputs()
        os.utime(source, (1000000000, 1000000000))
        self.assertEqual(initial, self.inputs())
        subprocess.run(["git", "commit", "--allow-empty", "-qm", "metadata"], cwd=self.repo, check=True)
        self.assertEqual(initial, self.inputs())
        source.write_text("export const version = 2;\n")
        self.assertNotEqual(initial, self.inputs())

    def test_new_deleted_and_ignored_environment_inputs_invalidate_reuse(self):
        (self.repo / ".gitignore").write_text("*.local\n")
        initial = self.inputs()
        source = self.repo / "web/new.ts"
        source.write_text("new source\n")
        self.assertNotEqual(initial, self.inputs())
        source.unlink()
        self.assertEqual(initial, self.inputs())
        local_env = self.repo / "web/.env.production.local"
        local_env.write_text("VITE_FEATURE=true\n")
        self.assertNotEqual(initial, self.inputs())
        local_env.unlink()
        self.assertEqual(initial, self.inputs())

    def test_architecture_referenced_source_is_a_build_dependency(self):
        external = Path(self.temp.name) / "Laplace-Native"
        external.mkdir()
        source = external / "core.c"
        source.write_text("first source\n")
        atlas = self.repo / "docs/operation-atlas"
        atlas.mkdir(parents=True)
        (atlas / "traces.json").write_text(json.dumps([{"evidence": [
            {"path": "Laplace-Native/core.c", "sha256": "reviewed source hash"}
        ]}]))
        initial = self.inputs()
        source.write_text("changed source\n")
        self.assertNotEqual(initial, self.inputs())
        source.unlink()
        self.assertNotEqual(initial, self.inputs())

    def test_generated_outputs_do_not_invalidate_their_own_build_inputs(self):
        initial = self.inputs()
        (self.repo / "web/dist/index.html").write_text("rebuilt output\n")
        api = self.repo / "web/src/api"
        api.mkdir(parents=True)
        (api / "types.gen.ts").write_text("generated API\n")
        architecture = self.repo / "web/public/architecture"
        architecture.mkdir(parents=True)
        (architecture / "catalog.json").write_text("{}\n")
        self.assertEqual(initial, self.inputs())
        (self.repo / "web/openapi/openapi.json").write_text('{"openapi":"3.1.0"}\n')
        self.assertNotEqual(initial, self.inputs())

    def test_toolchain_and_vite_environment_invalidate_reuse(self):
        initial = self.inputs()
        self.assertNotEqual(initial, self.inputs({"node": "v24", "npm": "11"}))
        with patch.dict(os.environ, {"VITE_TEST_ARTIFACT": "changed"}):
            self.assertNotEqual(initial, self.inputs())

    def test_symlink_output_is_rejected_before_running_a_builder(self):
        link = self.repo / "web/dist/external"
        external = Path(self.temp.name) / "outside"
        external.mkdir()
        try:
            link.symlink_to(external, target_is_directory=True)
        except OSError:
            self.skipTest("Creating symlinks requires OS permission")
        with self.assertRaisesRegex(ValueError, "may not redirect"):
            ARTIFACT.build_web(self.repo, self.manifest)


if __name__ == "__main__":
    unittest.main(verbosity=2)
