-- Phase 1 / Track A — sample pgTAP test verifying the laplace_pg extension
-- loads and the laplace schema is created. Real per-service contract tests
-- land in Phase 2 (Track C) as schema and SQL services come online.

\set ON_ERROR_STOP on

BEGIN;

-- pgTAP must be available; install if missing.
CREATE EXTENSION IF NOT EXISTS pgtap;

SELECT plan(2);

-- Test 1: laplace_pg extension is installed
SELECT ok(
  EXISTS(SELECT 1 FROM pg_extension WHERE extname = 'laplace_pg'),
  'laplace_pg extension is installed'
);

-- Test 2: laplace schema exists
SELECT ok(
  EXISTS(SELECT 1 FROM information_schema.schemata WHERE schema_name = 'laplace'),
  'laplace schema exists'
);

SELECT * FROM finish();
ROLLBACK;
