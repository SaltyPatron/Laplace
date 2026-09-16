#!/usr/bin/env python3
"""Regression tests for selection precedence and honest corpus acceptance receipts."""
import copy
import importlib.util
import hashlib
import subprocess
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace

SPEC = importlib.util.spec_from_file_location('stockfish_corpus', Path(__file__).with_name('ingest-stockfish-corpus.py'))
CORPUS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CORPUS)


class CorpusAcceptanceTests(unittest.TestCase):
    def receipts(self):
        first = {'schema': 'laplace.verified-git-corpus-admission.v1', 'status': 'verified',
                 'run_id': 'first-fixture-run', 'repository_id': 'fixture-repo', 'provenance_witnesses': 1, 'source': 'RepoDecomposer', 'source_root': '/fixture/source',
                 'provenance_content_id': 'fixture-content', 'provenance_sha256': 'fixture-hash',
                 'provenance': {'commit': 'fixture-commit', 'artifacts': [
                     {'Path': 'src/a.cpp', 'Disposition': 'admitted', 'Sha256': 'a' * 64,
                      'Bytes': 7, 'Modality': 'cpp', 'Representation': 'native-cst'},
                     {'Path': 'fixture.bin', 'Disposition': 'unsupported-with-why-not',
                      'Sha256': 'b' * 64, 'Bytes': 3, 'Modality': None,
                      'Representation': 'unadmitted', 'Reason': 'No native representation'}]}, 'laplace_runtime': {'CoreSha256': 'fixture-core'},
                 'grammar_linkage': 'fixture linkage', 'selected_files': 1, 'tracked_entries': 2,
                 'coverage': {'native_cpp_files': 1, 'native_grammar_files': 1, 'native_partial_cst_files': 0,
                              'native_complete_cst_files': 1, 'raw_only_files': 0,
                              'unadmitted_entries': 1, 'all_tracked_bytes_roundtripped': False},
                 'readbacks': [{'path': 'src/a.cpp', 'sha256': 'a' * 64, 'bytes': 7, 'journal_status': 'ok',
                                'representation': 'native-cst', 'modality': 'cpp', 'syntax_complete': True,
                                'native_ast_nodes': 5, 'native_syntax_nodes': 7, 'native_error_nodes': 0,
                                'native_missing_nodes': 0, 'native_root_has_error': False}],
                 'inserted': {'entities': 3, 'physicalities': 2, 'attestations': 1},
                 'consensus_observations': 1, 'consensus_cells': 1}
        repeated = copy.deepcopy(first)
        repeated['run_id'] = 'repeat-fixture-run'
        repeated['readbacks'][0]['journal_status'] = 'skipped-complete'
        repeated['inserted'] = {'entities': 0, 'physicalities': 0, 'attestations': 0}
        repeated['consensus_observations'] = repeated['consensus_cells'] = 0
        return first, repeated

    def test_exact_repeat_with_explicit_unadmitted_entries_is_supported(self):
        CORPUS.verify_repeat(*self.receipts())

    def test_partial_cpp_and_raw_text_reconcile_without_false_syntax_claims(self):
        first, repeated = self.receipts()
        for receipt in (first, repeated):
            receipt['selected_files'] = 2
            receipt['readbacks'][0].update(syntax_complete=False, native_root_has_error=True,
                                          native_error_nodes=1, native_missing_nodes=2)
            receipt['provenance']['artifacts'][1] = {'Path': 'README.md', 'Disposition': 'admitted',
                'Sha256': 'c' * 64, 'Bytes': 4, 'Modality': 'text', 'Representation': 'raw-text'}
            receipt['readbacks'].append({'path': 'README.md', 'representation': 'raw-text',
                                        'sha256': 'c' * 64, 'bytes': 4,
                                        'modality': 'text', 'syntax_complete': None,
                                        'journal_status': receipt['readbacks'][0]['journal_status']})
            receipt['coverage'].update(native_partial_cst_files=1, native_complete_cst_files=0,
                                       raw_only_files=1, unadmitted_entries=0,
                                       all_tracked_bytes_roundtripped=True)
        CORPUS.verify_repeat(first, repeated)
        repeated['readbacks'][0]['syntax_complete'] = True
        with self.assertRaisesRegex(ValueError, 'Syntax completeness'):
            CORPUS.verify_repeat(first, repeated)

    def test_raw_text_cannot_claim_a_native_syntax_tree(self):
        first, _ = self.receipts()
        first['readbacks'].append({'path': 'README.md', 'representation': 'raw-text',
                                  'modality': 'text', 'syntax_complete': True})
        first['selected_files'] = 2
        with self.assertRaisesRegex(ValueError, 'Raw-only'):
            CORPUS.verify_coverage(first)

    def test_complete_coverage_cannot_hide_unadmitted_entries(self):
        first, _ = self.receipts()
        first['coverage']['all_tracked_bytes_roundtripped'] = True
        with self.assertRaisesRegex(ValueError, 'tracked artifact'):
            CORPUS.verify_coverage(first)

    def test_changed_loaded_native_runtime_rejects_repeat(self):
        first, repeated = self.receipts()
        repeated['laplace_runtime']['CoreSha256'] = 'different-core'
        with self.assertRaisesRegex(ValueError, 'identity'):
            CORPUS.verify_repeat(first, repeated)

    def test_repeat_rejects_missing_native_cpp(self):
        first, repeated = self.receipts()
        first['coverage']['native_cpp_files'] = 0
        with self.assertRaisesRegex(ValueError, 'C\\+\\+'):
            CORPUS.verify_repeat(first, repeated)

    def test_repeat_rejects_evidence_amplification(self):
        for field in ('consensus_observations', 'consensus_cells'):
            with self.subTest(field=field):
                first, repeated = self.receipts()
                repeated[field] = 1
                with self.assertRaisesRegex(ValueError, 'amplified'):
                    CORPUS.verify_repeat(first, repeated)

    def test_repeat_rejects_new_substrate_rows(self):
        for field in ('entities', 'physicalities', 'attestations'):
            with self.subTest(field=field):
                first, repeated = self.receipts()
                repeated['inserted'][field] = 1
                with self.assertRaisesRegex(ValueError, 'inserted'):
                    CORPUS.verify_repeat(first, repeated)

    def test_repeat_requires_distinct_run(self):
        first, repeated = self.receipts()
        repeated['run_id'] = first['run_id']
        with self.assertRaisesRegex(ValueError, 'distinct'):
            CORPUS.verify_repeat(first, repeated)

    def test_repeat_rejects_changed_build_or_source(self):
        first, repeated = self.receipts()
        repeated['provenance']['commit'] = 'different'
        with self.assertRaisesRegex(ValueError, 'identity'):
            CORPUS.verify_repeat(first, repeated)

    def test_repeat_rejects_failed_or_incomplete_readback(self):
        for mutation in (lambda r: r.update(status='failed'), lambda r: r['readbacks'].clear()):
            first, repeated = self.receipts()
            mutation(repeated)
            with self.assertRaises(ValueError):
                CORPUS.verify_repeat(first, repeated)

    def test_repeat_rejects_same_counts_with_changed_content(self):
        first, repeated = self.receipts()
        repeated['readbacks'][0]['sha256'] = 'different'
        with self.assertRaisesRegex(ValueError, 'readback differs'):
            CORPUS.verify_repeat(first, repeated)

    def test_repeat_requires_actual_resume(self):
        first, repeated = self.receipts()
        repeated['readbacks'][0]['journal_status'] = 'ok'
        with self.assertRaisesRegex(ValueError, 'completion'):
            CORPUS.verify_repeat(first, repeated)

    def test_source_override_uses_existing_doctor_precedence(self):
        doctor = CORPUS.module('check-chess-dependencies')
        with tempfile.TemporaryDirectory(prefix='stockfish-corpus-config-') as temporary:
            prefix = Path(temporary)
            (prefix / 'app').mkdir()
            (prefix / 'app/laplace-api.env').write_text('LAPLACE_STOCKFISH=/installed/src/stockfish\nLAPLACE_STOCKFISH_SOURCE=/installed\n')
            with patch.dict(os.environ, {'LAPLACE_STOCKFISH_SOURCE': '/operator/SF_19'}, clear=True):
                config = doctor.configuration(prefix)
            self.assertEqual('/operator/SF_19', config['LAPLACE_STOCKFISH_SOURCE'])
            self.assertNotIn('LAPLACE_STOCKFISH', config)

    def test_default_cli_resolves_redirected_msbuild_target(self):
        target = '/fixture/redirected/app/bin/Laplace.Cli/Release/net10.0/Laplace.Cli.dll'
        with patch.object(CORPUS.subprocess, 'run', return_value=SimpleNamespace(stdout=target + '\n')) as run:
            self.assertEqual(Path(target).with_suffix('.exe' if os.name == 'nt' else ''), CORPUS.default_cli())
            self.assertIn('-getProperty:TargetPath', run.call_args.args[0])
            self.assertIn('-property:Configuration=Release', run.call_args.args[0])

    def test_default_cli_rejects_ambiguous_target_output(self):
        for target in ('relative/Laplace.Cli.dll', 'warning then path\n/fixture/Laplace.Cli.dll'):
            with self.subTest(target=target), patch.object(CORPUS.subprocess, 'run', return_value=SimpleNamespace(stdout=target)):
                with self.assertRaisesRegex(ValueError, 'absolute'):
                    CORPUS.default_cli()

    def test_output_inside_checkout_rejects_before_any_ingest_or_write(self):
        with tempfile.TemporaryDirectory(prefix='stockfish-corpus-output-') as temporary:
            source = Path(temporary)
            with patch.object(CORPUS, 'selection', return_value=(source, {})), patch.object(CORPUS.subprocess, 'run') as run:
                with self.assertRaisesRegex(ValueError, 'outside'):
                    CORPUS.execute(Path('/fixture/prefix'), source / 'sidecar', Path('/fixture/cli'))
                run.assert_not_called()
                self.assertFalse((source / 'sidecar').exists())


    def execute_fixture(self, temporary, *, full=False, coverage='all-tracked'):
        root = Path(temporary)
        source = root / 'source'
        source.mkdir()
        first, repeated = self.receipts()
        for receipt in (first, repeated):
            receipt['laplace_runtime']['CorePath'] = '/fixture/liblaplace_core.so'
            if full:
                receipt['tracked_entries'] = receipt['selected_files']
                receipt['provenance']['artifacts'] = receipt['provenance']['artifacts'][:1]
                receipt['coverage'].update(unadmitted_entries=0, all_tracked_bytes_roundtripped=True)
        output = root / 'proof'
        pending = iter((first, repeated))
        def ingest(argv, **kwargs):
            path = Path(argv[argv.index('--git-corpus-receipt') + 1])
            path.write_text(json.dumps(next(pending)))
            return SimpleNamespace(returncode=0)
        selected = {'Upstream': 'https://github.com/official-stockfish/Stockfish', 'Commit': 'fixture-commit'}
        with patch.object(CORPUS, 'selection', return_value=(source, selected)), \
             patch.object(CORPUS.subprocess, 'run', side_effect=ingest), \
             patch.object(CORPUS.chess_corpus_inventory, 'observe',
                          return_value={'loaded_core': {'matches_receipt_bytes': True}}):
            CORPUS.execute(Path('/fixture/prefix'), output, Path('/fixture/cli'), coverage)
        return output

    def test_default_full_corpus_refuses_partial_selection_and_retains_exact_partial_receipts(self):
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaisesRegex(ValueError, 'verified only 1 of 2'):
                self.execute_fixture(temporary)
            output = Path(temporary) / 'proof'
            retained = json.loads((output / 'receipt.json').read_text())
            self.assertEqual('verified-partial', retained['status'])
            self.assertEqual('selected-files', retained['coverage_scope'])
            self.assertEqual('all-tracked', retained['requested_coverage'])
            self.assertFalse(retained['full_tracked_corpus'])
            self.assertEqual((1, 2), (retained['selected_files'], retained['tracked_entries']))
            self.assertTrue(retained['native_exact_readback'])
            self.assertTrue(retained['repeat_without_amplification'])
            self.assertTrue((output / 'admission.json').is_file())
            self.assertTrue((output / 'repeat.json').is_file())
            self.assertTrue((output / 'runtime-inventory.json').is_file())

    def test_explicit_selected_mode_preserves_legitimate_partial_proof_without_full_claim(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = self.execute_fixture(temporary, coverage='selected')
            retained = json.loads((output / 'receipt.json').read_text())
            self.assertEqual('verified-partial', retained['status'])
            self.assertEqual('selected', retained['requested_coverage'])
            self.assertFalse(retained['full_tracked_corpus'])
            self.assertEqual(1, retained['coverage']['unadmitted_entries'])

    def test_full_tracked_selection_qualifies_after_exact_admission_and_repeat(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = self.execute_fixture(temporary, full=True)
            retained = json.loads((output / 'receipt.json').read_text())
            self.assertEqual('verified', retained['status'])
            self.assertEqual('all-tracked', retained['coverage_scope'])
            self.assertTrue(retained['full_tracked_corpus'])
            self.assertEqual(retained['selected_files'], retained['tracked_entries'])
            self.assertEqual(0, retained['coverage']['unadmitted_entries'])

    def test_manifest_and_selection_counts_must_be_actual_integers(self):
        for field in ('selected_files', 'tracked_entries'):
            for invalid in (True, 1.0, '1', None):
                with self.subTest(field=field, invalid=invalid):
                    receipt, _ = self.receipts()
                    receipt[field] = invalid
                    with self.assertRaises(ValueError):
                        CORPUS.verify_coverage(receipt)


    def test_full_arithmetic_cannot_hide_a_changed_or_duplicated_manifest_selection(self):
        for mutation in (
            lambda r: r['provenance'].pop('artifacts'),
            lambda r: r['provenance']['artifacts'].__setitem__(1, copy.deepcopy(r['provenance']['artifacts'][0])),
            lambda r: r['provenance']['artifacts'][0].update(Path='src/other.cpp'),
            lambda r: r['provenance']['artifacts'][0].update(Disposition='unsupported-with-why-not'),
            lambda r: r['readbacks'][0].update(bytes=8),
            lambda r: r['readbacks'][0].update(sha256='changed-bytes'),
        ):
            receipt, _ = self.receipts()
            mutation(receipt)
            with self.assertRaises(ValueError):
                CORPUS.verify_coverage(receipt)

    def test_duplicate_readback_cannot_replace_another_tracked_file(self):
        receipt, _ = self.receipts()
        artifact = copy.deepcopy(receipt['provenance']['artifacts'][0])
        artifact['Path'] = 'src/b.cpp'
        receipt['provenance']['artifacts'][1] = artifact
        receipt['readbacks'].append(copy.deepcopy(receipt['readbacks'][0]))
        receipt['selected_files'] = 2
        receipt['coverage'].update(native_cpp_files=2, native_grammar_files=2,
            native_complete_cst_files=2, unadmitted_entries=0, all_tracked_bytes_roundtripped=True)
        with self.assertRaisesRegex(ValueError, 'exact selected manifest'):
            CORPUS.verify_coverage(receipt)

    def test_selected_body_evidence_cannot_pass_when_both_sides_are_missing_or_invalid(self):
        for field, source, invalid_values in (
            ('sha256', 'Sha256', (None, True, 1, '', 'a' * 63, 'a' * 65, 'A' * 64, 'g' * 64)),
            ('bytes', 'Bytes', (None, True, False, 0, -1, 7.0, '7')),
        ):
            for value in invalid_values:
                with self.subTest(field=field, value=value):
                    receipt, _ = self.receipts()
                    receipt['readbacks'][0][field] = value
                    receipt['provenance']['artifacts'][0][source] = value
                    with self.assertRaisesRegex(ValueError, 'Selected source'):
                        CORPUS.verify_coverage(receipt)
            with self.subTest(field=field, absent=True):
                receipt, _ = self.receipts()
                del receipt['readbacks'][0][field]
                del receipt['provenance']['artifacts'][0][source]
                with self.assertRaisesRegex(ValueError, 'Selected source'):
                    CORPUS.verify_coverage(receipt)

    def test_equal_numeric_value_does_not_hide_invalid_manifest_or_readback_byte_type(self):
        for manifest_side in (False, True):
            with self.subTest(manifest_side=manifest_side):
                receipt, _ = self.receipts()
                if manifest_side:
                    receipt['provenance']['artifacts'][0]['Bytes'] = 7.0
                else:
                    receipt['readbacks'][0]['bytes'] = 7.0
                with self.assertRaisesRegex(ValueError, 'Selected source byte count'):
                    CORPUS.verify_coverage(receipt)

    def test_empty_unadmitted_tracked_file_remains_explicit_partial_inventory(self):
        first, repeated = self.receipts()
        for receipt in (first, repeated):
            receipt['provenance']['artifacts'][1].update(Bytes=0,
                Sha256=hashlib.sha256(b'').hexdigest(),
                Reason='Empty source has no native grammar composition; bytes and Git identity are retained.')
        CORPUS.verify_repeat(first, repeated)
        self.assertEqual(1, first['coverage']['unadmitted_entries'])
        self.assertFalse(first['coverage']['all_tracked_bytes_roundtripped'])


inventory = CORPUS.chess_corpus_inventory


class InventoryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='stockfish-corpus-inventory-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.engine = self.root / 'build/engine'
        self.engine.joinpath('core').mkdir(parents=True)
        self.loaded = self.root / 'published/liblaplace_core.so'
        self.loaded.parent.mkdir()
        self.loaded.write_bytes(b'actual fixture core bytes')
        self.sha = hashlib.sha256(self.loaded.read_bytes()).hexdigest()
        (self.engine / 'core/liblaplace_core.so').write_bytes(self.loaded.read_bytes())
        self.external = self.root / 'external'
        self.external.mkdir()
        self.cache = self.root / 'build/CMakeCache.txt'
        self.cache.write_text(f'LAPLACE_EXTERNAL:PATH={self.external}\nPRIVATE_TOKEN:STRING=never-return-this-token\n')
        self.env = patch.dict(os.environ, {}, clear=True)
        self.env.start()
        self.addCleanup(self.env.stop)

    def observe(self):
        return inventory.observe(self.root, self.loaded, self.sha)

    def git_fixture(self, relative, origin):
        directory = self.external / relative
        directory.mkdir(parents=True)
        def run(*args):
            return subprocess.check_output(['git','-C',str(directory),*args],stderr=subprocess.DEVNULL,text=True).strip()
        run('init','-q')
        run('config','user.name','Inventory fixture')
        run('config','user.email','fixture@example.invalid')
        (directory/'source.c').write_text('fixture source')
        run('add','source.c')
        run('commit','-qm','fixture')
        run('remote','add','origin',origin)
        return directory, run('rev-parse','HEAD')

    def test_records_matching_bytes_without_claiming_compilation(self):
        result=self.observe()
        self.assertTrue(result['configured_build_core']['matches_loaded_core_bytes'])
        self.assertFalse(result['current_checkouts_proven_inputs_of_loaded_core'])
        self.assertEqual(str(self.external),result['external']['root'])
        self.assertNotIn('never-return-this-token',json.dumps(result))

    def test_loaded_or_build_byte_changes_do_not_match_receipt(self):
        self.loaded.write_bytes(b'changed')
        self.assertFalse(self.observe()['configured_build_core']['matches_loaded_core_bytes'])
        self.assertFalse(self.observe()['loaded_core']['matches_receipt_bytes'])

    def test_missing_cache_never_substitutes_environment_external(self):
        self.cache.unlink()
        os.environ['LAPLACE_EXTERNAL']=str(self.external)
        self.assertEqual('not-selected-by-observed-cache',self.observe()['external']['status'])

    def test_explicit_engine_prefers_own_cache_and_core(self):
        selected=self.root/'separate'
        (selected/'core').mkdir(parents=True)
        (selected/'core/liblaplace_core.so').write_bytes(self.loaded.read_bytes())
        (selected/'CMakeCache.txt').write_text(f'LAPLACE_EXTERNAL:PATH={self.external}\n')
        os.environ['LAPLACE_ENGINE_BUILD']=str(selected)
        result=self.observe()
        self.assertEqual(str(selected/'CMakeCache.txt'),result['cmake_cache']['path'])
        self.assertTrue(result['configured_build_core']['matches_loaded_core_bytes'])

    def test_duplicate_cache_selection_is_reported_ambiguous(self):
        with self.cache.open('a') as f:f.write(f'LAPLACE_EXTERNAL:STRING={self.external}\n')
        self.assertEqual('not-selected-by-observed-cache',self.observe()['external']['status'])

    def test_real_git_and_selected_pins_are_separate_observations(self):
        directory,head=self.git_fixture('tree-sitter','git@github.com:tree-sitter/tree-sitter.git')
        (self.external/'PINS.tsv').write_text(f'external/tree-sitter\thttps://github.com/tree-sitter/tree-sitter\t{head}\nexternal/other\thttps://secret:never-return-other@example.invalid/private\t{head}\n')
        result=self.observe()['external']
        self.assertEqual(1,len(result['pins']['selected_entries']))
        self.assertEqual('https://github.com/tree-sitter/tree-sitter',result['checkouts']['runtime']['origin']['url'])
        self.assertTrue(result['checkouts']['runtime']['head_matches_pin'])
        self.assertTrue(result['checkouts']['runtime']['tracked_worktree_clean'])
        (directory/'source.c').write_text('changed tracked source')
        self.assertFalse(self.observe()['external']['checkouts']['runtime']['tracked_worktree_clean'])
        self.assertNotIn('never-return-other',json.dumps(result))

    def test_credentials_and_malformed_urls_are_redacted(self):
        for value in ['https://user:secret@github.com/owner/repo','https://github.com/owner/repo?token=secret','https://[malformed','ssh://secret@example.invalid/private']:
            self.assertEqual({'status':'redacted'},inventory._public_origin(value))

    def test_duplicate_pin_does_not_claim_unique_match(self):
        _,head=self.git_fixture('tree-sitter','https://user:never-expose@github.com/tree-sitter/tree-sitter')
        line=f'external/tree-sitter\thttps://user:never-expose@github.com/tree-sitter/tree-sitter\t{head}\n'
        (self.external/'PINS.tsv').write_text(line*2)
        result=self.observe()
        self.assertIsNone(result['external']['checkouts']['runtime']['head_matches_pin'])
        self.assertEqual(2,len(result['external']['pins']['selected_entries']))
        self.assertNotIn('never-expose',json.dumps(result))


if __name__ == '__main__':
    unittest.main()
