BEGIN;

CREATE TEMP TABLE bulk_fold_inputs AS
SELECT n, laplace.word_id('bulk-fold/subject/' || n) subject,
       CASE WHEN n % 7 = 0 THEN NULL::bytea
            ELSE laplace.word_id('bulk-fold/object/' || n) END object
FROM generate_series(1,1024) n;

CREATE FUNCTION pg_temp.apply_bulk_fold() RETURNS bigint LANGUAGE SQL AS $$
    SELECT consensus.upsert_type(laplace.relation_type_id('IS_A'),
        array_agg(subject ORDER BY n), array_agg(object ORDER BY n),
        array_agg(30000000000::bigint), array_agg(1::bigint),
        array_agg(900000000::bigint), array_agg('2026-01-01'::timestamptz))
    FROM bulk_fold_inputs
$$;

-- An existing cell is updated before the novel COPY. A later COPY failure
-- must roll that update back along with every newly inserted cell.
SELECT consensus.upsert_type(laplace.relation_type_id('IS_A'),
    ARRAY[subject], ARRAY[object], ARRAY[30000000000::bigint],
    ARRAY[1::bigint], ARRAY[900000000::bigint], ARRAY['2026-01-01'::timestamptz])
FROM bulk_fold_inputs WHERE n = 1;

CREATE TEMP TABLE bulk_fold_before AS
SELECT c.* FROM laplace.consensus c JOIN bulk_fold_inputs f
    ON c.subject_id=f.subject AND c.id=laplace.consensus_id(f.subject,c.type_id,f.object)
WHERE c.type_id=laplace.relation_type_id('IS_A');

CREATE FUNCTION pg_temp.reject_bulk_tail() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.subject_id = laplace.word_id('bulk-fold/subject/1024') THEN
        RAISE EXCEPTION USING ERRCODE='P0001', MESSAGE='injected bulk fold failure';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER reject_bulk_tail BEFORE INSERT ON laplace.consensus
FOR EACH ROW EXECUTE FUNCTION pg_temp.reject_bulk_tail();

DO $$
BEGIN
    BEGIN
        PERFORM pg_temp.apply_bulk_fold();
        RAISE EXCEPTION 'bulk fold failed to invoke the insertion trigger';
    EXCEPTION WHEN SQLSTATE 'P0001' THEN
        IF SQLERRM <> 'injected bulk fold failure' THEN RAISE; END IF;
    END;
    IF EXISTS (
        (SELECT c.* FROM laplace.consensus c JOIN bulk_fold_inputs f
            ON c.subject_id=f.subject AND c.id=laplace.consensus_id(f.subject,c.type_id,f.object)
            WHERE c.type_id=laplace.relation_type_id('IS_A')
         EXCEPT SELECT * FROM bulk_fold_before)
        UNION ALL
        (SELECT * FROM bulk_fold_before EXCEPT
         SELECT c.* FROM laplace.consensus c JOIN bulk_fold_inputs f
            ON c.subject_id=f.subject AND c.id=laplace.consensus_id(f.subject,c.type_id,f.object)
            WHERE c.type_id=laplace.relation_type_id('IS_A'))
    ) THEN RAISE EXCEPTION 'failed COPY changed existing or novel consensus'; END IF;
END $$;
DROP TRIGGER reject_bulk_tail ON laplace.consensus;

-- A nested fold invoked by an insertion trigger must not replace the outer
-- COPY callback's input. The batch spans multiple callback buffer reads.
CREATE FUNCTION pg_temp.nested_bulk_fold() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.subject_id = laplace.word_id('bulk-fold/subject/2') THEN
        PERFORM consensus.upsert_type(laplace.relation_type_id('IS_A'),
            ARRAY[laplace.word_id('bulk-fold/nested')], ARRAY[NULL::bytea],
            ARRAY[30000000000::bigint], ARRAY[1::bigint],
            ARRAY[900000000::bigint], ARRAY['2026-01-01'::timestamptz]);
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER nested_bulk_fold BEFORE INSERT ON laplace.consensus
FOR EACH ROW EXECUTE FUNCTION pg_temp.nested_bulk_fold();

DO $$
DECLARE affected bigint;
BEGIN
    affected := pg_temp.apply_bulk_fold();
    IF affected <> 1024 THEN RAISE EXCEPTION 'retry affected % cells', affected; END IF;
    IF (SELECT count(*) FROM laplace.consensus c JOIN bulk_fold_inputs f
        ON c.subject_id=f.subject AND c.id=laplace.consensus_id(f.subject,c.type_id,f.object)
        WHERE c.type_id=laplace.relation_type_id('IS_A')) <> 1024 THEN
        RAISE EXCEPTION 'retry lost a cell';
    END IF;
    IF EXISTS (SELECT FROM laplace.consensus c JOIN bulk_fold_inputs f
        ON c.subject_id=f.subject AND c.id=laplace.consensus_id(f.subject,c.type_id,f.object)
        WHERE c.type_id=laplace.relation_type_id('IS_A')
            AND c.witness_count <> CASE WHEN f.n=1 THEN 2 ELSE 1 END) THEN
        RAISE EXCEPTION 'retry changed witness multiplicity';
    END IF;
    IF NOT EXISTS (SELECT FROM laplace.consensus
        WHERE type_id=laplace.relation_type_id('IS_A')
          AND subject_id=laplace.word_id('bulk-fold/nested') AND witness_count=1) THEN
        RAISE EXCEPTION 'nested fold missing';
    END IF;
END $$;

ROLLBACK;
