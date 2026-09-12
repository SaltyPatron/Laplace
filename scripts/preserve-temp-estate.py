#!/usr/bin/env python3
"""Move explicitly selected inactive artifacts to permanent storage with hash receipts.

This does not rewrite paths in archived builds or activate archived databases.
Git worktrees must be repaired with `git worktree repair` after relocation.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess


def manifest(root):
    result = {}
    paths = [root, *sorted(root.rglob('*'))] if root.is_dir() else [root]
    for path in paths:
        info = path.lstat()
        key = str(path.relative_to(root))
        if stat.S_ISLNK(info.st_mode):
            result[key] = ['symlink', os.readlink(path)]
        elif stat.S_ISREG(info.st_mode):
            digest = hashlib.sha256()
            with path.open('rb') as stream:
                for chunk in iter(lambda: stream.read(1024 * 1024), b''):
                    digest.update(chunk)
            result[key] = ['file', info.st_size, digest.hexdigest(), stat.S_IMODE(info.st_mode)]
        elif stat.S_ISDIR(info.st_mode):
            result[key] = ['directory', stat.S_IMODE(info.st_mode)]
        else:
            raise RuntimeError(f'Special file requires individual handling: {path}')
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--destination', required=True, type=Path)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('paths', nargs='+', type=Path)
    args = parser.parse_args()
    destination = args.destination.resolve()
    if not destination.is_relative_to(Path('/build/laplace')):
        parser.error('destination must be on /build/laplace')
    if not os.path.ismount('/build'):
        parser.error('/build must be mounted')
    destination.mkdir(parents=True, exist_ok=True)
    for source in args.paths:
        source = source.absolute()
        if source.parent != Path('/tmp') or source.is_symlink():
            parser.error(f'expected a non-symlink top-level /tmp artifact: {source}')
        target = destination / source.name
        receipt = destination / (source.name + '.preservation.json')
        if target.exists() or receipt.exists():
            raise RuntimeError(f'Destination already exists: {target}')
        check = subprocess.run(['lsof', '-nP', '-t', '+D' if source.is_dir() else '--',
                                str(source)], capture_output=True, text=True)
        if check.stdout.strip():
            print(f'ACTIVE retained: {source}', flush=True)
            continue
        if check.returncode not in (0, 1) or 'Permission denied' in check.stderr:
            raise RuntimeError(f'Cannot establish open-file state: {source}: {check.stderr}')
        before = manifest(source)
        if not args.apply:
            print(f'WOULD PRESERVE {source} -> {target} ({len(before)} entries)', flush=True)
            continue
        if source.is_dir():
            shutil.copytree(source, target, symlinks=True)
        else:
            shutil.copy2(source, target)
        if manifest(target) != before or manifest(source) != before:
            raise RuntimeError(f'Copy verification failed; source retained: {source}')
        with receipt.open('x') as stream:
            json.dump({'source': str(source), 'destination': str(target),
                       'sha256_manifest': before}, stream, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
        # Flush the destination filesystem before removing the verified source.
        subprocess.run(['sync', '-f', str(target)], check=True)
        if source.is_dir():
            shutil.rmtree(source)
        else:
            source.unlink()
        print(f'PRESERVED {source} -> {target} ({len(before)} entries)', flush=True)


if __name__ == '__main__':
    main()
