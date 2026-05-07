-- 0007_entity_flags.sql — categorical bit flags on entity.
--
-- These columns are PURE POSITIONAL ENUMS — bit positions in a frozen v1.0
-- substrate ABI. They are NOT entities. They categorize an entity at a
-- coarse level (POS, semantic primitives, number, tense/aspect, case,
-- modality, structural) for fast bitmask filter queries.
--
-- Everything ELSE that has identity is content-addressed:
--   - Models are entities (referenced by entity_hash; per-model fireflies
--     get their own physicality_type partition keyed by model entity_hash).
--   - Languages are entities (referenced by entity_hash; an entity's
--     language attribution is an edge to the language entity).
--   - Sources are entities (provenance.source_hash references them).
--   - Edge types and roles are entities.
--   - Modality kinds, scripts, blocks, ages are all referenced by hash
--     when used as substrate entities (the bit-flag form here is a
--     parallel categorical view, not a substitute for the entity).
--
-- Position remains a pure function of content (super-Fibonacci for tier-0,
-- centroid-of-children for tier-1+). It NEVER mutates. These flags
-- accumulate via OR as sources attest:
--
--   UPDATE entity
--      SET prime_flags = prime_flags | $new_flags
--    WHERE entity_hash = $hash AND tier = $tier;
--
-- Predicate functions (laplace.has_prime, has_all_primes, has_structural)
-- live in laplace_pg--0.1.0.sql and are IMMUTABLE PARALLEL SAFE so they
-- participate in index pushdown.

BEGIN;

ALTER TABLE entity
    ADD COLUMN IF NOT EXISTS prime_flags      bigint   NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS structural_flags smallint NOT NULL DEFAULT 0;

-- Per-tier-partition index on prime_flags. entity is range-partitioned by
-- tier so each partition gets its own index automatically.
CREATE INDEX IF NOT EXISTS entity_by_prime_flags
    ON entity (tier, prime_flags);

COMMIT;
