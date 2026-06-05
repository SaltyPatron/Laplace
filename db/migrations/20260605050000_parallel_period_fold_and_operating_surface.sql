-- 20260605050000_parallel_period_fold_and_operating_surface.sql
--
-- Two concerns, one cause (the 2026-06-05 CI autopsy: run 26974050291 — ETL
-- finished in 1h45m, then materialize_period_consensus folded 153M staged
-- relations on ONE backend for >4h14m until the silent 360-min job timeout
-- cancelled it; nothing materialized):
--
-- 1. PARALLEL PERIOD FOLD — TEMP session staging → K UNLOGGED partitions
--    routed by relation identity; one fold session per partition (disjoint
--    consensus rows, exact Σ of Σ); φ-uniformity guarded IN the merge pass
--    (the old separate check re-grouped the whole heap a second time);
--    function-scoped work_mem so the merge GROUP BY stops spilling.
--
-- 2. OPERATING SURFACE — eff_mu/eff_mu_display (THE effective-μ definition;
--    planner-inlined to the index expression — callers stop hand-writing
--    rating−2·rd), kind_id/source_id (the canonical-family rules in-DB —
--    callers stop hand-building blake3/convert_to strings and juggling hex),
--    evidence_count/consensus_count/content_count/multi_source_entity_count/
--    substrate_counts (the audit surface CI gates + scripts call), and
--    top_relations_readable (ranked-μ inference rendered by the substrate).
--
-- Schema-of-record: extension/laplace_substrate/sql/{12_inspect,13_consensus}
-- .sql.in (same commit). This migration converges EXISTING databases.

-- ── 1a. THE effective-μ definition (no SET clause: proconfig disables SQL
--        inlining; bodies are search-path-independent) ──────────────────────

CREATE OR REPLACE FUNCTION laplace.eff_mu(p_rating bigint, p_rd bigint)
    RETURNS bigint LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT p_rating - 2 * p_rd
$$;

CREATE OR REPLACE FUNCTION laplace.eff_mu_display(p_rating bigint, p_rd bigint)
    RETURNS numeric LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT round((laplace.eff_mu(p_rating, p_rd) / 1e9)::numeric, 3)
$$;

-- ── 1b. Retire the zero-arg TEMP-staging fold (signature change: the new
--        functions take a partition argument; both old forms must go or the
--        zero-arg calls keep resolving to the TEMP path) ────────────────────

DO $$
BEGIN
    IF to_regprocedure('laplace.create_period_staging()') IS NOT NULL THEN
        BEGIN
            ALTER EXTENSION laplace_substrate DROP FUNCTION laplace.create_period_staging();
        EXCEPTION WHEN OTHERS THEN NULL;   -- not a member / no extension: drop proceeds
        END;
        DROP FUNCTION laplace.create_period_staging();
    END IF;
    IF to_regprocedure('laplace.materialize_period_consensus()') IS NOT NULL THEN
        BEGIN
            ALTER EXTENSION laplace_substrate DROP FUNCTION laplace.materialize_period_consensus();
        EXCEPTION WHEN OTHERS THEN NULL;
        END;
        DROP FUNCTION laplace.materialize_period_consensus();
    END IF;
END $$;

-- ── 1c. The partitioned fold (bodies identical to 13_consensus.sql.in) ─────

CREATE OR REPLACE FUNCTION laplace.drop_period_staging()
    RETURNS void LANGUAGE plpgsql VOLATILE
    SET search_path = laplace, public AS $$
DECLARE
    stale text;
BEGIN
    FOR stale IN
        SELECT c.relname FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = current_schema() AND c.relkind = 'r'
          AND c.relname LIKE 'consensus\_period\_staging\_%'
    LOOP
        EXECUTE format('DROP TABLE %I', stale);
    END LOOP;
END;
$$;

CREATE OR REPLACE FUNCTION laplace.create_period_staging(p_partitions integer DEFAULT 1)
    RETURNS void LANGUAGE plpgsql VOLATILE
    SET search_path = laplace, public AS $$
