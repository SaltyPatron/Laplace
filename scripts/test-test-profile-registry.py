#!/usr/bin/env python3
from __future__ import annotations

import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent
MODULE_PATH = ROOT / "scripts" / "test-profile-registry.py"
REGISTRY_PATH = ROOT / "scripts" / "test-profiles.json"

spec = importlib.util.spec_from_file_location("test_profile_registry", MODULE_PATH)
assert spec is not None and spec.loader is not None
registry = importlib.util.module_from_spec(spec)
spec.loader.exec_module(registry)

POLICY_IDS = {
    "policy-source-contract", "policy-registry", "policy-actions-topology",
    "policy-actions-audit", "policy-shellcheck-gate", "policy-deploy-payload-sync",
    "policy-pipeline-install", "policy-application-runtime", "policy-stockfish-release",
    "policy-managed-services", "policy-managed-host", "policy-managed-tls",
    "policy-pg-access", "policy-managed-publish-shellcheck", "policy-pipeline",
    "policy-eval-op-lane", "policy-sql-audit-tests", "policy-sql-audit",
    "policy-upgrade-drop-order", "policy-isa-gate", "policy-model-payload-gate",
    "policy-attestation-determinism", "policy-docs-inventory", "policy-placement",
    "policy-banned-dependencies",
}


