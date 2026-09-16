#!/usr/bin/env python3
"""Executable contracts for delivery, independently dispatched ingestion and measurement."""
from __future__ import annotations

from pathlib import Path
import copy
import fcntl
import hashlib
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
    def test_recursive_receipt_retention_does_not_wait_for_host_or_query_history(self):
        """Run all three real workflow blocks while another job owns the host."""
        blocks = []
        for path, job in ((MAIN, "product"), (PR, "prove")):
            blocks.extend(step["run"] for step in load(path)["jobs"][job]["steps"]
                          if "scripts/collect-recursive-proof-evidence.py" in step.get("run", ""))
        self.assertEqual(3, len(blocks))
        with tempfile.TemporaryDirectory(prefix="retained-proof-", dir=os.environ.get("TMPDIR", "/build/laplace/work")) as directory:
            root = Path(directory)
            checkout = root / "checkout"
            scripts = checkout / "scripts"
            scripts.mkdir(parents=True)
            shutil.copyfile(ROOT / "scripts/collect-recursive-proof-evidence.py", scripts / "collect-recursive-proof-evidence.py")
            forbidden = root / "unexpected-live-query"
            (scripts / "inspect-recursive-proof-counterexamples.py").write_text(
                "from pathlib import Path\nPath(" + repr(str(forbidden)) + ").touch()\nraise SystemExit(91)\n")
            old = checkout / "build/test-receipts/live-recursive-substrate.json"
            old.parent.mkdir(parents=True)
            old.write_text(json.dumps({"schema": "laplace.proof.live-recursive-substrate/v1",
                                      "source_sha": "previous-source", "ok": False, "finished_unix_nanoseconds": 1,
                                      "identity": {"failure_examples": [{"parent_id": "a" * 32}]}}))
            current_build = root / "current-build"
            current = current_build / "test-receipts/live-recursive-substrate.json"
            current.parent.mkdir(parents=True)
            current.write_text(json.dumps({"schema": "laplace.proof.live-recursive-substrate/v1",
                                          "source_sha": "current-source", "ok": True, "finished_unix_nanoseconds": 2}))
            environment = {**os.environ, "GITHUB_WORKSPACE": str(checkout), "LAPLACE_PR_WORKTREE": str(checkout),
                           "LAPLACE_BUILD_DIRECTORY": str(current_build), "GITHUB_RUN_ID": "1234", "GITHUB_RUN_ATTEMPT": "2",
                           "LAPLACE_PR_TARGET_SHA": "current-source", "TARGET_SHA": "current-source", "PROOF_OUTCOME": "success"}
            host_lock = root / "host-resource.lock"
            with host_lock.open("a") as lock:
                fcntl.flock(lock, fcntl.LOCK_EX)
                for index, block in enumerate(blocks):
                    with self.subTest(block=index):
                        evidence = root / f"evidence-{index}"
                        shell = block.replace("/build/laplace/work/recursive-proof-evidence", str(evidence)).replace(
                            "/build/laplace/work/host-resource.lock", str(host_lock))
                        result = subprocess.run(["bash", "-euo", "pipefail"], input=shell, cwd=checkout,
                                                env=environment, text=True, capture_output=True, timeout=3)
                        self.assertEqual(0, result.returncode, result.stderr)
                        self.assertFalse(forbidden.exists(), "retention launched a historical live query")
                        manifests = list(evidence.glob("*/manifest.json"))
                        self.assertEqual(1, len(manifests))
                        manifest = json.loads(manifests[0].read_text())
                        for original in (old, current):
                            item = next(item for item in manifest["receipts"] if item["requested_path"] == str(original))
                            self.assertEqual("retained", item["disposition"])
                            self.assertEqual(hashlib.sha256(original.read_bytes()).hexdigest(), item["sha256"])
                            self.assertEqual(original.read_bytes(), (manifests[0].parent / item["artifact_file"]).read_bytes())
                        self.assertFalse((manifests[0].parent / "counterexample-structure.json").exists())

    def test_operational_delivery_does_not_depend_on_chess_ingestion_or_measurement(self):
        workflow = load(MAIN)
        self.assertEqual(["product"], list(workflow["jobs"]))
        product = workflow["jobs"]["product"]
        steps = product["steps"]
        indexes = {step.get("id"): index for index, step in enumerate(steps) if step.get("id")}
        phases = ["operational_seed", "publish", "operational_execution", "db_health"]
        positions = [indexes["product_" + phase] for phase in phases]
        self.assertEqual(sorted(positions), positions)
        for phase in phases:
            step = steps[indexes["product_" + phase]]
            selected = phase.replace("_", "-")
            self.assertEqual("contains(env.LAPLACE_CI_PHASES, '|" + selected + "|')", step["if"])
            self.assertIn("ci-session.py", step["run"])
            self.assertIn("--phase " + selected, step["run"])
            self.assertNotIn("continue-on-error", step)
        for phase in ("chess_runtime", "stockfish_corpus", "post_stockfish_execution", "chess_completion", "recorded_chess"):
            self.assertNotIn("product_" + phase, indexes)
        self.assertNotIn("outputs", product)
        upload = next(step for step in steps if step.get("name") == "Upload operational seed and execution receipts")
        self.assertLess(indexes["product_operational_execution"], steps.index(upload))
        self.assertEqual("always() && steps.product_session.outcome == 'success' && contains(env.LAPLACE_CI_PHASES, '|operational-seed|')", upload["if"])
        self.assertEqual("/build/laplace/work/operational-proof/${{ github.run_id }}-${{ github.run_attempt }}/", upload["with"]["path"])

    def test_existing_benchmark_and_source_tools_remain_independently_available(self):
        benchmark = load(WORKFLOWS / "benchmark-evidence.yml")
        self.assertIn("workflow_dispatch", benchmark["on"])
        choices = benchmark["on"]["workflow_dispatch"]["inputs"]["suite"]["options"]
        for suite in ("chess", "recorded", "geometry"):
            self.assertIn(suite, choices)
        self.assertTrue((ROOT / "scripts/ingest-stockfish-corpus.py").is_file())
        self.assertNotIn("needs", benchmark["jobs"]["benchmark"])

    def run_operational_proof(self, receipt_values, first_rc=0, second_rc=0, *, fresh="0", restore="0"):
        """Execute the actual receipt reader and orchestration under a real host lock."""
        source = PRODUCT.read_text()
        function = "verify_operational_execution() {" + source.split(
            "verify_operational_execution() {", 1)[1].split("\n}\n", 1)[0] + "\n}\n"
        script = ('set -euo pipefail\noperational_proof_directory="$TEST_CURRENT_INVOCATION"\n'
                  + function + '\nverify_operational_execution\necho later-acceptance\n')
        with tempfile.TemporaryDirectory(prefix="operational-proof-", dir=os.environ.get("TMPDIR", "/build/laplace/work")) as directory:
            root = Path(directory)
            proof_root = root / "operational-proof"
            current = proof_root / "34994682033-2"
            current.mkdir(parents=True)
            previous = proof_root / "34994682033-1" / "invocation-prior"
            previous.mkdir(parents=True)
            previous.joinpath("seed.json").write_text(json.dumps(
                {"disposition": "verified", "run": {"run_id": "bf972cf5-0ca8-4cab-99e7-0b9b19482c34"}}))
            for filename in ("task.json", "antonym-task.json"):
                previous.joinpath(filename).write_text("earlier-execution-receipt\n")
            baseline = {path: path.read_bytes() for path in previous.iterdir()}
            for index, value in enumerate(receipt_values):
                invocation = current / f"invocation-{index}"
                invocation.mkdir()
                invocation.joinpath("seed.json").write_text(json.dumps(value))
                baseline[invocation / "seed.json"] = (invocation / "seed.json").read_bytes()
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
                           "TEST_CURRENT_INVOCATION": str(current / "invocation-0"), "LAPLACE_CI_SESSION_DIRECTORY": ""}
            result = subprocess.run(["flock", "--exclusive", "--close", str(lock), "bash"],
                                    input=script, cwd=ROOT, env=environment,
                                    text=True, capture_output=True, timeout=20)
            observed = [json.loads(line) for line in calls.read_text().splitlines()] if calls.exists() else []
            for path, payload in baseline.items():
                self.assertEqual(payload, path.read_bytes(), "proof changed a seed or another invocation's evidence")
            retained = {path.name: json.loads(path.read_text()) for path in current.glob("invocation-0/*task.json")}
            return result, observed, retained, str(current / "invocation-0")

    def test_both_operational_forms_reuse_exact_seed_and_retain_separate_receipts(self):
        run_id = "e56a93c8-36b4-46ef-b6b7-6a224a2b5cb9"
        result, calls, retained, directory = self.run_operational_proof(
            [{"disposition": "verified", "run": {"run_id": run_id}}])
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("later-acceptance\n", result.stdout)
        self.assertEqual([
            ["scripts/verify-operational-task.py", "--shape-file", "seeds/operational/tasks/en_define.json",
             "--seed-run-id", run_id, "--receipt", directory + "/task.json"],
            ["scripts/verify-operational-task.py", "--proof-mode", "direct-relation", "--prompt", "The opposite of hot is",
             "--operand", "hot", "--shape-file", "seeds/operational/tasks/en_antonym.json",
             "--exemplar-file", "seeds/operational/exemplars/en_antonym.conllu", "--seed-run-id", run_id,
             "--receipt", directory + "/antonym-task.json"],
        ], calls)
        self.assertEqual({"task.json": {"exit": 0}, "antonym-task.json": {"exit": 0}}, retained)

    def test_operational_failure_stops_later_acceptance_and_retains_failure(self):
        seed = {"disposition": "verified", "run": {"run_id": "e56a93c8-36b4-46ef-b6b7-6a224a2b5cb9"}}
        for first, second in ((41, 0), (0, 42)):
            with self.subTest(first=first, second=second):
                result, calls, retained, _ = self.run_operational_proof([seed], first, second)
                self.assertEqual(first or second, result.returncode, result.stderr)
                self.assertEqual("", result.stdout)
                self.assertEqual(1 if first else 2, len(calls))
                self.assertEqual({"exit": first}, retained["task.json"])
                if first:
                    self.assertNotIn("antonym-task.json", retained)
                else:
                    self.assertEqual({"exit": second}, retained["antonym-task.json"])

    def test_operational_proof_rejects_missing_or_unverified_seed_before_execution(self):
        seed = {"disposition": "verified", "run": {"run_id": "e56a93c8-36b4-46ef-b6b7-6a224a2b5cb9"}}
        for name, values in (
            ("missing-current-attempt", []),
            ("unverified", [{**seed, "disposition": "failed"}]),
            ("invalid-uuid", [{**seed, "run": {"run_id": "not-a-run-uuid"}}]),
            ("noncanonical-uuid", [{**seed, "run": {"run_id": seed["run"]["run_id"].upper()}}]),
            ("oversized", [{**seed, "extra": "x" * 1048576}]),
        ):
            with self.subTest(name=name):
                result, calls, retained, _ = self.run_operational_proof(values)
                self.assertNotEqual(0, result.returncode, result.stderr)
                self.assertEqual("", result.stdout)
                self.assertEqual([], calls)
                self.assertEqual({}, retained)

    def test_operational_proof_uses_exact_invocation_not_latest_directory(self):
        seed = {"disposition": "verified", "run": {"run_id": "e56a93c8-36b4-46ef-b6b7-6a224a2b5cb9"}}
        unrelated = {"disposition": "failed", "run": {"run_id": "not-this-invocation"}}
        result, calls, _, _ = self.run_operational_proof([seed, unrelated])
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(2, len(calls))

    def test_operational_proof_preserves_only_explicit_fresh_unrestored_exception(self):
        result, calls, retained, _ = self.run_operational_proof([], fresh="1")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("fresh DB intentionally left unseeded", result.stdout)
        self.assertEqual([], calls)
        self.assertEqual({}, retained)
        result, calls, _, _ = self.run_operational_proof([], fresh="1", restore="1")
        self.assertNotEqual(0, result.returncode, result.stderr)
        self.assertEqual([], calls)
        seed = {"disposition": "verified", "run": {"run_id": "e56a93c8-36b4-46ef-b6b7-6a224a2b5cb9"}}
        result, calls, _, _ = self.run_operational_proof([seed], fresh="1", restore="1")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(2, len(calls))

    def test_main_has_one_product_job_and_one_mutation_authority(self):
        workflow = load(MAIN)
        self.assertEqual("laplace-substrate-lifecycle", workflow["concurrency"]["group"])
        self.assertEqual(["product"], list(workflow["jobs"]))
        product = workflow["jobs"]["product"]
        command = commands(product)
        self.assertIn('--kind product --stage "$LAPLACE_STAGE"', command)
        self.assertIn("bash scripts/product-ci.sh reconcile", command)
        for split_job in ("deploy", "db-ops", "publish", "restore-api", "smoke", "integration-test"):
            self.assertNotIn(split_job, workflow["jobs"])

    def run_lifecycle_order(self, stage="all", failed=""):
        """Execute actual phase selection; replace only external work with exits."""
        source = PRODUCT.read_text()
        initialization = source[source.index('stage="${1:-all}"'):source.index("run_policy() {")]
        definitions = source[source.index("run_policy() {"):source.index('case "${2:-}" in')]
        footer = source[source.index('case "${2:-}" in'):]
        stubs = """
step() { echo "$1"; if [[ "$1" == "$TEST_FAIL" ]]; then return 41; fi; }
run_policy() { step policy; }
run_deps() { step dependencies; }
run_build() { step build; }
run_suite() { step "$2"; }
run_live_suite() { step "$1"; }
run_install() { step native-install; }
run_database_maintenance() { step database-maintenance; }
resume_chess_observation_if_needed() { :; }
restore_foundation_if_requested() { step foundation; }
seed_operational_memory() { step operational-seed; }
run_publish_with_recovery() { step publish; }
verify_operational_execution() { step operational-execution; }
bash() { if [[ "$1" == scripts/test-parallel.sh ]]; then step performance; else step application-check; fi; }
"""
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "github-output"
            environment = {**os.environ, "GITHUB_OUTPUT": str(output), "TEST_FAIL": failed,
                "LAPLACE_FRESH_DB": "0", "LAPLACE_FULL_CLEAN": "0", "LAPLACE_RESTORE_FOUNDATION": "0",
                "LAPLACE_GENERATION_BENCHMARK": "1", "LAPLACE_CI_SESSION_DIRECTORY": ""}
            result = subprocess.run(["bash", "-s", stage], input="set -euo pipefail\n" + initialization
                + definitions + stubs + footer, cwd=directory, env=environment, text=True,
                capture_output=True, timeout=20)
            outputs = dict(line.split("=", 1) for line in output.read_text().splitlines()) if output.exists() else {}
            return result, result.stdout.splitlines(), outputs

    def test_later_failure_remains_a_failed_lifecycle_without_chess_readiness(self):
        for failed in ("db-health", "live-floor", "performance"):
            with self.subTest(failed=failed):
                result, calls, outputs = self.run_lifecycle_order(failed=failed)
                self.assertEqual(41, result.returncode, result.stderr)
                self.assertEqual(failed, calls[-1])
                ordered = ["operational-seed", "publish", "operational-execution", failed]
                positions = [calls.index(name) for name in ordered]
                self.assertEqual(sorted(positions), positions)
                self.assertEqual({}, outputs)

    def test_prerequisite_failure_stops_later_delivery(self):
        for failed in ("operational-seed", "publish", "operational-execution"):
            with self.subTest(failed=failed):
                result, calls, outputs = self.run_lifecycle_order(failed=failed)
                self.assertEqual(41, result.returncode, result.stderr)
                self.assertEqual(failed, calls[-1])
                self.assertEqual({}, outputs)
                self.assertNotIn("db-health", calls)
                self.assertNotIn("live-floor", calls)

    def test_application_only_executes_without_importing_or_measuring_chess(self):
        result, calls, outputs = self.run_lifecycle_order(stage="applications")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["policy", "dependencies", "build", "native-dev", "managed-dev", "uci-dev", "browser-dev",
                          "application-check", "operational-seed", "publish", "operational-execution"], calls)
        self.assertEqual({}, outputs)
        for failed in ("publish", "operational-seed", "operational-execution"):
            with self.subTest(failed=failed):
                result, _, outputs = self.run_lifecycle_order(stage="applications", failed=failed)
                self.assertEqual(41, result.returncode)
                self.assertEqual({}, outputs)

    def test_product_script_owns_order_once(self):
        result = subprocess.run(["bash", str(PRODUCT), "all", "--list-phases"],
                                capture_output=True, text=True, check=True,
                                env={**os.environ, "LAPLACE_GENERATION_BENCHMARK": "1", "LAPLACE_RESTORE_FOUNDATION": "1"})
        self.assertEqual([
            "policy", "dependencies", "build", "native-dev", "managed-dev",
            "uci-dev", "browser-dev", "native-install", "database-maintenance", "foundation",
            "operational-seed", "publish", "operational-execution", "db-health", "native-db", "managed-db", "live-floor", "live-api", "managed-live",
            "generation-eval", "performance"], result.stdout.splitlines())
        text = PRODUCT.read_text()
        for command in ("check-database-health.sh", "ensure-foundation.sh --check-only",
                        "publish-applications.sh deploy", "publish-applications.sh recover"):
            self.assertIn(command, text)

    def test_stage_selection_keeps_mutations_and_fresh_database_exceptions_explicit(self):
        def plan(stage, **flags):
            result = subprocess.run(["bash", str(PRODUCT), stage, "--list-phases"],
                env={**os.environ, "LAPLACE_RESTORE_FOUNDATION": "", "LAPLACE_FRESH_DB": "",
                     "LAPLACE_GENERATION_BENCHMARK": "", **flags},
                capture_output=True, text=True, check=True)
            return result.stdout.splitlines()
        self.assertEqual(["policy"], plan("check"))
        self.assertEqual("build", plan("build")[-1])
        self.assertEqual("browser-dev", plan("test")[-1])
        self.assertEqual("application-check", plan("application-check")[-1])
        self.assertEqual("operational-execution", plan("applications")[-1])
        self.assertNotIn("native-install", plan("applications"))
        self.assertNotIn("publish", plan("deploy"))
        self.assertNotIn("publish", plan("integrate"))
        self.assertNotIn("live-floor", plan("integrate"))
        fresh = plan("all", LAPLACE_FRESH_DB="1")
        self.assertIn("operational-seed", fresh)
        self.assertIn("managed-db", fresh)
        self.assertNotIn("operational-execution", fresh)
        self.assertNotIn("live-floor", fresh)
        restored = plan("all", LAPLACE_FRESH_DB="1", LAPLACE_RESTORE_FOUNDATION="1")
        self.assertIn("foundation", restored)
        self.assertIn("operational-execution", restored)
        self.assertIn("live-floor", restored)
        for stage in ("check", "build", "test", "deploy", "integrate", "all", "application-check", "applications"):
            self.assertFalse(any("chess" in phase or "stockfish" in phase for phase in plan(stage)))

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
                               ("scripts/ingest-stockfish-corpus.py", "1"),
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
        self.assertIn('--kind pr --stage "$stage"', command)
        self.assertIn("--phase policy", command)
        self.assertIn("git worktree add --detach", command)
        self.assertIn("git worktree remove --force", command)
        for forbidden in ("pipeline.sh install", "pipeline.sh migrate", "publish-applications.sh deploy", "sudo "):
            self.assertNotIn(forbidden, command)
        self.assertEqual("true", workflow["concurrency"]["cancel-in-progress"])

    def test_private_database_proof_requires_each_source_session_physicality_and_chess_case(self):
        source = (ROOT / "scripts/pr-db-proof.sh").read_text(encoding="utf-8")
        prefix = "Laplace.SubstrateCRUD.Tests."
        methods = [
            prefix + "OperationalSourceExecutionTests.AuthoredTaskSource_ExecutesNovelRequestAfterSharedAdmissionAndFold",
            prefix + "OperationalSourceExecutionTests.AuthoredTaskSource_BindsSynsetThroughTwoWitnessedNamingHops",
            prefix + "OperationalSourceExecutionTests.AuthoredAntonymExemplar_AdmitsCompleteSourceWithNativeParseProvenance",
            prefix + "OperationalSourceExecutionTests.AuthoredAntonymTask_ExecutesNovelRequestThroughAdmittedWordBinding",
            prefix + "NativeSqlBatchTests.ConversationWriterResumesProjectionWithoutForgingContent",
            prefix + "NativeSqlBatchTests.LegacySessionContentIsPreservedAndRequiresExplicitRecovery",
            prefix + "ChessPositionPlayingPersistenceTests.CompleteDistinctPlayingsFoldOnceAndExactReplayPreservesEvidenceAndStanding",
            prefix + "NativeSqlBatchTests.WitnessScopesExcludeCrossProductsButRetainConflictingObjects",
            prefix + "PhysicalityObservationWriterTests.OrdinaryWriterRetainsBothRawFormsAndReusesDurableDescriptorViewEvidence",
            prefix + "PhysicalityObservationWriterTests.SupplementalRawRowsCannotExcludeSelectedBodiesOrDuplicateTheirWitness",
            prefix + "PhysicalityObservationWriterTests.ConsensusFoldsGeneratedEvidenceOncePerDistinctActualSourceUnit",
            prefix + "PhysicalityObservationWriterTests.SourceOnlyJournalBackfillRequiresFreshVerificationAndAtomicGeneratedEvidence",
            prefix + "PhysicalityObservationWriterTests.SourceOnlyConversationBackfillDoesNotAppendTheOriginalTurnAgain",
            prefix + "PhysicalityObservationWriterTests.InvalidRawMetadataIsRejectedBeforeOpeningTheDatabase",
            prefix + "SessionPhysicalityObservationTests.ExistingTurnAppendRetainsOldAndNewFormsAndWriterReplayDoesNotAppendAgain",
            prefix + "SessionPhysicalityObservationTests.NativeSessionRollbackRetainsOriginalProjectionEvidenceAndFold",
            prefix + "SessionPhysicalityObservationTests.WaitingReadCommittedAppenderReadsTheBodyCommittedAfterItsStatementStarted",
            prefix + "PhysicalityObservationWriterTests.MissingCarrierRetainsDescriptorAndLaterContentCompletesOnlyItsView",
        ]
        expected_filter = "|".join("FullyQualifiedName=" + method for method in methods)
        self.assertIn("--filter '" + expected_filter + "'", source)
        self.assertIn('managed_results="$exemplar_results"', source)
        self.assertIn('"$managed_results/operational-source-execution.trx"',
                      source.split('rm -f ', 1)[1].split('PATH="$PG_PREFIX/bin:', 1)[0])
        validator = source.split('python3 - "$managed_results/operational-source-execution.trx" <<\'PY\'\n', 1)[1].split("\nPY\n", 1)[0]
        names = [methods[0], methods[1], methods[2], methods[3], methods[4] + "(batchPrefix: False)",
                 methods[4] + "(batchPrefix: True)", methods[5], methods[6],
                 prefix + "NativeSqlBatchTests.WitnessScopesExcludeCrossProductsButRetainConflictingObjects",
                 prefix + "PhysicalityObservationWriterTests.OrdinaryWriterRetainsBothRawFormsAndReusesDurableDescriptorViewEvidence",
                 prefix + "PhysicalityObservationWriterTests.SupplementalRawRowsCannotExcludeSelectedBodiesOrDuplicateTheirWitness(variant: 0, transportedForms: 1, expectedWitnesses: 1)",
                 prefix + "PhysicalityObservationWriterTests.SupplementalRawRowsCannotExcludeSelectedBodiesOrDuplicateTheirWitness(variant: 1, transportedForms: 2, expectedWitnesses: 2)",
                 prefix + "PhysicalityObservationWriterTests.SupplementalRawRowsCannotExcludeSelectedBodiesOrDuplicateTheirWitness(variant: 2, transportedForms: 3, expectedWitnesses: 2)",
                 prefix + "PhysicalityObservationWriterTests.ConsensusFoldsGeneratedEvidenceOncePerDistinctActualSourceUnit",
                 prefix + "PhysicalityObservationWriterTests.SourceOnlyJournalBackfillRequiresFreshVerificationAndAtomicGeneratedEvidence",
                 prefix + "PhysicalityObservationWriterTests.SourceOnlyConversationBackfillDoesNotAppendTheOriginalTurnAgain",
                 prefix + "PhysicalityObservationWriterTests.InvalidRawMetadataIsRejectedBeforeOpeningTheDatabase(partialTrajectory: False)",
                 prefix + "PhysicalityObservationWriterTests.InvalidRawMetadataIsRejectedBeforeOpeningTheDatabase(partialTrajectory: True)",
                 prefix + "SessionPhysicalityObservationTests.ExistingTurnAppendRetainsOldAndNewFormsAndWriterReplayDoesNotAppendAgain",
                 prefix + "SessionPhysicalityObservationTests.NativeSessionRollbackRetainsOriginalProjectionEvidenceAndFold",
                 prefix + "SessionPhysicalityObservationTests.WaitingReadCommittedAppenderReadsTheBodyCommittedAfterItsStatementStarted",
                 prefix + "PhysicalityObservationWriterTests.MissingCarrierRetainsDescriptorAndLaterContentCompletesOnlyItsView",
                 ]

        def receipt():
            root = ET.Element("TestRun", xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
            results = ET.SubElement(root, "Results")
            for name in names:
                ET.SubElement(results, "UnitTestResult", testName=name, outcome="Passed")
            summary = ET.SubElement(root, "ResultSummary")
            ET.SubElement(summary, "Counters", total=str(len(names)), executed=str(len(names)), passed=str(len(names)),
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
        for corruption in ("missing", "repeated-theory", "repeated-form-variant", "wrong-test", "skipped", "failed", "counter-only"):
            with self.subTest(corruption=corruption):
                root = receipt()
                results = root.find("Results")
                if corruption == "missing":
                    results.remove(results[6])
                elif corruption == "repeated-theory":
                    results[5].set("testName", names[4])
                elif corruption == "repeated-form-variant":
                    results[11].set("testName", names[10])
                elif corruption == "wrong-test":
                    results[6].set("testName", prefix + "UnrelatedPassingTest")
                elif corruption in ("skipped", "failed"):
                    results[2].set("outcome", "NotExecuted" if corruption == "skipped" else "Failed")
                else:
                    root.find("ResultSummary/Counters").set("executed", "6")
                check(root, False)

    def test_private_database_executes_the_endpoint_identity_store(self):
        source = (ROOT / "scripts/pr-db-proof.sh").read_text(encoding="utf-8")
        name = "Laplace.Endpoints.OpenAICompat.Tests.BrowserIdentityTests.PostgresIdentityStorePersistsAccountSessionAndConversation"
        self.assertIn('identity_database="${REGRESS_DB}_identity"', source)
        self.assertIn('"$PG_PREFIX/bin/createdb" "$identity_database"', source)
        self.assertIn('-f "$ROOT/db/migrations/20260915000000_app_identity_sessions.sql"', source)
        self.assertIn('LAPLACE_DB="Host=$socket_dir;Port=$PGPORT;Username=$PGUSER;Database=$identity_database"', source)
        self.assertIn("--filter 'FullyQualifiedName=" + name + "'", source)
        self.assertIn('rm -f -- "$identity_receipt"', source)
        self.assertIn('"$PG_PREFIX/bin/dropdb" "$identity_database"', source)
        validator = source.split("<<'PY_IDENTITY'\n", 1)[1].split("\nPY_IDENTITY\n", 1)[0]

        def receipt():
            root = ET.Element("TestRun", xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
            results = ET.SubElement(root, "Results")
            ET.SubElement(results, "UnitTestResult", testName=name, outcome="Passed")
            summary = ET.SubElement(root, "ResultSummary")
            ET.SubElement(summary, "Counters", total="1", executed="1", passed="1",
                          failed="0", notExecuted="0")
            return root

        for corruption in (None, "missing", "unrelated", "duplicate", "skipped", "failed", "counter"):
            with self.subTest(corruption=corruption):
                root = receipt()
                results = root.find("Results")
                if corruption == "missing":
                    results.remove(results[0])
                elif corruption == "unrelated":
                    results[0].set("testName", name + "Unrelated")
                elif corruption == "duplicate":
                    ET.SubElement(results, "UnitTestResult", testName=name, outcome="Passed")
                elif corruption in ("skipped", "failed"):
                    results[0].set("outcome", "NotExecuted" if corruption == "skipped" else "Failed")
                elif corruption == "counter":
                    root.find("ResultSummary/Counters").set("executed", "0")
                with tempfile.TemporaryDirectory() as directory:
                    path = Path(directory) / "identity.trx"
                    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)
                    result = subprocess.run([sys.executable, "-", str(path)], input=validator,
                                            text=True, capture_output=True)
                self.assertEqual(result.returncode == 0, corruption is None, result.stdout + result.stderr)

    def test_private_database_executes_all_durable_billing_contracts(self):
        source = (ROOT / "scripts/pr-db-proof.sh").read_text(encoding="utf-8")
        migrations = [
            "20260611000000_app_billing.sql",
            "20260722000000_app_billing_identity.sql",
            "20260807020000_app_consume_credit.sql",
            "20260915000000_app_identity_sessions.sql",
            "20260916000000_app_workspace_invitations.sql",
            "20260916000100_app_subscription_sync.sql",
            "20260916000200_browser_ticket_identity_binding.sql",
        ]
        migration_positions = [source.index('-f "$ROOT/db/migrations/' + name + '"') for name in migrations]
        self.assertEqual(migration_positions, sorted(migration_positions))
        billing_filter = "--filter 'FullyQualifiedName~Laplace.Endpoints.OpenAICompat.Tests.PostgresBillingStoreContractTests.'"
        self.assertIn(billing_filter, source)
        self.assertLess(migration_positions[-1], source.index(billing_filter))
        self.assertLess(source.index(billing_filter), source.index('"$PG_PREFIX/bin/dropdb" "$identity_database"'))
        self.assertIn('rm -f -- "$billing_receipt"', source)
        self.assertIn("--logger 'trx;LogFileName=billing-stores.trx'", source)
        tests = (ROOT / "app/Laplace.Endpoints.OpenAICompat.Tests/BillingStoreContractTests.cs").read_text(encoding="utf-8")
        self.assertIn('[Trait("Tier", "db")]\npublic sealed class PostgresBillingStoreContractTests', tests)
        validator = source.split("<<'PY_BILLING'\n", 1)[1].split("\nPY_BILLING\n", 1)[0]
        prefix = "Laplace.Endpoints.OpenAICompat.Tests.PostgresBillingStoreContractTests."
        methods = [
            "QuoteStore_PutGetUpdate_RoundTrips",
            "Ledger_RecordsAndReadsNewestFirst",
            "Entitlements_ActivateConsumeExhaustDeactivate",
            "Entitlements_RenewResetsUsedCredits",
            "WebhookEvents_DuplicateBeginIsRejected",
            "PriceMap_SetOverwritesAndGets",
            "ApiKeys_PutGetRevokeAndLabelLookup",
            "Config_SetOverwritesAndGets",
        ]

        def receipt():
            root = ET.Element("TestRun", xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
            results = ET.SubElement(root, "Results")
            for method in methods:
                ET.SubElement(results, "UnitTestResult", testName=prefix + method, outcome="Passed")
            summary = ET.SubElement(root, "ResultSummary")
            ET.SubElement(summary, "Counters", total="8", executed="8", passed="8",
                          failed="0", notExecuted="0")
            return root

        def check(root, passes):
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "billing.trx"
                ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)
                result = subprocess.run([sys.executable, "-", str(path)], input=validator,
                                        text=True, capture_output=True)
            self.assertEqual(result.returncode == 0, passes, result.stdout + result.stderr)

        check(receipt(), True)
        for omitted in range(len(methods)):
            with self.subTest(omitted=methods[omitted]):
                root = receipt()
                results = root.find("Results")
                results.remove(results[omitted])
                check(root, False)
        for corruption in ("duplicate", "unrelated", "skipped", "failed", "counter"):
            with self.subTest(corruption=corruption):
                root = receipt()
                results = root.find("Results")
                if corruption == "duplicate":
                    results[0].set("testName", results[1].get("testName"))
                elif corruption == "unrelated":
                    results[0].set("testName", prefix + "UnrelatedPassingTest")
                elif corruption in ("skipped", "failed"):
                    results[0].set("outcome", "NotExecuted" if corruption == "skipped" else "Failed")
                else:
                    root.find("ResultSummary/Counters").set("executed", "7")
                check(root, False)

    def test_private_native_database_requires_built_physicality_fixtures_in_order(self):
        source = (ROOT / "scripts/pr-db-proof.sh").read_text(encoding="utf-8")
        self.assertIn('ctest --test-dir "$BUILD" --show-only=json-v1 -L regress > "$native_selection"', source)
        self.assertIn('ctest --test-dir "$BUILD" --output-on-failure --no-tests=error -L regress', source)
        validator = source.split("<<'PY_NATIVE_SELECTION'\n", 1)[1].split("\nPY_NATIVE_SELECTION\n", 1)[0]
        required = ["physicality_descriptor_admission", "physicality_readback", "physicality_readback_cold"]
        selection = {"tests": [{"name": "regress_laplace_substrate", "command": [
            "/branch/pg_regress", "--use-existing", "bootstrap", *required]}]}

        def check(document, passes):
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "selection.json"
                path.write_text(json.dumps(document), encoding="utf-8")
                result = subprocess.run([sys.executable, "-", str(path)], input=validator,
                                        text=True, capture_output=True)
            self.assertEqual(result.returncode == 0, passes, result.stdout + result.stderr)

        check(selection, True)
        check({"tests": []}, False)
        check({"tests": selection["tests"] * 2}, False)
        disabled = copy.deepcopy(selection)
        disabled["tests"][0]["properties"] = [{"name": "DISABLED", "value": True}]
        check(disabled, False)
        for fixture in required:
            for mutation in ("missing", "repeated"):
                with self.subTest(fixture=fixture, mutation=mutation):
                    changed = copy.deepcopy(selection)
                    command = changed["tests"][0]["command"]
                    if mutation == "missing":
                        command.remove(fixture)
                    else:
                        command.append(fixture)
                    check(changed, False)
        changed = copy.deepcopy(selection)
        changed["tests"][0]["command"][-2:] = list(reversed(required[-2:]))
        check(changed, False)

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
        for name in ("actions-audit.py", "ci-session.py", "pr-db-proof.sh", "product-ci.sh", "pr-proof.sh", "bootstrap-laplace-runner.sh", "test-profile-registry.py", "test-profiles.json", "test-parallel.sh", "ci-policy.sh", "maintain-installed-database.sh", "repair-legacy-content-lifecycle.sh"):
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

    def test_corpus_measurement_remains_explicit_and_preserves_host_ownership(self):
        self.check_audit(lambda ws: ws["chess-corpus-evidence.yml"]["on"]["push"].update(
            {"branches": ["main"]}), "corpus work must use explicit operator branches")
        self.check_audit(lambda ws: ws["chess-corpus-evidence.yml"]["concurrency"].update(
            {"cancel-in-progress": "true"}), "shared workspace serialization")
        self.check_audit(lambda ws: ws["chess-corpus-evidence.yml"]["jobs"]["corpus"].update(
            {"timeout-minutes": "0"}), "independent and finitely bounded")
        def remove_lock(ws):
            step = self.step(ws, "chess-corpus-evidence.yml", "id", "corpus_measurement")
            step["run"] = step["run"].replace("flock --exclusive --close --timeout 1800", "bash")
        self.check_audit(remove_lock, "corpus owner lost flock")
        self.check_audit(lambda ws: self.step(ws, "chess-corpus-evidence.yml", "name",
            "Upload complete corpus evidence").update({"if": "success()"}), "complete evidence must upload after failure")

    def test_explicit_chess_acceptance_cannot_become_main_push_work(self):
        self.check_audit(lambda ws: ws["benchmark-evidence.yml"]["on"]["push"].update(
            {"branches": ["main"]}), "explicitly named operator branch")
        self.check_audit(lambda ws: ws["benchmark-evidence.yml"]["jobs"]["acceptance"].update(
            {"if": "always()"}), "acceptance must remain explicitly selected")
        self.check_audit(lambda ws: ws["benchmark-evidence.yml"]["jobs"]["benchmark"].update(
            {"if": "always()"}), "ordinary suites must exclude")
        self.check_audit(lambda ws: ws["benchmark-evidence.yml"]["jobs"]["acceptance"].update(
            {"needs": "product"}), "independent from deployment")

    def test_acceptance_selector_distinguishes_reusable_main_push_from_operator_push(self):
        workflow = load(WORKFLOWS / "benchmark-evidence.yml")
        self.assertEqual(
            "(github.event_name == 'push' && startsWith(github.ref, 'refs/heads/verify/chess-acceptance-')) || inputs.suite == 'acceptance'",
            workflow["jobs"]["acceptance"]["if"])
        self.assertEqual(
            "inputs.suite != 'acceptance' && (github.event_name != 'push' || !startsWith(github.ref, 'refs/heads/verify/chess-acceptance-'))",
            workflow["jobs"]["benchmark"]["if"])
        self.check_audit(lambda ws: ws["benchmark-evidence.yml"]["jobs"]["acceptance"].update(
            {"if": "github.event_name == 'push' || inputs.suite == 'acceptance'"}),
            "acceptance must remain explicitly selected")

    def test_explicit_acceptance_retains_whole_job_deadline(self):
        self.check_audit(lambda ws: ws["benchmark-evidence.yml"]["jobs"]["acceptance"].update(
            {"timeout-minutes": "0"}), "finite whole-job envelope")

    def test_explicit_acceptance_retains_lock_owner_and_failed_evidence(self):
        def edit_execution(ws):
            steps = ws["benchmark-evidence.yml"]["jobs"]["acceptance"]["steps"]
            step = next(s for s in steps if s.get("id") == "chess_acceptance")
            step["run"] = step["run"].replace("flock --exclusive --close", "true")
        self.check_audit(edit_execution, "canonical locked owner")
        def suppress_evidence(ws):
            steps = ws["benchmark-evidence.yml"]["jobs"]["acceptance"]["steps"]
            step = next(s for s in steps if s.get("name") == "Upload complete chess acceptance evidence")
            step["if"] = "success()"
        self.check_audit(suppress_evidence, "acceptance evidence must upload on failure")

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

    def test_build_and_deployment_do_not_repair_historical_content(self):
        source = PRODUCT.read_text()
        for token in ("repair-legacy-content", "resume_held_repair", "corpus-repair"):
            self.assertNotIn(token, source)
        main_commands = commands(load(MAIN)["jobs"]["product"])
        self.assertNotIn("collect-legacy-repair-evidence", main_commands)
        self.assertNotIn("classify-legacy-content", commands(load(PR)["jobs"]["prove"]))

    def test_operational_proof_cannot_precede_publication_or_have_failure_suppressed(self):
        path = self.root / "scripts/product-ci.sh"
        original = path.read_text()
        invocation = "operational-execution) verify_operational_execution ;;"
        mutations = (
            original.replace(invocation, "operational-execution) : ;;"),
            original.replace(invocation, "operational-execution) verify_operational_execution || true ;;"),
            original.replace("    echo publish", "    printf '%s\\n' operational-execution publish"),
        )
        for mutation in mutations:
            try:
                self.assertNotEqual(original, mutation)
                path.write_text(mutation)
                self.check_audit(diagnostic="product lifecycle")
            finally:
                path.write_text(original)

    def test_failed_operational_proof_stops_later_product_acceptance(self):
        source = PRODUCT.read_text()
        definitions = source[source.index("run_policy() {"):source.index('case "${2:-}" in')]
        script = "set -euo pipefail\nstage=all\n" + definitions + """
run_publish_with_recovery() { echo publication-complete; }
verify_operational_execution() { echo operational-proof-rejected; return 41; }
run_suite() { echo unexpected-integration; }
run_live_suite() { echo unexpected-live; }
for phase in publish operational-execution db-health native-db managed-db live-floor; do
  run_phase "$phase"
done
"""
        result = subprocess.run(["bash"], input=script, text=True, capture_output=True)
        self.assertEqual(41, result.returncode, result.stderr)
        self.assertEqual(["publication-complete", "operational-proof-rejected"], result.stdout.splitlines())

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
                    "LAPLACE_FRESH_DB": "0", "LAPLACE_RESTORE_FOUNDATION": "0", "LAPLACE_CI_SESSION_DIRECTORY": ""}
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

    def test_publication_recovery_is_scoped_to_publication(self):
        source = PRODUCT.read_text()
        definitions = source[source.index("run_policy() {"):source.index('case "${2:-}" in')]
        script = "set -euo pipefail\nstage=all\n" + definitions + """
bash() { echo publication-recovery; }
ensure_api_running() { echo API-START; }
run_publish() { echo published; }
python3() {
  if [[ "$*" == *'--resume-if-needed'* ]]; then return 0; fi
  [[ "$*" == 'scripts/quiesce-managed-database.py --database laplace --timeout-seconds 3600 -- bash scripts/repair-chess-position-outcomes.sh' ]] || return 90
  [[ "$LAPLACE_REPAIR_PUBLISHED_SOURCE" == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" ]] || return 91
  echo observation-migration
}
git() { printf '%s\\n' aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa; }
PGDATABASE=laplace
verify_operational_execution() { echo operational-proof-rejected; return 37; }
run_phase publish
run_phase operational-execution
"""
        result = subprocess.run(["bash", "-c", script], text=True, capture_output=True, timeout=10)
        self.assertEqual(37, result.returncode, result.stdout + result.stderr)
        self.assertEqual(["published", "observation-migration", "operational-proof-rejected"], result.stdout.splitlines())

    def test_observation_migration_failure_stops_acceptance_after_publication(self):
        source = PRODUCT.read_text()
        definitions = source[source.index("run_policy() {"):source.index('case "${2:-}" in')]
        script = "set -euo pipefail\nstage=all\n" + definitions + """
recover_publish() { echo unexpected-publication-rollback; }
run_publish() { echo published; }
python3() { if [[ "$*" == *'--resume-if-needed'* ]]; then return 0; fi; echo observation-migration-rejected; return 43; }
git() { printf '%s\\n' aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa; }
verify_operational_execution() { echo unexpected-operational-proof; }
run_phase publish
run_phase operational-execution
"""
        result = subprocess.run(["bash", "-c", script], text=True, capture_output=True, timeout=10)
        self.assertEqual(43, result.returncode, result.stdout + result.stderr)
        self.assertEqual(["published", "observation-migration-rejected"], result.stdout.splitlines())

    def test_failed_proof_cannot_be_hidden_at_step_or_job(self):
        mutations = [
            lambda ws: self.step(ws, "pr-validation.yml", "id", "pr_session").update({"continue-on-error": "true"}),
            lambda ws: ws["pr-validation.yml"]["jobs"]["prove"].update({"continue-on-error": "${{ true }}"}),
            lambda ws: self.step(ws, "laplace.yml", "id", "product_build").update({"continue-on-error": "True"}),
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
        self.check_audit(lambda ws: self.step(ws, "pr-validation.yml", "id", "pr_session").update({"if": "steps.baseline_diagnostic.outcome == 'success'"}), "independent of optional baseline")
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

    def test_no_extra_product_mutation_or_domain_measurement_job(self):
        self.check_audit(lambda ws: ws["laplace.yml"]["jobs"].update({"deploy": {"runs-on": "self-hosted", "timeout-minutes": "30", "steps": [{"run": "bash scripts/pipeline.sh install"}]}}), "product delivery without domain measurement jobs")
        self.check_audit(lambda ws: ws["laplace.yml"]["jobs"].update({"chess_environment": {
            "needs": "product", "uses": "./.github/workflows/benchmark-evidence.yml", "with": {"suite": "chess"}}}), "product delivery without domain measurement jobs")

    def test_operational_receipts_survive_seed_only_stage_and_execution_failure(self):
        self.check_audit(lambda ws: self.step(ws, "laplace.yml", "name", "Upload operational seed and execution receipts").update(
            {"if": "success()"}), "operational")


if __name__ == "__main__":
    unittest.main(verbosity=2)
