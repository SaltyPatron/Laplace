#!/usr/bin/env python3
"""Exercise the real dotnet/MSBuild solution scheduler used by managed DB QA.

The fixture projects replace only database work with a process ownership probe.
No test host, PostgreSQL, native library, or installed product is touched.
"""
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
import unittest
from xml.sax.saxutils import escape

sys.dont_write_bytecode = True
ROOT = pathlib.Path(__file__).resolve().parents[1]

PROBE = r'''import fcntl, json, os, pathlib, sys, time
root = pathlib.Path(os.environ["SCHEDULER_PROBE_ROOT"])
name = sys.argv[1]
event = {"project": name, "filter": sys.argv[2], "configuration": sys.argv[3],
         "pid": os.getpid(), "start": time.monotonic_ns()}
with (root / "owner.lock").open("a") as owner:
    try:
        fcntl.flock(owner, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        event["overlap"] = True
        (root / (name + ".json")).write_text(json.dumps(event))
        sys.exit(42)
    event["overlap"] = False
    # Real overlapping child processes remain possible inside each project.
    import concurrent.futures
    def child(_):
        return subprocess.run([sys.executable, "-c", "import time;time.sleep(0.08)"],
                              check=True).returncode
    import subprocess
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        assert list(pool.map(child, range(2))) == [0, 0]
    time.sleep(0.3)
    event["finish"] = time.monotonic_ns()
    (root / (name + ".json")).write_text(json.dumps(event))
sys.exit(9 if os.environ.get("SCHEDULER_FAIL_PROJECT") == name else 0)
'''

def owner_function():
    source = (ROOT / "scripts/test-parallel.sh").read_text()
    match = re.search(r"(?m)^run_managed_db\(\) \{\n.*?^\}", source, re.S)
    if match is None:
        raise AssertionError("canonical managed DB function not found")
    return match.group(0)

class ManagedDbSchedulingTests(unittest.TestCase):
    def exercise(self, fail_project=None, parallel_mutant=False):
        with tempfile.TemporaryDirectory(prefix="managed-db-scheduling-") as name:
            root = pathlib.Path(name)
            app = root / "app"
            app.mkdir()
            (root / "probe.py").write_text(PROBE)
            projects = ["First", "Second", "Third"]
            for project in projects:
                folder = app / project
                folder.mkdir()
                # The actual solution VSTest target invokes this finite process
                # instead of a DB fixture. No NuGet restore/build is required.
                command = (
                    f'{sys.executable} "{root / "probe.py"}" '
                    '"$(MSBuildProjectName)" "$(VSTestTestCaseFilter)" "$(Configuration)"'
                )
                (folder / (project + ".csproj")).write_text(
                    '<Project><PropertyGroup><IsTestProject>true</IsTestProject>'
                    '<TargetFramework>net10.0</TargetFramework></PropertyGroup>'
                    '<Target Name="VSTest"><Exec Command="' +
                    escape(command, {'"': '&quot;'}) +
                    '" /></Target></Project>'
                )
            (app / "Laplace.slnx").write_text(
                '<Solution>' + ''.join(
                    f'<Project Path="{project}/{project}.csproj" />'
                    for project in projects) + '</Solution>'
            )
            function = owner_function()
            if parallel_mutant:
                function = function.replace("-m:1", "-m:3").replace(
                    "-p:BuildInParallel=false", "-p:BuildInParallel=true")
            script = (
                "set -euo pipefail\n"
                "set_installed_perfcache() { :; }\n"
                "sync_managed_native() { :; }\n" +
                function + "\nrun_managed_db\n"
            )
            env = os.environ.copy()
            env.update(SCHEDULER_PROBE_ROOT=str(root),
                       DOTNET_CLI_TELEMETRY_OPTOUT="1",
                       MSBUILDDISABLENODEREUSE="1")
            if fail_project:
                env["SCHEDULER_FAIL_PROJECT"] = fail_project
            result = subprocess.run(
                ["bash", "-c", script], cwd=root, env=env,
                text=True, capture_output=True, timeout=90)
            rows = [json.loads(path.read_text())
                    for path in sorted(root.glob("*.json"))]
            return result, rows

    def test_real_solution_projects_keep_one_pool_owner_and_all_filters(self):
        result, rows = self.exercise()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual({row["project"] for row in rows},
                         {"First", "Second", "Third"})
        self.assertTrue(all(not row["overlap"] for row in rows), rows)
        self.assertTrue(all(row["filter"] == "Tier=db" for row in rows), rows)
        self.assertTrue(all(row["configuration"] == "Release" for row in rows), rows)
        ordered = sorted(rows, key=lambda row: row["start"])
        self.assertTrue(all(a["finish"] <= b["start"]
                            for a, b in zip(ordered, ordered[1:])), rows)

    def test_failed_project_remains_failed(self):
        result, rows = self.exercise(fail_project="Second")
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Second", {row["project"] for row in rows})
        self.assertTrue(all(not row["overlap"] for row in rows), rows)

    def test_parallel_counterfactual_detects_competing_pool_owners(self):
        result, rows = self.exercise(parallel_mutant=True)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(any(row["overlap"] for row in rows),
                        result.stdout + result.stderr + repr(rows))

if __name__ == "__main__":
    unittest.main()
