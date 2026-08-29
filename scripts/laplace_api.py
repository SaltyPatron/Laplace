#!/usr/bin/env python3
"""Small client for the deployed Laplace HTTP operation surface."""

from __future__ import annotations

import json
import sys
import time
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

    # Leave a small transport margin beyond the server-side operation budget.
    transport_timeout = max(30, (timeout_seconds or 15) + 10)
    result = None
    last_error: Exception | None = None

    # A just-restarted substrate can legitimately return one transient 503 while
    # PostgreSQL/page caches settle. The deployment eval is a semantic gate, not a
    # race against first-touch warm-up: retry that availability signal once, but
    # never retry 4xx operation failures or turn a persistent outage into a pass.
    for attempt in range(2):
        request = Request(
            f"{api.rstrip('/')}/v1/op",
            data=json.dumps(payload).encode("utf-8"),
            headers={
                "Content-Type": "application/json",
                "X-Laplace-Tenant": "ci-eval",
            },
            method="POST",
        )
        try:
            with urlopen(request, timeout=transport_timeout) as response:
                result = json.load(response)
            last_error = None
            break
        except HTTPError as ex:
            detail = ex.read().decode("utf-8", errors="replace")
            last_error = LaplaceApiError(f"{name}: HTTP {ex.code}: {detail}")
            if ex.code == 503 and attempt == 0:
                time.sleep(1.0)
                continue
            raise last_error from ex
        except (URLError, TimeoutError) as ex:
            last_error = LaplaceApiError(f"{name}: API unavailable: {ex}")
            if attempt == 0:
                time.sleep(1.0)
                continue
            raise last_error from ex

    if result is None:
        if last_error is not None:
            raise last_error
        raise LaplaceApiError(f"{name}: API unavailable without a response")

    if result.get("object") != "op.result" or result.get("name") != name:
        raise LaplaceApiError(f"{name}: invalid operation response: {result!r}")
    if result.get("truncated_at") is not None:
        raise LaplaceApiError(
            f"{name}: operation truncated at {result['truncated_at']} rows; increase max_rows"
        )
    rows = result.get("rows")
    if not isinstance(rows, list) or any(not isinstance(row, dict) for row in rows):
        raise LaplaceApiError(f"{name}: response rows are not objects")

    # The election gate normally reports only rank 1, which hid why the current
    # large corpus displaced the intended topic. Keep this strictly diagnostic:
    # emit the operation's already-returned rows, without changing ranking or
    # making an extra substrate read. The prompt lets ordinals be interpreted in
    # the job log. Remove after the corpus-scale regression is repaired.
    if name == "converse.prompt_coherence":
        sys.stderr.write(
            "PROMPT_COHERENCE_DIAG "
            + json.dumps(
                {"prompt": (args or {}).get("p_prompt"), "rows": rows},
                ensure_ascii=False,
                separators=(",", ":"),
            )
            + "\n"
        )

    return rows
