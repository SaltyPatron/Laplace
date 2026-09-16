#!/usr/bin/env python3
"""Regress shared-source upgrades, dirty-tree preservation and runtime probes."""
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('cutechess', Path(__file__).with_name('provision-cutechess.py'))
cutechess = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cutechess)


class CuteChessReleaseTests(unittest.TestCase):
    def setUp(self):
        temporary_root = Path(os.environ.get('LAPLACE_WORK_ROOT', '/build/laplace/work')) / 'tmp'
        temporary_root.mkdir(parents=True, exist_ok=True)
        self.work = tempfile.TemporaryDirectory(prefix='cutechess-release-test-', dir=temporary_root)
        self.addCleanup(self.work.cleanup)
        self.root = Path(self.work.name)
        self.lock = json.loads(cutechess.LOCK.read_text())
        self.remote = self.root / 'upstream'
        cutechess.run(['git', 'init', self.remote])
        (self.remote / '.version').write_text('1.4.0')
        (self.remote / 'CMakeLists.txt').write_text('project(cutechess)')
        (self.remote / 'source.h').write_text('official fixture header\n')
        (self.remote / 'source.cpp').write_text('official fixture implementation\n')
        (self.remote / 'configure.sh').write_text('#!/bin/sh\nexit 0\n')
        (self.remote / 'configure.sh').chmod(0o755)
        self.commit('old')
        self.old = cutechess.run(['git', '-C', self.remote, 'rev-parse', 'HEAD'])
        (self.remote / '.version').write_text(self.lock['version'])
        self.commit('new')
        self.lock['commit'] = cutechess.run(['git', '-C', self.remote, 'rev-parse', 'HEAD'])
        cutechess.run(['git', '-C', self.remote, 'tag', self.lock['tag']])
        self.lock['repository'] = str(self.remote)

    def commit(self, message):
        cutechess.run(['git', '-C', self.remote, 'add', '.'])
        cutechess.run(['git', '-C', self.remote, '-c', 'user.name=Fixture', '-c',
                      'user.email=fixture@example.invalid', 'commit', '-m', message])

    def test_clone_and_idempotent_shared_update(self):
        target = self.root / 'external/cutechess'
        self.assertEqual(cutechess.provision(target, self.lock), target)
        self.assertEqual(cutechess.provision(target, self.lock), target)
        self.assertEqual((target / '.version').read_text(), '1.5.1')

    def test_upgrade_preserves_old_branch_and_reconciles_existing_pins(self):
        target = self.root / 'external/cutechess'
        cutechess.provision(target, self.lock)
        cutechess.run(['git', '-C', target, 'checkout', '-b', 'operator-old', self.old])
        pins = target.parent / 'PINS.tsv'
        pins.write_text('external/other\tunchanged\t1234\nexternal/cutechess\told-url\told-sha\n')
        cutechess.provision(target, self.lock)
        self.assertEqual(cutechess.run(['git', '-C', target, 'rev-parse', 'operator-old']), self.old)
        self.assertIn('external/other\tunchanged\t1234', pins.read_text())
        self.assertIn(self.lock['commit'], pins.read_text())
        self.assertEqual(cutechess.run(['git', '-C', target, 'rev-parse', f'refs/laplace/previous/{self.old}']), self.old)

    def test_group_writer_preserves_manifest_owner_and_inode(self):
        target = cutechess.provision(self.root / 'external/cutechess', self.lock)
        pins = target.parent / 'PINS.tsv'
        pins.write_text('external/cutechess\told\told\n')
        before = pins.stat()
        with patch.object(cutechess.os, 'geteuid', return_value=before.st_uid + 1):
            cutechess.provision(target, self.lock)
        after = pins.stat()
        self.assertEqual((before.st_uid, before.st_ino), (after.st_uid, after.st_ino))
        self.assertIn(self.lock['commit'], pins.read_text())

    def test_local_changes_are_preserved(self):
        target = cutechess.provision(self.root / 'external/cutechess', self.lock)
        (target / 'CMakeLists.txt').write_text('local changes')
        with self.assertRaisesRegex(RuntimeError, 'local changes'):
            cutechess.provision(target, self.lock)
        self.assertEqual((target / 'CMakeLists.txt').read_text(), 'local changes')

    def test_untracked_build_output_is_retained(self):
        target = cutechess.provision(self.root / 'external/cutechess', self.lock)
        (target / 'operator.log').write_text('preserve')
        with self.assertRaisesRegex(RuntimeError, 'local changes'):
            cutechess.provision(target, self.lock)
        self.assertEqual((target / 'operator.log').read_text(), 'preserve')

    def test_foreign_origin_rejected_before_updating(self):
        target = cutechess.provision(self.root / 'external/cutechess', self.lock)
        cutechess.run(['git', '-C', target, 'remote', 'set-url', 'origin', '/unexpected/repository'])
        with self.assertRaisesRegex(RuntimeError, 'origin does not match'):
            cutechess.provision(target, self.lock)
        self.assertEqual(cutechess.run(['git', '-C', target, 'rev-parse', 'HEAD']), self.lock['commit'])

    def test_hidden_cpp_and_header_changes_are_preserved_before_update(self):
        for flag, filename in (('--assume-unchanged', 'source.cpp'),
                               ('--assume-unchanged', 'source.h'), ('--skip-worktree', 'source.h')):
            with self.subTest(flag=flag, filename=filename):
                target = cutechess.provision(self.root / ('source-' + flag + filename), self.lock)
                cutechess.run(['git', '-C', target, 'checkout', '--detach', self.old])
                cutechess.run(['git', '-C', target, 'update-index', flag, filename])
                (target / filename).write_text('operator bytes\n')
                self.assertEqual('', cutechess.run(['git', '-C', target, 'status', '--porcelain']))
                with self.assertRaisesRegex(ValueError, 'local byte changes'):
                    cutechess.provision(target, self.lock)
                self.assertEqual('operator bytes\n', (target / filename).read_text())
                self.assertEqual(self.old, cutechess.run(['git', '-C', target, 'rev-parse', 'HEAD']))

    @unittest.skipIf(os.name == 'nt', 'POSIX executable mode is not represented on Windows')
    def test_hidden_header_and_script_mode_changes_are_preserved(self):
        target = cutechess.provision(self.root / 'external/cutechess', self.lock)
        cutechess.run(['git', '-C', target, 'config', 'core.fileMode', 'false'])
        for filename, mode in (('source.h', 0o755), ('configure.sh', 0o644)):
            with self.subTest(filename=filename):
                path = target / filename
                original = path.stat().st_mode
                path.chmod(mode)
                self.assertEqual('', cutechess.run(['git', '-C', target, 'status', '--porcelain']))
                with self.assertRaisesRegex(ValueError, 'file type/mode changes'):
                    cutechess.verify_source(target, self.lock)
                self.assertEqual(mode, path.stat().st_mode & 0o777)
                path.chmod(original)

    def test_replacement_objects_cannot_redefine_committed_header(self):
        target = cutechess.provision(self.root / 'external/cutechess', self.lock)
        original = cutechess.run(['git', '-C', target, 'rev-parse', 'HEAD:source.h'])
        replacement = self.root / 'replacement'
        replacement.write_text('replacement header\n')
        object_id = cutechess.run(['git', '-C', target, 'hash-object', '-w', replacement])
        cutechess.run(['git', '-C', target, 'replace', original, object_id])
        cutechess.verify_source(target, self.lock)
        cutechess.run(['git', '-C', target, 'update-index', '--assume-unchanged', 'source.h'])
        (target / 'source.h').write_bytes(replacement.read_bytes())
        with self.assertRaisesRegex(ValueError, 'local byte changes'):
            cutechess.verify_source(target, self.lock)

    def build_fixture(self):
        source = cutechess.provision(self.root / 'external/cutechess', self.lock)
        build = self.root / 'build'
        build.mkdir()
        binary = build / 'cutechess-cli'
        binary.write_text('#!/bin/sh\nprintf "cutechess-cli 1.5.1\\nUsing Qt version 6.11.2\\n"\n')
        binary.chmod(0o755)
        (build / 'CMakeCache.txt').write_text('fixture build metadata\n')
        return source, binary, build / 'laplace-cutechess-build.json'

    def update_fixture_release(self):
        self.commit('cache reset fixture')
        self.lock['commit'] = cutechess.run(['git', '-C', self.remote, 'rev-parse', 'HEAD'])
        cutechess.run(['git', '-C', self.remote, 'tag', '--force', self.lock['tag']])

    def test_cache_reset_preserves_binary_logs_receipts_and_verified_source(self):
        source, binary, receipt = self.build_fixture()
        receipt.write_text('prior verified build receipt\n')
        log = binary.parent / 'operator.log'
        log.write_text('retain this log\n')
        metadata = binary.parent / 'CMakeFiles'
        metadata.mkdir()
        (metadata / 'old-compiler.cmake').write_text('stale compiler metadata\n')
        retained = {path: path.read_bytes() for path in
                    (binary, receipt, log, source / 'source.cpp', source / 'source.h')}
        result = cutechess.reset_build_cache(source, binary.parent, self.lock)
        self.assertEqual(str(binary.parent.resolve()), result['build_dir'])
        self.assertEqual(self.lock['commit'], result['source_integrity']['commit'])
        self.assertCountEqual(['CMakeCache.txt', 'CMakeFiles'], result['removed'])
        self.assertFalse((binary.parent / 'CMakeCache.txt').exists())
        self.assertFalse(metadata.exists())
        for path, content in retained.items():
            self.assertEqual(content, path.read_bytes(), str(path))
        self.assertEqual([], cutechess.reset_build_cache(source, binary.parent, self.lock)['removed'])

    def test_cache_reset_preflights_both_entry_types_before_any_deletion(self):
        source, binary, _ = self.build_fixture()
        cache = binary.parent / 'CMakeCache.txt'
        before = cache.read_bytes()
        files = binary.parent / 'CMakeFiles'
        files.write_text('operator file occupying the directory name\n')
        with self.assertRaises((ValueError, RuntimeError)):
            cutechess.reset_build_cache(source, binary.parent, self.lock)
        self.assertEqual(before, cache.read_bytes())
        self.assertEqual('operator file occupying the directory name\n', files.read_text())
        files.unlink()
        files.mkdir()
        (files / 'preserve').write_text('metadata retained when the other entry is unsafe')
        cache.unlink()
        cache.mkdir()
        with self.assertRaises((ValueError, RuntimeError)):
            cutechess.reset_build_cache(source, binary.parent, self.lock)
        self.assertTrue(cache.is_dir())
        self.assertTrue((files / 'preserve').is_file())

    @unittest.skipIf(os.name == 'nt', 'Creating symlinks requires Windows privileges')
    def test_cache_reset_rejects_metadata_links_but_supports_configured_root_link(self):
        source, binary, _ = self.build_fixture()
        build = binary.parent
        outside = self.root / 'outside'
        outside.mkdir()
        sentinel = outside / 'preserve'
        sentinel.write_text('outside the selected metadata\n')
        cache = build / 'CMakeCache.txt'
        cache_bytes = cache.read_bytes()
        files = build / 'CMakeFiles'
        files.symlink_to(outside, target_is_directory=True)
        with self.assertRaises((ValueError, RuntimeError)):
            cutechess.reset_build_cache(source, build, self.lock)
        self.assertEqual(cache_bytes, cache.read_bytes())
        self.assertTrue(files.is_symlink())
        files.unlink()
        files.mkdir()
        (files / 'old').write_text('old metadata')
        cache.unlink()
        cache.symlink_to(sentinel)
        with self.assertRaises((ValueError, RuntimeError)):
            cutechess.reset_build_cache(source, build, self.lock)
        self.assertTrue((files / 'old').is_file())
        self.assertEqual('outside the selected metadata\n', sentinel.read_text())
        cache.unlink()
        cache.write_bytes(cache_bytes)
        configured = self.root / 'configured-build'
        configured.symlink_to(build, target_is_directory=True)
        result = cutechess.reset_build_cache(source, configured, self.lock)
        self.assertEqual(str(build.resolve()), result['build_dir'])
        self.assertTrue(configured.is_symlink())
        self.assertTrue(binary.is_file())

    def test_hidden_source_changes_prevent_any_cache_reset(self):
        source, binary, _ = self.build_fixture()
        cache = binary.parent / 'CMakeCache.txt'
        before = cache.read_bytes()
        cutechess.run(['git', '-C', source, 'update-index', '--assume-unchanged', 'source.h'])
        (source / 'source.h').write_text('hidden operator source edit\n')
        with self.assertRaisesRegex(ValueError, 'local byte changes'):
            cutechess.reset_build_cache(source, binary.parent, self.lock)
        self.assertEqual(before, cache.read_bytes())
        self.assertEqual('hidden operator source edit\n', (source / 'source.h').read_text())

    def test_cache_reset_cannot_remove_a_source_checkout_inside_metadata_directory(self):
        build = self.root / 'build-containing-source'
        source = cutechess.provision(build / 'CMakeFiles' / 'source', self.lock)
        cache = build / 'CMakeCache.txt'
        cache.write_text('retained until both deletion targets are safe\n')
        with self.assertRaises((ValueError, RuntimeError)):
            cutechess.reset_build_cache(source, build, self.lock)
        self.assertEqual('retained until both deletion targets are safe\n', cache.read_text())
        self.assertEqual(self.lock['commit'], cutechess.verify_source(source, self.lock)['commit'])

    def test_cache_reset_preserves_tracked_source_entries_with_metadata_names(self):
        (self.remote / 'CMakeCache.txt').write_text('this file is committed source\n')
        self.update_fixture_release()
        source = cutechess.provision(self.root / 'tracked-cache-source', self.lock)
        with self.assertRaises((ValueError, RuntimeError)):
            cutechess.reset_build_cache(source, source, self.lock)
        self.assertEqual('this file is committed source\n', (source / 'CMakeCache.txt').read_text())
        self.assertEqual(self.lock['commit'], cutechess.verify_source(source, self.lock)['commit'])

    def test_cache_reset_preserves_tracked_source_below_cmakefiles(self):
        (self.remote / 'CMakeFiles').mkdir()
        (self.remote / 'CMakeFiles' / 'tracked.cmake').write_text('committed configure input\n')
        (self.remote / '.gitignore').write_text('/CMakeCache.txt\n')
        self.update_fixture_release()
        source = cutechess.provision(self.root / 'tracked-metadata-directory', self.lock)
        cache = source / 'CMakeCache.txt'
        cache.write_text('must survive unsafe second deletion target\n')
        with self.assertRaises((ValueError, RuntimeError)):
            cutechess.reset_build_cache(source, source, self.lock)
        self.assertEqual('must survive unsafe second deletion target\n', cache.read_text())
        self.assertEqual('committed configure input\n', (source / 'CMakeFiles' / 'tracked.cmake').read_text())
        self.assertEqual(self.lock['commit'], cutechess.verify_source(source, self.lock)['commit'])

    def test_cache_reset_supports_ignored_in_source_and_nested_build_metadata(self):
        (self.remote / '.gitignore').write_text('CMakeCache.txt\nCMakeFiles/\n/build/\n')
        self.update_fixture_release()
        source = cutechess.provision(self.root / 'in-source', self.lock)
        for build in (source, source / 'build'):
            with self.subTest(build=build):
                build.mkdir(exist_ok=True)
                (build / 'CMakeCache.txt').write_text('stale ignored metadata\n')
                (build / 'CMakeFiles').mkdir()
                (build / 'CMakeFiles' / 'old').write_text('stale generated file\n')
                result = cutechess.reset_build_cache(source, build, self.lock)
                self.assertCountEqual(['CMakeCache.txt', 'CMakeFiles'], result['removed'])
                self.assertEqual(self.lock['commit'], cutechess.verify_source(source, self.lock)['commit'])

    @unittest.skipIf(os.name == 'nt', 'Uses a POSIX command wrapper and compiler fixture')
    def test_portable_reset_reconfigures_and_builds_after_verified_source_moves(self):
        cmake = shutil.which('cmake')
        if not cmake or not shutil.which('make') or not shutil.which('c++'):
            self.skipTest('Real CMake, make and a C++ compiler are required')
        (self.remote / 'CMakeLists.txt').write_text(
            'cmake_minimum_required(VERSION 3.20)\n'
            'project(cutechess_reset_fixture LANGUAGES CXX)\n'
            'add_executable(cli main.cpp)\n')
        (self.remote / 'main.cpp').write_text(
            '#include <iostream>\nint main() { std::cout << "actual CMake fixture\\n"; }\n')
        self.update_fixture_release()
        source = cutechess.provision(self.root / 'original-source', self.lock)
        build = self.root / 'configured-build'
        wrapper = self.root / 'cmake-without-fresh'
        invocations = self.root / 'cmake-invocations.jsonl'
        wrapper.write_text(
            '#!' + sys.executable + '\nimport json, os, sys\n'
            'with open(' + repr(str(invocations)) + ', "a") as log:\n'
            '    log.write(json.dumps(sys.argv[1:]) + "\\n")\n'
            'if "--fresh" in sys.argv[1:]:\n'
            '    sys.stderr.write("Unknown argument --fresh\\n")\n'
            '    sys.exit(97)\n'
            'os.execv(' + repr(cmake) + ', [' + repr(cmake) + ', *sys.argv[1:]])\n')
        wrapper.chmod(0o755)
        rejected = subprocess.run([str(wrapper), '--fresh'], capture_output=True, text=True)
        self.assertEqual(97, rejected.returncode)
        self.assertIn('Unknown argument --fresh', rejected.stderr)
        configure = ['-G', 'Unix Makefiles', '-DCMAKE_BUILD_TYPE=Release']
        cutechess.run([wrapper, '-S', source, '-B', build, *configure, '-DCMAKE_PREFIX_PATH=old-qt'])
        cutechess.run([wrapper, '--build', build, '--clean-first', '--target', 'cli'])
        binary = build / 'cli'
        self.assertEqual('actual CMake fixture', cutechess.run([binary]))
        receipt = build / 'laplace-cutechess-build.json'
        receipt.write_text('retain prior build evidence\n')
        log = build / 'operator.log'
        log.write_text('retain operator log\n')
        retained = {path: path.read_bytes() for path in (binary, receipt, log)}
        moved = self.root / 'moved-source'
        source.rename(moved)
        stale = subprocess.run([str(wrapper), '-S', str(moved), '-B', str(build), *configure],
                               text=True, capture_output=True)
        self.assertNotEqual(0, stale.returncode)
        self.assertIn('does not match the source', stale.stderr)
        lock_path = self.root / 'fixture-release.json'
        lock_path.write_text(json.dumps(self.lock))
        result = json.loads(cutechess.run([
            sys.executable, Path(cutechess.__file__), '--lock', lock_path,
            '--verify-source', moved, '--reset-build-cache', build]))
        self.assertCountEqual(['CMakeCache.txt', 'CMakeFiles'], result['removed'])
        for path, content in retained.items():
            self.assertEqual(content, path.read_bytes(), str(path))
        self.assertFalse((build / 'CMakeCache.txt').exists())
        self.assertFalse((build / 'CMakeFiles').exists())
        cutechess.run([wrapper, '-S', moved, '-B', build, *configure, '-DCMAKE_PREFIX_PATH=new-qt'])
        cutechess.run([wrapper, '--build', build, '--clean-first', '--target', 'cli'])
        self.assertEqual('actual CMake fixture', cutechess.run([binary]))
        cache = (build / 'CMakeCache.txt').read_text()
        self.assertIn('CMAKE_HOME_DIRECTORY:INTERNAL=' + str(moved), cache)
        self.assertIn('CMAKE_PREFIX_PATH:UNINITIALIZED=new-qt', cache)
        self.assertEqual(retained[receipt], receipt.read_bytes())
        self.assertEqual(retained[log], log.read_bytes())
        self.assertEqual(self.lock['commit'], cutechess.verify_source(moved, self.lock)['commit'])
        commands = [json.loads(line) for line in invocations.read_text().splitlines()]
        self.assertEqual([['--fresh']], [args for args in commands if '--fresh' in args])
        self.assertEqual(2, sum('--clean-first' in args for args in commands))

    def test_post_build_receipt_binds_exact_source_and_probed_binary(self):
        source, binary, receipt_path = self.build_fixture()
        receipt = cutechess.verify_build(source, binary, self.lock, '6.11.2', receipt_path)
        self.assertEqual(receipt, json.loads(receipt_path.read_text()))
        self.assertEqual(self.lock['commit'], receipt['source_integrity']['commit'])
        self.assertEqual('raw-committed-blob-bytes', receipt['source_integrity']['verification'])
        self.assertEqual(5, receipt['source_integrity']['tracked_files'])
        self.assertEqual(cutechess.digest(binary), receipt['binary_sha256'])
        self.assertEqual(cutechess.digest(binary.parent / 'CMakeCache.txt'), receipt['cmake_cache_sha256'])

    def test_header_changed_during_probe_cannot_replace_prior_receipt(self):
        source, binary, receipt_path = self.build_fixture()
        cutechess.verify_build(source, binary, self.lock, '6.11.2', receipt_path)
        previous = receipt_path.read_bytes()
        cutechess.run(['git', '-C', source, 'update-index', '--assume-unchanged', 'source.h'])
        original_probe = cutechess.probe

        def probe(*args):
            result = original_probe(*args)
            (source / 'source.h').write_text('during runtime verification\n')
            return result

        with patch.object(cutechess, 'probe', side_effect=probe):
            with self.assertRaisesRegex(ValueError, 'local byte changes'):
                cutechess.verify_build(source, binary, self.lock, '6.11.2', receipt_path)
        self.assertEqual(previous, receipt_path.read_bytes())
        self.assertEqual('during runtime verification\n', (source / 'source.h').read_text())

    def test_binary_changed_during_probe_cannot_receive_receipt(self):
        source, binary, receipt_path = self.build_fixture()
        original_probe = cutechess.probe

        def probe(*args):
            result = original_probe(*args)
            binary.write_text('different executable\n')
            return result

        with patch.object(cutechess, 'probe', side_effect=probe):
            with self.assertRaisesRegex(RuntimeError, 'executable changed'):
                cutechess.verify_build(source, binary, self.lock, '6.11.2', receipt_path)
        self.assertFalse(receipt_path.exists())

    def test_latest_mismatch_fails(self):
        with patch.object(cutechess.urllib.request, 'urlopen', return_value=io.BytesIO(
                json.dumps({'tag_name': 'v1.6.0', 'draft': False, 'prerelease': False}).encode())):
            with self.assertRaisesRegex(RuntimeError, 'differs from upstream'):
                cutechess.check_latest(self.lock)

    def test_wrong_release_or_old_qt_rejected(self):
        for output in ['cutechess-cli 1.4.0\nUsing Qt version 6.8.3',
                       'cutechess-cli 1.5.1\nUsing Qt version 6.4.2']:
            with self.subTest(output=output), patch.object(cutechess, 'run', return_value=output):
                with self.assertRaises(RuntimeError):
                    cutechess.probe(Path('fixture'), self.lock)
        with patch.object(cutechess, 'run', return_value='cutechess-cli 1.5.1\nUsing Qt version 6.11.2'):
            self.assertTrue(cutechess.probe(Path('fixture'), self.lock, '6.11.2')['ready'])


    def gui_fixture(self):
        """Protocol fixtures exercise our probe; these bytes are not a Qt runtime."""
        source, cli, _ = self.build_fixture()
        sdk = self.root / "qt-sdk"
        for name in cutechess.GUI_MODULES:
            config = sdk / f"lib/cmake/Qt6{name}/Qt6{name}Config.cmake"
            config.parent.mkdir(parents=True)
            config.write_text(f"# controlled {name} SDK configuration fixture\n")
        version = sdk / "lib/cmake/Qt6/Qt6ConfigVersion.cmake"
        version.parent.mkdir(parents=True)
        version.write_text('include("\u0024{CMAKE_CURRENT_LIST_DIR}/Qt6ConfigVersionImpl.cmake")\n')
        version.with_name("Qt6ConfigVersionImpl.cmake").write_text('set(PACKAGE_VERSION "6.11.2")\n')
        plugin = sdk / "plugins/platforms" / ("qoffscreen.dll" if os.name == "nt" else "libqoffscreen.so")
        plugin.parent.mkdir(parents=True)
        plugin.write_bytes(b"controlled offscreen plugin identity fixture")
        binary = cli.with_name("cutechess")
        self.write_gui_program(binary, plugin)
        return source, binary, sdk, cli.parent / "laplace-cutechess-gui-build.json"

    def write_gui_program(self, binary, plugin, version="1.5.1", qt="6.11.2", exit_code=0, loader=True):
        observed = self.root / "gui-child-observation.json"
        binary.write_text(
            "#!" + sys.executable + "\nimport json, os, sys\n"
            "from pathlib import Path\n"
            "keys = ['XDG_CONFIG_HOME', 'XDG_CONFIG_DIRS', 'XDG_DATA_HOME', "
            "'XDG_DATA_DIRS', 'XDG_CACHE_HOME', 'XDG_RUNTIME_DIR']\n"
            "settings = {key: os.environ[key] for key in keys}\n"
            "assert all(Path(path).is_dir() for path in settings.values())\n"
            "assert 'DISPLAY' not in os.environ and 'WAYLAND_DISPLAY' not in os.environ\n"
            "assert os.environ['QT_QPA_PLATFORM'] == 'offscreen'\n"
            "assert sys.argv[1:] == ['-platform', 'offscreen', '--version']\n"
            "Path(" + repr(str(observed)) + ").write_text(json.dumps({"
            "'argv': sys.argv, 'settings': settings, 'cwd': os.getcwd(), "
            "'plugin_path': os.environ['QT_PLUGIN_PATH']}))\n"
            "print(" + repr("Cute Chess " + version + "\nUsing Qt version " + qt) + ")\n"
            + ("sys.stderr.write(" + repr("qt.core.library: " + json.dumps(str(plugin)) + " loaded library\n") + ")\n"
               if loader else "")
            + "sys.exit(" + repr(exit_code) + ")\n")
        binary.chmod(0o755)

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux direct GUI child and XDG isolation")
    def test_gui_receipt_binds_source_installed_copy_and_real_child_plugin_protocol(self):
        source, binary, sdk, receipt_path = self.gui_fixture()
        scratch = self.root / "probe-work"
        with patch.dict(os.environ, {"DISPLAY": "operator-display", "WAYLAND_DISPLAY": "operator-wayland",
                                     "XDG_CONFIG_HOME": str(self.root / "operator-settings")}):
            receipt = cutechess.verify_gui_build(source, binary, self.lock, sdk, receipt_path, scratch)
            self.assertEqual("operator-display", os.environ["DISPLAY"])
        self.assertEqual(receipt, json.loads(receipt_path.read_text()))
        runtime = receipt["runtime"]
        self.assertEqual("gui", receipt["build_target"])
        self.assertEqual(self.lock["commit"], receipt["source_integrity"]["commit"])
        self.assertEqual("ready-headless", runtime["status"])
        self.assertTrue(runtime["qapplication_initialized"])
        self.assertFalse(runtime["interactive_desktop_tested"])
        self.assertIsNone(runtime["interactive_desktop_ready"])
        self.assertEqual(set(cutechess.GUI_MODULES), set(runtime["qt"]["modules"]))
        self.assertEqual(2, len(runtime["qt"]["version_files"]))
        child = json.loads((self.root / "gui-child-observation.json").read_text())
        for path in child["settings"].values():
            self.assertTrue(Path(path).is_relative_to(scratch))
            self.assertFalse(Path(path).exists())
        installed = self.root / "installed-cutechess"
        shutil.copy2(binary, installed)
        checked = cutechess.verify_gui_install(installed, receipt_path, self.lock, scratch)
        self.assertEqual(cutechess.digest(installed), checked["runtime"]["binary_sha256"])
        self.assertEqual([str(installed)], checked["runtime"]["direct_launch"]["argv"])
        self.assertEqual(str(sdk / "plugins"), checked["runtime"]["direct_launch"]["environment"]["QT_PLUGIN_PATH"])

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux controlled GUI executable protocol")
    def test_gui_wrong_release_qt_exit_and_loader_identity_are_rejected(self):
        _, binary, sdk, _ = self.gui_fixture()
        selected = sdk / "plugins/platforms/libqoffscreen.so"
        foreign = self.root / "other-sdk/platforms/libqoffscreen.so"
        foreign.parent.mkdir(parents=True)
        foreign.write_bytes(selected.read_bytes())
        for options in ({"version": "1.4.0"}, {"qt": "6.8.3"}, {"exit_code": 7},
                        {"loader": False}, {"plugin": foreign}):
            with self.subTest(options=options):
                arguments = {"plugin": selected, **options}
                self.write_gui_program(binary, **arguments)
                with self.assertRaises(RuntimeError):
                    cutechess.probe_gui(binary, self.lock, sdk, self.root / "probe-work")

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux GUI SDK plugin inventory")
    def test_gui_missing_modules_platform_and_wrong_wrapper_version_fail_before_launch(self):
        _, binary, sdk, _ = self.gui_fixture()
        targets = [sdk / f"lib/cmake/Qt6{name}/Qt6{name}Config.cmake" for name in cutechess.GUI_MODULES]
        targets.append(sdk / "plugins/platforms/libqoffscreen.so")
        for target in targets:
            with self.subTest(target=target):
                original = target.read_bytes()
                target.unlink()
                with patch.object(cutechess.subprocess, "run") as launch:
                    with self.assertRaises((OSError, RuntimeError)):
                        cutechess.probe_gui(binary, self.lock, sdk, self.root / "probe-work")
                    launch.assert_not_called()
                target.write_bytes(original)
        version = sdk / "lib/cmake/Qt6/Qt6ConfigVersionImpl.cmake"
        version.write_text('set(PACKAGE_VERSION "6.4.2")\n')
        with patch.object(cutechess.subprocess, "run") as launch:
            with self.assertRaisesRegex(RuntimeError, "selected Qt"):
                cutechess.probe_gui(binary, self.lock, sdk, self.root / "probe-work")
            launch.assert_not_called()

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux direct GUI installed file contract")
    def test_gui_changed_source_binary_or_plugin_cannot_reuse_receipt(self):
        source, binary, sdk, receipt_path = self.gui_fixture()
        work = self.root / "probe-work"
        cutechess.verify_gui_build(source, binary, self.lock, sdk, receipt_path, work)
        for target in (source / "source.h", binary, sdk / "plugins/platforms/libqoffscreen.so"):
            with self.subTest(target=target):
                original = target.read_bytes()
                target.write_bytes(original + b"\nchanged\n")
                with patch.object(cutechess, "probe_gui") as probe:
                    with self.assertRaises((RuntimeError, ValueError)):
                        cutechess.verify_gui_install(binary, receipt_path, self.lock, work)
                    probe.assert_not_called()
                target.write_bytes(original)
        linked = self.root / "linked-cutechess"
        linked.symlink_to(binary)
        with patch.object(cutechess, "probe_gui") as probe:
            with self.assertRaisesRegex(RuntimeError, "installed executable"):
                cutechess.verify_gui_install(linked, receipt_path, self.lock, work)
            probe.assert_not_called()

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux GUI child lifecycle")
    def test_gui_timeout_cleans_isolated_settings_and_preserves_prior_receipt(self):
        source, binary, sdk, receipt_path = self.gui_fixture()
        scratch = self.root / "probe-work"
        prior = cutechess.verify_gui_build(source, binary, self.lock, sdk, receipt_path, scratch)
        original_run = cutechess.subprocess.run
        def timeout(command, **kwargs):
            if str(command[0]) == str(binary):
                self.assertEqual(30, kwargs["timeout"])
                self.assertTrue(Path(kwargs["cwd"]).is_dir())
                raise subprocess.TimeoutExpired(command, 30)
            return original_run(command, **kwargs)
        with patch.object(cutechess.subprocess, "run", side_effect=timeout):
            with self.assertRaises(subprocess.TimeoutExpired):
                cutechess.verify_gui_build(source, binary, self.lock, sdk, receipt_path, scratch)
        self.assertEqual(prior, json.loads(receipt_path.read_text()))
        self.assertEqual([], list(scratch.iterdir()))

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux GUI before/after runtime integrity")
    def test_gui_plugin_changed_during_probe_cannot_replace_prior_receipt(self):
        source, binary, sdk, receipt_path = self.gui_fixture()
        scratch = self.root / "probe-work"
        cutechess.verify_gui_build(source, binary, self.lock, sdk, receipt_path, scratch)
        prior = receipt_path.read_bytes()
        original_run = cutechess.subprocess.run
        def mutate(command, **kwargs):
            result = original_run(command, **kwargs)
            if str(command[0]) == str(binary):
                (sdk / "plugins/platforms/libqoffscreen.so").write_bytes(b"changed during GUI process")
            return result
        with patch.object(cutechess.subprocess, "run", side_effect=mutate):
            with self.assertRaisesRegex(RuntimeError, "changed during"):
                cutechess.verify_gui_build(source, binary, self.lock, sdk, receipt_path, scratch)
        self.assertEqual(prior, receipt_path.read_bytes())

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux CLI argument dispatch to GUI verification")
    def test_gui_command_line_build_and_installed_verification(self):
        source, binary, sdk, receipt_path = self.gui_fixture()
        lock_path = self.root / "gui-release.json"
        lock_path.write_text(json.dumps(self.lock))
        common = [sys.executable, cutechess.__file__, "--lock", str(lock_path), "--gui", "--binary", str(binary),
                  "--work", str(self.root / "probe-work")]
        result = subprocess.run([*common, "--verify-source", str(source), "--qt-prefix", str(sdk),
                                 "--receipt", str(receipt_path)], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("gui", json.loads(result.stdout)["build_target"])
        result = subprocess.run([*common, "--verify-receipt", str(receipt_path)], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("ready-headless", json.loads(result.stdout)["runtime"]["status"])



    def test_gui_sdk_direct_and_wrapped_versions_are_unambiguous(self):
        _, _, sdk, _ = self.gui_fixture()
        version = sdk / "lib/cmake/Qt6/Qt6ConfigVersion.cmake"
        implementation = version.with_name("Qt6ConfigVersionImpl.cmake")
        wrapped = version.read_text()
        expected = cutechess.gui_inventory(sdk, self.lock)
        self.assertEqual("6.11.2", expected["version"])
        self.assertEqual(2, len(expected["version_files"]))
        version.write_text(wrapped + 'set(PACKAGE_VERSION "6.4.2")\n')
        with self.assertRaises(RuntimeError):
            cutechess.gui_inventory(sdk, self.lock)
        version.write_text('# ' + wrapped + 'set(PACKAGE_VERSION "6.11.2")\n')
        self.assertEqual(1, len(cutechess.gui_inventory(sdk, self.lock)["version_files"]))
        version.write_text('set(PACKAGE_VERSION "6.11.2")\n')
        self.assertEqual(1, len(cutechess.gui_inventory(sdk, self.lock)["version_files"]))
        version.write_text('set(PACKAGE_VERSION "6.11.2")\nset(PACKAGE_VERSION "6.4.2")\n')
        with self.assertRaises(RuntimeError):
            cutechess.gui_inventory(sdk, self.lock)
        version.write_text(wrapped)
        implementation.unlink()
        with self.assertRaises(FileNotFoundError):
            cutechess.gui_inventory(sdk, self.lock)


    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux bootstrap option/presence selection")
    def test_bootstrap_gui_option_and_existing_artifacts_select_fresh_build(self):
        bootstrap = Path(cutechess.__file__).with_name("bootstrap-chess-lab.sh").read_text()
        self.assertTrue(bootstrap.endswith('main "$@"\n'))
        # Exercise the actual option/presence state machine while replacing the
        # downstream installation work. This is not a GUI runtime acceptance.
        script = self.root / "selection.sh"
        script.write_text(bootstrap.removesuffix('main "$@"\n') + r'''
ensure_dirs() { mkdir -p "$CC_BUILD" "$CC_BIN_DIR"; }
build_cutechess() { printf '%s\n' "$CUTECHESS_GUI_BUILD" > "$SELECTION_RESULT"; }
build_zstd() { :; }
run_as_owner() { :; }
verify() { :; }
write_api_env() { :; }
main "$@"
''')
        environment = dict(os.environ)
        for key in ("LAPLACE_CUTECHESS_GUI", "LAPLACE_CUTECHESS_GUI_BUILD"):
            environment.pop(key, None)
        prefix, build = self.root / "prefix", self.root / "selected-build"
        result = self.root / "selection-result"
        environment.update(LAPLACE_INSTALL_PREFIX=str(prefix), LAPLACE_CUTECHESS_BUILD=str(build),
                           LAPLACE_WORK_ROOT=str(self.root / "work"), SELECTION_RESULT=str(result))
        for arguments, expected in (([], "0"), (["--cutechess-gui"], "1")):
            run = subprocess.run(["bash", str(script), *arguments], env=environment, capture_output=True, text=True)
            self.assertEqual(0, run.returncode, run.stderr)
            self.assertEqual(expected, result.read_text().strip())
        for artifact in (prefix / "bin/cutechess", build / "cutechess", build / "laplace-cutechess-gui-build.json"):
            with self.subTest(artifact=artifact):
                artifact.write_text("existing GUI selects rebuild; these bytes are not reused")
                run = subprocess.run(["bash", str(script)], env=environment, capture_output=True, text=True)
                self.assertEqual(0, run.returncode, run.stderr)
                self.assertEqual("1", result.read_text().strip())
                artifact.unlink()
        environment["LAPLACE_CUTECHESS_GUI_BUILD"] = "invalid"
        result.unlink()
        run = subprocess.run(["bash", str(script)], env=environment, capture_output=True, text=True)
        self.assertEqual(2, run.returncode)
        self.assertFalse(result.exists())


    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux ELF DT_RUNPATH loader behavior")
    def test_gui_selected_sdk_library_wins_over_inherited_conflicting_library(self):
        compiler = shutil.which("cc") or shutil.which("gcc")
        if not compiler:
            self.skipTest("A C compiler is required for the real ELF loader control")
        sdk = self.root / "selected-sdk"
        selected, foreign, other = sdk / "lib", self.root / "foreign-lib", self.root / "other-lib"
        for directory in (selected, foreign, other):
            directory.mkdir(parents=True)
        soname = "liblaplace_gui_loader_control.so"
        for directory, label in ((selected, "selected-sdk"), (foreign, "foreign-library")):
            source = directory / "identity.c"
            source.write_text('const char *gui_library_identity(void) { return "' + label + '"; }\n')
            subprocess.run([compiler, "-shared", "-fPIC", str(source),
                            "-Wl,-soname," + soname, "-o", str(directory / soname)],
                           check=True, capture_output=True, text=True)
        main = self.root / "loader.c"
        main.write_text('#include <stdio.h>\nextern const char *gui_library_identity(void);\n'
                        'int main(void) { puts(gui_library_identity()); return 0; }\n')
        executable = self.root / "actual-elf-loader"
        subprocess.run([compiler, str(main), "-L" + str(selected), "-llaplace_gui_loader_control",
                        "-Wl,--enable-new-dtags,-rpath," + str(selected), "-o", str(executable)],
                       check=True, capture_output=True, text=True)
        inherited = os.pathsep.join((str(foreign), str(other)))
        environment = dict(os.environ, LD_LIBRARY_PATH=inherited)
        before = subprocess.run([str(executable)], env=environment, check=True, capture_output=True, text=True)
        self.assertEqual("foreign-library", before.stdout.strip())
        with patch.dict(os.environ, {"LD_LIBRARY_PATH": inherited}):
            overlay = cutechess.gui_environment(sdk)
        self.assertEqual([str(selected), str(foreign), str(other)],
                         overlay["LD_LIBRARY_PATH"].split(os.pathsep))
        environment.update(overlay)
        after = subprocess.run([str(executable)], env=environment, check=True, capture_output=True, text=True)
        self.assertEqual("selected-sdk", after.stdout.strip())



    def desktop_fixture(self, prefix_name="desktop prefix"):
        """Real subprocess transport fixture; this is not a Qt GUI acceptance."""
        source, binary, sdk, receipt = self.gui_fixture()
        observed = self.root / "desktop-child.json"
        program = binary.read_text()
        offset = program.index("keys = ['XDG_CONFIG_HOME'")
        program = program[:offset] + (
            "if sys.argv[1:] != ['-platform', 'offscreen', '--version']:\n"
            "    names = ['DISPLAY', 'XAUTHORITY', 'WAYLAND_DISPLAY', 'XDG_RUNTIME_DIR', "
            "'XDG_CONFIG_HOME', 'QT_PLUGIN_PATH', 'QT_QPA_PLATFORM_PLUGIN_PATH', 'LD_LIBRARY_PATH', 'LAPLACE_PERFCACHE_BIN', 'LAPLACE_CHESS_PERFCACHE_BIN', 'LAPLACE_CHESS_TRANSITION_BIN', 'LAPLACE_UCI_SUBSTRATE']\n"
            "    Path(" + repr(str(observed)) + ").write_text(json.dumps({'argv': sys.argv, "
            "'environment': {key: os.environ.get(key) for key in names}}))\n"
            "    raise SystemExit(0)\n"
        ) + program[offset:]
        binary.write_text(program)
        binary.chmod(0o755)
        work = self.root / "desktop-work"
        cutechess.verify_gui_build(source, binary, self.lock, sdk, receipt, work)
        prefix = self.root / prefix_name
        installed = prefix / "bin/cutechess"
        installed.parent.mkdir(parents=True)
        shutil.copy2(binary, installed)
        # Transport/configuration fixtures only, never actual engine or floor proof.
        app = prefix / "app"
        app.mkdir()
        uci = app / "laplace-uci"
        uci.write_text("#!/bin/sh\nexit 0\n")
        uci.chmod(0o755)
        share = prefix / "share/laplace"
        share.mkdir(parents=True)
        (share / "laplace_t0_perfcache_fixture.bin").write_bytes(b"configuration-path fixture")
        generation = share / "chess-floor/generations/fixture"
        generation.mkdir(parents=True)
        for name in ("laplace_chess_position_perfcache.bin", "laplace_chess_transition_perfcache.bin"):
            (generation / name).write_bytes(b"configuration-path fixture")
        (share / "chess-floor/current").symlink_to("generations/fixture", target_is_directory=True)
        selected_perfcache = patch.dict(os.environ, {"LAPLACE_PERFCACHE_BIN": str(share / "laplace_t0_perfcache_fixture.bin")})
        selected_perfcache.start()
        self.addCleanup(selected_perfcache.stop)
        return installed, sdk, receipt, work, prefix, observed

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux desktop subprocess contract")
    def test_desktop_launcher_uses_verified_installed_binary_and_user_session(self):
        installed, sdk, receipt, work, prefix, observed = self.desktop_fixture()
        with patch.dict(os.environ, {"LD_LIBRARY_PATH": "/private-provisioning-path",
                                     "LICHESS_API": "do-not-publish-fixture-secret"}):
            verified = cutechess.verify_gui_install(installed, receipt, self.lock, work)
            result = cutechess.install_desktop(prefix, verified, installed, work)
        launcher = Path(result["launcher"]["path"])
        manifest = prefix / "share/laplace/cutechess-desktop.json"
        for path in (launcher, Path(result["desktop"]["path"]), manifest):
            self.assertNotIn("private-provisioning-path", path.read_text())
            self.assertNotIn("do-not-publish-fixture-secret", path.read_text())
        self.assertEqual(result, json.loads(manifest.read_text()))
        self.assertFalse(result["operator_desktop_tested"])
        self.assertFalse(result["autostart_installed"])
        self.assertEqual(0o755, launcher.stat().st_mode & 0o777)
        arguments = ["a b", "$literal", ";no-shell", "unicode-\u03a9", ""]
        environment = dict(os.environ, DISPLAY=":73", XAUTHORITY="/operator/auth",
                           WAYLAND_DISPLAY="wayland-7", XDG_RUNTIME_DIR="/operator/run",
                           XDG_CONFIG_HOME=str(self.root / "operator-settings"), QT_PLUGIN_PATH="/wrong/plugin",
                           QT_QPA_PLATFORM_PLUGIN_PATH="/wrong/platform",
                           LD_LIBRARY_PATH="/operator/lib:" + str(sdk / "lib"), LAPLACE_UCI_SUBSTRATE="off")
        run = subprocess.run([str(launcher), *arguments], env=environment, capture_output=True, text=True, timeout=10)
        self.assertEqual(0, run.returncode, run.stderr)
        child = json.loads(observed.read_text())
        self.assertEqual([str(installed), *arguments], child["argv"])
        for key in ("DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "XDG_CONFIG_HOME"):
            self.assertEqual(environment[key], child["environment"][key])
        self.assertEqual(str(sdk / "plugins"), child["environment"]["QT_PLUGIN_PATH"])
        self.assertEqual(str(sdk / "plugins/platforms"), child["environment"]["QT_QPA_PLATFORM_PLUGIN_PATH"])
        self.assertEqual(str(sdk / "lib") + ":/operator/lib", child["environment"]["LD_LIBRARY_PATH"])
        self.assertEqual("substrate", child["environment"]["LAPLACE_UCI_SUBSTRATE"])
        engine_config = Path(environment["XDG_CONFIG_HOME"]) / "cutechess/engines.json"
        engines = json.loads(engine_config.read_text())
        self.assertEqual(["Stockfish (official)", "Laplace (substrate)"], [engine["name"] for engine in engines])
        self.assertEqual([str(prefix / "bin/laplace-cutechess-stockfish"), str(prefix / "app/laplace-uci")],
                         [engine["command"] for engine in engines])
        self.assertEqual(str(prefix / "share/laplace/laplace_t0_perfcache_fixture.bin"),
                         child["environment"]["LAPLACE_PERFCACHE_BIN"])
        self.assertEqual(str(prefix / "share/laplace/chess-floor/generations/fixture/laplace_chess_position_perfcache.bin"),
                         child["environment"]["LAPLACE_CHESS_PERFCACHE_BIN"])

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux installed launcher provenance")
    def test_desktop_cli_install_and_changed_binary_refusal_preserve_prior_launcher(self):
        installed, _, receipt, work, prefix, observed = self.desktop_fixture()
        lock = self.root / "desktop-lock.json"
        lock.write_text(json.dumps(self.lock))
        argv = [sys.executable, cutechess.__file__, "--lock", str(lock), "--gui",
                "--binary", str(installed), "--verify-receipt", str(receipt),
                "--work", str(work), "--install-desktop", str(prefix), "--desktop-stockfish", str(installed)]
        run = subprocess.run(argv, capture_output=True, text=True, timeout=60)
        self.assertEqual(0, run.returncode, run.stderr)
        result = json.loads(run.stdout)["desktop_install"]
        launcher = Path(result["launcher"]["path"])
        before = launcher.read_bytes()
        installed.write_text(installed.read_text() + "\n# changed executable\n")
        run = subprocess.run([str(launcher)], capture_output=True, text=True, timeout=10)
        self.assertNotEqual(0, run.returncode)
        self.assertIn("installed CuteChess changed", run.stderr)
        self.assertFalse(observed.exists())
        refused = subprocess.run(argv, capture_output=True, text=True, timeout=60)
        self.assertNotEqual(0, refused.returncode)
        self.assertEqual(before, launcher.read_bytes())

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux system desktop registration")
    def test_system_desktop_registration_reuses_prefix_owner_and_preserves_foreign_entry(self):
        installed, _, receipt, work, prefix, observed = self.desktop_fixture()
        verified = cutechess.verify_gui_install(installed, receipt, self.lock, work)
        result = cutechess.install_desktop(prefix, verified, installed, work)
        directory = self.root / "system-applications"
        registered = cutechess.register_desktop(prefix, directory)
        entry = directory / "laplace-cutechess.desktop"
        self.assertEqual(Path(result["desktop"]["path"]), entry.resolve())
        self.assertEqual(str(entry), registered["system_desktop_file"])
        self.assertFalse(observed.exists(), "registration must never launch GUI fixture")
        owner_before = (prefix / "share/laplace/cutechess-desktop.json").stat().st_uid
        self.assertEqual(registered, cutechess.register_desktop(prefix, directory))
        self.assertEqual(owner_before, (prefix / "share/laplace/cutechess-desktop.json").stat().st_uid)
        cutechess.install_desktop(prefix, verified, installed, work)
        self.assertEqual(Path(result["desktop"]["path"]), entry.resolve())
        entry.unlink()
        entry.write_text("operator's existing desktop entry")
        with self.assertRaisesRegex(RuntimeError, "another installation"):
            cutechess.register_desktop(prefix, directory)
        self.assertEqual("operator's existing desktop entry", entry.read_text())

    @unittest.skipUnless(sys.platform.startswith("linux") and shutil.which("gio"), "real GIO desktop-entry launcher")
    def test_desktop_entry_real_gio_preserves_quoted_prefix_path(self):
        # GIO parses the generated Desktop Entry; no mirrored Exec parser.
        prefix_name = 'prefix with "quote" $dollar %percent \\slash ' + chr(96) + 'tick' + chr(96)
        installed, _, receipt, work, prefix, observed = self.desktop_fixture(prefix_name)
        result = cutechess.install_desktop(prefix, cutechess.verify_gui_install(installed, receipt, self.lock, work), installed, work)
        launched = subprocess.run(["gio", "launch", result["desktop"]["path"]],
                                  env=dict(os.environ, XDG_CONFIG_HOME=str(self.root / "gio-settings")),
                                  capture_output=True, text=True, timeout=10)
        self.assertEqual(0, launched.returncode, launched.stderr)
        import time
        deadline = time.monotonic() + 5
        while not observed.exists() and time.monotonic() < deadline:
            time.sleep(.02)
        self.assertTrue(observed.is_file(), "GIO must execute the generated launcher")
        self.assertEqual([str(installed)], json.loads(observed.read_text())["argv"])



    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux bootstrap desktop ownership")
    def test_bootstrap_gui_probe_uses_runner_and_only_root_registers_verified_launcher(self):
        bootstrap = Path(cutechess.__file__).with_name("bootstrap-chess-lab.sh").read_text()
        script = self.root / "desktop-bootstrap.sh"
        script.write_text(bootstrap.removesuffix('main "$@"\n') + r'''
id() { if [[ "$*" == -u ]]; then printf '%s\n' "$FIXTURE_UID"; else command id "$@"; fi; }
resolve_stockfish() { printf '%s\n' /fixture/stockfish; }
resolve_qt_bin() { printf '%s\n' /fixture/qt; }
python3() {
  printf 'python' >> "$CALLS"
  printf ' %q' "$@" >> "$CALLS"
  printf '\n' >> "$CALLS"
  if [[ "$*" == *" --gui "* && "$FAIL_GUI" == 1 ]]; then return 17; fi
}
run_as_owner() {
  printf 'runner' >> "$CALLS"
  printf ' %q' "$@" >> "$CALLS"
  printf '\n' >> "$CALLS"
  "$@"
}
CUTECHESS_GUI_BUILD=1
verify
''')
        for uid, failure, expected in (("0", "0", 0), ("1000", "0", 0), ("0", "1", 1)):
            with self.subTest(uid=uid, failure=failure):
                calls = self.root / "desktop-bootstrap-calls"
                calls.write_text("")
                environment = dict(os.environ, FIXTURE_UID=uid, FAIL_GUI=failure, CALLS=str(calls),
                                   LAPLACE_INSTALL_PREFIX=str(self.root / "selected-prefix"))
                run = subprocess.run(["bash", str(script)], env=environment, capture_output=True, text=True, timeout=10)
                self.assertEqual(expected, run.returncode, run.stderr)
                observed = calls.read_text().splitlines()
                gui = [line for line in observed if " --gui " in line]
                self.assertEqual(2, len(gui), observed)
                self.assertTrue(gui[0].startswith("runner "))
                self.assertTrue(all("--install-desktop" in line and "--desktop-stockfish /fixture/stockfish" in line for line in gui))
                registration = [line for line in observed if "--register-desktop" in line]
                self.assertEqual(1 if uid == "0" and failure == "0" else 0, len(registration), observed)



    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux shared installation directory ownership")
    def test_bootstrap_prepares_existing_public_desktop_directories_for_runner_group(self):
        bootstrap = Path(cutechess.__file__).with_name("bootstrap-chess-lab.sh").read_text()
        script = self.root / "desktop-directories.sh"
        # Select the actual root branch while using the caller's real group.
        # install(1) operates only on this fixture's existing owned directories.
        script.write_text(bootstrap.removesuffix('main "$@"\n') + r'''
id() { if [[ "$*" == -u ]]; then printf '0\n'; else command id "$@"; fi; }
ensure_dirs
''')
        prefix = self.root / "shared-prefix"
        directories = [prefix / suffix for suffix in ("share", "share/applications", "share/laplace")]
        for path in directories:
            path.mkdir(parents=True, exist_ok=True)
            path.chmod(0o755)
        owners = {path: path.stat().st_uid for path in directories}
        group = subprocess.check_output(["id", "-gn"], text=True).strip()
        gid = int(subprocess.check_output(["id", "-g"], text=True))
        environment = dict(os.environ, RUNNER_GROUP=group, LAPLACE_INSTALL_PREFIX=str(prefix),
                           LAPLACE_EXTERNAL=str(self.root / "directory-external"),
                           LAPLACE_CUTECHESS_BUILD=str(self.root / "directory-build"),
                           LAPLACE_QT_ROOT=str(self.root / "directory-qt"),
                           LAPLACE_WORK_ROOT=str(self.root / "directory-work"))
        run = subprocess.run(["bash", str(script)], env=environment, capture_output=True, text=True, timeout=10)
        self.assertEqual(0, run.returncode, run.stderr)
        for path in directories:
            observed = path.stat()
            self.assertEqual(0o2775, observed.st_mode & 0o7777)
            self.assertEqual(gid, observed.st_gid)
            self.assertEqual(owners[path], observed.st_uid)



@unittest.skipUnless(sys.platform.startswith("linux"), "Linux official CuteChess user configuration")
class CuteChessUserEngineTests(unittest.TestCase):
    """Real files/processes; engine executables here are configuration fixtures."""
    def setUp(self):
        spec = importlib.util.spec_from_file_location(
            "cutechess_user_engines", Path(__file__).with_name("cutechess-user-engines.py"))
        self.owner = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.owner)
        parent = Path(os.environ.get("LAPLACE_WORK_ROOT", "/build/laplace/work")) / "tmp"
        parent.mkdir(parents=True, exist_ok=True)
        temporary = tempfile.TemporaryDirectory(prefix="cutechess-user-config-", dir=parent)
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.config = self.root / "settings with Ω"
        self.environment = patch.dict(os.environ, {"XDG_CONFIG_HOME": str(self.config)})
        self.environment.start()
        self.addCleanup(self.environment.stop)
        self.work = self.root / "work"
        self.work.mkdir()
        self.gui = self.root / "gui"
        self.gui.write_text("configuration identity fixture")
        self.catalog = self.root / "engines.json"
        self.selected = []
        for name in ("Stockfish (official)", "Laplace (substrate)"):
            command = self.root / ("command " + name)
            command.write_text("#!/bin/sh\nexit 0\n")
            command.chmod(0o755)
            self.selected.append({"name": name, "command": str(command), "protocol": "uci"})
        self.catalog.write_text(json.dumps(self.selected))
        self.path = self.config / "cutechess/engines.json"

    def prepare(self):
        return self.owner.prepare(self.catalog, self.gui, self.work)

    def test_merge_preserves_all_user_options_and_exact_backup_with_collision_free_names(self):
        self.path.parent.mkdir(parents=True)
        original = [{"name": "Stockfish (official)", "command": "/user/engine", "protocol": "uci",
                     "options": [{"name": "Hash", "value": 321}], "custom": {"unicode": "Ω", "nested": [1, False]}},
                    {"name": "Another", "command": "/user/other", "protocol": "xboard"}]
        raw = ("  " + json.dumps(original, ensure_ascii=False) + "\n").encode()
        self.path.write_bytes(raw)
        lock, env, receipt = self.prepare()
        os.close(lock)
        merged = json.loads(self.path.read_bytes())
        self.assertEqual(original, merged[:2])
        self.assertEqual(["Stockfish (official) (2)", "Laplace (substrate)"], receipt["added_names"])
        self.assertEqual(raw, Path(receipt["backup"]).read_bytes())
        self.assertEqual([item["command"] for item in self.selected], [item["command"] for item in merged[2:]])
        self.assertTrue(all(item["workingDirectory"] == env["TMPDIR"] for item in merged[2:]))
        before = self.path.read_bytes(), self.path.stat().st_mtime_ns
        lock, _, again = self.prepare()
        os.close(lock)
        self.assertEqual(before, (self.path.read_bytes(), self.path.stat().st_mtime_ns))
        self.assertEqual([], again["added_names"])

    def test_existing_selected_engine_user_edits_survive_and_wrong_protocol_is_not_a_match(self):
        self.path.parent.mkdir(parents=True)
        existing = [{**self.selected[0], "name": "My Stockfish", "options": [{"name": "Threads", "value": 7}]},
                    {**self.selected[1], "protocol": "xboard", "name": "User experiment"}]
        self.path.write_text(json.dumps(existing))
        lock, _, receipt = self.prepare()
        os.close(lock)
        merged = json.loads(self.path.read_bytes())
        self.assertEqual(existing, merged[:2])
        self.assertEqual([self.selected[1]["name"]], receipt["added_names"])
        self.assertEqual("uci", merged[-1]["protocol"])

    def test_malformed_duplicate_key_and_symlink_configs_are_preserved(self):
        self.path.parent.mkdir(parents=True)
        for raw in (b"{broken", b'{"name":"not-an-array"}',
                    b'[{"name":"A","name":"B","command":"/a","protocol":"uci"}]',
                    b'[{"name":"A","command":"/a","protocol":"uci","value":NaN}]'):
            with self.subTest(raw=raw):
                self.path.write_bytes(raw)
                with self.assertRaises((ValueError, UnicodeError)):
                    self.prepare()
                self.assertEqual(raw, self.path.read_bytes())
                self.assertEqual([], list(self.path.parent.glob("engines.json.laplace-backup-*")))
        self.path.unlink()
        target = self.root / "foreign-settings"
        target.write_text("[]")
        self.path.symlink_to(target)
        with self.assertRaises(OSError):
            self.prepare()
        self.assertTrue(self.path.is_symlink())
        self.assertEqual("[]", target.read_text())

    def test_existing_backup_is_never_overwritten_and_concurrent_config_change_is_preserved(self):
        self.path.parent.mkdir(parents=True)
        raw = b"[]\n"
        self.path.write_bytes(raw)
        preserved = self.path.with_name(self.path.name + ".laplace-backup-" + self.owner.digest(raw))
        preserved.write_bytes(b"operator-owned conflicting backup")
        with self.assertRaisesRegex(ValueError, "backup differs"):
            self.prepare()
        self.assertEqual(raw, self.path.read_bytes())
        self.assertEqual(b"operator-owned conflicting backup", preserved.read_bytes())
        preserved.unlink()
        real_backup = self.owner.backup
        concurrent = b'[{"name":"Concurrent","command":"/new","protocol":"uci"}]\n'
        def changed(path, original):
            result = real_backup(path, original)
            path.write_bytes(concurrent)
            return result
        with patch.object(self.owner, "backup", side_effect=changed):
            with self.assertRaisesRegex(ValueError, "changed concurrently"):
                self.prepare()
        self.assertEqual(concurrent, self.path.read_bytes())
        self.assertEqual(raw, preserved.read_bytes())

    def test_real_session_lock_survives_exec_until_child_exit(self):
        lock, _, _ = self.prepare()
        child = subprocess.Popen([sys.executable, "-c", "import sys; sys.stdin.read(1)"],
                                 stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                                 stderr=subprocess.PIPE, pass_fds=(lock,))
        os.close(lock)
        before = self.path.read_bytes()
        try:
            with self.assertRaisesRegex(ValueError, "already running"):
                self.prepare()
            self.assertEqual(before, self.path.read_bytes())
        finally:
            child.communicate(b"x", timeout=10)
            if child.poll() is None:
                child.kill()
                child.wait(timeout=5)
        self.assertEqual(0, child.returncode)
        lock, _, receipt = self.prepare()
        os.close(lock)
        self.assertEqual([], receipt["added_names"])

    def test_old_running_executable_replaced_in_place_still_prevents_config_merge(self):
        # Actual native executable lifetime; this is not a chess/Qt game fixture.
        shutil.copy2(shutil.which("sleep"), self.gui)
        child = subprocess.Popen([str(self.gui), "30"])
        try:
            replacement = self.root / "new-gui"
            replacement.write_text("replacement inode")
            os.replace(replacement, self.gui)
            with self.assertRaisesRegex(ValueError, "running CuteChess"):
                self.prepare()
            self.assertFalse(self.path.exists())
        finally:
            child.terminate()
            child.wait(timeout=5)

    def test_per_user_work_logs_are_private_without_changing_existing_config_directory_mode(self):
        self.path.parent.mkdir(parents=True, mode=0o755)
        self.path.parent.chmod(0o755)
        private = self.work / ("cutechess-user-" + str(os.geteuid()))
        (private / "logs").mkdir(parents=True)
        private.chmod(0o777)
        (private / "logs").chmod(0o755)
        lock, env, receipt = self.prepare()
        os.close(lock)
        self.assertEqual(0o700, private.stat().st_mode & 0o777)
        self.assertEqual(0o700, Path(env["LAPLACE_OPS_LOG_DIR"]).stat().st_mode & 0o777)
        self.assertEqual(0o755, self.path.parent.stat().st_mode & 0o777)
        self.assertFalse(receipt["engine_execution_verified"])
        self.assertFalse(receipt["substrate_access_verified"])

    def test_missing_engine_and_bounded_config_refuse_without_partial_engine_list(self):
        Path(self.selected[1]["command"]).unlink()
        with self.assertRaisesRegex(ValueError, "unavailable"):
            self.prepare()
        self.assertFalse(self.path.exists())
        Path(self.selected[1]["command"]).write_text("#!/bin/sh\nexit 0\n")
        Path(self.selected[1]["command"]).chmod(0o755)
        self.path.parent.mkdir(parents=True)
        raw = b"[" + b" " * self.owner.MAXIMUM_CONFIG_BYTES + b"]"
        self.path.write_bytes(raw)
        with self.assertRaisesRegex(ValueError, "byte envelope"):
            self.prepare()
        self.assertEqual(raw, self.path.read_bytes())


if __name__ == '__main__':
    unittest.main()
