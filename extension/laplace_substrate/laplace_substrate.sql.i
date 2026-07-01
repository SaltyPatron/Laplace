#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql/laplace_substrate.sql.in"
#line 1 "<built-in>"
#line 1 "<built-in>"
#line 471 "<built-in>"
#line 1 "<command line>"
#line 1 "<built-in>"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql/laplace_substrate.sql.in"
#line 1 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\sqldefines.h"
#line 2 "D:/Repositories/Laplace/extension/laplace_substrate/sql/laplace_substrate.sql.in"
#line 1 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\schema/laplace_substrate_version.sql.in"
CREATE OR REPLACE FUNCTION laplace_substrate_version()
    RETURNS text
    AS 'MODULE_PATHNAME', 'pg_laplace_substrate_version'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 2 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\schema/tables/entities.sql.in"
CREATE TABLE IF NOT EXISTS entities (
    id bytea PRIMARY KEY CHECK (octet_length(id) = 16),
    tier smallint NOT NULL CHECK (tier >= 0 AND tier < 256),
    type_id bytea NOT NULL,
    first_observed_by bytea,
    created_at timestamptz NOT NULL DEFAULT now(),
    highway_mask bytea CHECK (highway_mask IS NULL OR octet_length(highway_mask) = 32)
);
#line 3 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\schema/tables/physicalities.sql.in"
CREATE TABLE IF NOT EXISTS physicalities (
    id bytea PRIMARY KEY CHECK (octet_length(id) = 16),
    entity_id bytea NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
    type smallint NOT NULL,
    coord geometry(PointZM) NOT NULL,
    hilbert_index bytea NOT NULL CHECK (octet_length(hilbert_index) = 16),
    trajectory geometry(GeometryZM),
    radius_origin double precision GENERATED ALWAYS AS (
        sqrt(ST_X(coord)^2 + ST_Y(coord)^2 + ST_Z(coord)^2 + ST_M(coord)^2)
    ) STORED,
    n_constituents integer NOT NULL DEFAULT 0 CHECK (n_constituents >= 0),
    alignment_residual double precision,
    source_dim integer CHECK (source_dim IS NULL OR source_dim > 0),
    observed_at timestamptz NOT NULL DEFAULT now()
    -- No (entity_id, type) natural key: a physicality is keyed by its content-addressed id
    -- (BLAKE3 of entity_id|type|coord|trajectory). Under bit-perfect determinism id = f(entity,type),
    -- so the id PK already enforces one-per-content; a divergent geometry across compose paths is a
    -- determinism BUG to catch in a test, not to silently collapse with a relational unique. Dedup is
    -- the hash. "Building block" content is the same tree => same id => one physicality, referenced by
    -- id in other trajectories (no separate BuildingBlock row).
);

SELECT pg_extension_config_dump('physicalities', '');
#line 4 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\schema/tables/attestations.sql.in"
CREATE TABLE IF NOT EXISTS attestations (
    id bytea PRIMARY KEY CHECK (octet_length(id) = 16),
    subject_id bytea NOT NULL REFERENCES entities(id),
    type_id bytea NOT NULL REFERENCES entities(id),
    object_id bytea REFERENCES entities(id),
    source_id bytea NOT NULL REFERENCES entities(id),
    context_id bytea REFERENCES entities(id),
    outcome smallint NOT NULL CHECK (outcome IN (0, 1, 2)),
    last_observed_at timestamptz NOT NULL,
    observation_count bigint NOT NULL DEFAULT 1 CHECK (observation_count >= 0),
    highway_mask bytea CHECK (highway_mask IS NULL OR octet_length(highway_mask) = 32)
);
#line 5 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/entities_tier_btree.sql.in"
CREATE INDEX IF NOT EXISTS entities_tier_btree ON entities USING btree (tier);
#line 6 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/entities_type_btree.sql.in"
CREATE INDEX IF NOT EXISTS entities_type_btree ON entities USING btree (type_id);
#line 7 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/entities_tier_type_btree.sql.in"
CREATE INDEX IF NOT EXISTS entities_tier_type_btree ON entities USING btree (tier, type_id);
#line 8 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/entities_first_observed_btree.sql.in"
CREATE INDEX IF NOT EXISTS entities_first_observed_btree ON entities USING btree (first_observed_by) WHERE first_observed_by IS NOT NULL;
#line 9 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/entities_created_at_brin.sql.in"
CREATE INDEX IF NOT EXISTS entities_created_at_brin ON entities USING brin (created_at);
#line 10 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/physicalities_entity_btree.sql.in"
CREATE INDEX IF NOT EXISTS physicalities_entity_btree ON physicalities USING btree (entity_id);
#line 11 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/physicalities_type_btree.sql.in"
CREATE INDEX IF NOT EXISTS physicalities_type_btree ON physicalities USING btree (type);
#line 12 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/physicalities_coord_gist.sql.in"
CREATE INDEX IF NOT EXISTS physicalities_coord_gist ON physicalities USING gist (coord gist_geometry_ops_nd);
#line 13 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/physicalities_hilbert_btree.sql.in"
CREATE INDEX IF NOT EXISTS physicalities_hilbert_btree ON physicalities USING btree (hilbert_index);
#line 14 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/physicalities_radius_btree.sql.in"
CREATE INDEX IF NOT EXISTS physicalities_radius_btree ON physicalities USING btree (radius_origin);
#line 15 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/physicalities_residual_btree.sql.in"
CREATE INDEX IF NOT EXISTS physicalities_residual_btree ON physicalities USING btree (alignment_residual) WHERE alignment_residual IS NOT NULL;
#line 16 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/physicalities_observed_brin.sql.in"
CREATE INDEX IF NOT EXISTS physicalities_observed_brin ON physicalities USING brin (observed_at);
#line 17 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/physicalities_traj_probe.sql.in"
CREATE INDEX IF NOT EXISTS physicalities_traj_probe ON physicalities USING btree (observed_at) WHERE type = 1 AND trajectory IS NOT NULL;
#line 18 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/physicalities_constituents_gin.sql.in"
CREATE INDEX IF NOT EXISTS physicalities_constituents_gin ON physicalities USING gin (public.laplace_trajectory_constituent_ids(trajectory)) WHERE type = 1 AND trajectory IS NOT NULL;
#line 19 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/attestations_type_btree.sql.in"
CREATE INDEX IF NOT EXISTS attestations_type_btree ON attestations USING btree (type_id);
#line 20 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/attestations_object_btree.sql.in"
CREATE INDEX IF NOT EXISTS attestations_object_btree ON attestations USING btree (object_id) WHERE object_id IS NOT NULL;
#line 21 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/attestations_source_btree.sql.in"
CREATE INDEX IF NOT EXISTS attestations_source_btree ON attestations USING btree (source_id);
#line 22 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/attestations_context_btree.sql.in"
CREATE INDEX IF NOT EXISTS attestations_context_btree ON attestations USING btree (context_id) WHERE context_id IS NOT NULL;
#line 23 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/attestations_relation_btree.sql.in"
CREATE INDEX IF NOT EXISTS attestations_relation_btree ON attestations USING btree (subject_id, type_id, object_id);
#line 24 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/attestations_last_observed_brin.sql.in"
CREATE INDEX IF NOT EXISTS attestations_last_observed_brin ON attestations USING brin (last_observed_at);
#line 25 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\probes/entities_present_ordinals.sql.in"
CREATE OR REPLACE FUNCTION entities_present_ordinals(p_ids bytea[])
    RETURNS TABLE(idx int)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT (u.ord - 1)::int
    FROM unnest(p_ids) WITH ORDINALITY u(id, ord)
    INNER JOIN entities e ON e.id = u.id
$$;
#line 26 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\probes/physicalities_present_ordinals.sql.in"
CREATE OR REPLACE FUNCTION physicalities_present_ordinals(p_ids bytea[])
    RETURNS TABLE(idx int)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT (u.ord - 1)::int
    FROM unnest(p_ids) WITH ORDINALITY u(id, ord)
    INNER JOIN physicalities t ON t.id = u.id
$$;
#line 27 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\probes/attestations_present_ordinals.sql.in"
CREATE OR REPLACE FUNCTION attestations_present_ordinals(p_ids bytea[])
    RETURNS TABLE(idx int)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT (u.ord - 1)::int
    FROM unnest(p_ids) WITH ORDINALITY u(id, ord)
    INNER JOIN attestations t ON t.id = u.id
$$;
#line 28 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\probes/table_present_ordinals.sql.in"
CREATE OR REPLACE FUNCTION table_present_ordinals(p_table text, p_ids bytea[])
    RETURNS TABLE(idx int)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT o.idx FROM entities_present_ordinals(p_ids) o WHERE p_table = 'entities'
    UNION ALL
    SELECT o.idx FROM physicalities_present_ordinals(p_ids) o WHERE p_table = 'physicalities'
    UNION ALL
    SELECT o.idx FROM attestations_present_ordinals(p_ids) o WHERE p_table = 'attestations'
$$;
#line 29 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\probes/entities_exist_bitmap.sql.in"
CREATE OR REPLACE FUNCTION entities_exist_bitmap(ids bytea[])
    RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_laplace_entities_exist_bitmap'
    LANGUAGE C STABLE;
#line 30 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\probes/content_descent_bitmap.sql.in"
CREATE OR REPLACE FUNCTION content_descent_bitmap(ids bytea[], parents int[])
    RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_laplace_content_descent_bitmap'
    LANGUAGE C STABLE;
#line 31 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\probes/drop_content_descent_novel_ordinals.sql.in"
DROP FUNCTION IF EXISTS content_descent_novel_ordinals(bytea[], int[]);
#line 32 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\bootstrap/bootstrap.sql.in"
DO $bootstrap$
DECLARE
    type_id bytea;
    relation_type_meta_id bytea;
    physicality_type_meta_id bytea;
    source_id bytea;
    subscan_id bytea;

    pk_content_id bytea;
    pk_buildblock_id bytea;
    pk_projection_id bytea;
    pk_projection_out_id bytea;

    type_codepoint_id bytea;
    type_grapheme_id bytea;
    type_word_id bytea;
    type_sentence_id bytea;
    type_document_id bytea;

    tc_mandate_id bytea;
    tc_standards_id bytea;
    tc_academic_id bytea;
    tc_academic_user_id bytea;
    tc_corpus_id bytea;
    tc_user_curated_id bytea;
    tc_ai_probe_id bytea;
    tc_app_derived_id bytea;
    tc_user_prompt_id bytea;
    tc_response_id bytea;
    tc_adversarial_id bytea;

    reltype_is_a_id bytea;
    reltype_has_part_id bytea;
    reltype_co_occurs_with_id bytea;
    reltype_follows_id bytea;
    reltype_precedes_id bytea;
    reltype_occurs_in_context_id bytea;
    reltype_has_language_id bytea;
    reltype_is_translation_of_id bytea;
    reltype_depicts_id bytea;
    reltype_captions_id bytea;
    reltype_transcribes_as_id bytea;
    reltype_is_lossy_enc_id bytea;
    reltype_has_variant_of_id bytea;
    reltype_is_replaced_by_id bytea;
    reltype_has_trust_class_id bytea;
    reltype_is_alias_of_id bytea;



BEGIN
    type_id := public.laplace_hash128_blake3('Type'::bytea);
    relation_type_meta_id := public.laplace_hash128_blake3('RelationType'::bytea);
    physicality_type_meta_id := public.laplace_hash128_blake3('PhysicalityType'::bytea);
    source_id := public.laplace_hash128_blake3('Source'::bytea);

    INSERT INTO entities (id, tier, type_id) VALUES
        (type_id, 2, type_id),
        (relation_type_meta_id, 2, type_id),
        (physicality_type_meta_id, 2, type_id),
        (source_id, 2, type_id);

    subscan_id := public.laplace_hash128_blake3('substrate/source/SubstrateCanonical/v1'::bytea);
    INSERT INTO entities (id, tier, type_id, first_observed_by) VALUES
        (subscan_id, 2, source_id, NULL);

    pk_content_id := public.laplace_hash128_blake3('substrate/physicality_type/CONTENT/v1'::bytea);
    pk_buildblock_id := public.laplace_hash128_blake3('substrate/physicality_type/BUILDING_BLOCK/v1'::bytea);
    pk_projection_id := public.laplace_hash128_blake3('substrate/physicality_type/PROJECTION/v1'::bytea);

    pk_projection_out_id := public.laplace_hash128_blake3('substrate/physicality_type/PROJECTION_OUTPUT/v1'::bytea);

    INSERT INTO entities (id, tier, type_id, first_observed_by) VALUES
        (pk_content_id, 2, physicality_type_meta_id, subscan_id),
        (pk_buildblock_id, 2, physicality_type_meta_id, subscan_id),
        (pk_projection_id, 2, physicality_type_meta_id, subscan_id),
        (pk_projection_out_id, 2, physicality_type_meta_id, subscan_id);

    type_codepoint_id := public.laplace_hash128_blake3('Codepoint'::bytea);
    type_grapheme_id := public.laplace_hash128_blake3('Grapheme'::bytea);
    type_word_id := public.laplace_hash128_blake3('Word'::bytea);
    type_sentence_id := public.laplace_hash128_blake3('Sentence'::bytea);
    type_document_id := public.laplace_hash128_blake3('Document'::bytea);

    INSERT INTO entities (id, tier, type_id, first_observed_by) VALUES
        (type_codepoint_id, 2, type_id, subscan_id),
        (type_grapheme_id, 2, type_id, subscan_id),
        (type_word_id, 2, type_id, subscan_id),
        (type_sentence_id, 2, type_id, subscan_id),
        (type_document_id, 2, type_id, subscan_id);

    tc_mandate_id := public.laplace_hash128_blake3('substrate/trust_class/SubstrateMandate/v1'::bytea);
    tc_standards_id := public.laplace_hash128_blake3('substrate/trust_class/StandardsDerived/v1'::bytea);
    tc_academic_id := public.laplace_hash128_blake3('substrate/trust_class/AcademicCurated/v1'::bytea);
    tc_academic_user_id := public.laplace_hash128_blake3('substrate/trust_class/AcademicCuratedWithUserInput/v1'::bytea);
    tc_corpus_id := public.laplace_hash128_blake3('substrate/trust_class/StructuredCorpus/v1'::bytea);
    tc_user_curated_id := public.laplace_hash128_blake3('substrate/trust_class/UserCuratedResource/v1'::bytea);
    tc_ai_probe_id := public.laplace_hash128_blake3('substrate/trust_class/AIModelProbe/v1'::bytea);
    tc_app_derived_id := public.laplace_hash128_blake3('substrate/trust_class/AppDerived/v1'::bytea);
    tc_user_prompt_id := public.laplace_hash128_blake3('substrate/trust_class/UserPromptContent/v1'::bytea);
    tc_response_id := public.laplace_hash128_blake3('substrate/trust_class/ResponseContent/v1'::bytea);
    tc_adversarial_id := public.laplace_hash128_blake3('substrate/trust_class/AdversarialUntrusted/v1'::bytea);

    INSERT INTO entities (id, tier, type_id, first_observed_by) VALUES
        (tc_mandate_id, 2, type_id, subscan_id),
        (tc_standards_id, 2, type_id, subscan_id),
        (tc_academic_id, 2, type_id, subscan_id),
        (tc_academic_user_id, 2, type_id, subscan_id),
        (tc_corpus_id, 2, type_id, subscan_id),
        (tc_user_curated_id, 2, type_id, subscan_id),
        (tc_ai_probe_id, 2, type_id, subscan_id),
        (tc_app_derived_id, 2, type_id, subscan_id),
        (tc_user_prompt_id, 2, type_id, subscan_id),
        (tc_response_id, 2, type_id, subscan_id),
        (tc_adversarial_id, 2, type_id, subscan_id);

    reltype_is_a_id := public.laplace_hash128_blake3('IS_A'::bytea);
    reltype_has_part_id := public.laplace_hash128_blake3('HAS_PART'::bytea);
    reltype_co_occurs_with_id := public.laplace_hash128_blake3('CO_OCCURS_WITH'::bytea);
    reltype_follows_id := public.laplace_hash128_blake3('FOLLOWS'::bytea);
    reltype_precedes_id := public.laplace_hash128_blake3('PRECEDES'::bytea);
    reltype_occurs_in_context_id := public.laplace_hash128_blake3('OCCURS_IN_CONTEXT'::bytea);
    reltype_has_language_id := public.laplace_hash128_blake3('HAS_LANGUAGE'::bytea);
    reltype_is_translation_of_id := public.laplace_hash128_blake3('IS_TRANSLATION_OF'::bytea);
    reltype_depicts_id := public.laplace_hash128_blake3('DEPICTS'::bytea);
    reltype_captions_id := public.laplace_hash128_blake3('CAPTIONS'::bytea);
    reltype_transcribes_as_id := public.laplace_hash128_blake3('TRANSCRIBES_AS'::bytea);
    reltype_is_lossy_enc_id := public.laplace_hash128_blake3('IS_LOSSY_ENCODING_OF'::bytea);
    reltype_has_variant_of_id := public.laplace_hash128_blake3('HAS_VARIANT_OF'::bytea);
    reltype_is_replaced_by_id := public.laplace_hash128_blake3('IS_REPLACED_BY'::bytea);
    reltype_has_trust_class_id := public.laplace_hash128_blake3('HAS_TRUST_CLASS'::bytea);
    reltype_is_alias_of_id := public.laplace_hash128_blake3('IS_ALIAS_OF'::bytea);

    INSERT INTO entities (id, tier, type_id, first_observed_by) VALUES
        (reltype_is_a_id, 2, relation_type_meta_id, subscan_id),
        (reltype_has_part_id, 2, relation_type_meta_id, subscan_id),
        (reltype_co_occurs_with_id, 2, relation_type_meta_id, subscan_id),
        (reltype_follows_id, 2, relation_type_meta_id, subscan_id),
        (reltype_precedes_id, 2, relation_type_meta_id, subscan_id),
        (reltype_occurs_in_context_id, 2, relation_type_meta_id, subscan_id),
        (reltype_has_language_id, 2, relation_type_meta_id, subscan_id),
        (reltype_is_translation_of_id, 2, relation_type_meta_id, subscan_id),
        (reltype_depicts_id, 2, relation_type_meta_id, subscan_id),
        (reltype_captions_id, 2, relation_type_meta_id, subscan_id),
        (reltype_transcribes_as_id, 2, relation_type_meta_id, subscan_id),
        (reltype_is_lossy_enc_id, 2, relation_type_meta_id, subscan_id),
        (reltype_has_variant_of_id, 2, relation_type_meta_id, subscan_id),
        (reltype_is_replaced_by_id, 2, relation_type_meta_id, subscan_id),
        (reltype_has_trust_class_id, 2, relation_type_meta_id, subscan_id),
        (reltype_is_alias_of_id, 2, relation_type_meta_id, subscan_id);

    INSERT INTO attestations (
        id, subject_id, type_id, object_id, source_id, context_id,
        outcome, last_observed_at, observation_count
    ) VALUES (
        public.laplace_hash128_merkle(0::smallint, ARRAY[
            subscan_id, reltype_has_trust_class_id, tc_mandate_id, subscan_id, '\x00000000000000000000000000000000'::bytea
        ]),
        subscan_id, reltype_has_trust_class_id, tc_mandate_id, subscan_id, NULL,
        2, now(), 1
    );

    ALTER TABLE entities ADD CONSTRAINT entities_type_fk
        FOREIGN KEY (type_id) REFERENCES entities(id);
    ALTER TABLE entities ADD CONSTRAINT entities_first_observed_fk
        FOREIGN KEY (first_observed_by) REFERENCES entities(id);
END $bootstrap$;
#line 33 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/glicko2/laplace_glicko2_result.sql.in"
DO $idem$ BEGIN
    CREATE TYPE laplace_glicko2_result AS (
        rating bigint,
        rd bigint,
        volatility bigint
    );
EXCEPTION WHEN duplicate_object THEN NULL;
END $idem$;
#line 34 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/glicko2/laplace_glicko2_sfunc.sql.in"
CREATE OR REPLACE FUNCTION laplace_glicko2_sfunc(
    internal,
    bigint, bigint, bigint,
    bigint, bigint, bigint,
    bigint
) RETURNS internal
    AS 'MODULE_PATHNAME', 'pg_laplace_glicko2_sfunc'
    LANGUAGE C;
#line 35 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/glicko2/laplace_glicko2_finalfunc.sql.in"
CREATE OR REPLACE FUNCTION laplace_glicko2_finalfunc(internal)
    RETURNS laplace_glicko2_result
    AS 'MODULE_PATHNAME', 'pg_laplace_glicko2_finalfunc'
    LANGUAGE C;
#line 36 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/glicko2/laplace_glicko2_accumulate.sql.in"
CREATE OR REPLACE AGGREGATE laplace_glicko2_accumulate(
    prior_rating bigint,
    prior_rd bigint,
    prior_volatility bigint,
    opponent_rating bigint,
    opponent_rd bigint,
    score bigint,
    tau bigint
) (
    SFUNC = laplace_glicko2_sfunc,
    STYPE = internal,
    FINALFUNC = laplace_glicko2_finalfunc
);
#line 37 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/glicko2/laplace_score.sql.in"
CREATE OR REPLACE FUNCTION laplace_score(v double precision, m double precision)
    RETURNS bigint
    AS 'MODULE_PATHNAME', 'pg_laplace_score'
    LANGUAGE C IMMUTABLE STRICT;
#line 38 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/glicko2/laplace_score_inverse.sql.in"
CREATE OR REPLACE FUNCTION laplace_score_inverse(score bigint, m double precision)
    RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_laplace_score_inverse'
    LANGUAGE C IMMUTABLE STRICT;
#line 39 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/glicko2/laplace_glicko2_accumulate_games.sql.in"
CREATE OR REPLACE FUNCTION laplace_glicko2_accumulate_games(
    prior_rating bigint,
    prior_rd bigint,
    prior_volatility bigint,
    opponent_rating bigint,
    opponent_rd bigint,
    games bigint,
    sum_score bigint,
    tau bigint
) RETURNS laplace_glicko2_result
    AS 'MODULE_PATHNAME', 'pg_laplace_glicko2_accumulate_games'
    LANGUAGE C IMMUTABLE;
#line 40 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/cascade/astar_path_raw.sql.in"
CREATE OR REPLACE FUNCTION astar_path_raw(
    p_start bytea,
    p_goals bytea[],
    p_types bytea[],
    p_max_depth int DEFAULT 7,
    p_directed bool DEFAULT false)
    RETURNS TABLE(step int, entity_id bytea, g double precision)
    AS 'MODULE_PATHNAME', 'pg_laplace_astar_path'
    LANGUAGE C STABLE;
#line 41 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/trajectory/trajectory_point_count.sql.in"
CREATE OR REPLACE FUNCTION trajectory_point_count(p_traj geometry)
    RETURNS integer
    LANGUAGE sql IMMUTABLE STRICT
    SET search_path = public AS $$
    SELECT CASE WHEN p_traj IS NULL THEN 0
                ELSE public.ST_NPoints(p_traj)::integer END
$$;
#line 42 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/trajectory/trajectory_prefix_distance.sql.in"
CREATE OR REPLACE FUNCTION trajectory_prefix_distance(
    p_a geometry, p_b geometry)
    RETURNS double precision
    LANGUAGE sql STABLE STRICT
    SET search_path = @extschema@, public AS $$
    SELECT public.laplace_frechet_4d(p_a, p_b)
$$;
#line 43 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/entities_tier_brin.sql.in"
CREATE INDEX IF NOT EXISTS entities_tier_brin
    ON entities USING brin (tier) WITH (pages_per_range = 128);

COMMENT ON INDEX entities_tier_brin IS
    'BRIN tier banding for ingest validation and tier-distribution reads at scale.';
#line 44 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/entities_tier_type_brin.sql.in"
CREATE INDEX IF NOT EXISTS entities_tier_type_brin
    ON entities USING brin (tier, type_id) WITH (pages_per_range = 64);
#line 45 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\schema/tables/consensus.sql.in"
CREATE TABLE IF NOT EXISTS consensus (
    id bytea PRIMARY KEY CHECK (octet_length(id) = 16),
    subject_id bytea NOT NULL REFERENCES entities(id),
    type_id bytea NOT NULL REFERENCES entities(id),
    object_id bytea REFERENCES entities(id),
    rating bigint NOT NULL,
    rd bigint NOT NULL CHECK (rd > 0),
    volatility bigint NOT NULL CHECK (volatility > 0),
    witness_count bigint NOT NULL DEFAULT 1 CHECK (witness_count >= 0),
    last_observed_at timestamptz NOT NULL
);

SELECT pg_extension_config_dump('consensus', '');
#line 46 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/consensus_object_btree.sql.in"
CREATE INDEX IF NOT EXISTS consensus_object_btree ON consensus (object_id) WHERE object_id IS NOT NULL;
#line 47 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/consensus_type_btree.sql.in"
CREATE INDEX IF NOT EXISTS consensus_type_btree ON consensus (type_id);
#line 48 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/consensus_subject_type_btree.sql.in"
CREATE INDEX IF NOT EXISTS consensus_subject_type_btree ON consensus (subject_id, type_id);
#line 49 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/consensus_type_subject_btree.sql.in"
CREATE INDEX IF NOT EXISTS consensus_type_subject_btree ON consensus (type_id, subject_id) WHERE object_id IS NOT NULL;
#line 50 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/consensus_eff_mu_btree.sql.in"
CREATE INDEX IF NOT EXISTS consensus_eff_mu_btree ON consensus (((rating - 2*rd)) DESC) WHERE object_id IS NOT NULL;
#line 51 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\indexes/consensus_subject_eff_mu_btree.sql.in"
CREATE INDEX IF NOT EXISTS consensus_subject_eff_mu_btree ON consensus (subject_id, ((rating - 2*rd)) DESC) WHERE object_id IS NOT NULL;
#line 52 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\schema/tables/consensus_id.sql.in"
CREATE OR REPLACE FUNCTION consensus_id(
    p_subject bytea, p_type bytea, p_object bytea)
    RETURNS bytea LANGUAGE sql IMMUTABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT public.laplace_hash128_blake3(
        p_subject || p_type
        || COALESCE(p_object, '\x00000000000000000000000000000000'::bytea))
$$;
#line 53 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/mu/effective_mu.sql.in"
CREATE OR REPLACE FUNCTION @extschema@.effective_mu(p_rating bigint, p_rd bigint)
    RETURNS bigint
    AS 'MODULE_PATHNAME', 'pg_laplace_effective_mu'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 54 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/mu/eff_mu.sql.in"
CREATE OR REPLACE FUNCTION @extschema@.eff_mu(p_rating bigint, p_rd bigint)
    RETURNS bigint LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT p_rating - 2 * p_rd
$$;
#line 55 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/mu/eff_mu_display.sql.in"
CREATE OR REPLACE FUNCTION eff_mu_display(p_rating bigint, p_rd bigint)
    RETURNS numeric LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT round((@extschema@.eff_mu(p_rating, p_rd) / 1e9)::numeric, 3)
$$;
#line 56 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/mu/refuted.sql.in"
CREATE OR REPLACE FUNCTION refuted(p_rating bigint, p_rd bigint)
    RETURNS boolean LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT p_rating + 2 * p_rd < @extschema@.glicko2_neutral_mu()
$$;
#line 57 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/mu/glicko2_neutral_mu.sql.in"
CREATE OR REPLACE FUNCTION glicko2_neutral_mu() RETURNS bigint
    AS 'MODULE_PATHNAME', 'pg_laplace_glicko2_neutral_mu'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
#line 58 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/mu/glicko2_initial_rd.sql.in"
CREATE OR REPLACE FUNCTION glicko2_initial_rd() RETURNS bigint
    LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$ SELECT 350000000000::bigint $$;
#line 59 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/mu/glicko2_initial_volatility.sql.in"
CREATE OR REPLACE FUNCTION glicko2_initial_volatility() RETURNS bigint
    LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$ SELECT 60000000::bigint $$;
#line 60 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/mu/glicko2_tau.sql.in"
CREATE OR REPLACE FUNCTION glicko2_tau() RETURNS bigint
    LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$ SELECT 500000000::bigint $$;
#line 61 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/drop_period_staging.sql.in"
CREATE OR REPLACE FUNCTION drop_period_staging()
    RETURNS void LANGUAGE plpgsql VOLATILE
    SET search_path = @extschema@, public AS $$
DECLARE
    stale text;
BEGIN



    FOR stale IN
        SELECT c.relname FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = current_schema() AND c.relkind = 'r'
          AND (c.relname LIKE 'consensus\_period\_staging\_%'
               OR c.relname ~ '^consensus_walk_staging_\d+$')
    LOOP
        EXECUTE format('DROP TABLE %I', stale);
    END LOOP;
END;
$$;
#line 62 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/create_period_staging.sql.in"
CREATE OR REPLACE FUNCTION create_period_staging(p_partitions integer DEFAULT 1)
    RETURNS void LANGUAGE plpgsql VOLATILE
    SET search_path = @extschema@, public AS $$
DECLARE
    k integer;
BEGIN
    IF p_partitions < 1 OR p_partitions > 64 THEN
        RAISE EXCEPTION 'create_period_staging: % partitions out of range 1..64', p_partitions;
    END IF;
    PERFORM drop_period_staging();
    FOR k IN 0 .. p_partitions - 1 LOOP
        EXECUTE format(
            'CREATE UNLOGGED TABLE %I (
                 subject_id bytea NOT NULL,
                 type_id bytea NOT NULL,
                 object_id bytea,
                 phi bigint NOT NULL,
                 games bigint NOT NULL,
                 sum_score bigint NOT NULL,
                 last_ts timestamptz NOT NULL
             )',
            'consensus_period_staging_' || k);
    END LOOP;
END;
$$;
#line 63 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/period_staging_table.sql.in"
CREATE OR REPLACE FUNCTION period_staging_table(p_epoch integer, p_partition integer)
    RETURNS text LANGUAGE sql IMMUTABLE
    SET search_path = @extschema@, public AS $$
    SELECT format('consensus_period_staging_e%s_%s', lpad(p_epoch::text, 4, '0'), p_partition);
$$;
#line 64 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/create_period_staging_with_epoch.sql.in"
CREATE OR REPLACE FUNCTION create_period_staging(p_partitions integer, p_epoch integer)
    RETURNS void LANGUAGE plpgsql VOLATILE
    SET search_path = @extschema@, public AS $$
DECLARE
    k integer;
BEGIN
    IF p_partitions < 1 OR p_partitions > 64 THEN
        RAISE EXCEPTION 'create_period_staging: % partitions out of range 1..64', p_partitions;
    END IF;
    IF p_epoch < 1 OR p_epoch > 9999 THEN
        RAISE EXCEPTION 'create_period_staging: epoch % out of range 1..9999', p_epoch;
    END IF;
    FOR k IN 0 .. p_partitions - 1 LOOP
        EXECUTE format(
            'CREATE UNLOGGED TABLE %I (
                 subject_id bytea NOT NULL,
                 type_id bytea NOT NULL,
                 object_id bytea,
                 phi bigint NOT NULL,
                 games bigint NOT NULL,
                 sum_score bigint NOT NULL,
                 last_ts timestamptz NOT NULL
             )',
            period_staging_table(p_epoch, k));
    END LOOP;
END;
$$;
#line 65 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/period_phi_mixed.sql.in"
CREATE OR REPLACE FUNCTION period_phi_mixed(p_subject bytea, p_type bytea, p_object bytea)
    RETURNS bigint LANGUAGE plpgsql STABLE
    SET search_path = @extschema@, public AS $$
BEGIN
    RAISE EXCEPTION 'accumulation invariant violated: relation (% —[%]→ %) observed with mixed φ within one period',
        render(p_subject), render(p_type), COALESCE(render(p_object), '∅');
END;
$$;
#line 66 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/materialize_period_partition.sql.in"
CREATE OR REPLACE FUNCTION materialize_period_partition(p_table text)
    RETURNS bigint LANGUAGE plpgsql VOLATILE
    SET search_path = @extschema@, public AS $$
DECLARE
    n_materialized bigint;
    t0 timestamptz := clock_timestamp();
