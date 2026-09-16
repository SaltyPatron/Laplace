#!/usr/bin/env python3
"""Select the pinned Kitware CMake distribution without replacing host packages."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
LOCK = ROOT / "deploy/cmake-release.json"
TOOLS = ("cmake", "ctest", "cpack")
RECEIPT = "laplace-cmake.json"


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def inventory(root):
    files = {}
    for path in sorted(root.rglob("*")):
        relative = path.relative_to(root).as_posix()
        if relative == RECEIPT:
            continue
        if path.is_symlink():
            target = os.readlink(path)
            path.resolve(strict=True).relative_to(root.resolve())
            files[relative] = {"link": target}
        elif path.is_file():
            files[relative] = {"sha256": digest(path), "bytes": path.stat().st_size,
                               "mode": path.stat().st_mode & 0o777}
        elif not path.is_dir():
            raise RuntimeError(f"unexpected CMake package file: {relative}")
    return files


def versions(root, lock):
    for name in TOOLS:
        path = root / "bin" / name
        result = subprocess.run([str(path), "--version"], capture_output=True,
                                text=True, timeout=15)
        first = result.stdout.splitlines()[0] if result.stdout else ""
        if result.returncode or first != f"{name} version {lock['version']}":
            raise RuntimeError(f"selected {name} did not report {lock['version']}: {path}")


def verify(root, lock):
    if root.is_symlink() or not root.is_dir():
        raise RuntimeError(f"CMake generation is absent or is a link: {root}")
    receipt = json.loads((root / RECEIPT).read_text())
    if receipt.get("schema") != "laplace.cmake-install/v1" or receipt.get("release") != lock:
        raise RuntimeError("CMake receipt does not match the selected release")
    if receipt.get("files") != inventory(root):
        raise RuntimeError("CMake package bytes or links differ from the acquisition receipt")
    versions(root, lock)
    return root / "bin"


def acquire(archive, lock):
    started = time.monotonic()
    pending = archive.with_suffix(archive.suffix + ".partial")
    count = 0
    value = hashlib.sha256()
    try:
        request = urllib.request.Request(lock["url"], headers={"User-Agent": "Laplace-CMake"})
        with urllib.request.urlopen(request, timeout=20) as response, pending.open("xb") as target:
            while True:
                block = response.read(1024 * 1024)
                if not block:
                    break
                count += len(block)
                if count > lock["bytes"] or time.monotonic() - started > 180:
                    raise RuntimeError("CMake acquisition exceeded its byte or time envelope")
                value.update(block)
                target.write(block)
        if count != lock["bytes"] or value.hexdigest() != lock["sha256"]:
            raise RuntimeError("CMake archive size or SHA256 differs from the official release pin")
        os.replace(pending, archive)
    finally:
        pending.unlink(missing_ok=True)


def extract(archive, destination, lock):
    top = lock["top_directory"]
    with tarfile.open(archive, "r:gz") as source:
        members = source.getmembers()
        names = set()
        # The authenticated archive still gets a path check before materialization.
        for member in members:
            path = PurePosixPath(member.name)
            if path.is_absolute() or ".." in path.parts or not path.parts or path.parts[0] != top:
                raise RuntimeError("CMake archive member escapes the selected package")
            if member.name in names or member.mode & 0o6000:
                raise RuntimeError("CMake archive repeats a member or contains privileged mode bits")
            names.add(member.name)
            if not (member.isfile() or member.isdir() or member.issym()):
                raise RuntimeError("CMake archive contains an unsupported member type")
            if member.issym():
                link = PurePosixPath(member.linkname)
                if link.is_absolute():
                    raise RuntimeError("CMake archive has an absolute link")
                (destination / member.name).parent.joinpath(member.linkname).resolve().relative_to(
                    (destination / top).resolve())
        source.extractall(destination, members=members)
    return destination / top



def create_package_namespace(root):
    # Bootstrap may run as root with umask 077. Make only directories created
    # for this public tool namespace traversable by the later runner; leave
    # existing shared/private ancestors and their ownership/modes unchanged.
    missing = []
    current = root
    while not current.exists():
        missing.append(current)
        current = current.parent
    for path in reversed(missing):
        try:
            path.mkdir()
        except FileExistsError:
            if not path.is_dir():
                raise
        else:
            path.chmod((path.stat().st_mode & 0o7777) | 0o555)


def make_candidate_directories_readable(candidate):
    # All these directories belong to our new unpublished package. Archive
    # files retain their authenticated modes; implicit archive directories
    # must not inherit a root-only umask.
    for path in (candidate, *candidate.rglob("*")):
        if path.is_dir() and not path.is_symlink():
            path.chmod((path.stat().st_mode & 0o7777) | 0o555)


def select(root, work, lock, ensure=False):
    if platform.system() != lock["platform"] or platform.machine().lower() not in ("x86_64", "amd64"):
        raise RuntimeError("this CMake distribution is pinned for Linux x86_64")
    root = root.absolute()
    generation = root / lock["version"]
    if generation.exists() or generation.is_symlink():
        return verify(generation, lock)
    if not ensure:
        raise RuntimeError(f"pinned CMake is not installed: {generation}; run setup-host")
    work = work.absolute()
    work.mkdir(parents=True, exist_ok=True)
    create_package_namespace(root)
    # Callers already own the shared host reservation; independent private names
    # keep an interrupted acquisition from replacing an existing generation.
    with tempfile.TemporaryDirectory(prefix="cmake-download-", dir=work) as temporary:
        archive = Path(temporary) / lock["filename"]
        acquire(archive, lock)
        with tempfile.TemporaryDirectory(prefix=".cmake-pending-", dir=root) as stage:
            candidate = extract(archive, Path(stage), lock)
            make_candidate_directories_readable(candidate)
            versions(candidate, lock)
            receipt = {"schema": "laplace.cmake-install/v1", "release": lock,
                       "files": inventory(candidate)}
            receipt_path = candidate / RECEIPT
            receipt_path.write_text(json.dumps(receipt, sort_keys=True) + "\n")
            receipt_path.chmod(0o644)
            if generation.exists() or generation.is_symlink():
                return verify(generation, lock)
            os.rename(candidate, generation)
    return verify(generation, lock)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--ensure", action="store_true", help="acquire a missing pinned generation")
    args = parser.parse_args()
    try:
        lock = json.loads(LOCK.read_text())
        print(select(args.root, args.work, lock, args.ensure))
        return 0
    except (OSError, ValueError, RuntimeError, tarfile.TarError, subprocess.TimeoutExpired) as error:
        print(f"CMake selection: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
