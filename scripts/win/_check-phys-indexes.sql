SELECT c.relname, pg_get_indexdef(i.indexrelid) AS def
FROM pg_index i
JOIN pg_class c ON c.oid = i.indexrelid
JOIN pg_class t ON t.oid = i.indrelid
JOIN pg_namespace n ON n.oid = t.relnamespace
WHERE n.nspname = 'laplace' AND t.relname = 'physicalities'
  AND NOT i.indisprimary AND NOT i.indisunique
ORDER BY c.relname;
