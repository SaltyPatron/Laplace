#!/usr/bin/env python3
"""Execute the proof's repeated model-admission gate without model/DB work."""
from __future__ import annotations

import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
PROOF = ROOT / "scripts" / "model-synthesize-ci.sh"
CLI_SOURCE = ROOT / "app" / "Laplace.Cli" / "IngestCommands.cs"


class ModelProofReplayGate(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="laplace-model-proof-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "app").mkdir()
        script = PROOF.read_text(encoding="utf-8")
        start = script.index('log "deposit safetensors (pass 2')
        end = script.index('\nlog "evidence/consensus gates"', start)
        self.gate = script[start:end]
        producer = CLI_SOURCE.read_text(encoding="utf-8")
        self.assertIn(
            'Console.WriteLine($"Safetensor snapshot already deposited — source {modelName}: {modelSource}");',
            producer,
        )
        self.success = (
            "Safetensor snapshot already deposited — source Qwen/Qwen2.5-Coder-3B-Instruct: "
            "Hash128 { Hi = 123, Lo = 456 }\n"
            "(re-deposition refused to prevent consensus contamination; "
            "reset with db-fresh to test from scratch)\n"
        )

    def execute(self, output: str, rc: int = 0):
        env = dict(os.environ, ROOT=str(self.root), PROBE_OUTPUT=output, PROBE_RC=str(rc))
        script = (
            'set -euo pipefail\n'
            'MODEL_DIR="$ROOT/model"\n'
            'CLI=(fixture_cli)\n'
            'log() { :; }\n'
            'die() { printf "%s\\n" "$*" >&2; exit 1; }\n'
            'fixture_cli() {\n'
            '  [[ "$#" == 3 && "$1" == ingest && "$2" == safetensors && "$3" == "$MODEL_DIR" ]] || return 98\n'
            '  printf "%s" "$PROBE_OUTPUT"\n'
            '  return "$PROBE_RC"\n'
            '}\n'
            + self.gate
            + '\nprintf "%s\\n" EVIDENCE_GATES_REACHED\n'
        )
        return subprocess.run(
            ["bash", "-c", script], env=env, capture_output=True, text=True,
            encoding="utf-8", timeout=10,
        )

    def test_current_model_completion_branch_passes(self):
        result = self.execute(self.success)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("EVIDENCE_GATES_REACHED", result.stdout)

    def test_unrelated_already_messages_cannot_pass(self):
        for output in (
            "already ingested\n",
            "unicode already ingested\n",
            "model already deposited\n",
            "warning: Safetensor snapshot already deposited — source example\n",
            "done: 3 intents applied, 2 novel entities\n",
            "",
        ):
            with self.subTest(output=output):
                result = self.execute(output)
                self.assertNotEqual(result.returncode, 0)
                self.assertNotIn("EVIDENCE_GATES_REACHED", result.stdout)
                self.assertIn("pass 2 did not short-circuit", result.stderr)

    def test_success_diagnostic_cannot_hide_failed_model_command(self):
        result = self.execute(self.success, rc=23)
        self.assertEqual(result.returncode, 23, result.stdout + result.stderr)
        self.assertNotIn("EVIDENCE_GATES_REACHED", result.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
