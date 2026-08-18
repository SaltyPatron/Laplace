-- The consolidated read surface: one installed implementation per read, shared
-- by the HTTP API and the MCP server. These pin existence, arity and row shape
-- on the scratch DB (empty consensus); the semantics are exercised by the
-- callers against seeded substrates.
-- No SET search_path — every name is purpose- or storage-qualified.
BEGIN;

SELECT count(*) = 1 AS pulse_one_row FROM ops.substrate_pulse();

SELECT count(*) = 1 AS modality_one_row FROM ops.modality_counts();

SELECT count(*) >= 0 AS roster_runs
FROM ops.source_roster(public.laplace_hash128_blake3('test/ops/source'), 3);

-- The shared relation-law bootstrap must not consume a source's bounded roster.
-- Both rows carry the same source; only the source-distinctive content row survives.
INSERT INTO laplace.attestations (
    id, subject_id, type_id, object_id, source_id, context_id, outcome,
    last_observed_at, observation_count, sum_score_fp1e9, opponent_rd_fp1e9, highway_mask)
VALUES
    (public.laplace_hash128_blake3('test/ops/roster/bootstrap'),
     laplace.relation_type_id('HAS_PART'), laplace.relation_type_id('IS_A'), laplace.relation_type_id('RELATED_TO'),
     public.laplace_hash128_blake3('test/ops/source'), NULL, 2,
     statement_timestamp(), 1, 1000000000, 350000000000, NULL),
    (public.laplace_hash128_blake3('test/ops/roster/content'),
     laplace.word_id('roster-content'), laplace.relation_type_id('HAS_NAME_ALIAS'), laplace.word_id('roster-label'),
     public.laplace_hash128_blake3('test/ops/source'), NULL, 2,
     statement_timestamp(), 1, 1000000000, 350000000000, NULL);

SELECT count(*) = 1 AS roster_excludes_bootstrap,
       bool_and(subject_id = laplace.word_id('roster-content')) AS roster_returns_content
FROM ops.source_roster(public.laplace_hash128_blake3('test/ops/source'), 3);

-- mesh_position always yields the self row, even for an unwitnessed id
SELECT count(*) >= 1 AS mesh_has_self,
       count(*) FILTER (WHERE dir = 'self') = 1 AS mesh_one_self
FROM structural.mesh_position(laplace.word_id('x'));

-- taxonomy_tree roots at the id itself when no synset exists
SELECT count(*) >= 1 AS tax_has_self,
       count(*) FILTER (WHERE dir = 'self') = 1 AS tax_one_self
FROM taxonomy.tree(laplace.word_id('x'));

SELECT count(*) >= 0 AS leaders_runs FROM ops.band_leaders(ARRAY[1,2], 2);

SELECT count(*) = 1 AS record_one_row,
       bool_and(confirmed = 0 AND contested = 0 AND refuted = 0 AND thin = 0)
         AS record_zero_on_empty
FROM ops.entity_record(laplace.word_id('x'));

-- the display-mu overload: fp1e9 -> display, one definition
SELECT consensus.eff_mu_display(1500000000000::bigint) = 1500.000 AS fp_display_scales;

-- source_bootstrap_present: true once relation-law rows exist (writer shape:
-- HAS_NAME_ALIAS with a relation-type subject), false for an unwitnessed source.
INSERT INTO laplace.attestations (
    id, subject_id, type_id, object_id, source_id, context_id, outcome,
    last_observed_at, observation_count, sum_score_fp1e9, opponent_rd_fp1e9, highway_mask)
VALUES
    (public.laplace_hash128_blake3('test/ops/bootstrap/law'),
     laplace.relation_type_id('IS_A'), laplace.relation_type_id('HAS_NAME_ALIAS'), laplace.word_id('is-a'),
     public.laplace_hash128_blake3('test/ops/source'), NULL, 2,
     statement_timestamp(), 1, 1000000000, 350000000000, NULL);
SELECT ops.source_bootstrap_present(public.laplace_hash128_blake3('test/ops/source'),
                                laplace.relation_type_id('HAS_NAME_ALIAS')) AS bootstrap_present,
       NOT ops.source_bootstrap_present(public.laplace_hash128_blake3('test/ops/source-unwitnessed'),
                                    laplace.relation_type_id('HAS_NAME_ALIAS')) AS absent_on_unwitnessed;

-- generation_probe: both lanes over the seed set, one row per (lane, seed) —
-- shape pin only; reply content needs a seeded box.
SELECT count(*) = 4 AS probe_shape
FROM generation.probe('x', ARRAY[1,2]::bigint[], 5);

-- ingest_run_close: drives a running row terminal, refuses non-running rows.
INSERT INTO laplace.ingest_run_journal (run_id, source_name, layer)
VALUES ('00000000-0000-0000-0000-000000000001', 'test/ops/run', 0);
SELECT status = 'cancelled' AS closed_cancelled, ended_at IS NOT NULL AS closed_stamped
FROM ops.ingest_run_close('00000000-0000-0000-0000-000000000001');

INSERT INTO laplace.ingest_file_journal
    (run_id, file_label, source_name, status, ended_at, records)
VALUES
    ('00000000-0000-0000-0000-000000000001', 'done.xml', 'test/ops/run', 'ok', now(), 10),
    ('00000000-0000-0000-0000-000000000001', 'composed.xml', 'test/ops/run', 'composed', NULL, 7),
    ('00000000-0000-0000-0000-000000000001', 'active.xml', 'test/ops/run', 'running', NULL, 3);
SELECT count(*) = 3 AS ingest_files_rows
FROM ops.ingest_files('00000000-0000-0000-0000-000000000001', 10);
SELECT status = 'running' AS ingest_files_active_first
FROM ops.ingest_files('00000000-0000-0000-0000-000000000001', 10)
LIMIT 1;
SELECT status = 'composed' AS ingest_files_composed_second
FROM ops.ingest_files('00000000-0000-0000-0000-000000000001', 10)
OFFSET 1 LIMIT 1;

DO $$
BEGIN
    PERFORM ops.ingest_run_close('00000000-0000-0000-0000-000000000001');
    RAISE EXCEPTION 'ingest_run_close accepted a non-running row';
EXCEPTION WHEN raise_exception THEN
    IF SQLERRM LIKE 'ingest_run_close: run%' THEN NULL;
    ELSE RAISE; END IF;
END $$;

ROLLBACK;
