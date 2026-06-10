-- Generation prototype sandbox.
--
-- The CANONICAL generation surface lives in the extension:
--   extension/laplace_substrate/sql/26_generation.sql.in
--     rebuild_content_index / rebuild_content_index_deep  (index lifecycle)
--     generate_tokens / generation_cache_reset            (native kernel, generation_native.c)
--     generate_ngram                                      (endpoint surface)
--     pour_peer / pour_walk / pour                        (trajectory-respecting synthesis)
-- Deploy via scripts\win\build-extensions.cmd + install-extensions.cmd.
--
-- This file keeps ONLY prototypes that have not earned extension status.

SET search_path = laplace, public;
SET client_min_messages = warning;

-- ── continuation distribution (prototype dependency of the forward pass) ─────
-- Superseded for generation by the native kernel; retained solely because
-- forward_candidates below reads it.
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

-- ── multi-plane forward pass (PROTOTYPE — not yet canon) ─────────────────────
-- A generation step as a fusion over witnessed relation planes: the SEQUENTIAL
-- plane (longest-context PRECEDES) proposes what comes next; the SEMANTIC plane
-- (every non-PRECEDES knowledge edge) re-weights toward candidates related to
-- the recent context. Per-plane columns keep every choice auditable. Candidate
-- for a native kernel + extension module once the fusion law is settled.
DROP FUNCTION IF EXISTS forward_candidates(bytea[], int, int, int, numeric, numeric);
CREATE OR REPLACE FUNCTION forward_candidates(
        p_ctx       bytea[],
        p_window    int     DEFAULT 6,
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

    FOR k IN REVERSE LEAST(p_max_order, clen) .. 1 LOOP
        SELECT array_agg(c.token), array_agg(c.w) INTO ctok, cw
        FROM continuations(p_ctx[clen - k + 1 : clen], p_topk) c;
        EXIT WHEN ctok IS NOT NULL;
    END LOOP;
    IF ctok IS NULL THEN RETURN; END IF;
    SELECT sum(x) INTO tot FROM unnest(cw) x;

    RETURN QUERY
    WITH
    seqp AS (
        SELECT t.tok, (t.w::numeric / NULLIF(tot, 0)) AS seq
        FROM unnest(ctok, cw) AS t(tok, w)
    ),
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
    semp AS (
        SELECT e.tok,
               max(e.mu) / 1500.0                          AS sem,
               (array_agg(e.plane ORDER BY e.mu DESC))[1]  AS plane
        FROM sem_edges e
        WHERE e.tok IS NOT NULL
          AND e.tok <> ALL (recent)
          AND render_text(e.tok, 16) ~ '[[:alnum:]]'
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
