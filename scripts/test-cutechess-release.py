#!/usr/bin/env python3
"""Regress shared-source upgrades, dirty-tree preservation and runtime probes."""
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('cutechess', Path(__file__).with_name('provision-cutechess.py'))
cutechess = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cutechess)


class CuteChessReleaseTests(unittest.TestCase):
    def setUp(self):
        self.work = tempfile.TemporaryDirectory(prefix='cutechess-release-test-')
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


if __name__ == '__main__':
    unittest.main()
