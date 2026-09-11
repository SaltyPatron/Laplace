#!/usr/bin/env python3
"""Small clients for the deployed Laplace HTTP product and operation surfaces."""

from __future__ import annotations

import json
import sys
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


class LaplaceApiError(RuntimeError):
    """The deployed API rejected an operation or could not be reached."""


def _json_request(
    api: str,
    path: str,
    payload: dict,
    *,
    label: str,
    tenant: str,
    timeout_seconds: int,
    session: str | None = None,
) -> tuple[dict, dict[str, str]]:
    """POST one JSON request and preserve response headers needed by session tests.

    A single transient 503 is retried for parity with the existing operation client.
    Semantic, billing, validation, and other HTTP failures are never rewritten into
    success. The caller validates the response-specific schema.
    """
    result = None
    headers: dict[str, str] = {}
    last_error: Exception | None = None
    request_headers = {
        "Content-Type": "application/json",
        "X-Laplace-Tenant": tenant,
    }
    if session:
        request_headers["X-Laplace-Session"] = session

    for attempt in range(2):
        request = Request(
            f"{api.rstrip('/')}{path}",
            data=json.dumps(payload).encode("utf-8"),
            headers=request_headers,
            method="POST",
        )
        try:
            with urlopen(request, timeout=timeout_seconds) as response:
                result = json.load(response)
                headers = {key.lower(): value for key, value in response.headers.items()}
            last_error = None
            break
        except HTTPError as ex:
            detail = ex.read().decode("utf-8", errors="replace")
            last_error = LaplaceApiError(f"{label}: HTTP {ex.code}: {detail}")
            if ex.code == 503 and attempt == 0:
                time.sleep(1.0)
                continue
            raise last_error from ex
        except (URLError, TimeoutError) as ex:
            last_error = LaplaceApiError(f"{label}: API unavailable: {ex}")
            if attempt == 0:
                time.sleep(1.0)
                continue
            raise last_error from ex

    if result is None:
        if last_error is not None:
            raise last_error
        raise LaplaceApiError(f"{label}: API unavailable without a response")
    if not isinstance(result, dict):
        raise LaplaceApiError(f"{label}: response is not a JSON object")
    return result, headers


def chat_completion(
    api: str,
    prompt: str,
    *,
    model: str = "laplace-converse-001",
    session: str | None = None,
    tenant: str = "ci",
    timeout_seconds: int = 90,
) -> tuple[str, str | None, dict]:
    """Execute the deployed non-streaming OpenAI-compatible chat product surface.

    Returns assistant content, the substrate session key returned by the server, and
    the complete decoded response. An envelope with missing choices/message/content
    is an invalid product response rather than an empty semantic answer.
    """
    payload = {
        "model": model,
        "messages": [{"role": "user", "content": prompt}],
        "stream": False,
    }
    result, headers = _json_request(
        api,
        "/v1/chat/completions",
        payload,
        label="chat.completions",
        tenant=tenant,
        timeout_seconds=timeout_seconds,
        session=session,
    )
    if result.get("object") != "chat.completion":
        raise LaplaceApiError(f"chat.completions: invalid response object: {result!r}")
    choices = result.get("choices")
    if not isinstance(choices, list) or not choices or not isinstance(choices[0], dict):
        raise LaplaceApiError(f"chat.completions: missing choice: {result!r}")
    message = choices[0].get("message")
    if not isinstance(message, dict) or "content" not in message:
        raise LaplaceApiError(f"chat.completions: missing assistant content: {result!r}")
    content = message.get("content")
    if content is None:
        content = ""
    if not isinstance(content, str):
        raise LaplaceApiError(f"chat.completions: assistant content is not text: {result!r}")
    returned_session = headers.get("x-laplace-session")
    metadata = result.get("metadata")
    if isinstance(metadata, dict):
        body_session = metadata.get("session")
        if body_session is not None:
            if not isinstance(body_session, str):
                raise LaplaceApiError("chat.completions: metadata.session is not text")
            if returned_session is not None and returned_session != body_session:
                raise LaplaceApiError(
                    "chat.completions: response header/session metadata disagree"
                )
            returned_session = body_session
    return content, returned_session, result


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
    result, _ = _json_request(
        api,
        "/v1/op",
        payload,
        label=name,
        tenant="ci-eval",
        timeout_seconds=transport_timeout,
    )

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
