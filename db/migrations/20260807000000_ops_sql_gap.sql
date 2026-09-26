-- ops.sql_gap exposes laplace-sql-gap.csv (LaplaceInstall.OpsLogDirectory), one RFC 4180
-- row per accepted MCP `sql` call, through the ops_log_files file_fdw server.

CREATE SCHEMA IF NOT EXISTS ops;
CREATE EXTENSION IF NOT EXISTS file_fdw;
CREATE SERVER IF NOT EXISTS ops_log_files FOREIGN DATA WRAPPER file_fdw;

DO $$
BEGIN
    IF to_regclass('ops.sql_gap_ft') IS NULL THEN
        CREATE FOREIGN TABLE ops.sql_gap_ft (
            log_time       timestamptz,
            duration_ms    bigint,
            result_chars   bigint,
            is_error       boolean,
            query          text
        ) SERVER ops_log_files
          OPTIONS (filename 'ops_sql_gap_awaiting_repoint.csv', format 'csv', header 'true');
    END IF;
END $$;

CREATE OR REPLACE FUNCTION ops.repoint_sql_gap(p_dir text)
    RETURNS text
    LANGUAGE plpgsql AS $$
BEGIN
    IF p_dir IS NULL OR btrim(p_dir) = '' THEN
        RAISE EXCEPTION 'ops.repoint_sql_gap: a log directory is required';
    END IF;
    EXECUTE format(
        'ALTER FOREIGN TABLE ops.sql_gap_ft OPTIONS (SET filename %L)',
        rtrim(p_dir, '/') || '/laplace-sql-gap.csv');
    RETURN format('repointed sql_gap at %s', p_dir);
END;
$$;

CREATE OR REPLACE FUNCTION ops.sql_gap()
    RETURNS TABLE(
        log_time       timestamptz,
        duration_ms    bigint,
        result_chars   bigint,
        is_error       boolean,
        query          text)
    LANGUAGE plpgsql
    STABLE
AS $$
BEGIN
    BEGIN
        RETURN QUERY SELECT f.log_time, f.duration_ms, f.result_chars, f.is_error, f.query
                     FROM ops.sql_gap_ft f;
    EXCEPTION WHEN OTHERS THEN
        -- A missing file reads as an empty ledger.
        NULL;
    END;
END;
$$;
