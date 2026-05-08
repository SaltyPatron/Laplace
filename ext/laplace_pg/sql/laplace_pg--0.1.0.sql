-- laplace_pg extension v0.1.0 — canonical schema + GEOMETRY4D type family
-- registration + native function bindings.
--
-- This file is the substrate's frozen v0.1.0 schema definition. It is NOT
-- a sequential migration. The substrate is content-addressed; its schema
-- is defined once, by the extension, when the extension is installed.
-- Schema refactors before 1.0 are made in place by editing this file.
--
-- Per substrate invariant 1: every column that holds identity is
-- entity_hash bytea (BLAKE3-256). Per invariant 2: every column that
-- holds a 4D position is POINT4D (NOT bytea), and trajectories are
-- LINESTRING4D, and bounding boxes are BOX4D — all members of the
-- GEOMETRY4D parallel type family the synthesis doc describes
-- (substrate-synthesis.md §5).
--
-- Per invariant 6: foundational categorical bit flags are bigint
-- (prime_flags) / smallint (structural_flags) columns on entity, NOT
-- entities themselves. Per invariant 7: physicality is partitioned by
-- physicality_type_hash (the type itself an entity).

\echo Use "CREATE EXTENSION laplace_pg" to load this file. \quit

CREATE SCHEMA IF NOT EXISTS laplace;

COMMENT ON SCHEMA laplace IS
  'Laplace substrate — entity / edge / physicality / provenance / significance / sequence + GEOMETRY4D';

-- =====================================================================
-- GEOMETRY4D type family — synthesis doc §5
-- =====================================================================
-- Parallel to PostGIS GEOMETRYZM, NOT a repurposing of it. Genuine 4D
-- coordinates (x, y, z, w) — the 4th component is the substrate's
-- quaternion W component, NOT the conventional measure scalar of M.
-- This v0.1.0 ships POINT4D + LINESTRING4D + BOX4D; remaining subtypes
-- (POLYGON4D, MULTI*, TRIANGLE4D, TIN4D, POLYHEDRALSURFACE4D,
-- CIRCULARSTRING4D, COMPOUNDCURVE4D, CURVEPOLYGON4D, MULTICURVE4D,
-- MULTISURFACE4D, GEOMETRYCOLLECTION4D) land as their consumers come
-- online per the synthesis doc's §5 type family list.

-- ---------- POINT4D ---------------------------------------------------

CREATE TYPE point4d;  -- shell type

CREATE FUNCTION point4d_in(cstring) RETURNS point4d
AS '$libdir/laplace_pg', 'point4d_in'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION point4d_out(point4d) RETURNS cstring
AS '$libdir/laplace_pg', 'point4d_out'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION point4d_send(point4d) RETURNS bytea
AS '$libdir/laplace_pg', 'point4d_send'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION point4d_recv(internal) RETURNS point4d
AS '$libdir/laplace_pg', 'point4d_recv'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE TYPE point4d (
    INPUT          = point4d_in,
    OUTPUT         = point4d_out,
    SEND           = point4d_send,
    RECEIVE        = point4d_recv,
    INTERNALLENGTH = 32,
    ALIGNMENT      = double,
    STORAGE        = plain
);

COMMENT ON TYPE point4d IS
  'Substrate 4D point — 4 IEEE 754 doubles (x, y, z, w). Independent of PostGIS.';

