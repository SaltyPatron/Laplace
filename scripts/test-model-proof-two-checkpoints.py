#!/usr/bin/env python3
"""Execute selected proof orchestration against finite shell fixtures."""
from __future__ import annotations
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "scripts" / "model-synthesize-ci.sh"
QWEN = "models--Qwen--Qwen2.5-Coder-3B-Instruct"
TINY = "models--TinyLlama--TinyLlama-1.1B-Chat-v1.0"


class TwoCheckpointProof(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="laplace-model-pair-")
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        (self.root / "app").mkdir()
        self.hub = self.root / "models"
        self.source = SOURCE.read_text(encoding="utf-8")
        self.env = {key: value for key, value in os.environ.items()
                    if not key.startswith(("LAPLACE_MODEL_", "LAPLACE_QWEN25_", "LAPLACE_TINYLLAMA_"))}
        self.env["LAPLACE_MODEL_HUB"] = str(self.hub)

    def snapshot(self, family):
        path = self.hub / family / "snapshots" / "test-revision"
        path.mkdir(parents=True)
        for name in ("config.json", "tokenizer.json", "model.safetensors"):
            (path / name).write_text("{}")
        return path

    def select(self, **env):
        start = self.source.index("newest_weighted_snapshot()")
        end = self.source.index("\nmodel_slug=", start)
        script = "set -euo pipefail\n" + self.source[start:end]
        script += '\nprintf "PAIR %s|%s\\n" "$MODEL_DIR" "$CORROBORATION_MODEL_DIR"\n'
        return subprocess.run(["bash", "-c", script], env=dict(self.env, **env),
                              capture_output=True, text=True, timeout=10)

    def test_default_resolves_two_available_checkpoint_families(self):
        qwen, tiny = self.snapshot(QWEN), self.snapshot(TINY)
        result = self.select()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn(f"PAIR {qwen}|{tiny}", result.stdout)

    def test_explicit_tiny_primary_selects_qwen_as_second(self):
        qwen, tiny = self.snapshot(QWEN), self.snapshot(TINY)
        result = self.select(LAPLACE_MODEL_PROOF_DIR=str(tiny))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn(f"PAIR {tiny}|{qwen}", result.stdout)

    def test_one_snapshot_cannot_satisfy_independent_pair(self):
        self.snapshot(QWEN)
        result = self.select()
        self.assertEqual(result.returncode, 2)
        self.assertIn("no second weighted proof model resolved", result.stderr)

    def test_explicit_alias_of_primary_is_rejected(self):
        qwen = self.snapshot(QWEN)
        alias = self.root / "same-model"
        alias.symlink_to(qwen, target_is_directory=True)
        result = self.select(LAPLACE_MODEL_CORROBORATION_DIR=str(alias))
        self.assertEqual(result.returncode, 2)
        self.assertIn("second model directory", result.stderr)

    def phases(self, failure=""):
        start = self.source.index('log "deposit safetensors (pass 1)"')
        end = self.source.index('\nlog "evidence/consensus gates"', start)
        fixture = r'''
set -euo pipefail
MODEL_DIR="$ROOT/first"
CORROBORATION_MODEL_DIR="$ROOT/second"
CLI=(fixture_cli)
log() { :; }
die() { printf "%s\n" "$*" >&2; exit 1; }
fixture_cli() {
  [[ "$1" == ingest ]] || return 98
  if [[ "$2" == safetensors ]]; then
    local name count=0
    case "$3" in
      "$MODEL_DIR") name=first ;;
      "$CORROBORATION_MODEL_DIR") name=second ;;
      *) return 98 ;;
    esac
    [[ ! -f "$ROOT/$name.count" ]] || count="$(cat "$ROOT/$name.count")"
    count=$((count + 1))
    printf "%s\n" "$count" > "$ROOT/$name.count"
    printf "%s\n" "$name:$count" >> "$ROOT/calls"
    [[ "$FAILURE" != "$name:$count" ]] || return 23
    if [[ "$count" == 2 ]]; then
      if [[ "$FAILURE" == "$name:wrong-noop" ]]; then
        printf "%s\n" "already ingested"
      else
        printf "%s\n" "Safetensor snapshot already deposited — source $name: Hash128 { Hi = 1, Lo = 2 }"
      fi
    else
      printf "%s\n" "done: model fixture first admission"
    fi
  elif [[ "$2" == model-corroborate ]]; then
    [[ "$#" == 4 && "$3" == "$MODEL_DIR" && "$4" == "$CORROBORATION_MODEL_DIR" ]] || return 98
    printf "%s\n" corroborate >> "$ROOT/calls"
    [[ "$FAILURE" != corroborate ]] || return 23
  else
    return 98
  fi
}
'''
        script = fixture + self.source[start:end] + '\nprintf "%s\\n" EVIDENCE_GATES_REACHED\n'
        for name in ("calls", "first.count", "second.count"):
            (self.root / name).unlink(missing_ok=True)
        result = subprocess.run(["bash", "-c", script],
                                env=dict(self.env, ROOT=str(self.root), FAILURE=failure),
                                capture_output=True, text=True, timeout=10)
        calls = (self.root / "calls").read_text().splitlines() if (self.root / "calls").exists() else []
        return result, calls

    def test_two_admissions_real_replay_gates_and_joint_owner_precede_evidence(self):
        result, calls = self.phases()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(calls, ["first:1", "second:1", "second:2", "corroborate", "first:2"])
        self.assertIn("EVIDENCE_GATES_REACHED", result.stdout)

    def test_any_admission_replay_or_joint_failure_blocks_evidence_gate(self):
        for failure in ("first:1", "second:1", "second:2", "second:wrong-noop",
                        "corroborate", "first:2", "first:wrong-noop"):
            with self.subTest(failure=failure):
                result, calls = self.phases(failure)
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertNotIn("EVIDENCE_GATES_REACHED", result.stdout)
                if failure.startswith("second:"):
                    self.assertNotIn("corroborate", calls)


if __name__ == "__main__":
    unittest.main(verbosity=2)
