#!/usr/bin/env python3
"""Contracts for direct, usable operator workflows."""
from pathlib import Path
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ROOT / ".github" / "workflows"
SEED = (WORKFLOWS / "seed.yml").read_text(encoding="utf-8")
SEED_STEP = "Run substrate operation"


def run_block(step_name: str) -> str:
    marker = f"      - name: {step_name}\n"
    start = SEED.index(marker)
    run = SEED.index("        run: |\n", start) + len("        run: |\n")
    next_step = SEED.find("\n      - ", run)
    finish = len(SEED) if next_step < 0 else next_step
    return SEED[run:finish]


class WorkflowOwnership(unittest.TestCase):
    def test_product_push_does_not_run_for_docs_or_workflow_only_changes(self):
        text = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        self.assertIn("branches: [main]", text)
        self.assertIn('- ".github/**"', text)
        self.assertIn('- "docs/**"', text)
        self.assertIn('- "**/*.md"', text)

    def test_mainline_delegates_to_one_reusable_product_stage(self):
        text = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        mainline = text.split("  mainline:\n", 1)[1].split("\n  operator:\n", 1)[0]
        self.assertIn("uses: ./.github/workflows/product-stage.yml", mainline)
        self.assertIn("stage: mainline", mainline)
        self.assertNotIn("cancel-in-progress:", mainline)
        self.assertNotIn("runs-on:", mainline)

    def test_expensive_competitive_proof_is_dispatch_only(self):
        text = (WORKFLOWS / "competitive-proof.yml").read_text(encoding="utf-8")
        self.assertIn("on:\n  workflow_dispatch:\n", text)
        self.assertNotIn("\n  push:\n", text)

    def test_benchmark_is_dispatch_only_versioned_evidence(self):
        text = (WORKFLOWS / "benchmark-evidence.yml").read_text(encoding="utf-8")
        self.assertIn("on:\n  workflow_dispatch:\n", text)
        self.assertNotIn("\n  push:\n", text)
        self.assertNotIn("\n  workflow_call:\n", text)
        self.assertIn(
            "options: [quick, throughput, core, scale, dag, moby, query, chess, geometry, recorded, all]",
            text,
        )
        self.assertEqual(text.count("runs-on: [self-hosted, laplace]"), 1)
        self.assertEqual(text.count("host-resource.lock"), 1)
        self.assertNotIn("\nconcurrency:\n", text)
        self.assertIn('git show "$workflow_sha:scripts/benchmark-evidence-ci.sh"', text)
        self.assertIn('exec bash "$driver"', text)
        driver = (ROOT / "scripts/benchmark-evidence-ci.sh").read_text(encoding="utf-8")
        self.assertIn("python3 scripts/benchmark_suite.py validate", driver)
        self.assertIn('python3 scripts/benchmark_suite.py "${run_args[@]}"', driver)
        self.assertIn("scripts/benchmark_scale_plan.py", driver)
        self.assertIn("retention-days: 90", text)

    def test_database_surface_is_direct_and_operator_visible(self):
        text = (WORKFLOWS / "db-ops.yml").read_text(encoding="utf-8")
        self.assertIn(
            "options: [status, migrate, repair, reindex, remigrate, recreate]",
            text,
        )
        self.assertIn('description: "recreate only: type laplace"', text)
        self.assertIn('"$PGDATABASE"|"RECREATE"|"RECREATE $PGDATABASE")', text)
        self.assertNotIn("check-installed-extension-current.py", text)
        self.assertNotIn("group: laplace-host-lifecycle", text)
        for operation in ("migrate", "repair", "reindex", "remigrate", "recreate"):
            self.assertIn(f"            {operation})", text)


