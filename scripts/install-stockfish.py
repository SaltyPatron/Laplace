#!/usr/bin/env python3
"""Install the checksum-pinned upstream Stockfish release through setup/CI.

Keeps previous releases and distro binaries. Only the managed launch symlink is
switched, after archive validation and a real UCI handshake. No service restart.
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


def install(prefix, variant, archive=None):
    lock = json.loads((ROOT / "deploy/linux/stockfish-release.json").read_text())
    release = lock[variant]
    base = prefix / "stockfish"
    base.mkdir(parents=True, exist_ok=True, mode=0o755)
    destination = base / (lock["version"] + "-" + variant + "-" + release["sha256"][:12])
    binary = destination / release["binary"]
    receipt = destination / "receipt.json"
    if not destination.exists():
        with tempfile.TemporaryDirectory(prefix=".install-", dir=base) as temporary:
            temporary = Path(temporary)
            payload = temporary / "payload"
            payload.mkdir(mode=0o755)
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
                "archive_sha256": release["sha256"], "binary_sha256": digest(payload / release["binary"]),
                "version": lock["version"], "source": release["url"]}) + "\n")
            os.rename(payload, destination)
    saved = json.loads(receipt.read_text())
    if saved["archive_sha256"] != release["sha256"] or saved["binary_sha256"] != digest(binary):
        raise ValueError("installed Stockfish release drift; no files overwritten")
    capabilities = probe(binary, lock["version"])
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
    install(args.prefix.absolute(), variant, args.archive)
