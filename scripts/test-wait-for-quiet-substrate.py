#!/usr/bin/env python3
"""Exercise the real quiet shell owner across installed and pre-install SQL."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
OWNER = ROOT / "scripts/wait-for-quiet-substrate.sh"
PSQL = r"""
import json, os, pathlib, sys
root = pathlib.Path(os.environ["QUIET_FIXTURE"])
plan = json.loads((root / "plan.json").read_text())
query = sys.argv[-1]
with (root / "calls.jsonl").open("a") as stream:
    stream.write(json.dumps(sys.argv[1:]) + "\n")
if "to_regclass(" in query:
    key = "journal"
elif "to_regprocedure(" in query:
    key = "capability"
elif query.startswith("SELECT count(*) FROM ops.ingest_reconcile_orphans"):
    key = "canonical"
elif "ORDER BY started_at" in query:
    key = "diagnostic"
else:
    raise SystemExit("unexpected SQL protocol")
counter = root / (key + ".count")
index = int(counter.read_text()) if counter.exists() else 0
counter.write_text(str(index + 1))
responses = plan.get(key)
if responses is None:
    raise SystemExit("forbidden SQL owner: " + key)
response = responses[min(index, len(responses) - 1)]
sys.stdout.write(response["out"] + "\n")
raise SystemExit(response.get("rc", 0))
"""


class QuietBootstrapTests(unittest.TestCase):
    def run_owner(self, capability, *, canonical=None, journal="present",
                  diagnostic=None, budget=0):
        with tempfile.TemporaryDirectory(prefix="quiet-bootstrap-") as directory:
            root = Path(directory)
            binary = root / "bin"
            binary.mkdir()
            psql = binary / "psql"
            psql.write_text("#!" + sys.executable + "\n" + PSQL)
            psql.chmod(0o755)
            systemctl = binary / "systemctl"
            systemctl.write_text("#!/bin/sh\nexit 0\n")
            systemctl.chmod(0o755)
            plan = {"journal": [{"out": journal}]}
            if capability is not None:
                plan["capability"] = capability
            if canonical is not None:
                plan["canonical"] = canonical
            if diagnostic is not None:
                plan["diagnostic"] = diagnostic
            (root / "plan.json").write_text(json.dumps(plan))
            env = {**os.environ, "PATH": str(binary) + os.pathsep + os.environ["PATH"],
                   "QUIET_FIXTURE": str(root), "LAPLACE_QUIET_INTERVAL": "0",
                   "PGHOST": "/selected/original/socket", "PGUSER": "laplace_admin"}
            result = subprocess.run(["bash", str(OWNER), "laplace_selected", str(budget)],
                                    env=env, text=True, capture_output=True, timeout=8)
            calls = [json.loads(line) for line in (root / "calls.jsonl").read_text().splitlines()]
            for args in calls:
                self.assertEqual(args[:6], ["-h", "/selected/original/socket", "-U",
                                            "laplace_admin", "-d", "laplace_selected"])
            return result, [args[-1] for args in calls]

    def assert_no_reconciliation(self, calls):
        self.assertFalse(any(query.startswith("SELECT count(*) FROM ops.ingest_reconcile_orphans")
                             for query in calls))
        self.assertFalse(any("UPDATE " in query or "DELETE " in query for query in calls))

    def test_missing_functions_zero_running_is_read_only_idle(self):
        result, calls = self.run_owner([{"out": "0 missing"}])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("zero running journal rows", result.stdout)
        self.assertEqual(len(calls), 2)
        self.assertIn("to_regprocedure('ops.ingest_run_live(uuid,interval)')", calls[1])
        self.assertIn("to_regprocedure('ops.ingest_reconcile_orphans(interval)')", calls[1])
        self.assert_no_reconciliation(calls)

    def test_missing_functions_nonzero_waits_and_refuses_without_mutation(self):
        result, calls = self.run_owner([{"out": "7 missing"}])
        self.assertEqual(result.returncode, 1)
        self.assertIn("no reconciliation is attempted", result.stdout)
        self.assertIn("not proven quiet after 0s", result.stdout)
        self.assert_no_reconciliation(calls)

    def test_missing_functions_rechecks_until_owner_finishes(self):
        result, calls = self.run_owner([{"out": "7 missing"}, {"out": "0 missing"}], budget=3)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(sum("to_regprocedure(" in query for query in calls), 2)
        self.assert_no_reconciliation(calls)

    def test_failed_probe_with_plausible_zero_does_not_prove_idle(self):
        result, calls = self.run_owner([{"out": "0 missing", "rc": 1}])
        self.assertEqual(result.returncode, 1)
        self.assert_no_reconciliation(calls)

    def test_malformed_counts_and_function_states_fail_closed(self):
        for output in ("-1 missing", "x missing", "0 unknown", "0 missing\n0", "0 present\n0", ""):
            with self.subTest(output=output):
                result, calls = self.run_owner([{"out": output}])
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assert_no_reconciliation(calls)

    def test_installed_functions_use_canonical_reconciliation(self):
        result, calls = self.run_owner([{"out": "7 present"}], canonical=[{"out": "3\n0"}])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(sum(query.startswith("SELECT count(*) FROM ops.ingest_reconcile_orphans")
                             for query in calls), 1)
        self.assertIn("no live ingest in flight", result.stdout)

    def test_installed_functions_run_even_when_raw_running_count_is_zero(self):
        result, calls = self.run_owner([{"out": "0 present"}], canonical=[{"out": "0\n0"}])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(any(query.startswith("SELECT count(*) FROM ops.ingest_reconcile_orphans")
                            for query in calls))

    def test_installed_canonical_live_count_still_blocks(self):
        result, calls = self.run_owner([{"out": "7 present"}],
                                      canonical=[{"out": "0\n2"}], diagnostic=[{"out": ""}])
        self.assertEqual(result.returncode, 1)
        self.assertIn("waiting on 2 live ingest(s)", result.stdout)
        self.assertTrue(any("ORDER BY started_at" in query for query in calls))

    def test_installed_canonical_failure_still_blocks(self):
        result, _ = self.run_owner([{"out": "7 present"}],
                                   canonical=[{"out": "canonical query failed", "rc": 1}])
        self.assertEqual(result.returncode, 1)

    def test_missing_journal_remains_pre_schema_idle(self):
        result, calls = self.run_owner(None, journal="missing")
        self.assertEqual(result.returncode, 0)
        self.assertEqual(len(calls), 1)
        self.assert_no_reconciliation(calls)


if __name__ == "__main__":
    unittest.main()
