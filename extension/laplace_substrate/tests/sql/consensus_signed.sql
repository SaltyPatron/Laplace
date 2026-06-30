BEGIN;
SET search_path = laplace, public;

DO $$
DECLARE
    type_t   bytea := laplace_hash128_blake3('Type');
    src      bytea := laplace_hash128_blake3('test/s10/source');
    rel_type  bytea := laplace_hash128_blake3('test/s10/reltype');
    subj     bytea := laplace_hash128_blake3('test/s10/subject');
    o_conf   bytea := laplace_hash128_blake3('test/s10/obj_confirm');
    o_ref    bytea := laplace_hash128_blake3('test/s10/obj_refute');
    o_draw   bytea := laplace_hash128_blake3('test/s10/obj_draw');
    o_trust  bytea := laplace_hash128_blake3('test/s10/obj_trusted');
    o_crank  bytea := laplace_hash128_blake3('test/s10/obj_crank');
    o_games  bytea := laplace_hash128_blake3('test/s10/obj_manygames');
    o_one    bytea := laplace_hash128_blake3('test/s10/obj_onegame');
    phi_trust bigint := 30000000000;
    phi_crank bigint := 350000000000;
    s_conf   bigint := 900000000;
    s_ref    bigint := 100000000;
    s_draw   bigint := 500000000;
    mu_conf bigint; mu_ref bigint; mu_draw bigint;
    mu_trust bigint; mu_crank bigint; mu_many bigint; mu_one bigint;
    n_prov   bigint;
    neutral bigint := 1500000000000;
BEGIN
    INSERT INTO entities (id, tier, type_id, first_observed_by) VALUES
        (src,    0, type_t, NULL),
        (rel_type,   0, type_t, src), (subj, 0, type_t, src),
        (o_conf, 0, type_t, src), (o_ref, 0, type_t, src), (o_draw, 0, type_t, src),
        (o_trust,0, type_t, src), (o_crank,0, type_t, src),
        (o_games,0, type_t, src), (o_one, 0, type_t, src);

    INSERT INTO attestations
        (id, subject_id, type_id, object_id, source_id, context_id,
         outcome, last_observed_at, observation_count)
    VALUES
        (laplace_hash128_blake3('a/confirm'), subj, rel_type, o_conf,  src, NULL, 2, now(), 1),
        (laplace_hash128_blake3('a/refute'),  subj, rel_type, o_ref,   src, NULL, 0, now(), 1),
        (laplace_hash128_blake3('a/draw'),    subj, rel_type, o_draw,  src, NULL, 1, now(), 1),
        (laplace_hash128_blake3('a/trusted'), subj, rel_type, o_trust, src, NULL, 2, now(), 1),
        (laplace_hash128_blake3('a/crank'),   subj, rel_type, o_crank, src, NULL, 2, now(), 1),
        (laplace_hash128_blake3('a/many'),    subj, rel_type, o_games, src, NULL, 2, now(), 8),
        (laplace_hash128_blake3('a/one'),     subj, rel_type, o_one,   src, NULL, 2, now(), 1);

    SELECT count(*) INTO n_prov FROM attestations WHERE type_id = rel_type;
    IF n_prov <> 7 THEN RAISE EXCEPTION 'FAIL: expected 7 provenance rows, got %', n_prov; END IF;

    PERFORM create_period_staging();
    INSERT INTO consensus_period_staging_0
        (subject_id, type_id, object_id, phi, games, sum_score, last_ts)
    VALUES
        (subj, rel_type, o_conf,  phi_trust, 1, s_conf,     now()),
        (subj, rel_type, o_ref,   phi_trust, 1, s_ref,      now()),
        (subj, rel_type, o_draw,  phi_trust, 1, s_draw,     now()),
        (subj, rel_type, o_trust, phi_trust, 1, s_conf,     now()),
        (subj, rel_type, o_crank, phi_crank, 1, s_conf,     now()),
        (subj, rel_type, o_games, phi_trust, 8, s_conf * 8, now()),
        (subj, rel_type, o_one,   phi_trust, 1, s_conf,     now());
    PERFORM materialize_period_consensus();

    SELECT rating INTO mu_conf  FROM consensus WHERE object_id = o_conf;
    SELECT rating INTO mu_ref   FROM consensus WHERE object_id = o_ref;
    SELECT rating INTO mu_draw  FROM consensus WHERE object_id = o_draw;
    SELECT rating INTO mu_trust FROM consensus WHERE object_id = o_trust;
    SELECT rating INTO mu_crank FROM consensus WHERE object_id = o_crank;
    SELECT rating INTO mu_many  FROM consensus WHERE object_id = o_games;
    SELECT rating INTO mu_one   FROM consensus WHERE object_id = o_one;

    RAISE NOTICE 'confirm μ=% refute μ=% draw μ=% (neutral=%)', mu_conf, mu_ref, mu_draw, neutral;
    RAISE NOTICE 'trusted μ=% crank μ=% | 8-games μ=% 1-game μ=%', mu_trust, mu_crank, mu_many, mu_one;

    IF mu_conf <= neutral THEN RAISE EXCEPTION 'FAIL: confirm did not raise μ above neutral (% <= %)', mu_conf, neutral; END IF;
    IF mu_ref  >= neutral THEN RAISE EXCEPTION 'FAIL: refute did not lower μ below neutral (% >= %)', mu_ref, neutral; END IF;
    IF abs(mu_draw - neutral) > 5000000000 THEN RAISE EXCEPTION 'FAIL: draw moved μ off neutral (% vs %)', mu_draw, neutral; END IF;
    IF mu_trust <= mu_crank THEN RAISE EXCEPTION 'FAIL: trusted witness did not move μ more than crank (% <= %)', mu_trust, mu_crank; END IF;
    IF mu_many  <= mu_one   THEN RAISE EXCEPTION 'FAIL: more occurrences did not move μ more (% <= %)', mu_many, mu_one; END IF;

    RAISE NOTICE '✓ consensus_signed: confirm>neutral>refute, draw≈neutral, trust→φ moves more, games accumulate, evidence=provenance';
END $$;

ROLLBACK;
