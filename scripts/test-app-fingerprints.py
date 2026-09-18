#!/usr/bin/env python3
"""Exercise the current ingest owner's prepared CLI guard; no compilation or stamps."""
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


def shell_function(path, name):
    match = re.search(r"^" + name + r"\(\) \{\n.*?^\}$", path.read_text(), re.M | re.S)
    if match is None:
        raise AssertionError(f"missing {name} in {path}")
    return match.group()


class PreparedIngestTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(
            prefix="prepared-ingest-", dir=os.environ["TMPDIR"])
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        source = (ROOT / "scripts/ingest-source.sh").read_text()
        start = source.index('if [[ -n "${LAPLACE_BUILD_ROOT:-}" ]]; then')
        end = source.index('\nfi', start) + len('\nfi')
        self.selection = source[start:end]
        self.guard = shell_function(ROOT / "scripts/ingest-source.sh", "require_cli")
        self.native = self.root / "build/engine/core/liblaplace_core.so"
        self.native.parent.mkdir(parents=True)
        self.native.write_bytes(b"exact prepared native fixture")
        production = self.root / "app/Laplace.Decomposers/Fixture.cs"
        production.parent.mkdir(parents=True)
        production.write_text("// prepared runtime input\n")
        subprocess.run(["git", "init", "--initial-branch=main"], cwd=self.root,
                       check=True, capture_output=True, text=True)
        subprocess.run(["git", "config", "user.name", "runtime fixture"], cwd=self.root,
                       check=True)
        subprocess.run(["git", "config", "user.email", "fixture@example.invalid"],
                       cwd=self.root, check=True)
        subprocess.run(["git", "add", "app/Laplace.Decomposers/Fixture.cs"], cwd=self.root,
                       check=True)
        subprocess.run(["git", "commit", "-m", "prepared runtime"], cwd=self.root,
                       check=True, capture_output=True, text=True)
        revision = subprocess.run(["git", "rev-parse", "HEAD"], cwd=self.root,
                                  check=True, capture_output=True, text=True).stdout
        (self.root / "build/.laplace-source-revision").write_text(revision)

    def exercise(self, alternate):
        conventional = self.root / "app/Laplace.Cli/bin/Release/net10.0"
        configured = self.root / "alternate build/app/bin/Laplace.Cli/Release/net10.0"
        directory = configured if alternate else conventional
        directory.mkdir(parents=True)
        dll = directory / "Laplace.Cli.dll"
        native = directory / "liblaplace_core.so"
        dll.write_bytes(b"prepared managed fixture")
        native.write_bytes(self.native.read_bytes())
        env = dict(os.environ, FIXTURE_ROOT=str(self.root))
        env.pop("LAPLACE_BUILD_ROOT", None)
        if alternate:
            env["LAPLACE_BUILD_ROOT"] = str(self.root / "alternate build")
            conventional.mkdir(parents=True)
            (conventional / "Laplace.Cli.dll").write_bytes(b"wrong output decoy")
            (conventional / "liblaplace_core.so").write_bytes(self.native.read_bytes())
        script = r'''
set -euo pipefail
ROOT="$FIXTURE_ROOT"
dotnet() { printf 'unexpected compilation\n' >> "$ROOT/compile-events"; return 97; }
''' + self.selection + "\n" + self.guard + '\nrequire_cli\nprintf "%s\\n" "$DLL"\n'
        def run():
            return subprocess.run(["bash", "-c", script], env=env, text=True,
                                  capture_output=True, timeout=10)
        for _ in range(2):
            result = run()
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(str(dll), result.stdout.strip())
        original_dll, original_native = dll.read_bytes(), native.read_bytes()
        for missing in (dll, native, self.native):
            original = missing.read_bytes()
            missing.unlink()
            result = run()
            self.assertNotEqual(0, result.returncode)
            self.assertFalse(missing.exists(), "guard must never repair runtime artifacts")
            self.assertEqual("", result.stdout)
            missing.write_bytes(original)
        native.write_bytes(b"stale native fixture")
        result = run()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("differs from the prepared engine build", result.stderr)
        self.assertEqual(b"stale native fixture", native.read_bytes())
        native.write_bytes(original_native)
        self.assertEqual(0, run().returncode)
        self.assertEqual(original_dll, dll.read_bytes())
        self.assertFalse((self.root / "compile-events").exists())

        # An unrelated revision remains runtime-equivalent and must not force an
        # operator build. A production dependency change must fail before launch.
        docs = self.root / "docs/note.md"
        docs.parent.mkdir()
        docs.write_text("operator note\n")
        subprocess.run(["git", "add", "docs/note.md"], cwd=self.root, check=True)
        subprocess.run(["git", "commit", "-m", "docs only"], cwd=self.root,
                       check=True, capture_output=True, text=True)
        self.assertEqual(0, run().returncode)

        production = self.root / "app/Laplace.Decomposers/Fixture.cs"
        production.write_text("// changed runtime input\n")
        subprocess.run(["git", "add", str(production)], cwd=self.root, check=True)
        subprocess.run(["git", "commit", "-m", "runtime change"], cwd=self.root,
                       check=True, capture_output=True, text=True)
        stale = run()
        self.assertNotEqual(0, stale.returncode)
        self.assertIn("prepared ingest runtime is stale", stale.stderr)

    def test_default_prepared_cli_requires_exact_native_bytes_without_building(self):
        self.exercise(False)

    def test_configured_release_requires_its_own_closure_without_fallback(self):
        self.exercise(True)


if __name__ == "__main__":
    unittest.main(verbosity=2)
