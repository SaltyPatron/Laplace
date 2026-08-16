CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace_geom;
CREATE EXTENSION IF NOT EXISTS laplace_substrate;

-- consensus.belief_logit pinned against Glicko-2's published rating scale. External ground
-- truth, not a self-consistency check: r = 1500 + (400/ln 10)*mu is the scale that makes the
-- underlying logistic a base-10 400-point curve, so a rating exactly 400 points of belief
-- above neutral MUST be ln(10) in log-odds, and one 400 below MUST be -ln(10). If the neutral
-- constant or the scale drifts, these move. rating and rd are fp1e9.
SELECT round(consensus.belief_logit(1900000000000, 0)::numeric, 6) = round(ln(10)::numeric, 6)
         AS plus_400_is_ln10,
       round(consensus.belief_logit(1100000000000, 0)::numeric, 6) = round((-ln(10))::numeric, 6)
         AS minus_400_is_neg_ln10,
       consensus.belief_logit(consensus.glicko2_neutral_mu(), 0) = 0.0
         AS neutral_is_zero;

-- rd is subtracted at 2x and without bound, which is what makes this BELIEF and not rating:
-- 200 points of deviation costs 400 points of belief, exactly one ln(10).
SELECT round((consensus.belief_logit(1900000000000, 0)
            - consensus.belief_logit(1900000000000, 200000000000))::numeric, 6)
       = round(ln(10)::numeric, 6) AS rd_200_costs_one_ln10;

-- Monotone in rating at fixed rd, and decreasing in rd at fixed rating.
SELECT consensus.belief_logit(2000000000000, 100000000000)
     > consensus.belief_logit(1800000000000, 100000000000) AS rises_with_rating,
       consensus.belief_logit(2000000000000, 100000000000)
     > consensus.belief_logit(2000000000000, 300000000000) AS falls_with_rd;

-- Abstention: a subject that couples to nothing under the relation yields no
-- rows. A softmax cannot express this; the empty set is the point.
SELECT count(*) = 0 AS unattested_subject_returns_no_rows
FROM consensus.belief_distribution(
        '\x00000000000000000000000000000000'::bytea, 'HAS_LANGUAGE', 10);

-- INVENTION §5: belief is rating - 2*rd and "all ranked reads order by it." The logit
-- is monotone in rating but NOT in eff_mu -- g(phi) discounts where eff_mu subtracts --
-- so the two orders genuinely disagree and only a discriminating pair proves which one
-- the function uses. B carries the HIGHER share and the LOWER belief; belief wins.
--   A rating 2000 rd 100 -> eff_mu 1800, logit 2.7434, p 0.456941
--   B rating 2100 rd 200 -> eff_mu 1700, logit 2.9160, p 0.543059
BEGIN;
INSERT INTO laplace.consensus
    (id, subject_id, type_id, object_id, rating, rd, volatility, witness_count, last_observed_at)
VALUES
 ('\x11111111111111111111111111111111', '\xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
  laplace.relation_type_id('HAS_LANGUAGE'), '\x0000000000000000000000000000000a',
  2000000000000, 100000000000, 60000000, 5, now()),
 ('\x22222222222222222222222222222222', '\xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
  laplace.relation_type_id('HAS_LANGUAGE'), '\x0000000000000000000000000000000b',
  2100000000000, 200000000000, 60000000, 5, now());

SELECT encode(object_id, 'hex') AS obj, round(p::numeric, 6) AS p, eff_mu
FROM consensus.belief_distribution('\xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'::bytea, 'HAS_LANGUAGE', 10);

SELECT sum(p) = 1.0 AS mass_is_one
FROM consensus.belief_distribution('\xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'::bytea, 'HAS_LANGUAGE', 10);
ROLLBACK;
