#!/usr/bin/env python3
"""Read-only live API/Stockfish checks for the application release transaction."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]


def require_ready(value):
    if not all(value.get(key) is True for key in ("ready", "substrate_reachable", "perfcache_ready")):
        raise ValueError("API/substrate/perfcache readiness failed")
    if not all(type(value.get(key)) is int and value[key] > 0
               for key in ("entities", "consensus_relations")):
        raise ValueError("API has no populated substrate")


def verify(readiness_only=False):
    prefix = Path(os.environ.get("LAPLACE_INSTALL_PREFIX", "/opt/laplace"))
    deadline = time.monotonic() + 60
    while True:
        try:
            with urllib.request.urlopen("http://127.0.0.1:5187/health/ready", timeout=5) as response:
                require_ready(json.load(response))
            break
        except (OSError, ValueError):
            if time.monotonic() >= deadline:
                raise RuntimeError("API readiness did not pass within 60 seconds") from None
            time.sleep(2)
    if readiness_only:
        print("PASS: restored API/substrate readiness")
        return
    request = urllib.request.Request("http://127.0.0.1:5187/v1/op",
        data=json.dumps({"name": "ops.substrate_counts", "max_rows": 20}).encode(),
        headers={"Content-Type": "application/json", "X-Laplace-Tenant": "ci"})
    with urllib.request.urlopen(request, timeout=15) as response:
        result = json.load(response)
    if result.get("object") != "op.result" or result.get("name") != "ops.substrate_counts":
        raise ValueError("live typed operation endpoint failed")
    spec = importlib.util.spec_from_file_location("stockfish_release", ROOT / "scripts/install-stockfish.py")
    installer = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(installer)
    lock = json.loads((ROOT / "deploy/linux/stockfish-release.json").read_text())
    installer.probe(prefix / "bin/stockfish", lock["version"])
    print("PASS: API readiness, typed substrate read and installed Stockfish UCI/version")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--readiness-only", action="store_true")
    verify(parser.parse_args().readiness_only)
