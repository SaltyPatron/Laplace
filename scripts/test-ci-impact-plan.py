#!/usr/bin/env python3
from __future__ import annotations

import json
import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "ci-impact-plan.py"


def plan(*paths: str) -> dict:
    command = [sys.executable, str(SCRIPT)]
    for path in paths:
        command += ["--changed-file", path]
    result = subprocess.run(command, cwd=ROOT, check=True, text=True, capture_output=True)
    return json.loads(result.stdout)


class ImpactPlanTests(unittest.TestCase):
    def test_web_only_change_does_not_invalidate_native_or_managed_dev(self):
        value = plan("web/src/App.tsx")
        self.assertEqual(value["components"], ["web"])
        self.assertEqual(value["dev_suites"], ["browser-dev"])
        self.assertFalse(value["full_qualification"])

    def test_native_change_invalidates_native_managed_and_uci_but_not_browser(self):
        value = plan("engine/core/src/example.cpp")
        self.assertEqual(value["components"], ["managed", "native", "uci"])
        self.assertEqual(value["dev_suites"], ["native-dev", "managed-dev", "uci-dev"])
        self.assertFalse(value["full_qualification"])

    def test_chess_change_adds_uci_to_managed(self):
        value = plan("app/Laplace.Chess/Service/Foo.cs")
        self.assertIn("managed-dev", value["dev_suites"])
        self.assertIn("uci-dev", value["dev_suites"])
        self.assertNotIn("native-dev", value["dev_suites"])

    def test_unknown_production_path_fails_safe_to_full_qualification(self):
        value = plan("mystery/runtime.dat")
        self.assertEqual(
            value["dev_suites"],
            ["native-dev", "managed-dev", "uci-dev", "browser-dev"],
        )
        self.assertTrue(value["full_qualification"])
        self.assertEqual(value["unknown_paths"], ["mystery/runtime.dat"])

    def test_docs_and_workflow_paths_do_not_create_product_work(self):
        value = plan("docs/README.md", ".github/workflows/foo.yml")
        self.assertEqual(value["dev_suites"], [])
        self.assertEqual(value["components"], [])
        self.assertFalse(value["full_qualification"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
