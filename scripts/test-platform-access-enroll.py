#!/usr/bin/env python3
"""Prove explicit initial enrollment, refusal, and uncertain-response recovery."""
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import urllib.error
import urllib.request
import urllib.response

spec = importlib.util.spec_from_file_location("platform_access_enroll",
    Path(__file__).with_name("platform-access-enroll.py"))
enroll = importlib.util.module_from_spec(spec)
spec.loader.exec_module(enroll)
PRIVATE = "private-account@example.invalid"
TOKEN = "private-operator-token"


class EnrollmentTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary.name)
        self.request = {"version": 1, "target": "staging", "host": "hart-server",
            "origin": "http://127.0.0.1:5187", "role": "administrator", "nonce": "a" * 64, "selectors": []}
        self.rows = [
            {"user_id": "00000000-0000-0000-0000-000000000001", "provider": "google", "issuer": "https://issuer.invalid",
             "subject": PRIVATE, "signed_in": True, "role": None, "event_count": 0, "latest_event": None},
            {"user_id": "00000000-0000-0000-0000-000000000002", "provider": "microsoft", "issuer": "https://other.invalid",
             "subject": PRIVATE, "signed_in": True, "role": None, "event_count": 0, "latest_event": None}]
        for row in self.rows:
            self.request["selectors"].append({"provider": row["provider"], "fingerprint_sha256":
                enroll.fingerprint(self.request, row["provider"], row["issuer"], row["subject"])})
        self.calls = []
        self.fail_before = False
        self.fail_after = False
        outer = self
        class Api:
            def __init__(self, base):
                pass
            def capabilities(self):
                outer.calls.append("capabilities")
            def person(self, row):
                outer.calls.append("person:" + row["provider"])
            def grant(self, user):
                outer.calls.append("grant:" + user)
                if outer.fail_before:
                    raise enroll.EnrollmentError("No valid response.")
                row = next(r for r in outer.rows if r["user_id"] == user)
                row.update(role="administrator", event_count=1, latest_event={"event_id": 7,
                    "actor": "installation-credential", "previous_role": None, "role": "administrator"})
                if outer.fail_after:
                    raise enroll.EnrollmentError("No valid response.")
                return 204
        self.patches = [patch.object(enroll.socket, "gethostname", return_value="hart-server"),
                        patch.object(enroll, "Api", Api),
                        patch.object(enroll, "binding_rows", side_effect=lambda: copy.deepcopy(self.rows))]
        for value in self.patches:
            value.start()

    def tearDown(self):
        for value in reversed(self.patches):
            value.stop()
        self.temporary.cleanup()

    def run_enrollment(self):
        return enroll.enroll(copy.deepcopy(self.request), self.directory)

    def grants(self):
        return [c for c in self.calls if c.startswith("grant:")]

    def test_fingerprint_is_length_framed_utf8_and_request_scoped(self):
        values = ["staging", "administrator", "a" * 64, "google", "https://issuer.invalid", "é"]
        framed = b"".join(len(v.encode()).to_bytes(8, "big") + v.encode() for v in values)
        actual = enroll.fingerprint(self.request, "google", values[-2], "é")
        self.assertEqual(actual, hashlib.sha256(framed).hexdigest())
        other = copy.deepcopy(self.request)
        other["nonce"] = "b" * 64
        self.assertNotEqual(actual, enroll.fingerprint(other, "google", values[-2], "é"))
        self.assertNotEqual(enroll.fingerprint(self.request, "google", "ab", "c"),
                            enroll.fingerprint(self.request, "google", "a", "bc"))

    def test_all_bindings_are_preflighted_and_only_selected_accounts_are_granted(self):
        self.rows.append({**self.rows[0], "user_id": "00000000-0000-0000-0000-000000000003", "subject": "another-person"})
        report = self.run_enrollment()
        self.assertEqual(len(self.grants()), 2)
        first = next(i for i, item in enumerate(self.calls) if item.startswith("grant:"))
        self.assertIn("person:google", self.calls[:first])
        self.assertIn("person:microsoft", self.calls[:first])
        self.assertTrue(report["passed"])
        self.assertEqual(self.rows[2]["role"], None)
        for secret in (PRIVATE, TOKEN, *(r["user_id"] for r in self.rows)):
            self.assertNotIn(secret, json.dumps(report))

    def test_same_request_resume_reads_back_without_regranting(self):
        self.run_enrollment()
        self.calls.clear()
        report = self.run_enrollment()
        self.assertEqual(self.grants(), [])
        self.assertEqual(report["providers"], {"google": "verified", "microsoft": "verified"})
        journal = self.directory / (self.request["nonce"] + ".json")
        if os.name == "posix":
            self.assertEqual(journal.stat().st_mode & 0o777, 0o600)

    def test_lost_success_response_reconciles_audit_without_second_put(self):
        self.fail_after = True
        report = self.run_enrollment()
        self.assertTrue(report["passed"])
        self.assertEqual(len(self.grants()), 2)
        self.calls.clear()
        self.assertTrue(self.run_enrollment()["passed"])
        self.assertEqual(self.grants(), [])

    def test_uncertain_uncommitted_request_is_never_retried(self):
        self.fail_before = True
        with self.assertRaises(enroll.EnrollmentError):
            self.run_enrollment()
        self.assertEqual(len(self.grants()), 1)
        self.fail_before = False
        self.calls.clear()
        with self.assertRaises(enroll.EnrollmentError):
            self.run_enrollment()
        self.assertEqual(self.grants(), [])

    def test_pending_committed_journal_is_reconciled_after_process_restart(self):
        self.request["selectors"] = self.request["selectors"][:1]
        self.fail_before = True
        with self.assertRaises(enroll.EnrollmentError):
            self.run_enrollment()
        self.rows[0].update(role="administrator", event_count=1, latest_event={"event_id": 10,
            "actor": "installation-credential", "previous_role": None, "role": "administrator"})
        self.calls.clear()
        report = self.run_enrollment()
        self.assertEqual(report["providers"], {"google": "reconciled"})
        self.assertEqual(self.grants(), [])

    def test_missing_ambiguous_unsigned_and_unselected_bindings_fail_before_writes(self):
        initial = copy.deepcopy(self.rows)
        variants = [initial[:1], initial + [initial[0]],
                    [{**initial[0], "signed_in": False}, initial[1]],
                    [{**initial[0], "issuer": "https://different.invalid"}, initial[1]],
                    [{**initial[0], "subject": "wrong-subject", "email": PRIVATE}, initial[1]]]
        for rows in variants:
            self.rows = rows
            with self.subTest(rows=len(rows)), self.assertRaises(enroll.EnrollmentError):
                self.run_enrollment()
            self.assertEqual(self.grants(), [])
            self.assertEqual(list(self.directory.iterdir()), [])

    def test_any_existing_role_or_revocation_history_refuses_entire_request(self):
        for role, count in (("administrator", 1), ("observer", 0), (None, 1)):
            self.rows[1].update(role=role, event_count=count)
            with self.subTest(role=role, count=count), self.assertRaises(enroll.EnrollmentError):
                self.run_enrollment()
            self.assertEqual(self.grants(), [])

    def test_revocation_after_completed_enrollment_is_not_repaired(self):
        self.run_enrollment()
        self.calls.clear()
        self.rows[0].update(role=None, event_count=2, latest_event={"event_id": 11,
            "actor": "some-admin", "previous_role": "administrator", "role": None})
        with self.assertRaises(enroll.EnrollmentError):
            self.run_enrollment()
        self.assertEqual(self.grants(), [])

    def test_recreated_account_and_changed_request_cannot_reuse_journal(self):
        self.run_enrollment()
        self.calls.clear()
        self.rows[0]["user_id"] = "00000000-0000-0000-0000-000000000009"
        with self.assertRaises(enroll.EnrollmentError):
            self.run_enrollment()
        self.assertEqual(self.grants(), [])
        self.request["selectors"] = self.request["selectors"][:1]
        with self.assertRaises(enroll.EnrollmentError):
            self.run_enrollment()

    def test_journal_failure_prevents_grant_transmission(self):
        with patch.object(enroll.Journal, "save", side_effect=enroll.EnrollmentError("Private journal unavailable.")), \
                self.assertRaises(enroll.EnrollmentError):
            self.run_enrollment()
        self.assertEqual(self.grants(), [])

    def test_concurrent_noninstallation_grant_cannot_satisfy_pending_request(self):
        self.request["selectors"] = self.request["selectors"][:1]
        self.fail_before = True
        with self.assertRaises(enroll.EnrollmentError):
            self.run_enrollment()
        self.rows[0].update(role="administrator", event_count=1, latest_event={"event_id": 10,
            "actor": "another-browser-administrator", "previous_role": None, "role": "administrator"})
        self.calls.clear()
        with self.assertRaises(enroll.EnrollmentError):
            self.run_enrollment()
        self.assertEqual(self.grants(), [])

    def test_corrupt_or_nonprivate_journal_refuses_before_account_changes(self):
        path = self.directory / (self.request["nonce"] + ".json")
        path.write_text(PRIVATE)
        path.chmod(0o600)
        with self.assertRaises(enroll.EnrollmentError) as failure:
            self.run_enrollment()
        self.assertNotIn(PRIVATE, str(failure.exception))
        self.assertEqual(self.calls, [])
        if os.name == "posix":
            path.chmod(0o644)
            with self.assertRaises(enroll.EnrollmentError):
                self.run_enrollment()

    @unittest.skipUnless(os.name == "posix", "POSIX inherited directory permissions")
    def test_setgid_private_directory_enrolls_and_keeps_journal_private(self):
        self.directory.chmod(0o2700)
        self.assertTrue(self.run_enrollment()["passed"])
        self.assertEqual(stat.S_IMODE(self.directory.stat().st_mode), 0o2700)
        path = self.directory / (self.request["nonce"] + ".json")
        self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)
        self.calls.clear()
        self.assertTrue(self.run_enrollment()["passed"])
        self.assertEqual(self.grants(), [])

    @unittest.skipUnless(os.name == "posix", "POSIX inherited directory permissions")
    def test_group_readable_directory_is_rejected_before_requests(self):
        self.directory.chmod(0o2740)
        with self.assertRaisesRegex(enroll.EnrollmentError, "mode 2740"):
            self.run_enrollment()
        self.assertEqual(self.calls, [])
        self.assertEqual(stat.S_IMODE(self.directory.stat().st_mode), 0o2740)

    def test_private_failed_input_never_enters_cli_receipt(self):
        request_path = self.directory / "request.json"
        receipt = self.directory / "receipt.json"
        request_path.write_text(PRIVATE)
        with patch("sys.stdout", new_callable=io.StringIO) as output:
            code = enroll.main(["--request", str(request_path), "--journal-directory", str(self.directory),
                                "--state-file", str(receipt)])
        self.assertEqual(code, 1)
        self.assertEqual(json.loads(receipt.read_text()), json.loads(output.getvalue()))
        self.assertNotIn(PRIVATE, output.getvalue())
        self.assertEqual(self.grants(), [])

    def test_wrong_host_and_non_loopback_origin_fail_before_network(self):
        with patch.object(enroll.socket, "gethostname", return_value="hart-prod"), self.assertRaises(enroll.EnrollmentError):
            self.run_enrollment()
        for origin in ("https://staging.example.invalid", "http://remote.invalid", 4, "http://127.0.0.1:5187/path"):
            self.request["origin"] = origin
            with self.subTest(origin=origin), self.assertRaises(enroll.EnrollmentError):
                self.run_enrollment()
        self.assertEqual(self.calls, [])


