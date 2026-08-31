#!/usr/bin/env python3
"""Pure tests for the redacted audit, peer identities and managed DB boundaries."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import types
import unittest

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


audit = load("pg_access", "scripts/audit-pg-access.py")
peer_map = load("peer_map", "scripts/render-pg-peer-map.py")


class AuditTests(unittest.TestCase):
    def test_each_query_forces_socket_noninteractive_readonly_and_clean_environment(self):
        calls = []

        def run(argv, **kwargs):
            calls.append(argv)
            self.assertEqual("/var/run/postgresql", argv[argv.index("-h") + 1])
            self.assertEqual("laplace_admin", argv[argv.index("-U") + 1])
            self.assertEqual("laplace", argv[argv.index("-d") + 1])
            self.assertIn("-X", argv)
            self.assertIn("-w", argv)
            self.assertEqual(subprocess.DEVNULL, kwargs["stdin"])
            self.assertNotIn("shell", kwargs)
            self.assertNotIn("PGPASSWORD", kwargs["env"])
            self.assertNotIn("LAPLACE_DB", kwargs["env"])
            self.assertIn("default_transaction_read_only=on", kwargs["env"]["PGOPTIONS"])
            self.assertLessEqual(kwargs["timeout"], 10)
            self.assertTrue(argv[-1].startswith("SELECT "))
            self.assertNotIn(";", argv[-1])
            for prohibited in ("pg_authid", "rolpassword", "pg_read_file", "pg_reload_conf", "pg_stat_statements"):
                self.assertNotIn(prohibited, argv[-1])
            return types.SimpleNamespace(returncode=0, stdout='[{"sample": true}]')

        result = audit.database_snapshot(run)
        self.assertEqual(len(audit.QUERIES), len(calls))
        self.assertTrue(all(item["rows"] == [{"sample": True}] for item in result.values()))
        self.assertIn("split_part(option, '=', 1)", audit.QUERIES["hba_file_rules"])
        self.assertNotIn("error,", audit.QUERIES["hba_file_rules"])
        self.assertNotIn("query,", audit.QUERIES["clients"])

    def test_errors_and_invalid_responses_never_echo_diagnostics(self):
        for code, stdout in [(1, "test-sentinel-never-print"), (0, "test-sentinel-never-print")]:
            with self.subTest(code=code):
                result = audit.database_snapshot(lambda *a, **k: types.SimpleNamespace(
                    returncode=code, stdout=stdout, stderr="test-sentinel-never-print"))
                self.assertNotIn("test-sentinel-never-print", json.dumps(result))
                self.assertTrue(all(item["status"] == "unavailable" for item in result.values()))

    def test_timeout_details_are_omitted_and_firewall_comments_are_not_collected(self):
        def timeout(*args, **kwargs):
            raise subprocess.TimeoutExpired("test-sentinel-never-print", 10)

        self.assertNotIn("test-sentinel-never-print", json.dumps(audit.database_snapshot(timeout)))
        calls = []

        def run(argv, **kwargs):
            calls.append(argv)
            return types.SimpleNamespace(returncode=0, stdout="test-firewall-comment")

        result = audit.network_snapshot(run)
        self.assertNotIn("output", result["ufw_rules"])
        self.assertNotIn("output", result["nft_rules"])
        self.assertTrue(all("sudo" not in argv and "-p" not in argv for argv in calls))


class PeerMapTests(unittest.TestCase):
    def test_rebootstrap_keeps_both_installed_services_and_operator(self):
        rows = [line.split() for line in peer_map.render("ahart", exists=lambda name: True).splitlines() if not line.startswith("#")]
        self.assertEqual([["laplace_map", name, "laplace_admin"] for name in
            ("laplace-runner", "postgres", "ahart", "laplace-mcp", "laplace-lichess")], rows)

    def test_uninstalled_accounts_are_not_granted_and_operator_is_not_duplicated(self):
        text = peer_map.render("laplace-runner", exists=lambda name: name == "laplace-mcp")
        self.assertEqual(1, text.count("laplace-runner"))
        self.assertIn("laplace-mcp", text)
        self.assertNotIn("laplace-lichess", text)

    def test_operator_cannot_inject_a_mapping(self):
        for name in ("ahart\nother any postgres", "ahart postgres", "../other", "#comment"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                peer_map.render(name)

    def test_bootstrap_resolves_mapping_before_opening_ident_file(self):
        source = (ROOT / "scripts/bootstrap-laplace-runner.sh").read_text()
        renderer = source.index('peer_identities="$(python3')
        destination = source.index('tee "$PG_IDENT_FILE"')
        self.assertLess(renderer, destination)
        self.assertIn("render-pg-peer-map.py", source[renderer:destination])

    def test_mcp_readiness_uses_bounded_serving_probes_not_a_full_entity_audit(self):
        source = (ROOT / "app/Laplace.Endpoints.Mcp/McpHttpHost.cs").read_text()
        self.assertNotIn("SubstrateHealthAsync", source)
        self.assertIn("EntitiesAndConsensusExistAsync", source)
        self.assertIn("PerfCacheProbeAsync", source)
        self.assertIn("budget.CancelAfter(TimeSpan.FromSeconds(5))", source)


class DatabaseHealthScriptTests(unittest.TestCase):
    def _run_health(self, *, connect_fail=False):
        with tempfile.TemporaryDirectory(prefix="laplace-db-health-test-") as temporary:
            temp = Path(temporary)
            fake_bin = temp / "bin"
            fake_bin.mkdir()
            log = temp / "psql-calls.jsonl"
            psql = fake_bin / "psql"
            psql.write_text(
                f"""#!{sys.executable}