-- POINT4D operators
CREATE FUNCTION laplace.distance(point4d, point4d) RETURNS double precision
AS '$libdir/laplace_pg', 'point4d_distance'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.geodesic(point4d, point4d) RETURNS double precision
AS '$libdir/laplace_pg', 'point4d_geodesic'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.dot(point4d, point4d) RETURNS double precision
AS '$libdir/laplace_pg', 'point4d_dot'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.norm(point4d) RETURNS double precision
AS '$libdir/laplace_pg', 'point4d_norm'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.slerp(point4d, point4d, double precision) RETURNS point4d
AS '$libdir/laplace_pg', 'point4d_slerp'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.normalize(point4d) RETURNS point4d
AS '$libdir/laplace_pg', 'point4d_normalize'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.hilbert_index(point4d) RETURNS bigint
AS '$libdir/laplace_pg', 'point4d_hilbert_index'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.super_fibonacci(integer, integer) RETURNS point4d
AS '$libdir/laplace_pg', 'point4d_super_fibonacci'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION point4d_eq(point4d, point4d) RETURNS boolean
AS '$libdir/laplace_pg', 'point4d_eq'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE OPERATOR <-> (
    LEFTARG   = point4d,
    RIGHTARG  = point4d,
    PROCEDURE = laplace.distance
);

CREATE OPERATOR = (
    LEFTARG    = point4d,
    RIGHTARG   = point4d,
    PROCEDURE  = point4d_eq,
    COMMUTATOR = =,
    HASHES, MERGES
);

-- ---------- LINESTRING4D ----------------------------------------------

CREATE TYPE linestring4d;

CREATE FUNCTION linestring4d_in(cstring) RETURNS linestring4d
AS '$libdir/laplace_pg', 'linestring4d_in'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION linestring4d_out(linestring4d) RETURNS cstring
AS '$libdir/laplace_pg', 'linestring4d_out'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION linestring4d_send(linestring4d) RETURNS bytea
AS '$libdir/laplace_pg', 'linestring4d_send'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION linestring4d_recv(internal) RETURNS linestring4d
AS '$libdir/laplace_pg', 'linestring4d_recv'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE TYPE linestring4d (
    INPUT          = linestring4d_in,
    OUTPUT         = linestring4d_out,
    SEND           = linestring4d_send,
    RECEIVE        = linestring4d_recv,
    INTERNALLENGTH = VARIABLE,
    ALIGNMENT      = double,
    STORAGE        = extended
);

COMMENT ON TYPE linestring4d IS
  'Substrate 4D linestring — N>=2 POINT4D vertices. Tier-1+ composition trajectories.';

CREATE FUNCTION laplace.vertex_count(linestring4d) RETURNS integer
AS '$libdir/laplace_pg', 'linestring4d_vertex_count'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.vertex_at(linestring4d, integer) RETURNS point4d
AS '$libdir/laplace_pg', 'linestring4d_vertex_at'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.length(linestring4d) RETURNS double precision
AS '$libdir/laplace_pg', 'linestring4d_length'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.vertex_centroid(linestring4d) RETURNS point4d
AS '$libdir/laplace_pg', 'linestring4d_vertex_centroid'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.frechet_distance(linestring4d, linestring4d) RETURNS double precision
AS '$libdir/laplace_pg', 'linestring4d_frechet'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.hausdorff_distance(linestring4d, linestring4d) RETURNS double precision
AS '$libdir/laplace_pg', 'linestring4d_hausdorff'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.envelope(linestring4d) RETURNS box4d
AS '$libdir/laplace_pg', 'linestring4d_envelope'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

-- ---------- BOX4D -----------------------------------------------------

CREATE TYPE box4d;

CREATE FUNCTION box4d_in(cstring) RETURNS box4d
AS '$libdir/laplace_pg', 'box4d_in'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION box4d_out(box4d) RETURNS cstring
AS '$libdir/laplace_pg', 'box4d_out'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION box4d_send(box4d) RETURNS bytea
AS '$libdir/laplace_pg', 'box4d_send'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION box4d_recv(internal) RETURNS box4d
AS '$libdir/laplace_pg', 'box4d_recv'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE TYPE box4d (
    INPUT          = box4d_in,
    OUTPUT         = box4d_out,
    SEND           = box4d_send,
    RECEIVE        = box4d_recv,
    INTERNALLENGTH = 64,
    ALIGNMENT      = double,
    STORAGE        = plain
);

COMMENT ON TYPE box4d IS
  'Substrate 4D axis-aligned bounding box — 8 IEEE 754 doubles.';