BEGIN
    IF to_regclass(p_table) IS NULL THEN
        RAISE EXCEPTION 'materialize_period_partition: staging % does not exist', p_table;
    END IF;

    EXECUTE format('ANALYZE %I', p_table);





    EXECUTE format($f$
        WITH merged AS (
            SELECT s.subject_id, s.type_id, s.object_id,
                   min(s.phi) AS phi_min,
                   max(s.phi) AS phi_max,
                   sum(s.games)::bigint AS games,
                   sum(s.sum_score)::bigint AS sum_score,
                   max(s.last_ts) AS last_ts
            FROM %I s
            GROUP BY s.subject_id, s.type_id, s.object_id
        ),
        keyed AS MATERIALIZED (
            SELECT mm.*, consensus_id(mm.subject_id, mm.type_id, mm.object_id) AS cid
            FROM merged mm
            ORDER BY cid
        )
        INSERT INTO consensus (id, subject_id, type_id, object_id,
                               rating, rd, volatility, witness_count, last_observed_at)
        SELECT f.cid,
               f.subject_id, f.type_id, f.object_id,
               (f.acc).rating, (f.acc).rd, (f.acc).volatility,
               f.prior_witnesses + f.games,
               f.last_ts
        FROM (
            SELECT m.subject_id, m.type_id, m.object_id, m.games, m.last_ts,
                   m.cid, COALESCE(c.witness_count, 0) AS prior_witnesses,
                   laplace_glicko2_accumulate_games(
                       COALESCE(c.rating, glicko2_neutral_mu()),
                       COALESCE(c.rd, glicko2_initial_rd()),
                       COALESCE(c.volatility, glicko2_initial_volatility()),
                       glicko2_neutral_mu(),
                       CASE WHEN m.phi_min <> m.phi_max
                            THEN period_phi_mixed(m.subject_id, m.type_id, m.object_id)
                            ELSE m.phi_min END,
                       m.games,
                       m.sum_score,
                       glicko2_tau()
                   ) AS acc
            FROM keyed m
            LEFT JOIN consensus c ON c.id = m.cid
        ) f
        ORDER BY f.cid
        ON CONFLICT (id) DO UPDATE SET
            rating = EXCLUDED.rating,
            rd = EXCLUDED.rd,
            volatility = EXCLUDED.volatility,
            witness_count = EXCLUDED.witness_count,
            last_observed_at = EXCLUDED.last_observed_at
    $f$, p_table);
    GET DIAGNOSTICS n_materialized = ROW_COUNT;

    EXECUTE format('DROP TABLE %I', p_table);
    RAISE LOG 'period fold %: % relations materialized in %',
        p_table, n_materialized, clock_timestamp() - t0;
    RETURN n_materialized;
END;
$$;
#line 67 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/materialize_period_partition_fresh.sql.in"
CREATE OR REPLACE FUNCTION materialize_period_partition_fresh(p_table text)
    RETURNS bigint LANGUAGE plpgsql VOLATILE
    SET search_path = @extschema@, public AS $$
DECLARE
    n_materialized bigint;
    t0 timestamptz := clock_timestamp();
BEGIN
    IF to_regclass(p_table) IS NULL THEN
        RAISE EXCEPTION 'materialize_period_partition_fresh: staging % does not exist', p_table;
    END IF;
    EXECUTE format('ANALYZE %I', p_table);

    EXECUTE format($f$
        WITH merged AS (
            SELECT s.subject_id, s.type_id, s.object_id,
                   min(s.phi) AS phi_min,
                   max(s.phi) AS phi_max,
                   sum(s.games)::bigint AS games,
                   sum(s.sum_score)::bigint AS sum_score,
                   max(s.last_ts) AS last_ts
            FROM %I s
            GROUP BY s.subject_id, s.type_id, s.object_id
        )
        INSERT INTO consensus (id, subject_id, type_id, object_id,
                               rating, rd, volatility, witness_count, last_observed_at)
        SELECT m.cid,
               m.subject_id, m.type_id, m.object_id,
               (m.acc).rating, (m.acc).rd, (m.acc).volatility,
               m.games, m.last_ts
        FROM (
            SELECT mm.subject_id, mm.type_id, mm.object_id, mm.games, mm.last_ts,
                   consensus_id(mm.subject_id, mm.type_id, mm.object_id) AS cid,
                   laplace_glicko2_accumulate_games(
                       glicko2_neutral_mu(),
                       glicko2_initial_rd(),
                       glicko2_initial_volatility(),
                       glicko2_neutral_mu(),
                       CASE WHEN mm.phi_min <> mm.phi_max
                            THEN period_phi_mixed(mm.subject_id, mm.type_id, mm.object_id)
                            ELSE mm.phi_min END,
                       mm.games,
                       mm.sum_score,
                       glicko2_tau()
                   ) AS acc
            FROM merged mm
        ) m
        ORDER BY m.cid
        ON CONFLICT (id) DO NOTHING
    $f$, p_table);
    GET DIAGNOSTICS n_materialized = ROW_COUNT;

    EXECUTE format('DROP TABLE %I', p_table);
    RAISE LOG 'period fold (fresh) %: % relations materialized in %',
        p_table, n_materialized, clock_timestamp() - t0;
    RETURN n_materialized;
END;
$$;
#line 68 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/consensus_fold_result.sql.in"
DO $do$
BEGIN
    IF to_regprocedure(
        'consensus_fold(boolean,bigint,bigint,bigint,bigint,bigint,bigint,bigint)') IS NOT NULL
    THEN
        RETURN;
    END IF;
    EXECUTE 'DROP FUNCTION IF EXISTS consensus_fold_step('
         || 'internal, boolean, bigint, bigint, bigint, bigint, bigint, bigint, bigint)';
    EXECUTE 'DROP FUNCTION IF EXISTS consensus_fold_final(internal)';
    EXECUTE 'DROP TYPE IF EXISTS consensus_fold_result';
    EXECUTE $c$ CREATE TYPE consensus_fold_result AS (
        rating bigint, rd bigint, volatility bigint, witness_count bigint) $c$;
    EXECUTE $c$ CREATE FUNCTION consensus_fold_step(
            internal, boolean, bigint, bigint, bigint, bigint, bigint, bigint, bigint)
        RETURNS internal LANGUAGE C PARALLEL RESTRICTED
        AS 'MODULE_PATHNAME', 'pg_laplace_consensus_fold_step' $c$;
    EXECUTE $c$ CREATE FUNCTION consensus_fold_final(internal)
        RETURNS consensus_fold_result LANGUAGE C PARALLEL RESTRICTED
        AS 'MODULE_PATHNAME', 'pg_laplace_consensus_fold_final' $c$;
    EXECUTE $c$ CREATE AGGREGATE consensus_fold(
            boolean, bigint, bigint, bigint, bigint, bigint, bigint, bigint) (
        SFUNC = consensus_fold_step,
        STYPE = internal,
        FINALFUNC = consensus_fold_final
    ) $c$;
END $do$;
#line 69 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/hash128_lo.sql.in"
CREATE OR REPLACE FUNCTION hash128_lo(h bytea)
    RETURNS bigint LANGUAGE sql IMMUTABLE
    SET search_path = @extschema@, public AS $$
    SELECT get_byte(h, 8)::bigint
         | (get_byte(h, 9)::bigint << 8)
         | (get_byte(h, 10)::bigint << 16)
         | (get_byte(h, 11)::bigint << 24)
         | (get_byte(h, 12)::bigint << 32)
         | (get_byte(h, 13)::bigint << 40)
         | (get_byte(h, 14)::bigint << 48)
         | (get_byte(h, 15)::bigint << 56);
$$;
#line 70 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/consensus_partition_of.sql.in"
CREATE OR REPLACE FUNCTION consensus_partition_of(
        p_subject bytea, p_type bytea, p_object bytea, p_nparts integer)
    RETURNS integer LANGUAGE sql IMMUTABLE
    SET search_path = @extschema@, public AS $$
    SELECT mod(x::numeric
               + CASE WHEN x < 0 THEN 18446744073709551616::numeric ELSE 0::numeric END,
               p_nparts::numeric)::integer

    FROM (SELECT hash128_lo(p_subject) # hash128_lo(p_type) #
                 COALESCE(hash128_lo(p_object), 0::bigint) AS x) t;
$$;
#line 71 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/consensus_fold_partition.sql.in"
CREATE OR REPLACE FUNCTION consensus_fold_partition(
        p_tables text[], p_epochs int[],
        p_partition integer, p_nparts integer, p_seed boolean)
    RETURNS bigint
    AS 'MODULE_PATHNAME', 'pg_laplace_consensus_fold_partition'
    LANGUAGE C VOLATILE;
#line 72 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/create_walk_staging.sql.in"
CREATE OR REPLACE FUNCTION create_walk_staging(p_partitions integer)
    RETURNS void LANGUAGE plpgsql VOLATILE
    SET search_path = @extschema@, public AS $$
DECLARE
    p integer;
BEGIN
    IF p_partitions < 1 OR p_partitions > 64 THEN
        RAISE EXCEPTION 'create_walk_staging: partitions must be 1..64';
    END IF;
    FOR p IN 0 .. p_partitions - 1 LOOP
        EXECUTE format($t$
            CREATE UNLOGGED TABLE IF NOT EXISTS %I (
                subject_id bytea NOT NULL,
                type_id bytea NOT NULL,
                context_id bytea,
                phi bigint NOT NULL,
                n_vertices int NOT NULL,
                games_total bigint NOT NULL,
                last_ts timestamptz NOT NULL,
                walk bytea NOT NULL
            )$t$, 'consensus_walk_staging_' || p);
    END LOOP;
END;
$$;
#line 73 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/consensus_fold_walks.sql.in"
CREATE OR REPLACE FUNCTION consensus_fold_walks(
        p_partition integer, p_nparts integer, p_seed boolean)
    RETURNS bigint
    AS 'MODULE_PATHNAME', 'pg_laplace_consensus_fold_walks'
    LANGUAGE C VOLATILE;
#line 74 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/consensus_fold_one_partition.sql.in"
CREATE OR REPLACE FUNCTION consensus_fold_one_partition(
        p integer, nparts integer, fresh boolean, lane text)
    RETURNS bigint LANGUAGE plpgsql VOLATILE
    SET search_path = @extschema@, public AS $$
DECLARE
    tabs text[] := '{}';
    eps int[] := '{}';
    parts text := '';
    seed text := '';
    q text;
    rel record;
    n_round bigint;
BEGIN
    FOR rel IN
        SELECT c.relname,
               substring(c.relname FROM 'consensus_period_staging_e(\d{4})_')::int AS epoch
        FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
        WHERE ns.nspname = current_schema() AND c.relkind = 'r'
          AND c.relname ~ format('^consensus_period_staging_e\d{4}_%s$', p)
        ORDER BY c.relname
    LOOP
        tabs := tabs || rel.relname;
        eps := eps || rel.epoch;
        IF parts <> '' THEN parts := parts || ' UNION ALL '; END IF;
        parts := parts || format(
            'SELECT subject_id, type_id, object_id, %s::int AS ord, phi, games, sum_score, last_ts FROM %I',
            rel.epoch, rel.relname);
    END LOOP;
    IF tabs = '{}' THEN
        RETURN 0;
    END IF;

    IF lane = 'engine' THEN
        RETURN consensus_fold_partition(tabs, eps, p, nparts, NOT fresh);
    END IF;

    IF NOT fresh THEN
        seed := format(
            'UNION ALL '
            'SELECT c.subject_id, c.type_id, c.object_id, 0 AS ord, true AS is_seed, '
            '       c.rating AS v1, c.rd AS v2, c.volatility AS v3, '
            '       0::bigint AS phi, c.witness_count AS games, 0::bigint AS sum_score, '
            '       c.last_observed_at AS last_ts '
            'FROM consensus c '
            'WHERE consensus_partition_of(c.subject_id, c.type_id, c.object_id, %s) = %s',
            nparts, p);
    END IF;
#line 56 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/consensus_fold_one_partition.sql.in"
    q := format($q$
        INSERT INTO consensus_next
        WITH staged AS (%s),
        partial AS (
            SELECT subject_id, type_id, object_id,
                   CASE WHEN min(phi) <> max(phi)
                        THEN period_phi_mixed(subject_id, type_id, object_id)
                        ELSE min(phi) END AS phi,
                   sum(games)::bigint AS games,
                   sum(sum_score)::bigint AS sum_score,
                   max(last_ts) AS last_ts
            FROM staged
            GROUP BY subject_id, type_id, object_id
        ),
        united AS (
            SELECT subject_id, type_id, object_id, 1 AS ord, false AS is_seed,
                   0::bigint AS v1, 0::bigint AS v2, 0::bigint AS v3,
                   phi, games, sum_score, last_ts
            FROM partial
            %s
        ),
        folded AS (
            SELECT subject_id, type_id, object_id,
                   consensus_fold(is_seed, v1, v2, v3, phi, games, sum_score,
                                  glicko2_tau() ORDER BY ord) AS f,
                   max(last_ts) AS last_ts
            FROM united
            GROUP BY subject_id, type_id, object_id
        )
        SELECT consensus_id(subject_id, type_id, object_id) AS id,
               subject_id, type_id, object_id,
               (f).rating, (f).rd, (f).volatility, (f).witness_count,
               last_ts AS last_observed_at
        FROM folded
    $q$, parts, seed);

    EXECUTE q;
    GET DIAGNOSTICS n_round = ROW_COUNT;
    RETURN n_round;
END;
$$;
#line 75 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/consensus_fold_swap.sql.in"
CREATE OR REPLACE FUNCTION consensus_fold_swap()
    RETURNS void LANGUAGE plpgsql VOLATILE
    SET search_path = @extschema@, public AS $$
DECLARE
    rel record;
BEGIN





    IF NOT EXISTS (SELECT 1 FROM consensus_next)
       AND EXISTS (SELECT 1 FROM consensus)
       AND COALESCE(NULLIF(current_setting('laplace.allow_empty_swap', true), ''), 'off') <> 'on'
    THEN
        RAISE EXCEPTION 'consensus_fold_swap: refusing to swap an empty consensus_next over a populated consensus — the fold produced no rows (errored or no-op); this would wipe consensus. Set laplace.allow_empty_swap=on to override.';
    END IF;
    ALTER TABLE consensus_next
        ALTER COLUMN id SET NOT NULL,
        ALTER COLUMN subject_id SET NOT NULL,
        ALTER COLUMN type_id SET NOT NULL,
        ALTER COLUMN rating SET NOT NULL,
        ALTER COLUMN rd SET NOT NULL,
        ALTER COLUMN volatility SET NOT NULL,
        ALTER COLUMN witness_count SET NOT NULL,
        ALTER COLUMN last_observed_at SET NOT NULL;
    ALTER TABLE consensus_next ADD PRIMARY KEY (id);
    ALTER TABLE consensus RENAME TO consensus_retired;
    ALTER TABLE consensus_next RENAME TO consensus;
    EXECUTE format('ALTER EXTENSION %I DROP TABLE consensus_retired', 'laplace_substrate');
    EXECUTE format('ALTER EXTENSION %I ADD TABLE consensus', 'laplace_substrate');

    -- Rebind v_consensus_* to the new consensus heap (see views/refresh_consensus_views.sql.in).
    CREATE OR REPLACE VIEW v_consensus_resolved AS
    SELECT c.id, c.subject_id, c.type_id, c.object_id,
           c.rating, c.rd, c.volatility, c.witness_count,
           c.last_observed_at,
           eff_mu(c.rating, c.rd) AS eff_mu_raw,
           eff_mu_display(c.rating, c.rd) AS eff_mu
    FROM consensus c;
    CREATE OR REPLACE VIEW v_consensus_edges AS
    SELECT c.id, c.subject_id, c.type_id, c.object_id,
           c.rating, c.rd, c.volatility, c.witness_count,
           c.last_observed_at,
           c.eff_mu_raw, c.eff_mu
    FROM v_consensus_resolved c
    WHERE c.object_id IS NOT NULL
      AND c.eff_mu_raw > 0;

    DROP TABLE consensus_retired;


    FOR rel IN
        SELECT c.relname
        FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
        WHERE ns.nspname = current_schema() AND c.relkind = 'r'
          AND c.relname ~ '^consensus_period_staging_e\d{4}_\d+$'
    LOOP
        EXECUTE format('DROP TABLE %I', rel.relname);
    END LOOP;

    EXECUTE 'ANALYZE consensus';
END;
$$;
#line 76 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/walk_fold_prepare.sql.in"
CREATE OR REPLACE FUNCTION walk_fold_prepare(OUT nwalk integer, OUT fresh boolean)
    LANGUAGE plpgsql VOLATILE
    SET search_path = @extschema@, public AS $$
DECLARE
    nflat integer;
BEGIN
    SELECT max(substring(c.relname FROM '_(\d+)$')::int) + 1 INTO nwalk
    FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
    WHERE ns.nspname = current_schema() AND c.relkind = 'r'
      AND c.relname ~ '^consensus_walk_staging_\d+$';
    SELECT max(substring(c.relname FROM '_(\d+)$')::int) + 1 INTO nflat
    FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
    WHERE ns.nspname = current_schema() AND c.relkind = 'r'
      AND c.relname ~ '^consensus_period_staging_e\d{4}_\d+$';
    IF nwalk IS NULL THEN
        RAISE EXCEPTION 'walk_fold_prepare: no walk journal staged';
    END IF;
    IF nflat IS NOT NULL THEN
        RAISE EXCEPTION 'walk_fold_prepare: both walk and flat journals staged — fold one shape at a time';
    END IF;
    SELECT NOT EXISTS (SELECT 1 FROM consensus LIMIT 1) INTO fresh;
    IF to_regclass(current_schema() || '.consensus_next') IS NOT NULL THEN
        EXECUTE 'DROP TABLE consensus_next';
    END IF;
    CREATE TABLE consensus_next (
        id bytea,
        subject_id bytea,
        type_id bytea,
        object_id bytea,
        rating bigint,
        rd bigint,
        volatility bigint,
        witness_count bigint,
        last_observed_at timestamptz
    );
END;
$$;
#line 77 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/walk_fold_finalize.sql.in"
CREATE OR REPLACE FUNCTION walk_fold_finalize()
    RETURNS void LANGUAGE plpgsql VOLATILE
    SET search_path = @extschema@, public AS $$
DECLARE
    rel record;
BEGIN
    IF to_regclass(current_schema() || '.consensus_next') IS NULL THEN
        RAISE EXCEPTION 'walk_fold_finalize: consensus_next missing — prepare did not run or was rolled back';
    END IF;
    FOR rel IN
        SELECT c.relname
        FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
        WHERE ns.nspname = current_schema() AND c.relkind = 'r'
          AND c.relname ~ '^consensus_walk_staging_\d+$'
    LOOP
        EXECUTE format('DROP TABLE %I', rel.relname);
    END LOOP;
    PERFORM consensus_fold_swap();
END;
$$;
#line 78 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/finish_consensus_fold.sql.in"
CREATE OR REPLACE FUNCTION finish_consensus_fold()
    RETURNS bigint LANGUAGE plpgsql VOLATILE
    SET search_path = @extschema@, public AS $$
DECLARE
    nparts integer;
    nwalk integer;
    p integer;
    lane text;
    n_round bigint;
    n bigint := 0;
    fresh boolean;
    t0 timestamptz := clock_timestamp();
BEGIN


    SELECT max(substring(c.relname FROM '_(\d+)$')::int) + 1 INTO nwalk
    FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
    WHERE ns.nspname = current_schema() AND c.relkind = 'r'
      AND c.relname ~ '^consensus_walk_staging_\d+$';







    SELECT max(substring(c.relname FROM '_(\d+)$')::int) + 1 INTO nparts
    FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
    WHERE ns.nspname = current_schema() AND c.relkind = 'r'
      AND c.relname ~ '^consensus_period_staging_e\d{4}_\d+$';
    IF nwalk IS NOT NULL AND nparts IS NOT NULL THEN
        RAISE EXCEPTION 'finish_consensus_fold: both walk and flat journals staged — fold one shape at a time';
    END IF;

    IF nwalk IS NOT NULL THEN
        SELECT NOT EXISTS (SELECT 1 FROM consensus LIMIT 1) INTO fresh;
        IF to_regclass(current_schema() || '.consensus_next') IS NOT NULL THEN
            EXECUTE 'DROP TABLE consensus_next';
        END IF;
        CREATE TABLE consensus_next (
            id bytea,
            subject_id bytea,
            type_id bytea,
            object_id bytea,
            rating bigint,
            rd bigint,
            volatility bigint,
            witness_count bigint,
            last_observed_at timestamptz
        );
        FOR p IN 0 .. nwalk - 1 LOOP
            n_round := consensus_fold_walks(p, nwalk, NOT fresh);
            n := n + n_round;
            RAISE LOG 'terminal fold (walks): partition %/%: % relations folded (% cumulative) at %',
                p + 1, nwalk, n_round, n, clock_timestamp() - t0;
        END LOOP;
        FOR p IN 0 .. nwalk - 1 LOOP
            EXECUTE format('DROP TABLE %I', 'consensus_walk_staging_' || p);
        END LOOP;
        PERFORM consensus_fold_swap();
        RAISE LOG 'terminal fold (walks): % relations materialized (% partition(s), %) in %',
            n, nwalk, CASE WHEN fresh THEN 'fresh' ELSE 'seeded rebuild + swap' END,
            clock_timestamp() - t0;
        RETURN n;
    END IF;

    IF nparts IS NULL THEN
        RETURN 0;
    END IF;


    IF EXISTS (
        SELECT 1
        FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
        WHERE ns.nspname = current_schema() AND c.relkind = 'r'
          AND c.relname ~ '^consensus_period_staging_e\d{4}_\d+$'
        GROUP BY substring(c.relname FROM 'e(\d{4})_')
        HAVING count(*) <> nparts
            OR max(substring(c.relname FROM '_(\d+)$')::int) + 1 <> nparts)
    THEN
        RAISE EXCEPTION 'finish_consensus_fold: staged epochs disagree on the partition fan-out';
    END IF;

    SELECT NOT EXISTS (SELECT 1 FROM consensus LIMIT 1) INTO fresh;
    IF to_regclass(current_schema() || '.consensus_next') IS NOT NULL THEN
        EXECUTE 'DROP TABLE consensus_next';
    END IF;
    CREATE TABLE consensus_next (
        id bytea,
        subject_id bytea,
        type_id bytea,
        object_id bytea,
        rating bigint,
        rd bigint,
        volatility bigint,
        witness_count bigint,
        last_observed_at timestamptz
    );

    lane := COALESCE(NULLIF(current_setting('laplace.fold_lane', true), ''), 'engine');
    FOR p IN 0 .. nparts - 1 LOOP
        n_round := consensus_fold_one_partition(p, nparts, fresh, lane);
        n := n + n_round;
        RAISE LOG 'terminal fold (%): partition %/%: % relations folded (% cumulative) at %',
            lane, p + 1, nparts, n_round, n, clock_timestamp() - t0;
    END LOOP;

    PERFORM consensus_fold_swap();
    RAISE LOG 'terminal fold: % relations materialized (% partition(s), %) in %',
        n, nparts, CASE WHEN fresh THEN 'fresh' ELSE 'seeded rebuild + swap' END,
        clock_timestamp() - t0;
    RETURN n;
END;
$$;
#line 79 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/fold/materialize_period_consensus.sql.in"
CREATE OR REPLACE FUNCTION materialize_period_consensus(p_partition integer DEFAULT NULL)
    RETURNS bigint LANGUAGE plpgsql VOLATILE
    SET search_path = @extschema@, public AS $$
DECLARE
    t text;
    total bigint := 0;
BEGIN
    IF p_partition IS NOT NULL THEN
        RETURN materialize_period_partition('consensus_period_staging_' || p_partition);
    END IF;
    FOR t IN
        SELECT c.relname FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = current_schema() AND c.relkind = 'r'
          AND c.relname LIKE 'consensus\_period\_staging\_%'
        ORDER BY c.relname
    LOOP
        total := total + materialize_period_partition(t);
    END LOOP;
    RETURN total;
END;
$$;
#line 80 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/readback/canonical_id.sql.in"
CREATE OR REPLACE FUNCTION canonical_id(p_name text) RETURNS bytea
    LANGUAGE sql IMMUTABLE STRICT AS $$
    SELECT public.laplace_hash128_blake3(convert_to(p_name, 'UTF8'))
$$;
#line 81 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/readback/canonical_names.sql.in"
CREATE TABLE IF NOT EXISTS canonical_names (
    id bytea PRIMARY KEY CHECK (octet_length(id) = 16),
    name text NOT NULL
);
#line 82 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/readback/register_canonical.sql.in"
CREATE OR REPLACE FUNCTION register_canonical(p_name text) RETURNS bytea
    LANGUAGE sql AS $$
    INSERT INTO canonical_names (id, name)
    VALUES (@extschema@.canonical_id(p_name), p_name)
    ON CONFLICT (id) DO NOTHING;
    SELECT @extschema@.canonical_id(p_name)
$$;
#line 83 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/readback/codepoint_for_id.sql.in"
CREATE OR REPLACE FUNCTION codepoint_for_id(p_id bytea)
    RETURNS integer
    AS 'MODULE_PATHNAME', 'pg_laplace_codepoint_for_id'
    LANGUAGE C STABLE STRICT PARALLEL SAFE;
#line 84 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/readback/is_all_whitespace.sql.in"
CREATE OR REPLACE FUNCTION is_all_whitespace(p_text text)
    RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_laplace_is_all_whitespace'
    LANGUAGE C STABLE STRICT PARALLEL SAFE;
#line 85 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/readback/constituents.sql.in"
CREATE OR REPLACE FUNCTION constituents(p_id bytea)
    RETURNS TABLE(ordinal integer, child_id bytea, run_length integer, flags bigint)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT u.ordinal, u.entity_id, u.run_length, u.flags
    FROM (
        SELECT p.trajectory AS traj
        FROM physicalities p
        WHERE p.entity_id = p_id
          AND p.type = 1
          AND p.trajectory IS NOT NULL
        ORDER BY p.id
        LIMIT 1
    ) t
    CROSS JOIN LATERAL public.laplace_trajectory_constituents(t.traj) AS u
    ORDER BY 1
$$;
#line 86 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/readback/vertex_atom.sql.in"
CREATE OR REPLACE FUNCTION vertex_atom(p_flags bigint) RETURNS integer
    LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT public.laplace_vertex_atom(p_flags)
$$;
#line 87 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/readback/vertex_tier.sql.in"
CREATE OR REPLACE FUNCTION vertex_tier(p_flags bigint) RETURNS smallint
    LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT public.laplace_vertex_tier(p_flags)
$$;
#line 88 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/readback/render_text.sql.in"
CREATE OR REPLACE FUNCTION render_text(p_id bytea, p_max_depth integer DEFAULT 32)
    RETURNS text
    AS 'MODULE_PATHNAME', 'pg_laplace_render_text'
    LANGUAGE C STABLE;
#line 89 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/readback/render_text_fast.sql.in"
CREATE OR REPLACE FUNCTION render_text_fast(p_id bytea, p_max_depth integer DEFAULT 8)
    RETURNS text
    AS 'MODULE_PATHNAME', 'pg_laplace_render_text_fast'
    LANGUAGE C STABLE;
#line 90 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/readback/render_text_batch.sql.in"
CREATE OR REPLACE FUNCTION render_text_batch(p_ids bytea[], p_max_depth integer DEFAULT 8)
    RETURNS text[]
    AS 'MODULE_PATHNAME', 'pg_laplace_render_text_batch'
    LANGUAGE C STABLE;
#line 91 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/readback/render.sql.in"
CREATE OR REPLACE FUNCTION render(p_id bytea) RETURNS text
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT COALESCE(
        (SELECT n.name FROM @extschema@.canonical_names n WHERE n.id = p_id),
        (SELECT chr(@extschema@.codepoint_for_id(p_id))
         FROM @extschema@.entities e
         WHERE e.id = p_id AND e.tier = 0),
        -- substrate-native name: follow the highest-consensus HAS_NAME_ALIAS to a
        -- codepoint-walk content entity and reconstruct it (so senses/types render as
        -- their human name, not the raw key). The alias relation is used by id; the
        -- name renders from its own codepoints — no canonical_names dependency, no cycle.
        render_text((SELECT c.object_id FROM @extschema@.consensus c
                     WHERE c.subject_id = p_id
                       AND c.type_id = @extschema@.relation_type_id('HAS_NAME_ALIAS')
                     ORDER BY @extschema@.eff_mu(c.rating, c.rd) DESC, c.object_id
                     LIMIT 1)),
        render_text(p_id),
        encode(p_id, 'hex') || '…')
$$;
#line 92 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/readback/register_canonicals.sql.in"
CREATE OR REPLACE FUNCTION register_canonicals(p_names text[]) RETURNS bigint
    LANGUAGE sql AS $$
    INSERT INTO canonical_names (id, name)
    SELECT @extschema@.canonical_id(n), n FROM unnest(p_names) AS n
    ON CONFLICT (id) DO NOTHING;
    SELECT count(*)::bigint FROM unnest(p_names)
$$;
#line 93 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inspect/entity_facets.sql.in"
CREATE OR REPLACE FUNCTION entity_facets(p_id bytea)
    RETURNS TABLE(tier smallint, type_id bytea)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT e.tier, e.type_id FROM entities e WHERE e.id = p_id
$$;
#line 94 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inspect/entity_physicalities.sql.in"
CREATE OR REPLACE FUNCTION entity_physicalities(p_id bytea)
    RETURNS TABLE(type smallint, x double precision, y double precision,
                  z double precision, m double precision,
                  radius double precision, n_constituents integer,
                  hilbert_index bytea)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT p.type, ST_X(p.coord), ST_Y(p.coord), ST_Z(p.coord), ST_M(p.coord),
           p.radius_origin, p.n_constituents, p.hilbert_index
    FROM physicalities p
    WHERE p.entity_id = p_id
    ORDER BY p.type, p.id
$$;
#line 95 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inspect/attestations_out.sql.in"
CREATE OR REPLACE FUNCTION attestations_out(p_id bytea, p_limit integer DEFAULT 40)
    RETURNS TABLE(type_id bytea, object_id bytea, source_id bytea, context_id bytea,
                  outcome smallint, observation_count bigint)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT a.type_id, a.object_id, a.source_id, a.context_id,
           a.outcome, a.observation_count
    FROM attestations a
    WHERE a.subject_id = p_id
    -- rank-weighted: semantically salient relations (HAS_DEFINITION/IS_A/…) surface above
    -- grammatical scaffolding (HAS_LANGUAGE/HAS_POS/PRECEDES) regardless of raw witness volume,
    -- so the LIMIT cut keeps the meaningful edges. Witness count / outcome break ties within a rank.
    ORDER BY relation_rank_resolved(a.type_id) DESC, a.observation_count DESC, a.outcome DESC
    LIMIT p_limit
$$;
#line 96 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inspect/attestations_in.sql.in"
CREATE OR REPLACE FUNCTION attestations_in(p_id bytea, p_limit integer DEFAULT 40)
    RETURNS TABLE(subject_id bytea, type_id bytea, source_id bytea, context_id bytea,
                  outcome smallint, observation_count bigint)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT a.subject_id, a.type_id, a.source_id, a.context_id,
           a.outcome, a.observation_count
    FROM attestations a
    WHERE a.object_id = p_id
    -- rank-weighted: salient relations first so the LIMIT cut keeps meaning over scaffolding.
    ORDER BY relation_rank_resolved(a.type_id) DESC, a.observation_count DESC, a.outcome DESC
    LIMIT p_limit
