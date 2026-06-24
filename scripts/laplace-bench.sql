-- laplace-bench.sql — reproducible "transformer-operation" benchmark for the SQL substrate.
--
--   Usage:   psql -h localhost -U postgres -d laplace -f scripts/laplace-bench.sql
--   (run twice — first run warms the buffer cache; the second is the measured one)
--
-- Each row reports a transformer-class operation, its in-DB wall-clock latency, and
-- the result size.  Timing is measured with statement_timestamp()->clock_timestamp()
-- (statement_timestamp is fixed at the start of each statement, clock_timestamp is live),
-- so the number is the query's own server-side latency, independent of psql/round-trip.
--
-- statement_timeout caps any pathological query (e.g. salient_facts under scaffolding
-- domination) at 30s instead of hanging the whole run; a capped test shows ms = NULL.

\set ON_ERROR_STOP off
\pset footer off
\timing off
SET statement_timeout = '30s';

DROP TABLE IF EXISTS _bench;
CREATE TEMP TABLE _bench (
    seq    serial,
    test   text,
    op     text,        -- the transformer-equivalent operation class
    ms     numeric,
    rows   bigint
);

-- One macro: run <body>, record (test, op, latency_ms, rowcount). On timeout/error,
-- the INSERT is skipped (ON_ERROR_STOP off) and the test simply won't appear.
-- Each test is an independent statement so its statement_timestamp() is its own start.

INSERT INTO _bench(test,op,ms,rows)
SELECT 'recall: "what is a dog"', 'grounded NL recall',
       round(1000*extract(epoch from clock_timestamp()-statement_timestamp())::numeric,1), count(*)
FROM laplace.recall('what is a dog');

INSERT INTO _bench(test,op,ms,rows)
SELECT 'recall: "what does gravity mean"', 'grounded NL recall',
       round(1000*extract(epoch from clock_timestamp()-statement_timestamp())::numeric,1), count(*)
FROM laplace.recall('what does gravity mean');

INSERT INTO _bench(test,op,ms,rows)
SELECT 'recall: "synonyms for happy"', 'NL router → lexical',
       round(1000*extract(epoch from clock_timestamp()-statement_timestamp())::numeric,1), count(*)
FROM laplace.recall('synonyms for happy');

INSERT INTO _bench(test,op,ms,rows)
SELECT 'define(dog)', 'lexical definition',
       round(1000*extract(epoch from clock_timestamp()-statement_timestamp())::numeric,1), count(*)
FROM laplace.define(laplace.word_id('dog'));

INSERT INTO _bench(test,op,ms,rows)
SELECT 'synonyms(king)', 'synonym head (cross-lingual)',
       round(1000*extract(epoch from clock_timestamp()-statement_timestamp())::numeric,1), count(*)
FROM laplace.synonyms(laplace.word_id('king'));

INSERT INTO _bench(test,op,ms,rows)
SELECT 'translate_to(water) [all langs]', 'cross-lingual mapping',
       round(1000*extract(epoch from clock_timestamp()-statement_timestamp())::numeric,1), count(*)
FROM laplace.translate_to(laplace.word_id('water'));

INSERT INTO _bench(test,op,ms,rows)
SELECT 'isa_path(dog → animal)', 'exact multi-hop reasoning',
       round(1000*extract(epoch from clock_timestamp()-statement_timestamp())::numeric,1), count(*)
FROM laplace.isa_path(laplace.word_id('dog'), laplace.word_id('animal'), 10);

INSERT INTO _bench(test,op,ms,rows)
SELECT 'relate_path(dog, cat)', 'relational reasoning',
       round(1000*extract(epoch from clock_timestamp()-statement_timestamp())::numeric,1), count(*)
FROM laplace.relate_path(laplace.word_id('dog'), laplace.word_id('cat'), 7);

INSERT INTO _bench(test,op,ms,rows)
SELECT 'attention(king, 12)', 'attention over neighborhood',
       round(1000*extract(epoch from clock_timestamp()-statement_timestamp())::numeric,1), count(*)
FROM laplace.attention(laplace.word_id('king'), 12);

INSERT INTO _bench(test,op,ms,rows)
SELECT 'walk_branches(dog, d4×b5)', 'depth×breadth fan-out',
       round(1000*extract(epoch from clock_timestamp()-statement_timestamp())::numeric,1), count(*)
FROM laplace.walk_branches(laplace.word_id('dog'), NULL, 4, 5);

-- KNOWN-DEGRADED (kept, commented, so the harness documents them honestly):
--   hypernyms(dog,8)  recursive climb — slow at scale (~0.7s single, ~0.4 calls/s sustained)
--   salient_facts(gravity) — times out under scaffolding domination (catalog §0)
-- INSERT INTO _bench(test,op,ms,rows)
-- SELECT 'salient_facts(gravity)', 'salience (DEGRADED)',
--        round(1000*extract(epoch from clock_timestamp()-statement_timestamp())::numeric,1), count(*)
-- FROM laplace.salient_facts(laplace.word_id('gravity'), NULL, 24);

RESET statement_timeout;

\echo ''
\echo '=== Laplace substrate benchmark (server-side latency; run twice for warm numbers) ==='
SELECT test, op, ms AS latency_ms, rows
FROM _bench ORDER BY seq;

\echo ''
\echo '=== Field under test ==='
SELECT * FROM laplace.substrate_counts() LIMIT 3;
