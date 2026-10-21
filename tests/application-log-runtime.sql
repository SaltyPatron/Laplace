\set ON_ERROR_STOP on
-- Run after tests/persistent-logging.ps1. log_dir and marker come from its
-- receipt, so this reads an actual HTTP event rather than a synthetic CSV row.
DO $guard$ BEGIN
    IF current_database() NOT LIKE 'laplace_log_readback_test_%' THEN
        RAISE EXCEPTION 'Select an isolated log read-back test database';
    END IF;
END $guard$;
BEGIN;
SELECT ops.repoint_app_log('api', :'log_dir');
SELECT set_config('laplace.test_log_marker', :'marker', true);
DO $readback$
DECLARE
    marker text := current_setting('laplace.test_log_marker');
    event record;
BEGIN
    SELECT * INTO STRICT event
    FROM ops.app_log('api', CURRENT_TIMESTAMP - INTERVAL '1 hour', marker, 100)
    WHERE category = 'Laplace.Browser';
    IF event.application_name IS DISTINCT FROM 'api'
       OR event.error_severity IS DISTINCT FROM 'WARNING'
       OR event.detail::jsonb #>> '{Properties,CorrelationId}' IS DISTINCT FROM marker
       OR event.detail::jsonb #>> '{Properties,FailedRequestId}' IS DISTINCT FROM marker
       OR event.detail::jsonb #>> '{Properties,FailedPath}' IS DISTINCT FROM '/example'
       OR event.detail::jsonb #>> '{Properties,EventId,Name}' IS DISTINCT FROM 'ClientFailure'
       OR event.detail::jsonb #>> '{Properties,TraceId}' IS NULL THEN
        RAISE EXCEPTION 'HTTP diagnostic lost structured evidence through PostgreSQL read-back';
    END IF;
END $readback$;
ROLLBACK;
