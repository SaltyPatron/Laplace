#!/usr/bin/env python3
"""Mutation checks for runtime SQL ownership, followed by the repository gate."""
import importlib.util
from pathlib import Path
import subprocess
import sys
import unittest

path = Path(__file__).with_name("sql-catalog-gate.py")
spec = importlib.util.spec_from_file_location("sql_catalog_gate", path)
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


class SqlOwnership(unittest.TestCase):
    def test_native_concatenation_is_one_statement(self):
        self.assertEqual(list(gate.statements('const char *q = "SELECT id " "FROM entities";')), ['SELECT id FROM entities'])

    def test_comments_and_catalog_keys_are_not_sql(self):
        self.assertEqual(list(gate.statements('// "SELECT bad"\nSqlCatalog.Get("browse.page");')), [])

    def test_csharp_raw_and_verbatim_literals_are_captured(self):
        self.assertEqual(len(list(gate.statements('q = """SELECT id\nFROM entities"""; r = @"SELECT name FROM entities";'))), 2)

    def test_quote_character_literals_do_not_turn_comments_into_sql(self):
        source = r'''switch (c) {
            case '"': cp = '"'; break;
            case '\\': cp = '\\'; break;
            case '\'': cp = '\''; break;
        }
        // "SELECT hidden FROM comment"
        /* This is a "copy of a fact", not a runtime query. */
        const char *label = "done";
        '''
        self.assertEqual(list(gate.statements(source)), [])

    def test_actual_queries_after_characters_remain_visible(self):
        for literal in (r''' '"' ''', r''' '\"' ''', r''' '\\' ''', r''' '\'' ''',
                        r''' u8'"' ''', r''' u'"' ''', r''' U'"' ''', r''' L'"' '''):
            with self.subTest(literal=literal):
                source = 'auto c = ' + literal + '; const char *q = "SELECT id " /* join */ "FROM entities";'
                self.assertEqual(list(gate.statements(source)), ['SELECT id FROM entities'])

    def test_native_and_csharp_prefixed_queries_remain_visible(self):
        for prefix in ('u8', 'u', 'U', 'L', '$'):
            with self.subTest(prefix=prefix):
                source = r'''auto c = '\"'; q = ''' + prefix + '"SELECT id FROM entities";'
                self.assertEqual(list(gate.statements(source)), ['SELECT id FROM entities'])

    def test_character_expression_does_not_join_separate_string_literals(self):
        self.assertEqual(list(gate.statements('const auto q = "SEL" + \'x\' + "ECT id FROM entities";')), [])

    def test_cpp_numeric_separators_do_not_hide_queries(self):
        source = '''auto n = 1'000; const char *q = "SELECT id FROM entities"; auto m = 2'000;'''
        self.assertEqual(list(gate.statements(source)), ['SELECT id FROM entities'])

    def test_new_changed_and_duplicated_queries_fail(self):
        old = 'q = "SELECT id FROM entities";'
        baseline = gate.fingerprints(old)
        self.assertFalse(gate.excess(gate.fingerprints(old), baseline))
        for mutant in (old.replace('id', 'name'), old + old, old + ' q = "SELECT name FROM names";'):
            self.assertTrue(gate.excess(gate.fingerprints(mutant), baseline))

    def test_migration_to_catalog_reduces_debt(self):
        old = gate.fingerprints('q = "SELECT id FROM entities";')
        self.assertFalse(gate.excess(gate.fingerprints('q = SqlCatalog.Get("entity.ids");'), old))

    def test_catalog_comments_between_macros_do_not_merge_parameter_contracts(self):
        source = '''SQL_QUERY("masks", "bytea[],bytea[]", "SELECT f($1,$2)")
        /* Direct indexed containment selects candidates, never unverified body rows. */
        SQL_QUERY("readback", "bytea,bytea[],bytea,int4", "SELECT g($1,$2,$3,$4)")
        // Trailing commentary must not hide the final query either.
        '''
        entries = gate.catalog_entries(source)
        self.assertEqual([("masks", "bytea[],bytea[]", "SELECT f($1,$2)"),
                          ("readback", "bytea,bytea[],bytea,int4", "SELECT g($1,$2,$3,$4)")], entries)
        self.assertEqual([], gate.catalog_errors(entries))

    def test_catalog_comments_between_arguments_and_fragments_are_ignored(self):
        source = '''/* SQL_QUERY("fake", "", "SELECT forbidden") */
        SQL_QUERY /* owned */ ("real", // parameter declaration
            "bytea", /* body */ "SELECT id " /* join */ "FROM t WHERE id=$1")'''
        self.assertEqual([("real", "bytea", "SELECT id FROM t WHERE id=$1")], gate.catalog_entries(source))

    def test_catalog_literal_comment_markers_and_escaped_quotes_are_preserved(self):
        source = r'''SQL_QUERY("literal", "bytea", "SELECT '/* ) */', '-- (', \"name\" FROM t WHERE id=$1")'''
        self.assertEqual(r'''SELECT '/* ) */', '-- (', \"name\" FROM t WHERE id=$1''',
                         gate.catalog_entries(source)[0][2])

    def test_invalid_catalog_syntax_cannot_hide_a_later_entry(self):
        valid = 'SQL_QUERY("one", "", "SELECT 1")'
        for source in (valid + ' nonsense', valid + ' /* unclosed',
                       'SQL_QUERY("one", "" "SELECT 1")',
                       'SQL_QUERY("one", "", SOME_MACRO)',
                       'SQL_QUERY("one", "", "SELECT 1"',
                       valid + '\nSQL_QUERY("two", "", "SELECT 2"'):
            with self.subTest(source=source), self.assertRaisesRegex(ValueError, "invalid macro syntax"):
                gate.catalog_entries(source)

    def test_catalog_checks_every_query_after_comments(self):
        prefix = 'SQL_QUERY("one", "", "SELECT 1") /* boundary */\n'
        for final, message in (( 'SQL_QUERY("two", "bytea", "SELECT $2")', "parameters disagree"),
                              ( 'SQL_QUERY("one", "", "SELECT 2")', "duplicate native query"),
                              ( 'SQL_QUERY("two", "", "SELECT generate_series(1,2)")', "canonical native operation")):
            with self.subTest(final=final):
                self.assertTrue(any(message in error for error in gate.catalog_errors(gate.catalog_entries(prefix + final))))


if __name__ == "__main__":
    result = unittest.TextTestRunner().run(unittest.defaultTestLoader.loadTestsFromTestCase(SqlOwnership))
    if not result.wasSuccessful():
        sys.exit(1)
    sys.exit(subprocess.call([sys.executable, str(path)]))
