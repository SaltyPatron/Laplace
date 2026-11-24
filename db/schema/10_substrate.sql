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
    LANGUAGE sql STABLE AS $counts_fn$
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
$counts_fn$;

CREATE OR REPLACE FUNCTION ops.api(p_like text DEFAULT NULL)
    RETURNS TABLE(name text, args text, returns text, kind text)
    LANGUAGE sql STABLE PARALLEL SAFE AS $api_fn$
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
$api_fn$;

DROP FUNCTION IF EXISTS ops.source_status(text);
CREATE OR REPLACE FUNCTION ops.source_status(p_source text DEFAULT NULL)
    RETURNS TABLE(
        source text,
        source_id text,
        known boolean,
        ingested boolean,
        evidence_approx bigint,
        has_entities boolean,
        last_run_status text,
        last_run_at timestamptz,
        throughput_status text,
        throughput_compared boolean,
        throughput_rows_per_s double precision,
        throughput_baseline_rows_per_s double precision,
        throughput_slowdown_ratio double precision
    )
    LANGUAGE sql STABLE PARALLEL SAFE AS $$
    WITH w AS (
        SELECT coalesce(nullif(name, ''), encode(public.blake3_send(witness), 'hex')) AS src_name,
               encode(public.blake3_send(witness), 'hex') AS src_hex,
               (name IS NOT NULL AND name <> '') AS is_known,
               parts,
               readings
        FROM public.laplace_witnesses_named()
    )
    SELECT src_name,
           src_hex,
           is_known,
           (coalesce(readings, 0) > 0 OR coalesce(parts, 0) > 0),
           readings,
           (coalesce(parts, 0) > 0),
           'admitted'::text,
           NULL::timestamptz,
           NULL::text,
           false,
           NULL::double precision,
           NULL::double precision,
           NULL::double precision
    FROM w
    WHERE p_source IS NULL
       OR lower(src_name) = lower(p_source)
       OR lower(src_hex) = lower(p_source)
    ORDER BY readings DESC NULLS LAST, src_name;
$$;

DROP FUNCTION IF EXISTS ops.ingest_runs(integer);
CREATE OR REPLACE FUNCTION ops.ingest_runs(p_limit integer DEFAULT 25)
    RETURNS TABLE(
        run_id text,
        source_name text,
        source_id text,
        layer integer,
        status text,
        phase text,
        started_at timestamptz,
        ended_at timestamptz,
        units_attempted bigint,
        units_applied bigint,
        units_failed bigint,
        entities bigint,
        physicalities bigint,
        attestations bigint,
        attestations_measured boolean,
        file_counters_measured boolean,
        files_done bigint,
        files_total bigint,
        input_units_done bigint,
        input_units_total bigint,
        fold_drain_ms bigint,
        writer_maintenance_ms bigint,
        throughput_rows bigint,
        throughput_elapsed_ms bigint,
        throughput_rows_per_s double precision,
        throughput_baseline_rows_per_s double precision,
        throughput_slowdown_ratio double precision,
        throughput_compared boolean,
        throughput_status text,
        evidence_persisted boolean,
        error text
    )
    LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT encode(public.blake3_send(w.witness), 'hex') AS run_id,
           coalesce(nullif(w.name, ''), encode(public.blake3_send(w.witness), 'hex')) AS source_name,
           encode(public.blake3_send(w.witness), 'hex') AS source_id,
           NULL::integer AS layer,
           'completed'::text AS status,
           'admitted'::text AS phase,
           NULL::timestamptz AS started_at,
           NULL::timestamptz AS ended_at,
           NULL::bigint AS units_attempted,
           NULL::bigint AS units_applied,
           NULL::bigint AS units_failed,
           w.parts AS entities,
           w.parts AS physicalities,
           w.readings AS attestations,
           false AS attestations_measured,
           false AS file_counters_measured,
           NULL::bigint AS files_done,
           NULL::bigint AS files_total,
           NULL::bigint AS input_units_done,
           NULL::bigint AS input_units_total,
           NULL::bigint AS fold_drain_ms,
           NULL::bigint AS writer_maintenance_ms,
           NULL::bigint AS throughput_rows,
           NULL::bigint AS throughput_elapsed_ms,
           NULL::double precision AS throughput_rows_per_s,
           NULL::double precision AS throughput_baseline_rows_per_s,
           NULL::double precision AS throughput_slowdown_ratio,
           false AS throughput_compared,
           NULL::text AS throughput_status,
           (coalesce(w.readings, 0) > 0) AS evidence_persisted,
           NULL::text AS error
    FROM public.laplace_witnesses_named() w
    ORDER BY w.readings DESC NULLS LAST, w.name
    LIMIT GREATEST(COALESCE(p_limit, 25), 1);
