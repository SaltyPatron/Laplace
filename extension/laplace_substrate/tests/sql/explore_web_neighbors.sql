BEGIN;

DO $$
DECLARE
    type_t        bytea := public.laplace_hash128_blake3('Type');
    src           bytea := public.laplace_hash128_blake3('test/explore/source');
    s1            bytea := public.laplace_hash128_blake3('test/explore/s1');
    s2            bytea := public.laplace_hash128_blake3('test/explore/s2');
    n1            bytea := public.laplace_hash128_blake3('test/explore/n1');
    n2            bytea := public.laplace_hash128_blake3('test/explore/n2');
    n3            bytea := public.laplace_hash128_blake3('test/explore/n3');
    n4            bytea := public.laplace_hash128_blake3('test/explore/n4');
    m1            bytea := public.laplace_hash128_blake3('test/explore/m1');
    m2            bytea := public.laplace_hash128_blake3('test/explore/m2');
    m3            bytea := public.laplace_hash128_blake3('test/explore/m3');
    m4            bytea := public.laplace_hash128_blake3('test/explore/m4');
    rel_a         bytea := laplace.relation_type_id('IS_A');
    rel_b         bytea := laplace.relation_type_id('HAS_PART');
    rel_dynamic   bytea := public.laplace_hash128_blake3('test/explore/REL_DYNAMIC');
    subjects      bytea[] := ARRAY[s1, s2];
    governed      bytea[] := ARRAY[rel_a, rel_b];
    neutral       bigint := 1500000000000;
    sharp_rd      bigint := 30000000000;
    mismatches    bigint;
    wrong_roots   bigint;
    self_rows     bigint;
    expected_type bytea;
    chosen_type   bytea;
    chosen_out    boolean;
    snapshot_a    text[];
    snapshot_b    text[];
    zero_rows     bigint;
    revisit_rows  bigint;
    branch_rows   bigint;
    wrong_branches bigint;
    multi_seed_rows bigint;
