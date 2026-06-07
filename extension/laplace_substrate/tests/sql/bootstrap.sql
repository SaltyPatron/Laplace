CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION laplace_geom;
CREATE EXTENSION laplace_substrate;

SET search_path TO laplace, public;

SELECT count(*) AS canonical_kind_count FROM entities
WHERE id IN (
    laplace_hash128_blake3('substrate/kind/IS_A/v1'::bytea),
    laplace_hash128_blake3('substrate/kind/HAS_PART/v1'::bytea),
    laplace_hash128_blake3('substrate/kind/CO_OCCURS_WITH/v1'::bytea),
    laplace_hash128_blake3('substrate/kind/FOLLOWS/v1'::bytea),
    laplace_hash128_blake3('substrate/kind/PRECEDES/v1'::bytea),
    laplace_hash128_blake3('substrate/kind/OCCURS_IN_CONTEXT/v1'::bytea),
    laplace_hash128_blake3('substrate/kind/HAS_LANGUAGE/v1'::bytea),
    laplace_hash128_blake3('substrate/kind/IS_TRANSLATION_OF/v1'::bytea),
    laplace_hash128_blake3('substrate/kind/DEPICTS/v1'::bytea),
    laplace_hash128_blake3('substrate/kind/CAPTIONS/v1'::bytea),
    laplace_hash128_blake3('substrate/kind/TRANSCRIBES_AS/v1'::bytea),
    laplace_hash128_blake3('substrate/kind/IS_LOSSY_ENCODING_OF/v1'::bytea),
    laplace_hash128_blake3('substrate/kind/HAS_VARIANT_OF/v1'::bytea),
    laplace_hash128_blake3('substrate/kind/IS_REPLACED_BY/v1'::bytea),
    laplace_hash128_blake3('substrate/kind/HAS_TRUST_CLASS/v1'::bytea),
    laplace_hash128_blake3('substrate/kind/IS_ALIAS_OF/v1'::bytea)
);

SELECT EXISTS(
    SELECT 1 FROM entities
    WHERE id = laplace_hash128_blake3('substrate/source/SubstrateCanonical/v1'::bytea)
) AS substrate_canonical_present;

SELECT count(*) AS tier_count FROM entities
WHERE id IN (
    laplace_hash128_blake3('substrate/kind_tier/T1_Mandate/v1'::bytea),
    laplace_hash128_blake3('substrate/kind_tier/T2_StandardsStructural/v1'::bytea),
    laplace_hash128_blake3('substrate/kind_tier/T3_Taxonomic/v1'::bytea),
    laplace_hash128_blake3('substrate/kind_tier/T4_Partitive/v1'::bytea),
    laplace_hash128_blake3('substrate/kind_tier/T5_Causal/v1'::bytea),
    laplace_hash128_blake3('substrate/kind_tier/T6_Equivalence/v1'::bytea),
    laplace_hash128_blake3('substrate/kind_tier/T7_Oppositional/v1'::bytea),
    laplace_hash128_blake3('substrate/kind_tier/T8_Associative/v1'::bytea),
    laplace_hash128_blake3('substrate/kind_tier/T9_TensorCalculation/v1'::bytea),
    laplace_hash128_blake3('substrate/kind_tier/T10_ScalarValued/v1'::bytea),
    laplace_hash128_blake3('substrate/kind_tier/T11_Probationary/v1'::bytea)
);

SELECT count(*) AS trust_class_count FROM entities
WHERE id IN (
    laplace_hash128_blake3('substrate/trust_class/SubstrateMandate/v1'::bytea),
    laplace_hash128_blake3('substrate/trust_class/StandardsDerived/v1'::bytea),
    laplace_hash128_blake3('substrate/trust_class/AcademicCurated/v1'::bytea),
    laplace_hash128_blake3('substrate/trust_class/AcademicCuratedWithUserInput/v1'::bytea),
    laplace_hash128_blake3('substrate/trust_class/StructuredCorpus/v1'::bytea),
    laplace_hash128_blake3('substrate/trust_class/UserCuratedResource/v1'::bytea),
    laplace_hash128_blake3('substrate/trust_class/AIModelProbe/v1'::bytea),
    laplace_hash128_blake3('substrate/trust_class/AppDerived/v1'::bytea),
    laplace_hash128_blake3('substrate/trust_class/UserPromptContent/v1'::bytea),
    laplace_hash128_blake3('substrate/trust_class/AdversarialUntrusted/v1'::bytea)
);

SELECT EXISTS(
    SELECT 1 FROM attestations
    WHERE subject_id = laplace_hash128_blake3('substrate/source/SubstrateCanonical/v1'::bytea)
      AND type_id    = laplace_hash128_blake3('substrate/kind/HAS_TRUST_CLASS/v1'::bytea)
      AND object_id  = laplace_hash128_blake3('substrate/trust_class/SubstrateMandate/v1'::bytea)
      AND source_id  = laplace_hash128_blake3('substrate/source/SubstrateCanonical/v1'::bytea)
) AS substrate_canonical_trust_class_set;

SELECT count(*) AS deferred_fk_count
FROM pg_constraint
WHERE conrelid = 'entities'::regclass
  AND conname IN ('entities_type_fk', 'entities_first_observed_fk');
