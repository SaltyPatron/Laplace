-- scripts/verify-fk.sql
-- FK integrity checks for the substrate.
-- Run via: psql -d laplace -f scripts/verify-fk.sql
--
-- All orphan checks must be empty for referential integrity to hold.
-- These tables don't exist until Chunk 2 (Geometry serde) creates the schema;
-- this script skips gracefully in that case.

\set ON_ERROR_STOP on

DO $$
BEGIN
	IF to_regclass('laplace.entities') IS NULL
	   OR to_regclass('laplace.physicalities') IS NULL
	   OR to_regclass('laplace.attestations') IS NULL THEN
		RAISE NOTICE 'substrate tables not present; skipping FK verification';
		RETURN;
	END IF;

	IF EXISTS (
		SELECT 1
		FROM laplace.physicalities p
		LEFT JOIN laplace.entities e ON e.id = p.entity_id
		WHERE e.id IS NULL
	) THEN
		RAISE EXCEPTION 'orphan physicalities.entity_id rows found';
	END IF;

	IF EXISTS (
		SELECT 1
		FROM laplace.attestations a
		LEFT JOIN laplace.entities e ON e.id = a.subject_id
		WHERE e.id IS NULL
	) THEN
		RAISE EXCEPTION 'orphan attestations.subject_id rows found';
	END IF;

	IF EXISTS (
		SELECT 1
		FROM laplace.attestations a
		LEFT JOIN laplace.entities e ON e.id = a.kind_id
		WHERE e.id IS NULL
	) THEN
		RAISE EXCEPTION 'orphan attestations.kind_id rows found';
	END IF;

	IF EXISTS (
		SELECT 1
		FROM laplace.attestations a
		LEFT JOIN laplace.entities e ON e.id = a.source_id
		WHERE e.id IS NULL
	) THEN
		RAISE EXCEPTION 'orphan attestations.source_id rows found';
	END IF;

	IF EXISTS (
		SELECT 1
		FROM laplace.attestations a
		LEFT JOIN laplace.entities e ON e.id = a.object_id
		WHERE a.object_id IS NOT NULL AND e.id IS NULL
	) THEN
		RAISE EXCEPTION 'orphan attestations.object_id rows found';
	END IF;

	IF EXISTS (
		SELECT 1
		FROM laplace.attestations a
		LEFT JOIN laplace.entities e ON e.id = a.context_id
		WHERE a.context_id IS NOT NULL AND e.id IS NULL
	) THEN
		RAISE EXCEPTION 'orphan attestations.context_id rows found';
	END IF;

	RAISE NOTICE 'FK integrity verified';
END $$;
