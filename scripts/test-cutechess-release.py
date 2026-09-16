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


if __name__ == '__main__':
    unittest.main()
