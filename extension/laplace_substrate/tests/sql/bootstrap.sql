CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace WITH SCHEMA public;
CREATE EXTENSION IF NOT EXISTS laplace_geom;
CREATE EXTENSION IF NOT EXISTS laplace_substrate;

-- PostgreSQL caches each loaded module for the backend lifetime. A delivery
-- must bind every canonical Native function to the packaged byte generation.
DO $$
DECLARE module text;
BEGIN
  SELECT p.probin INTO module FROM pg_proc p
  JOIN pg_depend d ON d.classid='pg_proc'::regclass AND d.objid=p.oid
  JOIN pg_extension e ON e.oid=d.refobjid AND d.refclassid='pg_extension'::regclass
  WHERE e.extname='laplace' AND d.deptype='e' AND p.proname='laplace_restore_function_bindings';
  IF module IS NULL OR module !~ '(^|/)laplace_[0-9a-f]{20}$' THEN
    RAISE EXCEPTION 'canonical Native module lacks its packaged generation: %',module;
  END IF;
  IF EXISTS (
    SELECT FROM pg_proc p JOIN pg_language l ON l.oid=p.prolang
    JOIN pg_depend d ON d.classid='pg_proc'::regclass AND d.objid=p.oid
    JOIN pg_extension e ON e.oid=d.refobjid AND d.refclassid='pg_extension'::regclass
    WHERE e.extname='laplace' AND d.deptype='e' AND l.lanname='c'
      AND p.probin IS DISTINCT FROM module
  ) THEN
    RAISE EXCEPTION 'canonical Native functions reference different module generations';
  END IF;
END $$;