-- =====================================================================
-- POINT4D GiST opclass — KNN spatial index on the 4D point type
-- =====================================================================
-- Storage = BOX4D; leaf compress wraps each POINT4D as a degenerate box
-- (min == max == p), internal nodes union children's bounding boxes.
-- Supports two strategies on the 4D point space:
--   1  =   (point4d, point4d)             — exact equality with recheck
--   15 <-> (point4d, point4d) ORDER BY    — KNN distance ordering
-- The <-> operator (defined above) carries through to the index for
-- ORDER BY KNN queries:
--   SELECT entity_hash FROM physicality
--   WHERE physicality_type_hash = $1
--   ORDER BY position <-> $2 LIMIT $k;
-- The planner picks the GiST index if one exists on the position column.

CREATE FUNCTION point4d_gist_consistent(internal, point4d, smallint, oid, internal)
RETURNS boolean
AS '$libdir/laplace_pg', 'point4d_gist_consistent'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION point4d_gist_union(internal, internal) RETURNS box4d
AS '$libdir/laplace_pg', 'point4d_gist_union'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION point4d_gist_compress(internal) RETURNS internal
AS '$libdir/laplace_pg', 'point4d_gist_compress'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION point4d_gist_penalty(internal, internal, internal) RETURNS internal
AS '$libdir/laplace_pg', 'point4d_gist_penalty'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION point4d_gist_picksplit(internal, internal) RETURNS internal
AS '$libdir/laplace_pg', 'point4d_gist_picksplit'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION point4d_gist_same(box4d, box4d, internal) RETURNS internal
AS '$libdir/laplace_pg', 'point4d_gist_same'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION point4d_gist_distance(internal, point4d, smallint, oid, internal)
RETURNS double precision
AS '$libdir/laplace_pg', 'point4d_gist_distance'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE OPERATOR CLASS point4d_gist_ops
    DEFAULT FOR TYPE point4d USING gist AS
    OPERATOR  1   =   (point4d, point4d),
    OPERATOR  15  <-> (point4d, point4d) FOR ORDER BY float_ops,
    FUNCTION  1 point4d_gist_consistent(internal, point4d, smallint, oid, internal),
    FUNCTION  2 point4d_gist_union(internal, internal),
    FUNCTION  3 point4d_gist_compress(internal),
    FUNCTION  5 point4d_gist_penalty(internal, internal, internal),
    FUNCTION  6 point4d_gist_picksplit(internal, internal),
    FUNCTION  7 point4d_gist_same(box4d, box4d, internal),
    FUNCTION  8 point4d_gist_distance(internal, point4d, smallint, oid, internal),
    STORAGE box4d;

-- =====================================================================
-- LINESTRING4D GiST opclass — Fréchet KNN on tier-1+ trajectories
-- =====================================================================
-- Storage = BOX4D; leaf compress takes the AABB over a LINESTRING4D's
-- vertices, internal nodes union children's bounding boxes. Re-uses
-- the BOX4D-keyed union/same/penalty/picksplit functions registered
-- above for point4d_gist_ops (they operate purely on BOX4D, identical
-- regardless of leaf source type).
--
-- Strategy:
--   15 <-> (linestring4d, linestring4d) ORDER BY  — KNN by Fréchet
--
-- The <-> operator is bound to laplace.frechet_distance (the substrate's
-- named primitive for trajectory shape similarity per substrate-synthesis
-- lines 78, 246, 289). Index lower bound is bbox_min_distance(query_bbox,
-- entry_bbox), admissible because every alignment between two
-- trajectories has at least one matched pair with distance >=
-- bbox_min_distance, so the alignment's max pair-distance (its Fréchet
-- score) is bounded below by this value.
--
-- Trajectory-near-a-point queries belong on the centroid POINT4D column
-- with point4d_gist_ops, NOT on this opclass — per synthesis line 76 the
-- substrate stores a tier-1+ composition's representative position as
-- the centroid POINT4D in the 4-ball; the LINESTRING4D is the trajectory
-- itself, queried by shape, not by proximity-to-point.

