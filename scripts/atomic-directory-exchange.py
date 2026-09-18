#!/usr/bin/env python3
"""Atomically exchange two real directories on the same Linux filesystem."""
from __future__ import annotations

import argparse
import ctypes
import errno
import os
import stat
from pathlib import Path

AT_FDCWD = -100
RENAME_EXCHANGE = 2


def real_directory(path: Path) -> os.stat_result:
    entry = path.lstat()
    if stat.S_ISLNK(entry.st_mode) or not stat.S_ISDIR(entry.st_mode):
        raise ValueError(f"directory exchange operand is not a real directory: {path}")
    return entry


def exchange(left: Path, right: Path) -> None:
    left = left.absolute()
    right = right.absolute()
    if left == right:
        raise ValueError("directory exchange operands are identical")
    left_stat = real_directory(left)
    right_stat = real_directory(right)
    if left_stat.st_dev != right_stat.st_dev:
        raise ValueError("directory exchange operands are on different filesystems")

    libc = ctypes.CDLL(None, use_errno=True)
    renameat2 = getattr(libc, "renameat2", None)
    if renameat2 is None:
        raise OSError(errno.ENOSYS, "renameat2 is unavailable; refusing non-atomic fallback")
    renameat2.argtypes = [
        ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint
    ]
    renameat2.restype = ctypes.c_int
    rc = renameat2(
        AT_FDCWD, os.fsencode(left), AT_FDCWD, os.fsencode(right), RENAME_EXCHANGE
    )
    if rc != 0:
        code = ctypes.get_errno()
        raise OSError(code, os.strerror(code), f"{left} <-> {right}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("left", type=Path)
    parser.add_argument("right", type=Path)
    args = parser.parse_args()
    try:
        exchange(args.left, args.right)
    except (OSError, ValueError) as error:
        print(f"atomic-directory-exchange: {error}", file=__import__("sys").stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
