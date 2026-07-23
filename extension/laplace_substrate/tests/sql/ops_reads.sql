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

ROLLBACK;
