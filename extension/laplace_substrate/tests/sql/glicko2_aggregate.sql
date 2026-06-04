-- Story 5.6 / #68 — laplace_glicko2_accumulate aggregate pinned through pg_regress.
--
-- Pins the SQL-side aggregate to the same Glickman 2013 §"Example calculation"
-- vector the engine ctest pins, plus four substrate-specific behavior vectors.
-- Together these prove the aggregate's wiring (memory contexts, SFUNC/FINALFUNC
-- marshalling, composite return) preserves the engine math's correctness.

-- bootstrap.sql runs first in the same regress DB and creates these;
-- IF NOT EXISTS keeps this file independently runnable.
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace_geom;
CREATE EXTENSION IF NOT EXISTS laplace_substrate;

SET search_path TO laplace, public;

-- =====================================================================
-- Vector 1 — Glickman §"Example calculation" pinned through the aggregate.
-- Prior: r=1500, RD=200, sigma=0.06.
-- Opponents: (1400, 30, win), (1550, 100, loss), (1700, 300, loss); tau=0.5.
-- Paper: r'=1464.06, RD'=151.52, sigma'=0.05999.
-- =====================================================================

WITH paper_input(prior_rating, prior_rd, prior_volatility,
                 opponent_rating, opponent_rd, score, tau) AS (
    VALUES
        (1500000000000::bigint, 200000000000::bigint, 60000000::bigint,
         1400000000000::bigint,  30000000000::bigint, 1000000000::bigint, 500000000::bigint),
        (1500000000000,         200000000000,         60000000,
         1550000000000,         100000000000,                  0,         500000000),
        (1500000000000,         200000000000,         60000000,
         1700000000000,         300000000000,                  0,         500000000)
),
result AS (
    SELECT laplace_glicko2_accumulate(
        prior_rating, prior_rd, prior_volatility,
        opponent_rating, opponent_rd, score, tau
    ) AS r FROM paper_input
)
SELECT
    abs((r).rating     - 1464060000000) <= 50000000 AS r_pinned,
    abs((r).rd         -  151520000000) <= 50000000 AS rd_pinned,
    abs((r).volatility -      59990000) <=    50000 AS sigma_pinned
FROM result;

-- =====================================================================
-- Vector 2 — Empty input returns NULL (FINALFUNC sees state=NULL because
-- SFUNC never ran). Use a 0-row source with the same column list.
-- =====================================================================

SELECT laplace_glicko2_accumulate(
    prior_rating, prior_rd, prior_volatility,
    opponent_rating, opponent_rd, score, tau
) IS NULL AS empty_input_returns_null
FROM (
    SELECT 1500000000000::bigint AS prior_rating,
            200000000000::bigint AS prior_rd,
                60000000::bigint AS prior_volatility,
           1400000000000::bigint AS opponent_rating,
             30000000000::bigint AS opponent_rd,
              1000000000::bigint AS score,
               500000000::bigint AS tau
    WHERE false
) z;

-- =====================================================================
-- Vector 3 — Determinism / commutativity. Glicko-2 within a rating period
-- is order-independent (the sums in v and Delta are commutative); reversing
-- observation order must yield byte-identical aggregate output.
-- =====================================================================

WITH ordered_input(prior_rating, prior_rd, prior_volatility,
                   opponent_rating, opponent_rd, score, tau, ord) AS (
    VALUES
        (1500000000000::bigint, 200000000000::bigint, 60000000::bigint,
         1400000000000::bigint,  30000000000::bigint, 1000000000::bigint, 500000000::bigint, 1),
        (1500000000000,         200000000000,         60000000,
         1550000000000,         100000000000,                  0,         500000000,         2),
        (1500000000000,         200000000000,         60000000,
         1700000000000,         300000000000,                  0,         500000000,         3)
),
forward AS (
    SELECT laplace_glicko2_accumulate(
        prior_rating, prior_rd, prior_volatility,
        opponent_rating, opponent_rd, score, tau
    ) AS r FROM (SELECT * FROM ordered_input ORDER BY ord ASC) f
),
reverse AS (
    SELECT laplace_glicko2_accumulate(
        prior_rating, prior_rd, prior_volatility,
        opponent_rating, opponent_rd, score, tau
    ) AS r FROM (SELECT * FROM ordered_input ORDER BY ord DESC) r
)
SELECT
    (forward.r).rating     = (reverse.r).rating     AS rating_commutes,
    (forward.r).rd         = (reverse.r).rd         AS rd_commutes,
    (forward.r).volatility = (reverse.r).volatility AS volatility_commutes
FROM forward, reverse;

-- =====================================================================
-- Vector 4 — Idempotency-with-RD-shrink. Repeating an identical draw
-- shouldn't move the rating meaningfully (score=0.5 against equal-strength
-- opponent), but each observation reduces estimator variance — RD shrinks
-- monotonically as N grows. This pins the "Glicko-2 isn't raw voting"
-- property: equal-strength draws teach the substrate "we're confident this
-- entity is at 1500", not "this entity moved".
-- =====================================================================