class SeedHostOwnership(unittest.TestCase):
    def test_seed_does_not_add_a_second_actions_queue(self):
        self.assertNotIn("group: laplace-host-lifecycle", SEED)
        self.assertNotIn("queue: max", SEED)

    def test_checkout_and_mutation_share_the_real_host_lock(self):
        block = run_block(SEED_STEP)
        lock = block.index("flock 9")
        fetch = block.index("git fetch --no-tags --depth=2 origin")
        checkout = block.index("git checkout --no-overwrite-ignore --detach")
        environment = block.index("source scripts/ci-environment.sh")
        mutation = block.index('case "$MODE" in')
        self.assertLess(lock, fetch)
        self.assertLess(fetch, checkout)
        self.assertLess(checkout, environment)
        self.assertLess(environment, mutation)

    def test_seed_consumes_the_built_cli_without_help_rendering_or_exact_commit_gate(self):
        block = run_block(SEED_STEP)
        self.assertIn("LAPLACE_SETUP_REQUIRE_BUILT_REVISION=false", block)
        self.assertNotIn("scripts/laplace --help", block)
        self.assertNotIn("scripts/laplace -h", block)
        self.assertNotIn("built_sha=", block)
        self.assertNotIn('[[ "$built_sha" == "$TARGET_SHA" ]]', block)
        self.assertIn('case "$MODE" in', block)

    def test_evict_and_ingest_share_one_locked_step(self):
        block = run_block(SEED_STEP)
        eviction = block.index('scripts/measure-lane.sh -- "$GITHUB_WORKSPACE/scripts/laplace" evict')
        ingest = block.index('ingest_one "$SOURCE_KEY" "$PATH_INPUT"')
        self.assertLess(block.index("flock 9"), eviction)
        self.assertLess(eviction, ingest)
        self.assertIn('scripts/ingest-source.sh "$key" "$path"', block)
        self.assertIn('scripts/ingest-source.sh "$key"', block)
        self.assertIn("export LAPLACE_INGEST_FORCE=1", block)

    def test_mutation_shell_does_not_interpolate_dispatch_inputs_directly(self):
        block = run_block(SEED_STEP)
        self.assertNotIn("${{", block)
        for name in ("MODE", "SOURCE_KEY", "PATH_INPUT", "LANGS_INPUT", "EVICT_CONFIRM"):
            self.assertIn(f"{name}:", SEED)

    def test_every_seed_wrapper_delegates_to_one_shared_mutation_owner(self):
        direct = {
            "seed-chess.yml",
            "seed-code.yml",
            "seed-documents.yml",
            "seed-foundation.yml",
            "seed-knowledge.yml",
            "seed-models.yml",
        }
        actual = {p.name for p in WORKFLOWS.glob("seed-*.yml")}
        self.assertEqual(direct, actual)

        for name in direct:
            with self.subTest(workflow=name):
                text = (WORKFLOWS / name).read_text(encoding="utf-8")
                self.assertIn("uses: ./.github/workflows/seed.yml", text)
                self.assertNotIn("scripts/ingest-source.sh", text)
                self.assertNotIn("scripts/measure-lane.sh", text)


