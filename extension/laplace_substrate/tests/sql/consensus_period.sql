-- consensus_period.sql — THE fold: period staging → materialize_period_consensus.
--
-- Guards the gates the law requires of the period accumulation:
--   (a) ORDER-INVARIANCE — the same partial multiset staged in any order
--       materializes to identical rating / rd / volatility (Glicko-2 within a
--       period is commutative; partials merge as Σ of Σ).
--   (b) CROSS-PERIOD — a second period re-witnessing the SAME relation
--       accumulates onto the SAME consensus row (prior = current row): one row,
--       witness_count rises, rd tightens. Never a second row, never discarded.
--   (c) φ-UNIFORMITY — mixed φ for one relation within one period FAILS LOUD
--       (raises), never averaged.
--   (d) There is NO evidence-replay fold: evidence is PROVENANCE-only and the
--       fold consumes staged testimony exclusively.
--
-- Run inside a transaction and ROLLBACK — leaves no rows. RAISE EXCEPTION on any
-- failed assertion (non-zero psql exit). bootstrap.sql runs first in the same
-- regress DB; this is independently runnable (CREATE EXTENSION IF NOT EXISTS).

CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace_geom;
CREATE EXTENSION IF NOT EXISTS laplace_substrate;

BEGIN;
SET search_path = laplace, public;

DO $$
DECLARE
    type_t  bytea := laplace_hash128_blake3('substrate/type/Type/v1');   -- bootstrapped meta-type
    src     bytea := laplace_hash128_blake3('test/period/source');
    kind    bytea := laplace_hash128_blake3('test/period/kind');
    -- independent relations A and B (different subjects → different consensus rows)
    subjA   bytea := laplace_hash128_blake3('test/period/subjectA');
    subjB   bytea := laplace_hash128_blake3('test/period/subjectB');
    objA    bytea := laplace_hash128_blake3('test/period/objectA');
    objB    bytea := laplace_hash128_blake3('test/period/objectB');
    -- cross-period relation C
    subjC   bytea := laplace_hash128_blake3('test/period/subjectC');
    objC    bytea := laplace_hash128_blake3('test/period/objectC');
    phi     bigint := 30000000000;    -- opponent φ (30 ×1e9), trusted witness
    phi2    bigint := 350000000000;   -- a different φ (crank) for the mix gate
    s_conf  bigint := 900000000;      -- score 0.90 (confirm) — staged testimony
    s_ref   bigint := 100000000;      -- score 0.10 (refute)
    fA_r bigint; fA_rd bigint; fA_v bigint;
    fB_r bigint; fB_rd bigint; fB_v bigint;
    rA_r bigint; rA_rd bigint; rA_v bigint;
    rB_r bigint; rB_rd bigint; rB_v bigint;
    c1_r bigint; c1_rd bigint; c1_wc bigint;
    c2_r bigint; c2_rd bigint; c2_wc bigint; c_rows bigint;
    mix_failed boolean := false;
