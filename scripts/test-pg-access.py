#!/usr/bin/env python3
"""Pure tests for the redacted audit, peer identities and managed DB boundaries."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
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

    def test_ci_runs_the_executable_audit_and_database_contract_tests(self):
        source = (ROOT / ".github/workflows/laplace.yml").read_text()
        self.assertIn("python3 scripts/test-pg-access.py", source)
        self.assertIn("ManagedServiceDatabaseTests", source)

    def test_mcp_readiness_uses_bounded_serving_probes_not_a_full_entity_audit(self):
        source = (ROOT / "app/Laplace.Endpoints.Mcp/McpHttpHost.cs").read_text()
        self.assertNotIn("SubstrateHealthAsync", source)
        self.assertIn("EntitiesAndConsensusExistAsync", source)
        self.assertIn("PerfCacheProbeAsync", source)
        self.assertIn("budget.CancelAfter(TimeSpan.FromSeconds(5))", source)


if __name__ == "__main__":
    unittest.main()