$$;

DROP FUNCTION IF EXISTS ops.ingest_files(text, integer);
CREATE OR REPLACE FUNCTION ops.ingest_files(p_run_id text, p_limit integer DEFAULT 200)
    RETURNS TABLE(
        run_id text,
        file_label text,
        source_name text,
        file_id text,
        status text,
        started_at timestamptz,
        ended_at timestamptz,
        bytes bigint,
        records bigint,
        entities bigint,
        physicalities bigint,
        attestations bigint,
        error text
    )
    LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT encode(public.blake3_send(w.witness), 'hex') AS run_id,
           coalesce(nullif(w.name, ''), encode(public.blake3_send(w.witness), 'hex')) AS file_label,
           coalesce(nullif(w.name, ''), encode(public.blake3_send(w.witness), 'hex')) AS source_name,
           encode(public.blake3_send(w.record_id), 'hex') AS file_id,
           'admitted'::text AS status,
           NULL::timestamptz AS started_at,
           NULL::timestamptz AS ended_at,
           NULL::bigint AS bytes,
           w.readings AS records,
           w.parts AS entities,
           w.parts AS physicalities,
           w.readings AS attestations,
           NULL::text AS error
    FROM public.laplace_witnesses_named() w
    WHERE p_run_id IS NOT NULL
      AND (encode(public.blake3_send(w.witness), 'hex') = lower(p_run_id)
           OR lower(coalesce(w.name, '')) = lower(p_run_id))
    LIMIT GREATEST(COALESCE(p_limit, 200), 1);
$$;

DROP FUNCTION IF EXISTS ops.ingest_run_close(text, text);
CREATE OR REPLACE FUNCTION ops.ingest_run_close(p_run_id text, p_status text DEFAULT 'cancelled')
    RETURNS TABLE(run_id text, status text, closed boolean, detail text)
    LANGUAGE sql VOLATILE AS $$
    SELECT p_run_id,
           coalesce(nullif(btrim(p_status), ''), 'cancelled'),
           false,
           'Witness testimony resides directly in the 4-table substrate; manage active processes via Ingest control.'::text;
$$;

DROP FUNCTION IF EXISTS ops.evict_source(text);
CREATE OR REPLACE FUNCTION ops.evict_source(p_source text)
    RETURNS TABLE(
        source text,
        witness_id text,
        witnesses_removed bigint,
        entities_removed bigint,
        standings_recomputed bigint,
        orphaned_claims bigint,
        swept_orphans bigint
    )
    LANGUAGE plpgsql VOLATILE AS $evict$
DECLARE
    v_witness public.blake3;
    v_removed bigint := 0;
    v_entities bigint := 0;
    v_standings bigint := 0;
    v_orphans bigint := 0;
    v_swept bigint := 0;
