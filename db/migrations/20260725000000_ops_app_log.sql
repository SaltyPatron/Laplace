-- ops.app_log — the .NET console apps' diagnostics, queryable in SQL (GH #602 follow-up,
-- closes the loop opened by GH #601). The shared logging foundation writes one RFC 4180 CSV
-- per role under LaplaceInstall.OpsLogDirectory — laplace-{role}.csv — with the column subset
-- ops.pg_log shares. This exposes those files through file_fdw, reusing the ops_log_files
-- server that the ops_logs migration already created. No third-party log service; no log rows
-- in substrate tables.
--
-- Per-role files, not one shared file, so `tail -f laplace-uci.csv` still works — which means
-- a role that has never run on this host has no file, and file_fdw errors on a missing file.
-- ops.app_log() (a function, not a view) wraps each role's read in its own handler and skips
-- the absent ones, so the unified query never fails just because, say, no UCI engine has run.
--
-- The rotating-vs-stable question ops.pg_log has does not arise: the sink uses a STABLE
-- filename per role (size-rolled to timestamped archives), so once repointed the tables never
-- need repointing again. DbUp wraps each script in one transaction.

CREATE SCHEMA IF NOT EXISTS ops;
CREATE EXTENSION IF NOT EXISTS file_fdw;
CREATE SERVER IF NOT EXISTS ops_log_files FOREIGN DATA WRAPPER file_fdw;

-- One foreign table per known role. Placeholder filenames are never read (ops.repoint_app_log
-- sets the live paths); the 6-column shape is OpsLogCsvFormatter.Columns verbatim.
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

-- Aim every per-role table at <p_dir>/laplace-{role}.csv. p_dir is the deploy's shared
-- LaplaceInstall.OpsLogDirectory (set LAPLACE_OPS_LOG_DIR to one path for all apps so a single
-- directory holds every role's file). Unlike ops.pg_log, Postgres cannot discover this path
-- itself, so a deploy step or the always-on API calls this once after publish:
--   SELECT ops.repoint_app_log('/opt/laplace/app/logs');
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

-- The unified read. A role that has never logged on this host has no CSV file, so its foreign
-- table errors — caught per role and skipped, so `SELECT * FROM ops.app_log()` returns whatever
-- is actually there instead of failing wholesale. application_name distinguishes the roles.
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
            -- file absent (role never ran here) or transiently unreadable — skip this role.
            NULL;
        END;
    END LOOP;
END;
$$;
