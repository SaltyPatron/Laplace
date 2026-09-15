#!/usr/bin/env python3
from __future__ import annotations

import copy
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import sys
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
    "policy-source-contract", "policy-registry", "policy-sql-catalog", "policy-actions-topology",
    "policy-actions-audit", "policy-shellcheck-gate", "policy-deploy-payload-sync",
    "policy-pipeline-install", "policy-application-runtime", "policy-stockfish-release", "policy-zstd-release",
    "policy-chess-dependencies", "policy-stockfish-corpus", "policy-cutechess-release", "policy-chess-environment-benchmark",
    "policy-recorded-chess-benchmark", "policy-retained-chess-ingestion", "policy-benchmark-registry", "policy-postgres-geometry-benchmark",
    "policy-managed-services", "policy-managed-host", "policy-managed-tls",
    "policy-managed-database-quiescence", "policy-repair-transaction",
    "policy-legacy-content-history", "policy-legacy-repair-evidence",
    "policy-operational-source-readback", "policy-operational-task",
    "policy-pg-access", "policy-managed-publish-shellcheck", "policy-pipeline",
    "policy-eval-op-lane", "policy-sql-audit-tests", "policy-sql-audit",
    "policy-upgrade-drop-order", "policy-isa-gate", "policy-model-payload-gate",
    "policy-attestation-determinism", "policy-docs-inventory", "policy-placement",
    "policy-banned-dependencies",
}