BEGIN
    IF p_source IS NULL OR btrim(p_source) = '' THEN
        RAISE EXCEPTION 'ops.evict_source: p_source is required' USING ERRCODE = '22023';
    END IF;

    IF p_source ~ '^[0-9a-fA-F]{32}$' THEN
        v_witness := public.blake3_from_bytea(decode(p_source, 'hex'));
    ELSE
        SELECT w.witness INTO v_witness
        FROM public.laplace_witnesses_named() w
        WHERE lower(coalesce(w.name, '')) = lower(p_source)
        LIMIT 1;
        IF v_witness IS NULL THEN
            v_witness := public.laplace_text_id(p_source);
        END IF;
    END IF;

    SELECT f.witnesses_removed, f.entities_removed, f.standings_recomputed, f.orphaned_claims
    INTO v_removed, v_entities, v_standings, v_orphans
    FROM public.laplace_forget_witnesses(ARRAY[v_witness]) f;

    SELECT public.laplace_sweep_orphans(NULL) INTO v_swept;

    RETURN QUERY SELECT p_source, encode(public.blake3_send(v_witness), 'hex'),
                        v_removed, v_entities, v_standings, v_orphans, v_swept;
END $evict$;

DROP FUNCTION IF EXISTS ops.activity(double precision, boolean);
CREATE OR REPLACE FUNCTION ops.activity(p_min_seconds double precision DEFAULT 0, p_include_idle boolean DEFAULT true)
    RETURNS TABLE(
        pid integer,
        is_self boolean,
        restricted boolean,
        usename text,
        application_name text,
        client_addr text,
        backend_type text,
        state text,
        wait_event_type text,
        wait_event text,
        query_seconds double precision,
        xact_seconds double precision,
        backend_seconds double precision,
        query text
    )
    LANGUAGE sql STABLE AS $$
    SELECT a.pid,
           (a.pid = pg_backend_pid()) AS is_self,
           (a.state IS NULL AND a.pid <> pg_backend_pid()) AS restricted,
           a.usename::text,
           a.application_name,
           a.client_addr::text,
           a.backend_type,
           a.state,
           a.wait_event_type,
           a.wait_event,
           EXTRACT(EPOCH FROM (clock_timestamp() - a.query_start))::double precision AS query_seconds,
           EXTRACT(EPOCH FROM (clock_timestamp() - a.xact_start))::double precision AS xact_seconds,
           EXTRACT(EPOCH FROM (clock_timestamp() - a.backend_start))::double precision AS backend_seconds,
           left(a.query, 4000) AS query
    FROM pg_stat_activity a
    WHERE a.datname = current_database()
      AND (COALESCE(p_include_idle, true) OR a.state IS DISTINCT FROM 'idle')
      AND COALESCE(EXTRACT(EPOCH FROM (clock_timestamp() - COALESCE(a.query_start, a.xact_start, a.backend_start))), 0)
          >= COALESCE(p_min_seconds, 0)
    ORDER BY (a.state = 'active') DESC NULLS LAST, query_seconds DESC NULLS LAST, a.pid;
$$;

DROP FUNCTION IF EXISTS ops.cancel_backend(integer);
CREATE OR REPLACE FUNCTION ops.cancel_backend(p_pid integer)
    RETURNS TABLE(pid integer, signalled boolean, was_state text, was_query text, was_seconds double precision)
    LANGUAGE plpgsql VOLATILE AS $cancel_fn$
DECLARE
    v_state text;
    v_query text;
    v_secs double precision;
    v_sig boolean := false;
BEGIN
    SELECT a.state, left(a.query, 1000), EXTRACT(EPOCH FROM (clock_timestamp() - a.query_start))::double precision
    INTO v_state, v_query, v_secs
    FROM pg_stat_activity a
    WHERE a.pid = p_pid AND a.datname = current_database();

    IF FOUND AND p_pid IS DISTINCT FROM pg_backend_pid() THEN
        v_sig := pg_cancel_backend(p_pid);
    END IF;

    RETURN QUERY SELECT p_pid, v_sig, v_state, v_query, v_secs;
END $cancel_fn$;

DROP FUNCTION IF EXISTS ops.terminate_backend(integer);
CREATE OR REPLACE FUNCTION ops.terminate_backend(p_pid integer)
    RETURNS TABLE(pid integer, signalled boolean, was_state text, was_query text, was_seconds double precision)
    LANGUAGE plpgsql VOLATILE AS $term_fn$
