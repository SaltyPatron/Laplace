SELECT p.proname, l.lanname, p.proconfig
FROM pg_proc p
JOIN pg_language l ON l.oid = p.prolang
JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname = 'laplace' AND p.proname = 'laplace_apply_batch';
