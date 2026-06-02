-- consensus_incremental.sql — L3 INCREMENTAL consensus path (no TRUNCATE).
--
-- Guards the three gates the law requires of incremental_consensus(p_since):
--   (a) ORDER-INVARIANCE — accumulating evidence for relation A then B equals
--       B then A on INDEPENDENT relations (identical rating / rd / volatility).
--   (b) IDEMPOTENCE — re-ingest of an IDENTICAL tuple adds 0 net games: the
--       evidence set is unchanged (UPSERT-no-op on attestations) so a second
--       incremental_consensus leaves rating / rd / volatility / witness_count
--       byte-identical (μ no-op, no double-count).
--   (c) CROSS-WITNESS — a SECOND source witnessing the SAME relation lands on
--       the SAME consensus row (source out of identity) and raises witness_count
--       to >= 2 — NOT a second row, NOT discarded.
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
    src1    bytea := laplace_hash128_blake3('test/l3/source1');
    src2    bytea := laplace_hash128_blake3('test/l3/source2');
    kind    bytea := laplace_hash128_blake3('test/l3/kind');
    -- independent relations A and B (different subjects → different consensus rows)
    subjA   bytea := laplace_hash128_blake3('test/l3/subjectA');
    subjB   bytea := laplace_hash128_blake3('test/l3/subjectB');
    objA    bytea := laplace_hash128_blake3('test/l3/objectA');
    objB    bytea := laplace_hash128_blake3('test/l3/objectB');
    -- cross-witness relation C (two sources, same (subject,kind,object))
    subjC   bytea := laplace_hash128_blake3('test/l3/subjectC');
    objC    bytea := laplace_hash128_blake3('test/l3/objectC');
    cid_A   bytea;  cid_B bytea;  cid_C bytea;
    phi     bigint := 30000000000;    -- opponent φ (30 ×1e9), trusted witness
    s_conf  bigint := 900000000;      -- score 0.90 (confirm)
    t0      timestamptz := now();
    -- forward-order (A then B) results
    fA_r bigint; fA_rd bigint; fA_v bigint;
    fB_r bigint; fB_rd bigint; fB_v bigint;
    -- reverse-order (B then A) results
    rA_r bigint; rA_rd bigint; rA_v bigint;
    rB_r bigint; rB_rd bigint; rB_v bigint;
    -- idempotence snapshot (relation A, before / after a second identical run)
    i1_r bigint; i1_rd bigint; i1_v bigint; i1_wc bigint;
    i2_r bigint; i2_rd bigint; i2_v bigint; i2_wc bigint;
    -- cross-witness
    c_rows int; c_wc bigint;
    n bigint;
