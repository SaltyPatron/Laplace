#!/usr/bin/env python3
"""Non-gating live API inventory and safe-read probe collector for Laplace."""
from __future__ import annotations

import json
import os
from pathlib import Path
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urljoin
from urllib.request import Request, urlopen

BASE = os.environ.get("LAPLACE_API_URL", "http://127.0.0.1:5187").rstrip("/") + "/"
OUT = Path(os.environ.get("LAPLACE_API_DIAGNOSTICS_DIR", "api-diagnostics")).resolve()
TIMEOUT = float(os.environ.get("LAPLACE_API_TIMEOUT_SECONDS", "10"))
DIAGNOSTIC_SOURCE_REVISION = os.environ.get("LAPLACE_DIAGNOSTIC_SOURCE_REVISION", "").strip() or None
DEPLOYED_RUNTIME_REVISION = os.environ.get("LAPLACE_DEPLOYED_RUNTIME_REVISION", "").strip() or None
REVISION_MATCH = (
    DIAGNOSTIC_SOURCE_REVISION == DEPLOYED_RUNTIME_REVISION
    if DIAGNOSTIC_SOURCE_REVISION and DEPLOYED_RUNTIME_REVISION
    else None
)
SAFE_METHODS = {"get", "head", "options"}
HTTP_METHODS = {"get", "put", "post", "delete", "options", "head", "patch", "trace"}
OUT.mkdir(parents=True, exist_ok=True)


def response_evidence(headers, body: bytes, status: int | None) -> dict:
    correlation_id = headers.get("x-correlation-id") or headers.get("x-request-id") if headers else None
    interesting_headers = {}
    if headers:
        for name in ("content-type", "content-length", "retry-after", "x-correlation-id", "x-request-id"):
            value = headers.get(name)
            if value is not None:
                interesting_headers[name] = value
    return {
        "correlationId": correlation_id,
        "responseHeaders": interesting_headers,
        "errorBodyPreview": body[:8192].decode("utf-8", errors="replace") if status is not None and status >= 400 else None,
    }


def probe(path: str, method: str = "GET") -> dict:
    url = urljoin(BASE, path.lstrip("/"))
    request = Request(url, method=method.upper(), headers={
        "User-Agent": "laplace-api-diagnostics/1",
        "Accept": "application/json, text/plain;q=0.9, */*;q=0.1",
        "X-Laplace-Request": "1",
        "X-Laplace-Tenant": "ci-diagnostics",
    })
    started = time.perf_counter()
    try:
        with urlopen(request, timeout=TIMEOUT) as response:
            body = response.read(1024 * 1024)
            return {
                "method": method.upper(), "path": path, "url": url,
                "status": response.status,
                "milliseconds": round((time.perf_counter() - started) * 1000, 1),
                "contentType": response.headers.get("content-type"),
                "contentLength": int(response.headers.get("content-length", len(body))) if response.headers.get("content-length", "").isdigit() else len(body),
                "bytesRead": len(body),
                "truncated": len(body) == 1024 * 1024,
                **response_evidence(response.headers, body, response.status),
                "error": None,
            }
    except HTTPError as error:
        body = error.read(1024 * 1024)
        return {
            "method": method.upper(), "path": path, "url": url,
            "status": error.code,
            "milliseconds": round((time.perf_counter() - started) * 1000, 1),
            "contentType": error.headers.get("content-type"),
            "contentLength": int(error.headers.get("content-length", len(body))) if error.headers.get("content-length", "").isdigit() else len(body),
            "bytesRead": len(body),
            "truncated": len(body) == 1024 * 1024,
            **response_evidence(error.headers, body, error.code),
            "error": str(error),
        }
    except (URLError, TimeoutError, OSError) as error:
        return {
            "method": method.upper(), "path": path, "url": url,
            "status": None,
            "milliseconds": round((time.perf_counter() - started) * 1000, 1),
            "contentType": None, "contentLength": None, "bytesRead": 0,
            "truncated": False, "correlationId": None, "responseHeaders": {},
            "errorBodyPreview": None, "error": str(error),
        }


