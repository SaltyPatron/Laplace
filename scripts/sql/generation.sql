-- Trajectory-grounded sequence model: content index + autoregressive generation.
-- Permanent, memory-safe, resumable. No model — pure witnessed content trajectories.
--   psql -h localhost -U postgres -d laplace -f scripts/sql/generation.sql
--   CALL laplace.rebuild_content_index();          -- (re)materialize the position index
--   SELECT * FROM laplace.generate_ngram('the angle', 16);
--
-- Why batched: the position index is the trajectory geometry unpacked via ST_DumpPoints.
-- Doing it as one CREATE TABLE AS with a global window-sort over ~7M vertices, on top of a
-- 15 GB shared_buffers, exhausts system memory and the OS kills the postmaster. Batching by
-- sentence keyset with COMMIT per batch bounds memory to one batch's sort and is resumable.

SET search_path = laplace, public;
SET client_min_messages = warning;

-- ── content vocabulary ──────────────────────────────────────────────────────
-- Tokens that participate in PRECEDES are content words by construction
-- (BuildDistributionalAttestations filters to alphanumeric-bearing tokens).
CREATE OR REPLACE PROCEDURE rebuild_content_tok()
    LANGUAGE plpgsql
    SET search_path = laplace, public AS $$
BEGIN
    DROP TABLE IF EXISTS content_tok;
    CREATE TABLE content_tok AS
        SELECT DISTINCT subject_id AS id FROM consensus
            WHERE type_id = relation_type_id('PRECEDES')
        UNION
        SELECT object_id FROM consensus
            WHERE type_id = relation_type_id('PRECEDES') AND object_id IS NOT NULL;
    ALTER TABLE content_tok ADD PRIMARY KEY (id);
END $$;

-- ── position index: trajectory geometry unpacked to (seq_id, pos, token) ─────
-- pos is consecutive over CONTENT tokens only (whitespace/punctuation dropped),
-- so generate_ngram's k-way contiguity join matches real word adjacency.
-- Memory-safe: per-batch window-sort over one keyset page of sentences, COMMIT each.
-- NB: no SET search_path clause — a config-SET on a procedure forces atomic execution
-- and forbids COMMIT. Call with the session search_path set to laplace, public (the
-- generation.sql header and the run scripts do this).
CREATE OR REPLACE PROCEDURE rebuild_content_index(p_batch int DEFAULT 20000)
    LANGUAGE plpgsql AS $$
DECLARE
    last_id bytea := '\x'::bytea;     -- keyset low-water mark
    v_max   bytea;
    v_cnt   int;
    total   bigint := 0;
BEGIN
    -- content vocabulary (inlined; a nested CALL would force this procedure atomic and reject COMMIT)
    DROP TABLE IF EXISTS content_tok;
    CREATE TABLE content_tok AS
        SELECT DISTINCT subject_id AS id FROM consensus
            WHERE type_id = relation_type_id('PRECEDES')
        UNION
        SELECT object_id FROM consensus
            WHERE type_id = relation_type_id('PRECEDES') AND object_id IS NOT NULL;
    ALTER TABLE content_tok ADD PRIMARY KEY (id);

    DROP TABLE IF EXISTS content_index;
    CREATE TABLE content_index (seq_id bytea NOT NULL, token bytea NOT NULL, pos int NOT NULL);

    LOOP
        -- next page of sentence entities by id (keyset; one trajectory per entity)
        SELECT max(entity_id), count(*) INTO v_max, v_cnt
        FROM (
            SELECT DISTINCT p.entity_id
            FROM physicalities p
            JOIN entities e ON e.id = p.entity_id AND e.tier = 3
            WHERE p.type = 1 AND p.trajectory IS NOT NULL AND p.entity_id > last_id
            ORDER BY p.entity_id
            LIMIT p_batch
        ) z;
        EXIT WHEN v_cnt = 0;

        INSERT INTO content_index (seq_id, token, pos)
        SELECT s.entity_id, u.entity_id,
               row_number() OVER (PARTITION BY s.entity_id ORDER BY dp.path[1])::int
        FROM (
            SELECT DISTINCT ON (p.entity_id) p.entity_id, p.trajectory
            FROM physicalities p
            JOIN entities e ON e.id = p.entity_id AND e.tier = 3
            WHERE p.type = 1 AND p.trajectory IS NOT NULL
              AND p.entity_id > last_id AND p.entity_id <= v_max
        ) s,
        LATERAL ST_DumpPoints(s.trajectory) dp,
        LATERAL laplace_mantissa_unpack(dp.geom) u
        WHERE EXISTS (SELECT 1 FROM content_tok t WHERE t.id = u.entity_id);

        total := total + v_cnt;
        COMMIT;                       -- bound memory + persist progress
        last_id := v_max;
        RAISE NOTICE 'content_index: % sentences done', total;
    END LOOP;

    CREATE INDEX content_index_seq ON content_index(seq_id, pos);
    CREATE INDEX content_index_tok ON content_index(token, pos);
    ANALYZE content_index;
