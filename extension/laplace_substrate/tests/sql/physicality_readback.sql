-- Exact descriptor body readback and indexed form cursor acceptance.
\set ECHO none
BEGIN ISOLATION LEVEL READ COMMITTED;
CREATE SCHEMA physicality_readback_fixture;
CREATE FUNCTION pg_temp.descriptor_field(value bytea) RETURNS bytea
LANGUAGE SQL IMMUTABLE AS $$
SELECT CASE WHEN value IS NULL THEN int4send(-1) ELSE int4send(octet_length(value)) || value END
$$;
CREATE TABLE physicality_readback_fixture.atoms AS
SELECT * FROM converse.text_root_placements(ARRAY['A','B','AB','substrate/source/PhysicalityDescriptorAdmission/v1']);
CREATE TABLE physicality_readback_fixture.source AS
SELECT p.root_id AS entity_id,
    public.laplace_hash128_blake3(p.root_id || decode('0100','hex')) AS placement_id,
    public.ST_MakePoint(p.coord[1],p.coord[2],p.coord[3],p.coord[4]) AS coord,
    public.ST_MakeLine(ARRAY[
        public.laplace_mantissa_pack(a.root_id,1,1,0),
        public.laplace_mantissa_pack(b.root_id,2,1,0)]) AS trajectory,
    source.root_id AS source_id,
    public.laplace_hash128_blake3(convert_to('physicality-admission/test-unit','UTF8')) AS unit_id
FROM physicality_readback_fixture.atoms p CROSS JOIN physicality_readback_fixture.atoms a CROSS JOIN physicality_readback_fixture.atoms b
CROSS JOIN physicality_readback_fixture.atoms source
WHERE p.ord=3 AND a.ord=1 AND b.ord=2 AND source.ord=4;
DO $subject$
BEGIN
    IF (SELECT tier FROM physicality_readback_fixture.atoms WHERE ord=3)<>2 THEN
        RAISE EXCEPTION 'native AB fixture must have the ordinary Word tier';
    END IF;
END
$subject$;
-- Entities are content rows carrying their tier/type projection directly.
INSERT INTO laplace.entities(id,tier,type_id)
SELECT p.root_id,p.tier,laplace.entity_type_id('Word')
FROM physicality_readback_fixture.atoms p WHERE p.ord=3
ON CONFLICT DO NOTHING;
CREATE TEMP TABLE descriptor_floor ON COMMIT DROP AS
SELECT * FROM converse.text_root_placements(
    ARRAY(SELECT chr(cp) FROM generate_series(33,126) cp ORDER BY cp));
DO $floor$
BEGIN
    IF (SELECT count(*) FROM descriptor_floor)<>94
       OR EXISTS(SELECT FROM descriptor_floor WHERE tier<>0) THEN
        RAISE EXCEPTION 'fixture basis did not resolve to actual native floor atoms';
    END IF;
END
$floor$;
INSERT INTO laplace.entities(id,tier,type_id)
SELECT root_id,0,laplace.entity_type_id('Codepoint')
FROM descriptor_floor
ON CONFLICT DO NOTHING;
-- Stage the descriptor's typed physicalities directly: this is the same
-- committed shape the native owner produces, read back through the real
-- boundary without the deleted admission transport.
DELETE FROM laplace.physicalities WHERE id=(SELECT placement_id FROM physicality_readback_fixture.source);
INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,radius_origin,n_constituents,alignment_residual,source_dim,observed_at)
SELECT s.placement_id,s.entity_id,9,c.coord,
       public.laplace_hilbert_encode(c.coord),s.trajectory,3,2,float8send(0.5),4,
       '2024-01-01T00:00:00Z'::timestamptz
FROM physicality_readback_fixture.source s
CROSS JOIN LATERAL (SELECT s.coord AS coord) c;
-- Warm read: exact body fields of the staged descriptor through the real
-- readback owner.
CREATE TABLE physicality_readback_fixture.warm AS
SELECT * FROM structural.physicality_descriptor_read(
    ARRAY[(SELECT placement_id FROM physicality_readback_fixture.source)],268435456,2048,10000000);
DO $warm$
DECLARE w record; s record;
BEGIN
    SELECT * INTO STRICT w FROM physicality_readback_fixture.warm;
    SELECT * INTO STRICT s FROM physicality_readback_fixture.source;
    IF array_length(w.descriptor_ids,1)<>1
       OR w.descriptor_ids<>ARRAY[s.placement_id]
       OR w.entity_ids<>ARRAY[s.entity_id]
       OR w.physicality_types<>ARRAY[9::smallint]
       OR w.constituent_counts<>ARRAY[2]
       OR w.source_dimensions<>ARRAY[4]
       OR NOT w.exhausted THEN
        RAISE EXCEPTION 'warm descriptor readback lost exact body fields';
    END IF;
    RAISE NOTICE 'physicality readback: exact typed descriptor body read through the real owner';
