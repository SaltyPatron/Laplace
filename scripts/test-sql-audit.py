#!/usr/bin/env python3
"""Regression tests for the dependency-free SQL corpus auditor."""

from __future__ import annotations

import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
SPEC = importlib.util.spec_from_file_location("sql_audit", ROOT / "scripts/sql-audit.py")
assert SPEC and SPEC.loader
AUDIT = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = AUDIT
SPEC.loader.exec_module(AUDIT)


class SqlLexerTests(unittest.TestCase):
    def test_semicolons_inside_dollar_body_do_not_split_definition(self):
        sql = """
            CREATE OR REPLACE FUNCTION ops.example() RETURNS int
            LANGUAGE plpgsql AS $fn$
            BEGIN
              PERFORM 1;
              RETURN 2;
            END
            $fn$;
            SELECT 3;
        """
        statements = AUDIT.split_sql(sql)
        self.assertEqual(2, len(statements))
        self.assertIn("PERFORM 1;", statements[0][0])
        self.assertEqual("SELECT 3;", statements[1][0].strip())

    def test_nested_comments_and_dollar_tags_normalize_stably(self):
        left = "SELECT /* outer /* nested */ done */ x FROM t WHERE n = 7"
        right = "select x from t -- comment\n where n=7"
        self.assertEqual(
            AUDIT.normalize_tokens(left, structural=False),
            AUDIT.normalize_tokens(right, structural=False),
        )
        self.assertEqual(
            AUDIT.normalize_tokens("SELECT $$ SELECT 1; $$", structural=True),
            AUDIT.normalize_tokens("select $body$ select 99; $body$", structural=True),
        )

    def test_literal_parameterization_preserves_exact_difference(self):
        left = "SELECT x FROM t WHERE kind = 'a' AND n = 1"
        right = "SELECT x FROM t WHERE kind = 'b' AND n = 2"
        self.assertNotEqual(
            AUDIT.normalize_tokens(left, structural=False),
            AUDIT.normalize_tokens(right, structural=False),
        )
        self.assertEqual(
            AUDIT.normalize_tokens(left, structural=True),
            AUDIT.normalize_tokens(right, structural=True),
        )


class EmbeddedSqlTests(unittest.TestCase):
    def test_csharp_comments_and_apostrophes_are_not_strings(self):
        source = '''
            // A source's state is not a SQL string.
            var sql = "SELECT id, name "
                    + "FROM ops.sources WHERE id = @id";
            // "SELECT fake FROM comments"
        '''
        chunks = AUDIT.string_chunks(source, ".cs")
        self.assertEqual(1, len(chunks))
        self.assertEqual(
            "SELECT id, name FROM ops.sources WHERE id = @id",
            chunks[0][0],
        )

    def test_c_adjacent_literals_are_joined(self):
        source = '''
            static const char *q =
                "SELECT id "
                /* compiler concatenates these */
                "FROM laplace.entities "
                "WHERE id = $1";
        '''
        chunks = AUDIT.string_chunks(source, ".c")
        self.assertEqual(1, len(chunks))
        self.assertIn("FROM laplace.entities", chunks[0][0])

    def test_python_implicit_string_concatenation_is_joined(self):
        source = '''
q = ("SELECT id "
     "FROM things "
     "WHERE id = %s")
        '''
        chunks = AUDIT.string_chunks(source, ".py")
        self.assertEqual([("SELECT id FROM things WHERE id = %s", 2, 4)], chunks)


