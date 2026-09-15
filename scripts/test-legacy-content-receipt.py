#!/usr/bin/env python3
"""Focused SQL projection/parser checks; owner DB fixtures verify actual bytes."""
from __future__ import annotations

from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
from lib.legacy_content_receipt import frozen_receipt_sql, receipt_stream_sql  # noqa: E402

try:
    from pglast import ast as pgast, parse_sql
except ImportError:
    parse_sql = None

CONTEXT = "SELECT jsonb_build_object('kind','context','observed_at',clock_timestamp(),'text','水; 🧪');"
SUMMARY = "SELECT jsonb_build_object('kind','plan','count',count(*)) FROM pg_temp.repair_plan;"


class LegacyReceiptSqlTests(unittest.TestCase):
    def test_preamble_and_direct_stream_share_the_identical_frozen_projection(self):
        measured = frozen_receipt_sql(CONTEXT, SUMMARY)
        direct = frozen_receipt_sql(CONTEXT, SUMMARY, resource_preamble=False)
        setup = measured.split("WITH record_sizes AS MATERIALIZED (", 1)[0]
        self.assertEqual(setup + receipt_stream_sql(), direct)
        self.assertEqual(1, setup.count("clock_timestamp()"))
        self.assertEqual(1, setup.count("'kind','native-input'"))
        self.assertEqual(1, setup.count("'kind','physicality'"))
        self.assertIn("'action',action,'original',original,'existing_target',existing_target", setup)
        self.assertIn("'migration_proposal',migration_proposal,'proposed',proposed,'evidence',evidence", setup)
        self.assertIn("'entity',entity,'content',content", setup)
        self.assertNotIn(receipt_stream_sql(), measured)

    @unittest.skipIf(parse_sql is None, "optional PostgreSQL parser is not installed")
    def test_parser_confirms_only_control_payload_is_materialized_and_header_covers_full_view(self):
        statements = parse_sql(frozen_receipt_sql(CONTEXT, SUMMARY))
        self.assertEqual(["VariableSetStmt", "CreateTableAsStmt", "ViewStmt", "SelectStmt"],
                         [type(node.stmt).__name__ for node in statements])
        table = statements[1].stmt
        self.assertEqual("repair_receipt_control", table.into.rel.relname)
        self.assertEqual("t", table.into.rel.relpersistence)
        self.assertEqual("repair_receipt_records", statements[2].stmt.view.relname)
        header = statements[3].stmt
        self.assertEqual("record_sizes", header.fromClause[0].relname)
        sizes = header.withClause.ctes[0].ctequery
        self.assertEqual("repair_receipt_records", sizes.fromClause[0].relname)
        self.assertEqual(["phase", "line_bytes"],
                         [target.name or target.val.fields[0].sval for target in sizes.targetList])
        self.assertIsNone(sizes.limitCount)
        self.assertIsNone(sizes.whereClause)
        expression = header.targetList[0].val
        self.assertIsInstance(expression, pgast.FuncCall)
        keys = [arg.val.sval for arg in expression.args[::2]]
        self.assertEqual(["kind", "schema", "physicality_rows", "native_input_rows",
                          "context_rows", "summary_rows", "plan_bytes", "max_line_bytes",
                          "max_jsonl_line_bytes", "temporary_relation_bytes",
                          "temporary_relation_count", "temporary_relation_scope", "plan_context",
                          "plan_summary"], keys)
        storage = header.withClause.ctes[1].ctequery
        self.assertEqual("pg_class", storage.fromClause[0].relname)
        self.assertEqual("pg_catalog", storage.fromClause[0].schemaname)
        size_sql = frozen_receipt_sql(CONTEXT, SUMMARY).split("temporary_storage AS MATERIALIZED (", 1)[1]
        self.assertEqual(1, size_sql.count("pg_total_relation_size(c.oid)"))
        self.assertIn("c.relnamespace=pg_my_temp_schema() AND c.relkind IN ('r','m')", size_sql)
        self.assertNotIn("pg_indexes_size", size_sql)
        context = expression.args[keys.index("plan_context") * 2 + 1].subselect
        self.assertEqual("repair_receipt_control", context.fromClause[0].relname)
        self.assertEqual("pg_temp", context.fromClause[0].schemaname)
        frozen_fields = context.targetList[0].val.args
        self.assertEqual(["database", "database_oid", "system_identifier", "transaction",
                          "observed_at", "producer_generation", "chess_coordinate_recipe",
                          "write_epoch_before", "substrate_extension_version", "geometry_extension_version"],
                         [key.val.sval for key in frozen_fields[::2]])
        # Every value is a field of the captured row, never a fresh clock/catalog call.
        for key, value in zip(frozen_fields[::2], frozen_fields[1::2]):
            self.assertIsInstance(value, pgast.A_Expr)
            self.assertEqual("->", value.name[0].sval)
            self.assertEqual("record", value.lexpr.fields[0].sval)
            self.assertEqual(key.val.sval, value.rexpr.val.sval)
        summary = expression.args[keys.index("plan_summary") * 2 + 1].subselect
        self.assertEqual("repair_receipt_control", summary.fromClause[0].relname)
        self.assertEqual("pg_temp", summary.fromClause[0].schemaname)
        self.assertIsInstance(summary.targetList[0].val, pgast.ColumnRef)
        self.assertEqual("record", summary.targetList[0].val.fields[0].sval)
        self.assertEqual("phase", summary.whereClause.lexpr.fields[0].sval)
        self.assertEqual(3, summary.whereClause.rexpr.val.ival)
        stream = parse_sql(receipt_stream_sql())[0].stmt
        self.assertEqual("repair_receipt_records", stream.fromClause[0].relname)
        self.assertEqual(["phase", "sort_key"], [item.node.fields[0].sval for item in stream.sortClause])


if __name__ == "__main__":
    unittest.main(verbosity=2)
