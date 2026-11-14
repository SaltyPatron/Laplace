\set ECHO none
BEGIN;
DO $counts$
DECLARE
    a bytea := public.blake3_send(public.laplace_text_id('test/band-counts/a'));
    b bytea := public.blake3_send(public.laplace_text_id('test/band-counts/b'));
    c bytea := public.blake3_send(public.laplace_text_id('test/band-counts/c'));
    isa bytea := laplace.relation_type_id('IS_A');
    part bytea := laplace.relation_type_id('HAS_PART');
    before_tax bigint;
    before_part bigint;
BEGIN
    SELECT consensus_rows INTO STRICT before_tax FROM converse.relation_bands() WHERE band=2;
    SELECT consensus_rows INTO STRICT before_part FROM converse.relation_bands() WHERE band=4;
    -- Stored rows include refuted and unary cells. No ANALYZE can see these
    -- uncommitted changes; MCV estimates cannot satisfy the count contract.
    INSERT INTO laplace.consensus(id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
    VALUES (public.blake3_send(public.laplace_compose(array_remove(ARRAY[a, isa, b]::public.blake3[], NULL))),a,isa,b,1500000000000,30000000000,60000000,1,now()),
           (public.blake3_send(public.laplace_compose(array_remove(ARRAY[b, isa, c]::public.blake3[], NULL))),b,isa,c,0,1,60000000,1,now()),
           (public.blake3_send(public.laplace_compose(ARRAY[c, isa]::public.blake3[])),c,isa,NULL,1500000000000,30000000000,60000000,1,now());
    IF (SELECT consensus_rows FROM converse.relation_bands() WHERE band=2) IS DISTINCT FROM before_tax+3 THEN
        RAISE EXCEPTION 'band count did not include all three inserted consensus rows';
    END IF;
    DELETE FROM laplace.consensus WHERE subject_id=c AND type_id=isa AND object_id IS NULL;
    IF (SELECT consensus_rows FROM converse.relation_bands() WHERE band=2) IS DISTINCT FROM before_tax+2 THEN
        RAISE EXCEPTION 'band count did not observe the deletion';
    END IF;
    UPDATE laplace.consensus SET id=public.blake3_send(public.laplace_compose(array_remove(ARRAY[b, part, c]::public.blake3[], NULL))),type_id=part
    WHERE subject_id=b AND type_id=isa AND object_id=c;
    IF (SELECT consensus_rows FROM converse.relation_bands() WHERE band=2) IS DISTINCT FROM before_tax+1
       OR (SELECT consensus_rows FROM converse.relation_bands() WHERE band=4) IS DISTINCT FROM before_part+1 THEN
        RAISE EXCEPTION 'band counts did not follow relation reclassification';
    END IF;
    IF EXISTS (SELECT 1 FROM converse.relation_bands() q
               JOIN ops.relation_band_census() exact ON exact.band=q.band
               WHERE q.consensus_rows IS DISTINCT FROM exact.consensus_rows) THEN
        RAISE EXCEPTION 'query band counts and operator census disagree';
    END IF;
    RAISE NOTICE 'relation bands: exact insert/delete/reclassification counts without ANALYZE';
END
$counts$;
ROLLBACK;
