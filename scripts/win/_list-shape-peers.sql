\set ON_ERROR_STOP on
SELECT p.oid::regprocedure AS sig
FROM pg_proc p
JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname = 'laplace' AND p.proname = 'word_shape_peers'
ORDER BY 1;
