#!/usr/bin/env python3
"""Small client for the deployed Laplace HTTP operation surface."""

from __future__ import annotations

import json
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


class LaplaceApiError(RuntimeError):
    """The deployed API rejected an operation or could not be reached."""


def op_rows(
    api: str,
    name: str,
    args: dict | None = None,
    *,
    max_rows: int = 200,
    timeout_seconds: int | None = None,
) -> list[dict]:
    payload: dict = {"name": name, "max_rows": max_rows}
    if args:
        payload["args"] = args
    if timeout_seconds is not None:
        payload["timeout_seconds"] = timeout_seconds

    request = Request(
        f"{api.rstrip('/')}/v1/op",
        data=json.dumps(payload).encode("utf-8"),
        headers={
            "Content-Type": "application/json",
            "X-Laplace-Tenant": "ci-eval",
        },
        method="POST",
    )
    # Leave a small transport margin beyond the server-side operation budget.
    transport_timeout = max(30, (timeout_seconds or 15) + 10)
    try:
        with urlopen(request, timeout=transport_timeout) as response:
            result = json.load(response)
    except HTTPError as ex:
        detail = ex.read().decode("utf-8", errors="replace")
        raise LaplaceApiError(f"{name}: HTTP {ex.code}: {detail}") from ex
    except (URLError, TimeoutError) as ex:
        raise LaplaceApiError(f"{name}: API unavailable: {ex}") from ex

    if result.get("object") != "op.result" or result.get("name") != name:
        raise LaplaceApiError(f"{name}: invalid operation response: {result!r}")
    if result.get("truncated_at") is not None:
        raise LaplaceApiError(
            f"{name}: operation truncated at {result['truncated_at']} rows; increase max_rows"
        )
    rows = result.get("rows")
    if not isinstance(rows, list) or any(not isinstance(row, dict) for row in rows):
        raise LaplaceApiError(f"{name}: response rows are not objects")
    return rows