WITH one_draw AS (
    SELECT laplace_glicko2_accumulate(
        1500000000000, 200000000000, 60000000,
        1500000000000,  30000000000,  500000000, 500000000
    ) AS r
    FROM generate_series(1, 1)
),
five_draws AS (
    SELECT laplace_glicko2_accumulate(
        1500000000000, 200000000000, 60000000,
        1500000000000,  30000000000,  500000000, 500000000
    ) AS r
    FROM generate_series(1, 5)
)
SELECT
    (five_draws.r).rd < (one_draw.r).rd                                   AS rd_shrinks_with_repetition,
    abs((five_draws.r).rating - 1500000000000) < 5000000000                AS rating_stays_near_prior,
    abs((one_draw.r).rating   - 1500000000000) < 5000000000                AS one_draw_rating_stays_near_prior
FROM one_draw, five_draws;

-- =====================================================================
-- Vector 5 — Multi-observation consensus. Five wins against the same
-- weaker opponent drive the rating UP more AND tighten RD MORE than a
-- single win., the substrate-side arena resolver is what
-- decides whether five rows reach this aggregate (independent sources)
-- or get pre-collapsed (correlated repetition from one source); given
-- that the resolver delivers N rows, the math is sum-proportional.
-- =====================================================================

WITH one_win AS (
    SELECT laplace_glicko2_accumulate(
        1500000000000, 200000000000, 60000000,
        1400000000000,  30000000000, 1000000000, 500000000
    ) AS r
    FROM generate_series(1, 1)
),
five_wins AS (
    SELECT laplace_glicko2_accumulate(
        1500000000000, 200000000000, 60000000,
        1400000000000,  30000000000, 1000000000, 500000000
    ) AS r
    FROM generate_series(1, 5)
)
SELECT
    (five_wins.r).rating > (one_win.r).rating AS multi_obs_higher_rating,
    (five_wins.r).rd     < (one_win.r).rd     AS multi_obs_tighter_rd
FROM one_win, five_wins;

-- =====================================================================
-- Vector 6 — Batch entry ≡ per-game replay, BIT-IDENTICAL. The period
-- fold's laplace_glicko2_accumulate_games(n, Σs) builds the same
-- observation multiset the per-game replay built ((n−1) games at ⌊Σs/n⌋
-- + one remainder game, same order) inside the kernel; outputs must be
-- byte-equal, including a non-zero remainder and the n=1 degenerate.
-- =====================================================================

-- (a) n=5 identical draws (Σs divisible by n; remainder game = q).
WITH replay AS (
    SELECT laplace_glicko2_accumulate(
        1500000000000, 350000000000, 60000000,
        1500000000000,  80000000000,  500000000, 500000000
    ) AS r
    FROM generate_series(1, 5)
),
batch AS (
    SELECT laplace_glicko2_accumulate_games(
        1500000000000, 350000000000, 60000000,
        1500000000000,  80000000000, 5, 2500000000, 500000000
    ) AS r
)
SELECT
    (batch.r).rating     = (replay.r).rating     AS rating_bit_equal,
    (batch.r).rd         = (replay.r).rd         AS rd_bit_equal,
    (batch.r).volatility = (replay.r).volatility AS volatility_bit_equal
FROM replay, batch;

-- (b) n=3 with a non-zero remainder: Σs = 1_000_000_001 → q = 333333333,
--     remainder game = 333333335 (q, q, rem in that order — the replay's
--     CASE emitted exactly this multiset).
WITH replay AS (
    SELECT laplace_glicko2_accumulate(
        1480000000000, 200000000000, 60000000,
        1500000000000, 120000000000, s.score, 500000000
    ) AS r
    FROM (VALUES (1, 333333333::bigint), (2, 333333333::bigint),
                 (3, 333333335::bigint)) AS s(ord, score)
),
batch AS (
    SELECT laplace_glicko2_accumulate_games(
        1480000000000, 200000000000, 60000000,
        1500000000000, 120000000000, 3, 1000000001, 500000000
    ) AS r
)
SELECT
    (batch.r).rating     = (replay.r).rating     AS rem_rating_bit_equal,
    (batch.r).rd         = (replay.r).rd         AS rem_rd_bit_equal,
    (batch.r).volatility = (replay.r).volatility AS rem_volatility_bit_equal
FROM replay, batch;

-- (c) n=1 degenerate: one game carrying the whole Σs.
WITH replay AS (
    SELECT laplace_glicko2_accumulate(
        1520000000000, 180000000000, 60000000,
        1500000000000,  90000000000, 700000000, 500000000
    ) AS r
    FROM generate_series(1, 1)
),
batch AS (
    SELECT laplace_glicko2_accumulate_games(
        1520000000000, 180000000000, 60000000,
        1500000000000,  90000000000, 1, 700000000, 500000000
    ) AS r
)
SELECT
    (batch.r).rating     = (replay.r).rating     AS one_rating_bit_equal,
    (batch.r).rd         = (replay.r).rd         AS one_rd_bit_equal,
    (batch.r).volatility = (replay.r).volatility AS one_volatility_bit_equal
FROM replay, batch;

-- (d) games must be positive — fail loud, never a silent no-op.
SELECT laplace_glicko2_accumulate_games(
    1500000000000, 350000000000, 60000000,
    1500000000000,  80000000000, 0, 0, 500000000);
