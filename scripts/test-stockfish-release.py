#!/usr/bin/env python3
"""Stockfish source update/build contracts; isolated git repos and UCI peers only."""
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("stockfish_install", ROOT / "scripts/install-stockfish.py")
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)

PROGRAM = ("#!/bin/sh\nwhile IFS= read -r command; do\ncase \"$command\" in\n"
                   "uci) printf 'id name Stockfish 19\\n"
                   "option name Threads type spin default 1 min 1 max 1024\\n"
                   "option name Hash type spin default 16 min 1 max 1024\\n"
                   "option name UCI_LimitStrength type check default false\\n"
                   "option name UCI_Elo type spin default 3190 min 1320 max 3190\\nuciok\\n';;\n"
                   "isready) printf 'readyok\\n';;\n"
                   "'go depth 1') printf 'info depth 1 score cp 10\\nbestmove e2e4\\n';;\n"
                   "quit) exit 0;;\nesac\ndone\n").encode()

class StockfishSourceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="stockfish-source-contract-")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.source = self.base / "external/stockfish"
        self.source.mkdir(parents=True)
        self.original_root = installer.ROOT
        installer.ROOT = self.base
        self.addCleanup(setattr, installer, "ROOT", self.original_root)
        self.environment = patch.dict(os.environ, {
            "LAPLACE_EXTERNAL": str(self.base / "external"),
            "LAPLACE_INSTALL_PREFIX": str(self.base / "install"),
            "LAPLACE_STOCKFISH_SOURCE": "", "LAPLACE_STOCKFISH": ""})
        self.environment.start()
        self.addCleanup(self.environment.stop)
        self.git("init", "--quiet")
        self.git("config", "user.name", "Stockfish contract")
        self.git("config", "user.email", "stockfish-contract@example.invalid")
        self.git("remote", "add", "origin", "https://github.com/official-stockfish/Stockfish.git")
        (self.source / "src").mkdir()
        (self.source / "src/source.cpp").write_text("release source\n")
        (self.source / "src/source.h").write_text("release header\n")
        (self.source / "scripts").mkdir()
        native_script = self.source / "scripts/get_native_properties.sh"
        native_script.write_text("#!/bin/sh\nprintf 'x86-64-avx2\\n'\n")
        native_script.chmod(0o755)
        (self.source / ".gitignore").write_text("/src/stockfish\n/src/*.nnue\n")
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", "fixture release")
        self.commit = self.git("rev-parse", "HEAD")
        self.git("tag", "sf_19")
        self.lock = {"version": "19", "tag": "sf_19", "commit": self.commit,
                     "repository": "https://github.com/official-stockfish/Stockfish.git"}
        (self.base / "deploy/linux").mkdir(parents=True)
        (self.base / "deploy/linux/stockfish-release.json").write_text(json.dumps(self.lock))

    def git(self, *args):
        return subprocess.run(["git", "-C", str(self.source), *args],
                              text=True, capture_output=True, check=True).stdout.strip()

    def executable(self, program=PROGRAM):
        binary = self.source / "src/stockfish"
        binary.write_bytes(program)
        binary.chmod(0o755)
        return binary

    def test_source_selection_uses_existing_external_tree(self):
        self.assertEqual(self.source, installer.source_root())
        self.assertEqual(self.source / "src/stockfish", installer.binary_path())
        with patch.dict(os.environ, {"LAPLACE_STOCKFISH_SOURCE": str(self.base / "own-checkout")}):
            self.assertEqual(self.base / "own-checkout", installer.source_root())

    def test_source_selection_retains_installed_path_across_later_invocations(self):
        config = self.base / "install/app/laplace-api.env"
        config.parent.mkdir(parents=True)
        config.write_text('LICHESS_API=must-not-be-read\n'
                          'LAPLACE_STOCKFISH_SOURCE=/obsolete\n'
                          'LAPLACE_STOCKFISH_SOURCE="' + str(self.source) + '"\n')
        self.assertEqual((self.source, "installed-service"), installer.source_selection())
        self.assertEqual(self.source / "src/stockfish", installer.binary_path())
        with patch.dict(os.environ, {"LAPLACE_STOCKFISH_SOURCE": str(self.base / "explicit")}):
            self.assertEqual((self.base / "explicit", "environment"), installer.source_selection())
            self.assertEqual((self.source, "command-line"), installer.source_selection(self.source))
        self.assertEqual((self.source, "installed-service"), installer.source_selection())

    def test_explicit_missing_source_cannot_create_or_clone_a_checkout(self):
        missing = self.base / "local/SF_19"
        for explicit in (True, False):
            with self.subTest(command_line=explicit):
                environment = {} if explicit else {"LAPLACE_STOCKFISH_SOURCE": str(missing)}
                with patch.dict(os.environ, environment), patch.object(installer.subprocess, "run") as run:
                    with self.assertRaisesRegex(ValueError, "existing directory"):
                        installer.build(missing if explicit else None, jobs=1)
                run.assert_not_called()
                self.assertFalse(missing.parent.exists())

    def test_host_receipt_keeps_actual_checkout_when_requested_local_path_is_absent(self):
        missing = self.base / "local/SF_19"
        observed = installer.host_source_receipt(missing)
        self.assertFalse(observed["requested"]["available"])
        self.assertEqual("requested-local-path-unavailable",
                         observed["requested"]["reason"])
        self.assertEqual(str(self.source), observed["configured_source"])
        self.assertEqual(str(self.source.resolve()), observed["source"])
        self.assertEqual(str(self.source.resolve()), observed["git_root"])
        self.assertEqual(self.commit, observed["commit"])
        self.assertEqual("external-default", observed["selection"])
        self.assertFalse(missing.parent.exists())

    def test_uninstalled_default_is_receipted_as_pending_without_creating_a_checkout(self):
        self.source.rename(self.base / "preserved-fixture")
        with patch.object(installer.subprocess, "run") as run:
            observed = installer.host_source_receipt(self.base / "absent-local/SF_19")
        run.assert_not_called()
        self.assertEqual("external-default", observed["selection"])
        self.assertEqual("pending-default-install", observed["checkout_status"])
        self.assertEqual(str(self.source.resolve()), observed["source"])
        self.assertIsNone(observed["git_root"])
        self.assertIsNone(observed["origin"])
        self.assertIsNone(observed["commit"])
        self.assertEqual(self.lock["repository"], observed["planned_repository"])
        self.assertFalse(self.source.exists())

    @unittest.skipIf(os.name == "nt", "POSIX symlink path validation")
    def test_resolved_checkout_path_cannot_inject_environment_assignments(self):
        target = self.base / "source\nINJECTED=value"
        self.source.rename(target)
        self.source.symlink_to(target, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "one environment assignment"):
            installer.existing_source(self.source)

    def test_host_receipt_selects_real_requested_checkout_without_copying_it(self):
        preferred = self.base / "local/SF_19 with spaces"
        preferred.parent.mkdir()
        self.source.rename(preferred)
        observed = installer.host_source_receipt(preferred)
        self.assertTrue(observed["requested"]["available"])
        self.assertEqual("requested-existing-local-checkout", observed["selection"])
        self.assertEqual(str(preferred.resolve()), observed["source"])
        self.assertEqual(str(preferred.resolve() / "src/stockfish"), observed["executable"])
        self.assertEqual(self.commit, observed["commit"])
        self.assertFalse(self.source.exists())

    def test_host_preference_never_overrides_explicit_source_or_accepts_unofficial_origin(self):
        preferred = self.base / "local/SF_19"
        subprocess.run(["git", "clone", "--quiet", str(self.source), str(preferred)], check=True)
        observed = installer.host_source_receipt(preferred)
        self.assertFalse(observed["requested"]["available"])
        self.assertEqual(str(self.source.resolve()), observed["source"])
        subprocess.run(["git", "-C", str(preferred), "remote", "set-url", "origin",
                        self.lock["repository"]], check=True)
        with patch.dict(os.environ, {"LAPLACE_STOCKFISH_SOURCE": str(self.source)}):
            observed = installer.host_source_receipt(preferred)
            self.assertTrue(observed["requested"]["available"])
            self.assertEqual("environment", observed["selection"])
            self.assertEqual(str(self.source.resolve()), observed["source"])
        with patch.dict(os.environ, {"LAPLACE_STOCKFISH_SOURCE": str(self.base / "missing")}):
            with self.assertRaisesRegex(ValueError, "existing directory"):
                installer.host_source_receipt(preferred)

    @unittest.skipIf(os.name == "nt", "Linux bootstrap environment persistence")
    def test_bootstrap_republication_keeps_selected_source_without_reexporting_it(self):
        script = (ROOT / "scripts/bootstrap-chess-lab.sh").read_text()
        script = script.replace('SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"',
                                'SCRIPT_DIR="$SOURCE_SELECTION_TEST_SCRIPTS"')
        script = script.replace('main "$@"', 'id() { printf "1\n"; }\nwrite_api_env')
        prefix = self.base / "install"
        (prefix / "app").mkdir(parents=True)
        (prefix / "app/laplace-api.env").write_text("PRESERVED_SETTING=unchanged\n")
        (prefix / "bin").mkdir()
        cutechess = prefix / "bin/cutechess-cli"
        cutechess.write_text("#!/bin/sh\nexit 0\n")
        cutechess.chmod(0o755)
        environment = dict(os.environ, SOURCE_SELECTION_TEST_SCRIPTS=str(ROOT / "scripts"),
                           LAPLACE_STOCKFISH_SOURCE=str(self.source))
        subprocess.run(["bash", "-c", script], env=environment, check=True,
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        environment.pop("LAPLACE_STOCKFISH_SOURCE")
        environment["LAPLACE_EXTERNAL"] = str(self.base / "different-external")
        subprocess.run(["bash", "-c", script], env=environment, check=True,
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        content = (prefix / "app/laplace-api.env").read_text()
        self.assertEqual(1, content.count("LAPLACE_STOCKFISH_SOURCE="))
        self.assertIn("LAPLACE_STOCKFISH_SOURCE=" + str(self.source) + "\n", content)
        self.assertIn("LAPLACE_STOCKFISH=" + str(self.source / "src/stockfish") + "\n", content)
        self.assertIn("PRESERVED_SETTING=unchanged\n", content)
        self.assertFalse((self.base / "different-external").exists())

    def test_clean_pinned_checkout_is_reused_without_network(self):
        self.assertEqual(self.commit, installer.update_source(self.source, self.lock))
        self.assertEqual(self.commit, self.git("rev-parse", "HEAD"))

    def test_dirty_and_untracked_source_files_are_preserved(self):
        for filename in ("src/source.cpp", "operator-note.txt"):
            with self.subTest(filename=filename):
                path = self.source / filename
                path.write_text("operator changes\n")
                with self.assertRaisesRegex(ValueError, "local changes"):
                    installer.update_source(self.source, self.lock)
                self.assertEqual("operator changes\n", path.read_text())
                self.assertEqual(self.commit, self.git("rev-parse", "HEAD"))
                if filename == "src/source.cpp":
                    path.write_text("release source\n")
                else:
                    path.unlink()

    def test_prior_branch_tip_remains_reachable_after_update(self):
        (self.source / "src/source.cpp").write_text("local committed implementation\n")
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", "preserved local branch")
        previous = self.git("rev-parse", "HEAD")
        installer.update_source(self.source, self.lock)
        self.assertEqual(self.commit, self.git("rev-parse", "HEAD"))
        self.assertEqual(previous, self.git("rev-parse", "refs/laplace/stockfish-before-update/" + previous))

    def test_hidden_tracked_bytes_are_rejected_and_preserved(self):
        for flag, filename in (("--assume-unchanged", "src/source.cpp"),
                               ("--assume-unchanged", "src/source.h"),
                               ("--skip-worktree", "src/source.h")):
            with self.subTest(flag=flag, filename=filename):
                path = self.source / filename
                original = path.read_bytes()
                self.git("update-index", flag, filename)
                path.write_bytes(b"hidden operator change\n")
                self.assertEqual("", self.git("status", "--porcelain"))
                with self.assertRaisesRegex(ValueError, "local byte changes"):
                    installer.update_source(self.source, self.lock)
                self.assertEqual(b"hidden operator change\n", path.read_bytes())
                self.assertEqual(self.commit, self.git("rev-parse", "HEAD"))
                path.write_bytes(original)
                self.git("update-index", flag.replace("--", "--no-", 1), filename)

    @unittest.skipIf(os.name == "nt", "POSIX executable mode is not represented on Windows")
    def test_hidden_executable_mode_changes_are_rejected(self):
        self.git("config", "core.fileMode", "false")
        for filename, changed_mode in (("src/source.h", 0o755),
                                       ("scripts/get_native_properties.sh", 0o644)):
            with self.subTest(filename=filename):
                path = self.source / filename
                original_mode = path.stat().st_mode
                path.chmod(changed_mode)
                self.assertEqual("", self.git("status", "--porcelain"))
                with self.assertRaisesRegex(ValueError, "file type/mode changes"):
                    installer.update_source(self.source, self.lock)
                self.assertEqual(changed_mode, path.stat().st_mode & 0o777)
                path.chmod(original_mode)

    def test_blob_replacements_cannot_redefine_official_source(self):
        original_blob = self.git("rev-parse", "HEAD:src/source.h")
        changed = self.base / "replacement"
        changed.write_bytes(b"replacement header\n")
        replacement_blob = self.git("hash-object", "-w", str(changed))
        self.git("replace", original_blob, replacement_blob)
        # Genuine committed bytes remain admissible with replacement refs
        # present; replacement bytes cannot impersonate that same object id.
        installer.verify_source(self.source, self.commit)
        self.git("update-index", "--assume-unchanged", "src/source.h")
        (self.source / "src/source.h").write_bytes(changed.read_bytes())
        self.assertEqual("", self.git("status", "--porcelain"))
        with self.assertRaisesRegex(ValueError, "local byte changes"):
            installer.update_source(self.source, self.lock)
        self.assertEqual(changed.read_bytes(), (self.source / "src/source.h").read_bytes())

    def test_commit_replacements_cannot_redefine_official_tree(self):
        (self.source / "src/source.h").write_text("replacement header\n")
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", "replacement commit")
        replacement = self.git("rev-parse", "HEAD")
        self.git("replace", self.commit, replacement)
        self.git("update-ref", "HEAD", self.commit)
        self.assertEqual("", self.git("status", "--porcelain"))
        with self.assertRaisesRegex(ValueError, "local byte changes"):
            installer.verify_source(self.source, self.commit)
        self.assertEqual("replacement header\n", (self.source / "src/source.h").read_text())

    def test_clean_filter_cannot_hide_different_compiler_input_bytes(self):
        (self.source / ".gitattributes").write_text("src/source.h filter=header\n")
        self.git("config", "filter.header.clean", "sed s/operator/release/g")
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", "filter fixture")
        commit = self.git("rev-parse", "HEAD")
        (self.source / "src/source.h").write_text("operator header\n")
        self.git("add", "src/source.h")
        self.assertEqual("", self.git("status", "--porcelain"))
        with self.assertRaisesRegex(ValueError, "local byte changes"):
            installer.verify_source(self.source, commit)

    def build_peer(self, mutation=None):
        original_run = installer.subprocess.run
        calls = []

        def run(args, **kwargs):
            if args[0] == "g++":
                return subprocess.CompletedProcess(args, 0, stdout="gcc fixture\n")
            if args[0] == "make":
                calls.append(args)
                self.assertEqual("1", kwargs["env"]["GIT_NO_REPLACE_OBJECTS"])
                binary = installer.binary_path(self.source)
                candidate = binary.with_name(next(arg.split("=", 1)[1] for arg in args if arg.startswith("EXE=")))
                candidate.write_bytes(PROGRAM)
                candidate.chmod(0o755)
                if mutation:
                    mutation()
                return subprocess.CompletedProcess(args, 0)
            return original_run(args, **kwargs)

        return run, calls

    def test_successful_build_is_receipted_and_reused_after_full_source_check(self):
        run, calls = self.build_peer()
        with patch.object(installer.subprocess, "run", side_effect=run):
            installer.build(self.source, jobs=1)
            installer.build(self.source, jobs=1)
        self.assertEqual(1, len(calls))
        state = json.loads((self.source / ".git/laplace-stockfish-build.json").read_text())
        self.assertEqual("git-committed-bytes-and-modes-v1", state["recipe"]["source_integrity"])
        self.assertEqual(installer.digest(installer.binary_path(self.source)), state["binary_sha256"])

    def test_built_source_receipt_binds_actual_checkout_and_executable(self):
        selected = self.base / "source-selection.json"
        selected.write_text(json.dumps(installer.host_source_receipt()))
        run, _ = self.build_peer()
        with patch.object(installer.subprocess, "run", side_effect=run):
            installer.build(self.source, jobs=1)
        observed = installer.verify_source_receipt(selected)
        self.assertEqual(str(self.source.resolve()), observed["source"])
        self.assertEqual(self.commit, observed["build_verification"]["commit"])
        self.assertEqual(installer.digest(self.source / "src/stockfish"),
                         observed["build_verification"]["binary_sha256"])
        (self.source / "src/stockfish").write_bytes(b"different executable")
        with self.assertRaisesRegex(ValueError, "build receipt"):
            installer.verify_source_receipt(selected)

    def test_built_source_receipt_rejects_a_different_checkout_before_build_readback(self):
        selected = self.base / "source-selection.json"
        observed = installer.host_source_receipt()
        observed["source"] = str(self.base / "other-source")
        selected.write_text(json.dumps(observed))
        with self.assertRaisesRegex(ValueError, "retained host selection"):
            installer.verify_source_receipt(selected)

    def test_old_receipt_without_source_integrity_requires_rebuild(self):
        run, calls = self.build_peer()
        with patch.object(installer.subprocess, "run", side_effect=run):
            installer.build(self.source, jobs=1)
            path = self.source / ".git/laplace-stockfish-build.json"
            state = json.loads(path.read_text())
            del state["recipe"]["source_integrity"]
            path.write_text(json.dumps(state))
            installer.build(self.source, jobs=1)
        self.assertEqual(2, len(calls))

    def test_during_build_source_changes_prevent_promotion_or_official_receipt(self):
        binary = self.executable(PROGRAM + b"# prior executable\n")
        previous = binary.read_bytes()
        self.git("update-index", "--assume-unchanged", "src/source.h")
        header = self.source / "src/source.h"
        run, calls = self.build_peer(lambda: header.write_bytes(b"during build mutation\n"))
        with patch.object(installer.subprocess, "run", side_effect=run):
            with self.assertRaisesRegex(ValueError, "local byte changes"):
                installer.build(self.source, jobs=1)
        self.assertEqual(1, len(calls))
        self.assertEqual(previous, binary.read_bytes())
        self.assertEqual(b"during build mutation\n", header.read_bytes())
        self.assertFalse((self.source / "src/stockfish.pending").exists())
        self.assertFalse((self.source / ".git/laplace-stockfish-build.json").exists())
        self.assertFalse((self.source.parent / "PINS.tsv").exists())

    def test_during_probe_source_changes_prevent_cached_receipt_success(self):
        run, _ = self.build_peer()
        with patch.object(installer.subprocess, "run", side_effect=run):
            installer.build(self.source, jobs=1)
            self.git("update-index", "--assume-unchanged", "src/source.h")
            old_probe = installer.probe

            def probe(binary, version):
                result = old_probe(binary, version)
                (self.source / "src/source.h").write_bytes(b"during cached probe mutation\n")
                return result

            with patch.object(installer, "probe", side_effect=probe):
                with self.assertRaisesRegex(ValueError, "local byte changes"):
                    installer.build(self.source, jobs=1)

    @unittest.skipIf(os.name == "nt", "POSIX executable mode is not represented on Windows")
    def test_during_rebuild_mode_change_preserves_previous_binary_and_receipt(self):
        run, _ = self.build_peer()
        with patch.object(installer.subprocess, "run", side_effect=run):
            installer.build(self.source, jobs=1)
        binary = installer.binary_path(self.source)
        state = self.source / ".git/laplace-stockfish-build.json"
        previous_binary, previous_state = binary.read_bytes(), state.read_bytes()
        self.git("config", "core.fileMode", "false")
        header = self.source / "src/source.h"
        run, _ = self.build_peer(lambda: header.chmod(0o755))
        with patch.object(installer.subprocess, "run", side_effect=run):
            with self.assertRaisesRegex(ValueError, "file type/mode changes"):
                installer.build(self.source, jobs=1, rebuild=True)
        self.assertEqual(previous_binary, binary.read_bytes())
        self.assertEqual(previous_state, state.read_bytes())
        self.assertEqual(0o755, header.stat().st_mode & 0o777)

    @unittest.skipIf(os.name == "nt", "Fixture requires POSIX symlinks")
    def test_existing_dangling_build_candidate_is_preserved(self):
        (self.source / ".git/info/exclude").write_text("/src/stockfish.pending\n")
        candidate = self.source / "src/stockfish.pending"
        target = self.base / "operator-target"
        candidate.symlink_to(target)
        run, calls = self.build_peer()
        with patch.object(installer.subprocess, "run", side_effect=run):
            with self.assertRaisesRegex(ValueError, "candidate already exists"):
                installer.build(self.source, jobs=1)
        self.assertEqual([], calls)
        self.assertTrue(candidate.is_symlink())
        self.assertEqual(str(target), os.readlink(candidate))
        self.assertFalse(target.exists())

    def real_make_fixture(self, failing=False):
        # Exercise GNU make's actual ignored-include evaluation and the same
        # hardcoded executable removal used by upstream Stockfish objclean.
        (self.source / "src/fixture-engine").write_bytes(PROGRAM)
        (self.source / "src/fixture-engine").chmod(0o755)
        (self.source / "src/Makefile").write_text(
            "-include .depend\n.PHONY: profile-build\nprofile-build:\n"
            "\trm -f stockfish\n"
            + ("\texit 9\n" if failing else "\tcp fixture-engine $(EXE)\n")
            + ".depend:\n\tprintf '# generated from the admitted fixture\\n' > .depend\n")
        with (self.source / ".gitignore").open("a") as output:
            output.write("/src/.depend\n")
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", "real make fixture")
        self.commit = self.git("rev-parse", "HEAD")
        self.git("tag", "-f", "sf_19")
        self.lock["commit"] = self.commit
        (self.base / "deploy/linux/stockfish-release.json").write_text(json.dumps(self.lock))

    def test_real_upstream_cleanup_failure_restores_prior_executable_and_depend(self):
        self.real_make_fixture(failing=True)
        binary = self.executable(PROGRAM + b"# previous executable\n")
        previous = binary.read_bytes()
        dependency = self.source / "src/.depend"
        dependency.write_text("$(shell touch ../unsafe-include-executed)\n")
        original, inode = dependency.read_bytes(), dependency.stat().st_ino
        with self.assertRaises(subprocess.CalledProcessError):
            installer.build(self.source, jobs=1)
        self.assertEqual(previous, binary.read_bytes())
        self.assertEqual(original, dependency.read_bytes())
        self.assertEqual(inode, dependency.stat().st_ino)
        self.assertFalse((self.source / "unsafe-include-executed").exists())
        self.assertFalse((self.source / ".git/laplace-stockfish-build.json").exists())

    @unittest.skipIf(os.name == "nt", "Fixture requires POSIX symlinks and GNU make")
    def test_real_make_regenerates_include_and_preserves_prior_symlink(self):
        self.real_make_fixture()
        target = self.base / "operator-dependencies"
        target.write_text("$(shell touch ../unsafe-include-executed)\n")
        dependency = self.source / "src/.depend"
        dependency.symlink_to(target)
        installer.build(self.source, jobs=1)
        self.assertTrue(dependency.is_symlink())
        self.assertEqual(str(target), os.readlink(dependency))
        self.assertEqual("$(shell touch ../unsafe-include-executed)\n", target.read_text())
        self.assertFalse((self.source / "unsafe-include-executed").exists())
        state = json.loads((self.source / ".git/laplace-stockfish-build.json").read_text())
        self.assertEqual("regenerated-by-upstream-make-with-prior-file-preserved",
                         state["recipe"]["dependency_include"])

    def test_dependency_restoration_failure_keeps_exact_backup(self):
        dependency = self.source / "src/.depend"
        dependency.write_text("operator dependency artifact\n")
        with self.assertRaisesRegex(ValueError, "remains preserved at"):
            with installer.regenerate_dependencies(self.source, self.source / ".git"):
                dependency.mkdir()
        backups = list((self.source / ".git").glob("laplace-stockfish-depend-*/.depend"))
        self.assertEqual(1, len(backups))
        self.assertEqual("operator dependency artifact\n", backups[0].read_text())

    def test_unofficial_origin_and_wrong_pin_are_rejected(self):
        self.git("remote", "set-url", "origin", "https://example.invalid/Stockfish.git")
        with self.assertRaisesRegex(ValueError, "official repository"):
            installer.update_source(self.source, self.lock)
        self.git("remote", "set-url", "origin", self.lock["repository"])
        self.lock["commit"] = "0" * 40
        with self.assertRaisesRegex(ValueError, "pinned official"):
            installer.update_source(self.source, self.lock)

    def test_external_pin_update_preserves_unrelated_dependencies(self):
        pins = self.source.parent / "PINS.tsv"
        pins.write_text("# host pins\nexternal/other\thttps://example.invalid/other\t123\n"
                        "external/stockfish\thttps://old.invalid/Stockfish\told\n")
        pins.chmod(0o664)
        previous = pins.stat()
        installer.record_external_pin(self.source, self.lock)
        updated = pins.stat()
        self.assertEqual((previous.st_ino, previous.st_uid, previous.st_gid, previous.st_mode),
                         (updated.st_ino, updated.st_uid, updated.st_gid, updated.st_mode))
        self.assertIn("external/other\thttps://example.invalid/other\t123\n", pins.read_text())
        self.assertEqual(1, pins.read_text().count("external/stockfish\t"))
        self.assertIn(self.commit, pins.read_text())

    def test_probe_requires_search_and_rejects_missing_network(self):
        binary = self.executable()
        self.assertIn("UCI_Elo", installer.probe(binary, "19"))
        self.executable(PROGRAM.replace(b"printf 'info depth 1 score cp 10\\nbestmove e2e4\\n'", b"exit 7"))
        with self.assertRaises(subprocess.CalledProcessError):
            installer.probe(binary, "19")
        self.executable(PROGRAM.replace(b"bestmove e2e4", b"bestmove 0000"))
        with self.assertRaisesRegex(ValueError, "legal move"):
            installer.probe(binary, "19")

    def test_failed_source_build_restores_prior_executable(self):
        binary = self.executable()
        previous = binary.read_bytes()
        original_run = installer.subprocess.run

        def run(args, **kwargs):
            if args[0] == "sh":
                return subprocess.CompletedProcess(args, 0, stdout="x86-64-avx2\n")
            if args[0] == "g++":
                return subprocess.CompletedProcess(args, 0, stdout="gcc fixture\n")
            if args[0] == "make":
                candidate = binary.with_name(next(arg.split("=", 1)[1] for arg in args if arg.startswith("EXE=")))
                candidate.write_text("failed build output")
                raise subprocess.CalledProcessError(2, args)
            return original_run(args, **kwargs)

        with patch.object(installer.subprocess, "run", side_effect=run):
            with self.assertRaises(subprocess.CalledProcessError):
                installer.build(self.source, jobs=1)
        self.assertEqual(previous, binary.read_bytes())
        self.assertIn("UCI_Elo", installer.probe(binary, "19"))

    def test_configured_path_prefers_explicit_environment_then_application_config(self):
        prefix = self.base / "install"
        config = prefix / "app/laplace-api.env"
        config.parent.mkdir(parents=True)
        config.write_text("OTHER=untouched\nLAPLACE_STOCKFISH=/operator/stockfish\n")
        self.assertEqual(Path("/operator/stockfish"), installer.configured_binary(prefix))
        with patch.dict(os.environ, {"LAPLACE_STOCKFISH": "/explicit/stockfish"}):
            self.assertEqual(Path("/explicit/stockfish"), installer.configured_binary(prefix))

    def test_latest_check_verifies_release_tag_and_source_commit(self):
        with patch.object(installer, "github_json", side_effect=[{"tag_name": "sf_19"}, {"sha": self.commit}]):
            installer.check_latest()
        with patch.object(installer, "github_json", return_value={"tag_name": "sf_20"}):
            with self.assertRaisesRegex(ValueError, "stale"):
                installer.check_latest()
        with patch.object(installer, "github_json", side_effect=[{"tag_name": "sf_19"}, {"sha": "0" * 40}]):
            with self.assertRaisesRegex(ValueError, "source pin"):
                installer.check_latest()


if __name__ == "__main__":
    unittest.main(verbosity=2)
