-- Display identity and display text are different contracts. A graph node keeps its
-- exact content id, but the label must be Unicode/name/description — never that id
-- rendered back as arbitrary hex. High-tier text preview is containment-owned (#804).
\set ECHO none
BEGIN;
DO $display_label$
DECLARE
    src      bytea := public.laplace_hash128_blake3('test/display/source');
    type_cp  bytea := public.laplace_hash128_blake3('test/display/type/codepoint');
    type_s   bytea := public.laplace_hash128_blake3('test/display/type/sentence');
    type_d   bytea := public.laplace_hash128_blake3('test/display/type/document');
    type_b   bytea := public.laplace_hash128_blake3('test/display/type/book');
    type_c   bytea := public.laplace_hash128_blake3('test/display/type/concept');
    wolf     bytea := laplace.word_id('狼');
    stop     bytea := laplace.word_id('。');
    ending   bytea := laplace.word_id('終');
    sent1    bytea := public.laplace_hash128_blake3('test/display/sentence/1');
    sent2    bytea := public.laplace_hash128_blake3('test/display/sentence/2');
    doc      bytea := public.laplace_hash128_blake3('test/display/document');
    book     bytea := public.laplace_hash128_blake3('test/display/book');
    concept  bytea := public.laplace_hash128_blake3('test/display/concept');
    concept2 bytea := public.laplace_hash128_blake3('test/display/concept/nested');
    opaque   bytea := public.laplace_hash128_blake3('test/display/opaque');
    missing  bytea := public.laplace_hash128_blake3('test/display/missing');
    defrel   bytea := laplace.relation_type_id('HAS_DEFINITION');
    labels   text[];
    ids      bytea[];
BEGIN
    INSERT INTO laplace.entities (id, tier, type_id, first_observed_by) VALUES
        (wolf, 0, type_cp, src),
        (stop, 0, type_cp, src),
        (ending, 0, type_cp, src),
        (sent1, 3, type_s, src),
        (sent2, 3, type_s, src),
        (doc, 4, type_d, src),
        (book, 5, type_b, src),
        (concept, 2, type_c, src),
        (concept2, 2, type_c, src),
        (opaque, 2, type_c, src)
    ON CONFLICT DO NOTHING;

    INSERT INTO laplace.physicalities
        (id, entity_id, type, coord, hilbert_index, trajectory, n_constituents, observed_at)
    VALUES
        (public.laplace_hash128_blake3('test/display/physicality/sentence/1'), sent1, 1,
         public.ST_SetSRID(public.ST_MakePoint(0.1,0.1,0.1,0.1),0), decode(repeat('31',16),'hex'),
         public.ST_MakeLine(ARRAY[
             public.laplace_mantissa_pack(wolf,1,1,0),
             public.laplace_mantissa_pack(stop,2,1,0)]), 2, now()),
        (public.laplace_hash128_blake3('test/display/physicality/sentence/2'), sent2, 1,
         public.ST_SetSRID(public.ST_MakePoint(0.2,0.2,0.2,0.2),0), decode(repeat('32',16),'hex'),
         public.ST_MakeLine(ARRAY[
             public.laplace_mantissa_pack(ending,1,1,0),
             public.laplace_mantissa_pack(stop,2,1,0)]), 2, now()),
        (public.laplace_hash128_blake3('test/display/physicality/document'), doc, 1,
         public.ST_SetSRID(public.ST_MakePoint(0.3,0.3,0.3,0.3),0), decode(repeat('33',16),'hex'),
         public.ST_MakeLine(ARRAY[
             -- vertex flags carry the contextual constituent tier (tier << 1).
             public.laplace_mantissa_pack(sent1,1,1,6),
             public.laplace_mantissa_pack(sent2,2,1,6)]), 2, now()),
        (public.laplace_hash128_blake3('test/display/physicality/book'), book, 1,
         public.ST_SetSRID(public.ST_MakePoint(0.4,0.4,0.4,0.4),0), decode(repeat('34',16),'hex'),
         public.ST_MakeLine(ARRAY[
             public.laplace_mantissa_pack(doc,1,1,8)]), 1, now());

    INSERT INTO laplace.attestations
        (id, subject_id, type_id, object_id, source_id, context_id, outcome,
         last_observed_at, observation_count, sum_score_fp1e9, opponent_rd_fp1e9)
    VALUES
        (public.laplace_hash128_blake3('test/display/definition-edge'),
         concept, defrel, doc, src, NULL, 2, now(), 1, 1000000000, 30000000000),
        (public.laplace_hash128_blake3('test/display/definition-edge/nested'),
         concept2, defrel, book, src, NULL, 2, now(), 1, 1000000000, 30000000000);

    SELECT array_agg(d.id ORDER BY u.ord), array_agg(d.label ORDER BY u.ord)
      INTO ids, labels
    FROM unnest(ARRAY[concept, concept2, doc, sent1, wolf, opaque, missing])
         WITH ORDINALITY u(id, ord)
    JOIN LATERAL realize.display_label_batch(ARRAY[u.id]) d ON true;

    IF ids IS DISTINCT FROM ARRAY[concept, concept2, doc, sent1, wolf, opaque, missing] THEN
        RAISE EXCEPTION 'FAIL: display label changed/reordered entity identity';
    END IF;
    IF labels[1] IS DISTINCT FROM '狼。' THEN
        RAISE EXCEPTION 'FAIL: concept did not use its definition preview: %', labels[1];
    END IF;
    IF labels[2] IS DISTINCT FROM '狼。' THEN
        RAISE EXCEPTION 'FAIL: nested high-tier definition did not descend one bounded spine: %', labels[2];
    END IF;
    IF labels[3] IS DISTINCT FROM '狼。' THEN
        RAISE EXCEPTION 'FAIL: definition document did not use containment-proven first-sentence preview: %', labels[3];
    END IF;
    IF labels[4] IS DISTINCT FROM '狼。' THEN
        RAISE EXCEPTION 'FAIL: sentence content did not render Unicode exactly: %', labels[4];
    END IF;
    IF labels[5] IS DISTINCT FROM '狼' THEN
        RAISE EXCEPTION 'FAIL: tier-0 codepoint did not render Unicode exactly: %', labels[5];
    END IF;
    IF labels[6] ~ '^[0-9A-Fa-f]{32}(…|\.\.\.)?$' THEN
        RAISE EXCEPTION 'FAIL: unresolved opaque entity leaked identity as label: %', labels[6];
    END IF;
    IF labels[6] IS NULL OR btrim(labels[6]) = '' THEN
        RAISE EXCEPTION 'FAIL: unresolved opaque entity has no friendly abstention/description';
    END IF;
    IF labels[7] IS DISTINCT FROM 'Unrealized entity' THEN
        RAISE EXCEPTION 'FAIL: absent identity did not abstain cleanly: %', labels[7];
    END IF;

    -- Final-result projection is aligned, including duplicates. A graph batch can contain
    -- the same id at multiple positions; display must not sort/dedup labels out of alignment.
    SELECT array_agg(d.label ORDER BY u.ord)
      INTO labels
    FROM unnest(ARRAY[concept2, opaque, concept2]) WITH ORDINALITY u(id, ord)
    JOIN LATERAL realize.display_label_batch(ARRAY[u.id]) d ON true;
    IF cardinality(labels) <> 3 OR labels[1] IS DISTINCT FROM labels[3] THEN
        RAISE EXCEPTION 'FAIL: display label batch lost duplicate positional alignment';
    END IF;

    RAISE NOTICE 'display labels: id separate, Unicode exact, bounded containment preview, no hash fallback';
END
$display_label$;
ROLLBACK;
\set ECHO all