END $$;

-- ── continuation distribution for an exact context (full p_ctx, in order) ────
CREATE OR REPLACE FUNCTION continuations(p_ctx bytea[], p_topk int DEFAULT 20)
    RETURNS TABLE(token bytea, w bigint)
    LANGUAGE plpgsql STABLE
    SET search_path = laplace, public AS $$
DECLARE
    k int := COALESCE(array_length(p_ctx, 1), 0);
    q text;
    i int;
BEGIN
    IF k < 1 THEN RETURN; END IF;
    q := 'SELECT cn.token, count(*)::bigint FROM content_index c1';
    FOR i IN 2..k LOOP
        q := q || format(' JOIN content_index c%s ON c%s.seq_id=c1.seq_id AND c%s.pos=c1.pos+%s',
                         i, i, i, i - 1);
    END LOOP;
    q := q || format(' JOIN content_index cn ON cn.seq_id=c1.seq_id AND cn.pos=c1.pos+%s', k);
    q := q || ' WHERE c1.token=($1)[1]';
    FOR i IN 2..k LOOP
        q := q || format(' AND c%s.token=($1)[%s]', i, i);
    END LOOP;
    q := q || format(' GROUP BY cn.token ORDER BY 2 DESC LIMIT %s', p_topk);
    RETURN QUERY EXECUTE q USING p_ctx;
END $$;

-- ── autoregressive generation with longest-context back-off ─────────────────
CREATE OR REPLACE FUNCTION generate_ngram(
        p_prompt      text,
        p_steps       int    DEFAULT 24,
        p_max_order   int    DEFAULT 5,
        p_temperature float8 DEFAULT 0.7,
        p_topk        int    DEFAULT 10)
    RETURNS TABLE(step int, token text, ord_used int)
    LANGUAGE plpgsql VOLATILE
    SET search_path = laplace, public AS $$
DECLARE
    ctx bytea[]; nxt bytea; i int; k int; used int; clen int;
BEGIN
    SELECT array_agg(id ORDER BY ord) INTO ctx
    FROM prompt_state(p_prompt) WHERE id IS NOT NULL;
    IF ctx IS NULL THEN RETURN; END IF;

    FOR i IN 1..p_steps LOOP
        nxt := NULL; used := NULL;
        clen := array_length(ctx, 1);
        FOR k IN REVERSE LEAST(p_max_order, clen) .. 1 LOOP
            SELECT c.token INTO nxt
            FROM continuations(ctx[clen - k + 1 : clen], p_topk) c
            ORDER BY (-ln(random())) / power(c.w::float8, 1.0 / GREATEST(p_temperature, 1e-6)) ASC
            LIMIT 1;
            IF nxt IS NOT NULL THEN used := k; EXIT; END IF;
        END LOOP;
        EXIT WHEN nxt IS NULL;
        RETURN QUERY SELECT i, render_text(nxt, 32), used;
        ctx := ctx || nxt;
    END LOOP;
END $$;

