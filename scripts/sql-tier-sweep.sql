-- Tier sweep: exercise every installed SQL function at a given call-graph tier and
-- record what it costs. See docs/sql-cascade.md for the cascade this walks.
--
-- Usage:  psql -d laplace -v tier=0 -f scripts/sql-tier-sweep.sql
--
-- Two things this gets right that a naive harness does not:
--   * a scalar function is CONSUMED (cast to text, tested IS NOT NULL). SELECT
--     count(*) FROM (SELECT fn()) q does not evaluate fn() at all -- the count needs
--     no column, so the planner elides the call and every scalar leaf reports as
--     instant. That mistake reported separator_ids at 0.2ms against a real 10-26s.
--   * every call is capped with SET LOCAL statement_timeout, so one slow function
--     does not end the sweep. ops.consensus_tier_distribution takes 9.2 minutes.
--
-- Comments are stripped before the call graph is built: matching function names in
-- raw pg_get_functiondef counts prose mentions as calls and invents cycles.

\set ON_ERROR_STOP on
\if :{?tier} \else \set tier 0 \endif

CREATE TEMP TABLE fns AS
SELECT p.oid,
       n.nspname||'.'||p.proname AS fq,
       p.proargtypes,
       p.pronargs,
       p.pronargdefaults,
       p.proretset,
       regexp_replace(regexp_replace(pg_get_functiondef(p.oid), '--[^\n]*', ' ', 'g'),
                      '/\*.*?\*/', ' ', 'gs') AS def
FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname IN ('consensus','converse','generation','structural','taxonomy',
                    'lexical','realize','ops','chess')
  AND p.prokind = 'f';

CREATE TEMP TABLE edges AS
SELECT DISTINCT f.fq AS caller, g.fq AS callee
FROM fns f JOIN fns g ON g.fq <> f.fq AND position(g.fq||'(' in f.def) > 0;

-- Depth = longest path to a leaf. No cycles exist once comments are stripped, so
-- this terminates; the bound is a guard, not a semantic.
CREATE TEMP TABLE depth AS
WITH RECURSIVE walk(root, node, d) AS (
    SELECT f.fq, f.fq, 0 FROM (SELECT DISTINCT fq FROM fns) f
    UNION ALL
    SELECT w.root, e.callee, w.d + 1 FROM walk w JOIN edges e ON e.caller = w.node
    WHERE w.d < 20)
SELECT root AS fq, max(d) AS tier FROM walk GROUP BY root;

-- One fixture per argument type. Deliberately a real id from the live substrate:
-- a synthetic id exercises the miss path and measures nothing.
CREATE TEMP TABLE fixture(typ text, expr text);
INSERT INTO fixture VALUES
  ('bytea',            'laplace.word_id(''wolf'')'),
  ('text',             '''wolf'''),
  ('integer',          '8'),
  ('smallint',         '2::smallint'),
  ('bigint',           '1500000000000'),
  ('double precision', '1.0'),
  ('numeric',          '1.0'),
  ('boolean',          'false'),
  ('bytea[]',          'ARRAY[laplace.word_id(''hot''), laplace.word_id(''dog'')]'),
  ('text[]',           'ARRAY[''wolf'']'),
  ('integer[]',        'ARRAY[1]'),
  ('geometry',         '(SELECT trajectory FROM laplace.v_word_points WHERE trajectory IS NOT NULL LIMIT 1)'),
  ('timestamp with time zone', 'now()'),
  ('jsonb',            '''{}''::jsonb');

CREATE TEMP TABLE calls AS
SELECT f.fq, f.proretset,
       (SELECT string_agg(COALESCE(x.expr, '<<unfixtured:'||x.typ||'>>'), ', ' ORDER BY x.ord)
        FROM (SELECT t.ord, format_type(t.typid, NULL) AS typ,
                     (SELECT expr FROM fixture WHERE typ = format_type(t.typid, NULL)) AS expr
              FROM unnest(f.proargtypes) WITH ORDINALITY AS t(typid, ord)) x) AS arglist
FROM fns f JOIN depth d ON d.fq = f.fq
WHERE d.tier = :tier;

-- A REAL table, not TEMP: the sweep commits after every function so a second
-- session can watch progress, and so catalog locks are released between calls
-- instead of being held for the whole run (an uncommitted DO block blocks
-- ALTER EXTENSION for as long as it runs).
CREATE TABLE IF NOT EXISTS laplace.sql_tier_sweep(
    fq text, tier int, ms numeric, rows_out bigint, all_null boolean, err text,
    measured_at timestamptz DEFAULT now());
DELETE FROM laplace.sql_tier_sweep WHERE tier = :tier;

CREATE OR REPLACE PROCEDURE laplace.sql_tier_sweep_run(p_tier int, p_cap text DEFAULT '2s')
LANGUAGE plpgsql AS $proc$
DECLARE r record; t0 timestamptz; n bigint; nn bigint; call text;
BEGIN
  FOR r IN SELECT DISTINCT ON (fq) fq, proretset, arglist FROM calls ORDER BY fq LOOP
    IF r.arglist LIKE '%<<unfixtured:%' THEN
      INSERT INTO laplace.sql_tier_sweep(fq,tier,err)
        VALUES (r.fq, p_tier, 'skipped: '||r.arglist);
      COMMIT; CONTINUE;
    END IF;
    call := r.fq||'('||COALESCE(r.arglist,'')||')';
    BEGIN
      EXECUTE format('SET LOCAL statement_timeout = %L', p_cap);
      t0 := clock_timestamp();
      IF r.proretset THEN
        -- rows_out AND a null-ness probe: a function that returns rows of all
        -- NULLs is fast and answers nothing, which a timing-only sweep passes.
        EXECUTE format('SELECT count(*), count(*) FILTER (WHERE q IS NOT DISTINCT FROM NULL) FROM %s q', call)
          INTO n, nn;
      ELSE
        EXECUTE format('SELECT count(*), count(*) FILTER (WHERE q.v IS NULL) FROM (SELECT (%s)::text AS v) q', call)
          INTO n, nn;
      END IF;
      INSERT INTO laplace.sql_tier_sweep(fq,tier,ms,rows_out,all_null)
        VALUES (r.fq, p_tier, EXTRACT(epoch FROM clock_timestamp()-t0)*1000, n, (n > 0 AND nn = n));
    EXCEPTION WHEN OTHERS THEN
      INSERT INTO laplace.sql_tier_sweep(fq,tier,ms,err)
        VALUES (r.fq, p_tier, EXTRACT(epoch FROM clock_timestamp()-t0)*1000, left(SQLERRM,60));
    END;
    COMMIT;
  END LOOP;
END $proc$;

CALL laplace.sql_tier_sweep_run(:tier);

SELECT fq, round(ms,1) AS ms, rows_out,
       CASE WHEN err IS NOT NULL THEN err
            WHEN all_null THEN 'ALL NULL -- fast and answers nothing'
            WHEN ms > 200 THEN 'OVER BUDGET'
            ELSE 'ok' END AS verdict
FROM laplace.sql_tier_sweep WHERE tier = :tier
ORDER BY (err IS NOT NULL AND err NOT LIKE 'skipped:%') DESC, all_null DESC NULLS LAST, ms DESC NULLS LAST;
