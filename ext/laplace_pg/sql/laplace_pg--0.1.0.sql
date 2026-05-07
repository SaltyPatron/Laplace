-- laplace_pg extension version 0.1.0
-- Phase 2 / Track C / C2 + C3 — schema namespace + SQL function bindings to
-- the native services that have landed.
--
-- Schema migrations (entity, edge, provenance, significance, physicality,
-- sequence) are applied separately by `db-bootstrap.ps1` from
-- `sql/migrations/0001_*.sql` ... `0006_*.sql` and CREATEd into the laplace
-- schema before this extension's functions are wired against them.

\echo Use "CREATE EXTENSION laplace_pg" to load this file. \quit

CREATE SCHEMA IF NOT EXISTS laplace;

COMMENT ON SCHEMA laplace IS
  'Laplace substrate — entity / edge / physicality / provenance / significance / sequence + GEOMETRY4D';

-- ---------- Hashing ----------------------------------------------------

CREATE FUNCTION laplace.hash_atom(content bytea)
RETURNS bytea
AS '$libdir/laplace_pg', 'pg_laplace_hash_atom'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

COMMENT ON FUNCTION laplace.hash_atom(bytea) IS
  '32-byte BLAKE3-256 atom hash of raw content bytes.';

CREATE FUNCTION laplace.hash_composition(child_hashes bytea[], rle_counts integer[])
RETURNS bytea
AS '$libdir/laplace_pg', 'pg_laplace_hash_composition'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

COMMENT ON FUNCTION laplace.hash_composition(bytea[], integer[]) IS
  'Merkle hash of a composition: ordered children + parallel RLE counts.';

-- ---------- Glicko-2 ----------------------------------------------------

CREATE FUNCTION laplace.glicko2_apply(
  in_mu      double precision,
  in_phi     double precision,
  in_sigma   double precision,
  in_games   integer,
  opp_mu     double precision[],
  opp_phi    double precision[],
  scores     double precision[],
  weights    double precision[],
  tau        double precision DEFAULT 0.5
) RETURNS TABLE(out_mu double precision, out_phi double precision,
                out_sigma double precision, out_games integer)
AS '$libdir/laplace_pg', 'pg_laplace_glicko2_apply'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

COMMENT ON FUNCTION laplace.glicko2_apply IS
  'Apply one rating period (Glickman 2013) and return updated (mu, phi, sigma, games).';

-- ---------- Super-Fibonacci on S³ --------------------------------------

CREATE FUNCTION laplace.super_fibonacci_4d(i integer, total integer)
RETURNS bytea
AS '$libdir/laplace_pg', 'pg_laplace_super_fibonacci_4d'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

COMMENT ON FUNCTION laplace.super_fibonacci_4d(integer, integer) IS
  '32-byte bytea (4 LE doubles x,y,z,w) — sample i of total on S^3 via Alexa CVPR 2022 spiral.';

-- ---------- Hilbert curve linearization --------------------------------

CREATE FUNCTION laplace.hilbert_index(position bytea)
RETURNS bigint
AS '$libdir/laplace_pg', 'pg_laplace_hilbert_index'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

COMMENT ON FUNCTION laplace.hilbert_index(bytea) IS
  '64-bit Hilbert index for a 4D position bytea (Skilling 2003, 16 bits per axis).';

-- ---------- Sidecar metadata predicates --------------------------------
--
-- Operate on the physicality.prime_flags / structural_flags / language_id
-- / model_id sidecar columns — pure SQL, IMMUTABLE + PARALLEL SAFE so they
-- participate in index predicate pushdown. Position is content-derived and
-- never mutates; these flags accumulate via UPDATE ... SET prime_flags =
-- prime_flags | $new as more sources attest.

CREATE FUNCTION laplace.has_prime(flags bigint, mask bigint)
RETURNS boolean
AS $$ SELECT ($1 & $2) <> 0 $$
LANGUAGE SQL IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION laplace.has_prime(bigint, bigint) IS
  'True if any bit in mask is set in flags. Use for OR-style filters.';

CREATE FUNCTION laplace.has_all_primes(flags bigint, mask bigint)
RETURNS boolean
AS $$ SELECT ($1 & $2) = $2 $$
LANGUAGE SQL IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION laplace.has_all_primes(bigint, bigint) IS
  'True only if every bit in mask is set in flags. Use for conjunctive filters.';

CREATE FUNCTION laplace.has_structural(flags smallint, mask smallint)
RETURNS boolean
AS $$ SELECT ($1::int & $2::int) <> 0 $$
LANGUAGE SQL IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION laplace.has_structural(smallint, smallint) IS
  'True if any bit in mask is set in structural_flags.';