BEGIN
    INSERT INTO laplace.entities (id, tier, type_id, first_observed_by)
    VALUES (src, 0, type_t, NULL),
           (s1, 0, type_t, src), (s2, 0, type_t, src),
           (n1, 0, type_t, src), (n2, 0, type_t, src),
           (n3, 0, type_t, src), (n4, 0, type_t, src),
           (m1, 0, type_t, src), (m2, 0, type_t, src),
           (m3, 0, type_t, src), (m4, 0, type_t, src),
           (rel_dynamic, 0, type_t, src)
    ON CONFLICT DO NOTHING;

    -- s1 exercises outbound, inbound, dynamic/default-partition, and self-edge
    -- paths. s2 has an exact-rank tie on n4 (two types plus a reverse copy) so
    -- direction closes that deterministic order, plus one genuinely distinct
    -- lower-ranked neighbor so a fanout=2 multi-seed test can require two entity
    -- discoveries from s2 instead of counting three edges to n4 as three slots.
    INSERT INTO laplace.consensus
        (id, subject_id, type_id, object_id, rating, rd,
         volatility, witness_count, last_observed_at)
    VALUES
      (laplace.consensus_id(s1, rel_a, n1), s1, rel_a, n1,
       neutral + 300000000000, sharp_rd, 60000000, 5, '2026-01-01 00:00:00+00'),
      (laplace.consensus_id(n2, rel_b, s1), n2, rel_b, s1,
       neutral + 250000000000, sharp_rd, 60000000, 4, '2026-01-01 00:00:00+00'),
      (laplace.consensus_id(s1, rel_dynamic, n3), s1, rel_dynamic, n3,
       neutral + 200000000000, sharp_rd, 60000000, 3, '2026-01-01 00:00:00+00'),
      (laplace.consensus_id(s1, rel_a, s1), s1, rel_a, s1,
       neutral + 100000000000, sharp_rd, 60000000, 2, '2026-01-01 00:00:00+00'),
      (laplace.consensus_id(s2, rel_a, n4), s2, rel_a, n4,
       neutral + 150000000000, sharp_rd, 60000000, 2, '2026-01-01 00:00:00+00'),
      (laplace.consensus_id(s2, rel_b, n4), s2, rel_b, n4,
       neutral + 150000000000, sharp_rd, 60000000, 2, '2026-01-01 00:00:00+00'),
      (laplace.consensus_id(n4, rel_a, s2), n4, rel_a, s2,
       neutral + 150000000000, sharp_rd, 60000000, 2, '2026-01-01 00:00:00+00'),
      (laplace.consensus_id(s2, rel_a, m1), s2, rel_a, m1,
       neutral + 90000000000, sharp_rd, 60000000, 1, '2026-01-01 00:00:00+00'),
      (laplace.consensus_id(n1, rel_a, m1), n1, rel_a, m1,
       neutral + 140000000000, sharp_rd, 60000000, 2, '2026-01-01 00:00:00+00'),
      (laplace.consensus_id(n1, rel_b, m2), n1, rel_b, m2,
       neutral + 130000000000, sharp_rd, 60000000, 2, '2026-01-01 00:00:00+00'),
      (laplace.consensus_id(n2, rel_a, m3), n2, rel_a, m3,
       neutral + 120000000000, sharp_rd, 60000000, 2, '2026-01-01 00:00:00+00'),
      (laplace.consensus_id(n2, rel_b, m4), n2, rel_b, m4,
       neutral + 110000000000, sharp_rd, 60000000, 2, '2026-01-01 00:00:00+00');

    -- The frontier executor must be the set form of the established scalar
    -- contract, not a subtly different scan hidden behind a batch signature.
    WITH batched AS (
        SELECT b.frontier_id, b.nbr, b.type_id, b.rating, b.rd,
               b.witness_count, b.outbound
        FROM consensus.explore_web_neighbors(subjects, 32) b
    ), scalar AS (
        SELECT f.frontier_id, e.nbr, e.type_id, e.rating, e.rd,
               e.witness_count, e.outbound
        FROM unnest(subjects) AS f(frontier_id)
        CROSS JOIN LATERAL consensus.explore_web_neighbors(f.frontier_id, 32) e
    ), delta AS (
        (SELECT * FROM batched EXCEPT ALL SELECT * FROM scalar)
        UNION ALL
        (SELECT * FROM scalar EXCEPT ALL SELECT * FROM batched)
    )
    SELECT count(*) INTO mismatches FROM delta;
    IF mismatches <> 0 THEN
        RAISE EXCEPTION 'FAIL: batched/scalar parity has % mismatched rows', mismatches;
    END IF;

    -- The masked path prunes named relation partitions but still probes the
    -- default partition for dynamic relation families. It must lose neither.
    WITH full_scan AS (
        SELECT * FROM consensus.explore_web_neighbors(subjects, 32)
    ), masked_scan AS (
        SELECT * FROM consensus.explore_web_neighbors(subjects, governed, 32)
    ), delta AS (
        (SELECT * FROM full_scan EXCEPT ALL SELECT * FROM masked_scan)
        UNION ALL
        (SELECT * FROM masked_scan EXCEPT ALL SELECT * FROM full_scan)
    )
    SELECT count(*) INTO mismatches FROM delta;
    IF mismatches <> 0 THEN
        RAISE EXCEPTION 'FAIL: masked/full parity has % mismatched rows', mismatches;
    END IF;

    SELECT count(*)
      INTO self_rows
    FROM consensus.explore_web_neighbors(s1, 32) e
    WHERE e.nbr = s1 AND e.type_id = rel_a;
    IF self_rows <> 0 THEN
        RAISE EXCEPTION 'FAIL: self edge escaped crawl suppression, rows=%', self_rows;
    END IF;

    SELECT count(*) INTO wrong_roots
    FROM (
        SELECT f.frontier_id, count(e.nbr) AS admitted,
               count(DISTINCT e.nbr) AS distinct_admitted
        FROM unnest(subjects) AS f(frontier_id)
        LEFT JOIN consensus.explore_web_neighbors(subjects, 2) e
               ON e.frontier_id = f.frontier_id
        GROUP BY f.frontier_id
        HAVING count(e.nbr) > 2 OR count(e.nbr) <> count(DISTINCT e.nbr)
    ) q;
    IF wrong_roots <> 0 THEN
        RAISE EXCEPTION 'FAIL: % frontier roots exceeded or duplicated neighbor slots', wrong_roots;
    END IF;

    SELECT t.type_id INTO expected_type
    FROM unnest(ARRAY[rel_a, rel_b]) AS t(type_id)
    ORDER BY t.type_id
    LIMIT 1;
    SELECT e.type_id, e.outbound INTO chosen_type, chosen_out
    FROM consensus.explore_web_neighbors(s2, 1) e;
    IF chosen_type IS DISTINCT FROM expected_type
       OR chosen_out IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'FAIL: total tie order selected type=% outbound=%',
                        encode(chosen_type, 'hex'), chosen_out;
    END IF;

    SELECT array_agg(encode(e.frontier_id, 'hex') || ':' ||
                     encode(e.nbr, 'hex') || ':' || encode(e.type_id, 'hex') || ':' ||
                     e.rating::text || ':' || e.rd::text || ':' ||
                     e.witness_count::text || ':' || e.outbound::text
                     ORDER BY e.frontier_id, (e.rating - 2 * e.rd) DESC,
                              e.nbr, e.type_id, e.outbound DESC)
      INTO snapshot_a
    FROM consensus.explore_web_neighbors(subjects, governed, 32) e;
    SELECT array_agg(encode(e.frontier_id, 'hex') || ':' ||
                     encode(e.nbr, 'hex') || ':' || encode(e.type_id, 'hex') || ':' ||
                     e.rating::text || ':' || e.rd::text || ':' ||
                     e.witness_count::text || ':' || e.outbound::text
                     ORDER BY e.frontier_id, (e.rating - 2 * e.rd) DESC,
                              e.nbr, e.type_id, e.outbound DESC)
      INTO snapshot_b
    FROM consensus.explore_web_neighbors(subjects, governed, 32) e;
    IF snapshot_a IS DISTINCT FROM snapshot_b THEN
        RAISE EXCEPTION 'FAIL: repeated masked frontier probes diverged';
    END IF;

    SELECT count(*) INTO zero_rows
    FROM consensus.explore_web_neighbors(subjects, 0);
    IF zero_rows <> 0 THEN
        RAISE EXCEPTION 'FAIL: unmasked zero limit returned % rows', zero_rows;
    END IF;
    SELECT count(*) INTO zero_rows
    FROM consensus.explore_web_neighbors(subjects, governed, 0);
    IF zero_rows <> 0 THEN
        RAISE EXCEPTION 'FAIL: masked zero limit returned % rows', zero_rows;
    END IF;
    SELECT count(*) INTO zero_rows
    FROM consensus.explore_web_neighbors(s1, 0);
    IF zero_rows <> 0 THEN
        RAISE EXCEPTION 'FAIL: scalar zero limit returned % rows', zero_rows;
    END IF;

    -- PostgreSQL's canonical empty typed array has zero dimensions. It is the
    -- exact no-seed operand produced by ARRAY(SELECT ...) when prompt routing
    -- resolves nothing, so the native crawl must abstain with zero rows rather
    -- than reject a valid empty set. NULL and actual multidimensional arrays
    -- remain malformed operands.
    SELECT count(*) INTO zero_rows
    FROM consensus.explore_web('{}'::bytea[], 2, 8);
    IF zero_rows <> 0 THEN
        RAISE EXCEPTION 'FAIL: empty seed set returned % crawl rows', zero_rows;
    END IF;

    BEGIN
        PERFORM * FROM consensus.explore_web(NULL::bytea[], 1, 1, 8);
        RAISE EXCEPTION 'FAIL: NULL seed array was accepted';
    EXCEPTION WHEN others THEN
        IF SQLERRM NOT LIKE 'explore_web: seeds must not be NULL%' THEN
            RAISE;
        END IF;
    END;

    BEGIN
        PERFORM * FROM consensus.explore_web(ARRAY[[s1], [s2]]::bytea[], 1, 1, 8);
        RAISE EXCEPTION 'FAIL: multidimensional seed array was accepted';
    EXCEPTION WHEN others THEN
        IF SQLERRM NOT LIKE 'explore_web: seeds must be an empty or 1-D bytea array%' THEN
            RAISE;
        END IF;
    END;

    SELECT count(*) INTO revisit_rows
    FROM consensus.explore_web(s1, 3, 32, 128) w
    WHERE w.hop > 1 AND (w.source_id = s1 OR w.object_id = s1);
    IF revisit_rows <> 0 THEN
        RAISE EXCEPTION 'FAIL: multi-hop crawl emitted % back-links to its seed', revisit_rows;
    END IF;

    -- Fanout belongs to EACH frontier member. With n1 and n2 retained at hop 1,
    -- both must contribute their two children at hop 2. The old global beam
    -- returned only two of these four and made the web stop after its first star.
    SELECT count(*) INTO branch_rows
    FROM consensus.explore_web(s1, 2, 2) w
    WHERE w.hop = 2;
    IF branch_rows <> 4 THEN
        RAISE EXCEPTION 'FAIL: per-parent fanout expected 4 hop-2 discoveries, got %', branch_rows;
    END IF;

    SELECT count(*) INTO wrong_branches
    FROM (
        SELECT w.source_id, count(*) AS children
        FROM consensus.explore_web(s1, 2, 2) w
        WHERE w.hop = 2 AND w.source_id IN (n1, n2)
        GROUP BY w.source_id
        HAVING count(*) <> 2
    ) q;
    IF wrong_branches <> 0 THEN
        RAISE EXCEPTION 'FAIL: % hop-1 parents did not receive their own fanout quota', wrong_branches;
    END IF;

    -- Multiple resolved prompt constituents enter one native crawl. Each seed
    -- receives its own per-parent quota; the array form is the canonical
    -- implementation used by the scalar compatibility wrapper above it. Both
    -- roots have two distinct eligible neighbors, so this checks the quota
    -- rather than accidentally counting relation/direction duplicates as slots.
    SELECT count(*) INTO multi_seed_rows
    FROM consensus.explore_web(ARRAY[s1, s2], 1, 2, 16) w
    WHERE w.hop = 1;
    IF multi_seed_rows <> 4 THEN
        RAISE EXCEPTION 'FAIL: two-seed fanout expected 4 discoveries, got %', multi_seed_rows;
    END IF;

    RAISE NOTICE '✓ explore_web_neighbors: scalar/batch/default-partition parity, per-parent expansion, self/revisit suppression, and total tie order all hold';
END $$;

ROLLBACK;