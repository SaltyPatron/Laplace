#!/usr/bin/env python3
"""Install the checksum-pinned upstream Stockfish release through setup/CI.

Keeps previous releases and distro binaries. Only the managed launch symlink is
switched, after immutable-release validation and a real UCI handshake. A caller
may name another managed prefix as a reuse source: the exact lock/version/archive
and binary digest are reverified before its binary is materialized into the new
prefix. Missing reusable state may fall back to upstream; drifted reusable state
fails closed rather than being bypassed. No service restart.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import shutil
import subprocess
import tarfile
import tempfile
from urllib.request import urlopen

ROOT = Path(__file__).resolve().parents[1]
MAX_ARCHIVE = 256 * 1024 * 1024


def snapshot(prefix, state):
    """Record only the managed pointer and its API config, never credentials."""
    link = prefix / "bin/stockfish"
    config = prefix / "app/laplace-api.env"
    saved = {
        "link": os.readlink(link) if link.is_symlink() else None,
        "regular_file": link.exists() and not link.is_symlink(),
        "config": [line for line in config.read_text().splitlines(keepends=True)
                   if line.startswith("LAPLACE_STOCKFISH=")] if config.exists() else [],
    }
    with state.open("x") as output:
        json.dump(saved, output)


def restore(prefix, state):
    """Restore the prior launch contract, retaining downloaded immutable releases."""
    saved = json.loads(state.read_text())
    link = prefix / "bin/stockfish"
    if saved["link"] is not None:
        # Replace only our managed link, or the exact pre-existing symlink.
        if link.is_symlink() and os.readlink(link) == saved["link"]:
            pass
        else:
            if link.exists() or link.is_symlink():
                if not link.is_symlink() or not link.resolve().is_relative_to((prefix / "stockfish").resolve()):
                    raise ValueError("unmanaged Stockfish path changed during publish; preserved")
            with tempfile.TemporaryDirectory(prefix=".stockfish-restore-", dir=link.parent) as temporary:
                candidate = Path(temporary) / "stockfish"
                candidate.symlink_to(saved["link"])
                os.replace(candidate, link)
    elif not saved["regular_file"] and link.is_symlink():
        if not link.resolve().is_relative_to((prefix / "stockfish").resolve()):
            raise ValueError("unmanaged Stockfish link changed during publish; preserved")
        link.unlink()
    config = prefix / "app/laplace-api.env"
    if config.exists():
        lines = [line for line in config.read_text().splitlines(keepends=True)
                 if not line.startswith("LAPLACE_STOCKFISH=")]
        if lines and saved["config"] and not lines[-1].endswith("\n"):
            lines[-1] += "\n"
        lines.extend(saved["config"])
        with tempfile.TemporaryDirectory(prefix=".stockfish-env-", dir=config.parent) as temporary:
            candidate = Path(temporary) / config.name
            shutil.copy2(config, candidate)
            candidate.write_text("".join(lines))
            os.replace(candidate, config)
    print("Previous Stockfish launch contract restored; immutable releases retained")


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def extract(archive, destination, expected):
    if digest(archive) != expected:
        raise ValueError("Stockfish archive checksum mismatch; installed engine unchanged")
    with tarfile.open(archive) as source:
        members = source.getmembers()
        names = set()
        total = 0
        for member in members:
            path = PurePosixPath(member.name)
            if path.is_absolute() or ".." in path.parts or not path.parts or path.parts[0] != "stockfish":
                raise ValueError("unsafe Stockfish archive path")
            if member.name in names or not (member.isdir() or member.isfile()):
                raise ValueError("duplicate or unsupported Stockfish archive member")
            names.add(member.name)
            total += member.size
            if total > MAX_ARCHIVE:
                raise ValueError("Stockfish extracted size exceeds installation limit")
        for member in members:
            target = destination / member.name
            target.parent.mkdir(parents=True, exist_ok=True, mode=0o755)
            if member.isdir():
                target.mkdir(exist_ok=True, mode=0o755)
            else:
                with source.extractfile(member) as data, target.open("xb") as output:
                    shutil.copyfileobj(data, output)
                target.chmod(0o755 if member.mode & 0o111 else 0o644)


def probe(binary, version):
    completed = subprocess.run([str(binary)], input="uci\nquit\n", text=True,
                               capture_output=True, timeout=15, check=True)
    lines = completed.stdout.splitlines()
    if "id name Stockfish " + version not in lines or "uciok" not in lines:
        raise ValueError("Stockfish version/UCI handshake did not match the release lock")
    return next((line for line in lines if line.startswith("option name UCI_Elo ")), "UCI_Elo not advertised")


def release_paths(prefix, lock, variant):
    release = lock[variant]
    destination = prefix / "stockfish" / (
        lock["version"] + "-" + variant + "-" + release["sha256"][:12])
    return destination, destination / release["binary"], destination / "receipt.json"


def verify_release(prefix, lock, variant, *, missing_ok=False):
    """Return an exact managed release or fail on any declared-state drift."""
    release = lock[variant]
    destination, binary, receipt = release_paths(prefix, lock, variant)
    if not destination.exists():
        if missing_ok:
            return None
        raise ValueError("managed Stockfish release is absent")
    if not destination.is_dir() or not receipt.is_file() or not binary.is_file():
        raise ValueError("installed Stockfish release is incomplete; no files overwritten")
    saved = json.loads(receipt.read_text())
    if (saved.get("archive_sha256") != release["sha256"]
            or saved.get("version") != lock["version"]
            or saved.get("source") != release["url"]
            or saved.get("binary_sha256") != digest(binary)):
        raise ValueError("installed Stockfish release drift; no files overwritten")
    capabilities = probe(binary, lock["version"])
    return destination, binary, saved, capabilities


def materialize_reuse(payload, source_binary, source_receipt, release, version, reuse_prefix):
    """Copy only the verified executable needed by an isolated test/runtime prefix."""
    target = payload / release["binary"]
    target.parent.mkdir(parents=True, exist_ok=True, mode=0o755)
    shutil.copy2(source_binary, target)
    target.chmod(0o755)
    # Re-probe the materialized bytes, not only the source path. The receipt keeps
    # upstream provenance and records the local acceleration as transport metadata.
    probe(target, version)
    copied_sha = digest(target)
    if copied_sha != source_receipt["binary_sha256"]:
        raise ValueError("reused Stockfish binary changed during materialization")
    (payload / "receipt.json").write_text(json.dumps({
        "archive_sha256": release["sha256"],
        "binary_sha256": copied_sha,
        "version": version,
        "source": release["url"],
        "reused_from": str(reuse_prefix),
    }) + "\n")


def install(prefix, variant, archive=None, reuse_prefix=None):
    lock = json.loads((ROOT / "deploy/linux/stockfish-release.json").read_text())
    release = lock[variant]
    base = prefix / "stockfish"
    base.mkdir(parents=True, exist_ok=True, mode=0o755)
    destination, binary, receipt = release_paths(prefix, lock, variant)
    if not destination.exists():
        reusable = None
        if archive is None and reuse_prefix is not None:
            # Absence permits normal acquisition. Any present-but-invalid managed
            # release is corruption and must fail closed rather than being hidden by
            # a network fallback.
            reusable = verify_release(reuse_prefix, lock, variant, missing_ok=True)
        with tempfile.TemporaryDirectory(prefix=".install-", dir=base) as temporary:
            temporary = Path(temporary)
            payload = temporary / "payload"
            payload.mkdir(mode=0o755)
            if reusable is not None:
                _, source_binary, source_receipt, _ = reusable
                materialize_reuse(
                    payload, source_binary, source_receipt, release,
                    lock["version"], reuse_prefix)
            else:
                if archive is None:
                    archive = temporary / "download.tar"
                    size = 0
                    with urlopen(release["url"], timeout=30) as source, archive.open("xb") as output:
                        while block := source.read(1024 * 1024):
                            size += len(block)
                            if size > MAX_ARCHIVE:
                                raise ValueError("Stockfish download exceeds installation limit")
                            output.write(block)
                extract(archive, payload, release["sha256"])
                probe(payload / release["binary"], lock["version"])
                (payload / "receipt.json").write_text(json.dumps({
                    "archive_sha256": release["sha256"],
                    "binary_sha256": digest(payload / release["binary"]),
                    "version": lock["version"],
                    "source": release["url"]}) + "\n")
            os.rename(payload, destination)
    verified = verify_release(prefix, lock, variant)
    assert verified is not None
    _, binary, _, capabilities = verified
    bin_dir = prefix / "bin"
    bin_dir.mkdir(parents=True, exist_ok=True, mode=0o755)
    link = bin_dir / "stockfish"
    if link.exists() or link.is_symlink():
        if not link.is_symlink() or not link.resolve().is_relative_to(base.resolve()):
            raise ValueError("unmanaged Stockfish path exists; preserved without replacement")
    if not link.is_symlink() or link.resolve() != binary.resolve():
        with tempfile.TemporaryDirectory(prefix=".stockfish-link-", dir=bin_dir) as temporary:
            candidate = Path(temporary) / "stockfish"
            candidate.symlink_to(binary)
            os.replace(candidate, link)
    print("Stockfish " + lock["version"] + " verified at " + str(link))
    print(capabilities)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prefix", type=Path, default=Path("/opt/laplace"))
    parser.add_argument("--archive", type=Path, help="Optional offline archive; still checksum verified")
    parser.add_argument(
        "--reuse-prefix", type=Path,
        help="Optional managed prefix containing the exact pinned immutable release to reuse before network acquisition")
    action = parser.add_mutually_exclusive_group()
    action.add_argument("--snapshot", type=Path, help="Save the prior launch contract for CI rollback")
    action.add_argument("--restore", type=Path, help="Restore the prior CI launch contract")
    args = parser.parse_args()
    if args.snapshot or args.restore:
        if args.snapshot:
            snapshot(args.prefix.absolute(), args.snapshot)
        else:
            restore(args.prefix.absolute(), args.restore)
        raise SystemExit(0)
    if platform.system() != "Linux" or platform.machine() not in ("x86_64", "AMD64"):
        parser.error("this Linux host installer currently has verified x86-64 release artifacts only")
    cpu = Path("/proc/cpuinfo").read_text()
    variant = "linux-x86_64-avx2" if "avx2" in cpu.split() else "linux-x86_64"
    install(
        args.prefix.absolute(), variant, args.archive,
        args.reuse_prefix.absolute() if args.reuse_prefix is not None else None)
