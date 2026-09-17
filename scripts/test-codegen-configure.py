#!/usr/bin/env python3
"""Fresh configure and incremental generation use the real attestation-law owner."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SEEDS = ("seed_pos.sql.in", "seed_relation_types.sql.in")

class ConfigureGeneration(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="laplace-codegen-configure-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "source"
        self.build = Path(self.temp.name) / "build"
        for name in ("scripts", "engine/core/include/laplace/core", "engine/core/src/generated",
                     "engine/manifest", "extension/laplace_substrate/sql/generated"):
            (self.root / name).mkdir(parents=True, exist_ok=True)
        shutil.copyfile(ROOT / "scripts/codegen-attestation-law.py",
                        self.root / "scripts/codegen-attestation-law.py")
        for name in ("relation_types.toml", "pos_tags.toml"):
            shutil.copyfile(ROOT / "engine/manifest" / name, self.root / "engine/manifest" / name)
        real = (ROOT / "engine/core/CMakeLists.txt").read_text()
        start = real.index('set(LAPLACE_PERFCACHE_DIR ')
        end = real.index('add_custom_command(\n    OUTPUT  ${LAPLACE_CODEGEN_OUTPUTS}', start)
        # Execute the actual configure owner; the fixture omits unrelated native
        # compilation dependencies and retains the real generator and manifests.
        (self.root / "engine/core/CMakeLists.txt").write_text(real[start:end])
        checks = "\n".join('file(SHA256 "${CMAKE_SOURCE_DIR}/extension/laplace_substrate/sql/generated/'
                           + name + '" _seed_hash)' for name in SEEDS)
        (self.root / "CMakeLists.txt").write_text(
            'cmake_minimum_required(VERSION 3.20)\nproject(codegen_fixture NONE)\n'
            'add_subdirectory(engine/core)\n' + checks + '\n')

    def configure(self, succeeds=True):
        result = subprocess.run(["cmake", "-S", str(self.root), "-B", str(self.build)],
                                text=True, capture_output=True, timeout=45)
        if succeeds:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        else:
            self.assertNotEqual(result.returncode, 0)
        return result

    def test_fresh_configure_generates_seed_inputs_before_hashing(self):
        paths = [self.root / "extension/laplace_substrate/sql/generated" / name for name in SEEDS]
        self.assertTrue(all(not path.exists() for path in paths))
        self.configure()
        self.assertTrue(all(path.stat().st_size > 0 for path in paths))
        before = {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in paths}
        self.configure()
        self.assertEqual(before, {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in paths})
        # One missing untracked seed is regenerated without deleting a build tree.
        paths[0].unlink()
        self.configure()
        self.assertEqual(paths[0].read_bytes(), before[paths[0]][0])

    def test_generator_failure_fails_configuration(self):
        (self.root / "scripts/codegen-attestation-law.py").write_text(
            'raise SystemExit("controlled generator failure")\n')
        result = self.configure(succeeds=False)
        self.assertIn("Attestation-law generation failed", result.stdout + result.stderr)
        self.assertIn("controlled generator failure", result.stdout + result.stderr)

if __name__ == "__main__":
    unittest.main(verbosity=2)
