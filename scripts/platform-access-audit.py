#!/usr/bin/env python3
"""Read installed platform access without publishing account or credential data."""
import argparse
import hashlib
from collections import Counter
from datetime import datetime, timezone
import ipaddress
import json
import os
from pathlib import Path
import shlex
import sys
import urllib.error
import urllib.parse
import urllib.request


MAX_BODY = 1024 * 1024
ROUTES = ("people", "events", "clients")
ROLES = ("administrator", "operator", "observer")
PROVIDERS = ("google", "microsoft")


class AuditError(ValueError):
    """A diagnostic that contains no remote response or private configuration."""


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def origin(value):
    try:
        parsed = urllib.parse.urlsplit(value)
        port = parsed.port
        host = parsed.hostname
        if (parsed.scheme not in ("http", "https") or not host or parsed.username is not None
                or parsed.password is not None or parsed.query or parsed.fragment
                or parsed.path not in ("", "/") or "\\" in value
                or any(ord(c) <= 32 or ord(c) >= 127 for c in value)):
            raise ValueError()
        if parsed.scheme == "http":
            try:
                loopback = ipaddress.ip_address(host).is_loopback
            except ValueError:
                loopback = host.lower() == "localhost"
            if not loopback:
                raise ValueError()
        if port is not None and not 1 <= port <= 65535:
            raise ValueError()
        return urllib.parse.urlunsplit((parsed.scheme, parsed.netloc, "", "", ""))
    except (ValueError, TypeError):
        raise AuditError("Select an HTTPS origin or an HTTP loopback origin.") from None


def credential():
    token = os.environ.get("LAPLACE_OPERATOR_TOKEN", "").strip()
    if not token:
        prefix = Path(os.environ.get("LAPLACE_INSTALL_PREFIX", "/opt/laplace"))
        try:
            for line in (prefix / "secrets/operator.env").read_text().splitlines():
                name, separator, value = line.partition("=")
                if separator and name.strip() == "LAPLACE_OPERATOR_TOKEN":
                    parsed = shlex.split(value)
                    if len(parsed) != 1:
                        raise ValueError()
                    token = parsed[0]
        except (OSError, UnicodeError, ValueError):
            raise AuditError("The installed operator credential could not be read.") from None
    if not token or any(ord(c) < 33 or ord(c) > 126 for c in token):
        raise AuditError("The installed operator credential is missing or invalid.")
    return token


def read_route(opener, base, route, token=None):
    if route not in ROUTES:
        raise AuditError("Unsupported platform access audit route.")
    headers = {"Accept": "application/json"}
    if token is not None:
        headers["X-Laplace-Operator-Token"] = token
    request = urllib.request.Request(base + "/v1/admin/access/" + route,
                                     headers=headers, method="GET")
    try:
        with opener.open(request, timeout=15) as response:
            if response.status != 200:
                return response.status, None
            if token is None:
                return response.status, None
            body = response.read(MAX_BODY + 1)
            if len(body) > MAX_BODY:
                raise AuditError("Platform access response exceeds the size limit.")
            if "application/json" not in response.headers.get("Content-Type", "").lower():
                raise AuditError("Platform access response is not JSON.")
    except urllib.error.HTTPError as error:
        # Error bodies and Location headers can contain account details or secrets.
        code = error.code
        error.close()
        return code, None
    except (OSError, ValueError) as error:
        if isinstance(error, AuditError):
            raise
        raise AuditError("Platform access request failed before a valid response.") from None
    try:
        value = json.loads(body)
        if not isinstance(value, dict):
            raise ValueError()
        return response.status, value
    except (ValueError, UnicodeError):
        raise AuditError("Platform access response has an invalid JSON contract.") from None


def rows(value, key, maximum):
    result = value.get(key)
    if not isinstance(result, list) or len(result) > maximum or any(not isinstance(r, dict) for r in result):
        raise AuditError("Platform access response has an invalid row contract.")
    return result


def limited_rows(value, key, maximum):
    limit = value.get("limit")
    if type(limit) is not int or not 1 <= limit <= maximum:
        raise AuditError("Platform access response has an invalid result limit.")
    result = rows(value, key, limit)
    return result, {"observed_count": len(result), "limit": limit,
                    "possibly_truncated": len(result) == limit}


