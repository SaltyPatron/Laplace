#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql/laplace_geom.sql.in"
#line 1 "<built-in>"
#line 1 "<built-in>"
#line 471 "<built-in>"
#line 1 "<command line>"
#line 1 "<built-in>"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql/laplace_geom.sql.in"
#line 1 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\sqldefines.h"
#line 2 "D:/Repositories/Laplace/extension/laplace_geom/sql/laplace_geom.sql.in"
#line 1 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql\\functions/laplace_geom_version.sql.in"
CREATE FUNCTION laplace_geom_version()
    RETURNS text
    AS 'MODULE_PATHNAME', 'pg_laplace_geom_version'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 2 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql\\functions/laplace_hash128_blake3.sql.in"
CREATE FUNCTION laplace_hash128_blake3(data bytea)
    RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_laplace_hash128_blake3'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 3 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql\\functions/laplace_hash128_merkle.sql.in"
CREATE FUNCTION laplace_hash128_merkle(tier smallint, child_hashes bytea[])
    RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_laplace_hash128_merkle'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 4 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql\\functions/laplace_hilbert_encode.sql.in"
CREATE FUNCTION @extschema@.laplace_hilbert_encode(p geometry)
    RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_laplace_hilbert_encode'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 5 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql\\functions/laplace_hilbert_decode.sql.in"
CREATE FUNCTION @extschema@.laplace_hilbert_decode(h bytea)
    RETURNS geometry
    AS 'MODULE_PATHNAME', 'pg_laplace_hilbert_decode'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 6 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql\\functions/laplace_mantissa_pack.sql.in"
CREATE FUNCTION @extschema@.laplace_mantissa_pack(
        entity_id bytea,
        ordinal integer,
        run_length integer,
        flags bigint)
    RETURNS geometry
    AS 'MODULE_PATHNAME', 'pg_laplace_mantissa_pack'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 7 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql\\functions/laplace_mantissa_unpack.sql.in"
CREATE FUNCTION @extschema@.laplace_mantissa_unpack(vertex geometry,
        OUT entity_id bytea,
        OUT ordinal integer,
        OUT run_length integer,
        OUT flags bigint)
    AS 'MODULE_PATHNAME', 'pg_laplace_mantissa_unpack'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 8 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql\\functions/laplace_trajectory_constituents.sql.in"
CREATE FUNCTION @extschema@.laplace_trajectory_constituents(traj geometry,
        OUT ordinal integer,
        OUT entity_id bytea,
        OUT run_length integer,
        OUT flags bigint)
    RETURNS SETOF record
    AS 'MODULE_PATHNAME', 'pg_laplace_trajectory_constituents'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE ROWS 16;
#line 9 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql\\functions/laplace_trajectory_constituent_ids.sql.in"
CREATE FUNCTION @extschema@.laplace_trajectory_constituent_ids(traj geometry)
    RETURNS bytea[]
    AS 'MODULE_PATHNAME', 'pg_laplace_trajectory_constituent_ids'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 10 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql\\functions/laplace_vertex_atom.sql.in"
CREATE FUNCTION @extschema@.laplace_vertex_atom(flags bigint) RETURNS integer
    AS 'MODULE_PATHNAME', 'pg_laplace_vertex_atom'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 11 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql\\functions/laplace_vertex_tier.sql.in"
CREATE FUNCTION @extschema@.laplace_vertex_tier(flags bigint) RETURNS smallint
    AS 'MODULE_PATHNAME', 'pg_laplace_vertex_tier'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 12 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql\\functions/laplace_distance_4d.sql.in"
CREATE FUNCTION @extschema@.laplace_distance_4d(a geometry, b geometry)
    RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_laplace_distance_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 13 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql\\functions/laplace_angular_distance_4d.sql.in"
CREATE FUNCTION @extschema@.laplace_angular_distance_4d(a geometry, b geometry)
    RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_laplace_angular_distance_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 14 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql\\functions/laplace_dwithin_4d.sql.in"
CREATE FUNCTION @extschema@.laplace_dwithin_4d(a geometry, b geometry, eps double precision)
    RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_laplace_dwithin_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 15 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql\\functions/laplace_centroid_4d.sql.in"
CREATE FUNCTION @extschema@.laplace_centroid_4d(g geometry)
    RETURNS geometry
    AS 'MODULE_PATHNAME', 'pg_laplace_centroid_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 16 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql\\functions/laplace_radius_origin.sql.in"
CREATE FUNCTION @extschema@.laplace_radius_origin(p geometry)
    RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_laplace_radius_origin'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 17 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql\\functions/laplace_frechet_4d.sql.in"
CREATE FUNCTION @extschema@.laplace_frechet_4d(a geometry, b geometry)
    RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_laplace_frechet_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 18 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 1 "D:/Repositories/Laplace/extension/laplace_geom/sql\\functions/laplace_hausdorff_4d.sql.in"
CREATE FUNCTION @extschema@.laplace_hausdorff_4d(a geometry, b geometry)
    RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_laplace_hausdorff_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
#line 19 "D:/Repositories/Laplace/build-win-ext/laplace_geom/sql\\generated/install_chain.sql.in"
#line 3 "D:/Repositories/Laplace/extension/laplace_geom/sql/laplace_geom.sql.in"

