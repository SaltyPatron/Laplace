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

    def run_cache(
        self, operation: str, suite: str, check: bool = False,
        managed_projects: str | None = None,
    ):
        environment = dict(os.environ)
        if managed_projects is not None:
            environment["LAPLACE_MANAGED_TEST_PROJECTS"] = managed_projects
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
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ],
            text=True,
            capture_output=True,
            env=environment,
        )
        if check and result.returncode != 0:
            self.fail(result.stdout + result.stderr)
        return result

    def fingerprint(self, suite: str, managed_projects: str | None = None) -> str:
        result = self.run_cache(
            "fingerprint", suite, check=True, managed_projects=managed_projects
        )
        return result.stdout.strip()

    def test_unrelated_web_change_does_not_invalidate_managed_receipt(self):
        managed_before = self.fingerprint("managed-dev")
        browser_before = self.fingerprint("browser-dev")
        (self.repo / "web" / "app.ts").write_text("web-v2\n", encoding="utf-8")
        subprocess.run(["git", "add", "web/app.ts"], cwd=self.repo, check=True)
        self.assertEqual(managed_before, self.fingerprint("managed-dev"))
        self.assertNotEqual(browser_before, self.fingerprint("browser-dev"))

    def test_targeted_managed_receipt_ignores_unrelated_project_inputs(self):
        for name in ("A", "A.Tests", "B", "B.Tests"):
            (self.repo / "app" / name).mkdir(parents=True, exist_ok=True)
        (self.repo / "app/A/A.csproj").write_text("<Project />\n", encoding="utf-8")
        (self.repo / "app/A/a.cs").write_text("a-v1\n", encoding="utf-8")
        (self.repo / "app/A.Tests/A.Tests.csproj").write_text(
            '<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>'
            '<ItemGroup><ProjectReference Include="../A/A.csproj" /></ItemGroup></Project>\n',
            encoding="utf-8",
        )
        (self.repo / "app/A.Tests/test.cs").write_text("test-a\n", encoding="utf-8")
        (self.repo / "app/B/B.csproj").write_text("<Project />\n", encoding="utf-8")
        (self.repo / "app/B/b.cs").write_text("b-v1\n", encoding="utf-8")
        (self.repo / "app/B.Tests/B.Tests.csproj").write_text(
            '<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>'
            '<ItemGroup><ProjectReference Include="../B/B.csproj" /></ItemGroup></Project>\n',
            encoding="utf-8",
        )
        (self.repo / "app/B.Tests/test.cs").write_text("test-b\n", encoding="utf-8")
        subprocess.run(["git", "add", "."], cwd=self.repo, check=True)

        selected = "app/A.Tests/A.Tests.csproj"
        before = self.fingerprint("managed-dev", selected)

        (self.repo / "app/B/b.cs").write_text("b-v2\n", encoding="utf-8")
        subprocess.run(["git", "add", "app/B/b.cs"], cwd=self.repo, check=True)
        self.assertEqual(before, self.fingerprint("managed-dev", selected))

        (self.repo / "app/A/a.cs").write_text("a-v2\n", encoding="utf-8")
        subprocess.run(["git", "add", "app/A/a.cs"], cwd=self.repo, check=True)
        self.assertNotEqual(before, self.fingerprint("managed-dev", selected))

    def test_targeted_managed_receipt_can_be_recorded_and_reused(self):
        for name in ("A", "A.Tests"):
            (self.repo / "app" / name).mkdir(parents=True, exist_ok=True)
        (self.repo / "app/A/A.csproj").write_text("<Project />\n", encoding="utf-8")
        (self.repo / "app/A/a.cs").write_text("a-v1\n", encoding="utf-8")
        (self.repo / "app/A.Tests/A.Tests.csproj").write_text(
            '<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>'
            '<ItemGroup><ProjectReference Include="../A/A.csproj" /></ItemGroup></Project>\n',
            encoding="utf-8",
        )
        (self.repo / "app/A.Tests/test.cs").write_text("test-a\n", encoding="utf-8")
        subprocess.run(["git", "add", "."], cwd=self.repo, check=True)
        selected = "app/A.Tests/A.Tests.csproj"

        miss = self.run_cache("check", "managed-dev", managed_projects=selected)
        self.assertEqual(1, miss.returncode)
        self.run_cache("record", "managed-dev", check=True, managed_projects=selected)
        hit = self.run_cache("check", "managed-dev", check=True, managed_projects=selected)
        self.assertIn("QUALIFICATION_HIT", hit.stdout)

    def test_source_returns_revision_that_owns_matching_receipt(self):
        self.run_cache("record", "native-dev", check=True)
        source = self.run_cache("source", "native-dev", check=True)
        self.assertEqual(source.stdout.strip(), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")

    def test_latest_source_tracks_most_recent_success_for_bounded_artifact_reuse(self):
        self.run_cache("record", "native-dev", check=True)
        latest = self.run_cache("latest-source", "native-dev", check=True)
        self.assertEqual(latest.stdout.strip(), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")

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
