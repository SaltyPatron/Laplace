\set ECHO none
BEGIN;
DO $membership_readers$
DECLARE
    word_type bytea := realize.canonical_id('Word');
    sentence_type bytea := realize.canonical_id('Sentence');
    codepoint_type bytea := realize.canonical_id('Codepoint');
    language_type bytea := realize.canonical_id('Language');
    relation_type bytea := laplace.entity_type_id('RelationType');
    low_type bytea := decode(repeat('00',16),'hex');
    source_id bytea := public.laplace_hash128_blake3('test/interpretation-readers/source');
    topic_id bytea := public.laplace_hash128_blake3('test/interpretation-readers/topic');
    sentence_id bytea := public.laplace_hash128_blake3('test/interpretation-readers/sentence');
    lang_a bytea := public.laplace_hash128_blake3('test/interpretation-readers/language-a');
    lang_b bytea := public.laplace_hash128_blake3('test/interpretation-readers/language-b');
    grammar_type bytea := public.laplace_hash128_blake3('test/interpretation-readers/grammar');
    wa bytea := laplace.word_id('ab');
    wb bytea := laplace.word_id('ba');
    wc bytea := laplace.word_id('aba');
    ca bytea := laplace.word_id('a');
    cb bytea := laplace.word_id('b');
    relation_id bytea := laplace.relation_type_id('HAS_SENSE');
    peer_relation bytea := laplace.relation_type_id('IS_A');
    all_mask bytea := decode(repeat('ff',32),'hex');
    family_before bytea[];
    band_before bytea[];
    bands_before bytea[];
    mask_before bytea[];
    corpus_before jsonb;
    graphemes_before jsonb;
    order_before jsonb;
    foundry_before jsonb;
    converse_before text;
    original_entities bigint;
    original_physicalities bigint;
    band int;
    prohibited_before bigint;
    missing_type bytea := public.laplace_hash128_blake3('test/interpretation-readers/missing-type');
