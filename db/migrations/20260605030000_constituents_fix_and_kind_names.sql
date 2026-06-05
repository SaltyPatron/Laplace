-- 20260605030000_constituents_fix_and_kind_names.sql
--
-- 1) constituents(): the ST_DumpPoints SRF sat inside the LIMIT 1 subquery,
--    so the limit truncated the EXPANDED vertices to one — render_text("dog")
--    returned 'd'. Pick the single Content physicality row first, then expand
--    vertices. Schema-of-record: 12_inspect.sql.in (same commit).
-- 2) Seed canonical names for the kind registry (99 static kinds,
--    pattern substrate/kind/<NAME>/v1 per KindRegistry.cs:61). Dynamic
--    families (DEP_*/FEAT_*) register at ingest.

CREATE OR REPLACE FUNCTION laplace.constituents(p_id bytea)
    RETURNS TABLE(ordinal integer, child_id bytea)
    LANGUAGE sql STABLE
    SET search_path = laplace, public AS $$
    SELECT u.ordinal, u.entity_id
    FROM (
        SELECT p.trajectory AS traj
        FROM physicalities p
        WHERE p.entity_id = p_id
          AND p.kind = 1
          AND p.trajectory IS NOT NULL
        ORDER BY p.source_id
        LIMIT 1
    ) t
    CROSS JOIN LATERAL public.ST_DumpPoints(t.traj) AS dp
    CROSS JOIN LATERAL public.laplace_mantissa_unpack(dp.geom) AS u
    ORDER BY 1
$$;

