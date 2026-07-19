CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace_geom;
CREATE EXTENSION IF NOT EXISTS laplace_substrate;

SET search_path TO laplace, public;

-- doc 15 Phase 3C: coverage for walk_branches' Glicko-complete scoring
-- (3Ca: refutation is visible to scoring but a net-negative candidate is
-- never walked into, even as the only option -- doc 15 I2 combined with the
-- live regression this exact scenario caught via converse.sql's "syn_bad"
-- fixture), highway-mask gating (3Cb), and astar_path_raw's opt-in geometry
-- heuristic default-safety. Fixture ids are hand-picked distinct bytes,
-- same convention as entities_exist_bitmap.sql -- not real content-addressed
-- hashes.
CREATE TEMP TABLE walk_c_fixtures AS
SELECT * FROM (VALUES
    ('subject',          decode(repeat('a1', 16), 'hex')),
    ('obj_confirmed',    decode(repeat('a2', 16), 'hex')),
    ('obj_refuted',      decode(repeat('a3', 16), 'hex')),
    ('obj_refuted_wide', decode(repeat('a4', 16), 'hex')),
    ('dummy_type',       decode(repeat('a0', 16), 'hex'))
) v(name, id);

INSERT INTO entities (id, tier, type_id, highway_mask)
SELECT id, 2::smallint,
       (SELECT id FROM walk_c_fixtures WHERE name = 'dummy_type'),
       CASE name
           WHEN 'obj_confirmed'    THEN decode(repeat('01', 32), 'hex')
           WHEN 'obj_refuted'      THEN decode(repeat('01', 32), 'hex')
           WHEN 'obj_refuted_wide' THEN decode(repeat('01', 32), 'hex')
           ELSE NULL
       END
FROM walk_c_fixtures
WHERE name <> 'dummy_type'
UNION ALL
SELECT id, 2::smallint, id, NULL
FROM walk_c_fixtures WHERE name = 'dummy_type'
ON CONFLICT (id, tier) DO NOTHING;

-- obj_confirmed: rating 1800, rd 50 -> eff_mu 1700 (well above neutral 1500).
-- obj_refuted:   rating 500,  rd 200 -> eff_mu 100 (well below neutral) --
-- signed-scores negative under 3Ca, so it is excluded at beam-placement time
-- (never a hard drop earlier in scoring, but never walked either).
INSERT INTO consensus (id, subject_id, type_id, object_id, rating, rd, volatility, witness_count, last_observed_at)
VALUES
    (decode(repeat('b1', 16), 'hex'),
     (SELECT id FROM walk_c_fixtures WHERE name = 'subject'),
     relation_type_id('COMPLETES_TO'),
     (SELECT id FROM walk_c_fixtures WHERE name = 'obj_confirmed'),
     1800000000000, 50000000000, 60000000, 5, now()),
    (decode(repeat('b2', 16), 'hex'),
     (SELECT id FROM walk_c_fixtures WHERE name = 'subject'),
     relation_type_id('COMPLETES_TO'),
     (SELECT id FROM walk_c_fixtures WHERE name = 'obj_refuted'),
     500000000000, 200000000000, 60000000, 5, now()),
    -- obj_refuted_wide: refuted (signed_mu ~ -1300) with RD so wide that
    -- exp(-kappa*rd) squashes |base| toward 0 -- the case where unconditional
    -- additive bonuses (geometry +2, partition +1) would flip a refuted edge
    -- positive and walk it (caught by the live closed-loop test: 60 refutes,
    -- signed_mu -600, edge still served). Its coords below sit EXACTLY on the
    -- subject's point (max geometry bonus) in the same hilbert partition.
    (decode(repeat('b3', 16), 'hex'),
     (SELECT id FROM walk_c_fixtures WHERE name = 'subject'),
     relation_type_id('COMPLETES_TO'),
     (SELECT id FROM walk_c_fixtures WHERE name = 'obj_refuted_wide'),
     1000000000000, 400000000000, 60000000, 5, now())
ON CONFLICT (id, type_id, subject_id) DO NOTHING;

INSERT INTO physicalities (id, entity_id, type, coord, hilbert_index, n_constituents, observed_at)
SELECT decode(repeat('c1', 16), 'hex'), id, 1,
       ST_SetSRID(ST_MakePoint(0.5, 0.5, 0.5, 0.5), 0),
       decode(repeat('00', 16), 'hex'), 0, now()
FROM walk_c_fixtures WHERE name = 'subject'
UNION ALL
SELECT decode(repeat('c2', 16), 'hex'), id, 1,
       ST_SetSRID(ST_MakePoint(0.5, 0.5, 0.5, 0.5), 0),
       decode(repeat('00', 16), 'hex'), 0, now()