DECLARE
    v_state text;
    v_query text;
    v_secs double precision;
    v_sig boolean := false;
BEGIN
    SELECT a.state, left(a.query, 1000), EXTRACT(EPOCH FROM (clock_timestamp() - a.query_start))::double precision
    INTO v_state, v_query, v_secs
    FROM pg_stat_activity a
    WHERE a.pid = p_pid AND a.datname = current_database();

    IF FOUND AND p_pid IS DISTINCT FROM pg_backend_pid() THEN
        v_sig := pg_terminate_backend(p_pid);
    END IF;

    RETURN QUERY SELECT p_pid, v_sig, v_state, v_query, v_secs;
END $term_fn$;

DROP FUNCTION IF EXISTS ops.index_health();
CREATE OR REPLACE FUNCTION ops.index_health()
    RETURNS TABLE(
        index_name text,
        table_name text,
        schema_name text,
        valid boolean,
        is_partitioned_parent boolean,
        leaf_count bigint
    )
    LANGUAGE sql STABLE AS $$
    SELECT ic.relname::text AS index_name,
           tc.relname::text AS table_name,
           n.nspname::text AS schema_name,
           (i.indisvalid AND i.indisready) AS valid,
           (ic.relkind = 'I') AS is_partitioned_parent,
           CASE WHEN ic.relkind = 'I'
                THEN (SELECT count(*)::bigint FROM pg_inherits inh WHERE inh.inhparent = ic.oid)
                ELSE 1::bigint END AS leaf_count
    FROM pg_catalog.pg_index i
    JOIN pg_catalog.pg_class ic ON ic.oid = i.indexrelid
    JOIN pg_catalog.pg_class tc ON tc.oid = i.indrelid
    JOIN pg_catalog.pg_namespace n ON n.oid = tc.relnamespace
    WHERE n.nspname IN ('public', 'app')
      AND (NOT i.indisvalid
           OR NOT i.indisready
           OR (ic.relkind = 'I' AND NOT EXISTS (SELECT 1 FROM pg_inherits inh WHERE inh.inhparent = ic.oid)))
    ORDER BY n.nspname, tc.relname, ic.relname;
$$;

DROP FUNCTION IF EXISTS ops.reindex_invalid(boolean);
CREATE OR REPLACE FUNCTION ops.reindex_invalid(p_dry_run boolean DEFAULT true)
    RETURNS TABLE(schema_name text, index_name text, valid boolean, action text)
    LANGUAGE plpgsql VOLATILE AS $reidx$
DECLARE
    rec record;
BEGIN
    FOR rec IN
        SELECT n.nspname::text AS sch, ic.relname::text AS idx, (i.indisvalid AND i.indisready) AS is_valid
        FROM pg_catalog.pg_index i
        JOIN pg_catalog.pg_class ic ON ic.oid = i.indexrelid
        JOIN pg_catalog.pg_class tc ON tc.oid = i.indrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = tc.relnamespace
        WHERE n.nspname IN ('public', 'app')
          AND (NOT i.indisvalid OR NOT i.indisready)
          AND ic.relkind = 'i'
        ORDER BY n.nspname, ic.relname
    LOOP
        IF COALESCE(p_dry_run, true) THEN
            RETURN QUERY SELECT rec.sch, rec.idx, rec.is_valid, 'would_reindex'::text;
        ELSE
            EXECUTE format('REINDEX INDEX %I.%I', rec.sch, rec.idx);
            RETURN QUERY SELECT rec.sch, rec.idx, true, 'reindexed'::text;
        END IF;
    END LOOP;
END $reidx$;

DROP PROCEDURE IF EXISTS ops.analyze_substrate();
CREATE OR REPLACE PROCEDURE ops.analyze_substrate()
    LANGUAGE plpgsql AS $analyze_proc$
BEGIN
    ANALYZE public.entity;
    ANALYZE public.physicality;
    ANALYZE public.attestation;
    ANALYZE public.consensus;
END $analyze_proc$;