def fetch_openapi() -> tuple[str | None, dict | None, list[dict]]:
    attempts = []
    for path in ("/openapi/v1.json", "/swagger/v1/swagger.json", "/openapi.json", "/openapi"):
        url = urljoin(BASE, path.lstrip("/"))
        started = time.perf_counter()
        try:
            request = Request(url, headers={"User-Agent": "laplace-api-diagnostics/1", "Accept": "application/json"})
            with urlopen(request, timeout=TIMEOUT) as response:
                body = response.read()
                attempt = {"path": path, "status": response.status, "milliseconds": round((time.perf_counter() - started) * 1000, 1), "bytes": len(body), "error": None}
                attempts.append(attempt)
                if response.status == 200:
                    document = json.loads(body)
                    (OUT / "openapi.json").write_bytes(body)
                    return path, document, attempts
        except HTTPError as error:
            attempts.append({"path": path, "status": error.code, "milliseconds": round((time.perf_counter() - started) * 1000, 1), "bytes": 0, "error": str(error)})
        except Exception as error:  # diagnostic collection must preserve the failed attempt
            attempts.append({"path": path, "status": None, "milliseconds": round((time.perf_counter() - started) * 1000, 1), "bytes": 0, "error": str(error)})
    return None, None, attempts


def required_parameters(path_item: dict, operation: dict) -> list[dict]:
    params = []
    for source in (path_item.get("parameters", []), operation.get("parameters", [])):
        for item in source:
            if isinstance(item, dict) and item.get("required"):
                params.append({"name": item.get("name"), "in": item.get("in")})
    return params


openapi_path, document, openapi_attempts = fetch_openapi()
operations = []
probes = []
if document:
    for path, path_item in sorted((document.get("paths") or {}).items()):
        if not isinstance(path_item, dict):
            continue
        for method, operation in path_item.items():
            method_lower = method.lower()
            if method_lower not in HTTP_METHODS or not isinstance(operation, dict):
                continue
            required = required_parameters(path_item, operation)
            entry = {
                "method": method_lower.upper(),
                "path": path,
                "operationId": operation.get("operationId"),
                "summary": operation.get("summary"),
                "tags": operation.get("tags", []),
                "deprecated": bool(operation.get("deprecated", False)),
                "security": operation.get("security", document.get("security")),
                "requiredParameters": required,
                "declaredResponses": sorted((operation.get("responses") or {}).keys()),
                "probeDisposition": None,
            }
            if method_lower not in SAFE_METHODS:
                entry["probeDisposition"] = "skipped-unsafe-method"
            elif "{" in path:
                entry["probeDisposition"] = "skipped-path-parameters"
            elif required:
                entry["probeDisposition"] = "skipped-required-parameters"
            else:
                entry["probeDisposition"] = "probed"
                probes.append(probe(path, method_lower.upper()))
            operations.append(entry)

# These product-level probes are useful even when they are omitted from OpenAPI.
for path in ("/health", "/health/status", "/health/ready", "/v1/capabilities"):
    if not any(item["path"] == path and item["method"] == "GET" for item in probes):
        probes.append(probe(path))

status_counts: dict[str, int] = {}
for item in probes:
    key = "network-error" if item["status"] is None else f"{item['status'] // 100}xx"
    status_counts[key] = status_counts.get(key, 0) + 1

summary = {
    "schema": "laplace.api-diagnostics/v1",
    "observedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    "baseUrl": BASE.rstrip("/"),
    "diagnosticSourceRevision": DIAGNOSTIC_SOURCE_REVISION,
    "deployedRuntimeRevision": DEPLOYED_RUNTIME_REVISION,
    "revisionMatch": REVISION_MATCH,
    "openapiPath": openapi_path,
    "openapiAttempts": openapi_attempts,
    "endpointCount": len(operations),
    "safeProbeCount": len(probes),
    "statusCounts": status_counts,
    "serverErrorCount": sum(1 for item in probes if item["status"] is not None and item["status"] >= 500),
    "badRequestCount": sum(1 for item in probes if item["status"] == 400),
    "networkErrorCount": sum(1 for item in probes if item["status"] is None),
}