class TestProfileRegistryTests(unittest.TestCase):
    def document(self):
        return json.loads(REGISTRY_PATH.read_text(encoding="utf-8"))

    def test_registry_accounts_for_all_executable_profiles(self):
        suites = registry.validate_document(self.document())
        self.assertEqual(
            POLICY_IDS | {
                "native-dev", "managed-dev", "uci-dev", "browser-dev",
                "db-health", "native-db", "managed-db", "live-floor", "live-api",
                "managed-live", "generation-eval", "managed-perf", "generation-perf",
            },
            set(suites),
        )
        self.assertEqual(registry.ALLOWED_PROFILES, {s["profile"] for s in suites.values()})
        self.assertEqual(POLICY_IDS, {
            s["id"] for s in registry.suites_for_request(suites, "policy")
        })
        self.assertEqual(
            ["native-dev", "managed-dev", "uci-dev", "browser-dev"],
            [s["id"] for s in registry.suites_for_request(suites, "dev")],
        )
        self.assertEqual(
            ["db-health", "native-db", "managed-db"],
            [s["id"] for s in registry.suites_for_request(suites, "db")],
        )
        self.assertEqual(
            ["live-floor", "live-api", "managed-live", "generation-eval"],
            [s["id"] for s in registry.suites_for_request(suites, "live")],
        )
        self.assertEqual(
            ["managed-perf", "generation-perf"],
            [s["id"] for s in registry.suites_for_request(suites, "perf")],
        )
        self.assertIn("scripts/test-test-profile-registry.py", suites["policy-registry"]["command"])

    def test_profile_boundaries_are_executable_law(self):
        suites = registry.validate_document(self.document())
        self.assertEqual("regress", suites["native-dev"]["selector"]["exclude_label"])
        self.assertEqual("regress", suites["native-db"]["selector"]["include_label"])
        self.assertEqual(
            "Tier!=db&Tier!=live&Tier!=perf",
            suites["managed-dev"]["selector"]["filter"],
        )
        self.assertEqual("Tier=db", suites["managed-db"]["selector"]["filter"])
        self.assertEqual("Tier=live", suites["managed-live"]["selector"]["filter"])
        self.assertEqual("Tier=perf", suites["managed-perf"]["selector"]["filter"])

    def test_duplicate_and_cross_profile_mutations_fail(self):
        doc = self.document()
        doc["suites"].append(copy.deepcopy(doc["suites"][0]))
        with self.assertRaisesRegex(registry.RegistryError, "duplicate suite id"):
            registry.validate_document(doc)

        doc = self.document()
        managed_db = next(s for s in doc["suites"] if s["id"] == "managed-db")
        managed_db["profile"] = "dev-managed"
        with self.assertRaisesRegex(registry.RegistryError, "DEV suites"):
            registry.validate_document(doc)

        doc = self.document()
        live = next(s for s in doc["suites"] if s["id"] == "managed-live")
        live["shared_substrate"] = "forbidden"
        with self.assertRaisesRegex(registry.RegistryError, "live suites"):
            registry.validate_document(doc)

    def test_required_zero_selection_fails_before_execution(self):
        with tempfile.TemporaryDirectory(prefix="test-profile-receipt-") as td:
            receipt = Path(td) / "receipt.json"
            with patch.object(registry, "selected_count", return_value=(0, "")), \
                 patch.object(registry, "_run") as execute:
                rc = registry.run_profile("dev-native", REGISTRY_PATH, receipt)
            self.assertEqual(1, rc)
            execute.assert_not_called()
            saved = json.loads(receipt.read_text(encoding="utf-8"))
            self.assertEqual("failed", saved["status"])
            self.assertEqual(0, saved["selected"])
            self.assertEqual("failed-zero-selection", saved["suites"][0]["status"])

    def test_result_count_drift_fails_closed(self):
        suite = registry.load_validated()["managed-dev"]
        with self.assertRaisesRegex(registry.RegistryError, "selection/result drift"):
            registry._result_counts(suite, 3, "Total: 2\nSkipped: 0\n")

    def test_dotnet_runtime_expansion_counts_actual_results(self):
        suite = registry.load_validated()["managed-dev"]
        self.assertEqual(
            (3, 1),
            registry._result_counts(
                suite, 2, "Passed: 3\nFailed: 0\nSkipped: 1\nTotal: 4\n"
            ),
        )

    def test_dotnet_discovery_counts_interleaved_solution_output(self):
        output = """Test run for A.Tests.dll
The following Tests are available:
Test run for B.Tests.dll
No test matches the given testcase filter `Tier=db` in A.Tests.dll
    B.Tests.DatabaseFixture.First
Test run for C.Tests.dll
The following Tests are available:
No test matches the given testcase filter `Tier=db` in C.Tests.dll
    B.Tests.DatabaseFixture.Second(value: 1)
"""
        self.assertEqual(2, registry._count_dotnet_list(output))

    def test_dotnet_discovery_without_listing_heading_is_zero(self):
        self.assertEqual(
            0,
            registry._count_dotnet_list("    indented build warning without discovery\n"),
        )

    def test_dotnet_runtime_expansion_receipt_preserves_discovery_and_exact_selection(self):
        with tempfile.TemporaryDirectory(prefix="test-profile-receipt-") as td:
            receipt = Path(td) / "receipt.json"
            output = "Passed: 3\nFailed: 0\nSkipped: 1\nTotal: 4\n"
            with patch.object(
                registry, "selected_count",
                side_effect=lambda suite: (2, "") if suite["runner"] == "dotnet" else (1, ""),
            ), patch.object(registry, "_run", return_value=(0, output, 5)):
                rc = registry.run_profile("dev-managed", REGISTRY_PATH, receipt)
            self.assertEqual(0, rc)
            saved = json.loads(receipt.read_text(encoding="utf-8"))
            self.assertEqual("success", saved["status"])
            self.assertEqual(6, saved["selected"])
            self.assertEqual(5, saved["executed"])
            self.assertEqual(1, saved["skipped"])
            self.assertEqual(2, saved["suites"][0]["discovered"])
            self.assertEqual(4, saved["suites"][0]["selected"])

    def test_receipt_contains_source_artifact_counts_and_suite_results(self):
        records = [{
            "id": "fixture", "profile": "dev-native", "runner": "script",
            "selected": 1, "executed": 1, "skipped": 0,
            "status": "success", "elapsed_ms": 4,
        }]
        receipt = registry._finish_receipt("dev-native", 1.0, records, "success")
        for key in (
            "schema_version", "profile", "source_sha", "built_native_sha256",
            "installed_native_sha256", "started_at_unix_ms", "ended_at_unix_ms",
            "elapsed_ms", "selected", "executed", "skipped", "status", "suites",
        ):
            self.assertIn(key, receipt)
        self.assertEqual(1, receipt["selected"])
        self.assertEqual(1, receipt["executed"])

    def test_legacy_shell_is_only_a_profile_alias(self):
        source = (ROOT / "scripts/test-parallel.sh").read_text(encoding="utf-8")
        self.assertIn("test-profile-registry.py run --profile", source)
        for forbidden in (
            "DOTNET_DEV_FILTER=", "DOTNET_DB_FILTER=", "DOTNET_LIVE_FILTER=",
            "ctest --test-dir", "dotnet test Laplace.slnx",
        ):
            self.assertNotIn(forbidden, source)

    def test_policy_and_justfile_compatibility_surfaces_delegate_to_profiles(self):
        policy = (ROOT / "scripts/ci-policy.sh").read_text(encoding="utf-8")
        self.assertIn("test-profile-registry.py run --profile policy", policy)
        self.assertNotIn("test-managed-host.py", policy)
        legacy_policy = (ROOT / "scripts/ci-policy-suite.sh").read_text(encoding="utf-8")
        self.assertIn("test-profile-registry.py run --profile policy", legacy_policy)
        self.assertNotIn("test-managed-host.py", legacy_policy)
        registry_source = REGISTRY_PATH.read_text(encoding="utf-8")
        self.assertNotIn('"scripts/ci-policy-suite.sh"', registry_source)

        just = (ROOT / "Justfile").read_text(encoding="utf-8")
        self.assertNotIn("ctest ", just)
        self.assertNotIn("dotnet test ", just)
        self.assertNotIn("eval-generation.py", just)
        for recipe in (
            "eval:", "verify:", "verify-determinism:", "verify-fk:", "verify-perfcache:"
        ):
            block = just.split(recipe, 1)[1].split("\n\n", 1)[0]
            self.assertIn("scripts/test-parallel.sh", block, recipe)


if __name__ == "__main__":
    unittest.main(verbosity=2)
