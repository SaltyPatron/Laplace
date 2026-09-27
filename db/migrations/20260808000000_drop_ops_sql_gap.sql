-- The MCP boundary accepts typed tools and named operations only, so it has no free-form
-- SQL calls for ops.sql_gap to record.
DROP FUNCTION IF EXISTS ops.sql_gap();
DROP FUNCTION IF EXISTS ops.repoint_sql_gap(text);
DROP FOREIGN TABLE IF EXISTS ops.sql_gap_ft;