-- ── order-1 PRECEDES walk (modality-agnostic, no index needed) ──────────────
-- Works for any modality the moment PRECEDES is witnessed (text words, code tokens, …).
-- Temperature-sampled; avoids immediately repeating the previous token.
CREATE OR REPLACE FUNCTION walk(
        p_seed text, p_steps int DEFAULT 16,
        p_temp float8 DEFAULT 0.6, p_topk int DEFAULT 6)
    RETURNS text
    LANGUAGE plpgsql VOLATILE
    SET search_path = laplace, public AS $w$
DECLARE
    cur bytea; nxt bytea; prev bytea; out text := p_seed; i int;
BEGIN
    cur := COALESCE(resolve_phrase(p_seed), word_id(p_seed));
    IF cur IS NULL THEN RETURN p_seed || ' [unknown]'; END IF;
    FOR i IN 1..p_steps LOOP
        SELECT object_id INTO nxt FROM (
            SELECT c.object_id,
                   (-ln(random())) / power(c.witness_count::float8, 1.0 / GREATEST(p_temp,1e-6)) AS key
            FROM consensus c
            WHERE c.subject_id = cur AND c.type_id = relation_type_id('PRECEDES')
              AND c.object_id IS NOT NULL AND NOT refuted(c.rating, c.rd)
              AND c.object_id <> COALESCE(prev, '\x00'::bytea)
            ORDER BY key ASC LIMIT p_topk
        ) z ORDER BY random() LIMIT 1;
        EXIT WHEN nxt IS NULL;
        out := out || ' ' || render_text(nxt, 24);
        prev := cur; cur := nxt;
    END LOOP;
    RETURN out;
END $w$;

-- ── multi-plane forward pass ────────────────────────────────────────────────
-- A generation step is not a Markov walk over PRECEDES; it is a fusion over witnessed
-- relation planes. The SEQUENTIAL plane (longest-context PRECEDES) proposes what comes next
-- syntactically; the SEMANTIC plane (every non-PRECEDES knowledge edge — IS_A, USED_FOR,
-- AT_LOCATION, X_INTENT, CAUSES, …) re-weights toward candidates that are actually related to
-- the recent context. Each plane's vote is a Glicko-2 eff_mu — witnessed, with uncertainty.
-- The per-plane columns make every choice auditable: you can see which planes elected a token.
-- return-type changed across versions; CREATE OR REPLACE cannot alter it, so drop first
DROP FUNCTION IF EXISTS forward_candidates(bytea[], int, int, int, numeric, numeric);
CREATE OR REPLACE FUNCTION forward_candidates(
        p_ctx       bytea[],
        p_window    int     DEFAULT 6,      -- recent entities used for semantic coherence
        p_max_order int     DEFAULT 5,
        p_topk      int     DEFAULT 24,
        w_seq       numeric DEFAULT 1.0,
        w_sem       numeric DEFAULT 0.7)
    RETURNS TABLE(token bytea, label text, plane text, seq numeric, sem numeric, score numeric)
    LANGUAGE plpgsql STABLE
    SET search_path = laplace, public AS $fc$
DECLARE
    clen   int := COALESCE(array_length(p_ctx, 1), 0);
    recent bytea[];
    ctok   bytea[];
    cw     bigint[];
    k      int;
    tot    numeric;
