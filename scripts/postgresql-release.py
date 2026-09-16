#!/usr/bin/env python3
"""Enforce the tracked PostgreSQL selection at existing dependency boundaries.

The bootstrap still owns Git acquisition; build-system-deps still owns compilation
and installation. This helper never contacts or changes a running PostgreSQL
server. The official release archive is separately checkable; Git source builds
are bound by exact commit, tracked-source cleanliness and release version.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_CONTRACT = ROOT / "deploy/postgresql-release.json"
PIN_PATH = "external/postgresql"


def contract(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    version = value.get("version", "")
    if (value.get("schema") != "laplace.postgresql-release/v1"
            or value.get("major") != 18
            or not re.fullmatch(r"18\.[0-9]+", version)
            or value.get("tag") != "REL_" + version.replace(".", "_")
            or not re.fullmatch(r"[0-9a-f]{40}", value.get("commit", ""))
            or value.get("repository") != "https://git.postgresql.org/git/postgresql.git"):
        raise ValueError("invalid PostgreSQL release selection")
    archive = value.get("archive", {})
    if (archive.get("url") != "https://ftp.postgresql.org/pub/source/v"
            + version + "/postgresql-" + version + ".tar.bz2"
            or not re.fullmatch(r"[0-9a-f]{64}", archive.get("sha256", ""))
            or type(archive.get("size_bytes")) is not int
            or archive["size_bytes"] <= 0
            or value.get("license_file") != "COPYRIGHT"
            or not re.fullmatch(r"[0-9a-f]{64}", value.get("license_sha256", ""))):
        raise ValueError("invalid PostgreSQL release digest")
    return value


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def pins(path: Path) -> tuple[bytes, list[bytes], int | None]:
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode):
        raise ValueError("PINS.tsv must be a regular file")
    data = path.read_bytes()
    lines = data.splitlines(keepends=True)
    selected = []
    for index, line in enumerate(lines):
        body = line.rstrip(b"\r\n")
        if not body or body.startswith(b"#"):
            continue
        fields = body.decode("utf-8").split("\t")
        if len(fields) != 3:
            raise ValueError("malformed PINS.tsv row " + str(index + 1))
        # The existing bootstrap strips external/; reject equivalent duplicate paths.
        if fields[0].removeprefix("external/") == "postgresql":
            selected.append(index)
    if len(selected) > 1:
        raise ValueError("duplicate PostgreSQL pins")
    return data, lines, selected[0] if selected else None


def pin_line(selected: dict) -> bytes:
    return (PIN_PATH + "\t" + selected["repository"] + "\t"
            + selected["commit"] + "\n").encode("utf-8")


def verify_pin(path: Path, selected: dict) -> None:
    _, lines, index = pins(path)
    if index is None or lines[index].rstrip(b"\r\n") != pin_line(selected).rstrip(b"\n"):
        raise ValueError("PINS.tsv PostgreSQL selection differs from tracked release; run bootstrap")


def select_pin(path: Path, selected: dict) -> bool:
    """Replace this one selection, retaining other bytes, file owner and mode."""
    before = path.lstat()
    data, lines, index = pins(path)
    replacement = pin_line(selected)
    if index is None:
        updated = data + (b"\n" if data and not data.endswith(b"\n") else b"") + replacement
    else:
        lines[index] = replacement
        updated = b"".join(lines)
    if updated == data:
        return False
    fd, name = tempfile.mkstemp(prefix=".PINS.postgresql.", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as stream:
            current = os.fstat(stream.fileno())
            if (current.st_uid, current.st_gid) != (before.st_uid, before.st_gid):
                os.fchown(stream.fileno(), before.st_uid, before.st_gid)
            os.fchmod(stream.fileno(), stat.S_IMODE(before.st_mode))
            stream.write(updated)
            stream.flush()
            os.fsync(stream.fileno())
        now = path.lstat()
        if ((now.st_dev, now.st_ino, now.st_mtime_ns, now.st_size)
                != (before.st_dev, before.st_ino, before.st_mtime_ns, before.st_size)
                or path.read_bytes() != data):
            raise ValueError("PINS.tsv changed during PostgreSQL selection")
        os.replace(name, path)
        directory_fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory_fd)
        finally:
            os.close(directory_fd)
    finally:
        Path(name).unlink(missing_ok=True)
    return True


def output(argv: list[str]) -> str:
    result = subprocess.run(argv, check=True, stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            text=True, timeout=30)
    return result.stdout.strip()


def verify_source(external: Path, selected: dict) -> dict:
    verify_pin(external / "PINS.tsv", selected)
    source = external / "postgresql"
    git = ["git", "-c", "safe.directory=" + str(source.resolve()), "-C", str(source)]
    observed = output(git + ["rev-parse", "HEAD"])
    if observed != selected["commit"]:
        raise ValueError("PostgreSQL source HEAD differs from tracked release")
    if output(git + ["status", "--porcelain", "--untracked-files=no"]):
        raise ValueError("PostgreSQL tracked source has local changes")
    text = (source / "configure.ac").read_text(encoding="utf-8")
    versions = re.findall(r"^AC_INIT\(\[PostgreSQL\], \[([^\]]+)\]", text, re.MULTILINE)
    if versions != [selected["version"]]:
        raise ValueError("PostgreSQL configure.ac version differs from tracked release")
    if sha256(source / selected["license_file"]) != selected["license_sha256"]:
        raise ValueError("PostgreSQL source license digest differs from tracked release")
    return {"source_commit": observed, "source_version": versions[0],
            "tracked_source_clean": True}



def prepare_source(external: Path, selected: dict) -> dict:
    """Converge through the existing prefix owner, then verify its actual result."""
    external = external.resolve()
    try:
        result = verify_source(external, selected)
    except (OSError, ValueError, subprocess.SubprocessError) as exc:
        print("PostgreSQL source preparation required: " + str(exc),
              file=sys.stderr, flush=True)
    else:
        return {**result, "source_preparation": "already-current"}

    # Prefix owns pin selection and Git acquisition. Keep its own root timeout
    # around every child; no credential prompt or alternate acquisition path.
    environment = ["LAPLACE_EXTERNAL=" + str(external)]
    for name in ("TMPDIR", "TMP", "TEMP", "LAPLACE_OPERATOR"):
        if name in os.environ:
            environment.append(name + "=" + os.environ[name])
    argv = ["timeout", "--signal=TERM", "--kill-after=10s", "600s",
            "env", *environment, "bash",
            str(ROOT / "scripts/bootstrap-laplace-runner.sh"), "prefix"]
    if os.geteuid() != 0:
        argv = ["sudo", "-n", "--", *argv]
    subprocess.run(argv, check=True, stdin=subprocess.DEVNULL)
    result = verify_source(external, selected)
    return {**result, "source_preparation": "bootstrap-prefix"}


def verify_installed(prefix: Path, selected: dict) -> dict:
    versions = {}
    for name, label in (("postgres", "postgres"), ("pg_config", "PostgreSQL")):
        observed = output([str(prefix / "bin" / name), "--version"])
        expected = label + (" (PostgreSQL) " if name == "postgres" else " ") + selected["version"]
        if observed != expected:
            raise ValueError(name + " version differs from tracked release: " + observed)
        versions[name] = observed
    return {"installed_tools": versions, "running_server_checked": False}



def build_inputs(prefix: Path, selected: dict) -> dict:
    """Fingerprint actual installed inputs used by the native PostgreSQL build."""
    installed = verify_installed(prefix, selected)
    prefix = prefix.resolve(strict=True)
    config = prefix / "bin/pg_config"
    paths = {}
    for option in ("includedir", "includedir-server", "libdir", "pkglibdir", "sharedir", "bindir"):
        path = Path(output([str(config), "--" + option]))
        if not path.is_absolute() or not path.is_dir():
            raise ValueError("pg_config --" + option + " did not select an existing absolute directory")
        paths[option] = path.resolve(strict=True)
    configuration = (paths["includedir-server"] / "pg_config.h").read_text(encoding="utf-8")
    major, minor = (int(part) for part in selected["version"].split("."))
    versions = re.findall(r'^#define\s+PG_VERSION\s+"([^"]+)"\s*$', configuration, re.MULTILINE)
    numbers = re.findall(r"^#define\s+PG_VERSION_NUM\s+([0-9]+)\s*$", configuration, re.MULTILINE)
    if versions != [selected["version"]] or numbers != [str(major * 10000 + minor)]:
        raise ValueError("PostgreSQL server headers differ from the selected installed release")

    def file(path: Path) -> dict:
        before = path.stat()
        if not stat.S_ISREG(before.st_mode):
            raise ValueError("PostgreSQL build input is not a regular file: " + str(path))
        digest = sha256(path)
        after = path.stat()
        identity = lambda value: (value.st_dev, value.st_ino, value.st_size,
                                  value.st_mtime_ns, value.st_ctime_ns)
        if identity(before) != identity(after):
            raise ValueError("PostgreSQL build input changed while hashing: " + str(path))
        return {"bytes": before.st_size, "sha256": digest}

    def headers(root: Path) -> dict:
        rows = []
        active = set()
        def visit(directory: Path):
            info = directory.stat()
            identity = (info.st_dev, info.st_ino)
            if identity in active:
                raise ValueError("PostgreSQL include directory contains a link cycle")
            active.add(identity)
            try:
                for path in sorted(directory.iterdir()):
                    if path.is_dir():
                        visit(path)
                    else:
                        row = {"path": str(path.relative_to(root)), **file(path)}
                        if path.is_symlink():
                            row["link"] = os.readlink(path)
                        rows.append(row)
            finally:
                active.remove(identity)
        visit(root)
        if not rows:
            raise ValueError("PostgreSQL include directory is empty: " + str(root))
        raw = json.dumps(rows, sort_keys=True, separators=(",", ":")).encode()
        return {"path": str(root), "files": len(rows), "sha256": hashlib.sha256(raw).hexdigest()}

    # The public include tree normally contains the server tree. Hash each
    # logical selected tree explicitly so custom pg_config layouts are covered.
    return {**installed, "prefix": str(prefix),
            "configuration_paths": {key: str(value) for key, value in paths.items()},
            "tools": {name: file(prefix / "bin" / name) for name in ("postgres", "pg_config")},
            "headers": {key: headers(paths[key]) for key in ("includedir", "includedir-server")}}


def verify_archive(path: Path, selected: dict) -> dict:
    observed = sha256(path)
    if (path.stat().st_size != selected["archive"]["size_bytes"]
            or observed != selected["archive"]["sha256"]):
        raise ValueError("PostgreSQL release archive checksum or size differs")
    return {"release_archive_sha256": observed}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("select-pin", "source", "prepare-source", "installed", "build-inputs", "restart-needed", "archive"))
    parser.add_argument("--contract", type=Path, default=DEFAULT_CONTRACT)
    parser.add_argument("--external", type=Path, default=Path(os.environ.get("LAPLACE_EXTERNAL", "/build/external")))
    parser.add_argument("--prefix", type=Path, default=Path(os.environ.get("LAPLACE_PG_PREFIX", "/opt/laplace/pgsql-18")))
    parser.add_argument("--archive", type=Path)
    parser.add_argument("--server-version-num", help="actual SHOW server_version_num read by the activation owner")
    args = parser.parse_args(argv)
    try:
        selected = contract(args.contract)
        if args.mode == "select-pin":
            result = {"pin_changed": select_pin(args.external / "PINS.tsv", selected)}
        elif args.mode == "source":
            result = verify_source(args.external, selected)
        elif args.mode == "prepare-source":
            result = prepare_source(args.external, selected)
        elif args.mode == "build-inputs":
            result = build_inputs(args.prefix, selected)
        elif args.mode in ("installed", "restart-needed"):
            result = verify_installed(args.prefix, selected)
            if args.mode == "restart-needed":
                observed = args.server_version_num or ""
                if not re.fullmatch(r"[1-9][0-9]{4,7}", observed):
                    raise ValueError("invalid running server_version_num observation")
                major, minor = (int(part) for part in selected["version"].split("."))
                if int(observed) // 10000 != major:
                    raise ValueError("running PostgreSQL major differs; a minor-release restart cannot upgrade this cluster")
                expected = major * 10000 + minor
                result.update(running_server_version_num=int(observed),
                              selected_server_version_num=expected,
                              restart_required=int(observed) != expected,
                              running_server_checked=True,
                              observation_owner="pipeline SHOW server_version_num")
        else:
            if args.archive is None:
                parser.error("archive mode requires --archive")
            result = verify_archive(args.archive, selected)
        print(json.dumps({"schema": "laplace.postgresql-selection/v1",
                          "version": selected["version"], "mode": args.mode, **result},
                         sort_keys=True))
        # Distinguish a valid observed mismatch from a failed read/installation.
        return 3 if result.get("restart_required") else 0
    except (OSError, ValueError, subprocess.SubprocessError) as exc:
        print("PostgreSQL selection failed: " + str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