class FoundationCompletion(unittest.TestCase):
    """Execute the real ladder shell with explicit ingest/psql test boundaries."""

    SOURCES = {
        "unicode": "UnicodeDecomposer",
        "iso639": "ISO639Decomposer",
        "operational": "OperationalDecomposer",
        "cili": "CILIDecomposer",
        "wordnet": "WordNetDecomposer",
        "verbnet": "VerbNetDecomposer",
        "propbank": "PropBankDecomposer",
        "framenet": "FrameNetDecomposer",
        "mapnet": "MapNetDecomposer",
        "wordframenet": "WordFrameNetDecomposer",
        "semlink": "SemLinkDecomposer",
    }

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="foundation-completion-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        scripts = self.root / "scripts"
        scripts.mkdir()
        shutil.copyfile(ROOT / "scripts" / "ensure-foundation.sh",
                        scripts / "ensure-foundation.sh")
        self.state = self.root / "state.json"
        self.ingests = self.root / "ingests.jsonl"
        self.env = dict(os.environ)
        for key in ("CI", "GITHUB_ACTIONS", "LAPLACE_INGEST_FORCE",
                    "LAPLACE_INGEST_MAX_UNITS", "LAPLACE_INGEST_LANGS"):
            self.env.pop(key, None)
        self.env.update(
            PATH=str(self.root) + os.pathsep + self.env.get("PATH", ""),
            LAPLACE_DBNAME="foundation-test",
            PGHOST="/test/no-database",
            PGUSER="test-no-database",
            FOUNDATION_TEST_STATE=str(self.state),
            FOUNDATION_TEST_INGESTS=str(self.ingests),
            FOUNDATION_TEST_SOURCES=json.dumps(self.SOURCES),
            FOUNDATION_TEST_OMIT="[]",
            FOUNDATION_TEST_INGEST_RC="0",
        )
        self.write_command(self.root / "psql", r'''
import json, os, re, sys
from pathlib import Path
query = sys.argv[-1]
if "pg_database" in query:
    print("1")
elif "ops.evidence_count" in query:
    source = re.search(r"laplace\.source_id\('([^']+)'\)", query).group(1)
    present = json.loads(Path(os.environ["FOUNDATION_TEST_STATE"]).read_text())
    print("t" if source in present else "f")
elif "laplace.ingest_run_journal" in query:
    print("test journal: ingest returned without fabricating a completion marker")
else:
    raise SystemExit("unexpected SQL in fixture: " + query)
''')
        self.write_command(scripts / "foundation-bulk-indexes.sh", r'''
import sys
if len(sys.argv) != 2 or sys.argv[1] not in ("begin", "end"):
    raise SystemExit("foundation bulk-index fixture expects begin|end")
''')
        self.write_command(scripts / "ingest-source.sh", r'''
import json, os, sys
from pathlib import Path
assert sys.argv[1] == "chain"
with Path(os.environ["FOUNDATION_TEST_INGESTS"]).open("a") as stream:
    stream.write(json.dumps(sys.argv[2:]) + "\n")
rc = int(os.environ["FOUNDATION_TEST_INGEST_RC"])
if rc:
    raise SystemExit(rc)
state = Path(os.environ["FOUNDATION_TEST_STATE"])
present = set(json.loads(state.read_text()))
sources = json.loads(os.environ["FOUNDATION_TEST_SOURCES"])
omitted = set(json.loads(os.environ["FOUNDATION_TEST_OMIT"]))
for source in sys.argv[2:]:
    if source not in omitted:
        present.add(sources[source])
state.write_text(json.dumps(sorted(present)))
''')

    def write_command(self, path, body):
        path.write_text("#!" + sys.executable + "\n" + body, encoding="utf-8")
        path.chmod(0o700)

    def run_ladder(self, *, missing=(), omitted=(), args=(), ingest_rc=0):
        self.state.write_text(json.dumps([
            source for key, source in self.SOURCES.items() if key not in missing
        ]), encoding="utf-8")
        self.env["FOUNDATION_TEST_OMIT"] = json.dumps(list(omitted))
        self.env["FOUNDATION_TEST_INGEST_RC"] = str(ingest_rc)
        return subprocess.run(
            ["bash", str(self.root / "scripts" / "ensure-foundation.sh"), *args],
            env=self.env, text=True, capture_output=True, timeout=30,
        )

    def calls(self):
        return [json.loads(line) for line in self.ingests.read_text().splitlines()] \
            if self.ingests.exists() else []

    def test_success_requires_the_missing_marker_to_become_durable(self):
        result = self.run_ladder(missing=("framenet",))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.calls(), [["framenet"]])
        self.assertIn("foundation complete: foundation-test", result.stdout)
        self.assertEqual(set(json.loads(self.state.read_text())),
                         set(self.SOURCES.values()))

    def test_successful_ingest_without_markers_lists_every_remaining_source(self):
        result = self.run_ladder(
            missing=("framenet", "mapnet"), omitted=("framenet", "mapnet"))
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertEqual(self.calls(), [["framenet", "mapnet"]])
        self.assertIn("missing: framenet (source=FrameNetDecomposer layer=3)",
                      result.stderr)
        self.assertIn("missing: mapnet (source=MapNetDecomposer layer=3)",
                      result.stderr)
        self.assertNotIn("foundation complete:", result.stdout)
        self.assertNotIn("missing: unicode", result.stderr)

    def test_already_complete_ladder_does_not_ingest(self):
        result = self.run_ladder()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.calls(), [])
        self.assertIn("foundation layers OK", result.stdout)

    def test_required_lexical_rechecks_only_its_selected_sources(self):
        result = self.run_ladder(
            missing=tuple(self.SOURCES), args=("--required-lexical",))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.calls(), [["unicode", "iso639", "cili", "wordnet"]])
        self.assertEqual(set(json.loads(self.state.read_text())),
                         {self.SOURCES[key] for key in
                          ("unicode", "iso639", "cili", "wordnet")})
        self.assertIn("foundation complete: foundation-test", result.stdout)

    def test_check_only_does_not_ingest_or_claim_completion(self):
        result = self.run_ladder(missing=("framenet",), args=("--check-only",))
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertEqual(self.calls(), [])
        self.assertIn("missing: framenet", result.stderr)
        self.assertNotIn("foundation complete:", result.stdout)

    def test_ingest_failure_is_not_replaced_by_a_successful_marker_check(self):
        result = self.run_ladder(missing=("framenet",), ingest_rc=23)
        self.assertEqual(result.returncode, 23, result.stdout + result.stderr)
        self.assertEqual(self.calls(), [["framenet"]])
        self.assertNotIn("foundation complete:", result.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
