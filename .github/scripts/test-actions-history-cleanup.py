#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location(
    "cleanup_actions_history", HERE / "cleanup-actions-history.py"
)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
SPEC.loader.exec_module(MODULE)


class CleanupWorkflowContractTests(unittest.TestCase):
    def test_cleanup_marker_has_unique_nonpreemptible_concurrency(self):
        workflow = (HERE.parent / "workflows" / "ci-contract.yml").read_text(encoding="utf-8")
        self.assertIn("laplace-actions-history-cleanup-{0}", workflow)
        self.assertIn("github.run_id", workflow)
        self.assertIn(
            "cancel-in-progress: ${{ github.event_name != 'push' || !contains(github.event.head_commit.message, '[actions-history-cleanup]') }}",
            workflow,
        )
        self.assertIn("if: github.event_name == 'push' && contains(github.event.head_commit.message, '[actions-history-cleanup]')", workflow)


class CleanupActionsHistoryTests(unittest.TestCase):
    def test_current_workflow_paths_only_reads_workflow_directory(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / ".github/workflows").mkdir(parents=True)
            (root / ".github/workflows/a.yml").write_text("name: A\n")
            (root / ".github/workflows/b.yaml").write_text("name: B\n")
            (root / ".github/other").mkdir()
            (root / ".github/other/c.yml").write_text("name: C\n")
            self.assertEqual(
                MODULE.current_workflow_paths(root),
                {".github/workflows/a.yml", ".github/workflows/b.yaml"},
            )

    def test_stale_group_with_any_artifact_is_preserved_whole(self):
        runs = [
            {"id": 1, "workflow_id": 9, "path": ".github/workflows/old.yml", "name": "Old", "status": "completed"},
            {"id": 2, "workflow_id": 9, "path": ".github/workflows/old.yml", "name": "Old", "status": "completed"},
            {"id": 3, "workflow_id": 10, "path": ".github/workflows/gone.yml", "name": "Gone", "status": "completed"},
        ]
        grouped = MODULE.group_stale_runs(runs, set())
        selected, preserved, nonterminal = MODULE.classify_groups(grouped, {2}, 100)
        self.assertEqual([item["workflow_id"] for item in selected], [10])
        self.assertEqual([item["workflow_id"] for item in preserved], [9])
        self.assertEqual(nonterminal, [])

    def test_current_path_is_never_stale_even_if_name_changed(self):
        runs = [
            {"id": 1, "workflow_id": 9, "path": ".github/workflows/current.yml", "name": "Ancient name", "status": "completed"},
        ]
        grouped = MODULE.group_stale_runs(runs, {".github/workflows/current.yml"})
        self.assertEqual(grouped, {})

    def test_nonterminal_stale_identity_is_preserved(self):
        runs = [
            {"id": 1, "workflow_id": 9, "path": ".github/workflows/old.yml", "name": "Old", "status": "in_progress"},
            {"id": 2, "workflow_id": 9, "path": ".github/workflows/old.yml", "name": "Old", "status": "completed"},
        ]
        grouped = MODULE.group_stale_runs(runs, set())
        selected, preserved, nonterminal = MODULE.classify_groups(grouped, set(), 100)
        self.assertEqual(selected, [])
        self.assertEqual(preserved, [])
        self.assertEqual([item["workflow_id"] for item in nonterminal], [9])

    def test_budget_prefers_complete_small_identities(self):
        runs = []
        for run_id in range(1, 6):
            runs.append({"id": run_id, "workflow_id": 20, "path": ".github/workflows/big.yml", "name": "Big", "status": "completed"})
        runs.append({"id": 20, "workflow_id": 21, "path": ".github/workflows/one.yml", "name": "One", "status": "completed"})
        runs.extend([
            {"id": 30, "workflow_id": 22, "path": ".github/workflows/two.yml", "name": "Two", "status": "completed"},
            {"id": 31, "workflow_id": 22, "path": ".github/workflows/two.yml", "name": "Two", "status": "completed"},
        ])
        grouped = MODULE.group_stale_runs(runs, set())
        selected, _, _ = MODULE.classify_groups(grouped, set(), 3)
        self.assertEqual([item["workflow_id"] for item in selected], [21, 22])


if __name__ == "__main__":
    unittest.main(verbosity=2)
