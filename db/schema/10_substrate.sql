-- DECLARED SCHEMA, substrate database: extensions, the ops log readers.
-- Applied in full, in file-name order, on every create and every deploy
-- (Laplace.Migrations substrate-up / up). There is no journal: every statement here is
-- idempotent, and re-applying reconciles drift. Grants are declared in
-- 20_substrate_grants.sql.

CREATE EXTENSION IF NOT EXISTS postgis;

-- The Engine and managed entry points use the same Native substrate contract.
-- This extension owns the fixed-width blake3 type and canonical public tables.
CREATE EXTENSION IF NOT EXISTS laplace WITH SCHEMA public;

-- Per-statement execution and planning time. The library is loaded through
-- shared_preload_libraries (CpuTopologyCommands.EmitPgTuning) next to laplace,
-- whose perfcache blobs are mapped at preload; loading it requires a postmaster restart.
-- The extension can be created before that restart; the view then returns no rows.
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;

-- =============================================================================
-- Ops CSV Logs, Substrate Counts & API Catalog
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
    FOREACH r IN ARRAY ARRAY['cli','mcp','uci','migrations','api','lichess'] LOOP
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

CREATE OR REPLACE FUNCTION ops.repoint_app_log(p_role text, p_dir text)
    RETURNS text LANGUAGE plpgsql AS $repoint_role$
BEGIN
    IF p_role IS NULL OR p_role NOT IN ('cli','mcp','uci','migrations','api','lichess') THEN
        RAISE EXCEPTION 'Unknown application log role: %', p_role;
    END IF;
    IF p_dir IS NULL OR btrim(p_dir) = '' THEN
        RAISE EXCEPTION 'ops.repoint_app_log: a log directory is required';
    END IF;
    EXECUTE format('ALTER FOREIGN TABLE ops.%I OPTIONS (SET filename %L)',
        'app_log_' || p_role, rtrim(replace(p_dir, chr(92), '/'), '/') || '/laplace-' || p_role || '.csv');
    RETURN format('repointed %s log at %s', p_role, p_dir);
END $repoint_role$;

CREATE OR REPLACE FUNCTION ops.repoint_app_log(p_dir text)
    RETURNS text LANGUAGE plpgsql AS $repoint$
DECLARE r text; n int := 0;
BEGIN
    IF p_dir IS NULL OR btrim(p_dir) = '' THEN
        RAISE EXCEPTION 'ops.repoint_app_log: a log directory is required';
    END IF;
    FOREACH r IN ARRAY ARRAY['cli','mcp','uci','migrations','api','lichess'] LOOP
        PERFORM ops.repoint_app_log(r, p_dir);
        n := n + 1;
    END LOOP;
    RETURN format('repointed %s role tables at %s', n, p_dir);
END $repoint$;

CREATE OR REPLACE FUNCTION ops.app_log(p_role text, p_since timestamptz DEFAULT NULL,
    p_contains text DEFAULT NULL, p_limit integer DEFAULT 200)
    RETURNS TABLE(log_time timestamptz, application_name text, error_severity text,
                  category text, message text, detail text)
    LANGUAGE plpgsql STABLE AS $source_logs$
BEGIN
    IF p_role IS NULL OR p_role NOT IN ('cli','mcp','uci','migrations','api','lichess') THEN
        RAISE EXCEPTION 'Unknown application log role: %', p_role USING ERRCODE='22023';
    END IF;
    IF p_limit IS NULL OR p_limit < 0 THEN
        RAISE EXCEPTION 'Application log limit must be zero or positive' USING ERRCODE='22023';
    END IF;
    RETURN QUERY EXECUTE format(
        'SELECT log_time,application_name,error_severity,category,message,detail FROM ops.%I
         WHERE ($1 IS NULL OR log_time >= $1)
           AND ($2 IS NULL OR strpos(message,$2) > 0 OR strpos(detail,$2) > 0)
         ORDER BY log_time DESC LIMIT $3', 'app_log_' || p_role)
        USING p_since, p_contains, p_limit;
END $source_logs$;

CREATE OR REPLACE FUNCTION ops.app_log()
    RETURNS TABLE(log_time timestamptz, application_name text, error_severity text,
                  category text, message text, detail text)
    LANGUAGE plpgsql STABLE AS $logs$
DECLARE r text; available integer := 0;
BEGIN
    FOREACH r IN ARRAY ARRAY['cli','mcp','uci','migrations','api','lichess'] LOOP
        BEGIN
            RETURN QUERY EXECUTE 'SELECT log_time,application_name,error_severity,category,message,detail FROM ops.'
                || quote_ident('app_log_' || r);
            available := available + 1;
        EXCEPTION WHEN undefined_file OR undefined_table THEN
            RAISE WARNING 'Application log for role % is unavailable [%]: %', r, SQLSTATE, SQLERRM;
        END;
    END LOOP;
    IF available = 0 THEN
        RAISE EXCEPTION USING ERRCODE='55000',
            MESSAGE='No application log source is readable. Configure its log directory and PostgreSQL file access.';
    END IF;
END $logs$;

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
        rtrim(p_dir, E'/\\') || '/laplace-chess-drops.csv');
    RETURN format('repointed chess_drops at %s', p_dir);
