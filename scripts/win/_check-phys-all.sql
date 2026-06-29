SELECT n.nspname, c.relname, t.relname AS table_name
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
LEFT JOIN pg_index i ON i.indexrelid = c.oid
LEFT JOIN pg_class t ON t.oid = i.indrelid
WHERE c.relname LIKE 'physicalities%'
ORDER BY n.nspname, c.relname;
