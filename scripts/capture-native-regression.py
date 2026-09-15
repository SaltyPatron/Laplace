#!/usr/bin/env python3
"""Retain bounded native regression evidence without querying or changing a database."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import tempfile


FILES = tuple(
    f"extension/{extension}/tests/regress_output/{name}"
    for extension in ("laplace_geom", "laplace_substrate")
    for name in ("regression.diffs", "regression.out")
) + ("extension/laplace_substrate/tests/regress_output/results/generation_corpus.out",
     "Testing/Temporary/LastTestsFailed.log", "Testing/Temporary/LastTest.log")
MAX_FILE_BYTES = 4 * 1024 * 1024
MAX_TOTAL_BYTES = 24 * 1024 * 1024
MAX_LOG_BYTES = 128 * 1024
CMAKE_FIELDS = {"CMAKE_HOME_DIRECTORY", "CMAKE_PROJECT_NAME", "CMAKE_BUILD_TYPE",
                "CMAKE_C_COMPILER", "CMAKE_CXX_COMPILER", "CMAKE_GENERATOR"}


def read_file(root: Path, relative: str, limit: int) -> tuple[bytes, dict]:
    """Open only regular allowlisted files; never follow links below the build root."""
    parts = Path(relative).parts
    if not parts or any(part in ("..", "/") for part in parts):
        raise ValueError("invalid diagnostic path")
    descriptor = os.open(root, os.O_RDONLY | os.O_DIRECTORY)
    try:
        for part in parts[:-1]:
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                            dir_fd=descriptor)
            os.close(descriptor)
            descriptor = child
        file_descriptor = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK,
                                  dir_fd=descriptor)
        with os.fdopen(file_descriptor, "rb") as stream:
            before = os.fstat(stream.fileno())
            if not stat.S_ISREG(before.st_mode):
                raise ValueError("diagnostic is not a regular file")
            data = stream.read(limit)
            after = os.fstat(stream.fileno())
        changed = (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns)
        complete = not changed and len(data) == before.st_size
        digest = hashlib.sha256(data).hexdigest()
        return data, {"status": "copied" if complete else "changed" if changed else "truncated",
                      "source_bytes": before.st_size, "source_mtime_ns": before.st_mtime_ns,
                      "captured_bytes": len(data), "captured_sha256": digest,
                      "source_sha256": digest if complete else None}
    finally:
        os.close(descriptor)


def git_head(root: Path) -> str | None:
    try:
        result = subprocess.run(["git", "-C", str(root), "rev-parse", "HEAD"],
                                capture_output=True, text=True, timeout=10, check=False)
        value = result.stdout.strip()
        return value if result.returncode == 0 and len(value) == 40 and all(
            char in "0123456789abcdef" for char in value) else None
    except (OSError, subprocess.TimeoutExpired):
        return None


def capture(repo_root: Path, build_root: Path, output_dir: Path, label: str,
            print_diffs: bool = False) -> Path:
    repo_root, build_root = repo_root.resolve(), build_root.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    destination = Path(tempfile.mkdtemp(prefix="capture-", dir=output_dir))
    report = {
        "schema": "laplace.native-regression-evidence.v1",
        "label": label, "captured_at": datetime.now(timezone.utc).isoformat(),
        "repo_root": str(repo_root), "build_root": str(build_root),
        "build_identifier": build_root.name,
        # A retained build may predate the checkout now present at the same path.
        # This is the capture checkout's HEAD, never a claim about built bytes.
        "capture_source_sha": git_head(repo_root),
        "run_id": os.environ.get("GITHUB_RUN_ID"),
        "run_attempt": os.environ.get("GITHUB_RUN_ATTEMPT"),
        "limits": {"file_bytes": MAX_FILE_BYTES, "total_bytes": MAX_TOTAL_BYTES,
                   "printed_diff_bytes": MAX_LOG_BYTES},
        "files": [], "build_metadata": {},
    }
    remaining, log_remaining = MAX_TOTAL_BYTES, MAX_LOG_BYTES
    for relative in FILES:
        record = {"path": relative}
        try:
            data, metadata = read_file(build_root, relative, min(MAX_FILE_BYTES, remaining))
            record.update(metadata)
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            remaining -= len(data)
            if print_diffs and relative.endswith("regression.diffs") and data and log_remaining:
                excerpt = data[:log_remaining]
                log_remaining -= len(excerpt)
                print(f"Native regression diff: {relative} ({len(excerpt)}/{len(data)} captured bytes)")
                # Prefix every line so captured text cannot become an Actions command.
                for line in excerpt.decode("utf-8", errors="backslashreplace").splitlines():
                    print("| " + line)
                if len(excerpt) < len(data):
                    print("| [Remaining captured diff is in the evidence artifact.]")
        except FileNotFoundError:
            record["status"] = "missing"
        except (OSError, ValueError) as exc:
            record.update(status="unreadable", error=str(exc))
        report["files"].append(record)

    for relative, limit in (("CMakeCache.txt", 1024 * 1024),
                            (".stamps/build-native", 4096), (".stamps/install-native", 4096)):
        try:
            data, metadata = read_file(build_root, relative, limit)
            if relative == "CMakeCache.txt":
                # Do not publish a cache wholesale: it may contain caller secrets.
                fields = {}
                for line in data.decode("utf-8", errors="replace").splitlines():
                    key, separator, value = line.partition("=")
                    name = key.partition(":")[0]
                    if separator and name in CMAKE_FIELDS:
                        fields[name] = value
                metadata["fields"] = fields
            else:
                value = data.decode("ascii", errors="replace").strip()
                if len(value) == 64 and all(char in "0123456789abcdef" for char in value):
                    metadata["fingerprint"] = value
            report["build_metadata"][relative] = metadata
        except FileNotFoundError:
            report["build_metadata"][relative] = {"status": "missing"}
        except (OSError, ValueError) as exc:
            report["build_metadata"][relative] = {"status": "unreadable", "error": str(exc)}

    statuses = {record["status"] for record in report["files"]}
    report["disposition"] = ("absent" if statuses == {"missing"} else "partial"
                              if statuses & {"truncated", "changed", "unreadable"} else "captured")
    report["captured_bytes"] = MAX_TOTAL_BYTES - remaining
    receipt = destination / "receipt.json"
    receipt.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"NATIVE_REGRESSION_EVIDENCE disposition={report['disposition']} receipt={receipt}")
    return receipt


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo-root", type=Path, required=True)
    parser.add_argument("--build-root", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--label", required=True)
    parser.add_argument("--print-diffs", action="store_true")
    args = parser.parse_args()
    capture(args.repo_root, args.build_root, args.output_dir, args.label, args.print_diffs)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
