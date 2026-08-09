-- The MCP boundary accepts typed tools and named operations only. Retire the
-- file_fdw ledger that existed solely to observe the removed free-form SQL hatch.
DROP FUNCTION IF EXISTS ops.sql_gap();
DROP FUNCTION IF EXISTS ops.repoint_sql_gap(text);
DROP FOREIGN TABLE IF EXISTS ops.sql_gap_ft;
