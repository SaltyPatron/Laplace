\set ON_ERROR_STOP on
-- Run only in an isolated test database. Supply api_csv, cli_csv and log_dir
-- psql variables naming a new writable test directory on that server.
DO $guard$ BEGIN
    IF current_database() NOT LIKE 'laplace_log_readback_test_%' THEN
        RAISE EXCEPTION 'Select an isolated log read-back test database';
    END IF;
END $guard$;
BEGIN;
COPY (
    SELECT CURRENT_TIMESTAMP - INTERVAL '3 days','cli','Information','OldCli',
           'old CLI event ' || n,'{}' FROM generate_series(1,1000) n
) TO :'cli_csv' WITH (FORMAT csv);
COPY (
    SELECT CURRENT_TIMESTAMP - INTERVAL '3 days','api','Information','Api', 'old API event','{}'
    UNION ALL SELECT CURRENT_TIMESTAMP - INTERVAL '2 minutes','api','Information','Api', 'recent API event','{}'
    UNION ALL SELECT CURRENT_TIMESTAMP - INTERVAL '1 minute','api','Warning','Api', 'acceptance needle','{"CorrelationId":"needle"}'
) TO :'api_csv' WITH (FORMAT csv);
SELECT ops.repoint_app_log('cli', :'log_dir');
SELECT ops.repoint_app_log('api', :'log_dir');
DO $accept$
BEGIN
    IF (SELECT count(*) FROM ops.app_log('api')) <> 3 THEN
        RAISE EXCEPTION 'Another source displaced selected API events';
    END IF;
    IF (SELECT count(*) FROM ops.app_log('api',CURRENT_TIMESTAMP - INTERVAL '10 minutes')) <> 2 THEN
        RAISE EXCEPTION 'Recent source filtering is incorrect';
    END IF;
    IF EXISTS(SELECT FROM ops.app_log('api',CURRENT_TIMESTAMP,NULL,1)) THEN
        RAISE EXCEPTION 'Events older than the cutoff passed the freshness check';
    END IF;
    IF (SELECT message FROM ops.app_log('api',NULL,NULL,1)) <> 'acceptance needle' THEN
        RAISE EXCEPTION 'The source limit was applied before ordering newest first';
    END IF;
    IF NOT EXISTS(SELECT FROM ops.app_log('api',NULL,'needle',1)) THEN
        RAISE EXCEPTION 'Correlation/message search lost the selected event';
    END IF;
    IF EXISTS(SELECT FROM ops.app_log('api',NULL,'%',1)) OR EXISTS(SELECT FROM ops.app_log('api',NULL,NULL,0)) THEN
        RAISE EXCEPTION 'Literal search or zero limit was not respected';
    END IF;
    BEGIN
        PERFORM * FROM ops.app_log('api;DROP TABLE anything');
        RAISE EXCEPTION 'Unknown role accepted';
    EXCEPTION WHEN invalid_parameter_value THEN NULL;
    END;
    BEGIN
        PERFORM * FROM ops.app_log('api',NULL,NULL,-1);
        RAISE EXCEPTION 'Invalid limit accepted';
    EXCEPTION WHEN invalid_parameter_value THEN NULL;
    END;
END $accept$;
SELECT ops.repoint_app_log('api', :'log_dir' || '/missing');
DO $missing$
BEGIN
    IF NOT EXISTS(SELECT FROM ops.app_log()) THEN
        RAISE EXCEPTION 'Fixture requires readable older CLI events';
    END IF;
    BEGIN
        PERFORM * FROM ops.app_log('api',CURRENT_TIMESTAMP - INTERVAL '10 minutes',NULL,1);
        RAISE EXCEPTION 'Another role masked an unavailable API source';
    EXCEPTION WHEN undefined_file THEN NULL;
    END;
    RAISE NOTICE 'PASS source selection, recent/newest reads, literal search, limits and missing-source failure';
END $missing$;
ROLLBACK;
