#!/usr/bin/env python3
"""Install or select the chess conductor in the declared dependency directory."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import tarfile
import tempfile
from urllib.request import urlopen

from installation_paths import configuration, configured

ROOT = Path(__file__).resolve().parents[1]


def release():
    return json.loads((ROOT / 'deploy/chess-ladder-release.json').read_text())['fastchess']


def selection(lock):
    spec = importlib.util.spec_from_file_location('chess_environment', ROOT / 'scripts/chess-runtime-env.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    values = configuration()
    selected = values.get('LAPLACE_FASTCHESS') or module.configuration(
        configured('LAPLACE_INSTALL_PREFIX'), keys={'LAPLACE_FASTCHESS'}).get('LAPLACE_FASTCHESS')
    if selected:
        path = Path(selected)
        if not path.is_absolute():
            raise ValueError('LAPLACE_FASTCHESS must be an absolute executable path')
        return path, True
    directory = values.get('LAPLACE_DEPS') or values.get('LAPLACE_DEPS_PREFIX')
    root = Path(directory) if directory else configured('LAPLACE_INSTALL_PREFIX') / 'tools'
    if not root.is_absolute():
        raise ValueError('The dependency directory must be absolute')
    return root / 'fastchess' / lock['version'] / 'fastchess', False


def verify(path, expected_hash=None):
    if not path.is_file() or not os.access(path, os.X_OK):
        raise ValueError(f'Fastchess is not executable: {path}')
    if expected_hash and hashlib.sha256(path.read_bytes()).hexdigest() != expected_hash:
        raise ValueError(f'Fastchess does not match the declared release: {path}')
    result = subprocess.run([str(path), '--version'], check=True, capture_output=True, text=True, timeout=15)
    if not result.stdout.lower().startswith('fastchess '):
        raise ValueError(f'The selected executable did not identify as Fastchess: {path}')


def ensure(path, lock, explicit):
    if explicit:
        verify(path)
        return
    if platform.system() != 'Linux' or platform.machine().lower() not in {'x86_64', 'amd64'}:
        raise ValueError('Declare LAPLACE_FASTCHESS for this platform; the pinned automatic package is Linux x86-64')
    asset = lock['assets']['linux-x86-64']
    if path.exists():
        verify(path, asset['binary_sha256'])
        return
    work = configured('LAPLACE_WORK_ROOT') / 'chess-tools'
    work.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='fastchess-', dir=work) as temporary:
        archive = Path(temporary) / 'release.tar'
        with urlopen(asset['url'], timeout=60) as source, archive.open('wb') as output:
            shutil.copyfileobj(source, output)
        if hashlib.sha256(archive.read_bytes()).hexdigest() != asset['sha256']:
            raise ValueError('Fastchess download does not match the release SHA-256')
        candidate = Path(temporary) / 'fastchess'
        with tarfile.open(archive) as package:
            members = [item for item in package.getmembers()
                       if item.isfile() and Path(item.name).name == asset['binary']]
            if len(members) != 1:
                raise ValueError('Fastchess package must contain exactly one conductor executable')
            with package.extractfile(members[0]) as source, candidate.open('wb') as output:
                shutil.copyfileobj(source, output)
        candidate.chmod(0o755)
        verify(candidate, asset['binary_sha256'])
        path.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(prefix='.fastchess-', dir=path.parent, delete=False) as output:
            pending = Path(output.name)
            with candidate.open('rb') as source:
                shutil.copyfileobj(source, output)
        try:
            pending.chmod(0o755)
            pending.replace(path)
        finally:
            pending.unlink(missing_ok=True)
    verify(path, asset['binary_sha256'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true', help='Verify the selection without installing')
    args = parser.parse_args()
    lock = release()
    path, explicit = selection(lock)
    if args.check:
        verify(path, None if explicit else lock['assets']['linux-x86-64']['binary_sha256'])
    else:
        ensure(path, lock, explicit)
    print(path)


if __name__ == '__main__':
    main()