DECLARE
    k integer;
BEGIN
    IF p_partitions < 1 OR p_partitions > 64 THEN
        RAISE EXCEPTION 'create_period_staging: % partitions out of range 1..64', p_partitions;
    END IF;
    PERFORM drop_period_staging();
    FOR k IN 0 .. p_partitions - 1 LOOP
        EXECUTE format(
            'CREATE UNLOGGED TABLE %I (
                 subject_id bytea  NOT NULL,
                 kind_id    bytea  NOT NULL,
                 object_id  bytea,
                 phi        bigint NOT NULL,
                 games      bigint NOT NULL,
                 sum_score  bigint NOT NULL,
                 last_ts    timestamptz NOT NULL
             )',
            'consensus_period_staging_' || k);
    END LOOP;
END;
$$;

CREATE OR REPLACE FUNCTION laplace.period_phi_mixed(p_subject bytea, p_kind bytea, p_object bytea)
    RETURNS bigint LANGUAGE plpgsql STABLE
    SET search_path = laplace, public AS $$
BEGIN
    RAISE EXCEPTION 'accumulation invariant violated: relation (% —[%]→ %) observed with mixed φ within one period',
        render(p_subject), render(p_kind), COALESCE(render(p_object), '∅');
END;
$$;

CREATE OR REPLACE FUNCTION laplace.materialize_period_partition(p_table text)
    RETURNS bigint LANGUAGE plpgsql VOLATILE
    SET search_path = laplace, public AS $$
DECLARE
    n_materialized bigint;
    t0 timestamptz := clock_timestamp();
BEGIN
    IF to_regclass(p_table) IS NULL THEN
        RAISE EXCEPTION 'materialize_period_partition: staging % does not exist', p_table;
    END IF;
    EXECUTE format($f$
        WITH merged AS (
            SELECT s.subject_id, s.kind_id, s.object_id,
                   min(s.phi)               AS phi_min,
                   max(s.phi)               AS phi_max,
                   sum(s.games)::bigint     AS games,
                   sum(s.sum_score)::bigint AS sum_score,
                   max(s.last_ts)           AS last_ts
            FROM %I s
            GROUP BY s.subject_id, s.kind_id, s.object_id
        )
        INSERT INTO consensus (id, subject_id, kind_id, object_id,
                               rating, rd, volatility, witness_count, last_observed_at)
        SELECT f.cid,
               f.subject_id, f.kind_id, f.object_id,
               (f.acc).rating, (f.acc).rd, (f.acc).volatility,
               f.prior_witnesses + f.games,
               f.last_ts
        FROM (
            SELECT m.subject_id, m.kind_id, m.object_id, m.games, m.last_ts,
                   m.cid, COALESCE(c.witness_count, 0) AS prior_witnesses,
                   laplace_glicko2_accumulate_games(
                       COALESCE(c.rating,     1500000000000::bigint),
                       COALESCE(c.rd,          350000000000::bigint),
                       COALESCE(c.volatility,     60000000::bigint),
                       1500000000000::bigint,
                       CASE WHEN m.phi_min <> m.phi_max
                            THEN period_phi_mixed(m.subject_id, m.kind_id, m.object_id)
                            ELSE m.phi_min END,
                       m.games,
                       m.sum_score,
                       500000000::bigint
                   ) AS acc
            FROM (SELECT mm.*, consensus_id(mm.subject_id, mm.kind_id, mm.object_id) AS cid
                  FROM merged mm) m
            LEFT JOIN consensus c ON c.id = m.cid
        ) f
        ON CONFLICT (id) DO UPDATE SET
            rating           = EXCLUDED.rating,
            rd               = EXCLUDED.rd,
            volatility       = EXCLUDED.volatility,
            witness_count    = EXCLUDED.witness_count,
            last_observed_at = EXCLUDED.last_observed_at
    $f$, p_table);
    GET DIAGNOSTICS n_materialized = ROW_COUNT;

    EXECUTE format('DROP TABLE %I', p_table);
    RAISE LOG 'period fold %: % relations materialized in %',
        p_table, n_materialized, clock_timestamp() - t0;
    RETURN n_materialized;