def role(value):
    if value is None:
        return "none"
    return value if isinstance(value, str) and value in ROLES else "unrecognized"


def provider(value):
    return value if isinstance(value, str) and value in PROVIDERS else "unrecognized"


def summarize(people_document, events_document, clients_document):
    people, people_report = limited_rows(people_document, "people", 100)
    events, events_report = limited_rows(events_document, "events", 50)
    clients = rows(clients_document, "clients", 100)
    people_roles, people_providers = Counter(), Counter()
    provider_roles = {}
    for person in people:
        if "role" not in person or not isinstance(person.get("providers"), list):
            raise AuditError("Platform people response lacks role or provider data.")
        assigned_role = role(person["role"])
        people_roles[assigned_role] += 1
        person_providers = set(provider(p) for p in person["providers"])
        people_providers.update(person_providers)
        for name in person_providers:
            provider_roles.setdefault(name, Counter())[assigned_role] += 1
    transitions = Counter()
    for event in events:
        if "previousRole" not in event or "role" not in event:
            raise AuditError("Platform event response lacks role transition data.")
        transitions[role(event["previousRole"]) + "->" + role(event["role"])] += 1
    configured = Counter()
    client_fingerprints = {}
    missing_client_id = 0
    for client in clients:
        if not isinstance(client.get("clientId"), str) or not client["clientId"].strip():
            missing_client_id += 1
        else:
            configured[provider(client.get("provider"))] += 1
            if client.get('provider') in PROVIDERS:
                client_fingerprints.setdefault(client['provider'], []).append(hashlib.sha256(client['clientId'].encode()).hexdigest())
    people_report.update(role_counts=dict(sorted(people_roles.items())),
                         provider_account_counts=dict(sorted(people_providers.items())),
                         role_counts_by_provider={name: dict(sorted(counts.items()))
                                                  for name, counts in sorted(provider_roles.items())})
    events_report["transition_counts"] = dict(sorted(transitions.items()))
    return {"people": people_report, "events": events_report,
            "clients": {"observed_count": len(clients),
                        "configured_provider_counts": dict(sorted(configured.items())),
                        "client_id_sha256": {name: sorted(values) for name, values in sorted(client_fingerprints.items())},
                        "missing_client_id_count": missing_client_id},
            "exact_provider_bindings": "unavailable-from-these-endpoints",
            "owner_administrator_verification": "not-established",
            "browser_session_verification": "not-performed"}


def audit(base, token=None):
    base = origin(base)
    if token is None:
        token = credential()
    if not isinstance(token, str) or not token or any(ord(c) < 33 or ord(c) > 126 for c in token):
        raise AuditError("The installed operator credential is missing or invalid.")
    # No ambient proxy or redirect may receive the installation credential.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    anonymous = {}
    for route in ROUTES:
        status, _ = read_route(opener, base, route)
        anonymous[route] = status
        if status not in (401, 403):
            raise AuditError("Anonymous platform access was not denied: " + route + " (HTTP " + str(status) + ").")
    documents = []
    for route in ROUTES:
        status, document = read_route(opener, base, route, token)
        if status != 200:
            raise AuditError("Authenticated platform access failed: " + route + " (HTTP " + str(status) + ").")
        documents.append(document)
    return {"passed": True, "checked_utc": datetime.now(timezone.utc).isoformat(),
            "anonymous_status": anonymous, **summarize(*documents)}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True)
    parser.add_argument("--state-file", type=Path)
    args = parser.parse_args(argv)
    try:
        report = audit(args.base)
    except AuditError as error:
        report = {"passed": False, "error": str(error)}
    raw = json.dumps(report, sort_keys=True, indent=2) + "\n"
    if args.state_file:
        try:
            args.state_file.parent.mkdir(parents=True, exist_ok=True)
            args.state_file.write_text(raw)
        except OSError:
            print(json.dumps({"passed": False, "error": "Platform access receipt could not be written."}))
            return 1
    print(raw, end="")
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
