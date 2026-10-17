CREATE EXTENSION IF NOT EXISTS postgis;

CREATE EXTENSION IF NOT EXISTS laplace_geom;

CREATE EXTENSION IF NOT EXISTS laplace_substrate;

-- Per-statement execution and planning time. The library is loaded through
-- shared_preload_libraries (CpuTopologyCommands.EmitPgTuning) next to laplace_substrate,
-- whose perfcache blobs are mapped at preload; loading it requires a postmaster restart.
-- The extension can be created before that restart; the view then returns no rows.
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;

-- =============================================================================
-- Ops CSV Logs & Chess Drops Foreign Tables (file_fdw)
-- =============================================================================

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

-- One foreign table per known role; ops.repoint_app_log sets the filenames. The 6-column
-- shape is OpsLogCsvFormatter.Columns.
DO $$
DECLARE r text;
BEGIN
    FOREACH r IN ARRAY ARRAY['cli','mcp','uci','migrations','api'] LOOP
        IF to_regclass('ops.app_log_' || r) IS NULL THEN
            EXECUTE format($ddl$
                CREATE FOREIGN TABLE ops.%I (
                    log_time         timestamptz,
                    application_name text,
                    error_severity   text,
                    category         text,
                    message          text,
                    detail           text
                ) SERVER ops_log_files
                  OPTIONS (filename %L, format 'csv')
            $ddl$, 'app_log_' || r, 'ops_app_log_' || r || '_awaiting_repoint.csv');
        END IF;
    END LOOP;
END $$;

CREATE OR REPLACE FUNCTION ops.repoint_app_log(p_dir text)
    RETURNS text
    LANGUAGE plpgsql AS $$
DECLARE
    r text;
    n int := 0;
BEGIN
    IF p_dir IS NULL OR btrim(p_dir) = '' THEN
        RAISE EXCEPTION 'ops.repoint_app_log: a log directory is required';
    END IF;
    FOREACH r IN ARRAY ARRAY['cli','mcp','uci','migrations','api'] LOOP
        EXECUTE format('ALTER FOREIGN TABLE ops.%I OPTIONS (SET filename %L)',
                       'app_log_' || r, rtrim(p_dir, '/') || '/laplace-' || r || '.csv');
        n := n + 1;
    END LOOP;
    RETURN format('repointed %s role tables at %s', n, p_dir);
END;
$$;

CREATE OR REPLACE FUNCTION ops.app_log()
    RETURNS TABLE(
        log_time         timestamptz,
        application_name text,
        error_severity   text,
        category         text,
        message          text,
        detail           text)
    LANGUAGE plpgsql STABLE AS $$
DECLARE r text;
BEGIN
    FOREACH r IN ARRAY ARRAY['cli','mcp','uci','migrations','api'] LOOP
        BEGIN
            RETURN QUERY EXECUTE
                'SELECT log_time, application_name, error_severity, category, message, detail '
                || 'FROM ops.' || quote_ident('app_log_' || r);
        EXCEPTION WHEN OTHERS THEN
            NULL;
        END;
    END LOOP;
END;
$$;

-- ops.chess_drops exposes laplace-chess-drops.csv (LaplaceInstall.OpsLogDirectory), one row
-- per CHESS_DROPPED reason written by ChessDropLedger, through the ops_log_files server.
DO $$
BEGIN
    IF to_regclass('ops.chess_drops_ft') IS NULL THEN
        CREATE FOREIGN TABLE ops.chess_drops_ft (
            log_time     timestamptz,
            source_name  text,
            reason       text,
            dropped      bigint,
            kept         bigint,
            seen         bigint,
            drop_pct     double precision
        ) SERVER ops_log_files
          OPTIONS (filename 'ops_chess_drops_awaiting_repoint.csv', format 'csv', header 'true');
    END IF;
END $$;

CREATE OR REPLACE FUNCTION ops.repoint_chess_drops(p_dir text)
    RETURNS text
    LANGUAGE plpgsql AS $$
BEGIN
    IF p_dir IS NULL OR btrim(p_dir) = '' THEN
        RAISE EXCEPTION 'ops.repoint_chess_drops: a log directory is required';
    END IF;
    EXECUTE format(
        'ALTER FOREIGN TABLE ops.chess_drops_ft OPTIONS (SET filename %L)',
        rtrim(p_dir, '/') || '/laplace-chess-drops.csv');
    RETURN format('repointed chess_drops at %s', p_dir);
END;
$$;

CREATE OR REPLACE FUNCTION ops.chess_drops()
    RETURNS TABLE(
        log_time     timestamptz,
        source_name  text,
        reason       text,
        dropped      bigint,
        kept         bigint,
        seen         bigint,
        drop_pct     double precision)
    LANGUAGE plpgsql
    STABLE
AS $$
BEGIN
    BEGIN
        RETURN QUERY
            SELECT f.log_time, f.source_name, f.reason, f.dropped, f.kept, f.seen, f.drop_pct
            FROM ops.chess_drops_ft f;
    EXCEPTION WHEN OTHERS THEN
        NULL;
    END;
END;
$$;

-- =============================================================================
-- Schema & Table Grants
-- =============================================================================

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_app') THEN
        EXECUTE 'GRANT USAGE ON SCHEMA laplace TO laplace_app';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_readonly') THEN
        EXECUTE 'GRANT USAGE ON SCHEMA laplace TO laplace_readonly';
    END IF;
END $$;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_app') THEN
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA laplace TO laplace_app';
        EXECUTE 'GRANT USAGE ON ALL SEQUENCES IN SCHEMA laplace TO laplace_app';
        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA laplace
                 GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO laplace_app';
        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA laplace
                 GRANT USAGE ON SEQUENCES TO laplace_app';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_readonly') THEN
        EXECUTE 'GRANT SELECT ON ALL TABLES IN SCHEMA laplace TO laplace_readonly';
        EXECUTE 'GRANT USAGE ON ALL SEQUENCES IN SCHEMA laplace TO laplace_readonly';
        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA laplace
                 GRANT SELECT ON TABLES TO laplace_readonly';
        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA laplace
                 GRANT USAGE ON SEQUENCES TO laplace_readonly';
    END IF;
END $$;
