-- The PostgreSQL csvlog is exposed read-only through file_fdw in the `ops` schema. Logs
-- remain files on disk; no log row is written into a substrate table. The `ops` schema
-- describes the deployment, not content or consensus.
--
-- repoint_pg_log() returns a status string while csvlog is inactive, so this script applies
-- before or after log_destination includes csvlog.

CREATE SCHEMA IF NOT EXISTS ops;

CREATE EXTENSION IF NOT EXISTS file_fdw;

CREATE SERVER IF NOT EXISTS ops_log_files FOREIGN DATA WRAPPER file_fdw;

-- The 26-column PostgreSQL csvlog shape. The filename is set by ops.repoint_pg_log(); until
-- then a SELECT reports the file as missing.
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

-- Points ops.pg_log at pg_current_logfile('csvlog'). The collector rotates to timestamped
-- names, so this must run again after each rotation. Returns a status string instead of
-- raising when csvlog is not active.
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

