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
    LANGUAGE plpgsql AS $$
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
CREATE OR REPLACE PROCEDURE rebuild_content_index(p_batch int DEFAULT 20000)
    LANGUAGE plpgsql AS $$
DECLARE
    last_id bytea := '\x'::bytea;     -- keyset low-water mark
    v_max   bytea;
    v_cnt   int;
    total   bigint := 0;
BEGIN
    CALL rebuild_content_tok();

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
