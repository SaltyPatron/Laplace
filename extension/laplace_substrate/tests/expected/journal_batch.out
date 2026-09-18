BEGIN;
CREATE TEMP TABLE journal_touches (run_id uuid);
CREATE FUNCTION pg_temp.count_journal_touches() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO journal_touches VALUES (NEW.run_id);
    RETURN NULL;
END $$;
CREATE TRIGGER test_journal_touches AFTER UPDATE ON laplace.ingest_run_journal
FOR EACH ROW EXECUTE FUNCTION pg_temp.count_journal_touches();
DO $$
DECLARE
    runs uuid[] := ARRAY['11111111-aaaa-bbbb-cccc-111111111111'::uuid,
                        '22222222-aaaa-bbbb-cccc-222222222222'::uuid];
BEGIN
    INSERT INTO laplace.ingest_run_journal(run_id,source_name,layer,status)
    SELECT r,'test/journal-batch',0,'running' FROM unnest(runs) r;
    INSERT INTO laplace.ingest_file_journal(run_id,file_label,source_name)
    SELECT runs[1+i%2],i::text,'test/journal-batch' FROM generate_series(1,1000) i;
    IF (SELECT count(*) FROM journal_touches) <> 2 THEN
        RAISE EXCEPTION 'file INSERT must touch each run once';
    END IF;
    TRUNCATE journal_touches;
    UPDATE laplace.ingest_file_journal SET status='composed' WHERE run_id=ANY(runs);
    IF (SELECT count(*) FROM journal_touches) <> 2 THEN
        RAISE EXCEPTION 'file UPDATE must touch each run once';
    END IF;
    TRUNCATE journal_touches;
    UPDATE laplace.ingest_file_journal SET status='ok' WHERE false;
    IF EXISTS (SELECT FROM journal_touches) THEN
        RAISE EXCEPTION 'empty file update must not touch runs';
    END IF;
END $$;
ROLLBACK;
