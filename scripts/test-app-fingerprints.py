#!/usr/bin/env python3
"""Exercise current publish identity and actual ingest build/closure ownership."""
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


def shell_function(path, name):
    match = re.search(r"^" + name + r"\(\) \{\n.*?^\}$", path.read_text(), re.M | re.S)
    if match is None:
        raise AssertionError(f"missing {name} in {path}")
    return match.group()


def ingest_cli_selection():
    # Execute the production output-path selection, including its build-root
    # override, rather than selecting a test-specific DLL before build_cli.
    match = re.search(r"^MANAGED_BUILD_ROOT=.*?^fi$",
                      (ROOT / "scripts/ingest-source.sh").read_text(), re.M | re.S)
    if match is None:
        raise AssertionError("missing ingest CLI output-path selection")
    return match.group()


class AppFingerprintTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="app-fingerprints-", dir=os.environ["TMPDIR"])
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.env = dict(os.environ)
        self.env.pop("LAPLACE_BUILD_ROOT", None)
        # Publish identity reads actual tracked sources and operational docs.
        # The removed affected-app planner is not part of this owner.
        for project in (ROOT / "app").glob("*/*.csproj"):
            destination = self.root / project.relative_to(ROOT)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(project, destination)
        destination = self.root / "scripts/lib/fp.sh"
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(ROOT / "scripts/lib/fp.sh", destination)
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

    def publish_fingerprint(self):
        return self.run_command("bash", "-c", r'''
set -euo pipefail
ROOT="$PWD"
source "$ROOT/scripts/lib/fp.sh"
''' + shell_function(ROOT / "scripts/pipeline.sh", "fp_publish") + "\nfp_publish\n")

    def build_cli(self, *, prepared=False, failure=0, build_root=None):
        # The child pipeline is an execution-protocol fixture. It does not
        # claim to build a real native library or managed assembly.
        environment = dict(self.env, LAPLACE_INGEST_RUNTIME_PREPARED="1" if prepared else "0",
                           PIPELINE_FAILURE=str(failure))
        if build_root is not None:
            environment["LAPLACE_BUILD_ROOT"] = str(build_root)
        return subprocess.run(["bash", "-c", r'''
set -euo pipefail
ROOT="$PWD"
''' + ingest_cli_selection() + "\n" +
                              shell_function(ROOT / "scripts/ingest-source.sh", "build_cli") +
                              "\nbuild_cli\n"],
                              cwd=self.root, env=environment, text=True,
                              capture_output=True, timeout=20)

    def exercise_build_owner(self, *, alternate):
        build_root = self.root / "selected build root" if alternate else None
        app = ((build_root / "app/bin/Laplace.Cli/Release/net10.0") if alternate else
               (self.root / "app/Laplace.Cli/bin/Release/net10.0"))
        pipeline = self.root / "scripts/pipeline.sh"
        pipeline.write_text(r'''#!/usr/bin/env bash
set -euo pipefail
[[ "$#" -eq 1 && "$1" == build ]] || exit 98
printf 'build\n' >> "$PWD/build-calls"
[[ "$PIPELINE_FAILURE" -eq 0 ]] || exit "$PIPELINE_FAILURE"
if [[ -n "${LAPLACE_BUILD_ROOT:-}" ]]; then
  destination="$LAPLACE_BUILD_ROOT/app/bin/Laplace.Cli/Release/net10.0"
else
  destination="$PWD/app/Laplace.Cli/bin/Release/net10.0"
fi
mkdir -p "$destination" "$PWD/build/engine/core"
printf 'protocol-only native fixture\n' > "$PWD/build/engine/core/liblaplace_core.so"
cp "$PWD/build/engine/core/liblaplace_core.so" "$destination/liblaplace_core.so"
printf 'protocol-only CLI fixture\n' > "$destination/Laplace.Cli.dll"
''')
        for _ in range(2):
            result = self.build_cli(build_root=build_root)
            self.assertEqual(0, result.returncode, result.stderr)
        calls = self.root / "build-calls"
        # Every unprepared invocation delegates to the ordinary build owner.
        # Prepared execution validates the exact selected closure without a build.
        self.assertEqual(["build", "build"], calls.read_text().splitlines())
        self.assertTrue((app / "Laplace.Cli.dll").is_file())
        self.assertEqual((self.root / "build/engine/core/liblaplace_core.so").read_bytes(),
                         (app / "liblaplace_core.so").read_bytes())
        result = self.build_cli(prepared=True, build_root=build_root)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["build", "build"], calls.read_text().splitlines())

        if alternate:
            # A valid artifact in the conventional directory must not mask a
            # missing/stale artifact at the explicitly selected build root.
            default = self.root / "app/Laplace.Cli/bin/Release/net10.0"
            default.mkdir(parents=True, exist_ok=True)
            for name in ("Laplace.Cli.dll", "liblaplace_core.so"):
                shutil.copy2(app / name, default / name)
        (app / "liblaplace_core.so").write_bytes(b"stale native fixture")
        refused = self.build_cli(prepared=True, build_root=build_root)
        self.assertNotEqual(0, refused.returncode)
        self.assertIn("native closure is absent or stale", refused.stderr)
        self.assertIn(str(app / "liblaplace_core.so"), refused.stderr)
        (app / "liblaplace_core.so").unlink()
        absent = self.build_cli(prepared=True, build_root=build_root)
        self.assertNotEqual(0, absent.returncode)
        self.assertIn("native closure is absent or stale", absent.stderr)
        (app / "Laplace.Cli.dll").unlink()
        missing = self.build_cli(prepared=True, build_root=build_root)
        self.assertNotEqual(0, missing.returncode)
        self.assertIn("CLI artifact missing", missing.stderr)
        self.assertIn(str(app / "Laplace.Cli.dll"), missing.stderr)
        failed = self.build_cli(failure=47, build_root=build_root)
        self.assertEqual(47, failed.returncode)
        self.assertFalse((app / "Laplace.Cli.dll").exists())
        self.assertEqual(["build", "build", "build"], calls.read_text().splitlines())
        retry = self.build_cli(build_root=build_root)
        self.assertEqual(0, retry.returncode, retry.stderr)
        self.assertEqual(["build"] * 4, calls.read_text().splitlines())

    def test_ingest_delegates_build_then_requires_exact_native_closure(self):
        self.exercise_build_owner(alternate=False)

    def test_ingest_selects_alternate_build_root_without_default_fallback(self):
        self.exercise_build_owner(alternate=True)

    def test_operational_docs_edits_change_publish_identity(self):
        baseline = self.publish_fingerprint()
        self.assertRegex(baseline, r"^[0-9a-f]{64}$")
        self.assertEqual(baseline, self.publish_fingerprint())
        for path in (self.seed, self.root / "docs/INVENTION.md", self.root / "docs/INVENTIONS.md"):
            with self.subTest(path=path.relative_to(self.root)):
                original = path.read_bytes()
                try:
                    path.write_bytes(original + b"\nSource revision for publish identity test.\n")
                    changed = self.publish_fingerprint()
                    self.assertNotEqual(baseline, changed)
                    self.assertEqual(changed, self.publish_fingerprint())
                finally:
                    path.write_bytes(original)
                self.assertEqual(baseline, self.publish_fingerprint())

    def test_missing_operational_source_and_restoration_change_publish_identity(self):
        baseline = self.publish_fingerprint()
        original = self.seed.read_bytes()
        self.seed.unlink()
        missing = self.publish_fingerprint()
        self.assertNotEqual(baseline, missing)
        self.assertEqual(missing, self.publish_fingerprint())
        self.seed.write_bytes(original)
        self.assertEqual(baseline, self.publish_fingerprint())


if __name__ == "__main__":
    unittest.main(verbosity=2)
