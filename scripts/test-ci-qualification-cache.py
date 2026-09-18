#!/usr/bin/env python3
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "ci-qualification-cache.py"


class QualificationCacheTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="laplace-qualification-")
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name) / "repo"
        self.cache = Path(self.temp.name) / "cache"
        (self.repo / "app").mkdir(parents=True)
        (self.repo / "web").mkdir()
        (self.repo / "scripts").mkdir()
        (self.repo / "app" / "managed.cs").write_text("managed-v1\n", encoding="utf-8")
        (self.repo / "web" / "app.ts").write_text("web-v1\n", encoding="utf-8")
        (self.repo / "scripts" / "test-parallel.sh").write_text("driver-v1\n", encoding="utf-8")
        subprocess.run(["git", "init", "-q"], cwd=self.repo, check=True)
        subprocess.run(["git", "add", "."], cwd=self.repo, check=True)

    def run_cache(self, operation: str, suite: str, check: bool = False):
        result = subprocess.run(
            [
                sys.executable,
                str(SCRIPT),
                operation,
                "--suite",
                suite,
                "--root",
                str(self.repo),
                "--cache-root",
                str(self.cache),
                "--source-sha",
                "test-source",
            ],
            text=True,
            capture_output=True,
        )
        if check and result.returncode != 0:
            self.fail(result.stdout + result.stderr)
        return result

    def fingerprint(self, suite: str) -> str:
        result = self.run_cache("fingerprint", suite, check=True)
        return result.stdout.strip()

    def test_unrelated_web_change_does_not_invalidate_managed_receipt(self):
        managed_before = self.fingerprint("managed-dev")
        browser_before = self.fingerprint("browser-dev")
        (self.repo / "web" / "app.ts").write_text("web-v2\n", encoding="utf-8")
        subprocess.run(["git", "add", "web/app.ts"], cwd=self.repo, check=True)
        self.assertEqual(managed_before, self.fingerprint("managed-dev"))
        self.assertNotEqual(browser_before, self.fingerprint("browser-dev"))

    def test_source_returns_revision_that_owns_matching_receipt(self):
        self.run_cache("record", "native-dev", check=True)
        source = self.run_cache("source", "native-dev", check=True)
        self.assertEqual(source.stdout.strip(), "test-source")

    def test_latest_source_tracks_most_recent_success_for_bounded_artifact_reuse(self):
        self.run_cache("record", "native-dev", check=True)
        latest = self.run_cache("latest-source", "native-dev", check=True)
        self.assertEqual(latest.stdout.strip(), "test-source")

    def test_recorded_receipt_is_reused_until_relevant_input_changes(self):
        miss = self.run_cache("check", "managed-dev")
        self.assertEqual(miss.returncode, 1)
        recorded = self.run_cache("record", "managed-dev", check=True)
        self.assertIn("QUALIFICATION_RECORDED", recorded.stdout)
        hit = self.run_cache("check", "managed-dev", check=True)
        self.assertIn("QUALIFICATION_HIT", hit.stdout)

        (self.repo / "app" / "managed.cs").write_text("managed-v2\n", encoding="utf-8")
        subprocess.run(["git", "add", "app/managed.cs"], cwd=self.repo, check=True)
        invalidated = self.run_cache("check", "managed-dev")
        self.assertEqual(invalidated.returncode, 1)
        self.assertIn("QUALIFICATION_MISS", invalidated.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
