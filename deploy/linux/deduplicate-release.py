#!/usr/bin/env python3
"""Store identical regular files once in an isolated Linux release bundle."""
import hashlib
import json
import os
from pathlib import Path
import stat
import sys

def deduplicate(root):
    root = Path(root).resolve(strict=True)
    sizes = {}
    for folder in ('app', 'share'):
        for path in (root / folder).rglob('*'):
            if path.is_symlink() or not path.is_file():
                continue
            info = path.stat()
            sizes.setdefault((info.st_size, stat.S_IMODE(info.st_mode)), []).append(path)
    saved = linked = 0
    for (size, _), paths in sizes.items():
        if size == 0 or len(paths) < 2:
            continue
        hashes = {}
        for path in paths:
            with path.open('rb') as stream:
                hasher = hashlib.sha256()
                for block in iter(lambda: stream.read(1024 * 1024), b''):
                    hasher.update(block)
                digest = hasher.digest()
            previous = hashes.setdefault(digest, path)
            if previous == path or os.path.samefile(previous, path):
                continue
            temporary = path.with_name(path.name + '.deduplicating')
            try:
                os.link(previous, temporary)
                os.replace(temporary, path)
            finally:
                temporary.unlink(missing_ok=True)
            saved += size
            linked += 1
    return {'identical_files_linked': linked, 'duplicate_bytes_removed': saved}

if __name__ == '__main__':
    print(json.dumps(deduplicate(sys.argv[1])))