CREATE FUNCTION linestring4d_gist_consistent(internal, linestring4d, smallint, oid, internal)
RETURNS boolean
AS '$libdir/laplace_pg', 'linestring4d_gist_consistent'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION linestring4d_gist_compress(internal) RETURNS internal
AS '$libdir/laplace_pg', 'linestring4d_gist_compress'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION linestring4d_gist_distance(internal, linestring4d, smallint, oid, internal)
RETURNS double precision
AS '$libdir/laplace_pg', 'linestring4d_gist_distance'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE OPERATOR <-> (
    LEFTARG    = linestring4d,
    RIGHTARG   = linestring4d,
    PROCEDURE  = laplace.frechet_distance
);

CREATE OPERATOR CLASS linestring4d_gist_ops
    DEFAULT FOR TYPE linestring4d USING gist AS
    OPERATOR  15  <-> (linestring4d, linestring4d) FOR ORDER BY float_ops,
    FUNCTION  1 linestring4d_gist_consistent(internal, linestring4d, smallint, oid, internal),
    FUNCTION  2 point4d_gist_union(internal, internal),
    FUNCTION  3 linestring4d_gist_compress(internal),
    FUNCTION  5 point4d_gist_penalty(internal, internal, internal),
    FUNCTION  6 point4d_gist_picksplit(internal, internal),
    FUNCTION  7 point4d_gist_same(box4d, box4d, internal),
    FUNCTION  8 linestring4d_gist_distance(internal, linestring4d, smallint, oid, internal),
    STORAGE box4d;

-- =====================================================================
-- UcdLookupService — Track B / B14
-- =====================================================================
-- O(1) lookup of any Unicode codepoint's substrate identity (hash, S³
-- position, Hilbert index, prime_flags) against the linked-in codepoint
-- table emitted by Laplace.SeedTableGenerator. Each function is STRICT
-- and IMMUTABLE; the table is constant data so PG can fully fold these
-- into query plans (e.g. partition pruning by laplace.ucd_hilbert($cp)).
-- Returns NULL for codepoints outside [0, 0x110000) or absent from the
-- table (cannot happen for the canonical full-Unicode-space build).

CREATE FUNCTION laplace.ucd_hash(integer) RETURNS bytea
AS '$libdir/laplace_pg', 'pg_laplace_ucd_hash'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.ucd_position(integer) RETURNS point4d
AS '$libdir/laplace_pg', 'pg_laplace_ucd_position'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.ucd_hilbert(integer) RETURNS bigint
AS '$libdir/laplace_pg', 'pg_laplace_ucd_hilbert'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.ucd_prime_flags(integer) RETURNS bigint
AS '$libdir/laplace_pg', 'pg_laplace_ucd_prime_flags'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.ucd_exists(integer) RETURNS boolean
AS '$libdir/laplace_pg', 'pg_laplace_ucd_exists'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

-- =====================================================================
-- QuaternionService — Track B / B8
-- =====================================================================
-- Hamilton (i, j, k) algebra over POINT4D in (x, y, z, w) layout. Used
-- for substrate composition operations on S³ unit quaternions: rotation
-- composition, S³ slerp endpoint construction, antipodal canonicalization.

CREATE FUNCTION laplace.quaternion_multiply(point4d, point4d) RETURNS point4d
AS '$libdir/laplace_pg', 'quaternion_multiply'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.quaternion_conjugate(point4d) RETURNS point4d
AS '$libdir/laplace_pg', 'quaternion_conjugate'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.quaternion_inverse(point4d) RETURNS point4d
AS '$libdir/laplace_pg', 'quaternion_inverse'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

