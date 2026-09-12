#!/usr/bin/env python3
"""Place a checkout's build directory on /build without discarding existing output."""
import fcntl
import hashlib
import json
import os
import re
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import time


def inventory(root):
    result = {}
    for path in sorted(root.rglob('*')):
        metadata = path.lstat()
        mode = stat.S_IMODE(metadata.st_mode)
        if path.is_symlink():
            value = ['link', os.readlink(path)]
        elif path.is_dir():
            value = ['directory', mode]
        elif path.is_file():
            digest = hashlib.sha256()
            with path.open('rb') as stream:
                for chunk in iter(lambda: stream.read(1024 * 1024), b''):
                    digest.update(chunk)
            value = ['file', mode, metadata.st_size, digest.hexdigest()]
        else:
            raise RuntimeError(f'unsupported build artifact: {path}')
        result[str(path.relative_to(root))] = value
    return result


def canonicalize_cmake_cache(target, lock_root, identity):
    cache = target / 'CMakeCache.txt'
    if not cache.is_file():
        return
    old = cache.read_text()
    updated = re.sub(r'^CMAKE_CACHEFILE_DIR:INTERNAL=.*$',
                     'CMAKE_CACHEFILE_DIR:INTERNAL=' + str(target), old, flags=re.MULTILINE)
    if updated == old:
        return
    preserved = lock_root / (identity + '-' + str(time.time_ns()) + '-CMakeCache.txt')
    preserved.write_text(old)
    with preserved.open('rb') as stream:
        os.fsync(stream.fileno())
    cache.write_text(updated)
    # Regenerate Ninja using the physical build path before a fingerprint skip.
    (target / '.stamps/build-native').unlink(missing_ok=True)


def place(checkout):
    if subprocess.run(['mountpoint', '-q', '/build']).returncode:
        raise RuntimeError('/build must be mounted')
    checkout = checkout.resolve(strict=True)
    identity = hashlib.sha256(os.fsencode(checkout)).hexdigest()[:16]
    target = Path('/build/laplace/build') / ('legacy-' + identity)
    source = checkout / 'build'
    lock_root = Path('/build/laplace/work/build-placement')
    lock_root.mkdir(parents=True, exist_ok=True)
    with (lock_root / (identity + '.lock')).open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        if source.is_symlink():
            actual = source.resolve()
            if actual != target:
                raise RuntimeError(f'build link does not match this checkout: {source} -> {actual}')
            actual.mkdir(parents=True, exist_ok=True)
            canonicalize_cmake_cache(actual, lock_root, identity)
            return actual
        if source.exists():
            if not source.is_dir():
                raise RuntimeError(f'build path is not a directory: {source}')
            before = inventory(source)
            if target.exists():
                if inventory(target) != before:
                    raise RuntimeError(f'build destination differs; both trees retained: {target}')
            else:
                shutil.copytree(source, target, symlinks=True)
            if inventory(target) != before or inventory(source) != before:
                raise RuntimeError('build copy changed during placement; both trees retained')
            receipt = lock_root / (identity + '-' + str(time.time_ns()) + '.json')
            receipt.write_text(json.dumps({'source': str(source), 'target': str(target),
                                          'entries': before}, sort_keys=True) + '\n')
            with receipt.open('rb') as stream:
                os.fsync(stream.fileno())
            # The checked copy is permanent before the old directory is removed.
            shutil.rmtree(source)
        else:
            target.mkdir(parents=True, exist_ok=True)
        source.symlink_to(target, target_is_directory=True)
        canonicalize_cmake_cache(target, lock_root, identity)
        return target


if __name__ == '__main__':
    os.umask(0o002)
    print(place(Path(sys.argv[1])))
