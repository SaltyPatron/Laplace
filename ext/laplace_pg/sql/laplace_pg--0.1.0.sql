-- laplace_pg extension version 0.1.0
-- Phase 1 / Track A — scaffolding only.
-- Real type / operator / function registrations land in Phase 2 (Track C2/C3/C4).

-- complain if script is sourced in psql, rather than via CREATE EXTENSION
\echo Use "CREATE EXTENSION laplace_pg" to load this file. \quit

CREATE SCHEMA IF NOT EXISTS laplace;

COMMENT ON SCHEMA laplace IS
  'Laplace substrate — entity / edge / physicality / provenance / significance / sequence + GEOMETRY4D';
