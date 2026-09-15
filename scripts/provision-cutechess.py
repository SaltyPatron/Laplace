#!/usr/bin/env python3
"""Provision the locked CuteChess source and verify the executable's Qt closure."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import urllib.request

from lib.chess_source_integrity import verify_checkout

LOCK = Path(__file__).resolve().parents[1] / "deploy" / "cutechess-release.json"


def run(argv, **kwargs):
    if len(argv) >= 3 and argv[0:2] == ["git", "-C"]:
        # Operators and the CI runner share this one external checkout. Trust
        # only the explicitly selected path without changing global Git policy.
        argv = ["git", "--no-replace-objects", "-c", "core.fsmonitor=false",
                "-c", f"safe.directory={Path(argv[2]).resolve()}", *argv[1:]]
    elif argv and argv[0] == "git":
        argv = ["git", "--no-replace-objects", *argv[1:]]
    result = subprocess.run([str(arg) for arg in argv], capture_output=True, text=True,
                            timeout=120, **kwargs)
    if result.returncode:
        raise RuntimeError(f"{' '.join(map(str, argv))} failed ({result.returncode}): "
                           f"{result.stderr.strip() or result.stdout.strip()}")
    return result.stdout.strip()


def verify_source(path, lock):
    root = Path(run(["git", "-C", path, "rev-parse", "--show-toplevel"])).resolve()
    if root != path.resolve():
        raise RuntimeError(f"{path} is not a standalone dependency repository")
    origin = run(["git", "-C", path, "remote", "get-url", "origin"])
    normalize = lambda value: value.rstrip("/").removesuffix(".git").replace("git@github.com:", "https://github.com/").replace("ssh://git@github.com/", "https://github.com/")
    if normalize(origin) != normalize(lock["repository"]):
        raise RuntimeError(f"{path} origin does not match {lock['repository']}")
    actual = run(["git", "-C", path, "rev-parse", "HEAD"])
    if actual != lock["commit"]:
        raise RuntimeError(f"{path} is at {actual}; expected {lock['commit']}")
    if run(["git", "-C", path, "status", "--porcelain", "--untracked-files=all"]):
        raise RuntimeError(f"{path} contains local changes; preserving it without building")
    integrity = verify_checkout(path, actual, "CuteChess")
    version = (path / ".version").read_text().strip()
    if version != lock["version"] or not (path / "CMakeLists.txt").is_file():
        raise RuntimeError(f"{path} does not contain the locked CuteChess {lock['version']} source")
    return integrity


def provision(target, lock):
    # This is the configured shared external dependency checkout, not a second
    # source cache. Preserve dirty trees and branch tips; only clean detached
    # checkouts are moved to the verified release commit.
    if not target.exists():
        target.parent.mkdir(parents=True, exist_ok=True)
        run(["git", "clone", "--branch", lock["tag"], "--single-branch", lock["repository"], target])
    root = Path(run(["git", "-C", target, "rev-parse", "--show-toplevel"])).resolve()
    if root != target.resolve():
        raise RuntimeError(f"{target} is not a standalone dependency repository")
    origin = run(["git", "-C", target, "remote", "get-url", "origin"])
    normalize = lambda value: value.rstrip("/").removesuffix(".git").replace("git@github.com:", "https://github.com/").replace("ssh://git@github.com/", "https://github.com/")
    if normalize(origin) != normalize(lock["repository"]):
        raise RuntimeError(f"{target} origin does not match {lock['repository']}")
    if run(["git", "-C", target, "status", "--porcelain", "--untracked-files=all"]):
        raise RuntimeError(f"{target} contains local changes; preserving them without updating")
    actual = run(["git", "-C", target, "rev-parse", "HEAD"])
    verify_checkout(target, actual, "CuteChess")
    if actual != lock["commit"]:
        run(["git", "-C", target, "fetch", "origin", f"refs/tags/{lock['tag']}"])
        fetched = run(["git", "-C", target, "rev-parse", "FETCH_HEAD^{commit}"])
        if fetched != lock["commit"]:
            raise RuntimeError(f"upstream {lock['tag']} points to {fetched}; expected {lock['commit']}")
        # Retain even a detached former tip before moving this dependency.
        run(["git", "-C", target, "update-ref", f"refs/laplace/previous/{actual}", actual])
        run(["git", "-C", target, "checkout", "--detach", lock["commit"]])
    verify_source(target, lock)
    # Keep the existing external cache authority in agreement with this upgrade;
    # a later setup-host bootstrap must not reset CuteChess to its previous pin.
    pins = target.parent / "PINS.tsv"
    descriptor = os.open(pins, os.O_RDWR | os.O_CREAT, 0o664)
    with os.fdopen(descriptor, "r+", encoding="utf-8") as output:
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(output.fileno(), msvcrt.LK_LOCK, 1)
        else:
            import fcntl
            fcntl.flock(output, fcntl.LOCK_EX)
        # Read after locking and preserve the inode, owner and mode shared by
        # operators and CI. Stockfish uses this same per-manifest writer lock.
        original = output.read()
        lines = original.splitlines()
        matching = [index for index, line in enumerate(lines)
                    if line.split("\t", 1)[0] == "external/cutechess"]
        row = "\t".join(["external/cutechess", lock["repository"], lock["commit"]])
        if matching:
            for index in matching:
                lines[index] = row
        else:
            lines.append(row)
        text = "\n".join(lines) + "\n"
        if text != original:
            output.seek(0)
            output.write(text)
            output.truncate()
            output.flush()
            os.fsync(output.fileno())
        if os.name == "nt":
            output.seek(0)
            msvcrt.locking(output.fileno(), msvcrt.LK_UNLCK, 1)

    return target


def check_latest(lock):
    request = urllib.request.Request(lock["latest_api"], headers={
        "Accept": "application/vnd.github+json", "User-Agent": "Laplace-chess-dependencies"})
    with urllib.request.urlopen(request, timeout=30) as response:
        release = json.load(response)
    if release.get("draft") or release.get("prerelease") or release.get("tag_name") != lock["tag"]:
        raise RuntimeError(f"CuteChess release lock {lock['tag']} differs from upstream "
                           f"{release.get('tag_name', 'unknown')}; update the verified release lock")
    return {"component": "cutechess", "locked": lock["tag"],
            "latest": release["tag_name"], "current": True}


def probe(binary, lock, qt_version=None):
    # Executing the process checks the dynamic loader, Core and Core5Compat;
    # directory existence cannot establish a working Qt runtime.
    output = run([binary, "--version"])
    if not re.search(r"^cutechess-cli " + re.escape(lock["version"]) + r"\s*$", output, re.M):
        raise RuntimeError(f"{binary} is not locked CuteChess {lock['version']}: {output[:800]}")
    match = re.search(r"Using Qt version (\d+\.\d+\.\d+)", output)
    if not match or tuple(map(int, match[1].split('.'))) < (6, 8, 0):
        raise RuntimeError(f"{binary} requires a working Qt >=6.8 runtime: {output[:800]}")
    qt_version = qt_version or lock.get("qt_version")
    if qt_version and match[1] != qt_version:
        raise RuntimeError(f"{binary} uses Qt {match[1]}; expected {qt_version}")
    return {"component": "cutechess", "version": lock["version"],
            "qt_version": match[1], "path": str(binary), "ready": True}


def digest(path):
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(chunk)
    return result.hexdigest()


def verify_build(source, binary, lock, qt_version=None, receipt_path=None):
    """Verify post-build source and the tested binary before publication."""
    source = source.resolve(strict=True)
    binary = binary.resolve(strict=True)
    verify_source(source, lock)
    before = digest(binary)
    runtime = probe(binary, lock, qt_version)
    integrity = verify_source(source, lock)
    if digest(binary) != before:
        raise RuntimeError("CuteChess executable changed during the runtime probe")
    cache = binary.parent / "CMakeCache.txt"
    receipt = {"schema": "laplace.cutechess-source-build.v1", "repository": lock["repository"],
               "commit": lock["commit"], "source": str(source), "source_integrity": integrity,
               "binary": str(binary), "binary_sha256": before, "runtime": runtime,
               "cmake_cache_sha256": digest(cache) if cache.is_file() else None,
               "scope": "post-build committed source bytes/modes and probed executable identity"}
    if receipt_path is not None:
        receipt_path = receipt_path.absolute()
        receipt_path.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix=".cutechess-receipt-", dir=receipt_path.parent) as temporary:
            pending = Path(temporary) / "receipt.json"
            pending.write_text(json.dumps(receipt, sort_keys=True) + "\n")
            os.replace(pending, receipt_path)
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lock", type=Path, default=LOCK)
    parser.add_argument("--source-dir", type=Path)
    parser.add_argument("--verify-source", type=Path, help="Read-only exact source verification; never updates the checkout")
    parser.add_argument("--binary", type=Path)
    parser.add_argument("--receipt", type=Path, help="Retain verified source and binary identity after the build")
    parser.add_argument("--qt-version")
    parser.add_argument("--check-latest", action="store_true")
    args = parser.parse_args()
    lock = json.loads(args.lock.read_text())
    if not re.fullmatch(r"[0-9a-f]{40}", lock["commit"]):
        parser.error("release lock must contain a complete source commit")
    if not (args.source_dir or args.verify_source or args.binary or args.check_latest):
        parser.error("choose --source-dir, --verify-source, --binary or --check-latest")
    if args.receipt and not (args.verify_source and args.binary):
        parser.error("--receipt requires --verify-source and --binary")
    try:
        if args.check_latest:
            print(json.dumps(check_latest(lock)))
        if args.source_dir:
            print(provision(args.source_dir.resolve(), lock))
        if args.verify_source and args.binary:
            print(json.dumps(verify_build(args.verify_source, args.binary, lock, args.qt_version, args.receipt)))
        elif args.verify_source:
            print(json.dumps(verify_source(args.verify_source, lock)))
        elif args.binary:
            print(json.dumps(probe(args.binary, lock, args.qt_version)))
    except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired) as error:
        print(f"CuteChess: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
