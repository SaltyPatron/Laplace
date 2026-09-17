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
    def test_all_mutation_owners_preserve_pending_operations(self):
        expected_groups = {
            "laplace.yml": 2,
            "db-ops.yml": 1,
            "seed.yml": 1,
        }
        for name, count in expected_groups.items():
            with self.subTest(workflow=name):
                text = (WORKFLOWS / name).read_text(encoding="utf-8")
                self.assertEqual(text.count("group: laplace-host-lifecycle"), count)
                self.assertEqual(text.count("queue: max"), count)
                self.assertEqual(text.count("cancel-in-progress: false"), count)


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
        ingest = block.index('scripts/ingest-source.sh "$SOURCE_KEY"')
        self.assertLess(block.index("flock 9"), eviction)
        self.assertLess(eviction, ingest)
        self.assertIn("export LAPLACE_INGEST_FORCE=1", block)
        self.assertNotIn("- name: Evict selected source", SEED)

    def test_mutation_shell_does_not_interpolate_dispatch_inputs_directly(self):
        block = run_block(SEED_STEP)
        self.assertNotIn("${{", block)
        for name in ("MODE", "SOURCE_KEY", "PATH_INPUT", "LANGS_INPUT", "EVICT_CONFIRM"):
            self.assertIn(f"{name}:", SEED)

    def test_operator_wrappers_delegate_mutation_to_reusable_seed(self):
        chess = (WORKFLOWS / "seed-chess.yml").read_text(encoding="utf-8")
        foundation = (WORKFLOWS / "seed-foundation.yml").read_text(encoding="utf-8")
        self.assertIn("uses: ./.github/workflows/seed.yml", chess)
        self.assertIn("uses: ./.github/workflows/seed.yml", foundation)


if __name__ == "__main__":
    unittest.main(verbosity=2)
