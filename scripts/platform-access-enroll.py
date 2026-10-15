#!/usr/bin/env python3
"""Apply an explicit initial platform grant to existing, exactly selected bindings."""
import argparse
from datetime import datetime, timezone
import hashlib
import hmac
import importlib.util
import ipaddress
import json
import os
from pathlib import Path
import re
import socket
import stat
import subprocess
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
import uuid

spec = importlib.util.spec_from_file_location("access_audit", Path(__file__).with_name("platform-access-audit.py"))
access = importlib.util.module_from_spec(spec)
spec.loader.exec_module(access)
MAX_ROWS = 1000
MAX_BODY = 1024 * 1024
PROVIDERS = ("google", "microsoft")


class EnrollmentError(ValueError):
    """A public diagnostic without account, response, or credential contents."""


def fingerprint(request, provider, issuer, subject):
    # This commitment selects a reviewed request; it is never a content or account ID.
    fields = (request["target"], request["role"], request["nonce"], provider, issuer, subject)
    payload = b"".join(len(raw).to_bytes(8, "big") + raw for raw in (v.encode("utf-8") for v in fields))
    return hashlib.sha256(payload).hexdigest()


def validate_request(value):
    keys = {"version", "target", "host", "origin", "nonce", "role", "selectors"}
    if not isinstance(value, dict) or set(value) != keys or type(value["version"]) is not int or value["version"] != 1:
        raise EnrollmentError("Invalid reviewed enrollment request.")
    if (value["target"] != "staging" or value["host"] != "hart-server" or value["role"] != "administrator"
            or not isinstance(value["nonce"], str) or not re.fullmatch(r"[0-9a-f]{64}", value["nonce"])):
        raise EnrollmentError("Enrollment request must select the reviewed staging host and administrator role.")
    if not isinstance(value["origin"], str):
        raise EnrollmentError("Enrollment requires the staging application's HTTP loopback origin.")
    try:
        base = access.origin(value["origin"])
        parsed = urllib.parse.urlsplit(base)
        try:
            loopback = ipaddress.ip_address(parsed.hostname).is_loopback
        except ValueError:
            loopback = parsed.hostname == "localhost"
        if parsed.scheme != "http" or not loopback:
            raise ValueError()
    except (access.AuditError, ValueError, TypeError):
        raise EnrollmentError("Enrollment requires the staging application's HTTP loopback origin.") from None
    selectors = value["selectors"]
    if not isinstance(selectors, list) or not 1 <= len(selectors) <= len(PROVIDERS):
        raise EnrollmentError("Enrollment must select one or two reviewed provider bindings.")
    names = set()
    for selector in selectors:
        if (not isinstance(selector, dict) or set(selector) != {"provider", "fingerprint_sha256"}
                or selector["provider"] not in PROVIDERS or selector["provider"] in names
                or not isinstance(selector["fingerprint_sha256"], str)
                or not re.fullmatch(r"[0-9a-f]{64}", selector["fingerprint_sha256"])):
            raise EnrollmentError("Invalid or repeated reviewed provider selector.")
        names.add(selector["provider"])
    return value