$$;
#line 97 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inspect/consensus_out_readable.sql.in"
CREATE OR REPLACE FUNCTION consensus_out_readable(p_id bytea, p_limit integer DEFAULT 40)
    RETURNS TABLE(type text, object text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT render(c.type_id), render(c.object_id),
           c.eff_mu, c.witness_count
    FROM v_consensus_resolved c
    WHERE c.subject_id = p_id
    ORDER BY relation_rank_resolved(c.type_id) * (c.eff_mu_raw / 1e9) DESC
    LIMIT p_limit
$$;
#line 98 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inspect/top_relations_readable.sql.in"
CREATE OR REPLACE FUNCTION top_relations_readable(p_limit integer DEFAULT 10, p_type bytea DEFAULT NULL)
    RETURNS TABLE(subject text, type text, object text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT render(t.subject_id), render(t.type_id), render(t.object_id),
           t.eff_mu, t.witnesses
    FROM top_relations(p_limit, p_type) t
$$;
#line 99 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inspect/attestation_response.sql.in"
CREATE OR REPLACE FUNCTION attestation_response(
    p_subject_id bytea,
    p_relation_type_id bytea,
    p_source_scope bytea[] DEFAULT NULL,
    p_context_id bytea DEFAULT NULL,
    p_top_k integer DEFAULT 32
)
RETURNS TABLE(
    object_id bytea,
    combined_eff_mu double precision,
    source_count integer,
    rating_fp1e9 bigint,
    rd_fp1e9 bigint
)
LANGUAGE sql STABLE
SET search_path = @extschema@, public
AS $$
    SELECT c.object_id,
           (eff_mu(c.rating, c.rd) / 1e9)::double precision,
           c.witness_count::integer,
           c.rating,
           c.rd
    FROM consensus c
    WHERE c.subject_id = p_subject_id
      AND c.type_id = p_relation_type_id
      AND c.object_id IS NOT NULL
      AND (
            (p_source_scope IS NULL AND p_context_id IS NULL)
         OR EXISTS (
                SELECT 1 FROM attestations a
                WHERE a.subject_id = c.subject_id
                  AND a.type_id = c.type_id
                  AND a.object_id IS NOT DISTINCT FROM c.object_id
                  AND (p_source_scope IS NULL OR a.source_id = ANY(p_source_scope))
                  AND (p_context_id IS NULL OR a.context_id IS NOT DISTINCT FROM p_context_id)
            )
          )
    ORDER BY eff_mu(c.rating, c.rd) DESC
    LIMIT GREATEST(1, p_top_k)
$$;
#line 100 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inspect/attestation_response_type.sql.in"
CREATE OR REPLACE FUNCTION attestation_response_type(
    p_subject_id bytea,
    p_relation_type_id bytea,
    p_source_scope bytea[] DEFAULT NULL,
    p_context_id bytea DEFAULT NULL,
    p_top_k integer DEFAULT 32
)
RETURNS TABLE(
    object_id bytea,
    combined_eff_mu double precision,
    source_count integer,
    rating_fp1e9 bigint,
    rd_fp1e9 bigint
)
LANGUAGE sql STABLE
SET search_path = @extschema@, public
AS $$
    SELECT * FROM attestation_response(p_subject_id, p_relation_type_id, p_source_scope, p_context_id, p_top_k)
$$;
#line 101 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inspect/attestation_unary_response.sql.in"
CREATE OR REPLACE FUNCTION attestation_unary_response(
    p_subject_id bytea,
    p_relation_type_id bytea,
    p_source_scope bytea[] DEFAULT NULL,
    p_context_id bytea DEFAULT NULL
)
RETURNS TABLE(
    combined_eff_mu double precision,
    source_count integer,
    rating_fp1e9 bigint,
    rd_fp1e9 bigint
)
LANGUAGE sql STABLE
SET search_path = @extschema@, public
AS $$
    SELECT (eff_mu(c.rating, c.rd) / 1e9)::double precision,
           c.witness_count::integer,
           c.rating,
           c.rd
    FROM consensus c
    WHERE c.subject_id = p_subject_id
      AND c.type_id = p_relation_type_id
      AND c.object_id IS NULL
      AND (
            (p_source_scope IS NULL AND p_context_id IS NULL)
         OR EXISTS (
                SELECT 1 FROM attestations a
                WHERE a.subject_id = c.subject_id
                  AND a.type_id = c.type_id
                  AND a.object_id IS NULL
                  AND (p_source_scope IS NULL OR a.source_id = ANY(p_source_scope))
                  AND (p_context_id IS NULL OR a.context_id IS NOT DISTINCT FROM p_context_id)
            )
          )
$$;
#line 102 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inspect/attestation_unary_response_type.sql.in"
CREATE OR REPLACE FUNCTION attestation_unary_response_type(
    p_subject_id bytea,
    p_relation_type_id bytea,
    p_source_scope bytea[] DEFAULT NULL,
    p_context_id bytea DEFAULT NULL
)
RETURNS TABLE(
    combined_eff_mu double precision,
    source_count integer,
    rating_fp1e9 bigint,
    rd_fp1e9 bigint
)
LANGUAGE sql STABLE
SET search_path = @extschema@, public
AS $$
    SELECT * FROM attestation_unary_response(p_subject_id, p_relation_type_id, p_source_scope, p_context_id)
$$;
#line 103 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/top_relations.sql.in"
CREATE OR REPLACE FUNCTION top_relations(p_limit integer DEFAULT 50, p_type bytea DEFAULT NULL)
    RETURNS TABLE(subject_id bytea, type_id bytea, object_id bytea, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE SET search_path = @extschema@, public AS $$
    SELECT c.subject_id, c.type_id, c.object_id,
           c.eff_mu, c.witness_count
    FROM v_consensus_resolved c
    WHERE c.object_id IS NOT NULL AND (p_type IS NULL OR c.type_id = p_type)
    ORDER BY relation_rank_resolved(c.type_id) * (c.eff_mu_raw / 1e9) DESC
    LIMIT p_limit
$$;
#line 104 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/completions.sql.in"
CREATE OR REPLACE FUNCTION completions(p_subject bytea, p_limit integer DEFAULT 40)
    RETURNS TABLE(object_id bytea, type_id bytea, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE SET search_path = @extschema@, public AS $$
    SELECT c.object_id, c.type_id, c.eff_mu, c.witness_count
    FROM v_consensus_resolved c
    WHERE c.subject_id = p_subject AND c.object_id IS NOT NULL
    ORDER BY relation_rank_resolved(c.type_id) * (c.eff_mu_raw / 1e9) DESC
    LIMIT p_limit
$$;
#line 105 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/consensus_out.sql.in"
CREATE OR REPLACE FUNCTION consensus_out(p_id bytea, p_limit integer DEFAULT 40)
    RETURNS TABLE(type_id bytea, object_id bytea, rating bigint, rd bigint,
                  volatility bigint, witness_count bigint)
    LANGUAGE sql STABLE SET search_path = @extschema@, public AS $$
    SELECT c.type_id, c.object_id, c.rating, c.rd, c.volatility, c.witness_count
    FROM consensus c
    WHERE c.subject_id = p_id
    -- rank-weighted: salient out-edges surface above scaffolding so the LIMIT keeps meaning.
    ORDER BY relation_rank_resolved(c.type_id) * (eff_mu(c.rating, c.rd) / 1e9) DESC
    LIMIT p_limit
$$;
#line 106 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/consensus_in.sql.in"
CREATE OR REPLACE FUNCTION consensus_in(p_id bytea, p_limit integer DEFAULT 40)
    RETURNS TABLE(subject_id bytea, type_id bytea, rating bigint, rd bigint,
                  volatility bigint, witness_count bigint)
    LANGUAGE sql STABLE SET search_path = @extschema@, public AS $$
    SELECT c.subject_id, c.type_id, c.rating, c.rd, c.volatility, c.witness_count
    FROM consensus c
    WHERE c.object_id = p_id
    -- rank-weighted: salient in-edges surface above scaffolding so the LIMIT keeps meaning.
    ORDER BY relation_rank_resolved(c.type_id) * (eff_mu(c.rating, c.rd) / 1e9) DESC
    LIMIT p_limit
$$;
#line 107 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/classify_circuit.sql.in"
CREATE OR REPLACE FUNCTION classify_circuit(p_pairs bytea[])
    RETURNS TABLE(subject_id bytea, object_id bytea, type_id bytea, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE SET search_path = @extschema@, public AS $$
    WITH pairs AS (
        SELECT substring(p FROM 1 FOR 16) AS subj,
               substring(p FROM 17 FOR 16) AS obj
        FROM unnest(p_pairs) AS p
        WHERE octet_length(p) = 32
    ),
    ranked AS (
        SELECT pr.subj, pr.obj, c.type_id,
               eff_mu_display(c.rating, c.rd) AS emu,
               c.witness_count AS w,
               row_number() OVER (
                   PARTITION BY pr.subj, pr.obj
                   ORDER BY eff_mu(c.rating, c.rd) DESC) AS rn
        FROM pairs pr
        JOIN consensus c
          ON c.subject_id = pr.subj AND c.object_id = pr.obj
    )
    SELECT subj, obj, type_id, emu, w
    FROM ranked
    WHERE rn = 1
$$;
#line 108 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/walk_branches.sql.in"
CREATE OR REPLACE FUNCTION walk_branches(
    p_prompt bytea,
    p_type bytea DEFAULT NULL,
    p_depth int DEFAULT 4,
    p_breadth int DEFAULT 5)
    RETURNS TABLE(depth int, path bytea[], types bytea[], entity_id bytea,
                  eff_mu numeric, path_mu numeric, witnesses bigint)
    AS 'MODULE_PATHNAME', 'pg_laplace_walk_branches'
    LANGUAGE C STABLE;
#line 109 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/walk_strongest.sql.in"
CREATE OR REPLACE FUNCTION walk_strongest(
    p_prompt bytea, p_type bytea DEFAULT NULL, p_depth int DEFAULT 8)
    RETURNS TABLE(step int, type_id bytea, entity_id bytea, eff_mu numeric)
    AS 'MODULE_PATHNAME', 'pg_laplace_walk_strongest'
    LANGUAGE C STABLE;
#line 110 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/consensus_stats.sql.in"
CREATE OR REPLACE FUNCTION consensus_stats()
    RETURNS TABLE(evidence_rows bigint, consensus_rows bigint, dedup_ratio numeric,
                  avg_witnesses numeric, max_witnesses bigint)
    LANGUAGE sql STABLE SET search_path = @extschema@, public AS $$
    SELECT a.cnt, c.cnt,
           round(a.cnt::numeric / NULLIF(c.cnt, 0), 3),
           c.avg_w, c.max_w
    FROM (SELECT count(*)::bigint AS cnt FROM attestations) a,
         (SELECT count(*)::bigint AS cnt,
                 round(avg(witness_count), 3) AS avg_w,
                 max(witness_count)::bigint AS max_w
          FROM consensus) c
$$;
#line 111 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/consensus_stats_approx.sql.in"
CREATE OR REPLACE FUNCTION consensus_stats_approx()
    RETURNS TABLE(evidence_rows bigint, consensus_rows bigint, dedup_ratio numeric,
                  avg_witnesses numeric, max_witnesses bigint)
    LANGUAGE sql STABLE SET search_path = @extschema@, public AS $$
    WITH est AS (
        SELECT c.relname,
               GREATEST(c.reltuples, 0)::bigint AS est_rows
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = current_schema()
          AND c.relname IN ('attestations', 'consensus')
    )
    SELECT (SELECT est_rows FROM est WHERE relname = 'attestations'),
           (SELECT est_rows FROM est WHERE relname = 'consensus'),
           round((SELECT est_rows FROM est WHERE relname = 'attestations')::numeric
                 / NULLIF((SELECT est_rows FROM est WHERE relname = 'consensus'), 0), 3),
           NULL::numeric,
           NULL::bigint
$$;
#line 112 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ops/relation_type_id.sql.in"
CREATE OR REPLACE FUNCTION relation_type_id(p_name text) RETURNS bytea
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $$
    SELECT @extschema@.canonical_id(p_name)
$$;
#line 113 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ops/entity_type_id.sql.in"
CREATE OR REPLACE FUNCTION entity_type_id(p_name text) RETURNS bytea
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $$
    SELECT @extschema@.canonical_id(p_name)
$$;
#line 114 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ops/source_id.sql.in"
CREATE OR REPLACE FUNCTION source_id(p_name text) RETURNS bytea
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $$
    SELECT @extschema@.canonical_id('substrate/source/' || p_name || '/v1')
$$;
#line 115 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ops/evidence_count.sql.in"
CREATE OR REPLACE FUNCTION evidence_count(
    p_type bytea DEFAULT NULL, p_source bytea DEFAULT NULL, p_object bytea DEFAULT NULL)
    RETURNS bigint
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT count(*)
    FROM @extschema@.attestations a
    WHERE (p_type IS NULL OR a.type_id = p_type)
      AND (p_source IS NULL OR a.source_id = p_source)
      AND (p_object IS NULL OR a.object_id = p_object)
$$;
#line 116 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ops/consensus_count.sql.in"
CREATE OR REPLACE FUNCTION consensus_count(p_type bytea DEFAULT NULL)
    RETURNS bigint
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT count(*) FROM @extschema@.consensus c
    WHERE (p_type IS NULL OR c.type_id = p_type)
$$;
#line 117 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ops/content_count.sql.in"
CREATE OR REPLACE FUNCTION content_count(p_source bytea DEFAULT NULL)
    RETURNS bigint
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT CASE
        WHEN p_source IS NULL THEN
            (SELECT count(*) FROM @extschema@.physicalities p WHERE p.type = 1)
        ELSE
            (SELECT count(DISTINCT p.entity_id)
             FROM @extschema@.physicalities p
             JOIN @extschema@.attestations a ON a.subject_id = p.entity_id
             WHERE p.type = 1 AND a.source_id = p_source)
    END
$$;
#line 118 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ops/multi_source_entity_count.sql.in"
CREATE OR REPLACE FUNCTION multi_source_entity_count()
    RETURNS bigint
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT count(*) FROM (
        SELECT a.subject_id FROM @extschema@.attestations a
        GROUP BY a.subject_id
        HAVING count(DISTINCT a.source_id) > 1) t
$$;
#line 119 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ops/substrate_counts.sql.in"
CREATE OR REPLACE FUNCTION substrate_counts()
    RETURNS TABLE(metric text, value bigint)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT 'entities', count(*) FROM @extschema@.entities
    UNION ALL
    SELECT 'entities/codepoint (T0)', count(*) FROM @extschema@.entities
        WHERE type_id = @extschema@.entity_type_id('Codepoint')
          AND tier = 0
    UNION ALL
    SELECT 'entities/compositional (T1-T4)', count(*) FROM @extschema@.entities e
        WHERE e.tier BETWEEN 1 AND 4
          AND e.type_id = ANY(@extschema@.compositional_type_ids())
    UNION ALL
    -- Anchors are identified by type_id (NOT a content-composition tier), never by tier.
    -- Anchors sit at Word tier (2) with a meta-type type_id; tier is composition depth only.
    SELECT 'entities/vocabulary (by type_id)', count(*) FROM @extschema@.entities e
        WHERE e.type_id <> ALL(@extschema@.compositional_type_ids())
    UNION ALL
    SELECT 'physicalities', count(*) FROM @extschema@.physicalities
    UNION ALL
    SELECT 'physicalities/content', count(*) FROM @extschema@.physicalities WHERE type = 1
    UNION ALL
    SELECT 'attestations (evidence)', count(*) FROM @extschema@.attestations
    UNION ALL
    SELECT 'consensus (relations)', count(*) FROM @extschema@.consensus
$$;
#line 120 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ops/arena_counts.sql.in"
CREATE OR REPLACE FUNCTION arena_counts()
    RETURNS TABLE(type text, type_id bytea, relations bigint, witnesses bigint,
                  top_eff_mu numeric)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT @extschema@.render(c.type_id), c.type_id, count(*), sum(c.witness_count),
           max(@extschema@.eff_mu_display(c.rating, c.rd))
    FROM @extschema@.consensus c
    GROUP BY c.type_id
    ORDER BY count(*) DESC
$$;
#line 121 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ops/source_counts.sql.in"
CREATE OR REPLACE FUNCTION source_counts()
    RETURNS TABLE(source text, source_id bytea, evidence bigint, content bigint)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$



    SELECT @extschema@.render(COALESCE(ev.sid, ct.sid)) AS source,
           COALESCE(ev.sid, ct.sid) AS source_id,
           COALESCE(ev.evidence, 0) AS evidence,
           COALESCE(ct.content, 0) AS content
    -- Geometry is source-free, so the content half is the count of distinct content
    -- entities each source ATTESTED (provenance on the attestation), not a physicality
    -- source filter (the column is gone).
    FROM (SELECT a.source_id AS sid, count(*) AS evidence
          FROM @extschema@.attestations a GROUP BY a.source_id) ev
    FULL JOIN (SELECT a.source_id AS sid, count(DISTINCT p.entity_id) AS content
               FROM @extschema@.attestations a
               JOIN @extschema@.physicalities p
                 ON p.entity_id = a.subject_id AND p.type = 1
               GROUP BY a.source_id) ct
      ON ev.sid = ct.sid
    ORDER BY evidence DESC NULLS LAST
$$;
#line 122 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ops/entity_type_counts.sql.in"
CREATE OR REPLACE FUNCTION entity_type_counts()
    RETURNS TABLE(type text, type_id bytea, tier smallint, entities bigint)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT @extschema@.render(e.type_id), e.type_id, e.tier, count(*)
    FROM @extschema@.entities e
    GROUP BY e.type_id, e.tier
    ORDER BY count(*) DESC
$$;
#line 123 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ops/consensus_tier_distribution.sql.in"
CREATE OR REPLACE FUNCTION consensus_tier_distribution()
    RETURNS TABLE(subject_tier smallint, relations bigint)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT e.tier, count(*)
    FROM @extschema@.consensus c JOIN @extschema@.entities e ON e.id = c.subject_id
    WHERE c.object_id IS NOT NULL
      AND e.type_id = ANY(@extschema@.compositional_type_ids())
    GROUP BY e.tier ORDER BY e.tier
$$;
#line 124 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ops/render_gaps.sql.in"
CREATE OR REPLACE FUNCTION render_gaps(p_limit integer DEFAULT 50)
    RETURNS TABLE(id bytea, roles text, refs bigint)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    WITH refs AS (
        SELECT c.type_id AS id, 'type' AS role FROM @extschema@.consensus c
        UNION ALL
        SELECT c.object_id, 'object' FROM @extschema@.consensus c WHERE c.object_id IS NOT NULL
        UNION ALL
        SELECT c.subject_id, 'subject' FROM @extschema@.consensus c
    ),
    agg AS (
        SELECT r.id, string_agg(DISTINCT r.role, ',' ORDER BY r.role) AS roles, count(*) AS refs
        FROM refs r GROUP BY r.id
    )
    SELECT a.id, a.roles, a.refs
    FROM agg a
    WHERE NOT EXISTS (SELECT 1 FROM @extschema@.canonical_names n WHERE n.id = a.id)
      AND @extschema@.codepoint_for_id(a.id) IS NULL
      AND NOT EXISTS (SELECT 1 FROM @extschema@.physicalities p
                      WHERE p.entity_id = a.id AND p.type = 1 AND p.trajectory IS NOT NULL)
    ORDER BY a.refs DESC
    LIMIT p_limit
$$;
#line 125 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ops/period_staging_status.sql.in"
CREATE OR REPLACE FUNCTION period_staging_status()
    RETURNS TABLE(partition_table text, staged_rows bigint)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT c.relname::text,
           coalesce(s.n_live_tup, 0)::bigint
    FROM pg_class c
    JOIN pg_namespace ns ON ns.oid = c.relnamespace
    LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
    WHERE ns.nspname = current_schema()
      AND c.relkind = 'r'
      AND c.relname LIKE 'consensus\_period\_staging\_%'
    ORDER BY c.relname
$$;
#line 126 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ops/api.sql.in"
CREATE OR REPLACE FUNCTION api(p_like text DEFAULT NULL)
    RETURNS TABLE(name text, args text, returns text)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT p.proname::text,
           pg_get_function_arguments(p.oid),
           pg_get_function_result(p.oid)
    FROM pg_proc p
    JOIN pg_namespace n ON n.oid = p.pronamespace
    WHERE n.nspname = current_schema()
      AND (p_like IS NULL OR p.proname ILIKE '%'||p_like||'%')
    ORDER BY p.proname
$$;
#line 127 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/relation/relation_type_resolve.sql.in"
CREATE OR REPLACE FUNCTION relation_type_resolve(p_surface text) RETURNS bytea
    LANGUAGE c IMMUTABLE STRICT PARALLEL SAFE AS 'MODULE_PATHNAME', 'pg_relation_type_resolve';
#line 128 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/relation/relation_type_in_family.sql.in"
CREATE OR REPLACE FUNCTION relation_type_in_family(p_type_id bytea, p_family text) RETURNS boolean
    LANGUAGE c IMMUTABLE STRICT PARALLEL SAFE AS 'MODULE_PATHNAME', 'pg_relation_type_in_family';
#line 129 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/relation/relation_rank.sql.in"
CREATE OR REPLACE FUNCTION relation_rank(p_type_id bytea) RETURNS float8
    LANGUAGE c IMMUTABLE STRICT PARALLEL SAFE AS 'MODULE_PATHNAME', 'pg_relation_rank';
#line 130 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/relation/relation_canonical.sql.in"
CREATE OR REPLACE FUNCTION relation_canonical(p_type_id bytea) RETURNS text
    LANGUAGE c IMMUTABLE STRICT PARALLEL SAFE AS 'MODULE_PATHNAME', 'pg_relation_canonical';
#line 131 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/relation/relation_rank_resolved.sql.in"
CREATE OR REPLACE FUNCTION relation_rank_resolved(p_type_id bytea) RETURNS float8
    LANGUAGE c STABLE STRICT AS 'MODULE_PATHNAME', 'pg_relation_rank_resolved';
#line 132 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/lexical/type_label.sql.in"
CREATE OR REPLACE FUNCTION type_label(p_type bytea)
    RETURNS text
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT lower(replace(render(p_type), '_', ' '))
$$;
#line 133 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/lexical/senses_bootstrap.sql.in"
CREATE OR REPLACE FUNCTION senses(p_word bytea)
    RETURNS TABLE(sense_id bytea, synset_id bytea, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT s.object_id, ss.object_id,
           eff_mu_display(s.rating, s.rd),
           s.witness_count + ss.witness_count
    FROM consensus s
    JOIN consensus ss ON ss.subject_id = s.object_id
                     AND ss.type_id = relation_type_id('IS_SENSE_OF')
    WHERE s.subject_id = p_word
      AND s.type_id = relation_type_id('HAS_SENSE')
    ORDER BY eff_mu(s.rating, s.rd) + eff_mu(ss.rating, ss.rd) DESC
$$;
#line 134 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/lexical/senses_with_context_bootstrap.sql.in"
CREATE OR REPLACE FUNCTION senses(p_word bytea, p_context bytea[])
    RETURNS TABLE(sense_id bytea, synset_id bytea, eff_mu numeric, witnesses bigint,
                  score numeric)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT s.object_id, ss.object_id,
           eff_mu_display(s.rating, s.rd),
           s.witness_count + ss.witness_count,
           round(((eff_mu(s.rating, s.rd) + eff_mu(ss.rating, ss.rd)
             + COALESCE((SELECT sum(eff_mu(c.rating, c.rd)) FROM consensus c
                         WHERE c.subject_id = ANY (p_context) AND c.object_id = ss.object_id
                           AND NOT refuted(c.rating, c.rd)), 0)
             + COALESCE((SELECT sum(eff_mu(c.rating, c.rd)) FROM consensus c
                         WHERE c.subject_id = ss.object_id AND c.object_id = ANY (p_context)
                           AND NOT refuted(c.rating, c.rd)), 0)) / 1e9)::numeric, 3)
    FROM consensus s
    JOIN consensus ss ON ss.subject_id = s.object_id
                     AND ss.type_id = relation_type_id('IS_SENSE_OF')
    WHERE s.subject_id = p_word
      AND s.type_id = relation_type_id('HAS_SENSE')
    ORDER BY 5 DESC
$$;
#line 135 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/lexical/define_bootstrap.sql.in"
CREATE OR REPLACE FUNCTION define(p_word bytea, p_limit int DEFAULT 5)
    RETURNS TABLE(definition text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT d.definition, d.eff_mu, d.witnesses
    FROM (
        SELECT render_text(g.object_id, 24) AS definition,
               eff_mu_display(g.rating, g.rd) AS eff_mu,
               g.witness_count AS witnesses,
               sn.eff_mu AS sense_rank
        FROM senses(p_word) sn
        JOIN consensus g ON g.subject_id = sn.synset_id
                        AND g.type_id = relation_type_id('HAS_DEFINITION')
        UNION ALL
        SELECT render_text(g.object_id, 24),
               eff_mu_display(g.rating, g.rd),
               g.witness_count,
               NULL::numeric
        FROM consensus g
        WHERE g.subject_id = p_word
          AND g.type_id = relation_type_id('HAS_DEFINITION')
    ) d
    ORDER BY COALESCE(d.sense_rank, 0) + d.eff_mu DESC
    LIMIT p_limit
$$;
#line 136 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/lexical/define_with_context_bootstrap.sql.in"
CREATE OR REPLACE FUNCTION define(p_word bytea, p_context bytea[], p_limit int DEFAULT 5)
    RETURNS TABLE(definition text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT d.definition, d.eff_mu, d.witnesses
    FROM (
        SELECT render_text(g.object_id, 24) AS definition,
               eff_mu_display(g.rating, g.rd) AS eff_mu,
               g.witness_count AS witnesses,
               sn.score AS sense_rank
        FROM senses(p_word, p_context) sn
        JOIN consensus g ON g.subject_id = sn.synset_id
                        AND g.type_id = relation_type_id('HAS_DEFINITION')
        UNION ALL
        SELECT render_text(g.object_id, 24),
               eff_mu_display(g.rating, g.rd),
               g.witness_count,
               NULL::numeric
        FROM consensus g
        WHERE g.subject_id = p_word
          AND g.type_id = relation_type_id('HAS_DEFINITION')
    ) d
    ORDER BY COALESCE(d.sense_rank, 0) + d.eff_mu DESC
    LIMIT p_limit
$$;
#line 137 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/lexical/examples_bootstrap.sql.in"
CREATE OR REPLACE FUNCTION examples(p_word bytea, p_limit int DEFAULT 5)
    RETURNS TABLE(example text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT render_text(g.object_id, 24),
           eff_mu_display(g.rating, g.rd), g.witness_count
    FROM senses(p_word) sn
    JOIN consensus g ON g.subject_id = sn.synset_id
                    AND g.type_id = relation_type_id('HAS_EXAMPLE')
    ORDER BY sn.eff_mu + eff_mu_display(g.rating, g.rd) DESC
    LIMIT p_limit
$$;
#line 138 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/word_id.sql.in"
CREATE OR REPLACE FUNCTION word_id(p_text text)
    RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_laplace_word_id'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 139 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/word_segment.sql.in"
CREATE OR REPLACE FUNCTION word_segment(p_text text)
    RETURNS TABLE(ord int, word text, id bytea)
    AS 'MODULE_PATHNAME', 'pg_laplace_word_segment'
    LANGUAGE C STABLE;
#line 140 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/label.sql.in"
CREATE OR REPLACE FUNCTION label(p_id bytea)
    RETURNS text
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT COALESCE(
        -- Entities content-addressed from an opaque join key (e.g. WordNet senses, keyed by
        -- lemma%ss_type:lex_filenum:lex_id for VerbNet/SemLink/CILI convergence) carry a
        -- human-readable display name as HAS_NAME_ALIAS. Prefer it over the raw key so a sense
        -- never surfaces as "sherlock%1:18:00" — this reads the deposited alias, it does not
        -- re-parse the key at render time.
        (SELECT render_text(c.object_id)
         FROM consensus c
         WHERE c.subject_id = p_id
           AND c.type_id = relation_type_id('HAS_NAME_ALIAS')
         ORDER BY eff_mu(c.rating, c.rd) DESC
         LIMIT 1),
        -- strip substrate wrappers and vocabulary paths so types, POS, morphology, languages,
        -- and relation entities resolve to clean, usable names via label().
        NULLIF(NULLIF(
            regexp_replace(
            regexp_replace(
                regexp_replace(
                    regexp_replace(
                        regexp_replace(
                            regexp_replace(
                                regexp_replace(
                                    regexp_replace(render(p_id),
                                        '^substrate/(?:type|relation|trust_class|source|entity)/(.*)/v1$', '\1'),
                                    '^substrate/pos/(?:probationary/[^/]+/)?(.+)/v1$', '\1'),
                                '^source/file/(.+)/v1$', '\1'),
                            '^repo:(.+)/v1$', '\1'),
                        '^language:([a-z]{3})$', '\1'),
                    '^ud/feature/(.+)$', '\1'),
                '^tiny-codes/concept/(.+)/v1$', '\1'),
            -- opaque join keys (ILI concept ids "i90107", synset offsets "00006150-n") are NOT names;
            -- null them so the HAS_DEFINITION fallback below renders the concept's gloss, not a hash-like id.
            '^(i[0-9]+|[0-9]{6,}-[nvarspNVARSP])$', ''),
            ''), encode(p_id, 'hex') || '…'),
        -- abstract entities (e.g. Language) carry their human-readable name as HAS_DEFINITION
        -- (ISO-639 stores it there). NO type:hash / hash-ellipsis fallback after this: a genuinely
        -- unnamed entity returns NULL (loud), never the "POS:6ea49d29" placeholder that masked the
        -- unfinished name links. The chain-completing, language-scoped resolver is realize(id, lang).
        (SELECT render_text(c.object_id)
         FROM consensus c
         WHERE c.subject_id = p_id
           AND c.type_id = relation_type_id('HAS_DEFINITION')
         ORDER BY eff_mu(c.rating, c.rd) DESC
         LIMIT 1))
$$;
#line 141 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/prompt_words.sql.in"
CREATE OR REPLACE FUNCTION prompt_words(p_text text)
    RETURNS TABLE(ord int, word text, id bytea)
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$



    SELECT ws.ord, ws.word,
           (SELECT e.id FROM entities e WHERE e.id = ws.id)
    FROM word_segment(p_text) ws
    ORDER BY ws.ord
$$;
#line 142 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/word_language.sql.in"
CREATE OR REPLACE FUNCTION word_language(p_word bytea)
    RETURNS bytea
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT c.object_id
    FROM consensus c
    WHERE c.subject_id = p_word
      AND c.type_id = relation_type_id('HAS_LANGUAGE')
      AND c.object_id IS NOT NULL
    ORDER BY eff_mu(c.rating, c.rd) DESC
    LIMIT 1
$$;
#line 143 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\inference/synset_members.sql.in"
-- inference/synset_members.sql.in
-- A word's synset co-members reached through the shared synset: word -> senses -> synset, then
-- the synset's IS_SYNONYM_OF members (both edge directions). synonyms() keeps the SAME-language
-- co-members; translations() keeps the cross-language ones — synonymy and translation are the
-- two halves of one emergent structure, never stored as direct edges.
--
-- A member may be a homograph shared across languages — the codepoints "roi" are simultaneously
-- French, Dutch, Indonesian, Breton, Boro, ... one content-addressed lemma carrying many
-- HAS_LANGUAGE edges. Collapsing it to word_language()'s single arbitrary pick (bor) makes it
-- invisible to a French query, so we EXPLODE each member over its full HAS_LANGUAGE set instead.
-- sense_mu carries the Glicko-2 eff_mu of the SOURCE word's sense that reached the member, so
-- dominant-sense members (king's "a male sovereign", 1154) outrank weaker senses (magnate, 1075).
DROP FUNCTION IF EXISTS synset_members(bytea);
CREATE OR REPLACE FUNCTION synset_members(p_word bytea)
    RETURNS TABLE(member bytea, lang bytea, mu bigint, witnesses bigint, sense_mu numeric)
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    -- Exclude named-entity (instance) synsets: a name identical across languages is a name,
    -- not a translation. WordNet @i (IS_INSTANCE_OF) marks person/place instances (Billie Jean
    -- King, M.L. King, B.B. King) whose OMW members are the untranslated proper name; @
    -- (HAS_HYPERNYM) marks real concepts (monarch, magnate). Dropping instance synsets here keeps
    -- both synonyms() and translations() to lexical equivalents only.
    WITH syn AS (
        SELECT s.synset_id AS id, max(s.eff_mu) AS sense_mu
        FROM senses(p_word) s
        WHERE NOT EXISTS (
            SELECT 1 FROM consensus i
            WHERE i.subject_id = s.synset_id
              AND i.type_id = relation_type_id('IS_INSTANCE_OF'))
        GROUP BY s.synset_id
    ),
    members AS (
        SELECT c.object_id AS w, c.rating, c.rd, c.witness_count, syn.sense_mu
        FROM consensus c JOIN syn ON c.subject_id = syn.id
        WHERE c.type_id = relation_type_id('IS_SYNONYM_OF') AND c.object_id IS NOT NULL
        UNION ALL
        SELECT c.subject_id AS w, c.rating, c.rd, c.witness_count, syn.sense_mu
        FROM consensus c JOIN syn ON c.object_id = syn.id
        WHERE c.type_id = relation_type_id('IS_SYNONYM_OF')
    ),
    agg AS (
        SELECT m.w,
               max(eff_mu(m.rating, m.rd)) AS mu,
               sum(m.witness_count)::bigint AS witnesses,
               max(m.sense_mu) AS sense_mu
        FROM members m
        WHERE m.w <> p_word
        GROUP BY m.w
    )
    SELECT a.w, hl.object_id AS lang, a.mu::bigint, a.witnesses, a.sense_mu
    FROM agg a
    LEFT JOIN consensus hl
      ON hl.subject_id = a.w AND hl.type_id = relation_type_id('HAS_LANGUAGE')
$$;
#line 144 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\inference/synonyms.sql.in"
-- inference/synonyms.sql.in
-- Same-language synset co-members of a word (king -> {male monarch, Rex, dynast, ...}).
-- Shares the synset traversal in synset_members(); ranked by Glicko-2 conservative strength.
CREATE OR REPLACE FUNCTION synonyms(p_word bytea, p_limit int DEFAULT 10)
    RETURNS TABLE(synonym text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    -- Same-language co-members, matched against the source word's PRIMARY language only: the
    -- source lemma is often a cross-language homograph (king = eng AND est "shoe"), and pulling
    -- in every shared tag would leak Estonian/Finnish co-members (kuningas) as English synonyms.
    -- word_language() is the dominant reading; explode in synset_members() means a member counts
    -- iff it actually carries that language. Deduped by surface form, dominant sense first.
    WITH src_lang AS (SELECT word_language(p_word) AS id)
    SELECT t.synonym, t.mu::numeric, t.witnesses
    FROM (
        SELECT render_text(sm.member, 64) AS synonym,
               max(sm.mu) AS mu, max(sm.witnesses) AS witnesses, max(sm.sense_mu) AS sense_mu
        FROM synset_members(p_word) sm
        WHERE sm.lang = (SELECT id FROM src_lang)
        GROUP BY render_text(sm.member, 64)
    ) t
    WHERE NULLIF(btrim(t.synonym), '') IS NOT NULL
    ORDER BY t.sense_mu DESC NULLS LAST, t.mu DESC, t.witnesses DESC, t.synonym
    LIMIT p_limit
$$;
#line 145 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\inference/translations.sql.in"
-- inference/translations.sql.in
-- Translations are EMERGENT, never stored as direct edges. A word reaches its cross-language
-- equivalents through the shared synset's members whose language differs from the source word
-- — king -> {roi(fra), 王(lzh), ราชา(tha), 국왕(kor), konge(dan), ...}. Ranked by Glicko-2 strength.
CREATE OR REPLACE FUNCTION translations(p_word bytea, p_limit int DEFAULT 24)
    RETURNS TABLE(translation text, language text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    -- Distinct foreign equivalents, one row per surface form (a homograph like rei spans 11
    -- Romance languages; we show it once with its best-ranked language rather than 11 near-dup
    -- rows). Excludes the full source language set so neither the English nor a coincidental
    -- same-spelling homograph reading (king=est) is listed. Dominant sense first.
    WITH src_langs AS (
        SELECT c.object_id AS id FROM consensus c
        WHERE c.subject_id = p_word AND c.type_id = relation_type_id('HAS_LANGUAGE')
    ),
    per_word AS (
        SELECT DISTINCT ON (render_text(sm.member, 64))
               render_text(sm.member, 64) AS translation, label(sm.lang) AS language,
               sm.mu, sm.witnesses, sm.sense_mu
        FROM synset_members(p_word) sm
        WHERE sm.lang IS NOT NULL
          AND sm.lang NOT IN (SELECT id FROM src_langs)
        ORDER BY render_text(sm.member, 64), sm.sense_mu DESC NULLS LAST, sm.mu DESC, sm.witnesses DESC
    )
    SELECT t.translation, t.language, t.mu::numeric, t.witnesses
    FROM per_word t
    WHERE NULLIF(btrim(t.translation), '') IS NOT NULL
    ORDER BY t.sense_mu DESC NULLS LAST, t.mu DESC, t.witnesses DESC, t.language
    LIMIT p_limit
$$;
#line 146 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\inference/translate_to.sql.in"
-- inference/translate_to.sql.in
-- Scoped translation for the recall router: resolve the requested language by ISO code (fra)
-- OR human name (French, via HAS_DEFINITION) against the candidate set of the word's synset
-- members, so "how do you say king in french" answers in French specifically. Members are
-- matched on their full HAS_LANGUAGE set (homographs like roi are French AND Dutch AND ...),
-- and ranked dominant-sense first via sense_mu. p_lang NULL/'' => every cross-language member.
CREATE OR REPLACE FUNCTION translate_to(p_word bytea, p_lang text DEFAULT NULL,
                                        p_limit int DEFAULT 24)
    RETURNS TABLE(translation text, language text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    WITH src_langs AS (
        SELECT c.object_id AS id FROM consensus c
        WHERE c.subject_id = p_word AND c.type_id = relation_type_id('HAS_LANGUAGE')
    ),
    want AS (SELECT NULLIF(btrim(lower(COALESCE(p_lang, ''))), '') AS name),
    cand AS (
        SELECT sm.member, sm.lang, sm.mu, sm.witnesses, sm.sense_mu, label(sm.lang) AS code
        FROM synset_members(p_word) sm
        WHERE sm.lang IS NOT NULL
          AND sm.lang NOT IN (SELECT id FROM src_langs)
    )
    SELECT t.tr, t.code, t.mu::numeric, t.witnesses
    FROM (
        SELECT render_text(c.member, 64) AS tr, c.code,
               max(c.mu) AS mu, max(c.witnesses) AS witnesses, max(c.sense_mu) AS sense_mu
        FROM cand c, want w
        WHERE w.name IS NULL
           OR lower(c.code) = w.name
           OR EXISTS (SELECT 1 FROM consensus d
                      WHERE d.subject_id = c.lang
                        AND d.type_id = relation_type_id('HAS_DEFINITION')
                        AND lower(render_text(d.object_id, 32)) = w.name)
        GROUP BY render_text(c.member, 64), c.code
    ) t
    WHERE NULLIF(btrim(t.tr), '') IS NOT NULL
    ORDER BY t.sense_mu DESC NULLS LAST, t.mu DESC, t.witnesses DESC, t.code
    LIMIT p_limit
$$;
#line 147 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\inference/language_coverage.sql.in"
-- inference/language_coverage.sql.in
-- One-line omniglottal coverage summary: across how many (and which) languages a word's
-- synset is witnessed. Returns a single conversational row, or none when nothing is known.
CREATE OR REPLACE FUNCTION language_coverage(p_word bytea, p_limit int DEFAULT 40)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    WITH langs AS (
        SELECT DISTINCT label(sm.lang) AS code
        FROM synset_members(p_word) sm
        WHERE sm.lang IS NOT NULL
        UNION
        SELECT label(word_language(p_word))
    ),
    shown AS (SELECT code FROM langs WHERE code IS NOT NULL ORDER BY code LIMIT p_limit)
    SELECT COALESCE(label(p_word), '?') || ' is witnessed across '
           || (SELECT count(*) FROM langs WHERE code IS NOT NULL)::text || ' languages: '
           || (SELECT string_agg(code, ', ' ORDER BY code) FROM shown)
           || CASE WHEN (SELECT count(*) FROM langs WHERE code IS NOT NULL) > p_limit
                   THEN ', …' ELSE '' END,
           NULL::numeric,
           (SELECT count(*) FROM langs WHERE code IS NOT NULL)::bigint
    WHERE (SELECT count(*) FROM langs WHERE code IS NOT NULL) > 0
$$;
#line 148 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/hypernyms.sql.in"
CREATE OR REPLACE FUNCTION hypernyms(p_word bytea, p_depth int DEFAULT 8)
    RETURNS TABLE(depth int, hypernym text, gloss text)
    LANGUAGE C STABLE STRICT
    AS 'MODULE_PATHNAME', 'pg_laplace_hypernyms';
#line 149 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/resolve_last_word.sql.in"
CREATE OR REPLACE FUNCTION resolve_last_word(p_phrase text)
    RETURNS bytea
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT pw.id FROM prompt_words(p_phrase) pw
    WHERE pw.id IS NOT NULL
    ORDER BY pw.ord DESC
    LIMIT 1
$$;
#line 150 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/resolve_phrase.sql.in"
CREATE OR REPLACE FUNCTION resolve_phrase(p_phrase text)
    RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_laplace_resolve_phrase'
    LANGUAGE C STABLE STRICT;
#line 151 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/prompt_state.sql.in"
CREATE OR REPLACE FUNCTION prompt_state(p_text text)
    RETURNS TABLE(ord int, word text, id bytea, language bytea)
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT pw.ord, pw.word, pw.id, word_language(pw.id)
    FROM prompt_words(p_text) pw
    WHERE pw.id IS NOT NULL
$$;
#line 152 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/first_placed_topic.sql.in"
CREATE OR REPLACE FUNCTION first_placed_topic(p_text text)
    RETURNS bytea
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT p.entity_id
    FROM physicalities p
    JOIN prompt_state(p_text) s ON p.entity_id = s.id
    WHERE p.type = 1 AND p.coord IS NOT NULL
    LIMIT 1
$$;
#line 153 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/realize_path.sql.in"
CREATE OR REPLACE FUNCTION realize_path(p_path bytea[], p_types bytea[],
                                        p_lang bytea DEFAULT NULL)
    RETURNS text
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT string_agg(
               CASE WHEN i = 1 THEN COALESCE(realize(p_path[i], p_lang), '?')
                    ELSE ' —' || COALESCE(type_label(p_types[i-1]), '?') || '→ '
                         || COALESCE(realize(p_path[i], p_lang), '?')
               END, '' ORDER BY i)
    FROM generate_subscripts(p_path, 1) AS i
$$;
#line 154 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/realize_path_with_dirs.sql.in"
CREATE OR REPLACE FUNCTION realize_path(p_path bytea[], p_types bytea[], p_dirs int[],
                                        p_lang bytea DEFAULT NULL)
    RETURNS text
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT string_agg(
               CASE WHEN i = 1 THEN COALESCE(realize(p_path[i], p_lang), '?')
                    ELSE CASE WHEN p_dirs[i-1] = -1
                              THEN ' ←' || COALESCE(type_label(p_types[i-1]), '?') || '— '
                              ELSE ' —' || COALESCE(type_label(p_types[i-1]), '?') || '→ '
                         END || COALESCE(realize(p_path[i], p_lang), '?')
               END, '' ORDER BY i)
    FROM generate_subscripts(p_path, 1) AS i
$$;
#line 155 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/isa_path.sql.in"
CREATE OR REPLACE FUNCTION isa_path(p_x bytea, p_y bytea, p_depth int DEFAULT 8)
    RETURNS TABLE(path bytea[], types bytea[], path_mu numeric)
    LANGUAGE C STABLE STRICT PARALLEL SAFE
    AS 'MODULE_PATHNAME', 'pg_laplace_isa_path';
#line 156 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/epistemic_status.sql.in"
CREATE OR REPLACE FUNCTION epistemic_status(p_word bytea, p_lang bytea DEFAULT NULL,
                                            p_limit int DEFAULT 30)
    RETURNS TABLE(type text, fact text, status text, mu numeric,
                  volatility numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    WITH subj(id) AS (SELECT p_word UNION SELECT sn.synset_id FROM senses(p_word) sn),
         rels AS (
            SELECT c.type_id, c.object_id, c.rating, c.rd, c.volatility, c.witness_count
            FROM consensus c JOIN subj s ON c.subject_id = s.id
            WHERE c.object_id IS NOT NULL
              AND c.type_id NOT IN (relation_type_id('HAS_SENSE'), relation_type_id('IS_SENSE_OF'),
                                    relation_type_id('HAS_LANGUAGE'))
            ORDER BY relation_rank_resolved(c.type_id) * (eff_mu(c.rating, c.rd) / 1e9) DESC
            LIMIT p_limit),
         lang(id) AS (SELECT COALESCE(p_lang, word_language(p_word))),
         objs(a) AS (SELECT array_agg(DISTINCT object_id) FROM rels),
         rendered AS (
            SELECT u.object_id, NULLIF(u.s, '') AS s
            FROM objs CROSS JOIN LATERAL unnest(objs.a, render_text_batch(objs.a, 32)) AS u(object_id, s)
            WHERE objs.a IS NOT NULL
         )
    SELECT type_label(r.type_id),
           COALESCE(rd.s, realize(r.object_id, (SELECT id FROM lang))),
           CASE WHEN refuted(r.rating, r.rd) THEN 'refuted'
                WHEN eff_mu(r.rating, r.rd) > glicko2_neutral_mu() THEN 'confirmed'
                WHEN r.volatility::numeric > glicko2_initial_volatility() * 1.5 THEN 'contested'
                ELSE 'thin' END,
           eff_mu_display(r.rating, r.rd),
           round(r.volatility / 1e9, 4), r.witness_count
    FROM rels r
    LEFT JOIN rendered rd ON rd.object_id = r.object_id
    ORDER BY relation_rank_resolved(r.type_id) * (eff_mu(r.rating, r.rd) / 1e9) DESC
$$;
#line 157 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/contrast.sql.in"
CREATE OR REPLACE FUNCTION contrast(p_x bytea, p_y bytea, p_lang bytea DEFAULT NULL,
                                    p_limit int DEFAULT 80)
    RETURNS TABLE(holder text, type text, fact text, mu numeric)
    LANGUAGE C STABLE PARALLEL SAFE
    AS 'MODULE_PATHNAME', 'pg_laplace_contrast';
#line 158 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/parse_ask.sql.in"
CREATE OR REPLACE FUNCTION parse_ask(p_prompt text)
    RETURNS TABLE(ask_kind text, phrase text, phrase2 text, type_name text)
    LANGUAGE C STABLE STRICT
    AS 'MODULE_PATHNAME', 'pg_laplace_parse_ask';
#line 159 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/resolve_topic.sql.in"
CREATE OR REPLACE FUNCTION resolve_topic(p_phrase text, p_context bytea)
    RETURNS bytea
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT CASE
        WHEN p_phrase IS NULL OR btrim(p_phrase) = '' THEN p_context
        WHEN p_context IS NOT NULL
             AND btrim(lower(p_phrase), ' ?.!') ~ '^(it|its|that|this|they|their|them|those|these|one|he|she|him|her)$'
            THEN p_context
        ELSE COALESCE(resolve_phrase(p_phrase), resolve_last_word(p_phrase))
    END
$$;
#line 160 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/recall.sql.in"
CREATE OR REPLACE FUNCTION recall(p_prompt text, p_context bytea DEFAULT NULL)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE C STABLE
    AS 'MODULE_PATHNAME', 'pg_laplace_recall';
#line 161 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/session_topics.sql.in"
CREATE UNLOGGED TABLE IF NOT EXISTS session_topics (
    session_id bytea NOT NULL,
    ord integer NOT NULL,
    prompt text NOT NULL,
    resolved_id bytea,
    asked_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (session_id, ord)
);
#line 162 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/recall_session.sql.in"
CREATE OR REPLACE FUNCTION recall_session(p_prompt text, p_session bytea DEFAULT NULL)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE C VOLATILE
    AS 'MODULE_PATHNAME', 'pg_laplace_recall_session';
#line 163 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/structural_neighbors.sql.in"
CREATE OR REPLACE FUNCTION structural_neighbors(p_word text, p_k integer DEFAULT 10)
    RETURNS TABLE(neighbor text, geodesic double precision, frechet double precision)
    LANGUAGE plpgsql STABLE
    SET search_path = @extschema@, public AS $sn$
DECLARE
    v_id bytea;
    v_coord geometry;
    v_traj geometry;
BEGIN
    SELECT p.entity_id, p.coord, p.trajectory
      INTO v_id, v_coord, v_traj
    FROM physicalities p
    JOIN prompt_state(p_word) s ON p.entity_id = s.id
    WHERE p.type = 1 AND p.coord IS NOT NULL
    -- geometry is source-free; order by id for determinism (was p.source_id)
    ORDER BY s.ord, p.id
    LIMIT 1;
    IF v_id IS NULL THEN RETURN; END IF;

    RETURN QUERY
    WITH knn AS (
        SELECT p.entity_id, p.coord, p.trajectory
        FROM physicalities p
        WHERE p.type = 1
        ORDER BY p.coord <<->> v_coord
        LIMIT GREATEST(p_k * 20, 200)),
    dedup AS (
        SELECT DISTINCT ON (k.entity_id) k.entity_id, k.trajectory,
               public.laplace_angular_distance_4d(k.coord, v_coord) AS geo
        FROM knn k
        ORDER BY k.entity_id, public.laplace_angular_distance_4d(k.coord, v_coord)),
    nearest AS (
        SELECT entity_id, trajectory, geo
        FROM dedup
        WHERE entity_id <> v_id
        ORDER BY geo
        LIMIT p_k)
    SELECT render_text(t.entity_id, 24), t.geo,
           CASE WHEN t.trajectory IS NOT NULL AND v_traj IS NOT NULL
                THEN public.laplace_frechet_4d(t.trajectory, v_traj) END
    FROM nearest t
    ORDER BY t.geo;
END;
$sn$;
#line 164 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/structural_locale.sql.in"
CREATE OR REPLACE FUNCTION structural_locale(p_word text, p_near double precision DEFAULT 0.05)
    RETURNS TABLE(nearest double precision, within_near bigint,
                  within_2x bigint, within_5x bigint, isolated boolean)
    LANGUAGE plpgsql STABLE
    SET search_path = @extschema@, public AS $sl$
DECLARE
    v_id bytea;
    v_coord geometry;
BEGIN
    SELECT p.entity_id, p.coord INTO v_id, v_coord
    FROM physicalities p
    JOIN prompt_state(p_word) s ON p.entity_id = s.id
    WHERE p.type = 1 AND p.coord IS NOT NULL
    -- geometry is source-free; order by id for determinism (was p.source_id)
    ORDER BY s.ord, p.id
    LIMIT 1;
    IF v_id IS NULL THEN RETURN; END IF;

    RETURN QUERY
    WITH cand AS (
        SELECT p.entity_id, p.coord
        FROM physicalities p
        WHERE p.type = 1
        ORDER BY p.coord <<->> v_coord
        LIMIT 3000),
    g AS (
        SELECT DISTINCT ON (c.entity_id) c.entity_id,
               public.laplace_angular_distance_4d(c.coord, v_coord) AS d
        FROM cand c
        WHERE c.entity_id <> v_id
        ORDER BY c.entity_id, public.laplace_angular_distance_4d(c.coord, v_coord))
    SELECT min(d),
           count(*) FILTER (WHERE d <= p_near),
           count(*) FILTER (WHERE d <= 2 * p_near),
           count(*) FILTER (WHERE d <= 5 * p_near),
           COALESCE(min(d) > p_near, true)
    FROM g;
END;
$sl$;
#line 165 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/links.sql.in"
CREATE OR REPLACE FUNCTION links(p_word text)
    RETURNS TABLE(relation text, target text, strength double precision)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT COALESCE(NULLIF(relation_canonical(c.type_id), ''), label(c.type_id)) AS relation,
           label(c.object_id) AS target,
           (eff_mu(c.rating, c.rd))::float8 / 1e9 AS strength
    FROM consensus c
    WHERE c.subject_id = word_id(p_word)
    -- rank-weighted: order by relation salience × strength so semantically meaningful links
    -- (IS_A/HAS_DEFINITION/SYNONYM) surface above grammatical scaffolding (HAS_LANGUAGE/HAS_POS/
    -- PRECEDES). The returned `strength` column is unchanged (raw eff_mu); only the order changes.
    ORDER BY relation_rank_resolved(c.type_id) * (eff_mu(c.rating, c.rd) / 1e9) DESC,
             strength DESC, relation
$$;
#line 166 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\inference/attention.sql.in"
-- inference/attention.sql.in
-- The SQL-transformer attention head. A query word attends over the entities it is
-- consensus-connected to (plus its synset co-members); the attention WEIGHT is the Glicko-2
-- conservative strength eff_mu(rating, rd) — this is what replaces geometric nearest-neighbor,
-- which on the densely-packed 3-sphere is semantically noisy. The S3 position (angular
-- geodesic between the two coords on the glome) is reported alongside as the geometric
-- annotation, fusing the embedding geometry with the consensus attention in one read.
CREATE OR REPLACE FUNCTION attention(p_word bytea, p_k int DEFAULT 12)
    RETURNS TABLE(neighbor text, relation text, attention double precision,
                  geodesic double precision)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    WITH qcoord AS (
        SELECT coord FROM physicalities WHERE entity_id = p_word AND type = 1 LIMIT 1),
    edges AS (
        SELECT c.object_id AS nb, c.type_id, eff_mu(c.rating, c.rd) AS mu
        FROM consensus c
        WHERE c.subject_id = p_word AND c.object_id IS NOT NULL
          -- exclude the positional/grammatical scaffolding (sequence order, POS, morphology,
          -- aliasing): those are the substrate's "positional encoding", not semantic attention.
          AND c.type_id NOT IN (relation_type_id('HAS_LANGUAGE'),
                                relation_type_id('HAS_SENSE'), relation_type_id('IS_SENSE_OF'),
                                relation_type_id('PRECEDES'), relation_type_id('FOLLOWS'),
                                relation_type_id('HAS_POS'), relation_type_id('HAS_XPOS'),
                                relation_type_id('HAS_FEATURE'), relation_type_id('HAS_LEX_CATEGORY'),
                                relation_type_id('HAS_NAME_ALIAS'), relation_type_id('IS_LEMMA_OF'),
                                relation_type_id('HAS_LANGUAGE_SCOPE'), relation_type_id('HAS_LANGUAGE_TYPE'))
        UNION ALL
        SELECT sm.member, relation_type_id('IS_SYNONYM_OF'), sm.mu
        FROM synset_members(p_word) sm
    ),
    best AS (
        SELECT nb, (array_agg(type_id ORDER BY mu DESC))[1] AS type_id, max(mu) AS mu
        FROM edges WHERE nb <> p_word GROUP BY nb)
    SELECT t.neighbor, t.relation, t.attention, t.geodesic
    FROM (
        SELECT label(b.nb) AS neighbor, type_label(b.type_id) AS relation,
               (b.mu::double precision / 1e9) AS attention,
               CASE WHEN qc.coord IS NOT NULL AND p.coord IS NOT NULL
                    THEN laplace_angular_distance_4d(p.coord, qc.coord) END AS geodesic
        FROM best b
        LEFT JOIN physicalities p ON p.entity_id = b.nb AND p.type = 1
        CROSS JOIN qcoord qc
    ) t
    WHERE NULLIF(btrim(t.neighbor), '') IS NOT NULL
    ORDER BY t.attention DESC NULLS LAST, t.geodesic NULLS LAST
    LIMIT p_k
$$;
#line 167 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/converse/correlate.sql.in"
CREATE OR REPLACE FUNCTION correlate(p_words text[])
    RETURNS TABLE(relation text, target text, words text[], strength double precision)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    WITH v AS (SELECT w AS word, word_id(w) AS id FROM unnest(p_words) AS w WHERE word_id(w) IS NOT NULL),
    e AS (
        SELECT v.word,
               COALESCE(NULLIF(relation_canonical(c.type_id), ''), label(c.type_id)) AS relation,
               label(c.object_id) AS target,
               (eff_mu(c.rating, c.rd))::float8 / 1e9 AS w
        FROM v JOIN consensus c ON c.subject_id = v.id
    )
    SELECT relation, target, array_agg(DISTINCT word ORDER BY word) AS words, max(w) AS strength
    FROM e GROUP BY relation, target
    ORDER BY count(DISTINCT word) DESC, max(w) DESC, relation
$$;
#line 168 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\seed/canonical_names_seed.sql.in"
INSERT INTO canonical_names (id, name)
SELECT canonical_id(v.name), v.name
FROM (VALUES
    ('substrate/atomic/none/v1'),
    ('substrate/entity/Architecture_Llama/v1'),
    ('ATTENDS'),
    ('CANONICAL_DECOMPOSES_TO'),
    ('CAPTIONS'),
    ('COMPLETES_TO'),
    ('CO_OCCURS_WITH'),
    ('DEPICTS'),
    ('FOLLOWS'),
    ('HAS_BLOCK'),
    ('HAS_COMBINING_CLASS'),
    ('HAS_GENERAL_CATEGORY'),
    ('HAS_HIDDEN_SIZE'),
    ('HAS_INTERMEDIATE_SIZE'),
    ('HAS_ISO639_1_CODE'),
    ('HAS_LANGUAGE'),
    ('HAS_LOWERCASE_MAPPING'),
    ('HAS_NUM_HEADS'),
    ('HAS_NUM_KV_HEADS'),
    ('HAS_NUM_LAYERS'),
    ('HAS_PART'),
    ('HAS_SCRIPT'),
    ('HAS_TRUST_CLASS'),
    ('HAS_UPPERCASE_MAPPING'),
    ('HAS_VARIANT_OF'),
    ('HAS_VOCAB_SIZE'),
    ('IS_A'),
    ('IS_ALIAS_OF'),
    ('IS_HYPERNYM_OF'),
    ('IS_LANGUAGE_CODE'),
    ('IS_LOSSY_ENCODING_OF'),
    ('IS_REPLACED_BY'),
    ('IS_TRANSLATION_OF'),
    ('MEMBER_OF_MACROLANGUAGE'),
    ('NORMALIZES'),
    ('OCCURS_IN_CONTEXT'),
    ('OV_RELATES'),
    ('PRECEDES'),
    ('SIMILAR_TO'),
    ('TOKEN_MAPS_TO'),
    ('TRANSCRIBES_AS'),
    ('USES_SCRIPT'),
    ('substrate/type_tier/T10_ScalarValued/v1'),
    ('substrate/type_tier/T11_Probationary/v1'),
    ('substrate/type_tier/T1_Mandate/v1'),
    ('substrate/type_tier/T2_StandardsStructural/v1'),
    ('substrate/type_tier/T3_Taxonomic/v1'),
    ('substrate/type_tier/T4_Partitive/v1'),
    ('substrate/type_tier/T5_Causal/v1'),
    ('substrate/type_tier/T6_Equivalence/v1'),
    ('substrate/type_tier/T7_Oppositional/v1'),
    ('substrate/type_tier/T8_Associative/v1'),
    ('substrate/type_tier/T9_TensorCalculation/v1'),
    ('substrate/physicality_type/BUILDING_BLOCK/v1'),
    ('substrate/physicality_type/CONTENT/v1'),
    ('substrate/physicality_type/PROJECTION/v1'),
    ('substrate/physicality_type/PROJECTION_OUTPUT/v1'),
    ('substrate/source/OpenSubtitlesDecomposer/v1'),
    ('substrate/source/PropBankDecomposer/v1'),
    ('substrate/source/VerbNetDecomposer/v1'),
    ('substrate/source/FrameNetDecomposer/v1'),
    ('substrate/source/SemLinkDecomposer/v1'),
    ('substrate/source/CodeDecomposer/v1'),
    ('substrate/source/RepoDecomposer/v1'),
    ('substrate/source/TinyCodesDecomposer/v1'),
    ('substrate/source/StackDecomposer/v1'),
    ('substrate/source/Atomic2020Decomposer/v1'),
    ('substrate/source/AudioDecomposer/v1'),
    ('substrate/source/ConceptNetDecomposer/v1'),
    ('substrate/source/ISO639Decomposer/v1'),
    ('substrate/source/ImageDecomposer/v1'),
    ('substrate/source/OMWDecomposer/v1'),
    ('substrate/source/SubstrateCanonical/v1'),
    ('substrate/source/SyntheticBatched/v1'),
    ('substrate/source/Response/v1'),
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
    ('substrate/sql/schema/tables/attestations.sql.in'),
    ('substrate/sql/functions/glicko2/laplace_glicko2_aggregate.sql.in'),
    ('substrate/sql/schema/tables/consensus.sql.in'),
    ('substrate/sql/manifest.install'),
    ('substrate/src/laplace_substrate.c'),
    ('substrate/tests/CMakeLists.txt'),
    ('substrate/trust_class/AIModelProbe/v1'),
    ('substrate/trust_class/AcademicCurated/v1'),
    ('substrate/trust_class/AcademicCuratedWithUserInput/v1'),
    ('substrate/trust_class/AdversarialUntrusted/v1'),
    ('substrate/trust_class/AppDerived/v1'),
    ('substrate/trust_class/ResponseContent/v1'),
    ('substrate/trust_class/StandardsDerived/v1'),
    ('substrate/trust_class/StructuredCorpus/v1'),
    ('substrate/trust_class/SubstrateMandate/v1'),
    ('substrate/trust_class/UserCuratedResource/v1'),
    ('substrate/trust_class/UserPromptContent/v1'),
    ('Architecture'),
    ('Atomic_Marker'),
    ('Atomic_Split'),
    ('Codepoint'),
    ('Document'),
    ('FoldingTestFixture'),
    ('Grapheme'),
    ('ISO639Code'),
    ('RelationType'),
    ('Language'),
    ('Model_Layer'),
    ('Model_Recipe'),
    ('Model_Tokenizer'),
    ('Ngram'),
    ('OrdinalContext'),
    ('PhysicalityType'),
    ('Scalar'),
    ('Sentence'),
    ('Source'),
    ('Tatoeba_Sentence'),
    ('TestFixture'),
    ('Text'),
    ('Type'),
    ('UD_Feature'),
    ('UD_UPOS'),
    ('UD_XPOS'),
    ('UcdClassifier'),
    ('Wiktionary_POS'),
    ('Word'),
    ('WordNet_LexCategory'),
    ('WordNet_POS'),
    ('WordNet_Sense'),
    ('WordNet_Synset'),
    ('ADJACENT_TO_PIXEL'),
    ('ALSO_SEE'),
    ('AT_LOCATION'),
    ('CAPABLE_OF'),
    ('CAUSES'),
    ('CAUSES_DESIRE'),
    ('CREATED_BY'),
    ('DEFINED_AS'),
    ('DEFINES'),
    ('DEPENDS_ON'),
    ('DERIVATIONALLY_RELATED'),
    ('DERIVED_FROM'),
    ('DESIRES'),
    ('DISTINCT_FROM'),
    ('ENTAILS'),
    ('ETYMOLOGICALLY_DERIVED_FROM'),
    ('ETYMOLOGICALLY_RELATED_TO'),
    ('FORM_OF'),
    ('HAS_A'),
    ('HAS_ATTRIBUTE'),
    ('HAS_CONTEXT'),
    ('HAS_DOMAIN_REGION'),
    ('HAS_DOMAIN_TOPIC'),
    ('HAS_DOMAIN_USAGE'),
    ('HAS_ETYMOLOGY'),
    ('HAS_EXAMPLE'),
    ('HAS_EXTERNAL_ID'),
    ('HAS_FEATURE'),
    ('HAS_FIRST_SUBEVENT'),
    ('HAS_HYPERNYM'),
    ('HAS_HYPONYM'),
    ('HAS_INSTANCE'),
    ('HAS_LAST_SUBEVENT'),
    ('HAS_LEX_CATEGORY'),
    ('HAS_MEMBER'),
    ('HAS_POS'),
    ('HAS_PREREQUISITE'),
    ('HAS_PROPERTY'),
    ('HAS_SENSE'),
    ('HAS_SENSE_OF'),
    ('HAS_SUBEVENT'),
    ('HAS_SUBSTANCE'),
    ('HAS_UPOS'),
    ('HAS_XPOS'),
    ('IN_VERB_GROUP_WITH'),
    ('IS_ANTONYM_OF'),
    ('IS_AT_SAMPLE'),
    ('IS_DOMAIN_REGION_MEMBER'),
    ('IS_DOMAIN_TOPIC_MEMBER'),
    ('IS_DOMAIN_USAGE_MEMBER'),
    ('IS_HYPONYM_OF'),
    ('IS_INSTANCE_OF'),
    ('IS_LEMMA_OF'),
    ('IS_MEMBER_OF'),
    ('IS_PARTICIPLE_OF'),
    ('IS_PART_OF'),
    ('IS_PIXEL_OF'),
    ('IS_SENSE_OF'),
    ('IS_SIMILAR_TO'),
    ('IS_SUBSTANCE_OF'),
    ('IS_SYNONYM_OF'),
    ('LOCATED_NEAR'),
    ('MADE_OF'),
    ('MANNER_OF'),
    ('MOTIVATED_BY_GOAL'),
    ('NOT_CAPABLE_OF'),
    ('NOT_DESIRES'),
    ('NOT_HAS_PROPERTY'),
    ('NOT_USED_FOR'),
    ('OBSTRUCTED_BY'),
    ('PERTAINS_TO'),
    ('RECEIVES_ACTION'),
    ('RELATED_TO'),
    ('SYMBOL_OF'),
    ('USED_FOR'),
    ('NORMALIZES_TO'),
    ('HAS_DEFINITION'),
    ('MERGES_WITH'),
    ('X_INTENT'),
    ('X_NEED'),
    ('X_WANT'),
    ('X_EFFECT'),
    ('X_REACT'),
    ('X_ATTR'),
    ('X_REASON'),
    ('X_FILLED_BY'),
    ('O_EFFECT'),
    ('O_REACT'),
    ('O_WANT'),
    ('IS_AFTER'),
    ('IS_BEFORE'),
    ('OBJECT_USE'),
    ('MADE_UP_OF'),
    ('IS_COORDINATE_TERM_WITH'),
    ('HAS_USAGE_REGISTER'),
    ('HAS_VERB_FRAME'),
    ('HAS_DBPEDIA_RELATION'),
    ('ENHANCED_DEPENDS_ON'),
    ('HAS_TITLECASE_MAPPING'),
    ('COMPATIBILITY_DECOMPOSES_TO'),
    ('HAS_NUMERIC_VALUE'),
    ('HAS_BIDI_CLASS'),
    ('HAS_MIRROR'),
    ('HAS_AGE'),
    ('HAS_NAME_ALIAS'),
    ('CONFUSABLE_WITH'),
    ('HAS_EMOJI_PROPERTY'),
    ('HAS_ISO639_2_CODE'),
    ('HAS_LANGUAGE_SCOPE'),
    ('HAS_LANGUAGE_TYPE'),
    ('POS'),
    ('substrate/pos/ADJ/v1'),
    ('substrate/pos/ADP/v1'),
    ('substrate/pos/ADV/v1'),
    ('substrate/pos/AUX/v1'),
    ('substrate/pos/CCONJ/v1'),
    ('substrate/pos/DET/v1'),
    ('substrate/pos/INTJ/v1'),
    ('substrate/pos/NOUN/v1'),
    ('substrate/pos/NUM/v1'),
    ('substrate/pos/PART/v1'),
    ('substrate/pos/PRON/v1'),
    ('substrate/pos/PROPN/v1'),
    ('substrate/pos/PUNCT/v1'),
    ('substrate/pos/SCONJ/v1'),
    ('substrate/pos/SYM/v1'),
    ('substrate/pos/VERB/v1'),
    ('substrate/pos/X/v1'),
    ('substrate/iso639/scope/I/v1'),
    ('substrate/iso639/scope/M/v1'),
    ('substrate/iso639/scope/S/v1'),
    ('substrate/iso639/type/L/v1'),
    ('substrate/iso639/type/E/v1'),
    ('substrate/iso639/type/A/v1'),
    ('substrate/iso639/type/H/v1'),
    ('substrate/iso639/type/C/v1'),
    ('substrate/iso639/type/S/v1'),
    ('Byte'),
    ('substrate/utf8/continuation/v1'), ('substrate/utf8/lead2/v1'),
    ('substrate/utf8/lead3/v1'), ('substrate/utf8/lead4/v1'),
    ('substrate/utf8/invalid/v1'),
    ('substrate/encoding/ISO-8859-1/v1'), ('substrate/encoding/windows-1252/v1'),
    ('DECODES_TO'), ('HAS_UTF8_ROLE'),
    ('unicode/emoji/Emoji/v1'),
    ('unicode/emoji/Emoji_Presentation/v1'),
    ('unicode/emoji/Emoji_Modifier/v1'),
    ('unicode/emoji/Emoji_Modifier_Base/v1'),
    ('unicode/emoji/Emoji_Component/v1'),
    ('unicode/emoji/Extended_Pictographic/v1')
) AS v(name)
ON CONFLICT (id) DO NOTHING;
#line 169 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\generated/seed_relation_types.sql.in"
INSERT INTO canonical_names (id, name)
SELECT canonical_id(v.name), v.name
FROM (VALUES
    ('ADJACENT_TO_PIXEL'),
    ('ALSO_SEE'),
    ('ATTENDS'),
    ('AT_LOCATION'),
    ('BORROWED_FROM'),
    ('CALLS'),
    ('CANONICAL_DECOMPOSES_TO'),
    ('CAPABLE_OF'),
    ('CAPTIONS'),
    ('CAUSATIVE_OF'),
    ('CAUSES'),
    ('CAUSES_DESIRE'),
    ('COMPATIBILITY_DECOMPOSES_TO'),
    ('COMPLETES_TO'),
    ('CONFUSABLE_WITH'),
    ('CONTAINS'),
    ('CONTINUES_TO'),
    ('CORRESPONDS_TO'),
    ('CREATED_BY'),
    ('DECODES_TO'),
    ('DEFINED_AS'),
    ('DEFINES'),
    ('DEPENDS_ON'),
    ('DEPICTS'),
    ('DERIVATIONALLY_RELATED'),
    ('DERIVED_FROM'),
    ('DESIRES'),
    ('DISTINCT_FROM'),
    ('ENHANCED_DEPENDS_ON'),
    ('ENTAILS'),
    ('ETYMOLOGICALLY_DERIVED_FROM'),
    ('ETYMOLOGICALLY_RELATED_TO'),
    ('EVOKES_FRAME'),
    ('EXCLUDES'),
    ('FOLLOWS'),
    ('FORM_OF'),
    ('FRAME_USES'),
    ('HAS_A'),
    ('HAS_AGE'),
    ('HAS_ATTRIBUTE'),
    ('HAS_BIDI_CLASS'),
    ('HAS_BLOCK'),
    ('HAS_COMBINING_CLASS'),
    ('HAS_CONTEXT'),
    ('HAS_DBPEDIA_RELATION'),
    ('HAS_DEFINITION'),
    ('HAS_DOMAIN_REGION'),
    ('HAS_DOMAIN_TOPIC'),
    ('HAS_DOMAIN_USAGE'),
    ('HAS_EAST_ASIAN_WIDTH'),
    ('HAS_EMOJI_PROPERTY'),
    ('HAS_ETYMOLOGY'),
    ('HAS_EXAMPLE'),
    ('HAS_EXTERNAL_ID'),
    ('HAS_FEATURE'),
    ('HAS_FIRST_SUBEVENT'),
    ('HAS_FRAME_ELEMENT'),
    ('HAS_GENERAL_CATEGORY'),
    ('HAS_HYPERNYM'),
    ('HAS_HYPONYM'),
    ('HAS_INSTANCE'),
    ('HAS_ISO639_1_CODE'),
    ('HAS_ISO639_2B_CODE'),
    ('HAS_ISO639_2T_CODE'),
    ('HAS_ISO639_2_CODE'),
    ('HAS_JOINING_TYPE'),
    ('HAS_LANGUAGE'),
    ('HAS_LANGUAGE_SCOPE'),
    ('HAS_LANGUAGE_TYPE'),
    ('HAS_LAST_SUBEVENT'),
    ('HAS_LEX_CATEGORY'),
    ('HAS_LINE_BREAK'),
    ('HAS_LOWERCASE_MAPPING'),
    ('HAS_MEMBER'),
    ('HAS_MIRROR'),
    ('HAS_NAME'),
    ('HAS_NAME_ALIAS'),
    ('HAS_NUMERIC_TYPE'),
    ('HAS_NUMERIC_VALUE'),
    ('HAS_PART'),
    ('HAS_POS'),
    ('HAS_PREREQUISITE'),
    ('HAS_PROPERTY'),
    ('HAS_SCRIPT'),
    ('HAS_SEMANTIC_ROLE'),
    ('HAS_SENSE'),
    ('HAS_SENSE_OF'),
    ('HAS_SUBEVENT'),
    ('HAS_SUBSTANCE'),
    ('HAS_SYNSET_KEY'),
    ('HAS_THEMATIC_ROLE'),
    ('HAS_TITLECASE_MAPPING'),
    ('HAS_UPOS'),
    ('HAS_UPPERCASE_MAPPING'),
    ('HAS_USAGE_REGISTER'),
    ('HAS_UTF8_ROLE'),
    ('HAS_VALENCE_PATTERN'),
    ('HAS_VARIANT_OF'),
    ('HAS_VERB_FRAME'),
    ('HAS_XPOS'),
    ('HINDERED_BY'),
    ('INCHOATIVE_OF'),
    ('INHERITED_FROM'),
    ('INHERITS_FROM'),
    ('IN_VERB_GROUP_WITH'),
    ('IS_A'),
    ('IS_AFTER'),
    ('IS_ANTONYM_OF'),
    ('IS_AT_SAMPLE'),
    ('IS_BEFORE'),
    ('IS_COORDINATE_TERM_WITH'),
    ('IS_DOMAIN_REGION_MEMBER'),
    ('IS_DOMAIN_TOPIC_MEMBER'),
    ('IS_DOMAIN_USAGE_MEMBER'),
    ('IS_FILLED_BY'),
    ('IS_HYPERNYM_OF'),
    ('IS_HYPONYM_OF'),
    ('IS_INHERITED_BY'),
    ('IS_INSTANCE_OF'),
    ('IS_LANGUAGE_CODE'),
    ('IS_LEMMA_OF'),
    ('IS_MEMBER_OF'),
    ('IS_PARTICIPLE_OF'),
    ('IS_PART_OF'),
    ('IS_PIXEL_OF'),
    ('IS_SENSE_OF'),
    ('IS_SIMILAR_TO'),
    ('IS_SUBSTANCE_OF'),
    ('IS_SYNONYM_OF'),
    ('IS_TRANSLATION_OF'),
    ('IS_TYPED_AS'),
    ('LOCATED_NEAR'),
    ('MADE_OF'),
    ('MADE_UP_OF'),
    ('MANNER_OF'),
    ('MEMBER_OF_MACROLANGUAGE'),
    ('MEMBER_OF_VERBNET_CLASS'),
    ('MERGES_WITH'),
    ('MOTIVATED_BY_GOAL'),
    ('NORMALIZES_TO'),
    ('NOT_CAPABLE_OF'),
    ('NOT_DESIRES'),
    ('NOT_HAS_PROPERTY'),
    ('NOT_USED_FOR'),
    ('OBJECT_USE'),
    ('OBSTRUCTED_BY'),
    ('OV_RELATES'),
    ('O_EFFECT'),
    ('O_REACT'),
    ('O_WANT'),
    ('PERSPECTIVE_ON'),
    ('PERTAINS_TO'),
    ('PRECEDES'),
    ('RECEIVES_ACTION'),
    ('REFERENCES'),
    ('RELATED_TO'),
    ('REQUIRES'),
    ('ROLE_CORRESPONDS_TO'),
    ('SIMILAR_TO'),
    ('SUBFRAME_OF'),
    ('SUPERSEDED_BY'),
    ('SYMBOL_OF'),
    ('TOKEN_MAPS_TO'),
    ('TRANSCRIBES_AS'),
    ('USED_FOR'),
    ('USES_SCRIPT'),
    ('USES_SCRIPT_EXTENSION'),
    ('X_ATTR'),
    ('X_EFFECT'),
    ('X_FILLED_BY'),
    ('X_INTENT'),
    ('X_NEED'),
    ('X_REACT'),
    ('X_REASON'),
    ('X_WANT')
) AS v(name)
ON CONFLICT (id) DO NOTHING;
#line 170 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\generated/seed_pos.sql.in"
INSERT INTO canonical_names (id, name)
SELECT canonical_id(v.name), v.name
FROM (VALUES
    ('substrate/pos/ADJ/v1'),
    ('substrate/pos/ADP/v1'),
    ('substrate/pos/ADV/v1'),
    ('substrate/pos/AUX/v1'),
    ('substrate/pos/CCONJ/v1'),
    ('substrate/pos/DET/v1'),
    ('substrate/pos/INTJ/v1'),
    ('substrate/pos/NOUN/v1'),
    ('substrate/pos/NUM/v1'),
    ('substrate/pos/PART/v1'),
    ('substrate/pos/PRON/v1'),
    ('substrate/pos/PROPN/v1'),
    ('substrate/pos/PUNCT/v1'),
    ('substrate/pos/SCONJ/v1'),
    ('substrate/pos/SYM/v1'),
    ('substrate/pos/VERB/v1'),
    ('substrate/pos/X/v1')
) AS v(name)
ON CONFLICT (id) DO NOTHING;
#line 171 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/cascade/astar_path.sql.in"
CREATE OR REPLACE FUNCTION astar_path(
    p_start bytea, p_goals bytea[], p_max_depth int DEFAULT 7,
    p_types bytea[] DEFAULT NULL, p_directed bool DEFAULT false)
    RETURNS TABLE(step int, entity_id bytea, g double precision)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT * FROM astar_path_raw(p_start, p_goals,
        COALESCE(p_types, ARRAY[
            relation_type_id('IS_A'), relation_type_id('IS_INSTANCE_OF'), relation_type_id('IS_SYNONYM_OF'),
            relation_type_id('IS_SIMILAR_TO'), relation_type_id('HAS_PART'), relation_type_id('HAS_MEMBER'),
            relation_type_id('HAS_SUBSTANCE'), relation_type_id('DERIVATIONALLY_RELATED'),
            relation_type_id('PERTAINS_TO'), relation_type_id('ALSO_SEE'), relation_type_id('IS_ANTONYM_OF'),
            relation_type_id('HAS_ATTRIBUTE'), relation_type_id('IN_VERB_GROUP_WITH')]),
        p_max_depth, p_directed)
$$;
#line 172 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/cascade/cascade.sql.in"
CREATE OR REPLACE FUNCTION cascade(p_x text, p_y text, p_max_depth int DEFAULT 7)
    RETURNS TABLE(chain text, cost double precision)
    LANGUAGE C STABLE STRICT PARALLEL SAFE
    AS 'MODULE_PATHNAME', 'pg_laplace_cascade';
#line 173 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/structural/word_curve.sql.in"
CREATE OR REPLACE FUNCTION word_curve(p_word bytea) RETURNS geometry
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT public.ST_MakeLine(p.coord ORDER BY c.ordinal)
    FROM constituents(p_word) c
    JOIN physicalities p ON p.entity_id = c.child_id AND p.type = 1
$$;
#line 174 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/structural/word_shape_distance.sql.in"
CREATE OR REPLACE FUNCTION word_shape_distance(p_a text, p_b text) RETURNS double precision
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT public.laplace_frechet_4d(word_curve(word_id(p_a)), word_curve(word_id(p_b)))
$$;
#line 175 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/structural/anagrams_of.sql.in"
CREATE OR REPLACE FUNCTION anagrams_of(p_word text)
    RETURNS TABLE(word text, entity_id bytea)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT DISTINCT render(p2.entity_id), p2.entity_id
    FROM physicalities p1
    JOIN physicalities p2 ON p2.hilbert_index = p1.hilbert_index AND p2.type = 1
    JOIN entities e ON e.id = p2.entity_id
    WHERE p1.entity_id = word_id(p_word) AND p1.type = 1
      AND e.type_id = canonical_id('Word')
      AND p2.entity_id <> p1.entity_id
$$;
#line 176 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/structural/collocates.sql.in"
CREATE OR REPLACE FUNCTION collocates(p_word text, p_limit int DEFAULT 12)
    RETURNS TABLE(next_word text, mu numeric, witnesses bigint)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT render(c.object_id), eff_mu_display(c.rating, c.rd), c.witness_count
    FROM consensus c
    WHERE c.subject_id = word_id(p_word)
      AND c.type_id = relation_type_id('PRECEDES')
      AND NOT refuted(c.rating, c.rd)
    ORDER BY eff_mu(c.rating, c.rd) DESC
    LIMIT p_limit
$$;
#line 177 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/structural/entity_curve.sql.in"
CREATE OR REPLACE FUNCTION entity_curve(p_entity bytea) RETURNS geometry
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT word_curve(p_entity)
$$;
#line 178 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/structural/nearest_neighbors_4d.sql.in"
CREATE OR REPLACE FUNCTION nearest_neighbors_4d(p_word text, p_k integer DEFAULT 10)
    RETURNS TABLE(neighbor text, geodesic double precision, frechet double precision)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT neighbor, geodesic, frechet FROM structural_neighbors(p_word, p_k)
$$;
#line 179 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/structural/consensus_export_relations.sql.in"
CREATE OR REPLACE FUNCTION consensus_export_relations(p_type_id bytea)
    RETURNS TABLE(subject_id bytea, object_id bytea, rating bigint, witness_count bigint)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT c.subject_id, c.object_id, c.rating, c.witness_count
    FROM consensus c
    WHERE c.type_id = p_type_id AND c.object_id IS NOT NULL
$$;
#line 180 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/structural/consensus_export_unary.sql.in"
CREATE OR REPLACE FUNCTION consensus_export_unary(p_type_id bytea)
    RETURNS TABLE(subject_id bytea, rating bigint, witness_count bigint)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT c.subject_id, c.rating, c.witness_count
    FROM consensus c
    WHERE c.type_id = p_type_id AND c.object_id IS NULL
$$;
#line 181 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/structural/consensus_export_relations_mu.sql.in"
CREATE OR REPLACE FUNCTION consensus_export_relations_mu(p_type_id bytea)
    RETURNS TABLE(subject_id bytea, object_id bytea, eff_mu_fp bigint)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT c.subject_id, c.object_id, eff_mu(c.rating, c.rd)
    FROM consensus c
    WHERE c.type_id = p_type_id AND c.object_id IS NOT NULL
$$;
#line 182 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/structural/model_recipes.sql.in"
CREATE OR REPLACE FUNCTION model_recipes()
    RETURNS TABLE(recipe_id bytea, recipe_json text, first_observed_by bytea)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    SELECT e.id, cn.name, e.first_observed_by
    FROM entities e
    JOIN canonical_names cn ON cn.id = e.id
    WHERE e.type_id = canonical_id('Model_Recipe')
$$;
#line 183 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/lexical/grapheme_case_target.sql.in"
CREATE OR REPLACE FUNCTION grapheme_case_target(p_grapheme_id bytea, p_map text)
    RETURNS bytea
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT c.object_id
    FROM consensus c
    WHERE c.subject_id = p_grapheme_id
      AND c.type_id = CASE p_map
            WHEN 'lower' THEN relation_type_id('HAS_LOWERCASE_MAPPING')
            WHEN 'upper' THEN relation_type_id('HAS_UPPERCASE_MAPPING')
            WHEN 'title' THEN relation_type_id('HAS_TITLECASE_MAPPING')
          END
      AND NOT refuted(c.rating, c.rd)
    ORDER BY eff_mu(c.rating, c.rd) DESC
    LIMIT 1
$$;
#line 184 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/lexical/word_case_map_surface.sql.in"
CREATE OR REPLACE FUNCTION word_case_map_surface(p_word bytea, p_map text)
    RETURNS text
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT string_agg(
               repeat(
                   COALESCE(render_text(grapheme_case_target(c.child_id, p_map)),
                            render_text(c.child_id)),
                   GREATEST(c.run_length, 1)),
               '' ORDER BY c.ordinal)
    FROM constituents(p_word) c
$$;
#line 185 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/lexical/word_case_variant_ids.sql.in"
CREATE OR REPLACE FUNCTION word_case_variant_ids(p_word bytea)
    RETURNS bytea[]
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT COALESCE(array_agg(DISTINCT q.id ORDER BY q.id), ARRAY[p_word])
    FROM (
        SELECT p_word AS id
        UNION
        SELECT word_id(v.surface)
        FROM (
            SELECT word_case_map_surface(p_word, 'lower') AS surface
            UNION ALL
            SELECT word_case_map_surface(p_word, 'upper')
            UNION ALL
            SELECT word_case_map_surface(p_word, 'title')
        ) v
        WHERE v.surface IS NOT NULL
          AND v.surface IS DISTINCT FROM render_text(p_word)
    ) q
    WHERE q.id IS NOT NULL
      AND (q.id = p_word OR entity_exists(q.id))
$$;
#line 186 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/lexical/word_case_class_surface.sql.in"
CREATE OR REPLACE FUNCTION word_case_class_surface(p_word bytea)
    RETURNS text
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT word_case_map_surface(p_word, 'lower')
$$;
#line 187 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/lexical/word_shape_peers.sql.in"
CREATE OR REPLACE FUNCTION word_shape_peers(
        p_word bytea,
        p_frechet_max double precision DEFAULT 0.02)
    RETURNS bytea[]
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT COALESCE(array_agg(k.entity_id ORDER BY k.ang, k.fr), ARRAY[]::bytea[])
    FROM (
        SELECT p2.entity_id,
               public.laplace_frechet_4d(p2.trajectory, me.trajectory) AS fr,
               public.laplace_angular_distance_4d(p2.coord, me.coord) AS ang
        FROM (
            SELECT p.trajectory, p.coord, e.type_id, p.n_constituents
            FROM physicalities p
            JOIN entities e ON e.id = p.entity_id
            WHERE p.entity_id = p_word
              AND p.type = 1
              AND p.trajectory IS NOT NULL
              AND p.coord IS NOT NULL
            ORDER BY p.id
            LIMIT 1
        ) me
        JOIN physicalities p2 ON p2.type = 1
                           AND p2.trajectory IS NOT NULL
                           AND p2.coord IS NOT NULL
        JOIN entities e2 ON e2.id = p2.entity_id
        WHERE e2.type_id = me.type_id
          AND p2.n_constituents = me.n_constituents
          AND p2.entity_id <> p_word
        ORDER BY p2.coord <<->> me.coord
        LIMIT 48
    ) k
    WHERE entity_exists(k.entity_id)
      AND word_case_class_surface(k.entity_id) = word_case_class_surface(p_word)
      AND (k.fr <= p_frechet_max OR k.ang <= 0.115)
$$;
#line 188 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/lexical/lexical_peers.sql.in"
CREATE OR REPLACE FUNCTION lexical_peers(p_word bytea)
    RETURNS bytea[]
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    WITH ucd AS (
        SELECT unnest(word_case_variant_ids(p_word)) AS id
    ),
    shape AS (
        SELECT unnest(word_shape_peers(p_word, 0.02::double precision)) AS id
    ),
    merged AS (
        SELECT p_word AS id
        UNION SELECT id FROM ucd
        UNION SELECT id FROM shape WHERE id IS NOT NULL
    )
    SELECT COALESCE(array_agg(DISTINCT id ORDER BY id), ARRAY[p_word])
    FROM merged
    WHERE id IS NOT NULL
$$;
#line 189 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/lexical/senses.sql.in"
CREATE OR REPLACE FUNCTION senses(p_word bytea)
    RETURNS TABLE(sense_id bytea, synset_id bytea, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT s.object_id, ss.object_id,
           eff_mu_display(s.rating, s.rd),
           s.witness_count + ss.witness_count
    FROM consensus s
    JOIN consensus ss ON ss.subject_id = s.object_id
                     AND ss.type_id = relation_type_id('IS_SENSE_OF')
    WHERE s.subject_id = ANY(lexical_peers(p_word))
      AND s.type_id = relation_type_id('HAS_SENSE')
    ORDER BY eff_mu(s.rating, s.rd) + eff_mu(ss.rating, ss.rd) DESC
$$;
#line 190 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/lexical/senses_with_context.sql.in"
CREATE OR REPLACE FUNCTION senses(p_word bytea, p_context bytea[])
    RETURNS TABLE(sense_id bytea, synset_id bytea, eff_mu numeric, witnesses bigint,
                  score numeric)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT s.object_id, ss.object_id,
           eff_mu_display(s.rating, s.rd),
           s.witness_count + ss.witness_count,
           round(((eff_mu(s.rating, s.rd) + eff_mu(ss.rating, ss.rd)
             + COALESCE((SELECT sum(eff_mu(c.rating, c.rd)) FROM consensus c
                         WHERE c.subject_id = ANY (p_context) AND c.object_id = ss.object_id
                           AND NOT refuted(c.rating, c.rd)), 0)
             + COALESCE((SELECT sum(eff_mu(c.rating, c.rd)) FROM consensus c
                         WHERE c.subject_id = ss.object_id AND c.object_id = ANY (p_context)
                           AND NOT refuted(c.rating, c.rd)), 0)) / 1e9)::numeric, 3)
    FROM consensus s
    JOIN consensus ss ON ss.subject_id = s.object_id
                     AND ss.type_id = relation_type_id('IS_SENSE_OF')
    WHERE s.subject_id = ANY(lexical_peers(p_word))
      AND s.type_id = relation_type_id('HAS_SENSE')
    ORDER BY 5 DESC
$$;
#line 191 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/lexical/define.sql.in"
CREATE OR REPLACE FUNCTION define(p_word bytea, p_limit int DEFAULT 5)
    RETURNS TABLE(definition text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT d.definition, d.eff_mu, d.witnesses
    FROM (
        SELECT render_text(g.object_id, 24) AS definition,
               eff_mu_display(g.rating, g.rd) AS eff_mu,
               g.witness_count AS witnesses,
               sn.eff_mu AS sense_rank
        FROM senses(p_word) sn
        JOIN consensus g ON g.subject_id = sn.synset_id
                        AND g.type_id = relation_type_id('HAS_DEFINITION')
        UNION ALL
        SELECT render_text(g.object_id, 24),
               eff_mu_display(g.rating, g.rd),
               g.witness_count,
               NULL::numeric
        FROM unnest(lexical_peers(p_word)) AS peer(id)
        JOIN consensus g ON g.subject_id = peer.id
                        AND g.type_id = relation_type_id('HAS_DEFINITION')
    ) d
    ORDER BY COALESCE(d.sense_rank, 0) + d.eff_mu DESC
    LIMIT p_limit
$$;
#line 192 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/lexical/define_with_context.sql.in"
CREATE OR REPLACE FUNCTION define(p_word bytea, p_context bytea[], p_limit int DEFAULT 5)
    RETURNS TABLE(definition text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT d.definition, d.eff_mu, d.witnesses
    FROM (
        SELECT render_text(g.object_id, 24) AS definition,
               eff_mu_display(g.rating, g.rd) AS eff_mu,
               g.witness_count AS witnesses,
               sn.score AS sense_rank
        FROM senses(p_word, p_context) sn
        JOIN consensus g ON g.subject_id = sn.synset_id
                        AND g.type_id = relation_type_id('HAS_DEFINITION')
        UNION ALL
        SELECT render_text(g.object_id, 24),
               eff_mu_display(g.rating, g.rd),
               g.witness_count,
               NULL::numeric
        FROM unnest(lexical_peers(p_word)) AS peer(id)
        JOIN consensus g ON g.subject_id = peer.id
                        AND g.type_id = relation_type_id('HAS_DEFINITION')
    ) d
    ORDER BY COALESCE(d.sense_rank, 0) + d.eff_mu DESC
    LIMIT p_limit
$$;
#line 193 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/lexical/examples.sql.in"
CREATE OR REPLACE FUNCTION examples(p_word bytea, p_limit int DEFAULT 5)
    RETURNS TABLE(example text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT render_text(g.object_id, 24),
           eff_mu_display(g.rating, g.rd), g.witness_count
    FROM senses(p_word) sn
    JOIN consensus g ON g.subject_id = sn.synset_id
                    AND g.type_id = relation_type_id('HAS_EXAMPLE')
    ORDER BY sn.eff_mu + eff_mu_display(g.rating, g.rd) DESC
    LIMIT p_limit
$$;
#line 194 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/lexical/synset_members.sql.in"
DROP FUNCTION IF EXISTS synset_members(bytea);
CREATE OR REPLACE FUNCTION synset_members(p_word bytea)
    RETURNS TABLE(member bytea, lang bytea, mu bigint, witnesses bigint, sense_mu numeric)
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    WITH syn AS (
        SELECT s.synset_id AS id, max(s.eff_mu) AS sense_mu
        FROM senses(p_word) s
        WHERE NOT EXISTS (
            SELECT 1 FROM consensus i
            WHERE i.subject_id = s.synset_id
              AND i.type_id = relation_type_id('IS_INSTANCE_OF'))
        GROUP BY s.synset_id
    ),
    members AS (
        SELECT c.object_id AS w, c.rating, c.rd, c.witness_count, syn.sense_mu
        FROM consensus c JOIN syn ON c.subject_id = syn.id
        WHERE c.type_id = relation_type_id('IS_SYNONYM_OF') AND c.object_id IS NOT NULL
        UNION ALL
        SELECT c.subject_id AS w, c.rating, c.rd, c.witness_count, syn.sense_mu
        FROM consensus c JOIN syn ON c.object_id = syn.id
        WHERE c.type_id = relation_type_id('IS_SYNONYM_OF')
    ),
    agg AS (
        SELECT m.w,
               max(eff_mu(m.rating, m.rd)) AS mu,
               sum(m.witness_count)::bigint AS witnesses,
               max(m.sense_mu) AS sense_mu
        FROM members m
        WHERE m.w <> ALL(lexical_peers(p_word))
        GROUP BY m.w
    )
    SELECT a.w, hl.object_id AS lang, a.mu::bigint, a.witnesses, a.sense_mu
    FROM agg a
    LEFT JOIN consensus hl
      ON hl.subject_id = a.w AND hl.type_id = relation_type_id('HAS_LANGUAGE')
$$;
#line 195 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/identity/fake_tier_band_count.sql.in"
CREATE OR REPLACE FUNCTION fake_tier_band_count()
RETURNS bigint
LANGUAGE sql
STABLE
SET search_path = @extschema@, public
AS $$
    SELECT count(*)::bigint FROM @extschema@.entities WHERE tier IN (247, 248, 250);
$$;
#line 196 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/identity/identity_law_violations.sql.in"
CREATE OR REPLACE FUNCTION identity_law_violations()
RETURNS TABLE(id bytea, tier smallint, reason text)
LANGUAGE sql
STABLE
SET search_path = @extschema@, public
AS $$
    SELECT e.id, e.tier, 'tier_out_of_range'::text
    FROM @extschema@.entities e
    WHERE e.tier < 0 OR e.tier >= 256;
$$;
#line 197 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/identity/compositional_type_ids.sql.in"
CREATE OR REPLACE FUNCTION compositional_type_ids()
RETURNS bytea[]
LANGUAGE sql
IMMUTABLE
SET search_path = @extschema@, public
AS $$
    SELECT ARRAY[
        public.laplace_hash128_blake3('Codepoint'::bytea),
        public.laplace_hash128_blake3('Grapheme'::bytea),
        public.laplace_hash128_blake3('Word'::bytea),
        public.laplace_hash128_blake3('Sentence'::bytea),
        public.laplace_hash128_blake3('Document'::bytea)
    ];
$$;
#line 198 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/identity/is_compositional_type.sql.in"
CREATE OR REPLACE FUNCTION is_compositional_type(p_type_id bytea)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
SET search_path = @extschema@, public
AS $$
    SELECT p_type_id = ANY(@extschema@.compositional_type_ids());
$$;
#line 199 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/identity/compositional_tier_distribution.sql.in"
CREATE OR REPLACE FUNCTION compositional_tier_distribution()
RETURNS TABLE(tier smallint, n bigint)
LANGUAGE sql
STABLE
SET search_path = @extschema@, public
AS $$
    SELECT e.tier, count(*)::bigint
    FROM @extschema@.entities e
    WHERE @extschema@.is_compositional_type(e.type_id)
    GROUP BY e.tier
    ORDER BY e.tier;
$$;
#line 200 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/identity/substrate_health.sql.in"
CREATE OR REPLACE FUNCTION substrate_health()
RETURNS TABLE(
    ok boolean,
    fake_tier_bands bigint,
    identity_violations bigint,
    bootstrap_entities bigint
)
LANGUAGE sql
STABLE
SET search_path = @extschema@, public
AS $$
    SELECT
        (@extschema@.fake_tier_band_count() = 0
         AND (SELECT count(*) FROM @extschema@.identity_law_violations()) = 0
         AND EXISTS (
             SELECT 1 FROM @extschema@.entities
             WHERE id = public.laplace_hash128_blake3('substrate/source/SubstrateCanonical/v1'::bytea)
         )) AS ok,
        @extschema@.fake_tier_band_count() AS fake_tier_bands,
        (SELECT count(*) FROM @extschema@.identity_law_violations()) AS identity_violations,
        (SELECT count(*) FROM @extschema@.entities) AS bootstrap_entities;
$$;
#line 201 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ingest/intent_preflight_result.sql.in"
DO $idem$ BEGIN
    CREATE TYPE intent_preflight_result AS (
        entity_exists bytea,
        phys_exists bytea,
        att_exists bytea
    );
EXCEPTION WHEN duplicate_object THEN NULL;
END $idem$;
#line 202 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ingest/intent_preflight.sql.in"
CREATE OR REPLACE FUNCTION intent_preflight(
    entity_ids bytea[],
    phys_ids bytea[],
    att_ids bytea[])
    RETURNS intent_preflight_result
    AS 'MODULE_PATHNAME', 'pg_laplace_intent_preflight'
    LANGUAGE C STABLE;
#line 203 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/drop_retired_content_lane.sql.in"
DO $do$
DECLARE
    obj text;
BEGIN
    FOREACH obj IN ARRAY ARRAY[
        'TABLE @extschema@.content_pairs',
        'PROCEDURE @extschema@.rebuild_content_index(int)',
        'PROCEDURE @extschema@.rebuild_content_index_deep()',
        'PROCEDURE @extschema@.rebuild_content_pairs(int)']
    LOOP
        BEGIN
            EXECUTE format('ALTER EXTENSION %I DROP %s', 'laplace_substrate', obj);
        EXCEPTION WHEN OTHERS THEN
            NULL;
        END;
    END LOOP;
    EXECUTE 'DROP TABLE IF EXISTS @extschema@.content_pairs';
    EXECUTE 'DROP TABLE IF EXISTS @extschema@.content_index';
    EXECUTE 'DROP TABLE IF EXISTS @extschema@.constituency_edge';
    EXECUTE 'DROP PROCEDURE IF EXISTS @extschema@.rebuild_content_index(int)';
    EXECUTE 'DROP PROCEDURE IF EXISTS @extschema@.rebuild_content_index_deep()';
    EXECUTE 'DROP PROCEDURE IF EXISTS @extschema@.rebuild_content_pairs(int)';


    EXECUTE 'DROP FUNCTION IF EXISTS @extschema@.entity_trajectory_plane(bytea[], int, int)';
    EXECUTE 'DROP FUNCTION IF EXISTS @extschema@.cooccurrence_scan(int, bytea[])';
END $do$;
#line 204 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/stream_stats.sql.in"
CREATE OR REPLACE FUNCTION stream_stats(
        OUT sequences bigint, OUT positions bigint, OUT distinct_entities int,
        OUT separators bigint, OUT trajectories bigint, OUT last_witnessed timestamptz)
    AS 'MODULE_PATHNAME', 'pg_laplace_stream_stats'
    LANGUAGE C VOLATILE;
#line 205 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/cooccurrence_scan.sql.in"
CREATE OR REPLACE FUNCTION cooccurrence_scan(p_max_gap int)
    RETURNS TABLE(gap int, subject_id bytea, object_id bytea, cnt bigint)
    AS 'MODULE_PATHNAME', 'pg_laplace_cooccurrence_scan'
    LANGUAGE C STRICT VOLATILE;
#line 206 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/trajectory_cooccurrence.sql.in"
CREATE OR REPLACE FUNCTION trajectory_cooccurrence(p_window int DEFAULT 1)
    RETURNS TABLE(subject_id bytea, object_id bytea, cnt bigint, subject_total bigint)
    LANGUAGE sql VOLATILE
    SET search_path = @extschema@, public AS $$



    WITH pairs AS (
        SELECT s.subject_id, s.object_id, sum(s.cnt) AS cnt
        FROM cooccurrence_scan(p_window) s
        GROUP BY s.subject_id, s.object_id
    )
    SELECT p.subject_id, p.object_id, p.cnt::bigint,
           sum(p.cnt) OVER (PARTITION BY p.subject_id)::bigint
    FROM pairs p
$$;
#line 207 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/trajectory_cooccurrence_by_stride.sql.in"
CREATE OR REPLACE FUNCTION trajectory_cooccurrence_by_stride(p_max_gap int DEFAULT 8)
    RETURNS TABLE(subject_id bytea, object_id bytea, gap int, cnt bigint, subject_total bigint)
    LANGUAGE sql VOLATILE
    SET search_path = @extschema@, public AS $$
    SELECT s.subject_id, s.object_id, s.gap, s.cnt,
           sum(s.cnt) OVER (PARTITION BY s.subject_id, s.gap)::bigint
    FROM cooccurrence_scan(p_max_gap) s
$$;
#line 208 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/trajectory_pairs.sql.in"
CREATE TABLE IF NOT EXISTS trajectory_pairs (
    subject_id bytea NOT NULL,
    object_id bytea NOT NULL,
    gap int NOT NULL,
    cnt bigint NOT NULL,
    subject_total bigint NOT NULL
);
#line 209 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/trajectory_pairs_subject_idx.sql.in"
CREATE INDEX IF NOT EXISTS trajectory_pairs_subject_idx ON trajectory_pairs (subject_id);

CREATE TABLE IF NOT EXISTS trajectory_pairs_meta (
    only_row boolean PRIMARY KEY DEFAULT true CHECK (only_row),
    probe_rows bigint NOT NULL,
    probe_max_us bigint NOT NULL,
    max_gap int NOT NULL,
    built_at timestamptz NOT NULL DEFAULT now()
);
#line 210 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/trajectory_pairs_ensure.sql.in"
CREATE OR REPLACE FUNCTION trajectory_pairs_ensure(p_max_gap int DEFAULT 8)
    RETURNS bigint
    LANGUAGE plpgsql VOLATILE
    SET search_path = @extschema@, public AS $$
DECLARE
    cur_rows bigint;
    cur_us bigint;
    m trajectory_pairs_meta%ROWTYPE;
    n bigint;
BEGIN
    SELECT count(*)::bigint,
           COALESCE((extract(epoch FROM max(observed_at)) * 1000000)::bigint, 0)
      INTO cur_rows, cur_us
      FROM physicalities
     WHERE type = 1 AND trajectory IS NOT NULL;

    SELECT * INTO m FROM trajectory_pairs_meta WHERE only_row;
    IF FOUND AND m.probe_rows = cur_rows AND m.probe_max_us = cur_us
       AND m.max_gap >= p_max_gap THEN
        RETURN (SELECT count(*) FROM trajectory_pairs);
    END IF;

    TRUNCATE trajectory_pairs;
    INSERT INTO trajectory_pairs (subject_id, object_id, gap, cnt, subject_total)
    SELECT subject_id, object_id, gap, cnt, subject_total
    FROM trajectory_cooccurrence_by_stride(p_max_gap);
    GET DIAGNOSTICS n = ROW_COUNT;

    INSERT INTO trajectory_pairs_meta (only_row, probe_rows, probe_max_us, max_gap, built_at)
    VALUES (true, cur_rows, cur_us, p_max_gap, now())
    ON CONFLICT (only_row) DO UPDATE
        SET probe_rows = excluded.probe_rows, probe_max_us = excluded.probe_max_us,
            max_gap = excluded.max_gap, built_at = excluded.built_at;
    RETURN n;
END $$;
#line 211 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/trajectory_pairs_plane.sql.in"
CREATE OR REPLACE FUNCTION trajectory_pairs_plane(p_vocab bytea[], p_max_gap int DEFAULT 8)
    RETURNS TABLE(subject_id bytea, object_id bytea, w double precision)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
    WITH v(id) AS (SELECT DISTINCT u FROM unnest(p_vocab) AS u)
    SELECT t.subject_id, t.object_id,
           sum( (t.cnt::float8 / NULLIF(t.subject_total, 0)) / t.gap )::float8 AS w
    FROM trajectory_pairs t
    JOIN v vs ON vs.id = t.subject_id
    JOIN v vo ON vo.id = t.object_id
    WHERE t.gap <= p_max_gap
    GROUP BY t.subject_id, t.object_id
$$;
#line 212 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/relation_plane.sql.in"
CREATE OR REPLACE FUNCTION relation_plane(p_family text, p_name text, p_arg int DEFAULT NULL)
    RETURNS TABLE(subject_id bytea, object_id bytea, w double precision)
    LANGUAGE plpgsql VOLATILE
    SET search_path = @extschema@, public AS $$
DECLARE
    g int;
BEGIN
    IF p_family = 'consensus' THEN
        RETURN QUERY
        SELECT c.subject_id, c.object_id,
               (eff_mu(c.rating, c.rd) - glicko2_neutral_mu())::double precision / 1e9
        FROM consensus c
        WHERE c.type_id = relation_type_id(p_name) AND c.object_id IS NOT NULL;
        RETURN;
    END IF;

    IF p_family <> 'traj' THEN
        RAISE EXCEPTION 'relation_plane: unknown family % (consensus | traj)', p_family;
    END IF;

    g := CASE p_name
             WHEN 'next' THEN 1
             WHEN 'gap' THEN COALESCE(p_arg, 1)
             WHEN 'window' THEN COALESCE(p_arg, 4)
             ELSE NULL
         END;
    IF g IS NULL THEN
        RAISE EXCEPTION 'relation_plane: unknown traj plane % (next | gap | window)', p_name;
    END IF;

    IF p_name = 'window' THEN
        RETURN QUERY
        SELECT t.subject_id, t.object_id, t.cnt::double precision / t.subject_total
        FROM trajectory_cooccurrence(g) t;
        RETURN;
    END IF;

    RETURN QUERY
    SELECT t.subject_id, t.object_id, t.cnt::double precision / t.subject_total
    FROM trajectory_cooccurrence_by_stride(g) t WHERE t.gap = g;
END;
$$;
#line 213 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/entity_relation_plane.sql.in"
CREATE OR REPLACE FUNCTION entity_relation_plane(
        p_vocab bytea[], p_rel_names text[], p_degree_cap int DEFAULT 48)
    RETURNS TABLE(subject_id bytea, object_id bytea, w double precision)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    WITH v(id) AS (SELECT DISTINCT u FROM unnest(p_vocab) AS u),
         t(tid) AS (SELECT relation_type_id(n) FROM unnest(p_rel_names) AS n),
         edges AS MATERIALIZED (




             SELECT c.subject_id, c.object_id,
                    (eff_mu(c.rating, c.rd) - glicko2_neutral_mu())::double precision / 1e9 AS w
             FROM v
             JOIN consensus c ON c.subject_id = v.id
             JOIN t ON t.tid = c.type_id
             JOIN v vo ON vo.id = c.object_id

         ),
         capped AS (
             SELECT e.subject_id, e.object_id, e.w,
                    row_number() OVER (PARTITION BY e.subject_id
                                       ORDER BY abs(e.w) DESC, e.object_id) AS rn
             FROM edges e
         )
    SELECT c.subject_id, c.object_id, c.w FROM capped c WHERE c.rn <= p_degree_cap
$$;
#line 214 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/consensus_layer_plane.sql.in"
CREATE OR REPLACE FUNCTION consensus_layer_plane(
        p_vocab bytea[], p_rank_lo float8, p_rank_hi float8, p_degree_cap int DEFAULT 48)
    RETURNS TABLE(subject_id bytea, object_id bytea, w double precision, layer_rank double precision)
    LANGUAGE sql STABLE PARALLEL RESTRICTED
    SET search_path = @extschema@, public AS $$
    WITH v(id) AS (SELECT DISTINCT u FROM unnest(p_vocab) AS u),
         edges AS MATERIALIZED (
             -- keep type_id; resolve rank ONCE per distinct type below, not per-edge.
             -- relation_rank_resolved does an SPI IS_A-inheritance walk, so the former
             -- per-edge call was the hot cost (mirrors the consensus_type_plane fix).
             SELECT c.subject_id, c.object_id, c.rating, c.rd, c.type_id
             FROM v
             JOIN consensus c ON c.subject_id = v.id
             JOIN v vo ON vo.id = c.object_id
         ),
         rk AS (
             SELECT d.type_id, r.rank
             FROM (SELECT DISTINCT type_id FROM edges) d
             CROSS JOIN LATERAL relation_rank_resolved(d.type_id) AS r(rank)
             WHERE r.rank IS NOT NULL AND r.rank BETWEEN p_rank_lo AND p_rank_hi
         ),
         ranked AS (
             SELECT e.subject_id, e.object_id,
                    (eff_mu(e.rating, e.rd))::float8 / 1e9 AS w, rk.rank AS rk
             FROM edges e JOIN rk ON rk.type_id = e.type_id
         ),
         capped AS (
             SELECT r.subject_id, r.object_id, r.w, r.rk,
                    row_number() OVER (PARTITION BY r.subject_id ORDER BY r.rk * r.w DESC, r.object_id) AS rn
             FROM ranked r
         )
    SELECT c.subject_id, c.object_id, c.w, c.rk FROM capped c WHERE c.rn <= p_degree_cap
$$;
#line 215 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/consensus_layer_plane_masked.sql.in"
CREATE OR REPLACE FUNCTION consensus_layer_plane_masked(
        p_vocab bytea[], p_band_mask bytea, p_degree_cap int DEFAULT 48)
    RETURNS TABLE(subject_id bytea, object_id bytea, w double precision, layer_rank double precision)
    LANGUAGE sql STABLE PARALLEL RESTRICTED
    SET search_path = @extschema@, public AS $$
    WITH v(id) AS (SELECT DISTINCT u FROM unnest(p_vocab) AS u),
         band_types AS MATERIALIZED (
             SELECT e.id, relation_rank_resolved(e.id) AS rank
             FROM entities e
             WHERE laplace_highway_match(e.highway_mask, p_band_mask)
         ),
         edges AS MATERIALIZED (
             SELECT c.subject_id, c.object_id, c.rating, c.rd, c.type_id,
                    bt.rank
             FROM v
             JOIN consensus c ON c.subject_id = v.id
             JOIN v vo ON vo.id = c.object_id
             JOIN band_types bt ON bt.id = c.type_id
             WHERE bt.rank IS NOT NULL
         ),
         ranked AS (
             SELECT e.subject_id, e.object_id,
                    (eff_mu(e.rating, e.rd))::float8 / 1e9 AS w, e.rank AS rk
             FROM edges e
         ),
         capped AS (
             SELECT r.subject_id, r.object_id, r.w, r.rk,
                    row_number() OVER (PARTITION BY r.subject_id ORDER BY r.rk * r.w DESC, r.object_id) AS rn
             FROM ranked r
         )
    SELECT c.subject_id, c.object_id, c.w, c.rk FROM capped c WHERE c.rn <= p_degree_cap
$$;
#line 216 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/consensus_type_plane.sql.in"
CREATE OR REPLACE FUNCTION consensus_type_plane(
        p_vocab bytea[], p_degree_cap int DEFAULT 48, p_types bytea[] DEFAULT NULL)
    RETURNS TABLE(subject_id bytea, object_id bytea, w double precision,
                  type_id bytea, layer_rank double precision)
    LANGUAGE sql STABLE PARALLEL RESTRICTED
    SET search_path = @extschema@, public AS $$
    WITH v(id) AS (SELECT DISTINCT u FROM unnest(p_vocab) AS u),
         edges AS MATERIALIZED (
             SELECT c.subject_id, c.object_id, c.rating, c.rd, c.type_id
             FROM v
             JOIN consensus c ON c.subject_id = v.id
             JOIN v vo ON vo.id = c.object_id
             WHERE (p_types IS NULL OR c.type_id = ANY(p_types))
         ),
         rk AS (
             SELECT d.type_id, r.rank
             FROM (SELECT DISTINCT type_id FROM edges) d
             CROSS JOIN LATERAL relation_rank_resolved(d.type_id) AS r(rank)
             WHERE r.rank IS NOT NULL
         ),
         ranked AS (
             SELECT e.subject_id, e.object_id,
                    (eff_mu(e.rating, e.rd))::float8 / 1e9 AS w, e.type_id, rk.rank AS rk
             FROM edges e JOIN rk ON rk.type_id = e.type_id
         ),
         capped AS (
             SELECT r.subject_id, r.object_id, r.w, r.type_id, r.rk,
                    row_number() OVER (PARTITION BY r.subject_id, r.type_id
                                       ORDER BY r.w DESC, r.object_id) AS rn
             FROM ranked r
         )
    SELECT c.subject_id, c.object_id, c.w, c.type_id, c.rk
    FROM capped c WHERE c.rn <= p_degree_cap
$$;
#line 217 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/consensus_adjacency.sql.in"
CREATE OR REPLACE FUNCTION consensus_adjacency(
        p_vocab bytea[], p_degree_cap int DEFAULT 64)
    RETURNS TABLE(subject_id bytea, object_id bytea, w double precision)
    LANGUAGE sql STABLE PARALLEL RESTRICTED
    SET search_path = @extschema@, public AS $$
    WITH v(id) AS (SELECT DISTINCT u FROM unnest(p_vocab) AS u),
         edges AS MATERIALIZED (







             SELECT c.subject_id, c.object_id, c.rating, c.rd,
                    relation_rank_resolved(c.type_id) AS rk
             FROM v
             JOIN consensus c ON c.subject_id = v.id
             JOIN v vo ON vo.id = c.object_id
         ),
         summed AS (
             SELECT e.subject_id, e.object_id,
                    sum(e.rk * (eff_mu(e.rating, e.rd))::float8 / 1e9) AS w
             FROM edges e
             WHERE e.rk IS NOT NULL
             GROUP BY e.subject_id, e.object_id
         ),
         capped AS (
             SELECT s.subject_id, s.object_id, s.w,
                    row_number() OVER (PARTITION BY s.subject_id ORDER BY s.w DESC, s.object_id) AS rn
             FROM summed s
             WHERE s.w > 0
         )
    SELECT c.subject_id, c.object_id, c.w FROM capped c WHERE c.rn <= p_degree_cap
$$;
#line 218 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/foundry_vocab.sql.in"
CREATE OR REPLACE FUNCTION foundry_vocab(p_size int)
    RETURNS TABLE(entity_id bytea, surface text, weight bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    WITH freq AS (
        SELECT c.subject_id AS id, count(*) AS d
        FROM consensus c
        WHERE c.type_id = relation_type_id('PRECEDES') AND c.object_id IS NOT NULL
        GROUP BY c.subject_id
    ),
    seeds AS (
        SELECT f.id, f.d
        FROM freq f JOIN entities e ON e.id = f.id AND e.tier = 2
        ORDER BY f.d DESC LIMIT p_size
    ),
    closure AS (
        SELECT DISTINCT c.object_id AS id
        FROM consensus c
        JOIN seeds s ON s.id = c.subject_id
        JOIN entities e ON e.id = c.object_id AND e.tier = 2
        WHERE c.object_id IS NOT NULL
          AND c.type_id IN (
              relation_type_id('IS_A'), relation_type_id('IS_SYNONYM_OF'),
              relation_type_id('RELATED_TO'), relation_type_id('HAS_PART'),
              relation_type_id('DERIVATIONALLY_RELATED'), relation_type_id('IS_COORDINATE_TERM_WITH'))
    ),
    ids AS (SELECT id FROM seeds UNION SELECT id FROM closure),






    ranked AS (
        SELECT i.id, COALESCE(f.d, 0) AS d
        FROM ids i LEFT JOIN freq f ON f.id = i.id
        ORDER BY COALESCE(f.d, 0) DESC, i.id
        LIMIT p_size * 3
    ),
    arr AS (
        SELECT array_agg(id ORDER BY d DESC, id) AS a,
               array_agg(d ORDER BY d DESC, id) AS ds
        FROM ranked
    ),
    named AS (


        SELECT u.id, u.s, a.ds[u.ord::int] AS d
        FROM arr a
        CROSS JOIN LATERAL unnest(a.a, render_text_batch(a.a, 64)) WITH ORDINALITY AS u(id, s, ord)
        WHERE a.a IS NOT NULL
    ),
    clean AS (
        SELECT DISTINCT ON (n.s) n.id, n.s, n.d
        FROM named n
        WHERE n.s IS NOT NULL AND btrim(n.s) <> '' AND NOT is_all_whitespace(n.s)
          AND position(' ' IN n.s) = 0 AND char_length(n.s) <= 40
        ORDER BY n.s, n.d DESC
    )
    SELECT c.id, c.s, c.d FROM clean c ORDER BY c.d DESC, c.id LIMIT p_size
$$;
#line 219 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/grapheme_floor_vocab.sql.in"
CREATE OR REPLACE FUNCTION grapheme_floor_vocab(p_size int DEFAULT 512, p_words int DEFAULT 50000)
    RETURNS TABLE(entity_id bytea, surface text, weight bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$




    WITH t AS (
        SELECT p.trajectory
        FROM physicalities p
        JOIN entities e ON e.id = p.entity_id AND e.tier = 2
        WHERE p.type = 1 AND p.trajectory IS NOT NULL
        ORDER BY p.entity_id LIMIT p_words
    ),
    g AS (
        SELECT tc.entity_id, tc.run_length
        FROM t CROSS JOIN LATERAL laplace_trajectory_constituents(t.trajectory) tc
    ),
    agg AS (


        SELECT g.entity_id, sum(GREATEST(g.run_length,1))::bigint AS cnt
        FROM g JOIN entities e ON e.id = g.entity_id AND e.tier <= 1
        GROUP BY g.entity_id
    ),
    named AS (
        SELECT a.entity_id, render_text(a.entity_id, 8) AS s, a.cnt
        FROM agg a ORDER BY a.cnt DESC LIMIT p_size * 2
    )
    SELECT n.entity_id, n.s, n.cnt
    FROM named n
    WHERE char_length(n.s) = 1 AND NOT is_all_whitespace(n.s)
    ORDER BY n.cnt DESC LIMIT p_size
$$;
#line 220 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/grapheme_order.sql.in"
CREATE OR REPLACE FUNCTION grapheme_order(p_vocab bytea[], p_words int DEFAULT 50000, p_gap int DEFAULT 1)
    RETURNS TABLE(subject_id bytea, object_id bytea, w double precision)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$





    WITH v(id) AS (SELECT DISTINCT u FROM unnest(p_vocab) AS u),
    t AS (




        SELECT DISTINCT ON (p.entity_id) p.entity_id AS wid, p.trajectory
        FROM physicalities p
        JOIN entities e ON e.id = p.entity_id AND e.tier = 2
        WHERE p.type = 1 AND p.trajectory IS NOT NULL
        ORDER BY p.entity_id, p.id LIMIT p_words
    ),
    g AS (




        SELECT t.wid, tc.ordinal AS ord, gs.i AS sub, tc.entity_id AS ch
        FROM t CROSS JOIN LATERAL laplace_trajectory_constituents(t.trajectory) tc
        CROSS JOIN LATERAL generate_series(1, GREATEST(tc.run_length, 1)) gs(i)
    ),
    bg AS (
        SELECT ch AS cur, lead(ch, p_gap) OVER (PARTITION BY wid ORDER BY ord, sub) AS nxt FROM g
    ),
    cnt AS (
        SELECT cur, nxt, count(*)::float8 AS c
        FROM bg WHERE nxt IS NOT NULL GROUP BY cur, nxt
    )
    SELECT b.cur, b.nxt, b.c
    FROM cnt b
    JOIN v vs ON vs.id = b.cur
    JOIN v vo ON vo.id = b.nxt
$$;
#line 221 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/word_order.sql.in"
CREATE OR REPLACE FUNCTION word_order(p_vocab bytea[], p_trajs int DEFAULT 100000, p_gap int DEFAULT 1)
    RETURNS TABLE(subject_id bytea, object_id bytea, w double precision)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    WITH v(id) AS (SELECT DISTINCT u FROM unnest(p_vocab) AS u),
    t AS (
        SELECT DISTINCT ON (p.entity_id) p.entity_id AS sid, p.trajectory
        FROM physicalities p
        JOIN entities e ON e.id = p.entity_id AND e.tier > 2
        WHERE p.type = 1 AND p.trajectory IS NOT NULL
        ORDER BY p.entity_id, p.id LIMIT p_trajs
    ),
    g AS (
        SELECT t.sid, tc.ordinal AS ord, tc.entity_id AS wid
        FROM t CROSS JOIN LATERAL laplace_trajectory_constituents(t.trajectory) tc
        JOIN v ON v.id = tc.entity_id
    ),
    bg AS (
        SELECT wid AS cur, lead(wid, p_gap) OVER (PARTITION BY sid ORDER BY ord) AS nxt FROM g
    ),
    cnt AS (
        SELECT cur, nxt, count(*)::float8 AS c
        FROM bg WHERE nxt IS NOT NULL AND cur <> nxt GROUP BY cur, nxt
    )
    SELECT cur, nxt, c FROM cnt
$$;
#line 222 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/corpus_word_vocab.sql.in"
CREATE OR REPLACE FUNCTION corpus_word_vocab(p_size int, p_trajs int DEFAULT 400000)
    RETURNS TABLE(surface text, weight bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    WITH t AS (
        SELECT DISTINCT ON (p.entity_id) p.entity_id, p.trajectory
        FROM physicalities p JOIN entities e ON e.id = p.entity_id AND e.tier > 2
        WHERE p.type = 1 AND p.trajectory IS NOT NULL
        ORDER BY p.entity_id, p.id LIMIT p_trajs
    ),
    w AS (
        SELECT tc.entity_id AS id, count(*) AS c
        FROM t CROSS JOIN LATERAL laplace_trajectory_constituents(t.trajectory) tc
        JOIN entities e ON e.id = tc.entity_id AND e.tier = 2
        GROUP BY tc.entity_id
    ),



    cand AS (SELECT id, c, render_text(id, 80) AS s FROM w ORDER BY c DESC LIMIT p_size * 4)
    SELECT s, c FROM cand
    WHERE s ~ '^[A-Za-z][A-Za-z''.-]*$'
    ORDER BY c DESC LIMIT p_size
$$;
#line 223 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/metric_edges.sql.in"
CREATE OR REPLACE FUNCTION metric_edges(
        p_vocab bytea[], p_metric text DEFAULT 'frechet', p_k int DEFAULT 16, p_probe int DEFAULT 64)
    RETURNS TABLE(subject_id bytea, object_id bytea, w double precision)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    WITH v AS (

        SELECT DISTINCT ON (p.entity_id) p.entity_id AS id, p.coord, p.trajectory
        FROM physicalities p
        JOIN unnest(p_vocab) AS u(id) ON u.id = p.entity_id
        WHERE p.type = 1
        ORDER BY p.entity_id, p.id
    ),
    cand AS (

        SELECT a.id AS aid, b.id AS bid, a.trajectory AS at, b.trajectory AS bt,
               laplace_angular_distance_4d(a.coord, b.coord) AS ang
        FROM v a JOIN v b ON a.id <> b.id
    ),
    pruned AS (
        SELECT aid, bid, at, bt, ang,
               row_number() OVER (PARTITION BY aid ORDER BY ang) AS rn
        FROM cand
    ),
    refined AS (

        SELECT aid, bid,
            CASE p_metric
                WHEN 'angular' THEN ang
                WHEN 'frechet' THEN coalesce(laplace_frechet_4d(at, bt), ang)
                WHEN 'hausdorff' THEN coalesce(laplace_hausdorff_4d(at, bt), ang)
                ELSE ang
            END AS d
        FROM pruned
        WHERE rn <= GREATEST(p_probe, p_k)
    ),
    topk AS (
        SELECT aid, bid, d,
               row_number() OVER (PARTITION BY aid ORDER BY d) AS rn
        FROM refined
    )
    SELECT aid, bid, d FROM topk WHERE rn <= p_k
$$;
#line 224 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/foundry_crawl.sql.in"
CREATE OR REPLACE FUNCTION foundry_crawl(
        p_seeds bytea[], p_budget int DEFAULT 32000, p_hops int DEFAULT 3, p_fanout int DEFAULT 64,
        p_rel_types bytea[] DEFAULT NULL)
    RETURNS TABLE(entity_id bytea, weight bigint)
    AS 'MODULE_PATHNAME', 'pg_laplace_foundry_crawl'
    LANGUAGE C VOLATILE;
#line 225 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/foundry_vocab_crawl.sql.in"
CREATE OR REPLACE FUNCTION foundry_vocab_crawl(
        p_seeds text[], p_max int DEFAULT 32000, p_hops int DEFAULT 3, p_fanout int DEFAULT 64,
        p_rank_floor float8 DEFAULT 0.5)
    RETURNS TABLE(entity_id bytea, surface text, weight bigint)
    LANGUAGE sql VOLATILE
    SET search_path = @extschema@, public AS $$
    WITH seed_ids AS (
        SELECT coalesce(array_agg(wid), '{}'::bytea[]) AS ids
        FROM (SELECT word_id(s) AS wid FROM unnest(p_seeds) AS s) q
        WHERE wid IS NOT NULL
    ),
#line 20 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/foundry_vocab_crawl.sql.in"
    rel_types AS (



        SELECT array_agg(e.id) AS ids
        FROM entities e
        WHERE e.type_id = canonical_id('RelationType')
          AND relation_rank_resolved(e.id) >= p_rank_floor
    ),
    crawled AS (
        SELECT c.entity_id, c.weight,
               row_number() OVER (ORDER BY c.weight DESC, c.entity_id) AS rn
        FROM seed_ids si, rel_types rt
        CROSS JOIN LATERAL foundry_crawl(si.ids, p_max, p_hops, p_fanout, rt.ids) c
    ),





    order1 AS (
        SELECT c.object_id AS entity_id,
               eff_mu(c.rating, c.rd)::numeric AS w,
               row_number() OVER (PARTITION BY seed.wid ORDER BY eff_mu(c.rating, c.rd) DESC, c.object_id) AS rn
        FROM seed_ids si
        CROSS JOIN unnest(si.ids) AS seed(wid)
        JOIN consensus c ON c.subject_id = seed.wid
            AND c.type_id = relation_type_id('PRECEDES')
            AND c.object_id IS NOT NULL
            AND NOT refuted(c.rating, c.rd)
        JOIN entities e ON e.id = c.object_id AND e.tier = 2
    ),
    order1_cut AS (
        SELECT entity_id, w, w::bigint AS weight FROM order1 WHERE rn <= p_fanout
    ),
    order2 AS (
        SELECT c.object_id AS entity_id,
               (o.w * eff_mu(c.rating, c.rd)::numeric / 1000000000)::bigint AS weight,
               row_number() OVER (ORDER BY o.w * eff_mu(c.rating, c.rd) DESC, c.object_id) AS rn
        FROM order1_cut o
        JOIN consensus c ON c.subject_id = o.entity_id
            AND c.type_id = relation_type_id('PRECEDES')
            AND c.object_id IS NOT NULL
            AND NOT refuted(c.rating, c.rd)
        JOIN entities e ON e.id = c.object_id AND e.tier = 2
    ),
    order2_cut AS (
        SELECT entity_id, weight FROM order2 WHERE rn <= p_max
    ),
    order_lemma AS (
        SELECT c.object_id AS entity_id, o.weight
        FROM (SELECT entity_id, max(weight) AS weight FROM (
                  SELECT entity_id, weight FROM order1_cut
                  UNION ALL SELECT entity_id, weight FROM order2_cut
              ) u GROUP BY entity_id) o
        JOIN consensus c ON c.subject_id = o.entity_id
            AND c.type_id = relation_type_id('IS_LEMMA_OF')
            AND c.object_id IS NOT NULL
            AND NOT refuted(c.rating, c.rd)
        JOIN entities e ON e.id = c.object_id AND e.tier = 2
    ),
    order_closure AS (
        SELECT entity_id, max(weight) AS weight,
               row_number() OVER (ORDER BY max(weight) DESC, entity_id) AS rn
        FROM (
            SELECT entity_id, weight FROM order1_cut
            UNION ALL SELECT entity_id, weight FROM order2_cut
            UNION ALL SELECT entity_id, weight FROM order_lemma
        ) u
        GROUP BY entity_id
    ),







    scaffold AS (
        SELECT f.id AS entity_id, f.d::bigint AS weight,
               row_number() OVER (ORDER BY f.d DESC) AS rn
        FROM (
            SELECT c.subject_id AS id, count(*) AS d
            FROM consensus c
            JOIN entities e ON e.id = c.subject_id AND e.tier = 2
            WHERE c.type_id = relation_type_id('PRECEDES') AND c.object_id IS NOT NULL
            GROUP BY c.subject_id
        ) f
    ),
    picked AS (
        SELECT entity_id, weight, 2 AS pri FROM order_closure WHERE rn <= p_max / 4
        UNION ALL
        SELECT entity_id, weight, 1 AS pri FROM crawled WHERE rn <= p_max - p_max / 2
        UNION ALL
        SELECT entity_id, weight, 0 AS pri FROM scaffold WHERE rn <= p_max / 4
    ),
    uniq AS (
        SELECT DISTINCT ON (entity_id) entity_id, weight, pri
        FROM picked ORDER BY entity_id, pri DESC, weight DESC
    ),
    ids_arr AS (
        SELECT array_agg(entity_id ORDER BY pri DESC, weight DESC) AS ids,
               array_agg(weight ORDER BY pri DESC, weight DESC) AS ws,
               array_agg(pri ORDER BY pri DESC, weight DESC) AS prs
        FROM uniq
    ),
    rendered AS (
        SELECT u.entity_id, u.s, a.ws[u.ord::int] AS weight, a.prs[u.ord::int] AS pri
        FROM ids_arr a
        CROSS JOIN LATERAL unnest(a.ids, render_text_batch(a.ids, 8))
                   WITH ORDINALITY AS u(entity_id, s, ord)
        WHERE a.ids IS NOT NULL AND cardinality(a.ids) > 0
    ),
    clean AS (
        SELECT DISTINCT ON (n.s) n.entity_id, n.s, n.weight, n.pri
        FROM rendered n
        WHERE n.s IS NOT NULL AND btrim(n.s) <> '' AND NOT is_all_whitespace(n.s)
          AND position(' ' IN n.s) = 0 AND char_length(n.s) <= 40
          AND n.s ~ '[[:alpha:]]'
          AND n.s !~ '^i[0-9]+$'
        ORDER BY n.s, n.pri DESC, n.weight DESC
    )
    SELECT c.entity_id, c.s, c.weight FROM clean c ORDER BY c.pri DESC, c.weight DESC LIMIT p_max
$$;
#line 226 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/walk_continuations.sql.in"
CREATE OR REPLACE FUNCTION walk_continuations(
        p_ctx bytea[], p_steps int DEFAULT 24, p_max_stride int DEFAULT 5,
        p_spread float8 DEFAULT 0.7, p_breadth int DEFAULT 10,
        p_seed bigint DEFAULT NULL)
    RETURNS TABLE(step int, entity bytea, stride_used int, sep_entity bytea)
    AS 'MODULE_PATHNAME', 'pg_laplace_walk_continuations'
    LANGUAGE C VOLATILE;
#line 227 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/stream_reset.sql.in"
CREATE OR REPLACE FUNCTION stream_reset() RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_laplace_stream_reset'
    LANGUAGE C VOLATILE;
#line 228 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/walk_text.sql.in"
CREATE OR REPLACE FUNCTION walk_text(
        p_prompt text, p_steps int DEFAULT 24, p_max_stride int DEFAULT 5,
        p_spread float8 DEFAULT 0.7, p_breadth int DEFAULT 10,
        p_seed bigint DEFAULT NULL)
    RETURNS TABLE(step int, entity text, stride_used int)
    LANGUAGE sql VOLATILE
    SET search_path = @extschema@, public AS $$
    WITH ctx AS (
        SELECT array_agg(id ORDER BY ord) AS ids
        FROM prompt_state(p_prompt) WHERE id IS NOT NULL
    )






    SELECT g.step,
           render_text(g.entity, 64) || COALESCE(render_text(g.sep_entity, 8), ''),
           g.stride_used
    FROM ctx, walk_continuations(ctx.ids, p_steps, p_max_stride, p_spread, p_breadth,
                              COALESCE(p_seed,
                                  ('x' || encode(substring(
                                       public.laplace_hash128_blake3(convert_to(p_prompt, 'UTF8'))
                                       for 8), 'hex'))::bit(64)::bigint)) g
    WHERE ctx.ids IS NOT NULL
$$;
#line 229 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/generate.sql.in"
CREATE OR REPLACE FUNCTION generate(
        p_prompt text, p_steps int DEFAULT 40, p_max_stride int DEFAULT 5,
        p_spread float8 DEFAULT 0.7, p_breadth int DEFAULT 12,
        p_seed bigint DEFAULT NULL)
    RETURNS text
    LANGUAGE sql VOLATILE
    SET search_path = @extschema@, public AS $$
    SELECT btrim(string_agg(entity, '' ORDER BY step))
    FROM walk_text(p_prompt, p_steps, p_max_stride, p_spread, p_breadth, p_seed)
$$;
#line 230 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/continue_text.sql.in"
CREATE OR REPLACE FUNCTION continue_text(
        p_prompt text, p_steps int DEFAULT 16, p_window int DEFAULT 3,
        p_spread double precision DEFAULT 0.6, p_breadth int DEFAULT 8,
        p_stop text[] DEFAULT '{}', p_boost double precision DEFAULT 0,
        p_require_pos boolean DEFAULT true)
    RETURNS TABLE(step int, entity text, mu numeric)
    LANGUAGE sql VOLATILE
    SET search_path = @extschema@, public AS $$
    SELECT g.step, g.entity, NULL::numeric AS mu
    FROM walk_text(p_prompt, p_steps, GREATEST(p_window, 1), p_spread, p_breadth) g
$$;
#line 231 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/variant_walk.sql.in"
CREATE OR REPLACE FUNCTION variant_walk(p_id bytea, p_swap float8 DEFAULT 0.3,
                          p_k int DEFAULT 6, p_depth int DEFAULT 4)
    RETURNS text
    AS 'MODULE_PATHNAME', 'pg_laplace_variant_walk'
    LANGUAGE C VOLATILE;
#line 232 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/respell_variant.sql.in"
CREATE OR REPLACE FUNCTION respell_variant(p_node_type text, p_modality text DEFAULT 'c-sharp',
                     p_substitution_rate float8 DEFAULT 0.3, p_k int DEFAULT 6, p_depth int DEFAULT 4)
    RETURNS text
    AS 'MODULE_PATHNAME', 'pg_laplace_respell_variant'
    LANGUAGE C VOLATILE;
#line 233 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/generation/recall_trajectories.sql.in"
CREATE OR REPLACE FUNCTION recall_trajectories(p_word text, p_limit int DEFAULT 6)
    RETURNS TABLE(answer text)
    LANGUAGE sql STABLE
    SET search_path = @extschema@, public AS $$
  WITH wid AS (SELECT word_id(p_word) AS id),
  ids AS (
    SELECT ph.entity_id
    FROM wid
    JOIN physicalities ph
      ON ph.type = 1 AND ph.trajectory IS NOT NULL
     AND public.laplace_trajectory_constituent_ids(ph.trajectory) @> ARRAY[wid.id]
    JOIN entities e ON e.id = ph.entity_id AND e.tier = 3
    ORDER BY abs(ph.n_constituents - 7)
    LIMIT p_limit
  )
  SELECT left(replace(render_text(entity_id, 300), chr(10), ' '), 180) FROM ids
$$;
#line 234 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ingest/laplace_apply_batch.sql.in"
CREATE OR REPLACE FUNCTION laplace_apply_batch(p_stage_prefix text)
    RETURNS TABLE (entities_inserted bigint,
                   physicalities_inserted bigint,
                   attestations_inserted bigint,
                   attestations_folded bigint,
                   entities_skipped bigint,
                   physicalities_skipped bigint)
    AS 'MODULE_PATHNAME', 'pg_laplace_apply_batch'
    LANGUAGE C VOLATILE;
#line 235 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ingest/drop_apply_batch_merge_entities.sql.in"
DROP FUNCTION IF EXISTS _apply_batch_merge_entities(regclass);
#line 236 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ingest/drop_apply_batch_merge_physicalities.sql.in"
DROP FUNCTION IF EXISTS _apply_batch_merge_physicalities(regclass);
#line 237 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/ingest/drop_apply_batch_merge_attestations.sql.in"
DROP FUNCTION IF EXISTS _apply_batch_merge_attestations(regclass);
#line 238 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/consensus_walk_edges.sql.in"
CREATE OR REPLACE FUNCTION consensus_walk_edges(
    p_subject bytea,
    p_type bytea DEFAULT NULL,
    p_limit int DEFAULT 40,
    p_exclude bytea[] DEFAULT '{}')
    RETURNS TABLE(object_id bytea, type_id bytea, rating bigint, rd bigint, witness_count bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT c.object_id, c.type_id, c.rating, c.rd, c.witness_count
    FROM consensus c
    WHERE c.subject_id = p_subject
      AND c.object_id IS NOT NULL
      AND (p_type IS NULL OR c.type_id = p_type)
      AND NOT refuted(c.rating, c.rd)
      AND NOT (c.object_id = ANY (COALESCE(p_exclude, '{}'::bytea[])))
      AND NOT EXISTS (
          SELECT 1 FROM entities et
          WHERE et.id = c.object_id AND et.type_id = entity_type_id('RelationType'))
    ORDER BY relation_rank_resolved(c.type_id) * eff_mu(c.rating, c.rd) DESC
    LIMIT p_limit
$$;
#line 239 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/consensus_neighbors_directed.sql.in"
CREATE OR REPLACE FUNCTION consensus_neighbors_directed(
    p_subject bytea,
    p_types bytea[],
    p_limit int)
    RETURNS TABLE(nbr bytea, rating bigint, rd bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT c.object_id AS nbr, c.rating, c.rd
    FROM consensus c
    WHERE c.subject_id = p_subject
      AND c.object_id IS NOT NULL
      AND c.type_id = ANY (p_types)
      AND NOT refuted(c.rating, c.rd)
    ORDER BY eff_mu(c.rating, c.rd) DESC
    LIMIT p_limit
$$;
#line 240 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/consensus_neighbors_undirected.sql.in"
CREATE OR REPLACE FUNCTION consensus_neighbors_undirected(
    p_subject bytea,
    p_types bytea[],
    p_limit int)
    RETURNS TABLE(nbr bytea, rating bigint, rd bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT nbr, rating, rd FROM (
        SELECT c.object_id AS nbr, c.rating, c.rd
        FROM consensus c
        WHERE c.subject_id = p_subject
          AND c.object_id IS NOT NULL
          AND c.type_id = ANY (p_types)
          AND NOT refuted(c.rating, c.rd)
        UNION ALL
        SELECT c.subject_id AS nbr, c.rating, c.rd
        FROM consensus c
        WHERE c.object_id = p_subject
          AND c.type_id = ANY (p_types)
          AND NOT refuted(c.rating, c.rd)
    ) e
    ORDER BY eff_mu(rating, rd) DESC
    LIMIT p_limit
$$;
#line 241 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/foundry_crawl_neighbors.sql.in"
CREATE OR REPLACE FUNCTION foundry_crawl_neighbors(
    p_subject bytea,
    p_limit int,
    p_types bytea[] DEFAULT NULL)
    RETURNS TABLE(object_id bytea, tier smallint, rating bigint, rd bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT c.object_id, e.tier, c.rating, c.rd
    FROM consensus c
    JOIN entities e ON e.id = c.object_id
    WHERE c.subject_id = p_subject
      AND c.object_id IS NOT NULL
      AND (p_types IS NULL OR c.type_id = ANY (p_types))
    ORDER BY eff_mu(c.rating, c.rd) DESC
    LIMIT p_limit
$$;
#line 242 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/consensus_step_edge.sql.in"
CREATE OR REPLACE FUNCTION consensus_step_edge(
    p_from bytea,
    p_to bytea)
    RETURNS TABLE(type_id bytea, subject_id bytea, rating bigint, rd bigint, witness_count bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT c.type_id, c.subject_id, c.rating, c.rd, c.witness_count
    FROM consensus c
    WHERE NOT refuted(c.rating, c.rd)
      AND (
          (c.subject_id = p_from AND c.object_id = p_to)
          OR (c.subject_id = p_to AND c.object_id = p_from))
    ORDER BY eff_mu(c.rating, c.rd) DESC
    LIMIT 1
$$;
#line 243 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/entity_exists.sql.in"
CREATE OR REPLACE FUNCTION entity_exists(p_id bytea)
    RETURNS boolean
    LANGUAGE sql STABLE PARALLEL SAFE STRICT
    SET search_path = @extschema@, public AS $$
    SELECT EXISTS (SELECT 1 FROM entities e WHERE e.id = p_id)
$$;
#line 244 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/realize/realize_synset_lemma.sql.in"
CREATE OR REPLACE FUNCTION _realize_synset_lemma(p_id bytea, p_lang bytea DEFAULT NULL)
    RETURNS text
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT q.s FROM (
        SELECT render_text(hs.subject_id, 24) AS s,
               (lang.object_id IS NOT NULL) AS lp,
               eff_mu(hs.rating, hs.rd) AS mu
        FROM consensus io
        JOIN consensus hs ON hs.object_id = io.subject_id
          AND hs.type_id = relation_type_id('HAS_SENSE')
        LEFT JOIN consensus lang ON lang.subject_id = hs.subject_id
          AND lang.type_id = relation_type_id('HAS_LANGUAGE')
          AND lang.object_id = p_lang
        WHERE io.object_id = p_id
          AND io.type_id = relation_type_id('IS_SENSE_OF')
          AND NOT refuted(io.rating, io.rd)
    ) q
    WHERE q.s IS NOT NULL AND q.s <> ''
    ORDER BY q.lp DESC, q.mu DESC
    LIMIT 1
$$;
#line 245 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/realize/realize_translation.sql.in"
CREATE OR REPLACE FUNCTION _realize_translation(p_id bytea, p_lang bytea DEFAULT NULL)
    RETURNS text
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT q.s FROM (
        SELECT render_text(m.object_id, 24) AS s,
               (lang.object_id IS NOT NULL) AS lp,
               eff_mu(m.rating, m.rd) AS mu
        FROM consensus m
        LEFT JOIN consensus lang ON lang.subject_id = m.object_id
          AND lang.type_id = relation_type_id('HAS_LANGUAGE')
          AND lang.object_id = p_lang
        WHERE m.subject_id = p_id
          AND m.type_id = relation_type_id('IS_TRANSLATION_OF')
          AND NOT refuted(m.rating, m.rd)
    ) q
    WHERE q.s IS NOT NULL AND q.s <> ''
    ORDER BY q.lp DESC, q.mu DESC
    LIMIT 1
$$;
#line 246 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/realize/realize_has_name.sql.in"
CREATE OR REPLACE FUNCTION _realize_has_name(p_id bytea, p_lang bytea DEFAULT NULL)
    RETURNS text
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT q.s FROM (
        SELECT render_text(nm.object_id, 24) AS s,
               (lang.object_id IS NOT NULL) AS lp,
               (nm.type_id = relation_type_id('HAS_NAME')) AS prim,
               eff_mu(nm.rating, nm.rd) AS mu
        FROM consensus nm
        LEFT JOIN consensus lang ON lang.subject_id = nm.object_id
          AND lang.type_id = relation_type_id('HAS_LANGUAGE')
          AND lang.object_id = p_lang
        WHERE nm.subject_id = p_id
          AND nm.type_id IN (relation_type_id('HAS_NAME'),
                             relation_type_id('HAS_NAME_ALIAS'))
          AND NOT refuted(nm.rating, nm.rd)
    ) q
    WHERE q.s IS NOT NULL AND q.s <> ''
    ORDER BY q.lp DESC, q.prim DESC, q.mu DESC
    LIMIT 1
$$;
#line 247 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/realize/realize_canonical.sql.in"
CREATE OR REPLACE FUNCTION _realize_canonical(p_id bytea)
    RETURNS text
    LANGUAGE sql STABLE PARALLEL SAFE STRICT
    SET search_path = @extschema@, public AS $$
    SELECT regexp_replace(n.name, '^substrate/[a-z_]+/(.+)/v1$', '\1')
    FROM canonical_names n
    WHERE n.id = p_id AND n.name LIKE 'substrate/%'
$$;
#line 248 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/realize/realize_defines.sql.in"
CREATE OR REPLACE FUNCTION _realize_defines(p_id bytea)
    RETURNS text
    LANGUAGE sql STABLE PARALLEL SAFE STRICT
    SET search_path = @extschema@, public AS $$
    SELECT render_text(g.object_id, 24)
    FROM consensus g
    WHERE g.subject_id = p_id
      AND g.type_id = relation_type_id('DEFINES')
      AND NOT refuted(g.rating, g.rd)
    ORDER BY eff_mu(g.rating, g.rd) DESC
    LIMIT 1
$$;
#line 249 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/realize/realize.sql.in"
CREATE OR REPLACE FUNCTION realize(p_id bytea, p_lang bytea DEFAULT NULL)
    RETURNS text
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT COALESCE(
        _realize_synset_lemma(p_id, p_lang),
        NULLIF(render_text(p_id, 24), ''),
        _realize_translation(p_id, p_lang),
        _realize_has_name(p_id, p_lang),
        _realize_canonical(p_id),
        _realize_defines(p_id))
$$;
#line 250 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/shared_objects.sql.in"
CREATE OR REPLACE FUNCTION shared_objects(p_subjects bytea[], p_type bytea DEFAULT NULL,
                                     p_limit int DEFAULT 40)
    RETURNS TABLE(object_id bytea, support int, total_mu numeric)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT c.object_id,
           count(DISTINCT c.subject_id)::int,
           round((sum(eff_mu(c.rating, c.rd)) / 1e9)::numeric, 3)
    FROM consensus c
    WHERE c.subject_id = ANY (p_subjects)
      AND c.object_id IS NOT NULL
      AND NOT refuted(c.rating, c.rd)
      AND (p_type IS NULL OR c.type_id = p_type)
    GROUP BY c.object_id
    ORDER BY count(DISTINCT c.subject_id) DESC, sum(eff_mu(c.rating, c.rd)) DESC
    LIMIT p_limit
$$;
#line 251 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/related.sql.in"
CREATE OR REPLACE FUNCTION related(p_word bytea, p_type bytea, p_lang bytea DEFAULT NULL,
                                   p_limit int DEFAULT 10)
    RETURNS TABLE(fact text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    WITH subj(id) AS (
        SELECT p_word UNION SELECT sn.synset_id FROM senses(p_word) sn
    ), top AS (
        SELECT cc.object_id, cc.rating, cc.rd, cc.witness_count
        FROM subj s CROSS JOIN LATERAL (
            SELECT c.object_id, c.rating, c.rd, c.witness_count
            FROM consensus c
            WHERE c.subject_id = s.id AND c.type_id = p_type
              AND c.object_id IS NOT NULL AND NOT refuted(c.rating, c.rd)
            ORDER BY eff_mu(c.rating, c.rd) DESC LIMIT p_limit
        ) cc
    )
    SELECT realize(t.object_id, COALESCE(p_lang, word_language(p_word))),
           eff_mu_display(t.rating, t.rd), t.witness_count
    FROM top t
    ORDER BY eff_mu(t.rating, t.rd) DESC
    LIMIT p_limit
$$;
#line 252 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/related_in.sql.in"
CREATE OR REPLACE FUNCTION related_in(p_word bytea, p_type bytea, p_lang bytea DEFAULT NULL,
                                      p_limit int DEFAULT 10)
    RETURNS TABLE(fact text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    WITH subj(id) AS (
        SELECT p_word UNION SELECT sn.synset_id FROM senses(p_word) sn
    ), top AS (
        SELECT cc.subject_id, cc.rating, cc.rd, cc.witness_count
        FROM subj s CROSS JOIN LATERAL (
            SELECT c.subject_id, c.rating, c.rd, c.witness_count
            FROM consensus c
            WHERE c.object_id = s.id AND c.type_id = p_type
              AND NOT refuted(c.rating, c.rd)
            ORDER BY eff_mu(c.rating, c.rd) DESC LIMIT p_limit
        ) cc
    )
    SELECT realize(t.subject_id, COALESCE(p_lang, word_language(p_word))),
           eff_mu_display(t.rating, t.rd), t.witness_count
    FROM top t
    ORDER BY eff_mu(t.rating, t.rd) DESC
    LIMIT p_limit
$$;
#line 253 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/salient_facts.sql.in"
CREATE OR REPLACE FUNCTION salient_facts(p_word bytea, p_lang bytea DEFAULT NULL,
                                    p_limit int DEFAULT 24)
    RETURNS TABLE(type text, fact text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    WITH subj(id) AS (
        SELECT p_word UNION SELECT sn.synset_id FROM senses(p_word) sn
    ), top AS (
        SELECT cc.type_id, cc.object_id, cc.rating, cc.rd, cc.witness_count
        FROM subj s CROSS JOIN LATERAL (
            SELECT c.type_id, c.object_id, c.rating, c.rd, c.witness_count
            FROM consensus c
            WHERE c.subject_id = s.id AND c.object_id IS NOT NULL
              AND NOT refuted(c.rating, c.rd)
              AND EXISTS (
                  SELECT 1 FROM entities et
                  WHERE et.id = c.type_id AND et.type_id = entity_type_id('RelationType'))
              AND NOT relation_type_in_family(c.type_id, 'HAS_POS')
              AND c.type_id NOT IN (
                relation_type_id('HAS_SENSE'), relation_type_id('IS_SENSE_OF'),
                relation_type_id('HAS_LANGUAGE'), relation_type_id('PRECEDES'),
                relation_type_id('FOLLOWS'), relation_type_id('CO_OCCURS_WITH'),
                relation_type_id('OCCURS_IN_CONTEXT'), relation_type_id('HAS_LEX_CATEGORY'),
                relation_type_id('HAS_FEATURE'), relation_type_id('HAS_VERB_FRAME'),
                relation_type_id('IS_LEMMA_OF'), relation_type_id('IS_TRANSLATION_OF'))
              AND NOT EXISTS (
                  SELECT 1 FROM entities et
                  WHERE et.id = c.object_id AND et.type_id = entity_type_id('RelationType'))
            ORDER BY eff_mu(c.rating, c.rd) DESC LIMIT p_limit * 3
        ) cc
    )
    SELECT d.type, d.fact, d.eff_mu, d.witnesses FROM (
        SELECT type_label(t.type_id) AS type,
               realize(t.object_id, COALESCE(p_lang, word_language(p_word))) AS fact,
               eff_mu_display(t.rating, t.rd) AS eff_mu, t.witness_count AS witnesses,
               eff_mu(t.rating, t.rd) AS sort_mu
        FROM top t
    ) d
    WHERE d.fact IS NOT NULL AND d.fact <> ''
      AND right(d.fact, 1) <> U&'\2026' AND right(d.type, 1) <> U&'\2026'
    ORDER BY d.sort_mu DESC
    LIMIT p_limit
$$;
#line 254 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/usage_overlap.sql.in"
CREATE OR REPLACE FUNCTION usage_overlap(p_x bytea, p_y bytea)
    RETURNS bigint
    LANGUAGE sql STABLE PARALLEL SAFE STRICT
    SET search_path = @extschema@, public AS $$
    WITH xn(n) AS (
        SELECT object_id FROM consensus
         WHERE subject_id = p_x AND type_id = relation_type_id('PRECEDES')
        UNION SELECT subject_id FROM consensus
         WHERE object_id = p_x AND type_id = relation_type_id('PRECEDES')
    ), yn(n) AS (
        SELECT object_id FROM consensus
         WHERE subject_id = p_y AND type_id = relation_type_id('PRECEDES')
        UNION SELECT subject_id FROM consensus
         WHERE object_id = p_y AND type_id = relation_type_id('PRECEDES')
    )
    SELECT count(*)::bigint FROM xn JOIN yn USING (n)
$$;
#line 255 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/relate_path.sql.in"
CREATE OR REPLACE FUNCTION relate_path(p_x bytea, p_y bytea, p_depth int DEFAULT 7)
    RETURNS TABLE(chain text, path_mu numeric, plane text)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    WITH RECURSIVE
    lat(k) AS (SELECT relation_type_id(n) FROM unnest(ARRAY[
        'IS_SYNONYM_OF','IS_SIMILAR_TO','IS_ANTONYM_OF','HAS_PART','HAS_MEMBER',
        'HAS_SUBSTANCE','DERIVATIONALLY_RELATED','PERTAINS_TO','ALSO_SEE',
        'IN_VERB_GROUP_WITH','HAS_ATTRIBUTE']) n),
    up_k(k) AS (SELECT relation_type_id(n) FROM unnest(ARRAY['IS_A','IS_INSTANCE_OF']) n),
    x_top AS (SELECT sn.synset_id FROM senses(p_x) sn
              ORDER BY sn.eff_mu DESC NULLS LAST LIMIT 1),
    y_top AS (SELECT sn.synset_id FROM senses(p_y) sn
              ORDER BY sn.eff_mu DESC NULLS LAST LIMIT 1),
    xset(id) AS (SELECT p_x UNION SELECT synset_id FROM x_top),
    yset(id) AS (SELECT p_y UNION SELECT synset_id FROM y_top),
    direct AS (
        SELECT realize_path(ARRAY[x.id, c.object_id], ARRAY[c.type_id], ARRAY[1],
                          COALESCE(word_language(p_x), word_language(p_y))) AS chain,
               eff_mu_display(c.rating, c.rd) AS mu, 1 AS len,
               type_label(c.type_id) AS plane
        FROM xset x JOIN consensus c ON c.subject_id = x.id
          AND c.object_id IN (SELECT id FROM yset)
          AND c.type_id IN (SELECT k FROM lat) AND NOT refuted(c.rating, c.rd)
        UNION ALL
        SELECT realize_path(ARRAY[x.id, c.subject_id], ARRAY[c.type_id], ARRAY[-1],
                          COALESCE(word_language(p_x), word_language(p_y))),
               eff_mu_display(c.rating, c.rd), 1, type_label(c.type_id)
        FROM xset x JOIN consensus c ON c.object_id = x.id
          AND c.subject_id IN (SELECT id FROM yset)
          AND c.type_id IN (SELECT k FROM lat) AND NOT refuted(c.rating, c.rd)
    ),
    ux(node, path, types, mu, d) AS (
        SELECT id, ARRAY[id], ARRAY[]::bytea[], NULL::numeric, 0 FROM xset
        UNION ALL
        SELECT c.object_id, u.path || c.object_id, u.types || c.type_id,
               LEAST(COALESCE(u.mu, eff_mu_display(c.rating, c.rd)),
                     eff_mu_display(c.rating, c.rd)), u.d + 1
        FROM ux u JOIN consensus c ON c.subject_id = u.node
          AND c.type_id IN (SELECT k FROM up_k) AND c.object_id IS NOT NULL
          AND NOT refuted(c.rating, c.rd) AND NOT (c.object_id = ANY (u.path))
        WHERE u.d < p_depth
    ),
    uy(node, path, types, mu, d) AS (
        SELECT id, ARRAY[id], ARRAY[]::bytea[], NULL::numeric, 0 FROM yset
        UNION ALL
        SELECT c.object_id, u.path || c.object_id, u.types || c.type_id,
               LEAST(COALESCE(u.mu, eff_mu_display(c.rating, c.rd)),
                     eff_mu_display(c.rating, c.rd)), u.d + 1
        FROM uy u JOIN consensus c ON c.subject_id = u.node
          AND c.type_id IN (SELECT k FROM up_k) AND c.object_id IS NOT NULL
          AND NOT refuted(c.rating, c.rd) AND NOT (c.object_id = ANY (u.path))
        WHERE u.d < p_depth
    ),
    lca_pick AS (
        SELECT ux.path AS xp, ux.types AS xk, uy.path AS yp, uy.types AS yk,
               LEAST(ux.mu, uy.mu) AS mu, ux.d + uy.d AS len
        FROM ux JOIN uy ON ux.node = uy.node
        WHERE ux.d > 0 OR uy.d > 0
        ORDER BY ux.d + uy.d, LEAST(ux.mu, uy.mu) DESC NULLS LAST LIMIT 1
    ),
    lca AS (
        SELECT realize_path(
                   xp || array_reverse(yp[1:cardinality(yp)-1]),
                   xk || array_reverse(yk),
                   (SELECT COALESCE(array_agg(1), ARRAY[]::int[]) FROM generate_subscripts(xk, 1))
                       || (SELECT COALESCE(array_agg(-1), ARRAY[]::int[]) FROM generate_subscripts(yk, 1)),
                   COALESCE(word_language(p_x), word_language(p_y))) AS chain,
               mu, len, 'taxonomy'::text AS plane FROM lca_pick
    )
    SELECT a.chain, a.mu, a.plane FROM (
        SELECT chain, mu, len, plane FROM direct
        UNION ALL SELECT chain, mu, len, plane FROM lca
    ) a ORDER BY a.len, a.mu DESC NULLS LAST LIMIT 1
$$;
#line 256 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/relation_summary.sql.in"
CREATE OR REPLACE FUNCTION relation_summary(p_x bytea, p_y bytea)
    RETURNS TABLE(relation text, plane text, mu numeric, usage bigint,
                  geodesic double precision, verdict text)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT r.chain, r.plane, r.path_mu, COALESCE(u.n, 0), g.geo,
           concat_ws('',
             CASE WHEN r.chain IS NOT NULL THEN 'related via ' || r.plane
                  ELSE 'no witnessed conceptual path' END,
             CASE WHEN COALESCE(u.n, 0) >= 10 THEN '; strong shared usage (' || u.n || ')'
                  WHEN COALESCE(u.n, 0) > 0 THEN '; some shared usage (' || u.n || ')'
                  ELSE '' END,
             CASE WHEN g.geo IS NOT NULL AND g.geo < 0.4 THEN '; structurally near'
                  ELSE '' END)
    FROM (SELECT 1) one
    LEFT JOIN LATERAL (SELECT * FROM relate_path(p_x, p_y) LIMIT 1) r ON true
    LEFT JOIN LATERAL (SELECT usage_overlap(p_x, p_y) AS n) u ON true
    LEFT JOIN LATERAL (
        SELECT public.laplace_angular_distance_4d(
            (SELECT coord FROM physicalities WHERE entity_id = p_x AND type = 1
             AND coord IS NOT NULL LIMIT 1),
            (SELECT coord FROM physicalities WHERE entity_id = p_y AND type = 1
             AND coord IS NOT NULL LIMIT 1)) AS geo) g ON true
$$;
#line 257 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/consensus/gaps.sql.in"
CREATE OR REPLACE FUNCTION gaps(p_word bytea)
    RETURNS TABLE(missing_arena text)
    LANGUAGE sql STABLE PARALLEL SAFE STRICT
    SET search_path = @extschema@, public AS $$
    WITH subj(id) AS (
        SELECT p_word UNION SELECT sn.synset_id FROM senses(p_word) sn
    ), expected(name) AS (SELECT unnest(ARRAY[
        'IS_A','HAS_PART','HAS_MEMBER','CAUSES','USED_FOR','IS_ANTONYM_OF',
        'IS_SIMILAR_TO','HAS_ATTRIBUTE','DERIVATIONALLY_RELATED']))
    SELECT e.name FROM expected e
    WHERE NOT EXISTS (
        SELECT 1 FROM consensus c JOIN subj s ON c.subject_id = s.id
        WHERE c.type_id = relation_type_id(e.name)
          AND NOT refuted(c.rating, c.rd))
$$;
#line 258 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/taxonomy/top_synset.sql.in"
CREATE OR REPLACE FUNCTION top_synset(p_word bytea)
    RETURNS bytea
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT sn.synset_id FROM senses(p_word) sn
    ORDER BY sn.eff_mu DESC NULLS LAST LIMIT 1
$$;
#line 259 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/taxonomy/synset_gloss.sql.in"
CREATE OR REPLACE FUNCTION synset_gloss(p_synset bytea)
    RETURNS text
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT render_text(g.object_id, 24)
    FROM consensus g
    WHERE g.subject_id = p_synset
      AND g.type_id = relation_type_id('DEFINES')
      AND NOT refuted(g.rating, g.rd)
    ORDER BY eff_mu(g.rating, g.rd) DESC
    LIMIT 1
$$;
#line 260 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/taxonomy/consensus_taxonomy_edges.sql.in"
CREATE OR REPLACE FUNCTION consensus_taxonomy_edges(p_subject bytea, p_types bytea[])
    RETURNS TABLE(object_id bytea, type_id bytea, rating bigint, rd bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT c.object_id, c.type_id, c.rating, c.rd
    FROM consensus c
    WHERE c.subject_id = p_subject
      AND c.type_id = ANY (p_types)
      AND c.object_id IS NOT NULL
      AND NOT refuted(c.rating, c.rd)
      AND NOT EXISTS (
          SELECT 1 FROM entities et
          WHERE et.id = c.object_id AND et.type_id = entity_type_id('RelationType'))
$$;
#line 261 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/taxonomy/translation_sources.sql.in"
CREATE OR REPLACE FUNCTION translation_sources(p_object bytea)
    RETURNS TABLE(subject_id bytea)
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT c.subject_id
    FROM consensus c
    WHERE c.type_id = relation_type_id('IS_TRANSLATION_OF')
      AND c.object_id = p_object
      AND NOT refuted(c.rating, c.rd)
$$;
#line 262 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/contrast/consensus_subject_edges.sql.in"
CREATE OR REPLACE FUNCTION consensus_subject_edges(p_subject bytea)
    RETURNS TABLE(type_id bytea, object_id bytea, rating bigint, rd bigint)
    LANGUAGE sql STABLE PARALLEL SAFE STRICT
    SET search_path = @extschema@, public AS $$
    SELECT c.type_id, c.object_id, c.rating, c.rd
    FROM v_consensus_resolved c
    WHERE c.subject_id = p_subject
      AND c.object_id IS NOT NULL
      AND NOT refuted(c.rating, c.rd)
$$;
#line 263 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/contrast/physicality_count.sql.in"
CREATE OR REPLACE FUNCTION physicality_count(p_entity bytea)
    RETURNS bigint
    LANGUAGE sql STABLE PARALLEL SAFE STRICT
    SET search_path = @extschema@, public AS $$
    SELECT count(*)::bigint FROM physicalities pp WHERE pp.entity_id = p_entity
$$;
#line 264 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/variant/consensus_peer.sql.in"
CREATE OR REPLACE FUNCTION consensus_peer(p_id bytea, p_k int DEFAULT 6)
    RETURNS bytea
    LANGUAGE sql STABLE PARALLEL RESTRICTED
    SET search_path = @extschema@, public AS $$
    WITH my_type AS (
        SELECT type_id FROM entities WHERE id = p_id
    ), my_ctx AS (
        SELECT c.type_id AS rel,
               CASE WHEN c.subject_id = p_id THEN c.object_id ELSE c.subject_id END AS partner,
               (c.subject_id = p_id) AS as_subject,
               eff_mu(c.rating, c.rd) AS mu
        FROM consensus c
        WHERE (c.subject_id = p_id OR c.object_id = p_id)
          AND c.object_id IS NOT NULL
          AND NOT refuted(c.rating, c.rd)
    ), elected AS (
        SELECT x.id, sum(LEAST(m.mu, eff_mu(c2.rating, c2.rd))) AS score
        FROM my_ctx m
        JOIN consensus c2 ON c2.type_id = m.rel
          AND NOT refuted(c2.rating, c2.rd)
          AND ((m.as_subject AND c2.object_id = m.partner AND c2.subject_id <> p_id)
            OR (NOT m.as_subject AND c2.subject_id = m.partner AND c2.object_id <> p_id))
        JOIN entities x
          ON x.id = CASE WHEN m.as_subject THEN c2.subject_id ELSE c2.object_id END
        JOIN my_type t ON x.type_id = t.type_id
        GROUP BY x.id
        ORDER BY score DESC
        LIMIT p_k
    ), geometric AS (
        SELECT near.id FROM (
            SELECT e.type_id, p.coord, p.trajectory
            FROM entities e
            JOIN physicalities p ON p.entity_id = e.id AND p.type = 1
            WHERE e.id = p_id AND p.trajectory IS NOT NULL
            LIMIT 1
        ) me,
        LATERAL (
            SELECT knn.id, knn.t2 FROM (
                SELECT e2.id, p2.trajectory AS t2
                FROM entities e2
                JOIN physicalities p2 ON p2.entity_id = e2.id AND p2.type = 1
                WHERE e2.type_id = me.type_id AND e2.id <> p_id AND p2.trajectory IS NOT NULL
                ORDER BY p2.coord <<->> me.coord
                LIMIT 48
            ) knn
            ORDER BY public.laplace_frechet_4d(knn.t2, me.trajectory) ASC
            LIMIT p_k
        ) near
    )
    SELECT id FROM (
        SELECT id FROM elected
        UNION ALL
        SELECT id FROM geometric
        WHERE NOT EXISTS (SELECT 1 FROM elected)
    ) z
    ORDER BY public.laplace_hash128_blake3(z.id || p_id) LIMIT 1
$$;
#line 265 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/variant/trajectory_unpacked_points.sql.in"
CREATE OR REPLACE FUNCTION trajectory_unpacked_points(p_entity bytea)
    RETURNS TABLE(entity_id bytea, run_length int, ctier int)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT u.entity_id,
           GREATEST(u.run_length, 1)::int AS run_length,
           (SELECT e.tier FROM entities e WHERE e.id = u.entity_id) AS ctier
    FROM physicalities p,
    LATERAL public.ST_DumpPoints(p.trajectory) dp,
    LATERAL public.laplace_mantissa_unpack(dp.geom) u
    WHERE p.entity_id = p_entity AND p.type = 1 AND p.trajectory IS NOT NULL
    ORDER BY u.ordinal
$$;
#line 266 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/variant/entity_tier_of.sql.in"
CREATE OR REPLACE FUNCTION entity_tier_of(p_entity bytea)
    RETURNS int
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT tier FROM entities WHERE id = p_entity
$$;
#line 267 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/variant/entity_has_trajectory.sql.in"
CREATE OR REPLACE FUNCTION entity_has_trajectory(p_entity bytea)
    RETURNS boolean
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT EXISTS (
        SELECT 1 FROM physicalities p
        WHERE p.entity_id = p_entity AND p.type = 1 AND p.trajectory IS NOT NULL
    )
$$;
#line 268 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/variant/respell_variant_seed.sql.in"
CREATE OR REPLACE FUNCTION respell_variant_seed(p_modality text, p_node_type text)
    RETURNS bytea
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT e.id
    FROM canonical_names n
    JOIN entities e ON e.type_id = n.id
    JOIN physicalities p ON p.entity_id = e.id AND p.type = 1
      AND p.trajectory IS NOT NULL
    WHERE n.name = 'substrate/type/grammar/' || p_modality || '/' || p_node_type || '/v1'
    ORDER BY public.laplace_hash128_blake3(e.id || convert_to(p_modality || '/' || p_node_type, 'UTF8'))
    LIMIT 1
$$;
#line 269 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/variant/walk_completes_floor.sql.in"
CREATE OR REPLACE FUNCTION walk_completes_floor(p_subject bytea, p_limit int)
    RETURNS TABLE(object_id bytea, weight bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT c.object_id,
           GREATEST(eff_mu(c.rating, c.rd) / 1000000000, 1)::int8 AS weight
    FROM consensus c
    WHERE c.subject_id = p_subject
      AND c.object_id IS NOT NULL
      AND c.type_id = relation_type_id('COMPLETES_TO')
      AND NOT refuted(c.rating, c.rd)
    ORDER BY eff_mu(c.rating, c.rd) DESC
    LIMIT p_limit
$$;
#line 270 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/variant/session_last_resolved.sql.in"
CREATE OR REPLACE FUNCTION session_last_resolved(p_session bytea)
    RETURNS bytea
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT t.resolved_id
    FROM session_topics t
    WHERE t.session_id = p_session AND t.resolved_id IS NOT NULL
    ORDER BY t.ord DESC
    LIMIT 1
$$;
#line 271 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/variant/session_record_prompt.sql.in"
CREATE OR REPLACE FUNCTION session_record_prompt(
        p_session bytea, p_prompt text, p_resolved bytea DEFAULT NULL)
    RETURNS void
    LANGUAGE plpgsql VOLATILE
    SET search_path = @extschema@, public AS $$
BEGIN
    INSERT INTO session_topics (session_id, ord, prompt, resolved_id)
    VALUES (
        p_session,
        COALESCE((SELECT max(t.ord) FROM session_topics t WHERE t.session_id = p_session), 0) + 1,
        p_prompt,
        p_resolved
    );
END;
$$;
#line 272 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/recall/recall_context_exclude.sql.in"
CREATE OR REPLACE FUNCTION recall_context_exclude(p_prompt text, p_topic bytea)
    RETURNS bytea[]
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT COALESCE(array_agg(ps.id), ARRAY[]::bytea[])
    FROM prompt_state(p_prompt) ps
    WHERE ps.id <> p_topic
$$;
#line 273 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/recall/recall_define_response.sql.in"
CREATE OR REPLACE FUNCTION recall_define_response(p_prompt text, p_topic bytea)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT d.definition, d.eff_mu, d.witnesses
    FROM define(p_topic, recall_context_exclude(p_prompt, p_topic)) d
$$;
#line 274 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/recall/recall_what_is_response.sql.in"
CREATE OR REPLACE FUNCTION recall_what_is_response(p_prompt text, p_topic bytea)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT label(p_topic) || ': ' || d.definition, d.eff_mu, d.witnesses
    FROM define(p_topic, recall_context_exclude(p_prompt, p_topic), 3) d
    UNION ALL
    SELECT repeat('  ', h.depth) || E'\u2192 is ' || COALESCE(h.hypernym, '?')
           || COALESCE(': ' || h.gloss, ''), NULL::numeric, NULL::bigint
    FROM hypernyms(p_topic, 6) h
    WHERE h.depth > 0
$$;
#line 275 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/recall/recall_translate_response.sql.in"
CREATE OR REPLACE FUNCTION recall_translate_response(p_topic bytea, p_lang text DEFAULT NULL)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT t.translation || ' [' || COALESCE(t.language, '?') || ']', t.eff_mu, t.witnesses
    FROM translate_to(p_topic, p_lang) t
$$;
#line 276 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/recall/recall_languages_response.sql.in"
CREATE OR REPLACE FUNCTION recall_languages_response(p_topic bytea)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT lc.reply, lc.eff_mu, lc.witnesses
    FROM language_coverage(p_topic) lc
$$;
#line 277 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/recall/recall_synonyms_response.sql.in"
CREATE OR REPLACE FUNCTION recall_synonyms_response(p_topic bytea)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT s.synonym, s.eff_mu, s.witnesses FROM synonyms(p_topic) s
$$;
#line 278 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/recall/recall_examples_response.sql.in"
CREATE OR REPLACE FUNCTION recall_examples_response(p_topic bytea)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT replace(e.example, '%s', label(p_topic)), e.eff_mu, e.witnesses
    FROM examples(p_topic) e
$$;
#line 279 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/recall/recall_describe_response.sql.in"
CREATE OR REPLACE FUNCTION recall_describe_response(p_topic bytea)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT d.type || ': ' || COALESCE(d.fact, '?'), d.eff_mu, d.witnesses
    FROM salient_facts(p_topic, word_language(p_topic)) d
$$;
#line 280 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/recall/recall_related_response.sql.in"
CREATE OR REPLACE FUNCTION recall_related_response(p_topic bytea, p_type_name text)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT replace(f.fact, '%s', label(p_topic)), f.eff_mu, f.witnesses
    FROM related(p_topic, relation_type_id(p_type_name), word_language(p_topic)) f
$$;
#line 281 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/recall/recall_related_in_response.sql.in"
CREATE OR REPLACE FUNCTION recall_related_in_response(p_topic bytea, p_type_name text)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT f.fact, f.eff_mu, f.witnesses
    FROM related_in(p_topic, relation_type_id(p_type_name), word_language(p_topic)) f
$$;
#line 282 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/recall/recall_relation_summary_response.sql.in"
CREATE OR REPLACE FUNCTION recall_relation_summary_response(p_topic bytea, p_topic2 bytea)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT CASE WHEN rs.relation IS NOT NULL
                THEN rs.relation || '  [' || rs.verdict || ']'
                ELSE rs.verdict END,
           rs.mu, rs.usage
    FROM relation_summary(p_topic, p_topic2) rs
$$;
#line 283 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/recall/recall_walk_response.sql.in"
CREATE OR REPLACE FUNCTION recall_walk_response(p_topic bytea, p_mode text)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT COALESCE(realize(p_topic, word_language(p_topic)), label(p_topic))
           || string_agg(' —' || COALESCE(type_label(g.type_id), '?') || E'\u2192 '
                         || COALESCE(realize(g.entity_id, word_language(p_topic)),
                                     label(g.entity_id)), '' ORDER BY g.step),
           min(g.eff_mu), NULL::bigint
    FROM walk_strongest(p_topic,
         CASE WHEN p_mode = 'complete' THEN relation_type_id('COMPLETES_TO') END, 8) g
    HAVING count(*) > 0
$$;
#line 284 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/recall/recall_fallback_gloss.sql.in"
CREATE OR REPLACE FUNCTION recall_fallback_gloss(p_prompt text, p_topic bytea)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT label(p_topic) || ': ' || d.definition, d.eff_mu, d.witnesses
    FROM define(p_topic, recall_context_exclude(p_prompt, p_topic), 3) d
$$;
#line 285 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/recall/recall_fallback_walk.sql.in"
CREATE OR REPLACE FUNCTION recall_fallback_walk(p_topic bytea)
    RETURNS TABLE(reply text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT COALESCE(realize(p_topic, word_language(p_topic)), label(p_topic))
           || string_agg(' —' || COALESCE(type_label(g.type_id), '?') || E'\u2192 '
                         || COALESCE(realize(g.entity_id, word_language(p_topic)),
                                     label(g.entity_id)), '' ORDER BY g.step),
           min(g.eff_mu), NULL::bigint
    FROM walk_strongest(p_topic, NULL::bytea, 6) g
    HAVING count(*) > 0
$$;
#line 286 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/recall/recall_is_a_yes_reply.sql.in"
CREATE OR REPLACE FUNCTION recall_is_a_yes_reply(
        p_path bytea[], p_types bytea[], p_lang bytea DEFAULT NULL)
    RETURNS text
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT 'Yes — ' || realize_path(p_path, p_types, p_lang)
$$;
#line 287 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/recall/recall_is_a_no_reply.sql.in"
CREATE OR REPLACE FUNCTION recall_is_a_no_reply(p_from bytea, p_to bytea, p_lang bytea DEFAULT NULL)
    RETURNS text
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT 'No witnessed IS_A path from "'
           || COALESCE(realize(p_from, p_lang), label(p_from))
           || '" to "'
           || COALESCE(realize(p_to, p_lang), label(p_to)) || '".'
$$;
#line 288 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/corpus/corpus_trajectory_probe.sql.in"
CREATE OR REPLACE FUNCTION corpus_trajectory_probe()
    RETURNS TABLE(rows bigint, max_us bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT count(*)::int8,
           COALESCE((extract(epoch FROM max(observed_at)) * 1000000)::int8, 0)
    FROM physicalities
    WHERE type = 1 AND trajectory IS NOT NULL
$$;
#line 289 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/corpus/corpus_whitespace_vocab_indices.sql.in"
CREATE OR REPLACE FUNCTION corpus_whitespace_vocab_indices(p_vocab bytea[])
    RETURNS TABLE(vocab_idx int)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT (v.ord::int4 - 1) AS vocab_idx
    FROM unnest(p_vocab) WITH ORDINALITY v(id, ord),
    LATERAL (SELECT render_text_fast(v.id, 8) AS s) r
    WHERE is_all_whitespace(r.s)
$$;
#line 290 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/corpus/corpus_sentence_constituents.sql.in"
CREATE OR REPLACE FUNCTION corpus_sentence_constituents(
        p_document_source text DEFAULT '',
        p_max_orphans int DEFAULT 0)
    RETURNS TABLE(parent_id bytea, child_id bytea, run_length int)
    LANGUAGE sql STABLE PARALLEL RESTRICTED
    SET search_path = @extschema@, public AS $$
    WITH doc AS (
        SELECT e.id
        FROM entities e
        JOIN physicalities p
          ON p.entity_id = e.id AND p.type = 1 AND p.trajectory IS NOT NULL
        WHERE e.tier = 4
          AND (p_document_source = '' OR EXISTS (
              SELECT 1 FROM attestations a
              WHERE a.context_id = e.id
                AND a.source_id = source_id(p_document_source)))
    ), book_sent AS (
        SELECT DISTINCT tc.entity_id AS id
        FROM doc d
        JOIN physicalities pd
          ON pd.entity_id = d.id AND pd.type = 1 AND pd.trajectory IS NOT NULL
        CROSS JOIN LATERAL laplace_trajectory_constituents(pd.trajectory) tc
    ), orphan_sent AS (
        SELECT e.id
        FROM entities e
        JOIN physicalities ps
          ON ps.entity_id = e.id AND ps.type = 1 AND ps.trajectory IS NOT NULL
        WHERE e.tier = 3
          AND NOT EXISTS (SELECT 1 FROM book_sent bs WHERE bs.id = e.id)
        ORDER BY ps.observed_at DESC
        LIMIT GREATEST(p_max_orphans, 0)
    ), s AS (
        SELECT id FROM book_sent
        UNION ALL
        SELECT id FROM orphan_sent
        WHERE p_max_orphans > 0
    )
    SELECT pp.entity_id AS parent_id,
           u.entity_id AS child_id,
           GREATEST(u.run_length, 1)::int AS run_length
    FROM (
        SELECT DISTINCT ON (pp.entity_id) pp.entity_id, pp.trajectory
        FROM physicalities pp
        JOIN s ON s.id = pp.entity_id
        WHERE pp.type = 1 AND pp.trajectory IS NOT NULL
        ORDER BY pp.entity_id
    ) pp
    CROSS JOIN LATERAL laplace_trajectory_constituents(pp.trajectory) u
    WHERE laplace_vertex_tier(u.flags) <= 2
    ORDER BY pp.entity_id, u.ordinal
$$;
#line 291 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/geometry/entity_physicality_coord.sql.in"
CREATE OR REPLACE FUNCTION entity_physicality_coord(p_entity bytea)
    RETURNS geometry
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = @extschema@, public AS $$
    SELECT p.coord
    FROM physicalities p
    WHERE p.entity_id = p_entity AND p.type = 1 AND p.coord IS NOT NULL
    LIMIT 1
$$;
#line 292 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/geometry/structural_cluster.sql.in"
CREATE OR REPLACE FUNCTION structural_cluster(
        p_seed bytea, p_eps double precision DEFAULT 0.05, p_limit integer DEFAULT 200)
    RETURNS TABLE(entity_id bytea, label text, frechet double precision, physicalities bigint)
    LANGUAGE sql STABLE PARALLEL RESTRICTED
    SET search_path = @extschema@, public AS $$
    WITH seed AS (
        SELECT p.entity_id, p.coord, p.trajectory
        FROM physicalities p
        WHERE p.entity_id = p_seed AND p.type = 1
          AND p.coord IS NOT NULL AND p.trajectory IS NOT NULL
        LIMIT 1
    ), knn AS (
        SELECT p.entity_id, p.trajectory
        FROM physicalities p, seed s
        WHERE p.type = 1 AND p.coord IS NOT NULL
          AND p.entity_id <> s.entity_id AND p.trajectory IS NOT NULL
        ORDER BY p.coord <<->> s.coord
        LIMIT GREATEST(p_limit * 20, 2000)
    ), scored AS (
        SELECT DISTINCT ON (k.entity_id) k.entity_id,
               public.laplace_frechet_4d(k.trajectory, s.trajectory) AS fr
        FROM knn k
        CROSS JOIN seed s
        ORDER BY k.entity_id, public.laplace_frechet_4d(k.trajectory, s.trajectory)
    )
    SELECT sc.entity_id,
           render_text(sc.entity_id, 48),
           sc.fr,
           physicality_count(sc.entity_id) AS recurrence
    FROM scored sc
    WHERE sc.fr <= p_eps
    ORDER BY sc.fr
    LIMIT p_limit
$$;
#line 293 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/geometry/structural_cluster_batch.sql.in"
CREATE OR REPLACE FUNCTION structural_cluster_batch(
    p_seeds bytea[],
    p_eps double precision DEFAULT 0.05,
    p_limit integer DEFAULT 200)
    RETURNS TABLE(seed bytea, entity_id bytea, label text, frechet double precision, recurrence bigint)
    LANGUAGE plpgsql STABLE
    SET search_path = @extschema@, public AS $scb$
DECLARE
    v_seed bytea;
BEGIN
    IF p_seeds IS NULL THEN RETURN; END IF;
    FOREACH v_seed IN ARRAY p_seeds LOOP
        RETURN QUERY
        SELECT v_seed, sc.entity_id, sc.label, sc.frechet, sc.recurrence
        FROM structural_cluster(v_seed, p_eps, p_limit) sc;
    END LOOP;
END;
$scb$;
#line 294 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/highway/laplace_highway_match.sql.in"
CREATE OR REPLACE FUNCTION laplace_highway_match(mask bytea, band_mask bytea)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT mask IS NOT NULL
       AND band_mask IS NOT NULL
       AND (mask & band_mask) <> '\x00000000000000000000000000000000000000000000000000000000000000000000'::bytea
$$;
#line 295 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/highway/laplace_highway_popcount.sql.in"
CREATE OR REPLACE FUNCTION laplace_highway_popcount(mask bytea)
RETURNS integer
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT COALESCE(
        (SELECT sum(bit_count)::integer
         FROM (SELECT length(replace(get_byte(mask, g)::bit(8)::text, '0', '')) AS bit_count
               FROM generate_series(0, octet_length(mask) - 1) AS g) bits),
        0)
$$;
#line 296 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/highway/entities_highway_hash.sql.in"
CREATE INDEX IF NOT EXISTS entities_highway_hash
    ON entities USING hash (highway_mask)
    WHERE highway_mask IS NOT NULL;
#line 297 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/highway/attestations_highway_hash.sql.in"
CREATE INDEX IF NOT EXISTS attestations_highway_hash
    ON attestations USING hash (highway_mask)
    WHERE highway_mask IS NOT NULL;
#line 298 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/highway/entities_has_highway.sql.in"
CREATE INDEX IF NOT EXISTS entities_has_highway
    ON entities (id)
    WHERE highway_mask IS NOT NULL;
#line 299 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/highway/attestations_has_highway.sql.in"
CREATE INDEX IF NOT EXISTS attestations_has_highway
    ON attestations (subject_id, type_id)
    WHERE highway_mask IS NOT NULL;
#line 300 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\views/v_consensus_resolved.sql.in"
CREATE OR REPLACE VIEW v_consensus_resolved AS
SELECT c.id, c.subject_id, c.type_id, c.object_id,
       c.rating, c.rd, c.volatility, c.witness_count,
       c.last_observed_at,
       eff_mu(c.rating, c.rd) AS eff_mu_raw,
       eff_mu_display(c.rating, c.rd) AS eff_mu
FROM consensus c;
#line 301 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\views/v_consensus_edges.sql.in"
CREATE OR REPLACE VIEW v_consensus_edges AS
SELECT c.id, c.subject_id, c.type_id, c.object_id,
       c.rating, c.rd, c.volatility, c.witness_count,
       c.last_observed_at,
       c.eff_mu_raw, c.eff_mu
FROM v_consensus_resolved c
WHERE c.object_id IS NOT NULL
  AND c.eff_mu_raw > 0;
#line 302 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\views/v_entities_highway.sql.in"
CREATE OR REPLACE VIEW v_entities_highway AS
SELECT e.*,
       laplace_highway_popcount(e.highway_mask) AS highway_density
FROM entities e
WHERE e.highway_mask IS NOT NULL;
#line 303 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/analysis/laplace_eff_mu.sql.in"
CREATE OR REPLACE FUNCTION laplace_eff_mu(
    p_subject bytea, p_type bytea, p_object bytea)
RETURNS bigint
LANGUAGE sql STABLE SET search_path = @extschema@, public AS $$
    SELECT eff_mu(c.rating, c.rd)
    FROM consensus c
    WHERE c.subject_id = p_subject
      AND c.type_id = p_type
      AND c.object_id = p_object
    LIMIT 1
$$;
#line 304 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/analysis/laplace_entity_attestations.sql.in"
CREATE OR REPLACE FUNCTION laplace_entity_attestations(
    p_subject bytea, p_min_eff_mu_raw bigint DEFAULT 0)
RETURNS TABLE(type_id bytea, object_id bytea, eff_mu_raw bigint, eff_mu numeric, witnesses bigint)
LANGUAGE sql STABLE SET search_path = @extschema@, public AS $$
    SELECT c.type_id, c.object_id,
           eff_mu(c.rating, c.rd),
           eff_mu_display(c.rating, c.rd),
           c.witness_count
    FROM consensus c
    WHERE c.subject_id = p_subject
      AND c.object_id IS NOT NULL
      AND eff_mu(c.rating, c.rd) >= p_min_eff_mu_raw
    ORDER BY eff_mu(c.rating, c.rd) DESC
$$;
#line 305 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/analysis/laplace_entities_at_depth.sql.in"
CREATE OR REPLACE FUNCTION laplace_entities_at_depth(p_depth smallint)
RETURNS SETOF entities
LANGUAGE sql STABLE SET search_path = @extschema@, public AS $$
    SELECT * FROM entities WHERE tier = p_depth
$$;
#line 306 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/analysis/laplace_ancestry.sql.in"
CREATE OR REPLACE FUNCTION laplace_ancestry(
    p_entity_id bytea,
    p_band_mask bytea,
    p_max_depth integer DEFAULT 10)
RETURNS TABLE(ancestor_id bytea, depth integer, best_eff_mu numeric)
LANGUAGE sql STABLE SET search_path = @extschema@, public AS $$
    WITH RECURSIVE
        band_types AS MATERIALIZED (
            SELECT e.id FROM entities e
            WHERE laplace_highway_match(e.highway_mask, p_band_mask)
        ),
        ancestry(ancestor_id, depth, eff_mu_raw) AS (
            SELECT c.object_id, 1, eff_mu(c.rating, c.rd)
            FROM consensus c
            JOIN band_types bt ON bt.id = c.type_id
            WHERE c.subject_id = p_entity_id
              AND c.object_id IS NOT NULL
              AND eff_mu(c.rating, c.rd) > 0
            UNION ALL
            SELECT c.object_id, anc.depth + 1, eff_mu(c.rating, c.rd)
            FROM ancestry anc
            JOIN consensus c ON c.subject_id = anc.ancestor_id
            JOIN band_types bt ON bt.id = c.type_id
            WHERE c.object_id IS NOT NULL
              AND eff_mu(c.rating, c.rd) > 0
              AND anc.depth < p_max_depth
        )
    SELECT ancestor_id, min(depth)::integer, eff_mu_display(max(eff_mu_raw), 0)
    FROM ancestry
    GROUP BY ancestor_id
$$;
#line 307 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/analysis/laplace_translations.sql.in"
CREATE OR REPLACE FUNCTION laplace_translations(
    p_entity_id bytea, p_band_mask bytea)
RETURNS TABLE(translation_id bytea, shared_object_id bytea, eff_mu numeric)
LANGUAGE sql STABLE SET search_path = @extschema@, public AS $$
    WITH band_types AS MATERIALIZED (
        SELECT e.id FROM entities e
        WHERE laplace_highway_match(e.highway_mask, p_band_mask)
    )
    SELECT DISTINCT a2.subject_id, a1.object_id,
           eff_mu_display(a2.rating, a2.rd)
    FROM consensus a1
    JOIN band_types bt ON bt.id = a1.type_id
    JOIN consensus a2
      ON a2.object_id = a1.object_id
     AND a2.subject_id != a1.subject_id
     AND a2.type_id = a1.type_id
    WHERE a1.subject_id = p_entity_id
      AND a1.object_id IS NOT NULL
      AND eff_mu(a1.rating, a1.rd) > 0
      AND eff_mu(a2.rating, a2.rd) > 0
$$;
#line 308 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inference/laplace_l2_sq.sql.in"
CREATE OR REPLACE FUNCTION laplace_l2_sq(a geometry, b geometry)
RETURNS float8
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $$
    SELECT (ST_X(a) - ST_X(b))^2 + (ST_Y(a) - ST_Y(b))^2
         + (ST_Z(a) - ST_Z(b))^2 + (ST_M(a) - ST_M(b))^2
$$;
#line 309 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inference/laplace_attention_centroid.sql.in"
CREATE OR REPLACE FUNCTION laplace_attention_centroid(
    p_entity bytea,
    p_band_mask bytea)
RETURNS TABLE(cx float8, cy float8, cz float8, cm float8, total_weight float8)
LANGUAGE sql STABLE PARALLEL RESTRICTED SET search_path = @extschema@, public AS $$
    WITH band_types AS MATERIALIZED (
        SELECT e.id FROM entities e
        WHERE laplace_highway_match(e.highway_mask, p_band_mask)
    ),
    attended AS (
        SELECT c.object_id, eff_mu(c.rating, c.rd)::float8 AS w
        FROM consensus c
        JOIN band_types bt ON bt.id = c.type_id
        WHERE c.subject_id = p_entity
          AND c.object_id IS NOT NULL
          AND eff_mu(c.rating, c.rd) > 0
    ),
    weighted AS (
        SELECT p.coord, a.w
        FROM attended a
        JOIN physicalities p ON p.entity_id = a.object_id AND p.type = 1
        WHERE p.coord IS NOT NULL
    )
    SELECT
        SUM(ST_X(coord) * w) / NULLIF(SUM(w), 0),
        SUM(ST_Y(coord) * w) / NULLIF(SUM(w), 0),
        SUM(ST_Z(coord) * w) / NULLIF(SUM(w), 0),
        SUM(ST_M(coord) * w) / NULLIF(SUM(w), 0),
        SUM(w)
    FROM weighted
$$;
#line 310 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inference/laplace_nearest_entity.sql.in"
CREATE OR REPLACE FUNCTION laplace_nearest_entity(
    p_cx float8, p_cy float8, p_cz float8, p_cm float8,
    p_k integer DEFAULT 1, p_band_mask bytea DEFAULT NULL)
RETURNS TABLE(entity_id bytea, distance float8)
LANGUAGE sql STABLE PARALLEL RESTRICTED SET search_path = @extschema@, public AS $$
    SELECT p.entity_id,
           laplace_l2_sq(p.coord,
               public.ST_MakePoint(p_cx, p_cy, p_cz, p_cm)::geometry) AS dist
    FROM physicalities p
    JOIN entities e ON e.id = p.entity_id
    WHERE p.type = 1 AND p.coord IS NOT NULL
      AND (p_band_mask IS NULL OR laplace_highway_match(e.highway_mask, p_band_mask))
    ORDER BY p.coord <<->> public.ST_SetSRID(
        public.ST_MakePoint(p_cx, p_cy, p_cz, p_cm), 0)::geometry
    LIMIT p_k
$$;
#line 311 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inference/laplace_astar_path.sql.in"
CREATE OR REPLACE FUNCTION laplace_astar_path(
    p_source bytea, p_target bytea,
    p_type_ids bytea[] DEFAULT NULL,
    p_max_depth integer DEFAULT 20)
RETURNS TABLE(step integer, entity_id bytea, g float8)
LANGUAGE sql STABLE SET search_path = @extschema@, public AS $$
    SELECT step, entity_id, g
    FROM astar_path_raw(
        p_source,
        ARRAY[p_target],
        p_type_ids,
        p_max_depth,
        false)
$$;
#line 312 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inference/laplace_forward_step.sql.in"
CREATE OR REPLACE FUNCTION laplace_forward_step(
    p_entity bytea,
    p_band_mask bytea)
RETURNS bytea
LANGUAGE sql STABLE PARALLEL RESTRICTED SET search_path = @extschema@, public AS $$
    SELECT n.entity_id
    FROM laplace_attention_centroid(p_entity, p_band_mask) c
    JOIN LATERAL laplace_nearest_entity(c.cx, c.cy, c.cz, c.cm, 1, p_band_mask) n ON TRUE
    WHERE c.total_weight > 0
      AND n.entity_id IS NOT NULL
      AND n.entity_id <> p_entity
    LIMIT 1
$$;
#line 313 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inference/laplace_distill.sql.in"
CREATE OR REPLACE FUNCTION laplace_distill(
    p_band_mask bytea,
    p_min_eff_mu float8 DEFAULT 0.5)
RETURNS TABLE(subject_id bytea, type_id bytea, object_id bytea, eff_mu float8)
LANGUAGE sql STABLE PARALLEL RESTRICTED SET search_path = @extschema@, public AS $$
    WITH band_types AS MATERIALIZED (
        SELECT e.id FROM entities e
        WHERE laplace_highway_match(e.highway_mask, p_band_mask)
    )
    SELECT c.subject_id, c.type_id, c.object_id,
           (eff_mu(c.rating, c.rd)::float8 / 1e9) AS eff_mu
    FROM consensus c
    JOIN band_types bt ON bt.id = c.type_id
    WHERE c.object_id IS NOT NULL
      AND eff_mu(c.rating, c.rd)::float8 / 1e9 >= p_min_eff_mu
    ORDER BY c.subject_id, eff_mu(c.rating, c.rd) DESC
$$;
#line 314 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inference/laplace_prune.sql.in"
CREATE OR REPLACE FUNCTION laplace_prune(
    p_max_eff_mu bigint DEFAULT 0,
    p_dry_run boolean DEFAULT true)
RETURNS TABLE(affected_rows bigint)
LANGUAGE plpgsql VOLATILE SET search_path = @extschema@, public AS $$
BEGIN
    IF p_dry_run THEN
        RETURN QUERY
            SELECT count(*)::bigint FROM consensus
            WHERE object_id IS NOT NULL
              AND eff_mu(rating, rd) < p_max_eff_mu;
    ELSE
        RETURN QUERY
        WITH deleted AS (
            DELETE FROM consensus
            WHERE object_id IS NOT NULL
              AND eff_mu(rating, rd) < p_max_eff_mu
            RETURNING 1
        )
        SELECT count(*)::bigint FROM deleted;
    END IF;
END;
$$;
#line 315 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inference/laplace_witness.sql.in"
CREATE OR REPLACE FUNCTION laplace_witness(
    p_subject bytea, p_type bytea, p_object bytea,
    p_source bytea,
    p_score_fp1e9 bigint DEFAULT 1000000000)
RETURNS void
LANGUAGE plpgsql VOLATILE SET search_path = @extschema@, public AS $$
DECLARE
    v_id bytea := consensus_id(p_subject, p_type, p_object);
    v_rec record;
    v_new laplace_glicko2_result;
BEGIN
    SELECT rating, rd, volatility INTO v_rec FROM consensus WHERE id = v_id;
    IF NOT FOUND THEN
        INSERT INTO consensus(id, subject_id, type_id, object_id,
            rating, rd, volatility, witness_count, last_observed_at)
        VALUES(v_id, p_subject, p_type, p_object,
            glicko2_neutral_mu(), glicko2_initial_rd(), glicko2_initial_volatility(),
            1, now());
        RETURN;
    END IF;
    v_new := laplace_glicko2_accumulate_games(
        v_rec.rating, v_rec.rd, v_rec.volatility,
        glicko2_neutral_mu(), glicko2_initial_rd(),
        1, p_score_fp1e9, glicko2_tau());
    UPDATE consensus
    SET rating = v_new.rating, rd = v_new.rd, volatility = v_new.volatility,
        witness_count = witness_count + 1,
        last_observed_at = now()
    WHERE id = v_id;
END;
$$;
#line 316 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_substrate/sql\\functions/inference/laplace_decay.sql.in"
CREATE OR REPLACE FUNCTION laplace_decay(
    p_subject bytea, p_type bytea, p_object bytea,
    p_decay_factor float8 DEFAULT 0.95)
RETURNS void
LANGUAGE plpgsql VOLATILE SET search_path = @extschema@, public AS $$
DECLARE
    v_id bytea := consensus_id(p_subject, p_type, p_object);
BEGIN
    UPDATE consensus
    SET rd = LEAST((rd / p_decay_factor)::bigint, glicko2_initial_rd())
    WHERE id = v_id;
END;
$$;
#line 317 "D:/Repositories/Laplace/build-win-ext/laplace_substrate/sql\\generated/install_chain.sql.in"
#line 3 "D:/Repositories/Laplace/extension/laplace_substrate/sql/laplace_substrate.sql.in"

