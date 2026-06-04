-- 20260604070000_evidence_single_uniqueness.sql
--
-- Evidence-layer storage correction: uniqueness is enforced ONCE, by the
-- content-addressed PK. attestations.id IS BLAKE3(subject, kind, object,
-- source, context) — every writer derives it through
-- AttestationFactory.ComputeId — so the table-level
-- UNIQUE NULLS NOT DISTINCT (subject_id, kind_id, object_id, source_id,
-- context_id) restated the same invariant a second time at a measured
-- 144 B/row. At model-evidence scale (one row per witnessed weight cell,
-- ~params rows per model) that duplication is hundreds of GB and ~⅓ of every
-- insert's index maintenance. Identity is content; the PK is the law.
--
-- Likewise attestations_subject_btree (subject_id) and
-- attestations_subject_kind_btree (subject_id, kind_id) were strict prefixes
-- of attestations_relation_btree (subject_id, kind_id, object_id): the
-- planner serves subject-only and subject+kind scans from the composite's
-- leading columns, so both were pure duplicates.
--
-- Schema-of-record: extension/laplace_substrate/sql/04_attestations.sql.in +
-- 05_indexes.sql.in (changed in the same commit). This migration converges
-- EXISTING databases; fresh installs never create the structures.

ALTER TABLE laplace.attestations
    DROP CONSTRAINT IF EXISTS attestations_subject_id_kind_id_object_id_source_id_context_key;

DROP INDEX IF EXISTS laplace.attestations_subject_btree;
DROP INDEX IF EXISTS laplace.attestations_subject_kind_btree;
