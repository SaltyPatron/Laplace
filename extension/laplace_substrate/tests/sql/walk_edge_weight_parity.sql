-- The Glicko-complete edge weight has ONE implementation (glicko2.c,
-- laplace_walk_edge_weight), reached from SQL as walk_edge_weight() and from
-- native code by generate_walk.c's beam scorer and astar_path.c's edge_cost.
--
-- It did not always. Until 2026-07-27 consensus_adjacency.sql.in re-derived the
-- same algebra as a hand-typed SQL expression, so the weight the ENGINE walks
-- and the weight the FOUNDRY exports -- the same claim about the same edge --
-- came from two separately-maintained bodies. The witness half-max (4.0) was
-- written into both. Nothing tested that they agreed: the only cross-language
-- parity assertion in the suite pinned eff_mu = effective_mu, which is
-- rating - 2*rd on both sides and could not drift.
--
-- This file pins the formula that CAN drift, and the constants it is built
-- from. If a future edit changes the C body, the SQL constants, or the
-- half-max, this fails instead of silently desynchronising inference from
-- export.
BEGIN;

-- 1. The constants the formula is built from agree across the language
-- boundary. glicko2.h: LAPLACE_GLICKO2_NEUTRAL_MU_FP 1500000000000,
-- LAPLACE_GLICKO2_FP_SCALE_D 1e9, LAPLACE_WITNESS_SAT_HALFMAX 4.0.
SELECT consensus.glicko2_neutral_mu() = 1500000000000        AS neutral_matches_engine,
       consensus.foundry_witness_sat(4) = 0.5                AS halfmax_is_4,
       consensus.foundry_witness_sat(0) = 0.0                AS zero_witness_zero_weight,
       consensus.foundry_rd_kappa() = 1.0                    AS kappa_is_1;

-- 2. The C weight equals the algebra it replaces, across the interesting
-- regions: a confident win, a wide-RD win (must stay POSITIVE and walkable --
-- the eff_mu-signed version scored 99.04% of won claims negative), a genuine
-- refutation (must stay NEGATIVE), neutral, and the witness-saturation curve.
SELECT bool_and(
         consensus.walk_edge_weight(r, d, w, 1.0)
         = (r - 1500000000000)::float8 / 1e9
           * exp(-1.0 * d::float8 / 1e9)
           * (w::float8 / (w::float8 + 4.0))
       ) AS c_weight_matches_reference_algebra
FROM (VALUES
    (1600000000000::bigint,  30000000000::bigint,  50::bigint),  -- confident win
    (1600000000000::bigint, 350000000000::bigint,   1::bigint),  -- wide-RD win
    (1400000000000::bigint,  30000000000::bigint,  50::bigint),  -- refuted
    (1500000000000::bigint,  80000000000::bigint,  10::bigint),  -- neutral
    (2010500000000::bigint,  12000000000::bigint,   4::bigint),  -- half-max witness
    (1500000000001::bigint,           0::bigint,   1::bigint),   -- minimal win, no rd
    (1499999999999::bigint,           0::bigint, 999::bigint)    -- minimal loss
) v(r, d, w);

-- 3. The sign law (glicko2.c): sign comes from the RATING against neutral,
-- never from eff_mu. A wide-RD win is LOW but positive; only a real refutation
-- is negative. This is what keeps the walk from an accidental floor at
-- eff_mu >= neutral.
SELECT consensus.walk_edge_weight(1600000000000, 350000000000, 1) > 0   AS wide_rd_win_stays_walkable,
       consensus.walk_edge_weight(1400000000000,  30000000000, 50) < 0  AS refutation_is_negative,
       consensus.walk_edge_weight(1500000000000,  80000000000, 10) = 0  AS neutral_is_zero,
       consensus.walk_edge_weight(1600000000000,  30000000000, 50)
         > consensus.walk_edge_weight(1600000000000, 350000000000, 50)  AS rd_discounts,
       consensus.walk_edge_weight(1600000000000,  30000000000, 50)
         > consensus.walk_edge_weight(1600000000000,  30000000000, 1)   AS witnesses_saturate;

-- 4. The 3-arg form defaults kappa to foundry_rd_kappa(), the same tunable the
-- native walkers fetch via spi_fetch_rd_kappa(). One tunable, not two.
SELECT consensus.walk_edge_weight(1600000000000, 30000000000, 50)
       = consensus.walk_edge_weight(1600000000000, 30000000000, 50, consensus.foundry_rd_kappa())
       AS default_kappa_is_the_sql_tunable;

-- 5. walk_edge_weight is NOT eff_mu. eff_mu (rating - 2*rd) stays the
-- conservative ranking key and the indexed expression; this is the weight.
-- Pinned so a future "simplification" cannot quietly collapse them.
SELECT consensus.walk_edge_weight(1600000000000, 350000000000, 1)::numeric
       <> consensus.eff_mu(1600000000000, 350000000000)::numeric AS weight_is_not_eff_mu,
       consensus.eff_mu(1600000000000, 350000000000) = 900000000000 AS eff_mu_unchanged;

ROLLBACK;
