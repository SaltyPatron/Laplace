#!/usr/bin/env python3
"""Admit the configured official Git checkout through the common native repo lane.

Builds are owned by install-stockfish.py. This command only reads that source and
its retained build receipt, then requires two actual PostgreSQL readbacks. A failed
or interrupted run retains its partial evidence and cannot claim corpus readiness.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
from lib import chess_corpus_inventory

ROOT = Path(__file__).resolve().parents[1]


def module(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), ROOT / 'scripts' / (name + '.py'))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


def selection(prefix):
    doctor, installer = module('check-chess-dependencies'), module('install-stockfish')
    config = doctor.configuration(prefix)
    source = Path(config.get('LAPLACE_STOCKFISH_SOURCE') or
                  str(Path(config.get('LAPLACE_EXTERNAL') or installer.external_root()) / 'stockfish')).resolve(strict=True)
    binary = Path(config.get('LAPLACE_STOCKFISH') or installer.binary_path(source)).absolute()
    lock = installer.load_lock()
    git_path = subprocess.run(['git', '--no-optional-locks', '-c', 'safe.directory=' + str(source),
                               '-C', str(source), 'rev-parse', '--git-path', 'laplace-stockfish-build.json'],
                              check=True, capture_output=True, text=True).stdout.strip()
    build_receipt = Path(git_path)
    if not build_receipt.is_absolute():
        build_receipt = source / build_receipt
    return source, {'Upstream': lock['repository'].removesuffix('.git'), 'Commit': lock['commit'],
                    'License': 'GPL-3.0', 'RequiredModality': 'cpp', 'BinaryPath': str(binary),
                    'BuildReceiptPath': str(build_receipt.absolute())}


def verify_coverage(receipt):
    rows = receipt.get('readbacks', [])
    coverage = receipt.get('coverage', {})
    if coverage.get('native_cpp_files', 0) < 1:
        raise ValueError('Native C++ corpus coverage is missing')
    if (type(receipt.get('selected_files')) is not int or receipt['selected_files'] <= 0 or
            receipt['selected_files'] != len(rows)):
        raise ValueError('Native readback does not cover all selected files')
    native, partial, raw, cpp = 0, 0, 0, 0
    diagnostics = ('native_ast_nodes', 'native_syntax_nodes', 'native_error_nodes', 'native_missing_nodes')
    for row in rows:
        if row.get('representation') == 'raw-text':
            raw += 1
            if row.get('modality') != 'text' or any(row.get(field) is not None for field in
                    (*diagnostics, 'native_root_has_error', 'syntax_complete')):
                raise ValueError('Raw-only coverage must not claim grammar diagnostics')
        elif row.get('representation') == 'native-cst':
            native += 1
            cpp += row.get('modality') == 'cpp'
            if any(type(row.get(field)) is not int or row[field] < 0 for field in diagnostics):
                raise ValueError('Native syntax diagnostics are absent or invalid')
            if not row['native_ast_nodes'] or not row['native_syntax_nodes'] or type(row.get('native_root_has_error')) is not bool:
                raise ValueError('Native grammar did not produce an observed source tree')
            complete = not (row['native_root_has_error'] or row['native_error_nodes'] or row['native_missing_nodes'])
            if row.get('syntax_complete') is not complete:
                raise ValueError('Syntax completeness differs from actual native diagnostics')
            partial += not complete
        else:
            raise ValueError('Readback representation is absent or unsupported')
    expected = {'native_grammar_files': native, 'native_partial_cst_files': partial,
                'native_complete_cst_files': native - partial, 'raw_only_files': raw, 'native_cpp_files': cpp}
    if any(coverage.get(field) != value for field, value in expected.items()):
        raise ValueError('Declared coverage does not reconcile with native readbacks')
    if type(receipt.get('tracked_entries')) is not int or receipt['tracked_entries'] < len(rows):
        raise ValueError('Tracked manifest count is absent or invalid')
    unadmitted = receipt['tracked_entries'] - len(rows)
    if unadmitted < 0 or coverage.get('unadmitted_entries') != unadmitted or coverage.get('all_tracked_bytes_roundtripped') is not (unadmitted == 0):
        raise ValueError('Complete tracked artifact coverage does not reconcile')


    artifacts = receipt.get('provenance', {}).get('artifacts')
    if not isinstance(artifacts, list) or len(artifacts) != receipt['tracked_entries']:
        raise ValueError('Tracked manifest entries do not reconcile with the receipt')
    manifest = {}
    for artifact in artifacts:
        if not isinstance(artifact, dict) or not isinstance(artifact.get('Path'), str) or not artifact['Path']:
            raise ValueError('Tracked manifest path is absent or invalid')
        if artifact['Path'] in manifest:
            raise ValueError('Tracked manifest contains a duplicate path')
        if artifact.get('Disposition') not in ('admitted', 'unsupported-with-why-not'):
            raise ValueError('Tracked manifest disposition is absent or unsupported')
        manifest[artifact['Path']] = artifact
    selected = {path for path, artifact in manifest.items() if artifact['Disposition'] == 'admitted'}
    readback_paths = [row.get('path') for row in rows]
    if (len(set(readback_paths)) != len(readback_paths) or set(readback_paths) != selected or
            len(selected) != receipt['selected_files']):
        raise ValueError('Native readback paths do not cover the exact selected manifest')
    for row in rows:
        artifact = manifest[row['path']]
        for field, source in (('sha256', 'Sha256'), ('bytes', 'Bytes'), ('modality', 'Modality'),
                              ('representation', 'Representation')):
            if row.get(field) != artifact.get(source):
                raise ValueError('Native readback differs from tracked manifest: ' + row['path'])
        for body, hash_field, byte_field in ((row, 'sha256', 'bytes'), (artifact, 'Sha256', 'Bytes')):
            digest, length = body.get(hash_field), body.get(byte_field)
            if (not isinstance(digest, str) or len(digest) != 64 or
                    any(character not in '0123456789abcdef' for character in digest)):
                raise ValueError('Selected source SHA256 is absent or invalid: ' + row['path'])
            # VerifiedGitRepository retains empty files as unsupported-with-why-not;
            # only nonempty selected/admitted bodies can carry this native readback.
            if type(length) is not int or length <= 0:
                raise ValueError('Selected source byte count is absent or invalid: ' + row['path'])


def verify_repeat(first, repeated):
    for receipt in (first, repeated):
        if receipt.get('schema') != 'laplace.verified-git-corpus-admission.v1' or receipt.get('status') != 'verified':
            raise ValueError('Native admission/readback receipt is absent or unsuccessful')
        verify_coverage(receipt)
    if first['run_id'] == repeated['run_id']:
        raise ValueError('Repeat requires a distinct actual ingest run')
    for field in ('source', 'source_root', 'provenance_content_id', 'provenance_sha256',
                  'provenance', 'selected_files', 'tracked_entries', 'coverage', 'repository_id', 'provenance_witnesses',
                  'laplace_runtime', 'grammar_linkage'):
        if first[field] != repeated[field]:
            raise ValueError('Repeat changed verified corpus identity: ' + field)
    expected = [{k: v for k, v in row.items() if k != 'journal_status'} for row in first['readbacks']]
    actual = [{k: v for k, v in row.items() if k != 'journal_status'} for row in repeated['readbacks']]
    if expected != actual:
        raise ValueError('Repeat native source readback differs from the first admission')
    if any(row.get('journal_status') != 'skipped-complete' for row in repeated['readbacks']):
        raise ValueError('Repeat did not use existing per-file completion proofs')
    if repeated['inserted'] != {'entities': 0, 'physicalities': 0, 'attestations': 0}:
        raise ValueError('Repeat inserted substrate rows')
    if repeated.get('consensus_observations') != 0 or repeated.get('consensus_cells') != 0:
        raise ValueError('Repeat amplified consensus evidence')


def execute(prefix, output, cli, coverage='all-tracked'):
    if coverage not in ('all-tracked', 'selected'):
        raise ValueError('Unsupported requested corpus coverage')
    source, selected = selection(prefix)
    output = output.absolute()
    if output == source or source in output.resolve().parents:
        raise ValueError('Evidence output must be outside the official source checkout')
    output.mkdir(parents=True, exist_ok=False)
    select_path = output / 'selection.private.json'
    # This file can contain private local paths, never upstream remote credentials.
    with select_path.open('x') as stream:
        json.dump(selected, stream, indent=2)
    os.chmod(select_path, 0o600)
    receipts = []
    for name in ('admission', 'repeat'):
        env = os.environ.copy()
        env['LAPLACE_INGEST_RUN_RECEIPT_PATH'] = str(output / (name + '-run.json'))
        command = [str(cli), 'ingest', 'repo', str(source), '--no-analyze',
                   '--git-corpus-selection', str(select_path),
                   '--git-corpus-receipt', str(output / (name + '.json'))]
        with (output / (name + '.log')).open('xb') as log:
            completed = subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT)
        if completed.returncode:
            raise RuntimeError(f'{name} failed with exit {completed.returncode}; raw log: {output / (name + ".log")}')
        receipts.append(json.loads((output / (name + '.json')).read_text()))
    verify_repeat(*receipts)
    runtime = receipts[0]['laplace_runtime']
    inventory = chess_corpus_inventory.observe(ROOT, runtime['CorePath'], runtime['CoreSha256'])
    inventory_bytes = (json.dumps(inventory, indent=2) + '\n').encode()
    (output / 'runtime-inventory.json').write_bytes(inventory_bytes)
    if not inventory['loaded_core']['matches_receipt_bytes']:
        raise ValueError('Loaded native core no longer matches the actual admission/readback receipt')
    full = receipts[0]['coverage']['all_tracked_bytes_roundtripped']
    status = 'verified' if full else 'verified-partial'
    proof = {'schema': 'laplace.stockfish-corpus-proof.v1', 'status': status,
             'requested_coverage': coverage,
             'coverage_scope': 'all-tracked' if full else 'selected-files',
             'full_tracked_corpus': full,
             'upstream': selected['Upstream'], 'commit': selected['Commit'],
             'admission_run_id': receipts[0]['run_id'], 'repeat_run_id': receipts[1]['run_id'],
             'provenance_content_id': receipts[0]['provenance_content_id'],
             'provenance_sha256': receipts[0]['provenance_sha256'],
             'selected_files': receipts[0]['selected_files'],
             'tracked_entries': receipts[0]['tracked_entries'],
             'coverage': receipts[0]['coverage'],
             'laplace_runtime': runtime,
             'runtime_inventory': {'path': 'runtime-inventory.json',
                                   'sha256': hashlib.sha256(inventory_bytes).hexdigest()},
             'native_exact_readback': True, 'repeat_without_amplification': True,
             'playing_strength_proved': False}
    with (output / 'receipt.json').open('x') as stream:
        json.dump(proof, stream, indent=2)
    print(json.dumps({'status': status, 'coverage_scope': proof['coverage_scope'],
                      'selected_files': proof['selected_files'],
                      'tracked_entries': proof['tracked_entries'],
                      'receipt': str(output / 'receipt.json')}))
    if coverage == 'all-tracked' and not full:
        raise ValueError('Full tracked Stockfish corpus required; verified only '
                         f"{proof['selected_files']} of {proof['tracked_entries']} tracked entries. "
                         'Partial admission and repeat evidence have been retained.')


def default_cli():
    # Ask the same MSBuild project which artifact it built, including supported
    # LAPLACE_BUILD_ROOT redirection and platform apphost naming.
    target = subprocess.run(['dotnet', 'msbuild', str(ROOT / 'app/Laplace.Cli/Laplace.Cli.csproj'),
                             '-nologo', '-property:Configuration=Release', '-getProperty:TargetPath'],
                            check=True, capture_output=True, text=True).stdout.strip()
    dll = Path(target)
    if not dll.is_absolute() or dll.suffix != '.dll':
        raise ValueError('MSBuild did not resolve an absolute CLI TargetPath')
    return dll.with_suffix('.exe' if os.name == 'nt' else '')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--prefix', type=Path, default=Path(os.environ.get('LAPLACE_INSTALL_PREFIX', '/opt/laplace')))
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--cli', type=Path, help='Exact candidate or installed CLI executable; default resolves the common Release build through MSBuild')
    parser.add_argument('--coverage', choices=('all-tracked', 'selected'), default='all-tracked',
                        help='Require every tracked entry by default; selected explicitly permits a partial proof')
    args = parser.parse_args()
    try:
        execute(args.prefix, args.output, args.cli or default_cli(), args.coverage)
    except (OSError, ValueError, KeyError, RuntimeError, subprocess.SubprocessError) as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
