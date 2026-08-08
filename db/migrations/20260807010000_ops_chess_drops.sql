-- ops.chess_drops — CHESS_DROPPED tallies persisted as CSV (GH #813 drop ledger).
-- ChessDropLedger still prints to stdout; it also appends one row per reason to
-- laplace-chess-drops.csv under LaplaceInstall.OpsLogDirectory. Queryable via
-- ops.chess_drops() / op(name => 'chess_drops'). Same file_fdw server as app_log.

CREATE SCHEMA IF NOT EXISTS ops;
CREATE EXTENSION IF NOT EXISTS file_fdw;
CREATE SERVER IF NOT EXISTS ops_log_files FOREIGN DATA WRAPPER file_fdw;

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
