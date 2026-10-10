#!/usr/bin/env python3
"""Verify the deployed application payload against the installed Laplace runtime.

Deployment health is deliberately seed-independent. A freshly recreated database is a
valid lifecycle state when the API can reach the installed substrate, the perfcache is
loaded, the SPA is served, and typed operations execute. Seeded product capability is
reported separately and is owned by Tier=live/smoke/eval.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import importlib.util
import json
import os
import shlex
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
    api_key = os.environ.get("LAPLACE_API_KEY", "").strip()
    if not api_key:
        raise ValueError("typed substrate verification requires LAPLACE_API_KEY")
    status, content_type, response = request("POST", base + "/v1/op", body, {
        "Content-Type": "application/json",
        "Authorization": f"Bearer {api_key}",
    })
    if status != 200:
        raise ValueError(f"typed substrate operation returned HTTP {status}")
    if "json" not in content_type.lower():
        raise ValueError("typed substrate operation did not return JSON")
    value = json_object(response, "typed substrate operation")
    if value.get("object") != "op.result" or value.get("name") != "ops.substrate_counts":
        raise ValueError("typed substrate operation returned the wrong contract")


@contextmanager
def verification_credential(base: str):
    """Use a supplied workload key or issue and revoke a key for this verification."""
    if os.environ.get("LAPLACE_API_KEY", "").strip():
        yield
        return
    operator = os.environ.get("LAPLACE_OPERATOR_TOKEN", "").strip()
    if not operator:
        prefix = Path(os.environ.get("LAPLACE_INSTALL_PREFIX", "/opt/laplace"))
        credential_file = prefix / "secrets" / "operator.env"
        if credential_file.is_file():
            for line in credential_file.read_text().splitlines():
                name, separator, value = line.partition("=")
                if separator and name.strip() == "LAPLACE_OPERATOR_TOKEN":
                    parsed = shlex.split(value)
                    operator = parsed[0] if parsed else ""
    if not operator:
        raise ValueError("deployment identity is not installed: configure the site's OpenBao credentials")
    status, _, response = request("POST", base + "/v1/billing/operator/keys",
        json.dumps({"tenant": os.environ.get("LAPLACE_VERIFICATION_TENANT", "deployment-verification"),
                    "label": "installed-runtime-check"}).encode(),
        {"Content-Type": "application/json", "X-Laplace-Operator-Token": operator})
    if status != 200:
        raise ValueError(f"deployment credential issuance returned HTTP {status}")
    issued = json_object(response, "credential issuance")
    key, key_prefix = issued.get("api_key"), issued.get("key_prefix")
    if not isinstance(key, str) or not key.startswith("sk-laplace-") or not key_prefix:
        raise ValueError("deployment credential issuance returned an invalid key contract")
    os.environ["LAPLACE_API_KEY"] = key
    try:
        yield
    finally:
        os.environ.pop("LAPLACE_API_KEY", None)
        status, _, _ = request("POST", base + "/v1/billing/keys/revoke",
            json.dumps({"key_prefix": key_prefix}).encode(),
            {"Content-Type": "application/json", "Authorization": "Bearer " + key})
        if status != 200:
            raise ValueError(f"verification credential revocation returned HTTP {status}")


def verify_storage_proof(base: str) -> None:
    headers = {"Content-Type": "application/json",
               "Authorization": "Bearer " + os.environ["LAPLACE_API_KEY"]}
    body = json.dumps({"text": "Laplace deployment validation."}).encode()
    status, _, response = request("POST", base + "/v1/explore/storage-proof", body, headers)
    if status != 200:
        raise ValueError(f"native storage proof returned HTTP {status}")
    proof = json_object(response, "native storage proof")
    receipt = proof.get("perfcache_receipt_hex")
    if (proof.get("perfcache_aligned") is not True or not receipt
            or receipt != proof.get("database_perfcache_receipt_hex")
            or proof.get("database_perfcache_error")):
        raise ValueError("API and PostgreSQL are not using compatible T0 artifacts")
    invariants = proof.get("invariants", [])
    required = {"tier0_rom", "merkle_recomposition", "trajectory_roundtrip"}
    if not required.issubset({i.get("key") for i in invariants}):
        raise ValueError("native storage proof omitted required invariants")
    if any(i.get("passed") is not True for i in invariants):
        raise ValueError("native storage proof failed: " + ", ".join(
            str(i.get("key")) for i in invariants if i.get("passed") is not True))
    status, _, response = request("POST", base + "/v1/explore/decompose", body, headers)
    if status != 200:
        raise ValueError(f"native decomposition returned HTTP {status}")
    decomposition = json_object(response, "native decomposition")
    if not proof.get("root_id_hex") or proof["root_id_hex"] != decomposition.get("root_id_hex"):
        raise ValueError("decomposition and storage proof disagree on canonical identity")


def stockfish_declared(prefix: Path) -> bool:
    names = {"LAPLACE_STOCKFISH", "LAPLACE_STOCKFISH_SOURCE"}
    if any(os.environ.get(name, "").strip() for name in names):
        return True
    config = prefix / "app/laplace-api.env"
    if config.is_file():
        for line in config.read_text().splitlines():
            key, separator, value = line.partition("=")
            if separator and key in names and value.strip().strip("\"'"):
                return True
    return False


def verify_stockfish() -> bool:
    prefix = Path(os.environ.get("LAPLACE_INSTALL_PREFIX", "/opt/laplace"))
    # Stockfish is an external chess-lab calculator, not Laplace's native chess
    # engine. Every declared installation is checked; an API-only host need not
    # acquire a calculation worker merely to serve the same substrate.
    if not stockfish_declared(prefix):
        print("Stockfish calculation worker: not configured on this host")
        return False
    spec = importlib.util.spec_from_file_location(
        "stockfish_release", ROOT / "scripts/install-stockfish.py"
    )
    installer = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(installer)
    lock = json.loads((ROOT / "deploy/linux/stockfish-release.json").read_text())
    installer.probe(installer.configured_binary(prefix), lock["version"])
    return True


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
           retry_seconds: float = 1.0, checks: dict | None = None) -> tuple[bool, bool]:
    checks = checks if checks is not None else {}
    has_data, product_ready = wait_for_readiness(base, timeout_seconds, retry_seconds)
    checks["readiness"] = "verified"
    if not readiness_only:
        verify_spa(base)
        with verification_credential(base):
            verify_typed_operation(base)
            verify_storage_proof(base)
        checks.update(spa="verified", authenticated_sql="verified", native_storage="verified")
        checks["stockfish"] = "verified" if verify_stockfish() else "not_configured"
    print(
        "PASS: application deployment health "
        f"has_data={'true' if has_data else 'false'} "
        f"product_ready={'true' if product_ready else 'false'} "
        "substrate_reachable=true perfcache_ready=true"
    )
    return has_data, product_ready


def write_state(path: Path, has_data: bool, product_ready: bool, checks: dict | None = None) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "has_data": has_data,
        "product_ready": product_ready,
        "substrate_reachable": True,
        "perfcache_ready": True,
    }
    if checks is not None:
        payload["checks"] = checks
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
    checks: dict[str, str] = {}
    has_data, product_ready = verify(
        args.base.rstrip("/"), args.readiness_only,
        args.timeout_seconds, args.retry_seconds, checks,
    )
    write_state(args.state_file, has_data, product_ready, checks)
    if args.github_output is not None:
        write_github_output(args.github_output, has_data, product_ready)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, ValueError, KeyError, TypeError, urllib.error.URLError) as error:
        raise SystemExit(f"application verification failed: {error}")
