#!/usr/bin/env python3
from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "web-artifact.py"


class WebArtifactTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="web-artifact-")
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
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

    def run(self, operation: str):
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
        sealed = self.run("seal")
        self.assertEqual(0, sealed.returncode, sealed.stderr)
        payload = json.loads(self.manifest.read_text())
        self.assertEqual(
            subprocess.run(
                ["git", "rev-parse", "HEAD"], cwd=self.repo, text=True,
                capture_output=True, check=True,
            ).stdout.strip(),
            payload["source_sha"],
        )
        verified = self.run("verify")
        self.assertEqual(0, verified.returncode, verified.stderr)

    def test_dist_mutation_invalidates_qualified_artifact(self):
        self.assertEqual(0, self.run("seal").returncode)
        (self.repo / "web/dist/assets/app.js").write_text("console.log('changed');\n", encoding="utf-8")
        verified = self.run("verify")
        self.assertNotEqual(0, verified.returncode)
        self.assertIn("dist_sha256", verified.stderr)

    def test_openapi_mutation_invalidates_qualified_artifact(self):
        self.assertEqual(0, self.run("seal").returncode)
        (self.repo / "web/openapi/openapi.json").write_text('{"openapi":"3.1.0"}\n', encoding="utf-8")
        verified = self.run("verify")
        self.assertNotEqual(0, verified.returncode)
        self.assertIn("openapi_sha256", verified.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