END
$warm$;
-- Indexed form discovery over the staged body.
DO $forms$
DECLARE f record; s record;
BEGIN
    SELECT * INTO STRICT s FROM physicality_readback_fixture.source;
    SELECT * INTO STRICT f FROM structural.physicality_forms(
        ARRAY[s.entity_id],decode('','hex'),10,268435456,2048,10000000);
    IF f.descriptor_ids IS DISTINCT FROM ARRAY[s.placement_id] OR NOT f.exhausted THEN
        RAISE EXCEPTION 'indexed form discovery lost the staged descriptor';
    END IF;
    RAISE NOTICE 'physicality readback: indexed form cursor discovers the exact staged body';
END
$forms$;
-- Transaction boundary: removing the retained P and re-reading inside a
-- subtransaction, then aborting, must restore exact committed state.
DO $rollback$
DECLARE s record; removed bigint; rolled_back boolean:=false;
BEGIN
    SELECT * INTO STRICT s FROM physicality_readback_fixture.source;
    BEGIN
        DELETE FROM laplace.physicalities WHERE id=s.placement_id;
        GET DIAGNOSTICS removed=ROW_COUNT;
        IF removed<>1 THEN
            RAISE EXCEPTION 'rollback fixture did not remove exactly one descriptor physicality';
        END IF;
        RAISE SQLSTATE 'ZX002' USING MESSAGE='abort after descriptor delete';
    EXCEPTION WHEN SQLSTATE 'ZX002' THEN
        rolled_back:=true;
    END;
    IF NOT rolled_back OR NOT EXISTS(
        SELECT FROM laplace.physicalities WHERE id=s.placement_id) THEN
        RAISE EXCEPTION 'post-delete transaction abort did not restore descriptor state exactly';
    END IF;
    RAISE NOTICE 'physicality readback: transaction abort restores exact committed descriptor state';
END
$rollback$;
-- A declared typed observation may reference an opaque named anchor with no
-- Content placement, as ParseStructure does.
CREATE TABLE physicality_readback_fixture.unavailable_source AS
SELECT public.laplace_hash128_merkle(4::smallint,ARRAY[n.anchor_id,s.entity_id]) AS entity_id,
       n.anchor_id,s.entity_id AS ab_id,s.coord,
       public.ST_MakeLine(ARRAY[public.laplace_mantissa_pack(n.anchor_id,1,1,0),
                               public.laplace_mantissa_pack(s.entity_id,2,1,0)]) AS trajectory,
       s.source_id,public.laplace_hash128_blake3(convert_to('physicality-readback/opaque-unit/v1','UTF8')) AS unit_id
FROM physicality_readback_fixture.source s CROSS JOIN LATERAL
    (SELECT public.laplace_hash128_blake3(convert_to('physicality-readback/opaque-anchor/v1','UTF8')) AS anchor_id) n;
INSERT INTO laplace.entities(id,tier,type_id)
VALUES ((SELECT entity_id FROM physicality_readback_fixture.unavailable_source),4,
        laplace.entity_type_id('Document'))
ON CONFLICT DO NOTHING;
INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,radius_origin,n_constituents,alignment_residual,source_dim,observed_at)
SELECT public.laplace_hash128_blake3(u.entity_id || u.unit_id),u.entity_id,9,u.coord,
       public.laplace_hilbert_encode(u.coord),u.trajectory,3,2,float8send(0.5),4,
       '2024-01-01T00:00:00Z'::timestamptz
FROM physicality_readback_fixture.unavailable_source u;
CREATE TABLE physicality_readback_fixture.unavailable_warm AS
SELECT * FROM structural.physicality_descriptor_read(
    ARRAY[public.laplace_hash128_blake3(u.entity_id || u.unit_id)
          FROM physicality_readback_fixture.unavailable_source u],268435456,2048,10000000);
-- A legacy Content-placement fallback body for the same anchor entity.
CREATE TABLE physicality_readback_fixture.legacy_frames AS
SELECT u.entity_id, u.anchor_id AS node0, u.ab_id AS node1,
       ARRAY[u.anchor_id,u.ab_id] AS node_ids
FROM physicality_readback_fixture.unavailable_source u;
CREATE TABLE physicality_readback_fixture.legacy_warm AS
SELECT * FROM structural.physicality_descriptor_read(
    ARRAY[(SELECT entity_id FROM physicality_readback_fixture.legacy_frames)],268435456,2048,10000000);
ROLLBACK;