END;
$$;

CREATE OR REPLACE FUNCTION laplace.materialize_period_consensus(p_partition integer DEFAULT NULL)
    RETURNS bigint LANGUAGE plpgsql VOLATILE
    SET search_path = laplace, public
    SET session_replication_role = replica
    SET work_mem = '4GB' AS $$
DECLARE
    t text;
    total bigint := 0;
BEGIN
    IF p_partition IS NOT NULL THEN
        RETURN materialize_period_partition('consensus_period_staging_' || p_partition);
    END IF;
    FOR t IN
        SELECT c.relname FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = current_schema() AND c.relkind = 'r'
          AND c.relname LIKE 'consensus\_period\_staging\_%'
        ORDER BY c.relname
    LOOP
        total := total + materialize_period_partition(t);
    END LOOP;
    RETURN total;
END;
$$;

-- ── 2a. Consensus reads onto eff_mu/eff_mu_display (bodies = .sql.in) ──────

CREATE OR REPLACE FUNCTION laplace.top_relations(p_limit integer DEFAULT 50, p_kind bytea DEFAULT NULL)
    RETURNS TABLE(subject_id bytea, kind_id bytea, object_id bytea, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE SET search_path = laplace, public AS $$
    SELECT c.subject_id, c.kind_id, c.object_id,
           eff_mu_display(c.rating, c.rd), c.witness_count
    FROM consensus c
    WHERE c.object_id IS NOT NULL AND (p_kind IS NULL OR c.kind_id = p_kind)
    ORDER BY eff_mu(c.rating, c.rd) DESC
    LIMIT p_limit
$$;

CREATE OR REPLACE FUNCTION laplace.completions(p_subject bytea, p_limit integer DEFAULT 40)
    RETURNS TABLE(object_id bytea, kind_id bytea, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE SET search_path = laplace, public AS $$
    SELECT c.object_id, c.kind_id, eff_mu_display(c.rating, c.rd), c.witness_count
    FROM consensus c
    WHERE c.subject_id = p_subject AND c.object_id IS NOT NULL
    ORDER BY eff_mu(c.rating, c.rd) DESC
    LIMIT p_limit
$$;

CREATE OR REPLACE FUNCTION laplace.consensus_out(p_id bytea, p_limit integer DEFAULT 40)
    RETURNS TABLE(kind_id bytea, object_id bytea, rating bigint, rd bigint,
                  volatility bigint, witness_count bigint)
    LANGUAGE sql STABLE SET search_path = laplace, public AS $$
    SELECT c.kind_id, c.object_id, c.rating, c.rd, c.volatility, c.witness_count
    FROM consensus c
    WHERE c.subject_id = p_id
    ORDER BY eff_mu(c.rating, c.rd) DESC
    LIMIT p_limit
$$;

CREATE OR REPLACE FUNCTION laplace.consensus_in(p_id bytea, p_limit integer DEFAULT 40)
    RETURNS TABLE(subject_id bytea, kind_id bytea, rating bigint, rd bigint,
                  volatility bigint, witness_count bigint)
    LANGUAGE sql STABLE SET search_path = laplace, public AS $$
    SELECT c.subject_id, c.kind_id, c.rating, c.rd, c.volatility, c.witness_count
    FROM consensus c
    WHERE c.object_id = p_id
    ORDER BY eff_mu(c.rating, c.rd) DESC
    LIMIT p_limit
$$;

CREATE OR REPLACE FUNCTION laplace.generate_tree(
    p_prompt bytea, p_kind bytea DEFAULT NULL, p_depth int DEFAULT 4, p_beam int DEFAULT 5)
    RETURNS TABLE(depth int, path bytea[], entity_id bytea,
                  eff_mu numeric, path_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE SET search_path = laplace, public AS $$
    WITH RECURSIVE walk(depth, path, entity_id, eff_mu, path_mu, witnesses) AS (
        SELECT 0, ARRAY[p_prompt], p_prompt, NULL::numeric, 0::numeric, NULL::bigint
        UNION ALL
        SELECT w.depth + 1,
               w.path || nxt.object_id,
               nxt.object_id,
               nxt.eff_mu,
               w.path_mu + nxt.eff_mu,
               nxt.witness_count
        FROM walk w
        CROSS JOIN LATERAL (
            SELECT c.object_id,
                   eff_mu_display(c.rating, c.rd) AS eff_mu,
                   c.witness_count
            FROM consensus c
            WHERE c.subject_id = w.entity_id
              AND c.object_id IS NOT NULL
              AND (p_kind IS NULL OR c.kind_id = p_kind)
              AND NOT (c.object_id = ANY (w.path))
            ORDER BY eff_mu(c.rating, c.rd) DESC
            LIMIT p_beam
        ) nxt
        WHERE w.depth < p_depth
    )
    SELECT depth, path, entity_id, eff_mu, path_mu, witnesses
    FROM walk
    WHERE depth > 0
    ORDER BY depth, path_mu DESC
$$;

CREATE OR REPLACE FUNCTION laplace.generate_greedy(
    p_prompt bytea, p_kind bytea DEFAULT NULL, p_depth int DEFAULT 8)
    RETURNS TABLE(step int, entity_id bytea, eff_mu numeric)
    LANGUAGE plpgsql STABLE SET search_path = laplace, public AS $$
DECLARE
    cur  bytea := p_prompt;
    nxt  record;
    seen bytea[] := ARRAY[p_prompt];
BEGIN
    FOR i IN 1..p_depth LOOP
        SELECT c.object_id, eff_mu_display(c.rating, c.rd) AS mu
        INTO nxt
        FROM consensus c
        WHERE c.subject_id = cur AND c.object_id IS NOT NULL
          AND (p_kind IS NULL OR c.kind_id = p_kind)
          AND NOT (c.object_id = ANY (seen))
        ORDER BY eff_mu(c.rating, c.rd) DESC
        LIMIT 1;
        EXIT WHEN nxt IS NULL OR nxt.object_id IS NULL;
        step := i; entity_id := nxt.object_id; eff_mu := nxt.mu;
        RETURN NEXT;
        seen := seen || nxt.object_id;
        cur  := nxt.object_id;
    END LOOP;
END;
$$;

CREATE OR REPLACE FUNCTION laplace.consensus_out_readable(p_id bytea, p_limit integer DEFAULT 40)
    RETURNS TABLE(kind text, object text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE
    SET search_path = laplace, public AS $$
    SELECT render(c.kind_id), render(c.object_id),
           eff_mu_display(c.rating, c.rd), c.witness_count
    FROM consensus c
    WHERE c.subject_id = p_id
    ORDER BY eff_mu(c.rating, c.rd) DESC
    LIMIT p_limit
$$;

-- ── 2b. entity_physicalities gains hilbert_index (return-type change ⇒
--        drop + recreate; conditional so a current DB no-ops) ───────────────

DO $$
BEGIN
    IF to_regprocedure('laplace.entity_physicalities(bytea)') IS NOT NULL
       AND pg_get_function_result('laplace.entity_physicalities(bytea)'::regprocedure)
           NOT LIKE '%hilbert_index%' THEN
        BEGIN
            ALTER EXTENSION laplace_substrate DROP FUNCTION laplace.entity_physicalities(bytea);
        EXCEPTION WHEN OTHERS THEN NULL;
        END;
        DROP FUNCTION laplace.entity_physicalities(bytea);
    END IF;
END $$;

CREATE OR REPLACE FUNCTION laplace.entity_physicalities(p_id bytea)
    RETURNS TABLE(kind smallint, x double precision, y double precision,
                  z double precision, m double precision,
                  radius double precision, n_constituents integer, source_id bytea,
                  hilbert_index bytea)
    LANGUAGE sql STABLE
    SET search_path = laplace, public AS $$
    SELECT p.kind, ST_X(p.coord), ST_Y(p.coord), ST_Z(p.coord), ST_M(p.coord),
           p.radius_origin, p.n_constituents, p.source_id, p.hilbert_index
    FROM physicalities p
    WHERE p.entity_id = p_id
    ORDER BY p.kind, p.source_id
$$;

-- ── 2c. Operating surface: id-builders + accounting + readable top read ────

CREATE OR REPLACE FUNCTION laplace.kind_id(p_name text) RETURNS bytea
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $$
    SELECT laplace.canonical_id('substrate/kind/' || p_name || '/v1')
$$;

CREATE OR REPLACE FUNCTION laplace.source_id(p_name text) RETURNS bytea
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $$
    SELECT laplace.canonical_id('substrate/source/' || p_name || '/v1')
$$;

CREATE OR REPLACE FUNCTION laplace.evidence_count(
    p_kind bytea DEFAULT NULL, p_source bytea DEFAULT NULL, p_object bytea DEFAULT NULL)
    RETURNS bigint
    LANGUAGE sql STABLE
    SET search_path = laplace, public AS $$
    SELECT count(*)
    FROM attestations a
    WHERE (p_kind   IS NULL OR a.kind_id   = p_kind)
      AND (p_source IS NULL OR a.source_id = p_source)
      AND (p_object IS NULL OR a.object_id = p_object)
$$;

CREATE OR REPLACE FUNCTION laplace.consensus_count(p_kind bytea DEFAULT NULL)
    RETURNS bigint
    LANGUAGE sql STABLE
    SET search_path = laplace, public AS $$
    SELECT count(*) FROM consensus c
    WHERE (p_kind IS NULL OR c.kind_id = p_kind)
$$;

CREATE OR REPLACE FUNCTION laplace.content_count(p_source bytea DEFAULT NULL)
    RETURNS bigint
    LANGUAGE sql STABLE
    SET search_path = laplace, public AS $$
    SELECT count(*) FROM physicalities p
    WHERE p.kind = 1
      AND (p_source IS NULL OR p.source_id = p_source)
$$;

CREATE OR REPLACE FUNCTION laplace.multi_source_entity_count()
    RETURNS bigint
    LANGUAGE sql STABLE
    SET search_path = laplace, public AS $$
    SELECT count(*) FROM (
        SELECT p.entity_id FROM physicalities p
        GROUP BY p.entity_id
        HAVING count(DISTINCT p.source_id) > 1) t
$$;

CREATE OR REPLACE FUNCTION laplace.substrate_counts()
    RETURNS TABLE(metric text, value bigint)
    LANGUAGE sql STABLE
    SET search_path = laplace, public AS $$
    SELECT 'entities',                 count(*) FROM entities
    UNION ALL
    SELECT 'entities/codepoint (T0)',  count(*) FROM entities
        WHERE type_id = canonical_id('substrate/type/Codepoint/v1')
    UNION ALL
    SELECT 'physicalities',            count(*) FROM physicalities
    UNION ALL
    SELECT 'physicalities/content',    count(*) FROM physicalities WHERE kind = 1
    UNION ALL
    SELECT 'attestations (evidence)',  count(*) FROM attestations
    UNION ALL
    SELECT 'consensus (relations)',    count(*) FROM consensus
$$;

CREATE OR REPLACE FUNCTION laplace.top_relations_readable(p_limit integer DEFAULT 10, p_kind bytea DEFAULT NULL)
    RETURNS TABLE(subject text, kind text, object text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE
    SET search_path = laplace, public AS $$
    SELECT render(t.subject_id), render(t.kind_id), render(t.object_id),
           t.eff_mu, t.witnesses
    FROM top_relations(p_limit, p_kind) t
$$;
