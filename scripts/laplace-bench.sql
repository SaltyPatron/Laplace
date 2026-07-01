\set ON_ERROR_STOP off
\pset footer off
\timing off
SET statement_timeout = '30s';

DROP TABLE IF EXISTS _bench;
CREATE TEMP TABLE _bench (
    seq    serial,
    test   text,
    op     text,
    ms     numeric,
    rows   bigint
);

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

RESET statement_timeout;

\echo ''
\echo '=== Laplace substrate benchmark (server-side latency; run twice for warm numbers) ==='
SELECT test, op, ms AS latency_ms, rows
FROM _bench ORDER BY seq;

\echo ''
\echo '=== Field under test ==='
SELECT * FROM laplace.substrate_counts() LIMIT 3;