class FindingTests(unittest.TestCase):
    def make(self, sql: str, kind: str | None = None):
        exact = AUDIT.normalize_tokens(sql, structural=False)
        return AUDIT.make_statement(
            "prod.sql", "production", "file", sql, 1, 1, 1, forced_kind=kind
        )

    def test_security_definer_requires_fixed_search_path(self):
        statement = self.make(
            "CREATE FUNCTION ops.f() RETURNS int LANGUAGE sql SECURITY DEFINER AS $$ SELECT 1 $$"
        )
        rules = {item.rule for item in AUDIT.definition_findings(statement, AUDIT.AuditConfig())}
        self.assertIn("LPSQL001", rules)

    def test_srf_and_numeric_cap_are_detected(self):
        statement = self.make(
            "CREATE FUNCTION ops.f(p_limit int DEFAULT 10) RETURNS TABLE(id int) "
            "LANGUAGE sql STABLE AS $$ SELECT 1 $$"
        )
        rules = {item.rule for item in AUDIT.definition_findings(statement, AUDIT.AuditConfig())}
        self.assertEqual({"LPSQL002", "LPSQL003"}, rules)

    def test_null_assignment_is_not_a_predicate_error(self):
        assignment = self.make("UPDATE jobs SET ended_at = NULL WHERE id = $1", "query")
        predicate = self.make("SELECT id FROM jobs WHERE ended_at = NULL", "query")
        assignment_rules = {item.rule for item in AUDIT.query_findings(assignment, AUDIT.AuditConfig())}
        predicate_rules = {item.rule for item in AUDIT.query_findings(predicate, AUDIT.AuditConfig())}
        self.assertNotIn("LPSQL101", assignment_rules)
        self.assertIn("LPSQL101", predicate_rules)

    def test_repository_hot_function_is_found_with_qualified_tokens(self):
        statement = self.make("SELECT realize.render_text(x.id) FROM things x", "query")
        config = AUDIT.AuditConfig(expensive_functions=["realize.render_text"])
        rules = {item.rule for item in AUDIT.query_findings(statement, config)}
        self.assertIn("LPSQL111", rules)

    def test_limit_without_order_is_review_candidate(self):
        statement = self.make("SELECT id FROM things LIMIT 5", "query")
        rules = {item.rule for item in AUDIT.query_findings(statement, AUDIT.AuditConfig())}
        self.assertIn("LPSQL104", rules)

    def test_function_wrapped_filter_key_is_a_plan_review_candidate(self):
        statement = self.make(
            "SELECT c.subject_id FROM laplace.consensus c "
            "WHERE consensus.relation_highway_bit(c.type_id) = 4",
            "query",
        )
        config = AUDIT.AuditConfig(
            filter_key_functions=["consensus.relation_highway_bit"]
        )
        rules = {item.rule for item in AUDIT.query_findings(statement, config)}
        self.assertIn("LPSQL112", rules)


class CloneTests(unittest.TestCase):
    def make(self, path: str, sql: str, ordinal: int = 1):
        return AUDIT.make_statement(
            path, "production", "file", sql, 1, 1, ordinal, forced_kind="query"
        )

    def test_exact_clone_ignores_formatting_and_comments(self):
        left = self.make("a.sql", "SELECT a, b FROM t WHERE id = 1")
        right = self.make("b.sql", "select a,b -- why\n from t where id=1")
        clusters = AUDIT.exact_clones([left, right], AUDIT.AuditConfig(exact_min_tokens=5))
        self.assertEqual(1, len(clusters))

    def test_near_clone_parameterizes_literals(self):
        template = (
            "SELECT a.id, a.name, count(*) FROM accounts a "
            "JOIN events e ON e.account_id = a.id "
            "WHERE e.kind = '{}' AND e.score > {} "
            "GROUP BY a.id, a.name ORDER BY count(*) DESC"
        )
        left = self.make("a.sql", template.format("open", 10))
        right = self.make("b.sql", template.format("closed", 20))
        config = AUDIT.AuditConfig(near_min_tokens=10, shingle_size=3, near_similarity=0.8)
        clusters = AUDIT.near_clones([left, right], [], config)
        self.assertEqual(1, len(clusters))
        self.assertEqual(1.0, clusters[0].score)


class DiscoveryTests(unittest.TestCase):
    def test_discovery_excludes_worktrees_and_classifies_test_projects(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            prod = root / "extension" / "x.sql.in"
            test = root / "app" / "Laplace.Widget.Tests" / "Queries.cs"
            worktree = root / ".worktrees" / "copy.sql"
            prod.parent.mkdir(parents=True)
            test.parent.mkdir(parents=True)
            worktree.parent.mkdir(parents=True)
            prod.write_text("SELECT id FROM prod;", encoding="utf-8")
            test.write_text('var q = "SELECT id FROM tests";', encoding="utf-8")
            worktree.write_text("SELECT id FROM duplicate;", encoding="utf-8")

            statements, inventory = AUDIT.discover(root, AUDIT.AuditConfig())

            self.assertEqual(1, inventory["sql_files"])
            self.assertFalse(any(item.path.startswith(".worktrees") for item in statements))
            test_units = [item for item in statements if item.path.startswith("app/")]
            self.assertEqual(["test"], [item.role for item in test_units])


if __name__ == "__main__":
    unittest.main()
