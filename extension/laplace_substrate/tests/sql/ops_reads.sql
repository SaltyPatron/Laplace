-- The consolidated read surface: one installed implementation per read, shared
-- by the HTTP API and the MCP server. These pin existence, arity and row shape
-- on the scratch DB (empty consensus); the semantics are exercised by the
-- callers against seeded substrates.
BEGIN;
SET search_path = laplace, public;

SELECT count(*) = 1 AS pulse_one_row FROM substrate_pulse();

SELECT count(*) = 1 AS modality_one_row FROM modality_counts();

SELECT count(*) >= 0 AS roster_runs
FROM source_roster(laplace_hash128_blake3('test/ops/source'), 3);

-- The shared relation-law bootstrap must not consume a source's bounded roster.
-- Both rows carry the same source; only the source-distinctive content row survives.
INSERT INTO attestations (
    id, subject_id, type_id, object_id, source_id, context_id, outcome,
    last_observed_at, observation_count, sum_score_fp1e9, opponent_rd_fp1e9, highway_mask)
VALUES
    (laplace_hash128_blake3('test/ops/roster/bootstrap'),
     relation_type_id('HAS_PART'), relation_type_id('IS_A'), relation_type_id('RELATED_TO'),
     laplace_hash128_blake3('test/ops/source'), NULL, 2,
     statement_timestamp(), 1, 1000000000, 350000000000, NULL),
    (laplace_hash128_blake3('test/ops/roster/content'),
     word_id('roster-content'), relation_type_id('HAS_NAME_ALIAS'), word_id('roster-label'),
     laplace_hash128_blake3('test/ops/source'), NULL, 2,
     statement_timestamp(), 1, 1000000000, 350000000000, NULL);

SELECT count(*) = 1 AS roster_excludes_bootstrap,
       bool_and(subject_id = word_id('roster-content')) AS roster_returns_content
FROM source_roster(laplace_hash128_blake3('test/ops/source'), 3);

-- mesh_position always yields the self row, even for an unwitnessed id
SELECT count(*) >= 1 AS mesh_has_self,
       count(*) FILTER (WHERE dir = 'self') = 1 AS mesh_one_self
FROM mesh_position(word_id('x'));

-- taxonomy_tree roots at the id itself when no synset exists
SELECT count(*) >= 1 AS tax_has_self,
       count(*) FILTER (WHERE dir = 'self') = 1 AS tax_one_self
FROM taxonomy_tree(word_id('x'));

SELECT count(*) >= 0 AS leaders_runs FROM band_leaders(ARRAY[1,2], 2);

SELECT count(*) = 1 AS record_one_row,
       bool_and(confirmed = 0 AND contested = 0 AND refuted = 0 AND thin = 0)
         AS record_zero_on_empty
FROM entity_record(word_id('x'));

-- the display-mu overload: fp1e9 -> display, one definition
SELECT eff_mu_display(1500000000000::bigint) = 1500.000 AS fp_display_scales;

-- source_bootstrap_present: true once relation-law rows exist (writer shape:
-- HAS_NAME_ALIAS with a relation-type subject), false for an unwitnessed source.
INSERT INTO attestations (
    id, subject_id, type_id, object_id, source_id, context_id, outcome,
    last_observed_at, observation_count, sum_score_fp1e9, opponent_rd_fp1e9, highway_mask)
VALUES
    (laplace_hash128_blake3('test/ops/bootstrap/law'),
     relation_type_id('IS_A'), relation_type_id('HAS_NAME_ALIAS'), word_id('is-a'),
     laplace_hash128_blake3('test/ops/source'), NULL, 2,
     statement_timestamp(), 1, 1000000000, 350000000000, NULL);
SELECT source_bootstrap_present(laplace_hash128_blake3('test/ops/source')) AS bootstrap_present,
       NOT source_bootstrap_present(laplace_hash128_blake3('test/ops/source-unwitnessed')) AS absent_on_unwitnessed;

-- ingest_run_close: drives a running row terminal, refuses non-running rows.
INSERT INTO ingest_run_journal (run_id, source_name, layer)
VALUES ('00000000-0000-0000-0000-000000000001', 'test/ops/run', 0);
SELECT status = 'cancelled' AS closed_cancelled, ended_at IS NOT NULL AS closed_stamped
FROM ingest_run_close('00000000-0000-0000-0000-000000000001');
DO $$
BEGIN
    PERFORM ingest_run_close('00000000-0000-0000-0000-000000000001');
    RAISE EXCEPTION 'ingest_run_close accepted a non-running row';
EXCEPTION WHEN raise_exception THEN
    IF SQLERRM LIKE 'ingest_run_close: run%' THEN NULL;
    ELSE RAISE; END IF;
END $$;

ROLLBACK;
