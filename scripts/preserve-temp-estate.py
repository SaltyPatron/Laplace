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


def require_inactive(source):
    check = subprocess.run(['lsof', '-nP', '-t', '+D' if source.is_dir() else '--',
                            str(source)], capture_output=True, text=True)
    if check.returncode not in (0, 1) or 'Permission denied' in check.stderr:
        raise RuntimeError(f'Cannot establish open-file state: {source}: {check.stderr}')
    if check.stdout.strip():
        return False
    if stat.S_ISSOCK(source.lstat().st_mode):
        for line in Path('/proc/net/unix').read_text().splitlines()[1:]:
            fields = line.split(maxsplit=7)
            if len(fields) == 8 and fields[7] == str(source):
                return False
    return True


def identity(source):
    info = source.lstat()
    return dict(device=info.st_dev, inode=info.st_ino, uid=info.st_uid,
                gid=info.st_gid, mode=info.st_mode, ctime_ns=info.st_ctime_ns)


def write_receipt(receipt, value):
    with receipt.open('x') as stream:
        json.dump(value, stream, indent=2)
        stream.flush()
        os.fsync(stream.fileno())


def retire_endpoint(source, receipt, apply):
    before = identity(source)
    if not (stat.S_ISSOCK(before['mode']) or stat.S_ISFIFO(before['mode'])):
        raise RuntimeError(f'Unsupported special file: {source}')
    if not apply:
        print(f'WOULD RETIRE inactive endpoint: {source}', flush=True)
        return
    value = {'source': str(source), 'endpoint_identity': before,
             'disposition': 'inactive endpoint metadata preserved; no durable file payload'}
    if receipt.exists():
        if json.loads(receipt.read_text()) != value:
            raise RuntimeError(f'Endpoint receipt differs; source retained: {source}')
    else:
        write_receipt(receipt, value)
    subprocess.run(['sync', '-f', str(receipt)], check=True)
    if not require_inactive(source) or identity(source) != before:
        raise RuntimeError(f'Endpoint became active or changed; retained: {source}')
    source.unlink()
    print(f'RETIRED inactive endpoint {source}; metadata: {receipt}', flush=True)


def preserve_regular(source, target, receipt, apply):
    before = manifest(source)
    if not apply:
        print(f'WOULD PRESERVE {source} -> {target} ({len(before)} entries)', flush=True)
        return
    if not target.exists():
        if source.is_dir():
            shutil.copytree(source, target, symlinks=True)
        else:
            shutil.copy2(source, target)
    if manifest(target) != before or manifest(source) != before:
        raise RuntimeError(f'Copy verification failed; source retained: {source}')
    value = {'source': str(source), 'destination': str(target),
             'sha256_manifest': before}
    if receipt.exists():
        if json.loads(receipt.read_text()) != value:
            raise RuntimeError(f'Preservation receipt differs; source retained: {source}')
    else:
        write_receipt(receipt, value)
    if not require_inactive(source):
        raise RuntimeError(f'Artifact became active; source and copy retained: {source}')
    # Flush the destination filesystem before removing the verified source.
    subprocess.run(['sync', '-f', str(target)], check=True)
    if source.is_dir():
        shutil.rmtree(source)
    else:
        source.unlink()
    print(f'PRESERVED {source} -> {target} ({len(before)} entries)', flush=True)

def main():
    os.umask(0o002)
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
            if not require_inactive(source):
                print(f'ACTIVE retained: {source}', flush=True)
                continue
            info = source.lstat()
            if stat.S_ISSOCK(info.st_mode) or stat.S_ISFIFO(info.st_mode):
                retire_endpoint(source, receipt, args.apply)
                continue
            preserve_regular(source, target, receipt, args.apply)
        except (OSError, RuntimeError, ValueError, subprocess.SubprocessError) as error:
            failures.append({"path": row["path"], "error": str(error)})
            print(f"RETAINED: {row['path']}: {error}", flush=True)
            if not args.keep_going:
                raise
    (destination / 'retained-errors.json').write_text(json.dumps(failures, indent=2))
    if failures:
        raise SystemExit(1)



if __name__ == '__main__':
    main()
