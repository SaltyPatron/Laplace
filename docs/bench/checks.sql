-- Reusable before/after state check. Run: psql ... -f docs/bench/checks.sql
\timing on
\echo '== row counts =='
SELECT 'entities' AS t, count(*) FROM laplace.entities
UNION ALL SELECT 'physicalities', count(*) FROM laplace.physicalities
UNION ALL SELECT 'attestations', count(*) FROM laplace.attestations
ORDER BY t;
\echo '== physicalities indexes =='
SELECT indexname FROM pg_indexes WHERE schemaname='laplace' AND tablename='physicalities' ORDER BY indexname;
\echo '== physicalities constraints (FK trigger lives here) =='
SELECT conname, contype FROM pg_constraint WHERE conrelid='laplace.physicalities'::regclass ORDER BY conname;
\echo '== table + index sizes =='
SELECT c.relname, pg_size_pretty(pg_total_relation_size(c.oid)) AS total,
       pg_size_pretty(pg_relation_size(c.oid)) AS heap,
       pg_size_pretty(pg_total_relation_size(c.oid) - pg_relation_size(c.oid)) AS idx_plus_toast
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname='laplace' AND c.relname IN ('entities','physicalities','attestations')
ORDER BY c.relname;
