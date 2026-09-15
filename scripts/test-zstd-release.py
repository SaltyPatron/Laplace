#!/usr/bin/env python3
"""Source selection, failure preservation and native-library receipt contracts."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("zstd_installer", ROOT / "scripts/install-zstd.py")
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class SourceFixture(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix="zstd-release-", dir=os.environ.get("TMPDIR", "/build/laplace/work"))
        self.addCleanup(self.scratch.cleanup)
        self.base = Path(self.scratch.name)
        self.source = self.base / "external/zstd"
        self.source.mkdir(parents=True)
        self.git("init", "--quiet")
        self.git("config", "user.name", "Source test")
        self.git("config", "user.email", "source-test@localhost")
        self.git("remote", "add", "origin", "https://github.com/facebook/zstd.git")
        (self.source / "lib").mkdir()
        (self.source / "lib/zstd.h").write_text("#define ZSTD_VERSION_MAJOR 1\n#define ZSTD_VERSION_MINOR 5\n#define ZSTD_VERSION_RELEASE 7\n")
        (self.source / "LICENSE").write_text("Fixture BSD license\n")
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", "Fixture source")
        commit = self.git("rev-parse", "HEAD")
        self.git("tag", "v1.5.7")
        self.lock = {"version": "1.5.7", "tag": "v1.5.7", "commit": commit,
                     "repository": "https://github.com/facebook/zstd.git", "latest_api": "https://api.github.com/repos/facebook/zstd/releases/latest",
                     "license_sha256": {"LICENSE": installer.digest(self.source / "LICENSE")}}
        self.patches = [patch.object(installer, "load_lock", return_value=self.lock),
                        patch.object(installer, "external_root", return_value=self.source.parent)]
        for item in self.patches:
            item.start()
            self.addCleanup(item.stop)

    def git(self, *args):
        return subprocess.run(["git", "-C", str(self.source), *args], capture_output=True, text=True, check=True).stdout.strip()


class SourceTests(SourceFixture):
    def test_selected_official_clean_source_is_used_directly(self):
        installer.update_source(self.source, self.lock)
        self.assertEqual(self.lock["commit"], self.git("rev-parse", "HEAD"))
        self.assertEqual("", self.git("status", "--porcelain", "--untracked-files=all"))

    def test_tracked_and_untracked_operator_work_is_preserved(self):
        for name in ("lib/zstd.h", "operator-notes.txt"):
            with self.subTest(name=name):
                target = self.source / name
                original = target.read_bytes() if target.exists() else None
                target.write_bytes(b"operator edit\n")
                before = self.git("rev-parse", "HEAD")
                with self.assertRaisesRegex(ValueError, "local changes"):
                    installer.update_source(self.source, self.lock)
                self.assertEqual(b"operator edit\n", target.read_bytes())
                self.assertEqual(before, self.git("rev-parse", "HEAD"))
                if original is None:
                    target.unlink()
                else:
                    target.write_bytes(original)

    def test_wrong_origin_and_wrong_release_tag_never_checkout(self):
        self.git("remote", "set-url", "origin", "https://github.com/example/zstd.git")
        with self.assertRaisesRegex(ValueError, "official facebook/zstd"):
            installer.update_source(self.source, self.lock)
        self.git("remote", "set-url", "origin", self.lock["repository"])
        wrong = dict(self.lock, commit="0" * 40)
        with self.assertRaisesRegex(ValueError, "tag does not match"):
            installer.update_source(self.source, wrong)
        self.assertEqual(self.lock["commit"], self.git("rev-parse", "HEAD"))

    def test_update_preserves_detached_previous_tip(self):
        (self.source / "operator-feature").write_text("preserve this source revision\n")
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", "Later operator revision")
        previous = self.git("rev-parse", "HEAD")
        self.git("checkout", "--detach", previous)
        installer.update_source(self.source, self.lock)
        self.assertEqual(previous, self.git("rev-parse", "refs/laplace/zstd-before-update/" + previous))
        self.assertEqual(self.lock["commit"], self.git("rev-parse", "HEAD"))
        self.assertEqual("preserve this source revision", self.git("show", previous + ":operator-feature"))

    def test_external_pin_preserves_inode_mode_and_other_sources(self):
        pins = self.source.parent / "PINS.tsv"
        pins.write_text("external/stockfish\tupstream\tother-commit\nexternal/zstd\told\told\n")
        pins.chmod(0o664)
        before = pins.stat()
        installer.record_pin(self.source, self.lock)
        after = pins.stat()
        self.assertEqual((before.st_ino, before.st_uid, before.st_gid, before.st_mode), (after.st_ino, after.st_uid, after.st_gid, after.st_mode))
        self.assertIn("external/stockfish\tupstream\tother-commit", pins.read_text())
        self.assertEqual(1, pins.read_text().count("external/zstd"))
        self.assertIn(self.lock["commit"], pins.read_text())

    def test_latest_check_rejects_stale_or_retagged_release(self):
        for release, commit in (("v1.5.8", self.lock["commit"]), ("v1.5.7", "0" * 40)):
            with self.subTest(release=release, commit=commit):
                with patch.object(installer, "github_json", side_effect=[{"tag_name": release}, {"sha": commit}]):
                    with self.assertRaises(ValueError):
                        installer.check_latest(self.lock)
        with patch.object(installer, "github_json", side_effect=[{"tag_name": "v1.5.7"}, {"sha": self.lock["commit"]}]):
            self.assertTrue(installer.check_latest(self.lock)["current"])


class BuildTests(SourceFixture):
    def setUp(self):
        super().setUp()
        self.builds = self.base / "builds"
        self.configure_calls = []
        original_run = installer.run
        def command(argv, **kwargs):
            if str(argv[0]) != "fixture-cmake":
                return original_run(argv, **kwargs)
            if "-S" in argv:
                output = Path(argv[argv.index("-B") + 1])
                self.configure_calls.append(output)
                output.mkdir(parents=True, exist_ok=True)
                (output / "CMakeCache.txt").write_text("CMAKE_C_COMPILER:FILEPATH=/fixture/cc\nCMAKE_GENERATOR:INTERNAL=Fixture\n")
                metadata = output / "CMakeFiles/4.0"
                metadata.mkdir(parents=True)
                for language in ("C", "CXX"):
                    (metadata / ("CMake" + language + "Compiler.cmake")).write_text('set(CMAKE_' + language + '_COMPILER_ID "Fixture")\n')
            else:
                output = Path(argv[argv.index("--build") + 1])
                (output / "runtime").mkdir()
                (output / "runtime/libzstd.so.1.5.7").write_bytes(b"fixture library bytes")
            return "fixture cmake completed"
        def native_probe(path, lock):
            return {"version": lock["version"], "library": str(path), "library_sha256": installer.digest(path), "decoded_bytes": 151}
        for item in (patch.object(installer, "run", side_effect=command),
                     patch.object(installer, "recipe_inputs", return_value={"fixture": "recipe"}),
                     patch.object(installer, "probe", side_effect=native_probe),
                     patch.object(installer.platform, "system", return_value="Linux")):
            item.start()
            self.addCleanup(item.stop)

    def build(self, **kwargs):
        return installer.build(self.source, self.builds, 2, "fixture-cmake", **kwargs)

    def test_build_retains_receipt_licenses_and_reuses_verified_library(self):
        library = self.build()
        self.assertTrue(library.is_relative_to(self.builds))
        self.assertEqual("Fixture BSD license\n", (library.parent / "LICENSE").read_text())
        self.assertEqual(library, self.build())
        self.assertEqual(1, len(self.configure_calls))
        receipt = installer.read_receipt(self.source, self.lock)
        self.assertEqual(self.lock["commit"], receipt["source_commit"])
        self.assertEqual(installer.digest(library), receipt["library_sha256"])
        self.assertEqual("", self.git("status", "--porcelain", "--untracked-files=all"))

    def test_failed_rebuild_preserves_prior_library_and_selection(self):
        previous = self.build()
        state = installer.git_metadata(self.source, "laplace-zstd-build.json")
        original = state.read_bytes()
        with patch.object(installer, "probe", side_effect=RuntimeError("native fixture mismatch")):
            with self.assertRaisesRegex(RuntimeError, "native fixture mismatch"):
                self.build(rebuild=True)
        self.assertEqual(original, state.read_bytes())
        self.assertEqual(b"fixture library bytes", previous.read_bytes())
        self.assertEqual(2, len(list(self.builds.iterdir())))

    def test_corrupted_library_cannot_reuse_receipt(self):
        previous = self.build()
        previous.write_bytes(b"changed library")
        with self.assertRaisesRegex(ValueError, "changed since"):
            installer.read_receipt(self.source, self.lock)
        current = self.build()
        self.assertNotEqual(previous, current)
        self.assertEqual(b"changed library", previous.read_bytes())

    def test_configured_source_path_readback_does_not_build(self):
        library = self.build()
        with patch.dict(os.environ, {"LAPLACE_ZSTD_LIBRARY": ""}):
            self.assertEqual(library, installer.configured_library(self.source))
        self.assertEqual(1, len(self.configure_calls))

    def test_persisted_source_selection_and_explicit_override(self):
        library = self.build()
        prefix = self.base / "installed"
        (prefix / "app").mkdir(parents=True)
        (prefix / "app/laplace-api.env").write_text(f"LAPLACE_ZSTD_SOURCE={self.source}\nLAPLACE_ZSTD_LIBRARY=/old/runtime/libzstd.so\n")
        with patch.dict(os.environ, {"LAPLACE_INSTALL_PREFIX": str(prefix), "LAPLACE_ZSTD_SOURCE": "", "LAPLACE_ZSTD_LIBRARY": ""}):
            self.assertEqual(self.source, installer.source_root())
            # Explicit source selection takes precedence over a persisted binary.
            with patch.dict(os.environ, {"LAPLACE_ZSTD_SOURCE": str(self.source)}):
                self.assertEqual(library, installer.configured_library())
            self.assertEqual(library, installer.configured_library(self.source))

    def test_zero_jobs_and_build_inside_source_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "positive"):
            installer.build(self.source, self.builds, 0, "fixture-cmake")
        with self.assertRaisesRegex(ValueError, "outside its source"):
            installer.build(self.source, self.source / "local-build", 2, "fixture-cmake")


class PlatformTests(unittest.TestCase):
    def test_windows_cmake_builds_shared_dll_and_static_crt(self):
        args = installer.configure_arguments("cmake", Path("source"), Path("build"), windows=True)
        self.assertIn("-DZSTD_BUILD_SHARED=ON", args)
        self.assertIn("-DZSTD_BUILD_STATIC=OFF", args)
        self.assertIn("-DZSTD_USE_STATIC_RUNTIME=ON", args)
        self.assertIn("-DCMAKE_RUNTIME_OUTPUT_DIRECTORY_RELEASE=build/runtime", args)
        linux = installer.configure_arguments("cmake", Path("source"), Path("build"), windows=False)
        self.assertNotIn("-DZSTD_USE_STATIC_RUNTIME=ON", linux)

    def test_library_discovery_is_platform_specific_and_unambiguous(self):
        with tempfile.TemporaryDirectory(prefix="zstd-platform-", dir=os.environ.get("TMPDIR", "/build/laplace/work")) as temporary:
            output = Path(temporary)
            (output / "runtime").mkdir()
            for system, name in (("Windows", "zstd.dll"), ("Linux", "libzstd.so.1.5.7"), ("Darwin", "libzstd.1.5.7.dylib")):
                with self.subTest(system=system):
                    path = output / "runtime" / name
                    path.write_bytes(b"library")
                    self.assertEqual(path, installer.selected_library(output, "1.5.7", system))
            (output / "runtime/libzstd.dll").write_bytes(b"second dll")
            with self.assertRaisesRegex(ValueError, "exactly one"):
                installer.selected_library(output, "1.5.7", "Windows")


if __name__ == "__main__":
    unittest.main(verbosity=2)