FROM walk_c_fixtures WHERE name = 'obj_refuted_wide'
ON CONFLICT DO NOTHING;

-- 3Ca: with beam wide enough to hold all (beam=10, 3 real candidates), only
-- the confirmed edge is walked -- both refuted edges score non-positive and
-- are never placed. obj_refuted_wide additionally pins that geometry/partition
-- bonuses CANNOT resurrect a refuted edge: bonuses rank confirmed candidates
-- only; the consensus verdict gates placement.
SELECT count(*) = 1 AS only_confirmed_walked
FROM walk_branches(
    (SELECT id FROM walk_c_fixtures WHERE name = 'subject'),
    relation_type_id('COMPLETES_TO'), 1, 10);

SELECT entity_id = (SELECT id FROM walk_c_fixtures WHERE name = 'obj_confirmed') AS walked_node_is_confirmed
FROM walk_branches(
    (SELECT id FROM walk_c_fixtures WHERE name = 'subject'),
    relation_type_id('COMPLETES_TO'), 1, 10);

-- 3Cb: NULL intent mask -- unfiltered; confirmed edge still walked (refuted
-- stays excluded by the 3Ca score floor regardless of masking).
SELECT count(*) = 1 AS null_mask_confirmed_present
FROM walk_branches(
    (SELECT id FROM walk_c_fixtures WHERE name = 'subject'),
    relation_type_id('COMPLETES_TO'), 1, 10, NULL, NULL);

-- 3Cb: intent mask sharing a bit with the confirmed object's highway_mask --
-- confirmed edge survives the gate.
SELECT count(*) = 1 AS overlapping_mask_confirmed_present
FROM walk_branches(
    (SELECT id FROM walk_c_fixtures WHERE name = 'subject'),
    relation_type_id('COMPLETES_TO'), 1, 10,
    decode(repeat('01', 32), 'hex'), NULL);

-- 3Cb: intent mask with zero bit overlap against the confirmed object's
-- highway_mask -- gated out before scoring even runs.
SELECT count(*) = 0 AS disjoint_mask_excludes_confirmed
FROM walk_branches(
    (SELECT id FROM walk_c_fixtures WHERE name = 'subject'),
    relation_type_id('COMPLETES_TO'), 1, 10,
    decode(repeat('02', 32), 'hex'), NULL);

-- A*: default call (p_use_geometry omitted) is byte-identical to explicit
-- false -- the toggle must never change default behavior.
SELECT
    (SELECT array_agg(entity_id ORDER BY step) FROM astar_path_raw(
        (SELECT id FROM walk_c_fixtures WHERE name = 'subject'),
        ARRAY[(SELECT id FROM walk_c_fixtures WHERE name = 'obj_confirmed')],
        ARRAY[relation_type_id('COMPLETES_TO')], 4, false))
    =
    (SELECT array_agg(entity_id ORDER BY step) FROM astar_path_raw(
        (SELECT id FROM walk_c_fixtures WHERE name = 'subject'),
        ARRAY[(SELECT id FROM walk_c_fixtures WHERE name = 'obj_confirmed')],
        ARRAY[relation_type_id('COMPLETES_TO')], 4, false, false))
    AS astar_default_matches_explicit_false;

-- A*: p_use_geometry=true where the GOAL has no physicality row (only the
-- subject and refuted_wide carry coords) must degrade gracefully (heuristic
-- contributes 0.0 for coordless goals, never errors) and still find the same
-- path as the non-geometry call.
SELECT
    (SELECT array_agg(entity_id ORDER BY step) FROM astar_path_raw(
        (SELECT id FROM walk_c_fixtures WHERE name = 'subject'),
        ARRAY[(SELECT id FROM walk_c_fixtures WHERE name = 'obj_confirmed')],
        ARRAY[relation_type_id('COMPLETES_TO')], 4, false, false))
    =
    (SELECT array_agg(entity_id ORDER BY step) FROM astar_path_raw(
        (SELECT id FROM walk_c_fixtures WHERE name = 'subject'),
        ARRAY[(SELECT id FROM walk_c_fixtures WHERE name = 'obj_confirmed')],
        ARRAY[relation_type_id('COMPLETES_TO')], 4, false, true))
    AS astar_geometry_toggle_no_coords_matches_default;

DELETE FROM consensus WHERE subject_id = (SELECT id FROM walk_c_fixtures WHERE name = 'subject');
DELETE FROM physicalities WHERE entity_id IN (SELECT id FROM walk_c_fixtures);
DELETE FROM entities WHERE id IN (SELECT id FROM walk_c_fixtures);
DROP TABLE walk_c_fixtures;