def binding_rows():
    required = ("PGHOST", "PGPORT", "PGUSER", "PGDATABASE", "LAPLACE_APP_DBNAME", "LAPLACE_PG_PREFIX")
    if any(not os.environ.get(name) for name in required):
        raise EnrollmentError("The selected application migration connection is incomplete.")
    if os.environ["PGDATABASE"] != os.environ["LAPLACE_APP_DBNAME"]:
        raise EnrollmentError("The database selection differs from the declared application database.")
    prefix = Path(os.environ["LAPLACE_PG_PREFIX"])
    if not prefix.is_absolute() or not os.environ["PGPORT"].isdigit() or not 1 <= int(os.environ["PGPORT"]) <= 65535:
        raise EnrollmentError("The application PostgreSQL executable or port declaration is invalid.")
    sql = """
BEGIN READ ONLY;
SET LOCAL statement_timeout = '10s';
SET LOCAL lock_timeout = '2s';
SELECT coalesce(json_agg(binding), '[]'::json) FROM (
 SELECT i.user_id::text, i.provider, i.issuer, i.subject,
        i.last_login_at IS NOT NULL AS signed_in, i.last_login_at AS verified_at, r.role,
        (SELECT count(*) FROM app.platform_role_events e WHERE e.user_id=i.user_id) AS event_count,
        (SELECT json_build_object('event_id',e.event_id,'actor',e.actor,
                'previous_role',e.previous_role,'role',e.role)
         FROM app.platform_role_events e WHERE e.user_id=i.user_id
         ORDER BY e.event_id DESC LIMIT 1) AS latest_event
 FROM app.external_identities i LEFT JOIN app.platform_roles r USING(user_id)
 WHERE i.provider IN ('google','microsoft')
 ORDER BY i.provider,i.user_id LIMIT 1001
) binding;
COMMIT;
"""
    command = [str(prefix / "bin/psql"), "-X", "-w", "-qAt", "-v", "ON_ERROR_STOP=1",
               "-v", "VERBOSITY=sqlstate", "-h", os.environ["PGHOST"], "-p", os.environ["PGPORT"],
               "-U", os.environ["PGUSER"], "-d", os.environ["PGDATABASE"], "-f", "-"]
    try:
        result = subprocess.run(command, input=sql, text=True, capture_output=True, timeout=20, check=False)
        if result.returncode:
            # Classify fixed libpq diagnostics without publishing server messages,
            # connection strings, query results, or provider identifiers.
            reason = "client-command-failed"
            for marker, category in (
                ("no password supplied", "password-unavailable"),
                ("password authentication failed", "password-rejected"),
                ("no pg_hba.conf entry", "connection-policy-rejected"),
                ("Connection refused", "connection-refused"),
                ("could not translate host name", "host-resolution-failed"),
                ("error while loading shared libraries", "client-library-unavailable"),
                ("symbol lookup error", "client-library-incompatible"),
                ("certificate verify failed", "certificate-verification-failed"),
                ("Permission denied", "client-access-denied"),
            ):
                if marker in result.stderr:
                    reason = category
                    break
            state = re.search(r"(?:ERROR|FATAL):\s+([0-9A-Z]{5})\b", result.stderr)
            if state:
                reason += "; sqlstate=" + state.group(1)
            raise EnrollmentError("The application identity lookup failed: " + reason + ".")
        if len(result.stdout.encode("utf-8")) > MAX_BODY:
            raise EnrollmentError("The application identity lookup exceeded its response limit.")
        rows = json.loads(result.stdout)
        if not isinstance(rows, list) or len(rows) > MAX_ROWS or any(not isinstance(row, dict) for row in rows):
            raise ValueError()
        return rows
    except EnrollmentError:
        raise
    except FileNotFoundError:
        raise EnrollmentError("The declared PostgreSQL client executable is missing.") from None
    except PermissionError:
        raise EnrollmentError("The declared PostgreSQL client executable is not accessible.") from None
    except subprocess.TimeoutExpired:
        raise EnrollmentError("The bounded application identity lookup timed out.") from None
    except (OSError, ValueError):
        raise EnrollmentError("The application identity lookup returned no valid bounded response.") from None


def selected_bindings(request, rows):
    result = {}
    for selector in request["selectors"]:
        matches = []
        for row in rows:
            if row.get("provider") != selector["provider"]:
                continue
            if any(not isinstance(row.get(key), str) or not row[key] or len(row[key]) > 4096 for key in ("issuer", "subject")):
                continue
            if hmac.compare_digest(fingerprint(request, row["provider"], row["issuer"], row["subject"]), selector["fingerprint_sha256"]):
                matches.append(row)
        if len(matches) != 1:
            raise EnrollmentError("A reviewed provider binding is absent or ambiguous; no initial grant was attempted.")
        row = matches[0]
        try:
            if str(uuid.UUID(row["user_id"])) != row["user_id"] or row.get("signed_in") is not True:
                raise ValueError()
        except (KeyError, TypeError, ValueError):
            raise EnrollmentError("A selected provider binding has no valid signed-in application account.") from None
        if type(row.get("event_count")) is not int or row["event_count"] < 0 or "role" not in row:
            raise EnrollmentError("The selected account access history has an invalid contract.")
        result[selector["provider"]] = row
    if len({row["user_id"] for row in result.values()}) != len(result):
        raise EnrollmentError("Multiple reviewed selectors resolve to one account; no initial grant was attempted.")
    return result


