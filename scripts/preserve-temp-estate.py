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
    parser.add_argument('--paths-json', type=Path, help='JSON inventory with path and uid per selected artifact')
    parser.add_argument('--keep-going', action='store_true')
    parser.add_argument('paths', nargs='*', type=Path)
    args = parser.parse_args()
    selected = [{"path": str(path)} for path in args.paths]
    if args.paths_json:
        selected.extend(json.loads(args.paths_json.read_text()))
    if not selected:
        parser.error('select paths or --paths-json')
    destination = args.destination.resolve()
    if not destination.is_relative_to(Path('/build/laplace')):
        parser.error('destination must be on /build/laplace')
    if not os.path.ismount('/build'):
        parser.error('/build must be mounted')
    destination.mkdir(parents=True, exist_ok=True)
    failures = []
    for row in selected:
        try:
            source = Path(row['path']).absolute()
            if not source.exists():
                print(f'ABSENT: {source}', flush=True)
                continue
            if 'uid' in row and source.lstat().st_uid != row['uid']:
                raise RuntimeError(f'Owner changed since inventory: {source}')
            if source.parent not in (Path('/tmp'), Path('/var/tmp')) or source.is_symlink():
                raise RuntimeError(f'expected a non-symlink top-level temp artifact: {source}')
            archive = destination / ('var-tmp' if source.parent == Path('/var/tmp') else 'tmp')
            archive.mkdir(exist_ok=True)
            target = archive / source.name
            receipt = archive / (source.name + '.preservation.json')
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
            recheck = subprocess.run(['lsof', '-nP', '-t', '+D' if source.is_dir() else '--',
                                      str(source)], capture_output=True, text=True)
            if recheck.stdout.strip() or recheck.returncode not in (0, 1) or 'Permission denied' in recheck.stderr:
                raise RuntimeError(f'Artifact became active or unverifiable; source and copy retained: {source}')
            # Flush the destination filesystem before removing the verified source.
            subprocess.run(['sync', '-f', str(target)], check=True)
            if source.is_dir():
                shutil.rmtree(source)
            else:
                source.unlink()
            print(f'PRESERVED {source} -> {target} ({len(before)} entries)', flush=True)
        except (OSError, RuntimeError, subprocess.SubprocessError) as error:
            failures.append({"path": row["path"], "error": str(error)})
            print(f"RETAINED: {row['path']}: {error}", flush=True)
            if not args.keep_going:
                raise
    (destination / 'retained-errors.json').write_text(json.dumps(failures, indent=2))
    if failures:
        raise SystemExit(1)



if __name__ == '__main__':
    main()