-- =====================================================================
-- HilbertCurveService inverse — Track B / B9
-- =====================================================================
-- Forward direction (point4d → bigint) is laplace.point4d_hilbert_index.
-- This adds the inverse so the curve round-trips at the SQL surface.
-- Lossy below 2^-15 per axis (forward quantizes each axis to 16 bits);
-- callers should treat the returned POINT4D as a representative grid-
-- cell corner rather than the original input.

CREATE FUNCTION laplace.hilbert_decode(bigint) RETURNS point4d
AS '$libdir/laplace_pg', 'hilbert_decode'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

-- =====================================================================
-- RleService — Track B / B3
-- =====================================================================
-- Substrate invariant 3: "Entities referenced as FEW times as physically
-- possible. RLE everywhere there's adjacency." Encode/decode at the SQL
-- surface so DDL paths (entity_child rle_count emission, sequence runs)
-- and ad-hoc tooling collapse runs without round-tripping managed code.

CREATE FUNCTION laplace.rle_encode_bytes(
  input  bytea,
  OUT values bytea,
  OUT counts integer[]
) RETURNS record
AS '$libdir/laplace_pg', 'rle_encode_bytes'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.rle_decode_bytes(values bytea, counts integer[]) RETURNS bytea
AS '$libdir/laplace_pg', 'rle_decode_bytes'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

-- =====================================================================
-- Hashing
-- =====================================================================

CREATE FUNCTION laplace.hash_atom(content bytea) RETURNS bytea
AS '$libdir/laplace_pg', 'pg_laplace_hash_atom'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

CREATE FUNCTION laplace.hash_composition(child_hashes bytea[], rle_counts integer[]) RETURNS bytea
AS '$libdir/laplace_pg', 'pg_laplace_hash_composition'
LANGUAGE C IMMUTABLE PARALLEL SAFE STRICT;

-- =====================================================================
-- Glicko-2
-- =====================================================================

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

-- =====================================================================
-- Bitmask predicates (entity.prime_flags / structural_flags)
-- =====================================================================

CREATE FUNCTION laplace.has_prime(flags bigint, mask bigint) RETURNS boolean
AS $$ SELECT ($1 & $2) <> 0 $$
LANGUAGE SQL IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION laplace.has_all_primes(flags bigint, mask bigint) RETURNS boolean
AS $$ SELECT ($1 & $2) = $2 $$
LANGUAGE SQL IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION laplace.has_structural(flags smallint, mask smallint) RETURNS boolean
AS $$ SELECT ($1::int & $2::int) <> 0 $$
LANGUAGE SQL IMMUTABLE PARALLEL SAFE;

-- =====================================================================
-- Canonical schema — ONE definition (no migrations pre-1.0)
-- =====================================================================

-- ---------- entity ----------------------------------------------------
-- Tier 0 = Unicode codepoint atoms (full 1,114,112 across 17 planes).
-- Tier 1+ = compositions of lower-tier entities.
-- Position columns are GEOMETRY4D types (NOT bytea):
--   centroid_4d  (POINT4D)      — content-derived representative position
--                                  (super-fib for tier-0; centroid of
--                                  constituent positions for tier-1+)
--   trajectory   (LINESTRING4D) — full trajectory through constituent
--                                  positions; null at tier-0.

CREATE TABLE entity (
    entity_hash      bytea          NOT NULL,
    tier             smallint       NOT NULL,
    codepoint        integer,
    content          bytea          NOT NULL DEFAULT '',
    centroid_4d      point4d,
    trajectory       linestring4d,
    prime_flags      bigint         NOT NULL DEFAULT 0,
    structural_flags smallint       NOT NULL DEFAULT 0,
    created_at       timestamptz    NOT NULL DEFAULT now(),
    PRIMARY KEY (entity_hash, tier)
)
PARTITION BY RANGE (tier);

