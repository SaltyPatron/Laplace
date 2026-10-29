#!/usr/bin/env python3
"""Read the installed service through its authenticated CLI without admitting test content."""
import argparse
import base64
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
from urllib.parse import urlsplit


DOCUMENT = 'Laplace installed client verification.\nUnicode: café λ 中.\n'.encode('utf-8')


def verify(cli: Path, base: str, work: Path, *, document_roundtrip: bool = False) -> None:
    if not cli.is_absolute() or not cli.is_file():
        raise ValueError('Select the absolute path of the installed CLI executable')
    if not work.is_absolute() or not work.is_dir():
        raise ValueError('Select an existing absolute work directory')
    origin = urlsplit(base)
    if (origin.scheme != 'https' or not origin.hostname or origin.username or origin.password
            or origin.path not in ('', '/') or origin.query or origin.fragment):
        raise ValueError('Installed API client verification requires an HTTPS origin')
    key = os.environ.get('LAPLACE_API_KEY', '').strip()
    if not key or any(character.isspace() for character in key):
        raise ValueError('Run with an authenticated verification credential')

    with tempfile.TemporaryDirectory(prefix='installed-api-client-', dir=work) as temporary:
        root = Path(temporary)
        key_file = root / 'api-key'
        descriptor = os.open(key_file, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(descriptor, 'w', encoding='utf-8') as stream:
            stream.write(key)
        environment = {name: value for name, value in os.environ.items()
                       if not name.upper().startswith('PG') and name.upper() not in {
                           'LAPLACE_API_KEY', 'LAPLACE_OPERATOR_TOKEN', 'LAPLACE_DB', 'LAPLACE_APP_DB',
                           'LAPLACE_ENGINE_BUILD', 'LAPLACE_EXT_BUILD', 'LAPLACE_BUILD_ROOT',
                           'LAPLACE_NATIVE_DIR', 'LAPLACE_PERFCACHE_BIN'}}
        environment.update(LAPLACE_API_URL=base, LAPLACE_API_KEY_FILE=str(key_file))

        def invoke(*arguments: str, read_json: bool = True):
            try:
                result = subprocess.run([str(cli), '--api-client', *arguments], cwd=root,
                                        env=environment, capture_output=True, text=True,
                                        encoding='utf-8', timeout=120, check=False)
            except subprocess.TimeoutExpired:
                raise ValueError(f'Installed client {arguments[0]} timed out; no retry was attempted') from None
            if result.returncode:
                status = re.search(r'\bHTTP (\d{3})\b', result.stderr)
                detail = f' (HTTP {status.group(1)})' if status else ''
                raise ValueError(f'Installed client {arguments[0]} failed with exit code {result.returncode}{detail}')
            return json.loads(result.stdout) if read_json else None

        catalog = invoke('ops')
        if (not isinstance(catalog, dict) or catalog.get('object') != 'op.catalog'
                or not isinstance(catalog.get('operations'), list) or not catalog['operations']):
            raise ValueError('Installed client returned an empty or invalid operation catalog')
        counts = invoke('op', 'ops.substrate_counts', '--max-rows', '20')
        if counts.get('object') != 'op.result' or counts.get('name') != 'ops.substrate_counts':
            raise ValueError('Installed client returned the wrong typed operation result')
        print('Installed client substrate inventory: ' + json.dumps(counts.get('rows'), ensure_ascii=True))
        # Routine deployment/health verification must not populate the operator's
        # corpus or journal. Exercise admission explicitly on an isolated test
        # installation; deleting shared content afterwards cannot undo provenance.
        if not document_roundtrip:
            return
        source = root / 'laplace-client-verification.txt'
        source.write_bytes(DOCUMENT)
        # Stable content, relative path and modification time keep the admitted fixture reusable.
        os.utime(source, (946684800, 946684800))
        admission = invoke('ingest', 'document', source.name, '--verify')
        identity = admission.get('file_id', '')
        if not isinstance(identity, str) or not re.fullmatch(r'[0-9a-fA-F]{32}', identity):
            raise ValueError('Installed client did not return a content admission identity')
        if admission.get('bytes') != len(DOCUMENT):
            raise ValueError('Installed client admitted a different byte count')
        content = invoke('content', 'get', identity)
        if (content.get('file_id') != identity
                or base64.b64decode(content.get('content_base64', ''), validate=True) != DOCUMENT):
            raise ValueError('Installed client JSON readback differs from the admitted document')
        destination = root / 'exported.txt'
        invoke('content', 'export', identity, str(destination), read_json=False)
        if destination.read_bytes() != DOCUMENT:
            raise ValueError('Installed client exported bytes differ from the admitted document')


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cli', required=True, type=Path)
    parser.add_argument('--base', required=True)
    parser.add_argument('--work', required=True, type=Path)
    parser.add_argument('--document-roundtrip', action='store_true',
                        help='Admit and export a sample document; use an isolated test installation')
    args = parser.parse_args()
    try:
        verify(args.cli, args.base, args.work, document_roundtrip=args.document_roundtrip)
    except (OSError, ValueError, TypeError, AttributeError) as error:
        print(f'Installed API client verification failed: {error}')
        return 1
    checks = 'catalog and typed operation'
    if args.document_roundtrip:
        checks += ', document admission, JSON readback and exported bytes'
    print(f'Installed API client: {checks} passed.')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
