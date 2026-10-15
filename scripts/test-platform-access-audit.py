#!/usr/bin/env python3
"""Check read-only access auditing and private-response exclusion."""
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import urllib.error
import urllib.request
import urllib.response

spec = importlib.util.spec_from_file_location("platform_access_audit",
    Path(__file__).with_name("platform-access-audit.py"))
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)
PRIVATE = "private-account@example.invalid"
TOKEN = "private-operator-token"


class Response:
    def __init__(self, value, status=200, content_type="application/json"):
        self.body = value if isinstance(value, bytes) else json.dumps(value).encode()
        self.status = status
        self.headers = {"Content-Type": content_type}
    def read(self, maximum):
        return self.body[:maximum]
    def __enter__(self):
        return self
    def __exit__(self, *args):
        pass


class AccessAuditTests(unittest.TestCase):
    def documents(self):
        return [
            {"people": [
                {"userId": PRIVATE, "email": PRIVATE, "displayName": PRIVATE,
                 "role": "administrator", "providers": ["google"]},
                {"userId": PRIVATE, "email": PRIVATE, "role": None, "providers": ["microsoft"]}], "limit": 100},
            {"events": [{"eventId": 1, "userId": PRIVATE, "actor": PRIVATE, "email": PRIVATE,
                         "previousRole": None, "role": "administrator"}], "limit": 50},
            {"clients": [{"provider": "google", "clientId": PRIVATE, "authority": PRIVATE},
                         {"provider": "microsoft", "clientId": PRIVATE}]}]

    def opener(self, documents=None):
        documents = self.documents() if documents is None else documents
        responses = iter([Response(b"", 401), Response(b"", 403), Response(b"", 401),
                          *(Response(v) for v in documents)])
        seen = []
        class Opener:
            def open(self, request, timeout):
                seen.append(request)
                return next(responses)
        return Opener(), seen

    def test_aggregate_report_does_not_publish_private_fields(self):
        opener, seen = self.opener()
        with patch.object(audit.urllib.request, "build_opener", return_value=opener) as build:
            report = audit.audit("https://staging.example.invalid/", TOKEN)
        self.assertTrue(report["passed"])
        self.assertEqual(report["people"]["role_counts"], {"administrator": 1, "none": 1})
        self.assertEqual(report["people"]["provider_account_counts"], {"google": 1, "microsoft": 1})
        self.assertEqual(report["people"]["role_counts_by_provider"],
                         {"google": {"administrator": 1}, "microsoft": {"none": 1}})
        self.assertEqual(report["events"]["transition_counts"], {"none->administrator": 1})
        self.assertEqual(report["exact_provider_bindings"], "unavailable-from-these-endpoints")
        self.assertEqual(report["owner_administrator_verification"], "not-established")
        self.assertNotIn(PRIVATE, json.dumps(report))
        self.assertNotIn(TOKEN, json.dumps(report))
        self.assertEqual([r.get_method() for r in seen], ["GET"] * 6)
        self.assertTrue(all(r.data is None for r in seen))
        self.assertTrue(all(r.get_header("X-laplace-operator-token") is None for r in seen[:3]))
        self.assertTrue(all(r.get_header("X-laplace-operator-token") == TOKEN for r in seen[3:]))
        self.assertEqual(build.call_args.args[0].proxies, {})
        self.assertIsInstance(build.call_args.args[1], audit.NoRedirect)

    def test_origin_validation_precedes_credential_read_and_network(self):
        invalid = ["http://remote.example.invalid", "https://user:secret@example.invalid",
                   "https://example.invalid/path", "https://example.invalid/?secret=x",
                   "https://example.invalid/#secret", "file:///private", "https://example.invalid:70000",
                   "https://example.invalid\\@other.invalid", "https://example.invalid\n"]
        with patch.object(audit, "credential") as credential, patch.object(audit.urllib.request, "build_opener") as build:
            for base in invalid:
                with self.subTest(base=base), self.assertRaises(audit.AuditError):
                    audit.audit(base)
            credential.assert_not_called()
            build.assert_not_called()
        for base in ("http://127.0.0.1:5187", "http://[::1]:5187", "http://localhost:5187", "https://example.invalid"):
            self.assertEqual(audit.origin(base), base)

    def test_redirect_handler_never_replays_credentials(self):
        handler = audit.NoRedirect()
        request = urllib.request.Request("https://staging.example.invalid", headers={"X-Laplace-Operator-Token": TOKEN})
        for status in (301, 302, 303, 307, 308):
            self.assertIsNone(handler.redirect_request(request, None, status, "", {}, "https://other.invalid"))

    def test_real_urllib_redirect_processing_does_not_contact_second_origin(self):
        for code in (301, 302, 303, 307, 308):
            requests = []
            class Transport(urllib.request.HTTPHandler):
                def http_open(self, request):
                    requests.append(request)
                    response = urllib.response.addinfourl(io.BytesIO(PRIVATE.encode()),
                        {"Location": "http://other.invalid/" + PRIVATE}, request.full_url, code)
                    response.msg = "Redirect"
                    return response
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), Transport(), audit.NoRedirect())
            with self.subTest(status=code):
                self.assertEqual(audit.read_route(opener, "http://127.0.0.1", "people", TOKEN), (code, None))
                self.assertEqual(len(requests), 1)
                self.assertEqual(requests[0].host, "127.0.0.1")

    def test_http_errors_discard_private_body_and_location(self):
        class Opener:
            def open(self, request, timeout):
                raise urllib.error.HTTPError(request.full_url, 302, PRIVATE,
                    {"Location": "https://other.invalid/" + TOKEN}, io.BytesIO(PRIVATE.encode()))
        status, value = audit.read_route(Opener(), "https://staging.example.invalid", "people", TOKEN)
        self.assertEqual((status, value), (302, None))

    def test_transport_and_invalid_json_errors_are_sanitized(self):
        class BrokenOpener:
            def open(self, request, timeout):
                raise urllib.error.URLError(PRIVATE + TOKEN)
        with self.assertRaises(audit.AuditError) as failure:
            audit.read_route(BrokenOpener(), "https://staging.example.invalid", "people", TOKEN)
        self.assertNotIn(PRIVATE, str(failure.exception))
        self.assertNotIn(TOKEN, str(failure.exception))
        for response in (Response(PRIVATE.encode()), Response([]), Response(b"{}", content_type="text/html"),
                         Response(b"x" * (audit.MAX_BODY + 1))):
            class Opener:
                def open(self, request, timeout):
                    return response
            with self.assertRaises(audit.AuditError) as failure:
                audit.read_route(Opener(), "https://staging.example.invalid", "people", TOKEN)
            self.assertNotIn(PRIVATE, str(failure.exception))

    def test_anonymous_success_stops_before_sending_credential(self):
        requests = []
        class Opener:
            def open(self, request, timeout):
                requests.append(request)
                return Response(PRIVATE.encode())
        with patch.object(audit.urllib.request, "build_opener", return_value=Opener()), self.assertRaises(audit.AuditError):
            audit.audit("https://staging.example.invalid", TOKEN)
        self.assertEqual(len(requests), 1)
        self.assertIsNone(requests[0].get_header("X-laplace-operator-token"))

    def test_limits_and_unknown_values_do_not_claim_completeness_or_leak(self):
        documents = self.documents()
        documents[0]["limit"] = 2
        documents[1]["limit"] = 1
        documents[0]["people"][0].update(role=PRIVATE, providers=[PRIVATE, PRIVATE])
        documents[1]["events"][0]["previousRole"] = PRIVATE
        documents[2]["clients"][0]["provider"] = PRIVATE
        report = audit.summarize(*documents)
        self.assertTrue(report["people"]["possibly_truncated"])
        self.assertTrue(report["events"]["possibly_truncated"])
        self.assertEqual(report["people"]["provider_account_counts"]["unrecognized"], 1)
        self.assertEqual(report["people"]["role_counts_by_provider"]["unrecognized"], {"unrecognized": 1})
        self.assertNotIn(PRIVATE, json.dumps(report))
        documents[0]["limit"] = True
        with self.assertRaises(audit.AuditError):
            audit.summarize(*documents)

    def test_credential_read_matches_installed_contract_without_overrides(self):
        with tempfile.TemporaryDirectory() as directory:
            prefix = Path(directory)
            (prefix / "secrets").mkdir()
            path = prefix / "secrets/operator.env"
            path.write_text("LAPLACE_OPERATOR_TOKEN='" + TOKEN + "'\n")
            with patch.dict(os.environ, {"LAPLACE_INSTALL_PREFIX": str(prefix)}, clear=True):
                self.assertEqual(audit.credential(), TOKEN)
                path.write_text("LAPLACE_OPERATOR_TOKEN='" + PRIVATE)
                with self.assertRaises(audit.AuditError) as failure:
                    audit.credential()
                self.assertNotIn(PRIVATE, str(failure.exception))
            with patch.dict(os.environ, {"LAPLACE_OPERATOR_TOKEN": TOKEN}, clear=True):
                self.assertEqual(audit.credential(), TOKEN)

    def test_absent_credential_and_header_injection_stop_before_network(self):
        with tempfile.TemporaryDirectory() as directory, \
                patch.dict(os.environ, {"LAPLACE_INSTALL_PREFIX": directory}, clear=True), \
                patch.object(audit.urllib.request, "build_opener") as build:
            with self.assertRaises(audit.AuditError):
                audit.audit("https://staging.example.invalid")
            with self.assertRaises(audit.AuditError):
                audit.audit("https://staging.example.invalid", TOKEN + "\r\nX-Extra: invalid")
            build.assert_not_called()

    def test_cli_failure_receipt_contains_only_sanitized_error(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "audit.json"
            with patch.object(audit, "audit", side_effect=audit.AuditError("Platform access request failed before a valid response.")), \
                    patch("sys.stdout", new_callable=io.StringIO) as output:
                code = audit.main(["--base", "https://staging.example.invalid", "--state-file", str(path)])
            self.assertEqual(code, 1)
            self.assertEqual(json.loads(path.read_text()), json.loads(output.getvalue()))
            self.assertFalse(json.loads(path.read_text())["passed"])


if __name__ == "__main__":
    unittest.main()
