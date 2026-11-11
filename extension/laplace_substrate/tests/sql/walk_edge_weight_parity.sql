-- Standing is Laplace-Native's: public.consensus float8 rating and deviation on
-- the Native scale (anchor 1500), read through public.laplace_confidence. The
-- consensus.* standing functions are thin reads of that one algebra.
BEGIN;

SELECT consensus.glicko2_neutral_mu() = 1500 AS anchor_is_native,
       consensus.glicko2_initial_rd() = 350 AS unrated_deviation_is_native,
       consensus.glicko2_initial_volatility() = 0.06 AS unrated_volatility_is_native,
       consensus.walk_edge_weight(1600000000000::bigint, 30000000000::bigint) = consensus.walk_edge_weight(1600::float8, 30::float8) AS fp1e9_is_native_read,
       to_regprocedure('consensus.effective_mu(bigint,bigint)') IS NULL AS duplicate_effective_mu_retired;

-- eff_mu is the Native conservative confidence; strength is the same reading and
-- the signed walk weight is that reading around one half.
SELECT bool_and(consensus.eff_mu(r, d) = public.laplace_confidence(r, d, 2)) AS eff_mu_is_native_confidence,
       bool_and(consensus.edge_strength(r, d) = consensus.eff_mu(r, d)) AS strength_is_confidence,
       bool_and(consensus.walk_edge_weight(r, d) = 2 * consensus.eff_mu(r, d) - 1) AS signed_weight_matches_strength,
       bool_and(consensus.refuted(r, d) = (public.laplace_confidence(r, d, -2) < 0.5)) AS refuted_is_optimistic_loss
FROM (VALUES (1600.0::float8, 30.0::float8), (1600, 350), (1400, 30), (1500, 80), (2010.5, 12)) v(r, d);

SELECT consensus.walk_edge_weight(1700, 30) > 0 AS confident_win_is_walkable,
       consensus.walk_edge_weight(1400, 30) < 0 AS refutation_is_negative,
       consensus.walk_edge_weight(1500, 0) = 0 AS anchor_is_zero,
       consensus.walk_edge_weight(1600, 30) > consensus.walk_edge_weight(1600, 350) AS deviation_attenuates,
       consensus.refuted(1400, 30) AS confident_loss_is_refuted,
       NOT consensus.refuted(1400, 350) AS uncertain_loss_is_not_refuted,
       consensus.eff_mu_display(1600, 30) = round(public.laplace_confidence(1600, 30, 2)::numeric, 3) AS display_rounds_confidence;

ROLLBACK;