BEGIN
    IF clen = 0 THEN RETURN; END IF;
    recent := p_ctx[GREATEST(1, clen - p_window + 1) : clen];

    -- SEQUENTIAL plane with longest-context back-off
    FOR k IN REVERSE LEAST(p_max_order, clen) .. 1 LOOP
        SELECT array_agg(c.token), array_agg(c.w) INTO ctok, cw
        FROM continuations(p_ctx[clen - k + 1 : clen], p_topk) c;
        EXIT WHEN ctok IS NOT NULL;
    END LOOP;
    IF ctok IS NULL THEN RETURN; END IF;
    SELECT sum(x) INTO tot FROM unnest(cw) x;

    RETURN QUERY
    WITH
    -- SEQUENTIAL plane proposes grammatical next tokens (longest-context PRECEDES)
    seqp AS (
        SELECT t.tok, (t.w::numeric / NULLIF(tot, 0)) AS seq
        FROM unnest(ctok, cw) AS t(tok, w)
    ),
    -- every non-PRECEDES edge touching the recent context, tagged with its OWN plane
    -- (has_property, made_up_of, is_a, used_for, at_location, x_intent, …) — not flattened
    sem_edges AS (
        SELECT CASE WHEN kk.subject_id = ANY (recent) THEN kk.object_id
                    ELSE kk.subject_id END        AS tok,
               type_label(kk.type_id)             AS plane,
               eff_mu_display(kk.rating, kk.rd)   AS mu
        FROM consensus kk
        WHERE kk.type_id <> relation_type_id('PRECEDES')
          AND NOT refuted(kk.rating, kk.rd)
          AND (kk.subject_id = ANY (recent) OR kk.object_id = ANY (recent))
    ),
    -- SEMANTIC plane proposes knowledge-neighbors, keeping the strongest plane that elected each
    semp AS (
        SELECT e.tok,
               max(e.mu) / 1500.0                          AS sem,
               (array_agg(e.plane ORDER BY e.mu DESC))[1]  AS plane
        FROM sem_edges e
        WHERE e.tok IS NOT NULL
          AND e.tok <> ALL (recent)
          AND render_text(e.tok, 16) ~ '[[:alnum:]]'       -- generable word tokens only
        GROUP BY e.tok
        ORDER BY 2 DESC
        LIMIT p_topk
    ),
    allc AS (SELECT tok FROM seqp UNION SELECT tok FROM semp)
    SELECT a.tok,
           render_text(a.tok, 24),
           CASE WHEN s.tok IS NOT NULL AND m.tok IS NOT NULL THEN m.plane || '+seq'
                WHEN m.tok IS NOT NULL THEN m.plane
                ELSE 'sequence' END,
           round(COALESCE(s.seq, 0), 4),
           round(COALESCE(m.sem, 0), 4),
           round(w_seq * COALESCE(s.seq, 0) + w_sem * COALESCE(m.sem, 0), 4)
    FROM allc a
    LEFT JOIN seqp s ON s.tok = a.tok
    LEFT JOIN semp m ON m.tok = a.tok
    ORDER BY 6 DESC
    LIMIT p_topk * 2;
END $fc$;

-- Generation driven by the multi-plane forward pass; emits the plane breakdown per token.
CREATE OR REPLACE FUNCTION generate_forward(
        p_prompt      text,
        p_steps       int     DEFAULT 24,
        p_max_order   int     DEFAULT 5,
        p_temperature float8  DEFAULT 0.6,
        p_topk        int     DEFAULT 16,
        w_sem         numeric DEFAULT 0.7)
    RETURNS TABLE(step int, token text, seq numeric, sem numeric)
    LANGUAGE plpgsql VOLATILE
    SET search_path = laplace, public AS $gf$
DECLARE
    ctx bytea[]; pick record; i int;
BEGIN
    SELECT array_agg(id ORDER BY ord) INTO ctx FROM prompt_state(p_prompt) WHERE id IS NOT NULL;
    IF ctx IS NULL THEN RETURN; END IF;

    FOR i IN 1..p_steps LOOP
        SELECT fc.token, fc.label, fc.seq, fc.sem INTO pick
        FROM forward_candidates(ctx, 6, p_max_order, p_topk, 1.0, w_sem) fc
        ORDER BY (-ln(random())) / power(GREATEST(fc.score, 1e-9), 1.0 / GREATEST(p_temperature,1e-6)) ASC
        LIMIT 1;
        EXIT WHEN pick.token IS NULL;
        RETURN QUERY SELECT i, pick.label, pick.seq, pick.sem;
        ctx := ctx || pick.token;
    END LOOP;
END $gf$;

-- ── convenience: assemble a completion as one string ────────────────────────
CREATE OR REPLACE FUNCTION complete(
        p_prompt text, p_steps int DEFAULT 24, p_max_order int DEFAULT 5,
        p_temperature float8 DEFAULT 0.7)
    RETURNS text
    LANGUAGE sql VOLATILE
    SET search_path = laplace, public AS $$
    SELECT p_prompt || ' ' || string_agg(token, ' ' ORDER BY step)
    FROM generate_ngram(p_prompt, p_steps, p_max_order, p_temperature);
$$;
