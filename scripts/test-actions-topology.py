#!/usr/bin/env python3
"""Executable contracts for one-product Actions orchestration."""
from __future__ import annotations

from pathlib import Path
import copy
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
import yaml

ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ROOT / ".github" / "workflows"
MAIN = WORKFLOWS / "laplace.yml"
PR = WORKFLOWS / "pr-validation.yml"
PRODUCT = ROOT / "scripts" / "product-ci.sh"


def load(path: Path):
    return yaml.load(path.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)


def commands(job: dict) -> str:
    return "\n".join(step.get("run", "") for step in job.get("steps", []))


class ActionsAuthorityTests(unittest.TestCase):
    def test_post_stockfish_operational_proof_precedes_upload_and_measurement(self):
        workflow = load(MAIN)
        steps = workflow["jobs"]["product"]["steps"]
        names = [step.get("name") for step in steps]
        lifecycle = names.index("Run full product lifecycle")
        upload = names.index("Upload operational seed and execution receipts")
        self.assertLess(lifecycle, upload)
        self.assertNotIn("Admit official Stockfish source and verify native corpus readback", names)
        self.assertNotIn("Verify ordinary operational execution after Stockfish admission", names)
        self.assertEqual("always() && steps.full_product.outcome != 'skipped' && (env.LAPLACE_STAGE == 'all' || env.LAPLACE_STAGE == 'applications')",
                         steps[upload]["if"])
        self.assertEqual("/build/laplace/work/operational-proof/${{ github.run_id }}-${{ github.run_attempt }}/",
                         steps[upload]["with"]["path"])
        self.assertEqual("always() && !cancelled() && needs.product.outputs.chess_benchmark_ready == 'true'",
                         workflow["jobs"]["chess_environment"]["if"])
        self.assertEqual("${{ steps.full_product.outputs.chess_benchmark_ready }}",
                         workflow["jobs"]["product"]["outputs"]["chess_benchmark_ready"])
        recorded = next(step for step in steps if step.get("name") == "Retain installed recorded-game benchmark evidence")
        self.assertEqual("always() && env.LAPLACE_FAST_ONLY != '1' && (env.LAPLACE_STAGE == 'all' || env.LAPLACE_STAGE == 'applications')",
                         recorded["if"])
        self.assertEqual("/build/laplace/work/recorded-chess-evidence/${{ github.run_id }}-${{ github.run_attempt }}/",
                         recorded["with"]["path"])
        runtime = next(step for step in steps if step.get("name") == "Retain installed chess service and bootstrap observations")
        self.assertIn("always()", runtime["if"])
        self.assertEqual("/build/laplace/work/chess-runtime-evidence/${{ github.run_id }}-${{ github.run_attempt }}/",
                         runtime["with"]["path"])

    def run_post_stockfish_proof(self, receipt_values, first_rc=0, second_rc=0, *, fresh="0", restore="0", initial_seed="e56a93c8-36b4-46ef-b6b7-6a224a2b5cb9"):
        """Execute the actual lifecycle function and receipt reader under a real host lock."""
        source = PRODUCT.read_text()
        function = "verify_operational_execution() {" + source.split(
            "verify_operational_execution() {", 1)[1].split("\n}\n", 1)[0] + "\n}\n"
        script = ('set -euo pipefail\noperational_proof_directory="$TEST_CURRENT_INVOCATION"\n'
                  'operational_seed_run_id="$TEST_INITIAL_SEED"\n' + function
                  + '\nverify_operational_execution post-stockfish-\necho later-acceptance\n')
        with tempfile.TemporaryDirectory(prefix="post-stockfish-", dir=os.environ.get("TMPDIR", "/build/laplace/work")) as directory:
            root = Path(directory)
            proof_root = root / "operational-proof"
            current = proof_root / "34994682033-2"
            current.mkdir(parents=True)
            # A previous attempt must never supply this attempt's missing seed.
            previous = proof_root / "34994682033-1" / "invocation-prior"
            previous.mkdir(parents=True)
            previous.joinpath("seed.json").write_text(json.dumps(
                {"disposition": "verified", "run": {"run_id": "bf972cf5-0ca8-4cab-99e7-0b9b19482c34"}}))
            baseline = {}
            for index, value in enumerate(receipt_values):
                invocation = current / f"invocation-{index}"
                invocation.mkdir()
                invocation.joinpath("seed.json").write_text(json.dumps(value))
                for filename in ("task.json", "antonym-task.json"):
                    path = invocation / filename
                    path.write_text("earlier-execution-receipt\n")
                    baseline[path] = path.read_bytes()
            lock = root / "host-resource.lock"
            calls = root / "calls.jsonl"
            executable = root / "python3"
            executable.write_text("#!" + sys.executable + "\n" + """import fcntl, json, os, sys
from pathlib import Path
if sys.argv[1] == '-':
    os.execv(sys.executable, [sys.executable, *sys.argv[1:]])
if sys.argv[1] != 'scripts/verify-operational-task.py':
    raise SystemExit('unexpected process instead of the existing verifier')
with open(os.environ['TEST_HOST_LOCK'], 'a') as lock:
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        pass
    else:
        raise SystemExit('verifier ran without the shared host lock')
with Path(os.environ['PROOF_CALLS']).open('a') as stream:
    stream.write(json.dumps(sys.argv[1:]) + '\\n')
code = int(os.environ['ANTONYM_RC' if '--proof-mode' in sys.argv else 'DEFINITION_RC'])
Path(sys.argv[sys.argv.index('--receipt') + 1]).write_text(json.dumps({'exit': code}))
raise SystemExit(code)
""")
            executable.chmod(0o755)
            environment = {**os.environ, "PATH": str(root) + os.pathsep + os.environ["PATH"],
                           "GITHUB_RUN_ID": "34994682033", "GITHUB_RUN_ATTEMPT": "2",
                           "PROOF_CALLS": str(calls), "TEST_HOST_LOCK": str(lock),
                           "DEFINITION_RC": str(first_rc), "ANTONYM_RC": str(second_rc),
                           "LAPLACE_FRESH_DB": fresh, "LAPLACE_RESTORE_FOUNDATION": restore,
                           "TEST_CURRENT_INVOCATION": str(current / "invocation-0"), "TEST_INITIAL_SEED": initial_seed}
            result = subprocess.run(["flock", "--exclusive", "--close", str(lock), "bash"],
                                    input=script, cwd=ROOT, env=environment,
                                    text=True, capture_output=True, timeout=20)
            observed = [json.loads(line) for line in calls.read_text().splitlines()] if calls.exists() else []
            for path, payload in baseline.items():
                self.assertEqual(payload, path.read_bytes(), "post proof overwrote an earlier execution receipt")
            retained = {path.name: json.loads(path.read_text()) for path in current.glob("invocation-*/post-stockfish-*.json")}
            return result, observed, retained, str(current / "invocation-0")

    def test_post_stockfish_both_forms_reuse_exact_seed_and_retain_separate_receipts(self):
        run_id = "e56a93c8-36b4-46ef-b6b7-6a224a2b5cb9"
        result, calls, retained, directory = self.run_post_stockfish_proof(
            [{"disposition": "verified", "run": {"run_id": run_id}}])
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("later-acceptance\n", result.stdout)
        self.assertEqual([
            ["scripts/verify-operational-task.py", "--shape-file", "seeds/operational/tasks/en_define.json",
             "--seed-run-id", run_id, "--receipt", directory + "/post-stockfish-task.json"],
            ["scripts/verify-operational-task.py", "--proof-mode", "direct-relation", "--prompt", "The opposite of hot is",
             "--operand", "hot", "--shape-file", "seeds/operational/tasks/en_antonym.json",
             "--exemplar-file", "seeds/operational/exemplars/en_antonym.conllu", "--seed-run-id", run_id,
             "--receipt", directory + "/post-stockfish-antonym-task.json"],
        ], calls)
        self.assertEqual({"post-stockfish-task.json": {"exit": 0}, "post-stockfish-antonym-task.json": {"exit": 0}}, retained)

    def test_post_stockfish_either_failure_stops_later_acceptance_and_retains_failure(self):
        seed = {"disposition": "verified", "run": {"run_id": "e56a93c8-36b4-46ef-b6b7-6a224a2b5cb9"}}
        for first, second in ((41, 0), (0, 42)):
            with self.subTest(first=first, second=second):
                result, calls, retained, _ = self.run_post_stockfish_proof([seed], first, second)
                self.assertEqual(first or second, result.returncode, result.stderr)
                self.assertEqual("", result.stdout)
                self.assertEqual(1 if first else 2, len(calls))
                self.assertEqual({"exit": first}, retained["post-stockfish-task.json"])
                if first:
                    self.assertNotIn("post-stockfish-antonym-task.json", retained)
                else:
                    self.assertEqual({"exit": second}, retained["post-stockfish-antonym-task.json"])

    def test_post_stockfish_rejects_missing_or_unverified_seed_before_execution(self):
        seed = {"disposition": "verified", "run": {"run_id": "e56a93c8-36b4-46ef-b6b7-6a224a2b5cb9"}}
        for name, values in (
            ("missing-current-attempt", []),
            ("unverified", [{**seed, "disposition": "failed"}]),
            ("invalid-uuid", [{**seed, "run": {"run_id": "not-a-run-uuid"}}]),
            ("noncanonical-uuid", [{**seed, "run": {"run_id": seed["run"]["run_id"].upper()}}]),
            ("oversized", [{**seed, "extra": "x" * 1048576}]),
        ):
            with self.subTest(name=name):
                result, calls, retained, _ = self.run_post_stockfish_proof(values)
                self.assertNotEqual(0, result.returncode, result.stderr)
                self.assertEqual("", result.stdout)
                self.assertEqual([], calls)
                self.assertEqual({}, retained)

    def test_post_stockfish_uses_exact_in_process_invocation_and_unchanged_seed(self):
        seed = {"disposition": "verified", "run": {"run_id": "e56a93c8-36b4-46ef-b6b7-6a224a2b5cb9"}}
        unrelated = {"disposition": "failed", "run": {"run_id": "not-this-invocation"}}
        result, calls, _, _ = self.run_post_stockfish_proof([seed, unrelated])
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(2, len(calls))
        result, calls, _, _ = self.run_post_stockfish_proof(
            [seed], initial_seed="bf972cf5-0ca8-4cab-99e7-0b9b19482c34")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("differs from this process", result.stderr)
        self.assertEqual([], calls)

    def test_post_stockfish_preserves_only_the_explicit_fresh_unrestored_exception(self):
        result, calls, retained, _ = self.run_post_stockfish_proof([], fresh="1")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("fresh DB intentionally left unseeded", result.stdout)
        self.assertEqual([], calls)
        self.assertEqual({}, retained)
        # Foundation restoration re-enables the exact seed and both proofs.
        result, calls, _, _ = self.run_post_stockfish_proof([], fresh="1", restore="1")
        self.assertNotEqual(0, result.returncode, result.stderr)
        self.assertEqual([], calls)
        seed = {"disposition": "verified", "run": {"run_id": "e56a93c8-36b4-46ef-b6b7-6a224a2b5cb9"}}
        result, calls, _, _ = self.run_post_stockfish_proof([seed], fresh="1", restore="1")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(2, len(calls))

    def test_main_has_one_product_job_and_one_mutation_authority(self):
        workflow = load(MAIN)
        self.assertEqual("laplace-substrate-lifecycle", workflow["concurrency"]["group"])
        self.assertEqual(["product", "chess_environment"], list(workflow["jobs"]))
        calibration = workflow["jobs"]["chess_environment"]
        self.assertEqual("product", calibration["needs"])
        self.assertEqual("./.github/workflows/benchmark-evidence.yml", calibration["uses"])
        self.assertIn("needs.product.outputs", calibration["if"])
        self.assertEqual("chess", calibration["with"]["suite"])
        product = workflow["jobs"]["product"]
        command = commands(product)
        self.assertIn('bash scripts/product-ci.sh "$LAPLACE_STAGE"', command)
        self.assertIn("bash scripts/product-ci.sh reconcile", command)
        for split_job in ("deploy", "db-ops", "publish", "restore-api", "smoke", "integration-test"):
            self.assertNotIn(split_job, workflow["jobs"])

    def run_lifecycle_order(self, stage="all", failed="", *, wrong_source=False, skip_post=False):
        """Run the real stage selection and completion publisher with boundary stubs."""
        source = PRODUCT.read_text()
        function = "record_chess_completion() {" + source.split(
            "record_chess_completion() {", 1)[1].split("\n}\n", 1)[0] + "\n}\n"
        # Keep the actual initialization, including rejection of inherited ready state.
        initialization = source.split('stage="${1:-all}"', 1)[0].split(
            "# Completion state belongs", 1)[1]
        initialization = "# Completion state belongs" + initialization
        footer = "run_policy\n" + source.split("\nrun_policy\n", 1)[1]
        stubs = """set -euo pipefail
step() { echo "$1"; [[ "$1" != "$TEST_FAIL" ]]; }
git() { printf '%040d\n' 1; }
run_policy() { step policy; }
resume_held_repair_if_needed() { step resume; }
run_deps() { step deps; }
run_build() { step build; }
run_dev() { step dev; }
run_install_and_db() { step install; }
restore_foundation_if_requested() { step restore; }
seed_operational_memory() { step seed; }
run_publish() { step publish; }
observe_chess_runtime() { step chess-runtime; }
recover_publish() { step recover; }
bash() { step "application-$2"; }
run_repair_installed_corpus() { step repair; }
verify_operational_execution() {
  if [[ "${1:-}" == post-stockfish- ]]; then
    step postcheck
    if [[ "$TEST_SKIP_POST" != 1 ]]; then operational_postchecks_passed=1; fi
  else
    step initial
  fi
}
run_stockfish_corpus_acceptance() { step corpus; stockfish_corpus_passed=1; }
run_recorded_chess_benchmark() { step recorded-games; }
run_integration() { step integration; }
run_live_if_expected() { step live; }
run_perf() { step perf; }
"""
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "github-output"
            environment = {**os.environ, "GITHUB_OUTPUT": str(output), "GITHUB_RUN_ID": "35001917982",
                "GITHUB_RUN_ATTEMPT": "3", "GITHUB_SHA": ("2" if wrong_source else "1").zfill(40),
                "TEST_FAIL": failed, "TEST_SKIP_POST": "1" if skip_post else "0", "LAPLACE_FRESH_DB": "0",
                "LAPLACE_FULL_CLEAN": "0", "stockfish_corpus_passed": "1", "operational_postchecks_passed": "1"}
            result = subprocess.run(["bash", "-s", stage], input=stubs + initialization
                + 'stage="${1:-all}"\n' + function + footer, env=environment, text=True,
                capture_output=True, timeout=20)
            outputs = dict(line.split("=", 1) for line in output.read_text().splitlines()) if output.exists() else {}
            return result, result.stdout.splitlines(), outputs

    def test_later_failure_preserves_current_job_corpus_completion_and_failed_lifecycle(self):
        for failed in ("recorded-games", "integration", "live", "perf"):
            with self.subTest(failed=failed):
                result, calls, outputs = self.run_lifecycle_order(failed=failed)
                self.assertNotEqual(0, result.returncode, result.stderr)
                self.assertEqual(failed, calls[-1])
                self.assertLess(calls.index("repair"), calls.index("initial"))
                self.assertLess(calls.index("publish"), calls.index("chess-runtime"))
                self.assertLess(calls.index("chess-runtime"), calls.index("repair"))
                self.assertLess(calls.index("initial"), calls.index("corpus"))
                self.assertLess(calls.index("corpus"), calls.index("postcheck"))
                self.assertLess(calls.index("postcheck"), calls.index(failed))
                self.assertEqual({"chess_benchmark_ready": "true", "activated_ref": "1".zfill(40),
                                  "chess_acceptance_stage": "all"}, outputs)

    def test_prerequisite_failure_never_authorizes_measurement_or_later_tests(self):
        for failed in ("publish", "repair", "initial", "corpus", "postcheck"):
            with self.subTest(failed=failed):
                result, calls, outputs = self.run_lifecycle_order(failed=failed)
                self.assertNotEqual(0, result.returncode, result.stderr)
                self.assertEqual({}, outputs)
                self.assertNotIn("integration", calls)
                self.assertNotIn("live", calls)

    def test_application_only_executes_its_explicit_prerequisites_without_claiming_repair(self):
        result, calls, outputs = self.run_lifecycle_order(stage="applications")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["policy", "resume", "deps", "build", "dev", "application-check",
                          "application-deploy", "recover", "chess-runtime", "seed", "initial", "corpus", "postcheck", "recorded-games"], calls)
        self.assertEqual("applications", outputs["chess_acceptance_stage"])
        for failed in ("application-deploy", "seed", "initial", "corpus", "postcheck"):
            with self.subTest(failed=failed):
                result, _, outputs = self.run_lifecycle_order(stage="applications", failed=failed)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual({}, outputs)

    def test_completion_rejects_wrong_source_and_cannot_inherit_a_skipped_postcheck(self):
        result, _, outputs = self.run_lifecycle_order(wrong_source=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("differs from this workflow revision", result.stderr)
        self.assertEqual({}, outputs)
        result, _, outputs = self.run_lifecycle_order(skip_post=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual({}, outputs)
        for stage in ("deploy", "integrate", "application-check"):
            with self.subTest(stage=stage):
                result, calls, outputs = self.run_lifecycle_order(stage=stage)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual({}, outputs)
                self.assertNotIn("corpus", calls)

    def test_product_script_owns_order_once(self):
        text = PRODUCT.read_text(encoding="utf-8")
        ordered = [
            "run_policy",
            "run_deps",
            "run_build",
            "run_dev",
            "run_install_and_db",
            "seed_operational_memory",
            "run_publish",
            "observe_chess_runtime",
            "run_repair_installed_corpus",
            "verify_operational_execution",
            "run_stockfish_corpus_acceptance",
            "verify_operational_execution post-stockfish-",
            "record_chess_completion",
            "run_recorded_chess_benchmark",
            "run_integration",
            "run_live_if_expected",
        ]
        positions = [text.rfind(f"\n{name}\n") for name in ordered]
        self.assertTrue(all(pos >= 0 for pos in positions), positions)
        self.assertEqual(sorted(positions), positions)
        self.assertIn('bash scripts/check-database-health.sh', text)
        self.assertIn('bash scripts/ensure-foundation.sh --check-only', text)
        self.assertIn('bash scripts/ensure-foundation.sh', text)
        self.assertIn('bash scripts/publish-applications.sh deploy', text)
        self.assertIn('bash scripts/publish-applications.sh recover', text)

    def test_tooling_only_main_changes_reconcile_without_native_rebuild_or_seed(self):
        source = MAIN.read_text(encoding="utf-8")
        self.assertIn("LAPLACE_FAST_ONLY", source)
        self.assertIn("scripts/check-*", source)
        self.assertIn("bash scripts/product-ci.sh reconcile", source)
        product = PRODUCT.read_text(encoding="utf-8")
        reconcile = product.split("reconcile_installed_product() {", 1)[1].split("\n}", 1)[0]
        self.assertIn("check-database-health.sh", reconcile)
        self.assertIn("verify-application-release.py", reconcile)
        self.assertNotIn("ensure_product_foundation", reconcile)
        self.assertNotIn("ensure-foundation.sh", reconcile)
        self.assertNotIn("pipeline.sh", reconcile)

    def test_actual_classifier_treats_selected_docs_as_product_inputs(self):
        workflow = load(MAIN)
        classifier = next(step["run"] for step in workflow["jobs"]["product"]["steps"]
                          if step.get("name") == "Classify source-only change")
        # Execute the checked-in classifier with only git's changed-file response
        # controlled. Selection uses the real CLI/project inventory and no build.
        git = '''git() {
  if [[ "$1" == diff ]]; then printf '%s\\n' "$TEST_CHANGED_PATH"; fi
}
'''
        with tempfile.TemporaryDirectory() as directory:
            envfile = Path(directory) / "github-env"
            for path, fast in (("docs/INVENTION.md", "0"),
                               ("docs/specs/37_Substrate_Operation_ISA.md", "0"),
                               (".github/workflows/laplace.yml", "0"),
                               ("scripts/product-ci.sh", "0"),
                               ("scripts/test-profile-registry.py", "0"),
                               ("scripts/test-parallel.sh", "0"),
                               ("scripts/ingest-stockfish-corpus.py", "0"),
                               ("scripts/test-actions-topology.py", "1"),
                               ("docs/INVENTORY.md", "1"),
                               ("docs/INVENTION.md.notes", "1")):
                envfile.write_text("")
                environment = dict(os.environ, BEFORE_SHA="base", TARGET_SHA="head",
                                   TEST_CHANGED_PATH=path, GITHUB_ENV=str(envfile))
                result = subprocess.run(["bash"], input=git + classifier, cwd=ROOT,
                                        env=environment, text=True, capture_output=True, timeout=20)
                with self.subTest(path=path):
                    self.assertEqual(0, result.returncode, result.stderr)
                    self.assertIn("LAPLACE_FAST_ONLY=" + fast, envfile.read_text().splitlines())

    def test_classifier_cannot_treat_failed_source_inventory_as_fast_success(self):
        classifier = next(step["run"] for step in load(MAIN)["jobs"]["product"]["steps"]
                          if step.get("name") == "Classify source-only change")
        with tempfile.TemporaryDirectory() as directory:
            envfile = Path(directory) / "github-env"
            environment = dict(os.environ, BEFORE_SHA="base", TARGET_SHA="head", GITHUB_ENV=str(envfile))
            # Assignment must retain the producer's failure before the diff loop;
            # process substitution would let an empty selection escape as success.
            result = subprocess.run(["bash"], input="python3() { return 43; }\n" + classifier,
                                    cwd=ROOT, env=environment, text=True, capture_output=True, timeout=20)
            self.assertEqual(43, result.returncode, result.stderr)
            self.assertFalse(envfile.exists())

    def test_foundation_restore_is_explicit_in_main_lifecycle(self):
        workflow = load(MAIN)
        restore = workflow["on"]["workflow_dispatch"]["inputs"]["restore_foundation"]
        self.assertEqual("false", restore["default"])
        self.assertIn("LAPLACE_RESTORE_FOUNDATION", MAIN.read_text(encoding="utf-8"))
        product = PRODUCT.read_text(encoding="utf-8")
        self.assertIn('if [[ "${LAPLACE_RESTORE_FOUNDATION:-}" == 1 ]]', product)
        self.assertIn("foundation restore not requested — no ingest", product)

    def test_pr_proof_is_proportional_and_nonmutating(self):
        workflow = load(PR)
        prove = workflow["jobs"]["prove"]
        command = commands(prove)
        self.assertIn("LAPLACE_PR_FULL_PROOF", command)
        self.assertIn("test-parallel.sh --policy", command)
        self.assertIn("scripts/pr-proof.sh", command)
        self.assertIn("git worktree add --detach", command)
        self.assertIn("git worktree remove --force", command)
        for forbidden in ("pipeline.sh install", "pipeline.sh migrate", "publish-applications.sh deploy", "sudo "):
            self.assertNotIn(forbidden, command)
        self.assertEqual("true", workflow["concurrency"]["cancel-in-progress"])

    def test_private_database_proof_requires_each_source_and_session_case(self):
        source = (ROOT / "scripts/pr-db-proof.sh").read_text(encoding="utf-8")
        prefix = "Laplace.SubstrateCRUD.Tests."
        methods = [
            prefix + "OperationalSourceExecutionTests.AuthoredTaskSource_ExecutesNovelRequestAfterSharedAdmissionAndFold",
            prefix + "OperationalSourceExecutionTests.AuthoredTaskSource_BindsSynsetThroughTwoWitnessedNamingHops",
            prefix + "OperationalSourceExecutionTests.AuthoredAntonymExemplar_AdmitsCompleteSourceWithNativeParseProvenance",
            prefix + "OperationalSourceExecutionTests.AuthoredAntonymTask_ExecutesNovelRequestThroughAdmittedWordBinding",
            prefix + "NativeSqlBatchTests.ConversationWriterResumesProjectionWithoutForgingContent",
            prefix + "NativeSqlBatchTests.LegacySessionContentIsPreservedAndRequiresExplicitRecovery",
        ]
        expected_filter = "|".join("FullyQualifiedName=" + method for method in methods)
        self.assertIn("--filter '" + expected_filter + "'", source)
        self.assertIn('managed_results="$exemplar_results"', source)
        self.assertIn('"$managed_results/operational-source-execution.trx"',
                      source.split('rm -f ', 1)[1].split('PATH="$PG_PREFIX/bin:', 1)[0])
        validator = source.split('python3 - "$managed_results/operational-source-execution.trx" <<\'PY\'\n', 1)[1].split("\nPY\n", 1)[0]
        names = [methods[0], methods[1], methods[2], methods[3], methods[4] + "(batchPrefix: False)",
                 methods[4] + "(batchPrefix: True)", methods[5]]

        def receipt():
            root = ET.Element("TestRun", xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
            results = ET.SubElement(root, "Results")
            for name in names:
                ET.SubElement(results, "UnitTestResult", testName=name, outcome="Passed")
            summary = ET.SubElement(root, "ResultSummary")
            ET.SubElement(summary, "Counters", total="7", executed="7", passed="7",
                          failed="0", notExecuted="0")
            return root

        def check(root, passes):
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "acceptance.trx"
                ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)
                result = subprocess.run([sys.executable, "-", str(path)], input=validator,
                                        text=True, capture_output=True)
            self.assertEqual(result.returncode == 0, passes, result.stdout + result.stderr)

        check(receipt(), True)
        lower = receipt()
        for result in lower.find("Results"):
            result.set("testName", result.get("testName").replace("False", "false").replace("True", "true"))
        check(lower, True)
        for missing_name in names:
            with self.subTest(missing_name=missing_name):
                root = receipt()
                results = root.find("Results")
                results.remove(next(result for result in results if result.get("testName") == missing_name))
                check(root, False)
        for corruption in ("missing", "repeated-theory", "wrong-test", "skipped", "failed", "counter-only"):
            with self.subTest(corruption=corruption):
                root = receipt()
                results = root.find("Results")
                if corruption == "missing":
                    results.remove(results[6])
                elif corruption == "repeated-theory":
                    results[5].set("testName", names[4])
                elif corruption == "wrong-test":
                    results[6].set("testName", prefix + "UnrelatedPassingTest")
                elif corruption in ("skipped", "failed"):
                    results[2].set("outcome", "NotExecuted" if corruption == "skipped" else "Failed")
                else:
                    root.find("ResultSummary/Counters").set("executed", "6")
                check(root, False)

    def test_manual_db_mutation_shares_product_lifecycle_lock(self):
        db = load(WORKFLOWS / "db-ops.yml")
        self.assertEqual("laplace-substrate-lifecycle", db["concurrency"]["group"])
        trigger = db["on"]
        names = {trigger} if isinstance(trigger, str) else set(trigger)
        self.assertEqual({"workflow_dispatch"}, names)

    def test_database_recreate_does_not_seed_unless_explicitly_requested(self):
        path = WORKFLOWS / "db-ops.yml"
        db = load(path)
        inputs = db["on"]["workflow_dispatch"]["inputs"]
        self.assertIn("restore_foundation", inputs)
        self.assertEqual("false", inputs["restore_foundation"]["default"])
        steps = db["jobs"]["db"]["steps"]
        recreate = next(step for step in steps if step.get("name") == "Recreate database structure and runtime")
        restore = next(step for step in steps if step.get("name") == "Restore canonical foundation (explicit opt-in)")
        self.assertNotIn("ensure-foundation.sh", recreate["run"])
        self.assertIn("ensure-foundation.sh --force", restore["run"])
        self.assertEqual("inputs.operation == 'recreate' && inputs.restore_foundation", restore["if"])

    def test_seed_workflows_are_manual_or_reusable_not_source_triggered(self):
        for path in sorted(WORKFLOWS.glob("seed-*.yml")):
            trigger = load(path)["on"]
            names = {trigger} if isinstance(trigger, str) else set(trigger)
            self.assertFalse(names & {"push", "pull_request"}, path.name)
            self.assertTrue(names & {"workflow_dispatch", "workflow_call"}, path.name)

    def test_all_external_actions_are_commit_pinned(self):
        import re
        for path in WORKFLOWS.glob("*.yml"):
            for line in path.read_text(encoding="utf-8").splitlines():
                if "uses:" not in line or "./" in line:
                    continue
                use = line.split("uses:", 1)[1].strip()
                self.assertRegex(use, r"^[^@\s]+@[0-9a-f]{40}$", path.name)


class ActionsAuditFailurePropagationTests(unittest.TestCase):
    """Execute the real audit against mutated workflows, never a parallel model."""

    @classmethod
    def setUpClass(cls):
        cls.originals = {path.name: load(path) for path in WORKFLOWS.glob("*.yml")}
        cls.scratch = tempfile.TemporaryDirectory(prefix="actions-audit-", dir=os.environ.get("TMPDIR", "/build/laplace/work"))
        cls.root = Path(cls.scratch.name)
        (cls.root / ".github/workflows").mkdir(parents=True)
        (cls.root / "scripts").mkdir()
        for name in ("actions-audit.py", "product-ci.sh", "pr-proof.sh", "bootstrap-laplace-runner.sh", "test-profile-registry.py", "test-profiles.json", "test-parallel.sh", "ci-policy.sh", "maintain-installed-database.sh", "repair-legacy-content-lifecycle.sh"):
            shutil.copy2(ROOT / "scripts" / name, cls.root / "scripts" / name)

    @classmethod
    def tearDownClass(cls):
        cls.scratch.cleanup()

    def check_audit(self, mutate=None, diagnostic=None):
        workflows = copy.deepcopy(self.originals)
        if mutate:
            mutate(workflows)
        for name, workflow in workflows.items():
            (self.root / ".github/workflows" / name).write_text(yaml.safe_dump(workflow, sort_keys=False), encoding="utf-8")
        result = subprocess.run([sys.executable, str(self.root / "scripts/actions-audit.py")], capture_output=True, text=True, timeout=20)
        if diagnostic:
            self.assertEqual(1, result.returncode, result.stdout + result.stderr)
            self.assertIn(diagnostic, result.stderr)
        else:
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("ACTIONS_AUDIT_OK", result.stdout)

    @staticmethod
    def step(workflows, filename, key, value):
        job = next(iter(workflows[filename]["jobs"].values()))
        return next(step for step in job["steps"] if step.get(key) == value)

    def test_deferred_readiness_and_optional_baseline_are_accepted(self):
        self.check_audit()

    def test_database_maintenance_cannot_bypass_managed_quiescence(self):
        path = self.root / "scripts/product-ci.sh"
        original = path.read_text()
        try:
            path.write_text(original.replace("python3 scripts/quiesce-managed-database.py", "python3 scripts/other.py"))
            self.check_audit(diagnostic="one managed-quiescence owner")
        finally:
            path.write_text(original)

    def test_database_maintenance_order_and_failure_propagation_are_required(self):
        path = self.root / "scripts/maintain-installed-database.sh"
        original = path.read_text()
        reconcile = 'bash scripts/reconcile-highway-masks.sh "${PGDATABASE:-laplace}"'
        migrate = 'bash scripts/pipeline.sh "${args[@]}" migrate sync-extension tune-pg tune-laplace perfcache-guc api-env'
        for mutation in (original.replace(migrate, ""), original.replace(reconcile, reconcile + " || true"),
                         original.replace(migrate, "MIGRATION_PLACEHOLDER").replace(reconcile, migrate).replace("MIGRATION_PLACEHOLDER", reconcile)):
            try:
                path.write_text(mutation)
                self.check_audit(diagnostic="maintenance sequence must migrate, reconcile, and verify once")
            finally:
                path.write_text(original)

    def test_corpus_repair_requires_successful_publication_and_its_own_quiescence(self):
        path = self.root / "scripts/product-ci.sh"
        original = path.read_text()
        repair = 'bash scripts/repair-legacy-content-lifecycle.sh "${PGDATABASE:-laplace}"'
        mutations = (
            (original.replace("\nrun_publish\n", "\nREPAIR_ORDER_PLACEHOLDER\n").replace("\nrun_repair_installed_corpus\n", "\nrun_publish\n").replace("\nREPAIR_ORDER_PLACEHOLDER\n", "\nrun_repair_installed_corpus\n"), "product lifecycle order drifted"),
            (original.replace("\nrun_repair_installed_corpus\n", "\nrun_repair_installed_corpus || true\n"), "one unsuppressed lifecycle invocation"),
            (original.replace(repair, repair + " || true"), "post-publication corpus repair"),
            (original.replace('LAPLACE_REPAIR_PUBLISHED_SOURCE="$(git rev-parse HEAD)"', 'LAPLACE_REPAIR_PUBLISHED_SOURCE="unknown"'), "post-publication corpus repair"),
            (original.replace('--max-current-readback-bytes 8589934592', ''), "post-publication corpus repair"),
        )
        for mutation, diagnostic in mutations:
            with self.subTest(diagnostic=diagnostic):
                try:
                    self.assertNotEqual(original, mutation)
                    path.write_text(mutation)
                    self.check_audit(diagnostic=diagnostic)
                finally:
                    path.write_text(original)

    def test_operational_proof_cannot_precede_repair_or_have_failure_suppressed(self):
        path = self.root / "scripts/product-ci.sh"
        original = path.read_text()
        invocation = "\nverify_operational_execution\n"
        mutations = (
            original.replace(invocation, ""),
            original.replace(invocation, "\nverify_operational_execution || true\n"),
            original.replace(invocation, "").replace("\nrun_publish\n", invocation + "run_publish\n"),
        )
        for mutation in mutations:
            try:
                self.assertNotEqual(original, mutation)
                path.write_text(mutation)
                self.check_audit(diagnostic="product lifecycle order drifted")
            finally:
                path.write_text(original)

    def test_failed_operational_proof_stops_later_product_acceptance(self):
        source = PRODUCT.read_text()
        footer = "run_repair_installed_corpus\n" + source.rsplit("\nrun_repair_installed_corpus\n", 1)[1]
        script = """set -euo pipefail
run_repair_installed_corpus() { echo repair-complete; }
verify_operational_execution() { echo operational-proof-rejected; return 41; }
run_integration() { echo unexpected-integration; }
run_live_if_expected() { echo unexpected-live; }
run_perf() { echo unexpected-perf; }
""" + footer
        result = subprocess.run(["bash"], input=script, text=True, capture_output=True)
        self.assertEqual(41, result.returncode, result.stderr)
        self.assertEqual(["repair-complete", "operational-proof-rejected"], result.stdout.splitlines())

    def test_both_operational_forms_require_the_same_seed_and_independent_passing_proofs(self):
        source = PRODUCT.read_text()
        function = "verify_operational_execution() {" + source.split(
            "verify_operational_execution() {", 1)[1].split("\n}\n", 1)[0] + "\n}\n"
        run_id = "e56a93c8-36b4-46ef-b6b7-6a224a2b5cb9"
        shim = "#!" + sys.executable + "\n" + """import json, os, sys
from pathlib import Path
if sys.argv[1] == '-':
    os.execv(sys.executable, [sys.executable, *sys.argv[1:]])
with Path(os.environ['PROOF_CALLS']).open('a') as stream:
    stream.write(json.dumps(sys.argv[1:]) + '\\n')
mode = 'ANTONYM_RC' if '--proof-mode' in sys.argv else 'DEFINITION_RC'
raise SystemExit(int(os.environ[mode]))
"""
        for first_rc, second_rc in ((0, 0), (41, 0), (0, 42)):
            with self.subTest(definition=first_rc, antonym=second_rc), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                (root / "seed.json").write_text(json.dumps(
                    {"disposition": "verified", "run": {"run_id": run_id}}))
                executable = root / "python3"
                executable.write_text(shim)
                executable.chmod(0o755)
                calls_path = root / "calls.jsonl"
                environment = {**os.environ, "PATH": str(root) + os.pathsep + os.environ["PATH"],
                    "PROOF_CALLS": str(calls_path), "TEST_PROOF_DIR": str(root),
                    "DEFINITION_RC": str(first_rc), "ANTONYM_RC": str(second_rc),
                    "LAPLACE_FRESH_DB": "0", "LAPLACE_RESTORE_FOUNDATION": "0"}
                script = 'set -euo pipefail\noperational_proof_directory="$TEST_PROOF_DIR"\n' \
                    + function + '\nverify_operational_execution\necho later-acceptance\n'
                result = subprocess.run(["bash"], input=script, env=environment,
                                        text=True, capture_output=True)
                self.assertEqual(first_rc or second_rc, result.returncode, result.stderr)
                self.assertEqual("later-acceptance\n" if not (first_rc or second_rc) else "", result.stdout)
                calls = [json.loads(line) for line in calls_path.read_text().splitlines()]
                self.assertEqual(1 if first_rc else 2, len(calls))
                for call in calls:
                    self.assertEqual("scripts/verify-operational-task.py", call[0])
                    self.assertEqual(run_id, call[call.index("--seed-run-id") + 1])
                self.assertNotIn("--proof-mode", calls[0])
                self.assertEqual("seeds/operational/tasks/en_define.json",
                                 calls[0][calls[0].index("--shape-file") + 1])
                self.assertEqual(str(root / "task.json"), calls[0][calls[0].index("--receipt") + 1])
                if len(calls) == 2:
                    direct = calls[1]
                    for flag, expected in (("--proof-mode", "direct-relation"),
                        ("--prompt", "The opposite of hot is"), ("--operand", "hot"),
                        ("--shape-file", "seeds/operational/tasks/en_antonym.json"),
                        ("--exemplar-file", "seeds/operational/exemplars/en_antonym.conllu"),
                        ("--receipt", str(root / "antonym-task.json"))):
                        self.assertEqual(expected, direct[direct.index(flag) + 1])

    def test_owned_repair_resume_cannot_be_omitted_swallowed_or_moved_after_build(self):
        path=self.root / "scripts/product-ci.sh"
        original=path.read_text()
        call="  reconcile|deploy|integrate|all|applications) resume_held_repair_if_needed ;;"
        block='case "$stage" in\n'+call+'\nesac\n'
        for mutation in (original.replace("--resume-if-needed", "--wrong-resume-mode"),
                         original.replace(call,call.replace(" ;;"," || true ;;")),
                         original.replace(block,"").replace("\nrun_build\n","\nrun_build\n"+block)):
            try:
                self.assertNotEqual(original,mutation)
                path.write_text(mutation)
                self.check_audit(diagnostic="owned repair resume must run unsuppressed")
            finally:
                    path.write_text(original)

    def test_publication_recovery_cannot_restart_api_after_unknown_repair_transaction(self):
        source = PRODUCT.read_text()
        footer = "trap recover_publish EXIT\nrun_publish\n" + source.rsplit(
            "trap recover_publish EXIT\nrun_publish\n", 1)[1]
        recovery = "recover_publish() {" + source.split("recover_publish() {", 1)[1].split("\n}\n", 1)[0] + "\n}\n"
        script = """set -euo pipefail
bash() { echo publication-recovery; }
ensure_api_running() { echo API-START; }
run_publish() { echo published; }
observe_chess_runtime() { echo runtime-observed; }
run_repair_installed_corpus() { echo repair-transaction-unknown; return 37; }
run_integration() { echo unexpected-integration; }
run_live_if_expected() { echo unexpected-live; }
run_perf() { echo unexpected-perf; }
""" + recovery + footer
        result = subprocess.run(["bash", "-c", script], text=True, capture_output=True, timeout=10)
        self.assertEqual(37, result.returncode, result.stdout + result.stderr)
        self.assertEqual(["published", "runtime-observed", "repair-transaction-unknown"], result.stdout.splitlines())
        path = self.root / "scripts/product-ci.sh"
        original = path.read_text()
        try:
            path.write_text(original.replace("\nrun_publish\ntrap - EXIT\n", "\nrun_publish\n"))
            self.check_audit(diagnostic="publication recovery must end before repair")
        finally:
            path.write_text(original)

    def test_database_repair_cannot_bypass_measurement_lane(self):
        path = self.root / "scripts/repair-legacy-content-lifecycle.sh"
        original = path.read_text()
        try:
            path.write_text(original.replace("bash scripts/measure-lane.sh --", "bash scripts/unlocked.sh --"))
            self.check_audit(diagnostic="authoritative measurement lane")
        finally:
            path.write_text(original)

    def test_failed_proof_cannot_be_hidden_at_step_or_job(self):
        mutations = [
            lambda ws: self.step(ws, "pr-validation.yml", "id", "proof").update({"continue-on-error": "true"}),
            lambda ws: ws["pr-validation.yml"]["jobs"]["prove"].update({"continue-on-error": "${{ true }}"}),
            lambda ws: self.step(ws, "laplace.yml", "name", "Run full product lifecycle").update({"continue-on-error": "True"}),
            lambda ws: self.step(ws, "benchmark-evidence.yml", "name", "Upload complete benchmark evidence").update({"continue-on-error": "true"}),
        ]
        for index, mutation in enumerate(mutations):
            with self.subTest(index=index):
                self.check_audit(mutation, "hidden red")

    def test_readiness_uses_raw_outcome_and_is_not_swallowed(self):
        def set_gate(ws, **changes):
            self.step(ws, "benchmark-evidence.yml", "name", "Require installed chess tools and verified release checks").update(changes)
        mutations = [
            (lambda ws: set_gate(ws, **{"if": "success()"}), "raw failed outcome"),
            (lambda ws: set_gate(ws, **{"if": "${{ !cancelled() && inputs.suite == 'chess' && steps.chess_readiness.conclusion == 'failure' }}"}), "raw failed outcome"),
            (lambda ws: set_gate(ws, run="echo failed\nexit 0\n"), "fail unconditionally"),
            (lambda ws: set_gate(ws, **{"continue-on-error": "true"}), "hidden red"),
            (lambda ws: self.step(ws, "benchmark-evidence.yml", "id", "chess_readiness").update({"if": "false"}), "every attempted chess path"),
        ]
        for index, (mutation, diagnostic) in enumerate(mutations):
            with self.subTest(index=index):
                self.check_audit(mutation, diagnostic)

    def test_readiness_gate_must_follow_success_independent_upload(self):
        def move_gate(ws):
            steps = ws["benchmark-evidence.yml"]["jobs"]["benchmark"]["steps"]
            gate = steps.pop()
            steps.insert(0, gate)
        self.check_audit(move_gate, "enforced after evidence upload")
        self.check_audit(lambda ws: self.step(ws, "benchmark-evidence.yml", "name", "Upload complete benchmark evidence").update({"if": "success()"}), "upload on failure")
        self.check_audit(lambda ws: self.step(ws, "benchmark-evidence.yml", "name", "Upload complete benchmark evidence")["with"].update({"if-no-files-found": "warn"}), "missing benchmark evidence must fail")

    def test_baseline_cannot_acquire_or_gate_real_proof(self):
        def move_proof_into_baseline(ws):
            self.step(ws, "pr-validation.yml", "id", "baseline_diagnostic")["run"] += "\nbash scripts/pr-proof.sh\n"
        self.check_audit(move_proof_into_baseline, "authoritative or mutating operation")
        self.check_audit(lambda ws: self.step(ws, "pr-validation.yml", "id", "proof").update({"if": "steps.baseline_diagnostic.outcome == 'success'"}), "independent of optional baseline")
        self.check_audit(lambda ws: self.step(ws, "pr-validation.yml", "id", "baseline_diagnostic").update({"if": "always()"}), "limited to full proof scope")
        self.check_audit(lambda ws: self.step(ws, "pr-validation.yml", "name", "Upload retained baseline before full proof").update({"run": "bash scripts/pr-proof.sh"}), "only retain the attempted diagnostic")

    def test_optional_readiness_cannot_swallow_authoritative_proof(self):
        def hide_proof(ws):
            self.step(ws, "benchmark-evidence.yml", "id", "chess_readiness")["run"] += "\nbash scripts/pr-proof.sh\n"
        self.check_audit(hide_proof, "authoritative or mutating operation")

    def test_reusable_measurement_cannot_gain_a_deployment_job(self):
        self.check_audit(lambda ws: ws["benchmark-evidence.yml"]["jobs"].update({"deploy": {"runs-on": "self-hosted", "timeout-minutes": "30", "steps": [{"run": "bash scripts/pipeline.sh install"}]}}), "only its measurement job")
        self.check_audit(lambda ws: ws["benchmark-evidence.yml"]["jobs"]["benchmark"]["steps"].insert(0, {"run": "bash scripts/pipeline.sh install"}), "product mutation authority")

    def test_duplicate_optional_step_cannot_mask_another_failure(self):
        def duplicate(ws):
            steps = ws["pr-validation.yml"]["jobs"]["prove"]["steps"]
            steps.append(copy.deepcopy(self.step(ws, "pr-validation.yml", "id", "baseline_diagnostic")))
        self.check_audit(duplicate, "requires exactly one id=baseline_diagnostic")

    def test_no_extra_product_mutation_job(self):
        self.check_audit(lambda ws: ws["laplace.yml"]["jobs"].update({"deploy": {"runs-on": "self-hosted", "timeout-minutes": "30", "steps": [{"run": "bash scripts/pipeline.sh install"}]}}), "one product job and its chess measurement")
        self.check_audit(lambda ws: ws["laplace.yml"]["jobs"]["chess_environment"].update({"steps": [{"run": "bash scripts/pipeline.sh install"}]}), "may only call the bounded reusable workflow")

    def test_measurement_requires_successful_activation_and_exact_source(self):
        mutations = [
            (lambda ws: ws["laplace.yml"]["jobs"]["chess_environment"].update({"if": "always()"}), "requires current-job completed corpus acceptance"),
            (lambda ws: ws["laplace.yml"]["jobs"]["chess_environment"].update({"needs": []}), "single product authority"),
            (lambda ws: ws["laplace.yml"]["jobs"]["chess_environment"]["with"].update({"target_ref": "main"}), "activated source"),
            (lambda ws: ws["laplace.yml"]["jobs"]["chess_environment"]["with"].update({"suite": "all"}), "activated source"),
            (lambda ws: ws["laplace.yml"]["jobs"]["product"]["outputs"].update({"chess_benchmark_ready": "true"}), "current lifecycle completion"),
            (lambda ws: self.step(ws, "laplace.yml", "id", "full_product").update({"id": "other"}), "lifecycle must own its completion outputs"),
        ]
        for index, (mutation, diagnostic) in enumerate(mutations):
            with self.subTest(index=index):
                self.check_audit(mutation, diagnostic)

    def test_native_only_deploy_cannot_authorize_installed_corpus_or_measurement(self):
        old_gate = "env.LAPLACE_FAST_ONLY != '1' && (env.LAPLACE_STAGE == 'all' || env.LAPLACE_STAGE == 'deploy' || env.LAPLACE_STAGE == 'applications')"
        for key, name, condition, diagnostic in (
            ("name", "Retain official Stockfish corpus admission evidence", "always() && " + old_gate, "application-publishing stage"),
        ):
            with self.subTest(name=name):
                self.check_audit(lambda ws: self.step(ws, "laplace.yml", key, name).update({"if": condition}), diagnostic)

    def test_corpus_acceptance_and_recorded_measurement_cannot_move_after_independent_tests(self):
        path = self.root / "scripts/product-ci.sh"
        original = path.read_text()
        for command in ("run_stockfish_corpus_acceptance", "verify_operational_execution post-stockfish-",
                        "record_chess_completion", "run_recorded_chess_benchmark"):
            invocation = "\n" + command + "\n"
            for mutation in (original.replace(invocation, "\n" + command + " || true\n"),
                             original.replace(invocation, "\n").replace("\nrun_integration\n", "\nrun_integration\n" + command + "\n")):
                with self.subTest(command=command):
                    try:
                        self.assertNotEqual(original, mutation)
                        path.write_text(mutation)
                        self.check_audit(diagnostic="product lifecycle order drifted")
                    finally:
                        path.write_text(original)

    def test_corpus_completion_cannot_be_reintroduced_as_an_unconditional_workflow_step(self):
        self.check_audit(lambda ws: ws["laplace.yml"]["jobs"]["product"]["steps"].append(
            {"run": "printf 'ready=true\\n' >> \"$GITHUB_OUTPUT\""}),
            "completion must stay inside the product lifecycle")
        self.check_audit(lambda ws: self.step(ws, "laplace.yml", "name", "Retain installed recorded-game benchmark evidence").update(
            {"if": "success()"}), "recorded-game failure evidence must upload")


if __name__ == "__main__":
    unittest.main(verbosity=2)