class Api:
    def __init__(self, base):
        self.base = access.origin(base)
        self.token = access.credential()
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), access.NoRedirect())

    def call(self, method, path, body=None):
        allowed = ((method == "GET" and (path == "/v1/admin/access/capabilities"
                    or re.fullmatch(r"/v1/admin/access/people\?search=[0-9a-f-]{36}", path)))
                   or (method == "PUT" and re.fullmatch(r"/v1/admin/access/people/[0-9a-f-]{36}/role", path)))
        if not allowed:
            raise EnrollmentError("Unsupported initial enrollment request route.")
        headers = {"Accept": "application/json", "X-Laplace-Operator-Token": self.token}
        if body is not None:
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(self.base + path, method=method, headers=headers,
                                        data=None if body is None else json.dumps(body).encode())
        try:
            with self.opener.open(request, timeout=15) as response:
                if method == "PUT" or response.status != 200:
                    return response.status, None
                if "application/json" not in response.headers.get("Content-Type", "").lower():
                    raise ValueError()
                payload = response.read(MAX_BODY + 1)
                if len(payload) > MAX_BODY:
                    raise ValueError()
                document = json.loads(payload)
                if not isinstance(document, dict):
                    raise ValueError()
                return response.status, document
        except urllib.error.HTTPError as error:
            status = error.code
            error.close()
            return status, None
        except (OSError, ValueError):
            raise EnrollmentError("The platform enrollment request returned no valid response.") from None

    def capabilities(self):
        status, document = self.call("GET", "/v1/admin/access/capabilities")
        if status != 200 or document.get("initialRoleAssignment") is not True:
            raise EnrollmentError("The installed API does not support atomic initial role assignment.")

    def person(self, row):
        status, document = self.call("GET", "/v1/admin/access/people?search=" + row["user_id"])
        people = document.get("people") if status == 200 and isinstance(document, dict) else None
        if not isinstance(people, list) or len(people) > 100:
            raise EnrollmentError("The serving application account lookup did not complete.")
        matches = [p for p in people if isinstance(p, dict) and p.get("userId") == row["user_id"]]
        if (len(matches) != 1 or "role" not in matches[0] or matches[0]["role"] != row["role"]
                or not isinstance(matches[0].get("providers"), list) or row["provider"] not in matches[0]["providers"]):
            raise EnrollmentError("The serving account binding or role differs from application database readback.")

    def grant(self, user):
        return self.call("PUT", "/v1/admin/access/people/" + user + "/role",
                         {"role": "administrator", "onlyIfNeverAssigned": True})[0]


def private_path(path, directory=False):
    if path.is_symlink() or not path.is_absolute():
        raise EnrollmentError("The enrollment journal must use a private absolute host-work path.")
    try:
        value = path.stat()
    except OSError:
        raise EnrollmentError("The private enrollment journal path is unavailable.") from None
    if (directory and not stat.S_ISDIR(value.st_mode)) or (not directory and not stat.S_ISREG(value.st_mode)):
        raise EnrollmentError("The enrollment journal path has the wrong file type.")
    if os.name == "posix":
        if value.st_uid != os.getuid():
            raise EnrollmentError("The enrollment journal is not owned by this operator.")
        mode = stat.S_IMODE(value.st_mode)
        # An inherited setgid directory selects the group of new files without
        # granting that group access. Journal files themselves remain mode 0600.
        if mode not in ((0o700, 0o2700) if directory else (0o600,)):
            raise EnrollmentError(f"The enrollment journal has non-private mode {mode:04o}.")


