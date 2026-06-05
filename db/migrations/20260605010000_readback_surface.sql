-- 20260605010000_readback_surface.sql
--
-- READBACK + RESOLUTION surface: the substrate answers in its own voice.
-- Content-addressing is one-way; id->content is reconstructed from what the
-- substrate already stores:
--   composites - Content physicality trajectory vertices ARE the ordered
--                constituent ids (public.laplace_mantissa_unpack, laplace_geom);
--   codepoints - id = BLAKE3-128(UTF-8 bytes) (engine unicode_seed.cpp:265),
--                derived wholly in SQL into codepoint_render (the perf-cache
--                principle in-DB; resolution index = lawful app plumbing);
--   meta       - canonical_names reverse map, seeded from the repo's canonical
--                vocabulary; writers register dynamic families at ingest.
-- Permanent substrate functions; retrieval never hand-writes inline SQL.
--
-- Schema-of-record: extension/laplace_substrate/sql/12_inspect.sql.in (same
-- commit). This migration converges EXISTING databases.

CREATE OR REPLACE FUNCTION laplace.canonical_id(p_name text) RETURNS bytea
    LANGUAGE sql IMMUTABLE STRICT AS $$
    SELECT public.laplace_hash128_blake3(convert_to(p_name, 'UTF8'))
$$;

CREATE TABLE IF NOT EXISTS laplace.canonical_names (
    id   bytea PRIMARY KEY CHECK (octet_length(id) = 16),
    name text  NOT NULL
);

CREATE OR REPLACE FUNCTION laplace.register_canonical(p_name text) RETURNS bytea
    LANGUAGE sql AS $$
    INSERT INTO laplace.canonical_names (id, name)
    VALUES (laplace.canonical_id(p_name), p_name)
    ON CONFLICT (id) DO NOTHING;
    SELECT laplace.canonical_id(p_name)
$$;

CREATE TABLE IF NOT EXISTS laplace.codepoint_render (
    id bytea   PRIMARY KEY CHECK (octet_length(id) = 16),
    cp integer NOT NULL
);

CREATE OR REPLACE FUNCTION laplace.build_codepoint_render() RETURNS bigint
    LANGUAGE sql AS $$
    INSERT INTO laplace.codepoint_render (id, cp)
    SELECT laplace.canonical_id(chr(g.cp)), g.cp
    FROM generate_series(1, 1114111) AS g(cp)
    WHERE g.cp NOT BETWEEN 55296 AND 57343
    ON CONFLICT (id) DO NOTHING;
    SELECT count(*) FROM laplace.codepoint_render
$$;

CREATE OR REPLACE FUNCTION laplace.constituents(p_id bytea)
    RETURNS TABLE(ordinal integer, child_id bytea)
    LANGUAGE sql STABLE
    SET search_path = laplace, public AS $$
    SELECT (public.laplace_mantissa_unpack((dp).geom)).ordinal,
           (public.laplace_mantissa_unpack((dp).geom)).entity_id
    FROM (
        SELECT public.ST_DumpPoints(p.trajectory) AS dp
        FROM physicalities p
        WHERE p.entity_id = p_id
          AND p.kind = 1
          AND p.trajectory IS NOT NULL
        ORDER BY p.source_id
        LIMIT 1
    ) d
    ORDER BY 1
$$;

CREATE OR REPLACE FUNCTION laplace.render_text(p_id bytea, p_max_depth integer DEFAULT 32)
    RETURNS text
    LANGUAGE sql STABLE
    SET search_path = laplace, public AS $$
    WITH RECURSIVE walk(id, path_ord, depth) AS (
        SELECT p_id, ARRAY[]::integer[], 0
        UNION ALL
        SELECT c.child_id, w.path_ord || c.ordinal, w.depth + 1
        FROM walk w
        CROSS JOIN LATERAL constituents(w.id) c
        WHERE w.depth < p_max_depth
          AND NOT EXISTS (SELECT 1 FROM codepoint_render r WHERE r.id = w.id)
    )
    SELECT string_agg(chr(r.cp), '' ORDER BY w.path_ord)
    FROM walk w
    JOIN codepoint_render r ON r.id = w.id
$$;

CREATE OR REPLACE FUNCTION laplace.render(p_id bytea) RETURNS text
    LANGUAGE sql STABLE
    SET search_path = laplace, public AS $$
    SELECT COALESCE(
        (SELECT n.name FROM canonical_names n WHERE n.id = p_id),
        (SELECT chr(r.cp) FROM codepoint_render r WHERE r.id = p_id),
        render_text(p_id),
        encode(p_id, 'hex') || '…')
$$;

