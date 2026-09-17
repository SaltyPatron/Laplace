#!/usr/bin/env python3
"""Static contracts for shared-host workflow ownership."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ROOT / ".github" / "workflows"
SEED = (WORKFLOWS / "seed.yml").read_text(encoding="utf-8")
SEED_STEP = "Select source and run substrate mutation under one host reservation"


def run_block(step_name: str) -> str:
    marker = f"      - name: {step_name}\n"
    start = SEED.index(marker)
    run = SEED.index("        run: |\n", start) + len("        run: |\n")
    next_step = SEED.find("\n      - ", run)
    finish = len(SEED) if next_step < 0 else next_step
    return SEED[run:finish]


class SharedHostQueue(unittest.TestCase):
    def test_product_lifecycle_validates_workflow_changes(self):
        text = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        self.assertIn("branches: [main]", text)
        self.assertNotIn('- ".github/**"', text)
        self.assertIn('- "docs/**"', text)
        self.assertIn('- "**/*.md"', text)

    def test_every_self_hosted_job_shares_lifecycle_concurrency(self):
        host_jobs = 0
        for path in sorted(WORKFLOWS.glob("*.yml")):
            text = path.read_text(encoding="utf-8")
            count = text.count("runs-on: [self-hosted, laplace]")
            if count == 0:
                continue
            host_jobs += count
            with self.subTest(workflow=path.name):
                self.assertEqual(text.count("group: laplace-host-lifecycle"), count)
                self.assertEqual(text.count("queue: max"), count)
                self.assertEqual(text.count("cancel-in-progress: false"), count)
        self.assertGreater(host_jobs, 0)

    def test_benchmark_is_dispatch_only_versioned_evidence(self):
        text = (WORKFLOWS / "benchmark-evidence.yml").read_text(encoding="utf-8")
        self.assertIn("on:\n  workflow_dispatch:\n", text)
        self.assertNotIn("\n  push:\n", text)
        self.assertNotIn("\n  workflow_call:\n", text)
        self.assertIn("options: [quick, throughput, core, scale, moby, all]", text)
        self.assertEqual(text.count("runs-on: [self-hosted, laplace]"), 1)
        self.assertIn("python3 scripts/benchmark_suite.py validate", text)
        self.assertIn('python3 scripts/benchmark_suite.py "${args[@]}"', text)
        self.assertIn("name: laplace-benchmark-${{ github.run_id }}-${{ github.run_attempt }}", text)
        self.assertIn("retention-days: 90", text)
        self.assertNotIn("accept-chess-environment.py", text)

    def test_benchmark_checkout_preserves_retained_workspace_state(self):
        text = (WORKFLOWS / "benchmark-evidence.yml").read_text(encoding="utf-8")
        self.assertIn("git diff --quiet", text)
        self.assertIn("git diff --cached --quiet", text)
        self.assertIn("git checkout --no-overwrite-ignore --detach", text)
        self.assertNotIn("git checkout --force", text)
        self.assertIn("AUTHORIZATION: basic $checkout_auth", text)


class SeedHostOwnership(unittest.TestCase):
    def test_reusable_seed_shares_actions_lifecycle_group(self):
        self.assertIn("group: laplace-host-lifecycle", SEED)
        self.assertIn("queue: max", SEED)
        self.assertIn("cancel-in-progress: false", SEED)

    def test_checkout_environment_and_mutation_share_one_host_reservation(self):
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
        self.assertNotIn("uses: ./.github/actions/setup-laplace-env", SEED)

    def test_mutation_holds_host_lock_before_reproving_source_and_build(self):
        block = run_block(SEED_STEP)
        lock_open = block.index('exec 9>"$workspace_lock_root/host-resource.lock"')
        lock_take = block.index("flock 9")
        source_proof = block.index('current_sha="$(git rev-parse HEAD)"')
        build_proof = block.index('built_sha="$(cat build/.laplace-source-revision')
        mutation = block.index('case "$MODE" in')
        self.assertLess(lock_open, lock_take)
        self.assertLess(lock_take, source_proof)
        self.assertLess(source_proof, build_proof)
        self.assertLess(build_proof, mutation)
        self.assertIn('[[ "$current_sha" == "$TARGET_SHA" ]]', block)
        self.assertIn('[[ "$built_sha" == "$TARGET_SHA" ]]', block)
        self.assertIn("LAPLACE_SETUP_REQUIRE_BUILT_REVISION=true", block)

    def test_evict_and_ingest_share_one_locked_step(self):
        block = run_block(SEED_STEP)
        eviction = block.index('scripts/measure-lane.sh -- "$GITHUB_WORKSPACE/scripts/laplace" evict')
        ingest = block.index('ingest_one "$SOURCE_KEY" "$PATH_INPUT"')
        self.assertLess(block.index("flock 9"), eviction)
        self.assertLess(eviction, ingest)
        self.assertIn('scripts/ingest-source.sh "$key" "$path"', block)
        self.assertIn('scripts/ingest-source.sh "$key"', block)
        self.assertIn("export LAPLACE_INGEST_FORCE=1", block)
        self.assertNotIn("- name: Evict selected source", SEED)

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
        chess_wrappers = {
            "seed-chess-books.yml",
            "seed-chess-eval.yml",
            "seed-chess-games.yml",
            "seed-chess-openings.yml",
        }
        actual = {p.name for p in WORKFLOWS.glob("seed-*.yml")}
        self.assertEqual(direct | chess_wrappers, actual)

        for name in direct:
            with self.subTest(workflow=name):
                text = (WORKFLOWS / name).read_text(encoding="utf-8")
                self.assertIn("uses: ./.github/workflows/seed.yml", text)
                self.assertNotIn("scripts/ingest-source.sh", text)
                self.assertNotIn("scripts/measure-lane.sh", text)

        for name in chess_wrappers:
            with self.subTest(workflow=name):
                text = (WORKFLOWS / name).read_text(encoding="utf-8")
                self.assertIn("uses: ./.github/workflows/seed-chess.yml", text)
                self.assertNotIn("scripts/ingest-source.sh", text)
                self.assertNotIn("scripts/measure-lane.sh", text)


if __name__ == "__main__":
    unittest.main(verbosity=2)