class TransportAndDatabaseTests(unittest.TestCase):
    def api(self):
        with patch.object(enroll.access, "credential", return_value=TOKEN):
            return enroll.Api("http://127.0.0.1:5187")

    def test_private_path_distinguishes_ownership_and_modes_without_disclosure(self):
        value = SimpleNamespace(st_mode=stat.S_IFDIR | 0o2700, st_uid=42)
        path = SimpleNamespace(is_symlink=lambda: False, is_absolute=lambda: True, stat=lambda: value)
        with patch.object(enroll.os, "name", "posix"), patch.object(enroll.os, "getuid", return_value=42, create=True):
            enroll.private_path(path, directory=True)
            for mode in (0o2740, 0o2701, 0o3700, 0o4700):
                value.st_mode = stat.S_IFDIR | mode
                with self.assertRaisesRegex(enroll.EnrollmentError, f"mode {mode:04o}"):
                    enroll.private_path(path, directory=True)
            value.st_mode = stat.S_IFREG | 0o2600
            with self.assertRaisesRegex(enroll.EnrollmentError, "mode 2600"):
                enroll.private_path(path)
            value.st_mode = stat.S_IFREG | 0o600
            enroll.private_path(path)
            value.st_uid = 43
            with self.assertRaisesRegex(enroll.EnrollmentError, "not owned by this operator"):
                enroll.private_path(path)

    def test_api_grant_is_atomic_initial_assignment_and_route_bounded(self):
        api = self.api()
        with patch.object(api, "call", return_value=(204, None)) as call:
            api.grant("00000000-0000-0000-0000-000000000001")
        self.assertEqual(call.call_args.args[2], {"role": "administrator", "onlyIfNeverAssigned": True})
        for method, path in (("POST", "/v1/admin/access/capabilities"), ("PUT", "/other"),
                             ("GET", "/v1/admin/access/people?search=" + PRIVATE)):
            with self.assertRaises(enroll.EnrollmentError):
                api.call(method, path)

    def test_missing_capability_stops_before_writes(self):
        api = self.api()
        for response in ((404, None), (200, {}), (200, {"initialRoleAssignment": False})):
            with patch.object(api, "call", return_value=response), self.assertRaises(enroll.EnrollmentError):
                api.capabilities()

    def test_urllib_redirects_and_private_response_errors_never_replay_or_print_token(self):
        for code in (301, 302, 303, 307, 308):
            seen = []
            class Transport(urllib.request.HTTPHandler):
                def http_open(self, request):
                    seen.append(request)
                    response = urllib.response.addinfourl(io.BytesIO((PRIVATE + TOKEN).encode()),
                        {"Location": "http://other.invalid/" + PRIVATE}, request.full_url, code)
                    response.msg = "Redirect"
                    return response
            api = self.api()
            api.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), Transport(), enroll.access.NoRedirect())
            self.assertEqual(api.call("GET", "/v1/admin/access/capabilities"), (code, None))
            self.assertEqual(len(seen), 1)
        api = self.api()
        with patch.object(api.opener, "open", side_effect=urllib.error.URLError(PRIVATE + TOKEN)):
            with self.assertRaises(enroll.EnrollmentError) as failure:
                api.call("GET", "/v1/admin/access/capabilities")
        self.assertNotIn(PRIVATE, str(failure.exception))
        self.assertNotIn(TOKEN, str(failure.exception))

    def test_database_uses_only_declared_application_and_readonly_sql_stdin(self):
        env = {"PGHOST": "/tmp", "PGPORT": "5432", "PGUSER": "migration-owner", "PGDATABASE": "application-db",
               "LAPLACE_APP_DBNAME": "application-db", "LAPLACE_PG_PREFIX": str(Path.cwd())}
        with patch.dict(os.environ, env, clear=True), \
                patch.object(enroll.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "[]", "")) as run:
            self.assertEqual(enroll.binding_rows(), [])
            args = run.call_args.args[0]
            self.assertEqual(args[args.index("-d") + 1], "application-db")
            self.assertEqual(args[-2:], ["-f", "-"])
            self.assertIn("BEGIN READ ONLY", run.call_args.kwargs["input"])
            self.assertIn("LIMIT 1001", run.call_args.kwargs["input"])
            self.assertNotIn("INSERT", run.call_args.kwargs["input"])
            with patch.dict(os.environ, {"PGDATABASE": "wrong-db"}), self.assertRaises(enroll.EnrollmentError):
                enroll.binding_rows()
            self.assertEqual(run.call_count, 1)

    def test_database_error_and_private_stdout_are_not_published(self):
        env = {"PGHOST": "/tmp", "PGPORT": "5432", "PGUSER": "migration-owner", "PGDATABASE": "application-db",
               "LAPLACE_APP_DBNAME": "application-db", "LAPLACE_PG_PREFIX": str(Path.cwd())}
        for result in (subprocess.CompletedProcess([], 1, PRIVATE, TOKEN),
                       subprocess.CompletedProcess([], 0, PRIVATE, ""),
                       subprocess.CompletedProcess([], 0, json.dumps([{}] * 1001), "")):
            with patch.dict(os.environ, env, clear=True), patch.object(enroll.subprocess, "run", return_value=result), \
                    self.assertRaises(enroll.EnrollmentError) as failure:
                enroll.binding_rows()
            self.assertNotIn(PRIVATE, str(failure.exception))
            self.assertNotIn(TOKEN, str(failure.exception))


if __name__ == "__main__":
    unittest.main()