INSERT INTO laplace.canonical_names (id, name)
SELECT laplace.canonical_id(v.name), v.name
FROM (VALUES
    ('substrate/kind/ADJACENT_TO_PIXEL/v1'),
    ('substrate/kind/ALSO_SEE/v1'),
    ('substrate/kind/ATTENDS/v1'),
    ('substrate/kind/AT_LOCATION/v1'),
    ('substrate/kind/CANONICAL_DECOMPOSES_TO/v1'),
    ('substrate/kind/CAPABLE_OF/v1'),
    ('substrate/kind/CAPTIONS/v1'),
    ('substrate/kind/CAUSES/v1'),
    ('substrate/kind/CAUSES_DESIRE/v1'),
    ('substrate/kind/COMPLETES_TO/v1'),
    ('substrate/kind/CREATED_BY/v1'),
    ('substrate/kind/DEFINED_AS/v1'),
    ('substrate/kind/DEFINES/v1'),
    ('substrate/kind/DEPENDS_ON/v1'),
    ('substrate/kind/DEPICTS/v1'),
    ('substrate/kind/DERIVATIONALLY_RELATED/v1'),
    ('substrate/kind/DERIVED_FROM/v1'),
    ('substrate/kind/DESIRES/v1'),
    ('substrate/kind/DISTINCT_FROM/v1'),
    ('substrate/kind/ENTAILS/v1'),
    ('substrate/kind/ETYMOLOGICALLY_DERIVED_FROM/v1'),
    ('substrate/kind/ETYMOLOGICALLY_RELATED_TO/v1'),
    ('substrate/kind/FORM_OF/v1'),
    ('substrate/kind/HAS_A/v1'),
    ('substrate/kind/HAS_ATTRIBUTE/v1'),
    ('substrate/kind/HAS_BLOCK/v1'),
    ('substrate/kind/HAS_COMBINING_CLASS/v1'),
    ('substrate/kind/HAS_CONTEXT/v1'),
    ('substrate/kind/HAS_DOMAIN_REGION/v1'),
    ('substrate/kind/HAS_DOMAIN_TOPIC/v1'),
    ('substrate/kind/HAS_DOMAIN_USAGE/v1'),
    ('substrate/kind/HAS_ETYMOLOGY/v1'),
    ('substrate/kind/HAS_EXAMPLE/v1'),
    ('substrate/kind/HAS_EXTERNAL_ID/v1'),
    ('substrate/kind/HAS_FEATURE/v1'),
    ('substrate/kind/HAS_FIRST_SUBEVENT/v1'),
    ('substrate/kind/HAS_GENERAL_CATEGORY/v1'),
    ('substrate/kind/HAS_HYPERNYM/v1'),
    ('substrate/kind/HAS_HYPONYM/v1'),
    ('substrate/kind/HAS_INSTANCE/v1'),
    ('substrate/kind/HAS_LANGUAGE/v1'),
    ('substrate/kind/HAS_LAST_SUBEVENT/v1'),
    ('substrate/kind/HAS_LEX_CATEGORY/v1'),
    ('substrate/kind/HAS_LOWERCASE_MAPPING/v1'),
    ('substrate/kind/HAS_MEMBER/v1'),
    ('substrate/kind/HAS_PART/v1'),
    ('substrate/kind/HAS_POS/v1'),
    ('substrate/kind/HAS_PREREQUISITE/v1'),
    ('substrate/kind/HAS_PROPERTY/v1'),
    ('substrate/kind/HAS_SCRIPT/v1'),
    ('substrate/kind/HAS_SENSE/v1'),
    ('substrate/kind/HAS_SENSE_OF/v1'),
    ('substrate/kind/HAS_SUBEVENT/v1'),
    ('substrate/kind/HAS_SUBSTANCE/v1'),
    ('substrate/kind/HAS_UPOS/v1'),
    ('substrate/kind/HAS_UPPERCASE_MAPPING/v1'),
    ('substrate/kind/HAS_VARIANT_OF/v1'),
    ('substrate/kind/HAS_XPOS/v1'),
    ('substrate/kind/IN_VERB_GROUP_WITH/v1'),
    ('substrate/kind/IS_A/v1'),
    ('substrate/kind/IS_ANTONYM_OF/v1'),
    ('substrate/kind/IS_AT_SAMPLE/v1'),
    ('substrate/kind/IS_DOMAIN_REGION_MEMBER/v1'),
    ('substrate/kind/IS_DOMAIN_TOPIC_MEMBER/v1'),
    ('substrate/kind/IS_DOMAIN_USAGE_MEMBER/v1'),
    ('substrate/kind/IS_HYPERNYM_OF/v1'),
    ('substrate/kind/IS_HYPONYM_OF/v1'),
    ('substrate/kind/IS_INSTANCE_OF/v1'),
    ('substrate/kind/IS_LANGUAGE_CODE/v1'),
    ('substrate/kind/IS_LEMMA_OF/v1'),
    ('substrate/kind/IS_MEMBER_OF/v1'),
    ('substrate/kind/IS_PARTICIPLE_OF/v1'),
    ('substrate/kind/IS_PART_OF/v1'),
    ('substrate/kind/IS_PIXEL_OF/v1'),
    ('substrate/kind/IS_SENSE_OF/v1'),
    ('substrate/kind/IS_SIMILAR_TO/v1'),
    ('substrate/kind/IS_SUBSTANCE_OF/v1'),
    ('substrate/kind/IS_SYNONYM_OF/v1'),
    ('substrate/kind/IS_TRANSLATION_OF/v1'),
    ('substrate/kind/LOCATED_NEAR/v1'),
    ('substrate/kind/MADE_OF/v1'),
    ('substrate/kind/MANNER_OF/v1'),
    ('substrate/kind/MEMBER_OF_MACROLANGUAGE/v1'),
    ('substrate/kind/MOTIVATED_BY_GOAL/v1'),
    ('substrate/kind/NORMALIZES/v1'),
    ('substrate/kind/NOT_CAPABLE_OF/v1'),
    ('substrate/kind/NOT_DESIRES/v1'),
    ('substrate/kind/NOT_HAS_PROPERTY/v1'),
    ('substrate/kind/NOT_USED_FOR/v1'),
    ('substrate/kind/OBSTRUCTED_BY/v1'),
    ('substrate/kind/OV_RELATES/v1'),
    ('substrate/kind/PERTAINS_TO/v1'),
    ('substrate/kind/RECEIVES_ACTION/v1'),
    ('substrate/kind/RELATED_TO/v1'),
    ('substrate/kind/SIMILAR_TO/v1'),
    ('substrate/kind/SYMBOL_OF/v1'),
    ('substrate/kind/TRANSCRIBES_AS/v1'),
    ('substrate/kind/USED_FOR/v1'),
    ('substrate/kind/USES_SCRIPT/v1')
) AS v(name)
ON CONFLICT (id) DO NOTHING;