END;
$$;

CREATE OR REPLACE FUNCTION ops.chess_drops()
    RETURNS TABLE(log_time timestamptz, source_name text, reason text, dropped bigint,
                  kept bigint, seen bigint, drop_pct double precision)
    LANGUAGE plpgsql STABLE AS $drops$
BEGIN
    RETURN QUERY SELECT f.log_time, f.source_name, f.reason, f.dropped, f.kept, f.seen, f.drop_pct
        FROM ops.chess_drops_ft f;
EXCEPTION WHEN undefined_file OR undefined_table THEN
    RAISE WARNING 'Chess drop log is unavailable [%]: %', SQLSTATE, SQLERRM;
END $drops$;

CREATE OR REPLACE FUNCTION ops.attestation_count()
RETURNS bigint LANGUAGE plpgsql STABLE AS $$
DECLARE measured bigint;
BEGIN
    IF to_regclass('public.attestation') IS NULL THEN
        RETURN NULL;
    END IF;
    EXECUTE 'SELECT public.laplace_attestation_count()' INTO measured;
    RETURN measured;
END $$;

CREATE OR REPLACE FUNCTION ops.substrate_counts()
    RETURNS TABLE(metric text, value bigint)
    LANGUAGE sql STABLE
BEGIN ATOMIC
    WITH targets(metric, relid, ordinal) AS (
        SELECT 'entities(ESTIMATE)'::text, to_regclass('public.entity'), 1
        UNION ALL
        SELECT 'physicalities(ESTIMATE)', to_regclass('public.physicality'), 2
        UNION ALL
        SELECT 'consensus(ESTIMATE)', to_regclass('public.consensus'), 4
    ), estimates AS (
        SELECT t.metric, COALESCE(sum(CASE
            WHEN s.n_live_tup > 0 OR s.n_tup_ins > 0 OR s.n_tup_upd > 0 OR s.n_tup_del > 0
              OR GREATEST(s.last_analyze, s.last_autoanalyze, s.last_vacuum, s.last_autovacuum) IS NOT NULL
            THEN s.n_live_tup
            ELSE GREATEST(c.reltuples, 0)::bigint
        END), 0)::bigint AS value, t.ordinal
        FROM targets t
        LEFT JOIN LATERAL pg_partition_tree(t.relid) p ON true
        JOIN pg_class c ON c.oid = COALESCE(p.relid, t.relid)
        LEFT JOIN pg_stat_all_tables s ON s.relid = c.oid
        WHERE p.isleaf OR p.relid IS NULL
        GROUP BY t.metric, t.ordinal
    ), totals AS (
        SELECT * FROM estimates
        UNION ALL SELECT 'attestations(MEASURED)', ops.attestation_count(), 3
    )
    SELECT metric, value FROM totals ORDER BY ordinal;
END;

CREATE OR REPLACE FUNCTION ops.api(p_like text DEFAULT NULL)
    RETURNS TABLE(name text, args text, returns text, kind text)
    LANGUAGE sql STABLE PARALLEL SAFE
BEGIN ATOMIC
    SELECT CASE WHEN n.nspname = 'public' THEN p.proname::text
                ELSE n.nspname || '.' || p.proname END AS name,
           pg_get_function_arguments(p.oid) AS args,
           pg_get_function_result(p.oid) AS returns,
           CASE p.prokind WHEN 'p' THEN 'procedure'
                          ELSE 'function' END AS kind
    FROM pg_proc p
    JOIN pg_namespace n ON n.oid = p.pronamespace
    WHERE p.prokind IN ('f', 'p')
      AND p.prorettype NOT IN (
          'internal'::regtype,
          'trigger'::regtype,
          'event_trigger'::regtype,
          'fdw_handler'::regtype,
          'index_am_handler'::regtype,
          'table_am_handler'::regtype,
          'tsm_handler'::regtype,
          'language_handler'::regtype
      )
      AND NOT ('internal'::regtype = ANY (p.proargtypes))
      AND p.proname NOT LIKE '\_%' ESCAPE '\'
      AND (
            n.nspname = 'ops'
            OR (n.nspname = 'public' AND (p.proname LIKE 'laplace\_%' ESCAPE '\' OR p.proname LIKE 'blake3%' ESCAPE '\'))
          )
      AND (p_like IS NULL
           OR p.proname ILIKE '%'||p_like||'%'
           OR (n.nspname || '.' || p.proname) ILIKE '%'||p_like||'%')
    ORDER BY 1, 2;
END;
