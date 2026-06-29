SELECT column_name
FROM information_schema.columns
WHERE table_schema = 'laplace' AND table_name = 'attestations'
ORDER BY ordinal_position;
