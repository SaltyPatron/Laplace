#!/usr/bin/env python3
"""Exercise real project planning and shell guards after external source edits."""
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


def shell_function(path, name):
    match = re.search(r"^" + name + r"\(\) \{\n.*?^\}$", path.read_text(), re.M | re.S)
    if match is None:
        raise AssertionError(f"missing {name} in {path}")
    return match.group()


class AppFingerprintTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="app-fingerprints-", dir=os.environ["TMPDIR"])
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.env = dict(os.environ, LAPLACE_FORCE_ALL="0")
        # Use the repository's actual project graph and external Content item,
        # so changing the owning project or losing a reference breaks the proof.
        for project in (ROOT / "app").glob("*/*.csproj"):
            destination = self.root / project.relative_to(ROOT)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(project, destination)
        for relative in ("scripts/affected-app.py", "scripts/lib/fp.sh"):
            destination = self.root / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / relative, destination)
        shutil.copytree(ROOT / "docs/specs", self.root / "docs/specs")
        for name in ("INVENTION.md", "INVENTIONS.md"):
            shutil.copy2(ROOT / "docs" / name, self.root / "docs" / name)
        self.run_command("git", "init", "-q")
        self.run_command("git", "add", ".")
        self.run_command("git", "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                         "commit", "-qm", "fixture")
        self.seed = self.root / "docs/specs/37_Substrate_Operation_ISA.md"

    def run_command(self, *args):
        result = subprocess.run(args, cwd=self.root, env=self.env, text=True,
                                capture_output=True, timeout=20)
        self.assertEqual(0, result.returncode, result.stderr)
        return result.stdout.strip()

    def planner(self, command, namespace):
        return self.run_command(sys.executable, "scripts/affected-app.py", command, "--ns", namespace)

    def publish_fingerprint(self):
        return self.run_command("bash", "-c", r'''
set -euo pipefail
ROOT="$PWD"
source "$ROOT/scripts/lib/fp.sh"
''' + shell_function(ROOT / "scripts/pipeline.sh", "fp_publish") + "\nfp_publish\n")

    def build_cli(self):
        self.run_command("bash", "-c", r'''
set -euo pipefail
ROOT="$PWD"
DLL="$ROOT/app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli.dll"
source "$ROOT/scripts/lib/fp.sh"
dotnet() {
  printf 'built\n' >> "$ROOT/build-calls"
  mkdir -p "$(dirname "$DLL")"
  touch "$DLL"
}
''' + shell_function(ROOT / "scripts/ingest-source.sh", "build_cli") + "\nbuild_cli\n")

    def test_seed_only_edit_rebuilds_consumers_retests_and_invalidates_publish(self):
        # Build/test stamps describe a successful unchanged baseline.
        self.planner("record", "build")
        self.planner("record", "test")
        self.assertEqual("", self.planner("plan", "build"))
        self.assertEqual("", self.planner("plan", "test"))
        self.build_cli()
        # CLI output must not itself participate in the content fingerprint.
        (self.root / ".git/info/exclude").write_text("**/bin/\nbuild/\nbuild-calls\n")
        self.build_cli()
        self.assertEqual(["built"], (self.root / "build-calls").read_text().splitlines())
        before_publish = self.publish_fingerprint()

        self.seed.write_text(self.seed.read_text() + "\nSource revision for fingerprint test.\n")
        tests = {Path(p).stem for p in self.planner("plan", "test").splitlines()}
        self.assertTrue({"Laplace.Decomposers.Tests", "Laplace.Cli.Tests",
                         "Laplace.Endpoints.OpenAICompat.Tests"}.issubset(tests), tests)
        self.assertNotIn("Laplace.Core.Tests", tests)

        # The build plan emits minimal roots. Expand those real project edges
        # and verify that every binary consuming the seed is rebuilt.
        import runpy
        planner = runpy.run_path(str(self.root / "scripts/affected-app.py"))
        deps = planner["parse_deps"](planner["discover_projects"]())
        covered = set()
        pending = [Path(p).stem for p in self.planner("plan", "build").splitlines()]
        while pending:
            name = pending.pop()
            if name not in covered:
                covered.add(name)
                pending.extend(deps[name])
        self.assertTrue({"Laplace.Decomposers", "Laplace.Cli",
                         "Laplace.Endpoints.OpenAICompat"}.issubset(covered), covered)
        self.assertNotEqual(before_publish, self.publish_fingerprint())
        self.build_cli()
        self.assertEqual(["built", "built"], (self.root / "build-calls").read_text().splitlines())

    def test_missing_source_and_restoration_invalidate_consumer(self):
        self.planner("record", "test")
        source_bytes = self.seed.read_bytes()
        self.seed.unlink()
        self.assertIn("Laplace.Decomposers.Tests", self.planner("plan", "test"))
        self.planner("record", "test")
        self.assertEqual("", self.planner("plan", "test"))
        self.seed.write_bytes(source_bytes)
        self.assertIn("Laplace.Decomposers.Tests", self.planner("plan", "test"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
