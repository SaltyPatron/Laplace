#!/usr/bin/env python3
"""Verify the deployed application payload against the installed Laplace runtime.

Deployment health is deliberately seed-independent. A freshly recreated database is a
valid lifecycle state when the API can reach the installed substrate, the perfcache is
loaded, the SPA is served, and typed operations execute. Seeded product capability is
reported separately and is owned by Tier=live/smoke/eval.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import sys
import time
import urllib.error
import urllib.request
from typing import Any

MAX_BODY = 1024 * 1024
ROOT = Path(__file__).resolve().parents[1]
DEFAULT_STATE = ROOT / "build" / ".application-release-state.json"


def _read_body(response) -> bytes:
    body = response.read(MAX_BODY + 1)
    if len(body) > MAX_BODY:
        raise ValueError("application verification response exceeds 1 MiB")
    return body


def request(method: str, url: str, body: bytes | None = None,
            headers: dict[str, str] | None = None) -> tuple[int, str, bytes]:
    req = urllib.request.Request(url, data=body, method=method, headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=10) as response:
            return response.status, response.headers.get("Content-Type", ""), _read_body(response)
    except urllib.error.HTTPError as error:
        # /health/ready intentionally returns 503 while a structurally healthy DB is
        # empty. Preserve and validate that typed receipt instead of losing it to the
        # transport exception.
        return error.code, error.headers.get("Content-Type", ""), _read_body(error)


def json_object(body: bytes, label: str) -> dict[str, Any]:
    try:
        value = json.loads(body)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError(f"{label} did not return JSON") from error
    if not isinstance(value, dict):
        raise ValueError(f"{label} returned a non-object JSON value")
    return value


def _count(value: Any, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise ValueError(f"readiness {name} must be a non-negative integer")
    return value


def classify_readiness(value: dict[str, Any]) -> tuple[bool, bool]:
    """Return ``(has_data, product_ready)`` after proving runtime health.

    Corpus population is evidence for downstream product QA, not authority over whether
    the current API/SPA payload may be activated. Exact-empty and thin substrates are
    therefore valid deployment states. The named substrate-floor smoke owns the alarm
    for partial or insufficient seed state after the application transaction commits.
    """
    for name in ("ready", "substrate_reachable", "perfcache_ready"):
        if type(value.get(name)) is not bool:
            raise ValueError(f"readiness {name} must be boolean")
    if value["substrate_reachable"] is not True:
        raise ValueError("deployed API cannot reach the substrate")
    if value["perfcache_ready"] is not True:
        raise ValueError("deployed API has not loaded the installed perfcache")

    entities = _count(value.get("entities"), "entities")
    consensus = _count(value.get("consensus_relations"), "consensus_relations")
    has_data = entities > 0 or consensus > 0
    product_ready = value["ready"]
    if product_ready and not (entities > 0 and consensus > 0):
        raise ValueError(
            "API reported product ready without both entities and consensus relations"
        )
    if not product_ready and entities > 0 and consensus > 0:
        # The deployed API computes ready = entities && consensus && perfcache.
        # Substrate/perfcache health was already proved above, so a populated but
        # not-ready receipt is internally inconsistent and cannot be classified as
        # an ordinary thin/unseeded state.
        raise ValueError(
            "API reported populated substrate and loaded runtime but remained not ready"
        )
    return has_data, product_ready


def readiness(base: str) -> tuple[bool, bool]:
    status, content_type, body = request("GET", base + "/health/ready")
    if status not in (200, 503):
        raise ValueError(f"readiness returned HTTP {status}")
    if "json" not in content_type.lower():
        raise ValueError("readiness did not return JSON content type")
    return classify_readiness(json_object(body, "readiness"))


def verify_spa(base: str) -> None:
    status, content_type, body = request("GET", base + "/")
    if status != 200:
        raise ValueError(f"SPA root returned HTTP {status}")
    if "text/html" not in content_type.lower():
        raise ValueError("SPA root did not return HTML")
    text = body.decode("utf-8", "strict").lower()
    if "<div id=\"root\"" not in text and "<div id='root'" not in text:
        raise ValueError("SPA root is not the Laplace application document")


def verify_typed_operation(base: str) -> None:
    body = json.dumps({"name": "ops.substrate_counts", "max_rows": 20}).encode()
    status, content_type, response = request("POST", base + "/v1/op", body, {
        "Content-Type": "application/json",
        "X-Laplace-Tenant": "release-verify",
    })
    if status != 200:
        raise ValueError(f"typed substrate operation returned HTTP {status}")
    if "json" not in content_type.lower():
        raise ValueError("typed substrate operation did not return JSON")
    value = json_object(response, "typed substrate operation")
    if value.get("object") != "op.result" or value.get("name") != "ops.substrate_counts":
        raise ValueError("typed substrate operation returned the wrong contract")


def verify_stockfish() -> None:
    prefix = Path(os.environ.get("LAPLACE_INSTALL_PREFIX", "/opt/laplace"))
    spec = importlib.util.spec_from_file_location(
        "stockfish_release", ROOT / "scripts/install-stockfish.py"
    )
    installer = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(installer)
    lock = json.loads((ROOT / "deploy/linux/stockfish-release.json").read_text())
    installer.probe(prefix / "bin/stockfish", lock["version"])


def wait_for_readiness(base: str, timeout_seconds: float = 60.0,
                       retry_seconds: float = 1.0) -> tuple[bool, bool]:
    if timeout_seconds < 0 or retry_seconds <= 0:
        raise ValueError("readiness retry bounds must be positive")
    deadline = time.monotonic() + timeout_seconds
    while True:
        try:
            return readiness(base)
        except (OSError, ValueError, KeyError, TypeError, urllib.error.URLError) as error:
            if time.monotonic() >= deadline:
                raise RuntimeError(
                    f"application readiness did not pass within {timeout_seconds:g} seconds: {error}"
                ) from error
            time.sleep(retry_seconds)


def verify(base: str, readiness_only: bool = False, timeout_seconds: float = 60.0,
           retry_seconds: float = 1.0) -> tuple[bool, bool]:
    has_data, product_ready = wait_for_readiness(base, timeout_seconds, retry_seconds)
    if not readiness_only:
        verify_spa(base)
        verify_typed_operation(base)
        verify_stockfish()
    print(
        "PASS: application deployment health "
        f"has_data={'true' if has_data else 'false'} "
        f"product_ready={'true' if product_ready else 'false'} "
        "substrate_reachable=true perfcache_ready=true"
    )
    return has_data, product_ready


def write_state(path: Path, has_data: bool, product_ready: bool) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "has_data": has_data,
        "product_ready": product_ready,
        "substrate_reachable": True,
        "perfcache_ready": True,
    }
    temporary = path.with_name(path.name + f".tmp-{os.getpid()}")
    temporary.write_text(json.dumps(payload, sort_keys=True) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def write_github_output(path: Path, has_data: bool, product_ready: bool) -> None:
    with path.open("a", encoding="utf-8") as output:
        output.write(f"has_data={'true' if has_data else 'false'}\n")
        output.write(f"product_ready={'true' if product_ready else 'false'}\n")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default="http://127.0.0.1:5187")
    parser.add_argument("--readiness-only", action="store_true")
    parser.add_argument("--github-output", type=Path)
    parser.add_argument("--state-file", type=Path, default=DEFAULT_STATE)
    parser.add_argument("--timeout-seconds", type=float, default=60.0)
    parser.add_argument("--retry-seconds", type=float, default=1.0)
    args = parser.parse_args(argv)
    has_data, product_ready = verify(
        args.base.rstrip("/"), args.readiness_only,
        args.timeout_seconds, args.retry_seconds,
    )
    write_state(args.state_file, has_data, product_ready)
    if args.github_output is not None:
        write_github_output(args.github_output, has_data, product_ready)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, ValueError, KeyError, TypeError, urllib.error.URLError) as error:
        raise SystemExit(f"application verification failed: {error}")