BEGIN
    INSERT INTO entities (id, tier, type_id, first_observed_by) VALUES
        (src,   0, type_t, NULL),
        (kind,  0, type_t, src),
        (subjA, 0, type_t, src), (objA, 0, type_t, src),
        (subjB, 0, type_t, src), (objB, 0, type_t, src),
        (subjC, 0, type_t, src), (objC, 0, type_t, src);

    -- (a) ORDER-INVARIANCE: stage A,B then materialize; compare against B,A.
    PERFORM create_period_staging();
    INSERT INTO consensus_period_staging_0 (subject_id, kind_id, object_id, phi, games, sum_score, last_ts) VALUES
        (subjA, kind, objA, phi, 2, s_conf * 2, now()),
        (subjB, kind, objB, phi, 3, s_ref  * 3, now());
    PERFORM materialize_period_consensus();
    SELECT rating, rd, volatility INTO fA_r, fA_rd, fA_v FROM consensus WHERE subject_id = subjA;
    SELECT rating, rd, volatility INTO fB_r, fB_rd, fB_v FROM consensus WHERE subject_id = subjB;

    DELETE FROM consensus WHERE subject_id IN (subjA, subjB);

    PERFORM create_period_staging();
    INSERT INTO consensus_period_staging_0 (subject_id, kind_id, object_id, phi, games, sum_score, last_ts) VALUES
        (subjB, kind, objB, phi, 3, s_ref  * 3, now()),
        (subjA, kind, objA, phi, 2, s_conf * 2, now());
    PERFORM materialize_period_consensus();
    SELECT rating, rd, volatility INTO rA_r, rA_rd, rA_v FROM consensus WHERE subject_id = subjA;
    SELECT rating, rd, volatility INTO rB_r, rB_rd, rB_v FROM consensus WHERE subject_id = subjB;

    IF fA_r <> rA_r OR fA_rd <> rA_rd OR fA_v <> rA_v
    OR fB_r <> rB_r OR fB_rd <> rB_rd OR fB_v <> rB_v THEN
        RAISE EXCEPTION 'FAIL: staging order changed the materialized consensus';
    END IF;

    -- (b) CROSS-PERIOD: period 1 folds 2 confirm games for C; period 2 folds 3
    -- more onto the SAME row (prior = current row).
    PERFORM create_period_staging();
    INSERT INTO consensus_period_staging_0 (subject_id, kind_id, object_id, phi, games, sum_score, last_ts)
        VALUES (subjC, kind, objC, phi, 2, s_conf * 2, now());
    PERFORM materialize_period_consensus();
    SELECT rating, rd, witness_count INTO c1_r, c1_rd, c1_wc FROM consensus WHERE subject_id = subjC;

    PERFORM create_period_staging();
    INSERT INTO consensus_period_staging_0 (subject_id, kind_id, object_id, phi, games, sum_score, last_ts)
        VALUES (subjC, kind, objC, phi, 3, s_conf * 3, now());
    PERFORM materialize_period_consensus();
    SELECT count(*) INTO c_rows FROM consensus WHERE subject_id = subjC;
    SELECT rating, rd, witness_count INTO c2_r, c2_rd, c2_wc FROM consensus WHERE subject_id = subjC;

    IF c_rows <> 1 THEN RAISE EXCEPTION 'FAIL: cross-period created % rows (want 1)', c_rows; END IF;
    IF c1_wc <> 2 OR c2_wc <> 5 THEN RAISE EXCEPTION 'FAIL: witness_count did not accumulate (% then %, want 2 then 5)', c1_wc, c2_wc; END IF;
    IF c2_r <= c1_r THEN RAISE EXCEPTION 'FAIL: second confirm period did not raise μ (% <= %)', c2_r, c1_r; END IF;
    IF c2_rd >= c1_rd THEN RAISE EXCEPTION 'FAIL: second period did not tighten rd (% >= %)', c2_rd, c1_rd; END IF;

    -- (c) φ-UNIFORMITY: mixed φ for one relation within one period fails loud.
    PERFORM create_period_staging();
    INSERT INTO consensus_period_staging_0 (subject_id, kind_id, object_id, phi, games, sum_score, last_ts) VALUES
        (subjA, kind, objA, phi,  1, s_conf, now()),
        (subjA, kind, objA, phi2, 1, s_conf, now());
    BEGIN
        PERFORM materialize_period_consensus();
    EXCEPTION WHEN OTHERS THEN
        mix_failed := true;
    END;
    IF NOT mix_failed THEN
        RAISE EXCEPTION 'FAIL: mixed φ within one period did not raise';
    END IF;

    -- (e) PARTITIONED STAGING: K=2 partitions; the NULL fold covers BOTH and
    -- the stale-period sweep precedes creation (the mixed-φ staging left
    -- behind by (c) must vanish, not fold).
    PERFORM create_period_staging(2);
    DELETE FROM consensus WHERE subject_id IN (subjA, subjB);
    INSERT INTO consensus_period_staging_0 (subject_id, kind_id, object_id, phi, games, sum_score, last_ts)
        VALUES (subjA, kind, objA, phi, 2, s_conf * 2, now());
    INSERT INTO consensus_period_staging_1 (subject_id, kind_id, object_id, phi, games, sum_score, last_ts)
        VALUES (subjB, kind, objB, phi, 3, s_ref * 3, now());
    IF materialize_period_consensus() <> 2 THEN
        RAISE EXCEPTION 'FAIL: NULL fold did not cover both partitions';
    END IF;
    SELECT rating, rd, volatility INTO rA_r, rA_rd, rA_v FROM consensus WHERE subject_id = subjA;
    SELECT rating, rd, volatility INTO rB_r, rB_rd, rB_v FROM consensus WHERE subject_id = subjB;
    IF fA_r <> rA_r OR fA_rd <> rA_rd OR fA_v <> rA_v
    OR fB_r <> rB_r OR fB_rd <> rB_rd OR fB_v <> rB_v THEN
        RAISE EXCEPTION 'FAIL: partitioned fold diverged from the single-partition fold';
    END IF;

    -- (f) PER-PARTITION fold (the K parallel sessions' call shape): a
    -- partition arg folds exactly its partition; both partitions drop after.
    PERFORM create_period_staging(2);
    INSERT INTO consensus_period_staging_0 (subject_id, kind_id, object_id, phi, games, sum_score, last_ts)
        VALUES (subjC, kind, objC, phi, 1, s_conf, now());
    INSERT INTO consensus_period_staging_1 (subject_id, kind_id, object_id, phi, games, sum_score, last_ts)
        VALUES (subjA, kind, objB, phi, 1, s_conf, now());
    IF materialize_period_consensus(0) <> 1 OR materialize_period_consensus(1) <> 1 THEN
        RAISE EXCEPTION 'FAIL: per-partition fold did not materialize exactly its partition';
    END IF;
    IF to_regclass('consensus_period_staging_0') IS NOT NULL
    OR to_regclass('consensus_period_staging_1') IS NOT NULL THEN
        RAISE EXCEPTION 'FAIL: fold did not drop its staging partition';
    END IF;

    RAISE NOTICE '✓ consensus_period: order-invariant, cross-period accumulates on one row, mixed φ fails loud, partitioned fold exact';
END $$;

ROLLBACK;

-- eff_mu / eff_mu_display MUST stay planner-inlinable: LANGUAGE sql, IMMUTABLE,
-- NO proconfig — a SET clause (or volatility downgrade) disables SQL-function
-- inlining and silently de-indexes every ranked-μ read (the expression indexes
-- match only the inlined body). Catalog pin; fails the diff if either drifts.
SELECT p.proname, p.provolatile, p.proconfig IS NULL AS no_proconfig, l.lanname
FROM pg_proc p JOIN pg_language l ON l.oid = p.prolang
WHERE p.pronamespace = 'laplace'::regnamespace
  AND p.proname IN ('eff_mu', 'eff_mu_display')
ORDER BY p.proname;

-- ── ported read primitives + §10 constants: presence + value pins ──
-- The read surfaces (attestation_response / attestation_unary_response), the
-- post-ingest name registrar (register_canonicals), and the four Glicko-2
-- calibration constants are catalog-pinned: a presence list (ordered) and exact
-- value pins for each constant. Fails the diff if any drifts or vanishes.
SELECT p.proname
FROM pg_proc p
WHERE p.pronamespace = 'laplace'::regnamespace
  AND p.proname IN (
        'attestation_response', 'attestation_unary_response', 'register_canonicals',
        'glicko2_neutral_mu', 'glicko2_initial_rd', 'glicko2_initial_volatility', 'glicko2_tau')
GROUP BY p.proname
ORDER BY p.proname;

SELECT laplace.glicko2_neutral_mu()         = 1500000000000::bigint AS neutral_ok,
       laplace.glicko2_initial_rd()         =  350000000000::bigint AS initial_rd_ok,
       laplace.glicko2_initial_volatility() =      60000000::bigint AS initial_volatility_ok,
       laplace.glicko2_tau()                =     500000000::bigint AS tau_ok;

-- ── in-band trajectory render: leaves resolve from vertex flags, never the
-- id→codepoint map (2026-06-05). Pack "hi" as a flagged 2-vertex trajectory
-- whose child ids are DELIBERATELY absent from codepoint_render-joinable
-- space (random test ids) — only the in-band atoms can render it.
BEGIN;
SET search_path = laplace, public;
DO $$
DECLARE
    type_t bytea := laplace_hash128_blake3('substrate/type/Type/v1');
    src    bytea := laplace_hash128_blake3('test/inband/source');
    word   bytea := laplace_hash128_blake3('test/inband/word');
    chH    bytea := laplace_hash128_blake3('test/inband/child-h');
    chI    bytea := laplace_hash128_blake3('test/inband/child-i');
    traj   geometry;
    rendered text;
BEGIN
    INSERT INTO entities (id, tier, type_id, first_observed_by) VALUES
        (src, 0, type_t, NULL), (word, 2, type_t, src),
        (chH, 0, type_t, src), (chI, 0, type_t, src);
    -- flags: HAS_ATOM | tier 0 | atom<<31  (h=U+0068, i=U+0069)
    traj := public.ST_MakeLine(ARRAY[
        public.laplace_mantissa_pack(chH, 1, 1, (1 + (104::bigint << 31))),
        public.laplace_mantissa_pack(chI, 2, 1, (1 + (105::bigint << 31)))]);
    INSERT INTO physicalities (id, entity_id, source_id, kind, coord, hilbert_index,
                               trajectory, n_constituents, observed_at)
    VALUES (laplace_hash128_blake3('test/inband/phys'), word, src, 1,
            public.ST_SetSRID(public.ST_MakePoint(1,1,1,1), 0),
            decode('00000000000000000000000000000000','hex'), traj, 2, now());
    rendered := render_text(word);
    IF rendered IS DISTINCT FROM 'hi' THEN
        RAISE EXCEPTION 'FAIL: in-band render returned % (want hi)', COALESCE(rendered, 'NULL');
    END IF;
    -- the accessors agree with the layout
    IF vertex_atom(1 + (104::bigint << 31)) <> 104 OR vertex_tier((2::bigint << 1)) <> 2 THEN
        RAISE EXCEPTION 'FAIL: vertex flag accessors disagree with mantissa.h layout';
    END IF;
    RAISE NOTICE '✓ in-band render: leaves decoded from vertex flags, no map join';
END $$;
ROLLBACK;