class TestProfileRegistryTests(unittest.TestCase):
    def document(self):
        return json.loads(REGISTRY_PATH.read_text(encoding="utf-8"))

    @unittest.skipUnless(shutil.which("ctest"), "CTest is required for the executable failure fixture")
    def test_real_ctest_failure_emits_regression_diff_and_keeps_failed_receipt(self):
        with tempfile.TemporaryDirectory(prefix="native-regression-diagnostic-") as td:
            root = Path(td)
            build = root / "build"
            build.mkdir()
            driver = root / "fail.py"
            driver.write_text(
                "from pathlib import Path\n"
                "import sys\n"
                "p=Path(__file__).parent/'build'/'regression.diffs'\n"
                "p.write_text('- expected result\\n+ ERROR: actual fixture failure\\n::error::fixture text\\n')\n"
                "print('The differences can be viewed in the file '+chr(34)+str(p)+chr(34)+'.')\n"
                "sys.exit(1)\n", encoding="utf-8")
            (build / "CTestTestfile.cmake").write_text(
                f'add_test(regression_failure [=[{sys.executable}]=] [=[{driver}]=])\n',
                encoding="utf-8")
            suite = registry.load_validated()["native-dev"]
            receipt = root / "receipt.json"
            log = io.StringIO()
            with patch.object(registry, "ROOT", root), \
                 patch.object(registry, "load_validated", return_value={suite["id"]: suite}), \
                 patch.dict(os.environ, {"GITHUB_STEP_SUMMARY": ""}), \
                 contextlib.redirect_stdout(log):
                rc = registry.run_profile("dev-native", REGISTRY_PATH, receipt)
            saved = json.loads(receipt.read_text())
            self.assertEqual(1, rc)
            self.assertEqual("failed", saved["status"])
            self.assertEqual("failed", saved["suites"][0]["status"])
            diagnostic = saved["suites"][0]["failure_diagnostics"][0]
            self.assertEqual("read", diagnostic["status"])
            self.assertFalse(diagnostic["truncated"])
            self.assertEqual(registry._sha256(build / "regression.diffs"),
                             diagnostic["emitted_sha256"])
            self.assertIn("NATIVE_REGRESSION | + ERROR: actual fixture failure", log.getvalue())
            self.assertIn("NATIVE_REGRESSION | ::error::fixture text", log.getvalue())

    def test_native_diagnostic_rejects_paths_outside_selected_build(self):
        with tempfile.TemporaryDirectory(prefix="native-regression-path-") as td:
            root = Path(td)
            build = root / "build"
            build.mkdir()
            outside = root / "regression.diffs"
            outside.write_text("outside data must not be printed")
            paths = [outside]
            if os.name == "posix":
                linked = build / "regression.diffs"
                linked.symlink_to(outside)
                paths.append(linked)
            log = io.StringIO()
            with patch.object(registry, "ROOT", root), contextlib.redirect_stdout(log):
                records = registry._ctest_failure_diagnostics(
                    "\n".join(f'file "{p}"' for p in paths))
            self.assertEqual(len(paths), len(records))
            self.assertTrue(all(r["status"] == "unavailable" for r in records))
            self.assertNotIn("outside data must not be printed", log.getvalue())

    def test_native_diagnostic_records_bounded_prefix_without_claiming_full_hash(self):
        with tempfile.TemporaryDirectory(prefix="native-regression-bound-") as td:
            root = Path(td)
            path = root / "build" / "regression.diffs"
            path.parent.mkdir()
            path.write_bytes(b"x" * (2 * 1024 * 1024 + 1))
            with patch.object(registry, "ROOT", root), contextlib.redirect_stdout(io.StringIO()):
                records = registry._ctest_failure_diagnostics(f'file "{path}"')
            self.assertEqual(1, len(records))
            self.assertTrue(records[0]["truncated"])
            self.assertEqual(2 * 1024 * 1024, records[0]["emitted_bytes"])
            self.assertNotEqual(registry._sha256(path), records[0]["emitted_sha256"])

    def test_one_registered_suite_runs_without_other_profile_suites(self):
        with tempfile.TemporaryDirectory(prefix="test-profile-suite-") as td:
            receipt = Path(td) / "receipt.json"
            with patch.object(registry, "discovered_count", return_value=(2, "")) as discover, \
                 patch.object(registry, "_run", return_value=(0, "Total: 2\nSkipped: 0\n", 5)) as execute:
                rc = registry.run_profile("db", REGISTRY_PATH, receipt, "managed-db")
            self.assertEqual(0, rc)
            self.assertEqual(1, discover.call_count)
            self.assertEqual(1, execute.call_count)
            self.assertEqual("managed-db", discover.call_args.args[0]["id"])
            saved = json.loads(receipt.read_text())
            self.assertEqual({"scope": "suite", "suite": "managed-db"}, saved["selection"])
            self.assertEqual(["managed-db"], [item["id"] for item in saved["suites"]])

    def test_unknown_empty_and_wrong_profile_suite_are_rejected_before_discovery(self):
        for name in ("not-registered", "", "managed-live"):
            with self.subTest(name=name), patch.object(registry, "discovered_count") as discover:
                with self.assertRaisesRegex(registry.RegistryError, "not a single member"):
                    registry.run_profile("db", REGISTRY_PATH, None, name)
                discover.assert_not_called()

    def test_cli_rejects_repeated_suite_instead_of_taking_last_value(self):
        with patch.object(registry, "run_profile") as run:
            self.assertEqual(2, registry.main(["run", "--profile", "db", "--suite", "native-db",
                                              "--suite", "native-db"]))
            run.assert_not_called()

    def test_native_failure_captures_diagnostics_before_leaving_original_failure(self):
        with tempfile.TemporaryDirectory(prefix="test-profile-native-") as td:
            receipt = Path(td) / "receipt.json"
            events = []
            def execute(*_args):
                events.append("native-test")
                return 8, "native regression failed\n", 5
            def capture(suite):
                events.append("capture-" + suite["id"])
                return {"directory": str(Path(td) / "evidence"), "capture_exit_code": 1}
            with patch.object(registry, "discovered_count", return_value=(6, "")), \
                 patch.object(registry, "_run", side_effect=execute), \
                 patch.object(registry, "_capture_native_failure", side_effect=capture):
                rc = registry.run_profile("db", REGISTRY_PATH, receipt, "native-db")
            self.assertEqual(1, rc)
            self.assertEqual(["native-test", "capture-native-db"], events)
            saved = json.loads(receipt.read_text())
            self.assertEqual("failed", saved["status"])
            self.assertEqual(1, saved["suites"][0]["native_diagnostics"]["capture_exit_code"])

    def test_successful_native_suite_does_not_collect_prior_failed_output(self):
        with tempfile.TemporaryDirectory(prefix="test-profile-native-") as td:
            with patch.object(registry, "discovered_count", return_value=(6, "")), \
                 patch.object(registry, "_run", return_value=(0, "", 5)), \
                 patch.object(registry, "_capture_native_failure") as capture:
                rc = registry.run_profile("db", REGISTRY_PATH, Path(td) / "receipt.json", "native-db")
            self.assertEqual(0, rc)
            capture.assert_not_called()

    def test_native_failure_hook_runs_real_collector_and_preserves_exact_diff(self):
        with tempfile.TemporaryDirectory(prefix="test-profile-native-capture-") as td:
            root = Path(td)
            (root / "scripts").mkdir()
            (root / "scripts/capture-native-regression.py").write_bytes(
                (ROOT / "scripts/capture-native-regression.py").read_bytes())
            relative = "extension/laplace_substrate/tests/regress_output/regression.diffs"
            source = root / "build" / relative
            source.parent.mkdir(parents=True)
            source.write_bytes(b"+ERROR: fixture exercises the real collector\n")
            with patch.object(registry, "ROOT", root), patch.dict(
                os.environ, {"LAPLACE_NATIVE_REGRESSION_EVIDENCE_DIRECTORY": str(root / "evidence")}
            ):
                result = registry._capture_native_failure({"id": "native-db"})
            self.assertEqual(0, result["capture_exit_code"])
            receipts = list((root / "evidence/current").glob("capture-*/receipt.json"))
            self.assertEqual(1, len(receipts))
            self.assertEqual(source.read_bytes(), (receipts[0].parent / relative).read_bytes())

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

    def test_required_zero_discovery_fails_before_execution(self):
        with tempfile.TemporaryDirectory(prefix="test-profile-receipt-") as td:
            receipt = Path(td) / "receipt.json"
            with patch.object(registry, "discovered_count", return_value=(0, "")), \
                 patch.object(registry, "_run") as execute:
                rc = registry.run_profile("dev-native", REGISTRY_PATH, receipt)
            self.assertEqual(1, rc)
            execute.assert_not_called()
            saved = json.loads(receipt.read_text(encoding="utf-8"))
            self.assertEqual("failed", saved["status"])
            self.assertEqual(0, saved["discovered"])
            self.assertEqual(0, saved["selected"])
            self.assertEqual("failed-zero-discovery", saved["suites"][0]["status"])

    def test_dotnet_runtime_filtered_selection_can_be_smaller_than_discovery(self):
        suite = registry.load_validated()["managed-dev"]
        # `--list-tests` can discover excluded Tier=db/live/perf tests. The
        # filtered runtime total is therefore authoritative selection, not a loss.
        self.assertEqual(
            (2, 0),
            registry._result_counts(suite, 300, "Passed: 2\nFailed: 0\nSkipped: 0\nTotal: 2\n"),
        )

    def test_dotnet_runtime_expansion_counts_actual_results(self):
        suite = registry.load_validated()["managed-dev"]
        self.assertEqual(
            (3, 1),
            registry._result_counts(
                suite, 2, "Passed: 3\nFailed: 0\nSkipped: 1\nTotal: 4\n"
            ),
        )

    def test_required_dotnet_zero_filtered_selection_fails_after_runtime_selection(self):
        with tempfile.TemporaryDirectory(prefix="test-profile-receipt-") as td:
            receipt = Path(td) / "receipt.json"

            def discover(suite):
                return (3, "all discovered") if suite["runner"] == "dotnet" else (1, "declared")

            def execute(command, env, timeout):
                if command[:2] == ["dotnet", "test"]:
                    return 0, "No test matches the given testcase filter\n", 5
                return 0, "", 5

            with patch.object(registry, "discovered_count", side_effect=discover), \
                 patch.object(registry, "_run", side_effect=execute):
                rc = registry.run_profile("dev-managed", REGISTRY_PATH, receipt)

            self.assertEqual(1, rc)
            saved = json.loads(receipt.read_text(encoding="utf-8"))
            managed = saved["suites"][0]
            self.assertEqual(3, managed["discovered"])
            self.assertEqual(0, managed["selected"])
            self.assertEqual(0, managed["executed"])
            self.assertEqual("failed-zero-selection", managed["status"])

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
                registry, "discovered_count",
                side_effect=lambda suite: (2, "") if suite["runner"] == "dotnet" else (1, ""),
            ), patch.object(registry, "_run", return_value=(0, output, 5)):
                rc = registry.run_profile("dev-managed", REGISTRY_PATH, receipt)
            self.assertEqual(0, rc)
            saved = json.loads(receipt.read_text(encoding="utf-8"))
            self.assertEqual("success", saved["status"])
            self.assertEqual(4, saved["discovered"])
            self.assertEqual(6, saved["selected"])
            self.assertEqual(5, saved["executed"])
            self.assertEqual(1, saved["skipped"])
            self.assertEqual(2, saved["suites"][0]["discovered"])
            self.assertEqual(4, saved["suites"][0]["selected"])

    def test_receipt_contains_source_artifact_counts_and_suite_results(self):
        records = [{
            "id": "fixture", "profile": "dev-native", "runner": "script",
            "discovered": 1, "selected": 1, "executed": 1, "skipped": 0,
            "status": "success", "elapsed_ms": 4,
        }]
        receipt = registry._finish_receipt("dev-native", 1.0, records, "success")
        for key in (
            "schema_version", "profile", "source_sha", "built_native_sha256",
            "installed_native_sha256", "started_at_unix_ms", "ended_at_unix_ms",
            "elapsed_ms", "discovered", "selected", "executed", "skipped", "status", "suites",
        ):
            self.assertIn(key, receipt)
        self.assertEqual(1, receipt["discovered"])
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
