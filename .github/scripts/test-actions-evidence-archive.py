#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "archive-actions-evidence.py"
SPEC = importlib.util.spec_from_file_location("archive_actions_evidence", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
SPEC.loader.exec_module(MODULE)


class ArchiveActionsEvidenceTests(unittest.TestCase):
    def test_safe_name_removes_release_asset_unsafe_noise(self):
        self.assertEqual(
            "weird-name-with-spaces.zip",
            MODULE.safe_name("weird name / with spaces.zip"),
        )

    def test_expired_artifacts_are_not_treated_as_retrievable(self):
        values = [
            {"id": 1, "expired": False},
            {"id": 2, "expired": True},
            {"id": 3},
        ]
        self.assertEqual(
            [1, 3],
            [item["id"] for item in MODULE.retrievable_artifacts(values)],
        )

    def test_chunk_planner_keeps_runs_together_and_respects_target(self):
        artifacts = {
            1: [{"id": 11, "size_in_bytes": 40, "expired": False}],
            2: [
                {"id": 21, "size_in_bytes": 30, "expired": False},
                {"id": 22, "size_in_bytes": 20, "expired": False},
            ],
            3: [{"id": 31, "size_in_bytes": 80, "expired": False}],
        }
        self.assertEqual(
            [[1, 2], [3]],
            MODULE.plan_chunks([1, 2, 3], artifacts, 100),
        )

    def test_workflow_metadata_includes_all_runs_but_only_counts_live_bytes(self):
        entry = {
            "workflow_id": 7,
            "path": ".github/workflows/old.yml",
            "names": {"Old"},
            "runs": [
                {"id": 1, "workflow_id": 7, "path": ".github/workflows/old.yml"},
                {"id": 2, "workflow_id": 7, "path": ".github/workflows/old.yml"},
            ],
        }
        artifacts = {
            1: [{"id": 11, "name": "live", "size_in_bytes": 40, "expired": False, "workflow_run": {"id": 1}}],
            2: [{"id": 21, "name": "expired", "size_in_bytes": 90, "expired": True, "workflow_run": {"id": 2}}],
        }
        value = MODULE.workflow_metadata(entry, artifacts)
        self.assertEqual(value["run_count"], 2)
        self.assertEqual(value["artifact_count"], 2)
        self.assertEqual(value["retrievable_artifact_count"], 1)
        self.assertEqual(value["artifact_source_bytes"], 40)


if __name__ == "__main__":
    unittest.main(verbosity=2)