CREATE TABLE entity_tier0       PARTITION OF entity FOR VALUES FROM (0) TO (1);
CREATE TABLE entity_tier1       PARTITION OF entity FOR VALUES FROM (1) TO (2);
CREATE TABLE entity_tier2       PARTITION OF entity FOR VALUES FROM (2) TO (3);
CREATE TABLE entity_tier3       PARTITION OF entity FOR VALUES FROM (3) TO (4);
CREATE TABLE entity_tier4       PARTITION OF entity FOR VALUES FROM (4) TO (5);
CREATE TABLE entity_tier_higher PARTITION OF entity FOR VALUES FROM (5) TO (32767);

CREATE UNIQUE INDEX entity_tier0_by_codepoint
    ON entity_tier0 (codepoint)
    WHERE codepoint IS NOT NULL;

CREATE INDEX entity_by_prime_flags ON entity (tier, prime_flags);

-- ---------- entity_child (composition with RLE) -----------------------

CREATE TABLE entity_child (
    parent_hash      bytea       NOT NULL,
    parent_tier      smallint    NOT NULL,
    position         integer     NOT NULL,
    child_hash       bytea       NOT NULL,
    child_tier       smallint    NOT NULL,
    rle_count        integer     NOT NULL CHECK (rle_count > 0),
    PRIMARY KEY (parent_hash, parent_tier, position),
    FOREIGN KEY (parent_hash, parent_tier) REFERENCES entity (entity_hash, tier),
    FOREIGN KEY (child_hash, child_tier)   REFERENCES entity (entity_hash, tier)
);

CREATE INDEX entity_child_by_child ON entity_child (child_hash, child_tier);

-- ---------- edge / edge_member ----------------------------------------

CREATE TABLE edge (
    edge_hash        bytea       NOT NULL,
    edge_type_hash   bytea       NOT NULL,
    member_count     smallint    NOT NULL CHECK (member_count > 0),
    created_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (edge_hash, edge_type_hash)
)
PARTITION BY HASH (edge_type_hash);

DO $$
DECLARE i int;
BEGIN
    FOR i IN 0..15 LOOP
        EXECUTE format(
            'CREATE TABLE edge_p%s PARTITION OF edge '
            'FOR VALUES WITH (modulus 16, remainder %s)', i, i);
    END LOOP;
END$$;

CREATE TABLE edge_member (
    edge_hash         bytea    NOT NULL,
    edge_type_hash    bytea    NOT NULL,
    role_hash         bytea    NOT NULL,
    role_position     smallint NOT NULL DEFAULT 0,
    participant_hash  bytea    NOT NULL,
    PRIMARY KEY (edge_hash, edge_type_hash, role_hash, role_position),
    FOREIGN KEY (edge_hash, edge_type_hash) REFERENCES edge (edge_hash, edge_type_hash)
);

CREATE INDEX edge_member_by_participant ON edge_member (participant_hash);
CREATE INDEX edge_member_by_role        ON edge_member (role_hash);

-- ---------- provenance ------------------------------------------------

