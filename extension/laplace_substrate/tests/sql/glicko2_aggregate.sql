CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace_geom;
CREATE EXTENSION IF NOT EXISTS laplace_substrate;

SET search_path TO laplace, public;

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

SELECT laplace_glicko2_accumulate_games(
    1500000000000, 350000000000, 60000000,
    1500000000000,  80000000000, 0, 0, 500000000);

SELECT laplace.laplace_score(0.0, 1.0)   = 500000000 AS zero_is_midpoint,
       laplace.laplace_score(15.0, 1.0)  = 968750000 AS fifteen_pinned,
       laplace.laplace_score(100.0, 1.0) = 995049505 AS hundred_pinned,
       laplace.laplace_score(15.0, 1.0) <> laplace.laplace_score(100.0, 1.0) AS tail_distinguished;

SELECT laplace.laplace_score(laplace.laplace_score_inverse(968750000, 1.0), 1.0) = 968750000 AS rescore_identity_15,
       laplace.laplace_score(laplace.laplace_score_inverse(995049505, 1.0), 1.0) = 995049505 AS rescore_identity_100;