CREATE OR REPLACE FUNCTION laplace.consensus_out_readable(p_id bytea, p_limit integer DEFAULT 40)
    RETURNS TABLE(kind text, object text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE
    SET search_path = laplace, public AS $$
    SELECT render(c.kind_id), render(c.object_id),
           round(((c.rating - 2*c.rd) / 1e9)::numeric, 3), c.witness_count
    FROM consensus c
    WHERE c.subject_id = p_id
    ORDER BY (c.rating - 2*c.rd) DESC
    LIMIT p_limit
$$;

-- Seed the canonical vocabulary harvested from the repo's source of record
-- (every 'substrate/...' canonical literal in app/, extension/, db/, engine/).
INSERT INTO laplace.canonical_names (id, name)
SELECT laplace.canonical_id(v.name), v.name
FROM (VALUES
    ('substrate/atomic/none/v1'),
    ('substrate/entity/Architecture_Llama/v1'),
    ('substrate/kind/ATTENDS/v1'),
    ('substrate/kind/CANONICAL_DECOMPOSES_TO/v1'),
    ('substrate/kind/CAPTIONS/v1'),
    ('substrate/kind/COMPLETES_TO/v1'),
    ('substrate/kind/CO_OCCURS_WITH/v1'),
    ('substrate/kind/DEPICTS/v1'),
    ('substrate/kind/DOWN_PROJECTS/v1'),
    ('substrate/kind/EMBEDS/v1'),
    ('substrate/kind/FOLLOWS/v1'),
    ('substrate/kind/GATES/v1'),
    ('substrate/kind/HAS_BLOCK/v1'),
    ('substrate/kind/HAS_COMBINING_CLASS/v1'),
    ('substrate/kind/HAS_GENERAL_CATEGORY/v1'),
    ('substrate/kind/HAS_HIDDEN_SIZE/v1'),
    ('substrate/kind/HAS_INTERMEDIATE_SIZE/v1'),
    ('substrate/kind/HAS_ISO639_1_CODE/v1'),
    ('substrate/kind/HAS_LANGUAGE/v1'),
    ('substrate/kind/HAS_LOWERCASE_MAPPING/v1'),
    ('substrate/kind/HAS_NUM_HEADS/v1'),
    ('substrate/kind/HAS_NUM_KV_HEADS/v1'),
    ('substrate/kind/HAS_NUM_LAYERS/v1'),
    ('substrate/kind/HAS_PART/v1'),
    ('substrate/kind/HAS_SCRIPT/v1'),
    ('substrate/kind/HAS_TRUST_CLASS/v1'),
    ('substrate/kind/HAS_UPPERCASE_MAPPING/v1'),
    ('substrate/kind/HAS_VARIANT_OF/v1'),
    ('substrate/kind/HAS_VOCAB_SIZE/v1'),
    ('substrate/kind/IS_A/v1'),
    ('substrate/kind/IS_ALIAS_OF/v1'),
    ('substrate/kind/IS_HYPERNYM_OF/v1'),
    ('substrate/kind/IS_LANGUAGE_CODE/v1'),
    ('substrate/kind/IS_LOSSY_ENCODING_OF/v1'),
    ('substrate/kind/IS_REPLACED_BY/v1'),
    ('substrate/kind/IS_TRANSLATION_OF/v1'),
    ('substrate/kind/K_PROJECTS/v1'),
    ('substrate/kind/MEMBER_OF_MACROLANGUAGE/v1'),
    ('substrate/kind/NORMALIZES/v1'),
    ('substrate/kind/OCCURS_IN_CONTEXT/v1'),
    ('substrate/kind/OUTPUT_PROJECTS/v1'),
    ('substrate/kind/OV_RELATES/v1'),
    ('substrate/kind/O_PROJECTS/v1'),
    ('substrate/kind/PRECEDES/v1'),
    ('substrate/kind/Q_PROJECTS/v1'),
    ('substrate/kind/SIMILAR_TO/v1'),
    ('substrate/kind/TOKEN_MAPS_TO/v1'),
    ('substrate/kind/TRANSCRIBES_AS/v1'),
    ('substrate/kind/UP_PROJECTS/v1'),
    ('substrate/kind/USES_SCRIPT/v1'),
    ('substrate/kind/V_PROJECTS/v1'),
    ('substrate/kind_tier/T10_ScalarValued/v1'),
    ('substrate/kind_tier/T11_Probationary/v1'),
    ('substrate/kind_tier/T1_Mandate/v1'),
    ('substrate/kind_tier/T2_StandardsStructural/v1'),
    ('substrate/kind_tier/T3_Taxonomic/v1'),
    ('substrate/kind_tier/T4_Partitive/v1'),
    ('substrate/kind_tier/T5_Causal/v1'),
    ('substrate/kind_tier/T6_Equivalence/v1'),
    ('substrate/kind_tier/T7_Oppositional/v1'),
    ('substrate/kind_tier/T8_Associative/v1'),
    ('substrate/kind_tier/T9_TensorCalculation/v1'),
    ('substrate/physicality_kind/BUILDING_BLOCK/v1'),
    ('substrate/physicality_kind/CONTENT/v1'),
    ('substrate/physicality_kind/PROJECTION/v1'),
    ('substrate/physicality_kind/PROJECTION_OUTPUT/v1'),
    ('substrate/source/Atomic2020Decomposer/v1'),
    ('substrate/source/AudioDecomposer/v1'),
    ('substrate/source/ConceptNetDecomposer/v1'),
    ('substrate/source/ISO639Decomposer/v1'),
    ('substrate/source/ImageDecomposer/v1'),
    ('substrate/source/OMWDecomposer/v1'),
    ('substrate/source/SubstrateCanonical/v1'),
    ('substrate/source/SyntheticBatched/v1'),
    ('substrate/source/SyntheticBatchedResume/v1'),
    ('substrate/source/SyntheticEnd2End/v1'),
    ('substrate/source/SyntheticLayer/v1'),
    ('substrate/source/SyntheticOverlap/v1'),
    ('substrate/source/SyntheticParallel/v1'),
    ('substrate/source/SyntheticParallelOverlap/v1'),
    ('substrate/source/SyntheticResume/v1'),
    ('substrate/source/TatoebaDecomposer/v1'),
    ('substrate/source/Test/v1'),
    ('substrate/source/UDDecomposer/v1'),
    ('substrate/source/UnicodeDecomposer/v1'),
    ('substrate/source/UserPrompt/v1'),
    ('substrate/source/WiktionaryDecomposer/v1'),
    ('substrate/source/WordNetDecomposer/v1'),
    ('substrate/source/model'),
    ('substrate/source/model/v1'),
    ('substrate/source/test-text/v1'),
    ('substrate/source/test/dedup'),
    ('substrate/source/test/empty'),
    ('substrate/source/test/full-row'),
    ('substrate/source/test/idempotent'),
    ('substrate/source/test/novel-ents'),
    ('substrate/source/test/reader'),
    ('substrate/source/test/rollback'),
    ('substrate/sql/04_attestations.sql.in'),
    ('substrate/sql/06_glicko2.sql.in'),
    ('substrate/sql/13_consensus.sql.in'),
    ('substrate/sql/laplace_substrate.sql.in'),
    ('substrate/src/laplace_substrate.c'),
    ('substrate/tests/CMakeLists.txt'),
    ('substrate/trust_class/AIModelProbe/v1'),
    ('substrate/trust_class/AcademicCurated/v1'),
    ('substrate/trust_class/AcademicCuratedWithUserInput/v1'),
    ('substrate/trust_class/AdversarialUntrusted/v1'),
    ('substrate/trust_class/AppDerived/v1'),
    ('substrate/trust_class/StandardsDerived/v1'),
    ('substrate/trust_class/StructuredCorpus/v1'),
    ('substrate/trust_class/SubstrateMandate/v1'),
    ('substrate/trust_class/UserCuratedResource/v1'),
    ('substrate/trust_class/UserPromptContent/v1'),
    ('substrate/type/Architecture/v1'),
    ('substrate/type/Atomic_Marker/v1'),
    ('substrate/type/Atomic_Split/v1'),
    ('substrate/type/Codepoint/v1'),
    ('substrate/type/Document/v1'),
    ('substrate/type/FoldingTestFixture/v1'),
    ('substrate/type/Grapheme/v1'),
    ('substrate/type/ISO639Code/v1'),
    ('substrate/type/Kind/v1'),
    ('substrate/type/Language/v1'),
    ('substrate/type/Model_Axis/v1'),
    ('substrate/type/Model_Recipe/v1'),
    ('substrate/type/Model_Tokenizer/v1'),
    ('substrate/type/Ngram/v1'),
    ('substrate/type/OrdinalContext/v1'),
    ('substrate/type/PhysicalityKind/v1'),
    ('substrate/type/Scalar/v1'),
    ('substrate/type/Sentence/v1'),
    ('substrate/type/Source/v1'),
    ('substrate/type/Tatoeba_Sentence/v1'),
    ('substrate/type/TestFixture/v1'),
    ('substrate/type/Text/v1'),
    ('substrate/type/Type/v1'),
    ('substrate/type/UD_Feature/v1'),
    ('substrate/type/UD_UPOS/v1'),
    ('substrate/type/UD_XPOS/v1'),
    ('substrate/type/UcdClassifier/v1'),
    ('substrate/type/Wiktionary_POS/v1'),
    ('substrate/type/Word/v1'),
    ('substrate/type/WordNet_LexCategory/v1'),
    ('substrate/type/WordNet_POS/v1'),
    ('substrate/type/WordNet_Sense/v1'),
    ('substrate/type/WordNet_Synset/v1')
) AS v(name)
ON CONFLICT (id) DO NOTHING;

SELECT laplace.build_codepoint_render();