import json
import os
import sys

argv = sys.argv[1:]
with open(os.environ["FAKE_PSQL_LOG"], "a", encoding="utf-8") as stream:
    stream.write(json.dumps(argv) + "\\n")

def value(flag):
    return argv[argv.index(flag) + 1]

database = value("-d")
query = argv[-1]

# Reproduce the production psql contract: psql metasyntax is not interpolated in
# a -c command string and therefore must never reach this server-command lane.
if ":'" in query or ':"' in query:
    print("syntax error at or near ':'", file=sys.stderr)
    raise SystemExit(9)

if query == "SELECT 1":
    if os.environ.get("FAKE_PSQL_CONNECT_FAIL") == "1":
        raise SystemExit(7)
    if database != "laplace":
        print("connection probe targeted wrong database", file=sys.stderr)
        raise SystemExit(8)
    print("1")
elif "SELECT extversion FROM pg_extension" in query:
    print("test-ext")
elif "string_agg(name" in query:
    print("")
elif "FROM pg_index" in query and "count(*)" in query:
    print("0")
elif "FROM pg_constraint" in query and "count(*)" in query:
    print("0")
elif "FROM laplace.ingest_run_journal" in query and "count(*)" in query:
    print("0")
elif "FROM ops.index_health()" in query and "count(*)" in query:
    print("0")
else:
    print("unexpected health query: " + query, file=sys.stderr)
    raise SystemExit(11)
"""
            )
            psql.chmod(0o755)

            env = os.environ.copy()
            env["PATH"] = str(fake_bin) + os.pathsep + env.get("PATH", "")
            env["FAKE_PSQL_LOG"] = str(log)
            if connect_fail:
                env["FAKE_PSQL_CONNECT_FAIL"] = "1"

            result = subprocess.run(
                ["bash", str(ROOT / "scripts/check-database-health.sh"), "laplace"],
                cwd=ROOT,
                env=env,
                capture_output=True,
                text=True,
                timeout=10,
            )
            calls = [json.loads(line) for line in log.read_text().splitlines()]
            return result, calls

    def test_health_connects_to_target_database_without_psql_command_metasyntax(self):
        result, calls = self._run_health()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("DB_HEALTH_OK database=laplace extension=test-ext", result.stdout)
        self.assertGreaterEqual(len(calls), 7)
        self.assertEqual("laplace", calls[0][calls[0].index("-d") + 1])
        self.assertEqual("SELECT 1", calls[0][-1])
        self.assertTrue(all(":'" not in call[-1] and ':"' not in call[-1] for call in calls))

    def test_health_fails_closed_when_target_database_is_not_connectable(self):
        result, calls = self._run_health(connect_fail=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("database 'laplace' is not connectable", result.stderr)
        self.assertEqual(1, len(calls))
        self.assertEqual("SELECT 1", calls[0][-1])


if __name__ == "__main__":
    unittest.main()