(OUT / "endpoints.json").write_text(json.dumps(operations, indent=2) + "\n", encoding="utf-8")
(OUT / "probes.json").write_text(json.dumps(probes, indent=2) + "\n", encoding="utf-8")
(OUT / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")

slowest = sorted(probes, key=lambda item: item["milliseconds"], reverse=True)[:10]
markdown = [
    "## Laplace API diagnostics", "",
    f"- Target: `{summary['baseUrl']}`",
    f"- Diagnostic source revision: **{DIAGNOSTIC_SOURCE_REVISION or 'unknown'}**",
    f"- Deployed runtime revision: **{DEPLOYED_RUNTIME_REVISION or 'unknown'}**",
    f"- Source/runtime revision match: **{'unknown' if REVISION_MATCH is None else 'yes' if REVISION_MATCH else 'NO'}**",
    f"- OpenAPI: `{openapi_path or 'not discovered'}`",
    f"- Declared operations: **{len(operations)}**",
    f"- Safe live probes: **{len(probes)}**",
    f"- Status classes: **{json.dumps(status_counts, sort_keys=True)}**",
    f"- 5xx responses: **{summary['serverErrorCount']}**",
    f"- 400 responses from parameter-free safe probes: **{summary['badRequestCount']}**",
    f"- Network errors: **{summary['networkErrorCount']}**",
    "", "### Slowest safe probes", "",
]
markdown.extend(
    f"- `{item['method']} {item['path']}` — {item['status'] if item['status'] is not None else 'network-error'} — {item['milliseconds']} ms"
    for item in slowest
)
failures = [item for item in probes if item["status"] is None or item["status"] >= 400]
if failures:
    markdown.extend(["", "### Failure evidence", ""])
    for item in failures:
        preview = (item.get("errorBodyPreview") or "").replace("\\n", " ").strip()
        if len(preview) > 240:
            preview = preview[:237] + "..."
        correlation = item.get("correlationId") or "none"
        markdown.append(f"- `{item['method']} {item['path']}` — {item['status'] if item['status'] is not None else 'network-error'} — correlation `{correlation}` — {preview or item.get('error') or 'no body'}")
markdown.extend(["", "Mutating endpoints are inventoried from OpenAPI but deliberately not invoked by this diagnostic workflow.", ""])
(OUT / "summary.md").write_text("\n".join(markdown), encoding="utf-8")


def h(value: object) -> str:
    return str(value).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")

rows = []
probe_by_key = {(item["method"], item["path"]): item for item in probes}
for operation in operations:
    live = probe_by_key.get((operation["method"], operation["path"]))
    rows.append("<tr>" + "".join([
        f"<td>{h(operation['method'])}</td>",
        f"<td><code>{h(operation['path'])}</code></td>",
        f"<td>{h(operation.get('operationId') or '')}</td>",
        f"<td>{h(operation['probeDisposition'])}</td>",
        f"<td>{h(live['status'] if live else '')}</td>",
        f"<td>{h(live['milliseconds'] if live else '')}</td>",
    ]) + "</tr>")
html = f"""<!doctype html><html><head><meta charset=\"utf-8\"><title>Laplace API diagnostics</title><style>body{{font:14px system-ui;margin:24px}}table{{border-collapse:collapse;width:100%}}th,td{{border:1px solid #ccc;padding:6px;text-align:left}}th{{position:sticky;top:0;background:#eee}}code{{white-space:nowrap}}</style></head><body><h1>Laplace API diagnostics</h1><p>{h(summary['baseUrl'])} · source={h(DIAGNOSTIC_SOURCE_REVISION or 'unknown')} · deployed={h(DEPLOYED_RUNTIME_REVISION or 'unknown')} · revision-match={h('unknown' if REVISION_MATCH is None else 'yes' if REVISION_MATCH else 'NO')} · operations={len(operations)} · probes={len(probes)} · 5xx={summary['serverErrorCount']} · network-errors={summary['networkErrorCount']}</p><table><thead><tr><th>Method</th><th>Path</th><th>Operation</th><th>Probe</th><th>Status</th><th>ms</th></tr></thead><tbody>{''.join(rows)}</tbody></table></body></html>"""
(OUT / "index.html").write_text(html, encoding="utf-8")
