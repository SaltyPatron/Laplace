#!/usr/bin/env python3
"""Exercise the real shell wrapper with a fake CLI; no corpus or database access."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class IngestExitTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="ingest-exit-", dir=os.environ["TMPDIR"])
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        scripts = self.root / "scripts"
        (scripts / "lib").mkdir(parents=True)
        for name in ("ingest-source.sh", "classify-ingest-exit.sh", "lib/storage.sh"):
            shutil.copy2(ROOT / "scripts" / name, scripts / name)
        (scripts / "lib/fp.sh").write_text("fp_compute() { echo fixture; }\nfp_check() { return 1; }\nfp_record() { :; }\n")
        (scripts / "decomposer-gates.json").write_text('{"sources":{"wordnet":{"decomposer":"fixture"}}}')
        (scripts / "verify-ingest-journal.sh").write_text('echo checked >> "$PROOF"\nexit "${PROOF_RC:-0}"\n')
        (self.root / "app").mkdir()
        bin_dir = self.root / "bin"
        bin_dir.mkdir()
        (bin_dir / "dotnet").write_text('''#!/bin/bash
if [[ "$1" == build ]]; then exit 0; fi
printf '%s\\n' "$CLI_MESSAGE"
if [[ "${BREAK_LOG:-0}" == 1 ]]; then
    rm "$INGEST_LOGDIR/laplace-ingest-wordnet.log"
    mkdir "$INGEST_LOGDIR/laplace-ingest-wordnet.log"
fi
exit "$CLI_RC"
''')
        (bin_dir / "psql").write_text("#!/bin/bash\nexit 1\n")
        for f in bin_dir.iterdir():
            f.chmod(0o755)
        self.env = dict(os.environ, PATH=str(bin_dir) + ":" + os.environ["PATH"],
                        GITHUB_ACTIONS="true", GITHUB_OUTPUT=str(self.root / "outputs"),
                        INGEST_LOGDIR=str(self.root / "logs"), PROOF=str(self.root / "proof"),
                        CLI_RC="0", CLI_MESSAGE="completed")

    def run_ingest(self, **changes):
        return subprocess.run(["bash", str(self.root / "scripts/ingest-source.sh"), "wordnet"],
                              env=dict(self.env, **changes), capture_output=True, text=True)

    def test_restart_retains_process_failure_and_marks_preempted(self):
        result = self.run_ingest(CLI_RC="57", CLI_MESSAGE="FATAL: 57P01: terminating connection due to administrator command")
        self.assertEqual(result.returncode, 57, result.stderr)
        self.assertIn("preempted=true", (self.root / "outputs").read_text())
        self.assertFalse((self.root / "proof").exists())

    def test_decomposer_failure_remains_failure(self):
        result = self.run_ingest(CLI_RC="23", CLI_MESSAGE="NullReferenceException")
        self.assertEqual(result.returncode, 23, result.stderr)
        self.assertIn("preempted=false", (self.root / "outputs").read_text())
        self.assertFalse((self.root / "proof").exists())

    def test_success_requires_journal_proof(self):
        self.assertEqual(self.run_ingest().returncode, 0)
        self.assertEqual((self.root / "proof").read_text(), "checked\n")
        self.assertNotEqual(self.run_ingest(PROOF_RC="1").returncode, 0)

    def test_output_failure_does_not_replace_process_failure(self):
        result = self.run_ingest(CLI_RC="23", GITHUB_OUTPUT=str(self.root))
        self.assertEqual(result.returncode, 23, result.stderr)
        self.assertNotEqual(self.run_ingest(GITHUB_OUTPUT=str(self.root)).returncode, 0)

    def test_timing_failure_does_not_replace_process_failure(self):
        result = self.run_ingest(CLI_RC="23", BREAK_LOG="1")
        self.assertEqual(result.returncode, 23, result.stderr)
        (self.root / "logs/laplace-ingest-wordnet.log").rmdir()
        self.assertNotEqual(self.run_ingest(BREAK_LOG="1").returncode, 0)

    def test_default_logs_use_build_drive(self):
        env = dict(self.env)
        env.pop("INGEST_LOGDIR")
        scratch = self.root / "scratch"
        env["LAPLACE_SCRATCH_ROOT"] = str(scratch)
        result = subprocess.run(["bash", str(self.root / "scripts/ingest-source.sh"), "wordnet"],
                                env=env, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue((scratch / "laplace-ingest/laplace-ingest-wordnet.log").is_file())

    def test_os_temp_log_path_is_rejected_before_ingest(self):
        self.assertEqual(self.run_ingest(INGEST_LOGDIR="/tmp/laplace-forbidden").returncode, 2)
        self.assertFalse((self.root / "proof").exists())


if __name__ == "__main__":
    unittest.main()
