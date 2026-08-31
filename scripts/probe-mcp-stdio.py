#!/usr/bin/env python3
"""Exercise the deployed MCP stdio product contract over real JSON-RPC.

The deploy used to prove only that the apphost survived until stdin EOF. That
accepts a stale/broken runtime which starts successfully but cannot initialize,
list its tool catalog, or read the substrate. This probe talks to the exact
binary the launcher resolves to and fails on protocol or tool errors.
"""

from __future__ import annotations

import argparse
import json
import os
import selectors
import subprocess
import sys
import time
from pathlib import Path
from typing import Any

PROTOCOL_VERSION = "2025-06-18"
REQUIRED_TOOLS = ("health", "api", "facts")


class ProbeFailure(RuntimeError):
    pass


def _readline(proc: subprocess.Popen[str], timeout: float, label: str) -> str:
    if proc.stdout is None:
        raise ProbeFailure("MCP stdout was not captured")

    selector = selectors.DefaultSelector()
    selector.register(proc.stdout, selectors.EVENT_READ)
    try:
        ready = selector.select(timeout)
    finally:
        selector.close()

    if not ready:
        if proc.poll() is not None:
            raise ProbeFailure(f"MCP exited with rc={proc.returncode} while waiting for {label}")
        raise ProbeFailure(f"timed out after {timeout:.1f}s waiting for {label}")

    line = proc.stdout.readline()
    if line == "":
        raise ProbeFailure(f"MCP closed stdout while waiting for {label}")
    return line.rstrip("\r\n")


def _request(
    proc: subprocess.Popen[str], request_id: int, method: str, params: dict[str, Any], timeout: float
) -> dict[str, Any]:
    if proc.stdin is None:
        raise ProbeFailure("MCP stdin was not captured")

    frame = {"jsonrpc": "2.0", "id": request_id, "method": method, "params": params}
    proc.stdin.write(json.dumps(frame, separators=(",", ":")) + "\n")
    proc.stdin.flush()

    raw = _readline(proc, timeout, method)
    try:
        reply = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ProbeFailure(f"{method} returned invalid JSON: {raw!r}") from exc

    if not isinstance(reply, dict):
        raise ProbeFailure(f"{method} returned non-object JSON-RPC reply")
    if reply.get("jsonrpc") != "2.0":
        raise ProbeFailure(f"{method} returned wrong jsonrpc version: {reply.get('jsonrpc')!r}")
    if reply.get("id") != request_id:
        raise ProbeFailure(f"{method} returned id={reply.get('id')!r}, expected {request_id}")
    if reply.get("error") is not None:
        raise ProbeFailure(f"{method} returned JSON-RPC error: {reply['error']!r}")
    result = reply.get("result")
    if not isinstance(result, dict):
        raise ProbeFailure(f"{method} returned no object result")
    return result


def _call_tool(
    proc: subprocess.Popen[str], request_id: int, name: str, arguments: dict[str, Any], timeout: float
) -> dict[str, Any]:
    result = _request(
        proc,
        request_id,
        "tools/call",
        {"name": name, "arguments": arguments},
        timeout,
    )
    if result.get("isError") is not False:
        content = result.get("content")
        raise ProbeFailure(f"tool {name!r} reported isError={result.get('isError')!r}: {content!r}")
    if not isinstance(result.get("content"), list):
        raise ProbeFailure(f"tool {name!r} returned no content array")
    return result


def probe(executable: Path, timeout: float) -> None:
    resolved = executable.resolve(strict=True)
    if not resolved.is_file() or not os.access(resolved, os.X_OK):
        raise ProbeFailure(f"MCP target is not executable: {resolved}")

    proc = subprocess.Popen(
        [str(resolved)],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        bufsize=1,
    )
    started = time.monotonic()
    stderr = ""
    try:
        init = _request(
            proc,
            1,
            "initialize",
            {
                "protocolVersion": PROTOCOL_VERSION,
                "capabilities": {},
                "clientInfo": {"name": "laplace-deploy-probe", "version": "1"},
            },
            timeout,
        )
        if init.get("protocolVersion") != PROTOCOL_VERSION:
            raise ProbeFailure(
                f"initialize protocolVersion={init.get('protocolVersion')!r}, expected {PROTOCOL_VERSION!r}"
            )
        server_info = init.get("serverInfo")
        if not isinstance(server_info, dict) or server_info.get("name") != "laplace-substrate":
            raise ProbeFailure(f"initialize returned unexpected serverInfo: {server_info!r}")
        if not server_info.get("version"):
            raise ProbeFailure("initialize returned no server version")

        listed = _request(proc, 2, "tools/list", {}, timeout)
        tools = listed.get("tools")
        if not isinstance(tools, list):
            raise ProbeFailure("tools/list returned no tools array")
        names = {
            item.get("name")
            for item in tools
            if isinstance(item, dict) and isinstance(item.get("name"), str)
        }
        missing = [name for name in REQUIRED_TOOLS if name not in names]
        if missing:
            raise ProbeFailure(f"tools/list missing required deployed tools: {', '.join(missing)}")

        # Three independent read surfaces: substrate health/inventory, the typed
        # operation catalog, and one ordinary content read. The final call is not
        # required to find a fact; an empty corpus may answer with zero rows, but
        # the deployed operation must execute without a protocol/substrate error.
        _call_tool(proc, 3, "health", {}, timeout)
        _call_tool(proc, 4, "api", {"query": "substrate_counts"}, timeout)
        _call_tool(proc, 5, "facts", {"term": "the", "limit": 1}, timeout)

        if proc.stdin is not None:
            proc.stdin.close()
        try:
            proc.wait(timeout=timeout)
        except subprocess.TimeoutExpired as exc:
            raise ProbeFailure("MCP did not exit after stdin EOF") from exc
        if proc.returncode != 0:
            raise ProbeFailure(f"MCP exited with rc={proc.returncode} after successful protocol calls")
    finally:
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=2)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=2)
        if proc.stderr is not None:
            stderr = proc.stderr.read().strip()

    elapsed = time.monotonic() - started
    print(
        f"MCP stdio probe OK: target={resolved} tools={len(names)} "
        f"server_version={server_info.get('version')} elapsed={elapsed:.2f}s"
    )
    if stderr:
        # Diagnostics belong on stderr; preserve them for CI logs without making
        # normal startup noise part of the protocol assertion.
        print(stderr, file=sys.stderr)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path, help="deployed laplace-mcp launcher or resolved apphost")
    parser.add_argument("--timeout", type=float, default=25.0, help="seconds allowed per protocol response")
    args = parser.parse_args()
    if args.timeout <= 0:
        parser.error("--timeout must be positive")

    try:
        probe(args.executable, args.timeout)
    except (OSError, ProbeFailure) as exc:
        print(f"MCP stdio probe FAILED: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
