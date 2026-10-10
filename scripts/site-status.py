#!/usr/bin/env python3
"""Inspect installed configuration and connectivity without disclosing credentials."""
import argparse
import json
import os
from pathlib import Path
import re
import socket
import subprocess


def environment(path):
    values = {}
    if path.is_file() and os.access(path, os.R_OK):
        for line in path.read_text().splitlines():
            key, sep, value = line.partition("=")
            if sep and not key.lstrip().startswith("#"):
                values[key.strip()] = value.strip().strip("\"'")
    return values


def connection(value):
    # Only these non-secret connection fields may enter the operator report.
    result = {}
    for key in ("Host", "Port", "Database", "Username"):
        match = re.search(r"(?:^|;)\s*" + key + r"\s*=\s*([A-Za-z0-9_./:\[\]-]+)\s*(?:;|$)", value, re.I)
        if match:
            result[key.lower()] = match[1]
    host = result.get("host", "")
    if host and not host.startswith("/"):
        try:
            with socket.create_connection((host, int(result.get("port", "5432"))), timeout=5):
                result["tcp_reachable"] = True
        except (OSError, ValueError) as error:
            result["tcp_reachable"] = False
            result["connection_error"] = type(error).__name__
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app-dir", type=Path, required=True)
    parser.add_argument("--machine-file", type=Path, default=Path("/etc/laplace/machine.env"))
    args = parser.parse_args()
    config = args.app_dir / "laplace-api.env"
    values = environment(config)
    units = {}
    for unit in ("laplace-api", "laplace-mcp", "pgdata", "cloudflared"):
        result = subprocess.run(["systemctl", "show", unit, "--no-pager",
            "-p", "User", "-p", "ActiveState", "-p", "InvocationID", "-p", "EnvironmentFiles"],
            text=True, capture_output=True, timeout=10)
        units[unit] = dict(line.split("=", 1) for line in result.stdout.splitlines() if "=" in line)
    receipts = {}
    for name in (".laplace-source-revision", "wwwroot/.laplace-web-source-revision"):
        path = args.app_dir / name
        receipts[name] = path.read_text().strip() if path.is_file() else None
    result = {"host": socket.gethostname(), "uid": os.getuid(), "groups": os.getgroups(),
        "app_dir": str(args.app_dir), "machine_file": str(args.machine_file),
        "machine_keys": sorted(environment(args.machine_file)), "service_keys": sorted(values),
        "installed_revisions": receipts, "services": units,
        "connections": {name: connection(values[name]) for name in ("LAPLACE_DB", "LAPLACE_APP_DB") if name in values},
        "paths": {key: {"path": value, "exists": Path(value).exists()} for key, value in values.items()
                  if key in ("LAPLACE_PERFCACHE_BIN", "LAPLACE_STOCKFISH", "LAPLACE_DATA_PROTECTION_KEYS")}}
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