BEGIN
    IF low_type >= LEAST(word_type,sentence_type,codepoint_type,language_type,relation_type,grammar_type) THEN
        RAISE EXCEPTION 'fixture requires a strictly smaller unrelated interpretation';
    END IF;
    INSERT INTO laplace.entities(id,tier,type_id) VALUES
        (source_id,2,word_type),(topic_id,2,word_type),
        (wa,2,word_type),(wb,2,word_type),(wc,2,word_type),
        (sentence_id,3,sentence_type),
        (ca,0,codepoint_type),(cb,0,codepoint_type),
        (lang_a,2,language_type),(lang_b,2,language_type),
        (relation_id,2,relation_type),(wa,2,grammar_type);
    INSERT INTO laplace.canonical_names(id,name)
    VALUES(grammar_type,'substrate/type/grammar/reader-fixture/node/v1');

    -- The geometric fallback resolves actual child coordinates, not packed IDs.
    -- Preserve any pre-existing atom physicality supplied by a seeded fixture.
    INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,n_constituents,observed_at)
    SELECT public.laplace_hash128_blake3(entity_id || decode('0100','hex')),
           entity_id,1,coord,decode(repeat('00',16),'hex'),0,now()
    FROM (VALUES (ca,public.ST_MakePoint(0.1,0.2,0.3,0.4)),
                 (cb,public.ST_MakePoint(-0.1,0.2,0.0,0.3))) v(entity_id,coord)
    ON CONFLICT(id) DO NOTHING;

    INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,n_constituents,observed_at)
    SELECT public.laplace_hash128_blake3(entity_id || decode('0100','hex')),
           entity_id,1,public.ST_MakePoint(x,0,0,0),decode(repeat('00',16),'hex'),
           public.ST_MakeLine(points),cardinality(points),now()
    FROM (VALUES
        (wa,0.0,ARRAY[public.laplace_mantissa_pack(ca,1,1,0),public.laplace_mantissa_pack(cb,2,1,0)]),
        (wb,0.5,ARRAY[public.laplace_mantissa_pack(cb,1,1,0),public.laplace_mantissa_pack(ca,2,1,0)]),
        (wc,0.01,ARRAY[public.laplace_mantissa_pack(ca,1,1,0),public.laplace_mantissa_pack(cb,2,1,0),
                      public.laplace_mantissa_pack(ca,3,1,0)]),
        (sentence_id,0.25,ARRAY[public.laplace_mantissa_pack(wa,1,1,4),public.laplace_mantissa_pack(wb,2,1,4)])
    ) v(entity_id,x,points);

    INSERT INTO laplace.attestations
        (id,subject_id,type_id,object_id,source_id,context_id,outcome,last_observed_at,
         observation_count,sum_score_fp1e9,opponent_rd_fp1e9)
    VALUES
        (public.laplace_hash128_blake3('test/interpretation-readers/a'),wa,peer_relation,wb,
         source_id,lang_a,2,now(),1,1000000000,30000000000),
        (public.laplace_hash128_blake3('test/interpretation-readers/b'),wa,peer_relation,wb,
         source_id,lang_b,2,now(),2,2000000000,30000000000);
    INSERT INTO laplace.consensus
        (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
    SELECT laplace.consensus_id(s,t,o),s,t,o,r,30000000000,60000000,5,now()
    FROM (VALUES
        (wa,peer_relation,source_id,3000000000000::bigint),
        (wb,peer_relation,source_id,1500000000000::bigint),
        (wc,peer_relation,source_id,2000000000000::bigint),
        (topic_id,laplace.relation_type_id('HAS_EXAMPLE'),sentence_id,2000000000000::bigint)
    ) v(s,t,o,r);

    -- Establish positive behavior before changing compatibility minima.
    band := consensus.relation_highway_band(relation_id);
    SELECT array_agg(id ORDER BY id) INTO family_before
    FROM consensus.relation_family_members('HAS_SENSE');
    SELECT array_agg(id ORDER BY id) INTO band_before
    FROM unnest(laplace.relation_band_types(band)) u(id);
    SELECT array_agg(id ORDER BY id) INTO bands_before
    FROM unnest(laplace.relation_band_types(ARRAY[band])) u(id);
    SELECT array_agg(id ORDER BY id) INTO mask_before
    FROM unnest(consensus.relation_mask_types(all_mask)) u(id);
    IF NOT COALESCE(relation_id=ANY(family_before) AND relation_id=ANY(band_before)
            AND relation_id=ANY(bands_before) AND relation_id=ANY(mask_before),false) THEN
        RAISE EXCEPTION 'governed relation fixture was not selected before reinterpretation';
    END IF;
    IF generation.respell_variant_seed('reader-fixture','node') IS DISTINCT FROM wa
       OR (SELECT lang FROM converse.attested_language_batch(ARRAY[wa])) IS DISTINCT FROM lang_b
       OR generation.consensus_peer(wa,1) IS DISTINCT FROM wc
       OR (SELECT count(*) FROM structural.entities_at_depth(3::smallint) WHERE id=sentence_id) <> 1 THEN
        RAISE EXCEPTION 'positive seed/language/peer/tier fixture failed';
    END IF;
    SELECT jsonb_agg(to_jsonb(v) ORDER BY surface) INTO corpus_before
    FROM generation.corpus_word_vocab(100000,NULL) v;
    SELECT jsonb_agg(to_jsonb(v) ORDER BY entity_id) INTO graphemes_before
    FROM generation.grapheme_floor_vocab(100000,NULL) v;
    SELECT jsonb_agg(to_jsonb(v) ORDER BY subject_id,object_id) INTO order_before
    FROM generation.grapheme_order(ARRAY[ca,cb],NULL,1) v;
    SELECT jsonb_agg(to_jsonb(v) ORDER BY entity_id) INTO foundry_before
    FROM generation.foundry_vocab_crawl(ARRAY['ab'],32,1,8,0.0) v;
    converse_before := converse.tiered(topic_id,NULL,1,NULL);
    IF corpus_before IS NULL OR graphemes_before IS NULL OR order_before IS NULL
       OR foundry_before IS NULL OR converse_before IS NULL
       OR NOT EXISTS (SELECT 1 FROM generation.corpus_word_vocab(100000,NULL) WHERE surface='ab')
       OR NOT EXISTS (SELECT 1 FROM generation.grapheme_floor_vocab(100000,NULL) WHERE entity_id=ca) THEN
        RAISE EXCEPTION 'positive vocabulary/trajectory/converse fixture failed';
    END IF;
    SELECT count(*) INTO original_entities FROM laplace.entities;
    SELECT count(*) INTO original_physicalities FROM laplace.physicalities;

    -- The canonical content rows do not multiply. Lower unrelated observations
    -- hide the old scalar summary, and repeated matching types at other tiers
    -- must not multiply attestation weight or physical trajectory occurrences.
    INSERT INTO laplace.entities(id,tier,type_id)
    SELECT id,0,low_type FROM laplace.entities
    WHERE id IN (wa,sentence_id,ca,cb,lang_b,relation_id);
    INSERT INTO laplace.entities(id,tier,type_id) VALUES
        (wb,0,decode('01'||repeat('00',15),'hex')),
        (wc,0,decode('02'||repeat('00',15),'hex')),
        (wa,5,word_type),(wb,5,word_type),(wb,6,word_type),
        (sentence_id,5,sentence_type),(sentence_id,3,low_type),
        (sentence_id,4,realize.canonical_id('Document')),
        (ca,5,codepoint_type),(ca,6,realize.canonical_id('Grapheme')),
        (lang_a,3,language_type),(lang_a,4,language_type),(lang_a,5,language_type),
        (relation_id,5,relation_type),(relation_id,6,relation_type);
    IF (SELECT tier FROM laplace.entities WHERE id=sentence_id) <> 0
       OR (SELECT type_id FROM laplace.entities WHERE id=relation_id) <> low_type
       OR (SELECT count(*) FROM laplace.entities) <> original_entities
       OR (SELECT count(*) FROM laplace.physicalities) <> original_physicalities THEN
        RAISE EXCEPTION 'reinterpretation fixture did not converge content while retaining physicalities';
    END IF;
    IF family_before IS DISTINCT FROM
           (SELECT array_agg(id ORDER BY id) FROM consensus.relation_family_members('HAS_SENSE'))
       OR band_before IS DISTINCT FROM
           (SELECT array_agg(id ORDER BY id) FROM unnest(laplace.relation_band_types(band)) u(id))
       OR bands_before IS DISTINCT FROM
           (SELECT array_agg(id ORDER BY id) FROM unnest(laplace.relation_band_types(ARRAY[band])) u(id))
       OR mask_before IS DISTINCT FROM
           (SELECT array_agg(id ORDER BY id) FROM unnest(consensus.relation_mask_types(all_mask)) u(id)) THEN
        RAISE EXCEPTION 'relation membership disappeared or multiplied after reinterpretation';
    END IF;
    IF generation.respell_variant_seed('reader-fixture','node') IS DISTINCT FROM wa
       OR (SELECT lang FROM converse.attested_language_batch(ARRAY[wa])) IS DISTINCT FROM lang_b
       OR generation.consensus_peer(wa,1) IS DISTINCT FROM wc
       OR (SELECT count(*) FROM structural.entities_at_depth(3::smallint) WHERE id=sentence_id) <> 1 THEN
        RAISE EXCEPTION 'seed/language/peer/tier membership or evidence weight changed';
    END IF;
    IF corpus_before IS DISTINCT FROM
           (SELECT jsonb_agg(to_jsonb(v) ORDER BY surface) FROM generation.corpus_word_vocab(100000,NULL) v)
       OR graphemes_before IS DISTINCT FROM
           (SELECT jsonb_agg(to_jsonb(v) ORDER BY entity_id) FROM generation.grapheme_floor_vocab(100000,NULL) v)
       OR order_before IS DISTINCT FROM
           (SELECT jsonb_agg(to_jsonb(v) ORDER BY subject_id,object_id)
            FROM generation.grapheme_order(ARRAY[ca,cb],NULL,1) v)
       OR foundry_before IS DISTINCT FROM
           (SELECT jsonb_agg(to_jsonb(v) ORDER BY entity_id)
            FROM generation.foundry_vocab_crawl(ARRAY['ab'],32,1,8,0.0) v)
       OR converse_before IS DISTINCT FROM converse.tiered(topic_id,NULL,1,NULL) THEN
        RAISE EXCEPTION 'interpretations changed vocabulary, physical occurrence counts or sentence election';
    END IF;

    -- The geometric branch shares the same set-valued type membership.
    DELETE FROM laplace.consensus
    WHERE subject_id IN (wa,wb,wc) AND type_id=peer_relation AND object_id=source_id;
    IF generation.consensus_peer(wa,1) IS DISTINCT FROM wc THEN
        RAISE EXCEPTION 'geometric peer fallback lost a shared non-minimum type';
    END IF;
    -- Diagnostics must see hidden invalid facets once per canonical object/type.
    prohibited_before := laplace.fake_tier_band_count();
    INSERT INTO laplace.entities(id,tier,type_id) VALUES
        (wa,247,missing_type),(wa,248,missing_type);
    IF laplace.fake_tier_band_count() <> prohibited_before + 1
       OR (SELECT count(*) FROM laplace.identity_law_violations()
           WHERE id=missing_type AND reason='dangling_entity_type') <> 1
       OR EXISTS (SELECT 1 FROM laplace.identity_law_violations()
                  WHERE id=wa AND reason='multi_tier_entity') THEN
        RAISE EXCEPTION 'diagnostics hid a non-minimum facet or mistook facets for duplicate entities';
    END IF;
END
$membership_readers$;
ROLLBACK;