class Journal:
    def __init__(self, directory, request):
        directory = Path(directory)
        private_path(directory, directory=True)
        self.path = directory / (request["nonce"] + ".json")
        digest = hashlib.sha256(json.dumps(request, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
        self.value = {"version": 1, "request_sha256": digest, "entries": {}}
        if self.path.exists() or self.path.is_symlink():
            private_path(self.path)
            try:
                if self.path.stat().st_size > 65536:
                    raise ValueError()
                self.value = json.loads(self.path.read_text())
                if (not isinstance(self.value, dict) or self.value.get("version") != 1
                        or self.value.get("request_sha256") != digest or not isinstance(self.value.get("entries"), dict)
                        or set(self.value["entries"]) - {s["provider"] for s in request["selectors"]}):
                    raise ValueError()
            except (OSError, ValueError):
                raise EnrollmentError("The existing enrollment journal does not match this reviewed request.") from None

    def save(self):
        name = None
        try:
            descriptor, name = tempfile.mkstemp(prefix=".enrollment-", dir=self.path.parent)
            with os.fdopen(descriptor, "w") as stream:
                os.chmod(name, 0o600)
                json.dump(self.value, stream, sort_keys=True)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(name, self.path)
            if os.name == "posix":
                descriptor = os.open(self.path.parent, os.O_RDONLY | os.O_DIRECTORY)
                try:
                    os.fsync(descriptor)
                finally:
                    os.close(descriptor)
        except OSError:
            raise EnrollmentError("The private enrollment journal could not be persisted.") from None
        finally:
            if name and os.path.exists(name):
                os.unlink(name)


def confirmed_event(row, entry):
    event = row.get("latest_event")
    if (row["role"] != "administrator" or row["event_count"] != 1 or not isinstance(event, dict)
            or type(event.get("event_id")) is not int or event["event_id"] < 1
            or event.get("actor") != "installation-credential" or event.get("previous_role") is not None
            or "previous_role" not in event or event.get("role") != "administrator"):
        raise EnrollmentError("The requested initial grant has no matching unchanged audit readback; it will not be repeated.")
    if entry["state"] == "confirmed" and entry.get("event_id") != event["event_id"]:
        raise EnrollmentError("The recorded initial grant audit changed; it will not be repeated.")
    return event["event_id"]


def enroll(request, directory):
    request = validate_request(request)
    if socket.gethostname().split(".")[0].lower() != request["host"]:
        raise EnrollmentError("This enrollment request belongs to another host.")
    journal = Journal(directory, request)
    api = Api(request["origin"])
    api.capabilities()
    bindings = selected_bindings(request, binding_rows())
    plans = {}
    # Every binding, serving role and previous request is checked before any PUT.
    for provider, row in bindings.items():
        api.person(row)
        entry = journal.value["entries"].get(provider)
        if entry is None:
            if row["role"] is not None or row["event_count"] != 0 or row.get("latest_event") is not None:
                raise EnrollmentError("A selected account already has a role or role history; no initial grant was attempted.")
            plans[provider] = ("new", None)
        else:
            if (not isinstance(entry, dict) or entry.get("user_id") != row["user_id"]
                    or entry.get("state") not in ("pending", "confirmed")):
                raise EnrollmentError("The account no longer matches this private enrollment journal.")
            plans[provider] = ("verified" if entry["state"] == "confirmed" else "reconciled", confirmed_event(row, entry))
    outcomes = {}
    for provider, row in bindings.items():
        disposition, event_id = plans[provider]
        if disposition == "new":
            journal.value["entries"][provider] = {"user_id": row["user_id"], "state": "pending"}
            journal.save()
            try:
                api.grant(row["user_id"])
            except EnrollmentError:
                # A lost response is not evidence that the transaction failed.
                pass
            current = selected_bindings(request, binding_rows())[provider]
            if current["user_id"] != row["user_id"]:
                raise EnrollmentError("The selected account changed during enrollment.")
            event_id = confirmed_event(current, journal.value["entries"][provider])
            api.person(current)
            disposition = "initial-grant-verified"
        journal.value["entries"][provider] = {"user_id": row["user_id"], "state": "confirmed", "event_id": event_id}
        journal.save()
        outcomes[provider] = disposition
    return {"passed": True, "checked_utc": datetime.now(timezone.utc).isoformat(),
            "target": "staging", "providers": outcomes,
            "provider_binding_match": "exact-reviewed-request", "audited_initial_roles": "verified",
            "browser_session_verification": "not-performed"}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--request", required=True, type=Path)
    parser.add_argument("--journal-directory", required=True, type=Path)
    parser.add_argument("--state-file", type=Path)
    args = parser.parse_args(argv)
    try:
        if args.request.stat().st_size > 65536:
            raise EnrollmentError("The reviewed enrollment request exceeds its size limit.")
        request = json.loads(args.request.read_text())
        report = enroll(request, args.journal_directory)
    except (EnrollmentError, access.AuditError) as error:
        report = {"passed": False, "error": str(error)}
    except (OSError, ValueError):
        report = {"passed": False, "error": "The reviewed enrollment request or local receipt is unavailable."}
    raw = json.dumps(report, sort_keys=True, indent=2) + "\n"
    if args.state_file:
        try:
            args.state_file.parent.mkdir(parents=True, exist_ok=True)
            args.state_file.write_text(raw)
        except OSError:
            print(json.dumps({"passed": False, "error": "The reduced enrollment receipt could not be written."}))
            return 1
    print(raw, end="")
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
