SHOW extension_control_path;
SHOW dynamic_library_path;
SELECT extname, extversion FROM pg_extension ORDER BY 1;
SELECT pg_get_extensiondef(oid) FROM pg_extension WHERE extname = 'laplace_substrate';
