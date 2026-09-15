#!/usr/bin/env python3
"""Exercise real evidence reads, bounded copies, and preservation across captures."""
from contextlib import redirect_stdout
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("native_evidence", ROOT / "scripts/capture-native-regression.py")
evidence = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evidence)


class NativeRegressionEvidenceTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="native-regression-evidence-")
        self.addCleanup(temporary.cleanup)
        self.base = Path(temporary.name)
        self.build = self.base / "retained-build"
        self.output = self.base / "evidence"
        self.build.mkdir()
        self.diff = evidence.FILES[2]

    def write(self, relative, data):
        path = self.build / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        return path

    def capture(self, **kwargs):
        output = io.StringIO()
        with redirect_stdout(output):
            receipt = evidence.capture(ROOT, self.build, self.output, "retained-baseline", **kwargs)
        return receipt, json.loads(receipt.read_text()), output.getvalue()

    def test_exact_failure_bytes_survive_source_cleanup_and_second_capture(self):
        data = b"--- expected\r\n+++ actual\r\n+ERROR: exact native failure \xce\xb2\n"
        source = self.write(self.diff, data)
        first, report, _ = self.capture()
        record = next(item for item in report["files"] if item["path"] == self.diff)
        self.assertEqual("captured", report["disposition"])
        self.assertEqual(len(data), record["captured_bytes"])
        self.assertEqual(hashlib.sha256(data).hexdigest(), record["source_sha256"])
        self.assertEqual(data, (first.parent / self.diff).read_bytes())
        source.unlink()
        second, second_report, _ = self.capture()
        self.assertNotEqual(first, second)
        self.assertEqual("absent", second_report["disposition"])
        self.assertEqual(data, (first.parent / self.diff).read_bytes())

    def test_retained_build_does_not_claim_capture_checkout_built_its_bytes(self):
        _, report, _ = self.capture()
        self.assertEqual("retained-build", report["build_identifier"])
        self.assertEqual(evidence.git_head(ROOT), report["capture_source_sha"])
        self.assertNotIn("build_source_sha", report)

    def test_file_and_total_limits_report_prefix_hash_not_full_hash(self):
        self.write(evidence.FILES[0], b"A" * 40)
        self.write(self.diff, b"B" * 40)
        with patch.object(evidence, "MAX_FILE_BYTES", 16), patch.object(evidence, "MAX_TOTAL_BYTES", 24):
            receipt, report, _ = self.capture()
        self.assertEqual("partial", report["disposition"])
        self.assertEqual(24, report["captured_bytes"])
        for relative, length in ((evidence.FILES[0], 16), (self.diff, 8)):
            record = next(item for item in report["files"] if item["path"] == relative)
            data = (receipt.parent / relative).read_bytes()
            self.assertEqual(length, len(data))
            self.assertEqual("truncated", record["status"])
            self.assertIsNone(record["source_sha256"])
            self.assertEqual(hashlib.sha256(data).hexdigest(), record["captured_sha256"])

    def test_symlink_file_and_directory_never_read_an_outside_file(self):
        secret = self.base / "not-a-regression"
        secret.write_text("do-not-capture")
        path = self.build / self.diff
        path.parent.mkdir(parents=True)
        path.symlink_to(secret)
        _, report, printed = self.capture(print_diffs=True)
        self.assertEqual("unreadable", next(item for item in report["files"] if item["path"] == self.diff)["status"])
        self.assertNotIn("do-not-capture", printed)
        path.unlink()
        path.parent.rmdir()
        path.parent.symlink_to(self.base, target_is_directory=True)
        _, report, _ = self.capture()
        self.assertEqual("unreadable", next(item for item in report["files"] if item["path"] == self.diff)["status"])

    def test_fifo_is_rejected_without_waiting_for_a_writer(self):
        path = self.build / self.diff
        path.parent.mkdir(parents=True)
        os.mkfifo(path)
        _, report, _ = self.capture()
        self.assertEqual("unreadable", next(item for item in report["files"] if item["path"] == self.diff)["status"])

    def test_only_selected_build_metadata_and_fixed_diagnostics_are_published(self):
        self.write("CMakeCache.txt", b"CMAKE_HOME_DIRECTORY:INTERNAL=/known/source\nSECRET:STRING=do-not-publish\n")
        self.write(".stamps/build-native", b"a" * 64)
        self.write("extension/laplace_substrate/tests/regress_output/credentials.txt", b"do-not-publish")
        receipt, report, _ = self.capture()
        self.assertEqual({"CMAKE_HOME_DIRECTORY": "/known/source"},
                         report["build_metadata"]["CMakeCache.txt"]["fields"])
        self.assertEqual("a" * 64, report["build_metadata"][".stamps/build-native"]["fingerprint"])
        self.assertNotIn("do-not-publish", receipt.read_text())
        self.assertFalse((receipt.parent / "CMakeCache.txt").exists())
        self.assertEqual([receipt], list(receipt.parent.rglob("*")))

    def test_log_excerpts_are_bounded_and_workflow_commands_are_prefixed(self):
        self.write(self.diff, b"::error::bad\n" + b"z" * 100)
        with patch.object(evidence, "MAX_LOG_BYTES", 16):
            receipt, _, printed = self.capture(print_diffs=True)
        self.assertIn("| ::error::bad", printed)
        self.assertNotIn("\n::error::", printed)
        self.assertIn("Remaining captured diff", printed)
        self.assertIn(b"z" * 100, (receipt.parent / self.diff).read_bytes())

    def test_absent_build_has_explicit_absent_receipt(self):
        self.build.rmdir()
        _, report, _ = self.capture()
        self.assertEqual("absent", report["disposition"])
        self.assertEqual(0, report["captured_bytes"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
