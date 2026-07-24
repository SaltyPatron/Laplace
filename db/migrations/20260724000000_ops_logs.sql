-- Ops logging, SQL-queryable (GH #601, campaign Workstream 1).
--
-- Logs stay plain files: if the database is down you still `less` the .log. They become
-- QUERYABLE through Postgres via file_fdw reading the csvlog sibling — no third-party log
-- service, and NO log rows are ever written into substrate tables. This is app-metadata
-- (the `ops` schema, DbUp), NOT the substrate extension: it describes the deployment, not
-- content/consensus. See db/migrations app_billing for the same app-schema precedent.
--
-- csvlog is enabled in the bootstrap managed block (log_destination = 'stderr,csvlog');
-- this migration applies cleanly whether or not that reload has reached the cluster yet —
-- repoint_pg_log() is a no-op until csvlog is live, then it aims ops.pg_log at the current
-- rotated file. DbUp wraps each script in one transaction.

CREATE SCHEMA IF NOT EXISTS ops;

CREATE EXTENSION IF NOT EXISTS file_fdw;

CREATE SERVER IF NOT EXISTS ops_log_files FOREIGN DATA WRAPPER file_fdw;

-- The 26-column Postgres csvlog shape (verbatim from file-fdw.sgml's worked example, PG 18).
-- The rotating filename is set live by ops.repoint_pg_log(); the placeholder below is never
-- read (a SELECT before the first repoint returns "file not found", which is the honest state).
CREATE FOREIGN TABLE IF NOT EXISTS ops.pg_log (
    log_time timestamp(3) with time zone,
    user_name text,
    database_name text,
    process_id integer,
    connection_from text,
    session_id text,
    session_line_num bigint,
    command_tag text,
    session_start_time timestamp with time zone,
    virtual_transaction_id text,
    transaction_id bigint,
    error_severity text,
    sql_state_code text,
    message text,
    detail text,
    hint text,
    internal_query text,
    internal_query_pos integer,
    context text,
    query text,
    query_pos integer,
    location text,
    application_name text,
    backend_type text,
    leader_pid integer,
    query_id bigint
) SERVER ops_log_files
  OPTIONS (filename 'ops_pg_log_awaiting_repoint.csv', format 'csv');

-- Aim ops.pg_log at the cluster's CURRENT csvlog file. The collector rotates the file
-- (timestamped name), so this is re-run after each rotation to follow it — call it from a
-- postmaster-start hook or a cron, or by hand. Safe to call anytime: returns a status
-- string instead of raising when csvlog is not enabled, so the migration and any scheduler
-- never fail just because logging config hasn't caught up.
CREATE OR REPLACE FUNCTION ops.repoint_pg_log()
    RETURNS text
    LANGUAGE plpgsql AS $$
DECLARE
    f text := pg_current_logfile('csvlog');
BEGIN
    IF f IS NULL THEN
        RETURN 'csvlog not active (log_destination lacks csvlog) — ops.pg_log left unpointed';
    END IF;
    EXECUTE format('ALTER FOREIGN TABLE ops.pg_log OPTIONS (SET filename %L)', f);
    RETURN f;
END;
$$;

SELECT ops.repoint_pg_log();

-- ops.app_log (the .NET side) is intentionally NOT created here: it reads a stable-filename
-- CSV sink that the shared Generic Host + logging foundation introduces (GH #602, Workstream
-- 2). A foreign table over a file no process writes would be dead scaffolding — it lands in
-- the same `ops` schema once that sink exists.