CREATE TABLE provenance (
    provenance_hash   bytea       NOT NULL PRIMARY KEY,
    source_hash       bytea       NOT NULL,
    location_hash     bytea,
    recorded_at       timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX provenance_by_source ON provenance (source_hash);

CREATE TABLE entity_provenance (
    entity_hash       bytea     NOT NULL,
    entity_tier       smallint  NOT NULL,
    provenance_hash   bytea     NOT NULL,
    PRIMARY KEY (entity_hash, entity_tier, provenance_hash),
    FOREIGN KEY (entity_hash, entity_tier) REFERENCES entity (entity_hash, tier),
    FOREIGN KEY (provenance_hash)          REFERENCES provenance (provenance_hash)
);

CREATE INDEX entity_provenance_by_provenance ON entity_provenance (provenance_hash);

CREATE TABLE edge_provenance (
    edge_hash         bytea  NOT NULL,
    edge_type_hash    bytea  NOT NULL,
    provenance_hash   bytea  NOT NULL,
    PRIMARY KEY (edge_hash, edge_type_hash, provenance_hash),
    FOREIGN KEY (edge_hash, edge_type_hash) REFERENCES edge (edge_hash, edge_type_hash),
    FOREIGN KEY (provenance_hash)           REFERENCES provenance (provenance_hash)
);

CREATE INDEX edge_provenance_by_provenance ON edge_provenance (provenance_hash);

-- ---------- significance (three-layer Glicko-2) -----------------------

CREATE TABLE significance_source (
    source_hash    bytea            NOT NULL PRIMARY KEY,
    mu             double precision NOT NULL DEFAULT 0.0,
    phi            double precision NOT NULL DEFAULT 2.014761872416,
    sigma          double precision NOT NULL DEFAULT 0.06,
    games          integer          NOT NULL DEFAULT 0,
    last_updated   timestamptz      NOT NULL DEFAULT now()
);

CREATE TABLE significance_entity (
    entity_hash    bytea            NOT NULL,
    entity_tier    smallint         NOT NULL,
    mu             double precision NOT NULL DEFAULT 0.0,
    phi            double precision NOT NULL DEFAULT 2.014761872416,
    sigma          double precision NOT NULL DEFAULT 0.06,
    games          integer          NOT NULL DEFAULT 0,
    last_updated   timestamptz      NOT NULL DEFAULT now(),
    PRIMARY KEY (entity_hash, entity_tier),
    FOREIGN KEY (entity_hash, entity_tier) REFERENCES entity (entity_hash, tier)
);

CREATE TABLE significance_edge (
    edge_hash      bytea            NOT NULL,
    edge_type_hash bytea            NOT NULL,
    mu             double precision NOT NULL DEFAULT 0.0,
    phi            double precision NOT NULL DEFAULT 2.014761872416,
    sigma          double precision NOT NULL DEFAULT 0.06,
    games          integer          NOT NULL DEFAULT 0,
    last_updated   timestamptz      NOT NULL DEFAULT now(),
    PRIMARY KEY (edge_hash, edge_type_hash),
    FOREIGN KEY (edge_hash, edge_type_hash) REFERENCES edge (edge_hash, edge_type_hash)
);

-- ---------- physicality (partitioned by physicality_type_hash) --------

CREATE TABLE physicality (
    physicality_type_hash bytea       NOT NULL,
    entity_hash           bytea       NOT NULL,
    entity_tier           smallint    NOT NULL,
    position              point4d     NOT NULL,
    bbox                  box4d,
    hilbert_index         bigint      NOT NULL DEFAULT 0,
    created_at            timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (physicality_type_hash, entity_hash, entity_tier)
)
PARTITION BY HASH (physicality_type_hash);

DO $$
DECLARE i int;
BEGIN
    FOR i IN 0..7 LOOP
        EXECUTE format(
            'CREATE TABLE physicality_p%s PARTITION OF physicality '
            'FOR VALUES WITH (modulus 8, remainder %s)', i, i);
    END LOOP;
END$$;

CREATE INDEX physicality_by_entity   ON physicality (entity_hash, entity_tier);
CREATE INDEX physicality_by_hilbert  ON physicality (physicality_type_hash, hilbert_index);

-- 4D GiST spatial index — backs `ORDER BY position <-> $q LIMIT $k`
-- queries used by firefly KNN, fault-line edge detection, and Voronoi
-- consensus traversal. Uses the point4d_gist_ops opclass declared above.
CREATE INDEX physicality_position_gist ON physicality USING gist (position);

-- ---------- sequence (ordered children where RLE doesn't compress) ----

CREATE TABLE sequence (
    parent_hash    bytea     NOT NULL,
    parent_tier    smallint  NOT NULL,
    position       integer   NOT NULL,
    child_hash     bytea     NOT NULL,
    child_tier     smallint  NOT NULL,
    PRIMARY KEY (parent_hash, parent_tier, position),
    FOREIGN KEY (parent_hash, parent_tier) REFERENCES entity (entity_hash, tier),
    FOREIGN KEY (child_hash, child_tier)   REFERENCES entity (entity_hash, tier)
);

CREATE INDEX sequence_by_child ON sequence (child_hash, child_tier);