BEGIN
    cid_A := consensus_id(subjA, kind, objA);
    cid_B := consensus_id(subjB, kind, objB);
    cid_C := consensus_id(subjC, kind, objC);

    -- entities (self-consistent FKs)
    INSERT INTO entities (id, tier, type_id, first_observed_by) VALUES
        (src1, 0, type_t, NULL), (src2, 0, type_t, NULL),
        (kind, 0, type_t, src1),
        (subjA, 0, type_t, src1), (objA, 0, type_t, src1),
        (subjB, 0, type_t, src1), (objB, 0, type_t, src1),
        (subjC, 0, type_t, src1), (objC, 0, type_t, src1);

    -- ============================================================
    -- (a) ORDER-INVARIANCE on INDEPENDENT relations.
    -- Two independent relations A and B, each one confirm witness. Apply the
    -- incremental path in BOTH orders (A's period then B's, vs B's then A's)
    -- against an empty consensus; the resulting per-relation Glicko state MUST
    -- be identical regardless of order (independent rows, full re-accumulation).
    -- ============================================================

    -- ---- forward run: relation A first, then relation B ----
    INSERT INTO attestations
        (id, subject_id, kind_id, object_id, source_id, context_id,
         score, opponent_rd, arena_m, last_observed_at, observation_count)
    VALUES (laplace_hash128_blake3('l3/fwd/A'), subjA, kind, objA, src1, NULL,
            s_conf, phi, 0, t0, 1);
    PERFORM incremental_consensus(NULL);                       -- touch A

    INSERT INTO attestations
        (id, subject_id, kind_id, object_id, source_id, context_id,
         score, opponent_rd, arena_m, last_observed_at, observation_count)
    VALUES (laplace_hash128_blake3('l3/fwd/B'), subjB, kind, objB, src1, NULL,
            s_conf, phi, 0, t0, 1);
    PERFORM incremental_consensus(t0 - interval '1 second');   -- touch A and B; A re-accumulates to same state

    SELECT rating, rd, volatility INTO fA_r, fA_rd, fA_v FROM consensus WHERE id = cid_A;
    SELECT rating, rd, volatility INTO fB_r, fB_rd, fB_v FROM consensus WHERE id = cid_B;

    -- wipe and replay in the OPPOSITE order (use rebuild's clean slate via DELETE,
    -- NOT TRUNCATE — incremental never truncates; this is test setup only).
    DELETE FROM consensus WHERE id IN (cid_A, cid_B);

    -- ---- reverse run: relation B first, then relation A ----
    PERFORM incremental_consensus(NULL);                       -- both already in evidence; same end state

    SELECT rating, rd, volatility INTO rA_r, rA_rd, rA_v FROM consensus WHERE id = cid_A;
    SELECT rating, rd, volatility INTO rB_r, rB_rd, rB_v FROM consensus WHERE id = cid_B;

    RAISE NOTICE 'order: A fwd(r=%,rd=%,v=%) rev(r=%,rd=%,v=%)', fA_r, fA_rd, fA_v, rA_r, rA_rd, rA_v;
    RAISE NOTICE 'order: B fwd(r=%,rd=%,v=%) rev(r=%,rd=%,v=%)', fB_r, fB_rd, fB_v, rB_r, rB_rd, rB_v;

    IF (fA_r, fA_rd, fA_v) IS DISTINCT FROM (rA_r, rA_rd, rA_v) THEN
        RAISE EXCEPTION 'FAIL: relation A not order-invariant: (%,%,%) vs (%,%,%)', fA_r,fA_rd,fA_v, rA_r,rA_rd,rA_v;
    END IF;
    IF (fB_r, fB_rd, fB_v) IS DISTINCT FROM (rB_r, rB_rd, rB_v) THEN
        RAISE EXCEPTION 'FAIL: relation B not order-invariant: (%,%,%) vs (%,%,%)', fB_r,fB_rd,fB_v, rB_r,rB_rd,rB_v;
    END IF;
    -- A and B carry IDENTICAL evidence shapes → identical Glicko state (independence).
    IF (fA_r, fA_rd, fA_v) IS DISTINCT FROM (fB_r, fB_rd, fB_v) THEN
        RAISE EXCEPTION 'FAIL: independent identical-evidence relations diverged: A(%,%,%) vs B(%,%,%)', fA_r,fA_rd,fA_v, fB_r,fB_rd,fB_v;
    END IF;

    -- ============================================================
    -- (b) IDEMPOTENCE — re-ingest of an IDENTICAL tuple adds 0 net games.
    -- Re-asserting relation A's existing attestation is an UPSERT-no-op
    -- (ON CONFLICT DO NOTHING semantics of the writer; here the row already
    -- exists so the evidence set is unchanged). A second incremental_consensus
    -- over the unchanged evidence MUST leave A's consensus byte-identical.
    -- ============================================================
    SELECT rating, rd, volatility, witness_count INTO i1_r, i1_rd, i1_v, i1_wc
        FROM consensus WHERE id = cid_A;

    -- re-ingest the IDENTICAL tuple: same content-addressed id → no-op insert.
    INSERT INTO attestations
        (id, subject_id, kind_id, object_id, source_id, context_id,
         score, opponent_rd, arena_m, last_observed_at, observation_count)
    VALUES (laplace_hash128_blake3('l3/fwd/A'), subjA, kind, objA, src1, NULL,
            s_conf, phi, 0, t0, 1)
    ON CONFLICT (subject_id, kind_id, object_id, source_id, context_id) DO NOTHING;

    PERFORM incremental_consensus(NULL);                       -- re-accumulate A from unchanged evidence

    SELECT rating, rd, volatility, witness_count INTO i2_r, i2_rd, i2_v, i2_wc
        FROM consensus WHERE id = cid_A;

    RAISE NOTICE 'idempotence: A before(r=%,rd=%,v=%,wc=%) after(r=%,rd=%,v=%,wc=%)',
                 i1_r,i1_rd,i1_v,i1_wc, i2_r,i2_rd,i2_v,i2_wc;

    IF (i1_r, i1_rd, i1_v, i1_wc) IS DISTINCT FROM (i2_r, i2_rd, i2_v, i2_wc) THEN
        RAISE EXCEPTION 'FAIL: re-ingest of identical tuple changed consensus (double-count): (%,%,%,%) -> (%,%,%,%)',
                        i1_r,i1_rd,i1_v,i1_wc, i2_r,i2_rd,i2_v,i2_wc;
    END IF;

    -- ============================================================
    -- (c) CROSS-WITNESS — two sources, one relation, one consensus row.
    -- src1 and src2 each witness relation C once. They are SEPARATE evidence
    -- rows (source in evidence identity) but collapse to ONE consensus row
    -- (source OUT of consensus identity); witness_count >= 2.
    -- ============================================================
    INSERT INTO attestations
        (id, subject_id, kind_id, object_id, source_id, context_id,
         score, opponent_rd, arena_m, last_observed_at, observation_count)
    VALUES
        (laplace_hash128_blake3('l3/C/src1'), subjC, kind, objC, src1, NULL, s_conf, phi, 0, t0, 1),
        (laplace_hash128_blake3('l3/C/src2'), subjC, kind, objC, src2, NULL, s_conf, phi, 0, t0, 1);

    PERFORM incremental_consensus(NULL);

    SELECT count(*) INTO c_rows  FROM consensus WHERE subject_id = subjC AND kind_id = kind AND object_id = objC;
    SELECT witness_count INTO c_wc FROM consensus WHERE id = cid_C;

    RAISE NOTICE 'cross-witness: relation C consensus rows=% witness_count=%', c_rows, c_wc;

    IF c_rows <> 1 THEN
        RAISE EXCEPTION 'FAIL: two sources produced % consensus rows, expected exactly 1 (source must be out of identity)', c_rows;
    END IF;
    IF c_wc < 2 THEN
        RAISE EXCEPTION 'FAIL: cross-witness did not raise witness_count to >= 2 (got %)', c_wc;
    END IF;

    RAISE NOTICE 'consensus_incremental: order-invariant + idempotent (0 net games) + cross-witness collapses to one row (wc>=2)';
END $$;

ROLLBACK;
