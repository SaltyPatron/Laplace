-- Native physicality admission: exact source forms and one snapshot provider.
\set ECHO none
BEGIN;
CREATE FUNCTION pg_temp.descriptor_field(value bytea) RETURNS bytea
LANGUAGE SQL IMMUTABLE AS $$
SELECT CASE WHEN value IS NULL THEN int4send(-1) ELSE int4send(octet_length(value)) || value END
$$;
CREATE TEMP TABLE descriptor_atoms ON COMMIT DROP AS
SELECT * FROM converse.text_root_placements(ARRAY['A','B','AB']);
CREATE TEMP TABLE descriptor_source ON COMMIT DROP AS
SELECT p.root_id AS entity_id,
    public.laplace_hash128_blake3(p.root_id || decode('0100','hex')) AS placement_id,
    public.ST_MakePoint(p.coord[1],p.coord[2],p.coord[3],p.coord[4]) AS coord,
    public.ST_MakeLine(ARRAY[
        public.laplace_mantissa_pack(a.root_id,1,1,0),
        public.laplace_mantissa_pack(b.root_id,2,1,0)]) AS trajectory,
    public.laplace_hash128_blake3(convert_to('physicality-admission/test-source','UTF8')) AS source_id,
    public.laplace_hash128_blake3(convert_to('physicality-admission/test-unit','UTF8')) AS unit_id
FROM descriptor_atoms p CROSS JOIN descriptor_atoms a CROSS JOIN descriptor_atoms b
WHERE p.ord=3 AND a.ord=1 AND b.ord=2;
-- Exact native A/B/AB content and geometry above are the fixture's provider.
-- The second body is an explicit alternate observation, not composer output.
-- Native COPY geometry fields use EWKB Z/M flag bits. ST_AsBinary emits ISO
-- type3001/3002 for these geometries and is not the native stage wire format.
CREATE TEMP TABLE descriptor_frames ON COMMIT DROP AS
SELECT variant,
    int2send(10::smallint) || pg_temp.descriptor_field(s.placement_id) || pg_temp.descriptor_field(s.entity_id) ||
    pg_temp.descriptor_field(int2send(1::smallint)) || pg_temp.descriptor_field(public.ST_AsEWKB(c.coord,'NDR')) ||
    pg_temp.descriptor_field(public.laplace_hilbert_encode(c.coord)) ||
    pg_temp.descriptor_field(public.ST_AsEWKB(s.trajectory,'NDR')) ||
    pg_temp.descriptor_field(int4send(2)) || pg_temp.descriptor_field(NULL) ||
    pg_temp.descriptor_field(NULL) || pg_temp.descriptor_field(int8send(1000000::bigint + variant)) AS tuples
FROM descriptor_source s CROSS JOIN (VALUES(0),(1)) v(variant)
CROSS JOIN LATERAL (SELECT CASE WHEN variant=0 THEN s.coord ELSE
    public.ST_MakePoint(CASE WHEN public.ST_X(s.coord)=0 THEN 0.125 ELSE public.ST_X(s.coord)*0.9 END,
        public.ST_Y(s.coord)*0.9,public.ST_Z(s.coord)*0.9,public.ST_M(s.coord)*0.9) END AS coord) c;
-- The raw AB forms reference only floor atoms A/B. A separate ordinary
-- parent [AB,AB] has a real non-atom dependency and one stored RLE carrier.
-- Its centroid is exactly the repeated child's actual native point. Native
-- COPY encodes one stored carrier as PointZM, including a repeated logical run.
CREATE TEMP TABLE descriptor_nested_frame ON COMMIT DROP AS
SELECT int2send(10::smallint) ||
    pg_temp.descriptor_field(public.laplace_hash128_blake3(n.entity_id||decode('0100','hex'))) ||
    pg_temp.descriptor_field(n.entity_id) || pg_temp.descriptor_field(int2send(1::smallint)) ||
    pg_temp.descriptor_field(public.ST_AsEWKB(s.coord,'NDR')) ||
    pg_temp.descriptor_field(public.laplace_hilbert_encode(s.coord)) ||
    pg_temp.descriptor_field(public.ST_AsEWKB(
        public.laplace_mantissa_pack(s.entity_id,1,2,0),'NDR')) ||
    pg_temp.descriptor_field(int4send(2)) || pg_temp.descriptor_field(NULL) ||
    pg_temp.descriptor_field(NULL) || pg_temp.descriptor_field(int8send(1000000::bigint)) AS tuples
FROM descriptor_source s CROSS JOIN LATERAL
(SELECT public.laplace_hash128_merkle(0::smallint,ARRAY[s.entity_id,s.entity_id]) AS entity_id) n;
DELETE FROM laplace.physicalities WHERE id=(SELECT placement_id FROM descriptor_source);
-- A named temporary result type lets failures and positive reads use the same
-- actual SQL boundary without repeating or weakening its output contract.
CREATE TYPE pg_temp.descriptor_result AS (
    entities bytea[],physicalities bytea[],attestations bytea[],descriptor_ids bytea[],view_ids bytea[],
    floor_receipt bytea,snapshot_receipt text,source_form_count bigint,current_content_count bigint,
    missing_content_count bigint,provider_rounds integer,database_operations integer,
    reserved_peak_bytes bigint,tuple_bytes bigint,floor_index_added_bytes bigint,
    raw_logical_work bigint,stored_vertices bigint,generated_source_id bytea);
CREATE FUNCTION pg_temp.descriptor_call(raw_frames bytea,winner_frames bytea,
    source_ids bytea[],unit_ids bytea[],priors float8[],byte_grant bigint DEFAULT 268435456,
    operation_grant integer DEFAULT 32,logical_grant bigint DEFAULT 1000000)
RETURNS SETOF pg_temp.descriptor_result LANGUAGE SQL STABLE AS $$
SELECT * FROM ops.physicality_descriptor_materialize(
    ARRAY[decode('','hex')],ARRAY[raw_frames],ARRAY[decode('','hex')],
    ARRAY[decode('','hex')],ARRAY[winner_frames],ARRAY[decode('','hex')],
    source_ids,unit_ids,priors,
    1700000000000000,byte_grant,operation_grant,logical_grant)
$$;
CREATE FUNCTION pg_temp.descriptor_receipt(r pg_temp.descriptor_result) RETURNS jsonb
LANGUAGE SQL IMMUTABLE AS $$
SELECT jsonb_build_object('source_forms',(r).source_form_count,'D_count',cardinality((r).descriptor_ids),
    'V_count',cardinality((r).view_ids),'D_first',encode((r).descriptor_ids[1],'hex'),
    'D_second',encode((r).descriptor_ids[2],'hex'),'V_first',encode((r).view_ids[1],'hex'),
    'V_second',encode((r).view_ids[2],'hex'),'source',encode((r).generated_source_id,'hex'),
    'floor_bytes',octet_length((r).floor_receipt),'snapshot_prefix',left((r).snapshot_receipt,15),
    'current',(r).current_content_count,'missing',(r).missing_content_count,'rounds',(r).provider_rounds,
    'operations',(r).database_operations,'peak',(r).reserved_peak_bytes,'logical',(r).raw_logical_work,
    'tuple_bytes',(r).tuple_bytes,'E_stages',cardinality((r).entities),'P_stages',cardinality((r).physicalities),
    'A_stages',cardinality((r).attestations),'generated_A_bytes',octet_length((r).attestations[3]))
$$;
CREATE TEMP TABLE descriptor_admitted ON COMMIT DROP AS
SELECT r.* FROM descriptor_source s CROSS JOIN LATERAL pg_temp.descriptor_call(
    (SELECT string_agg(tuples,decode('','hex') ORDER BY variant) FROM descriptor_frames),
    (SELECT tuples FROM descriptor_frames WHERE variant=0),
    ARRAY[s.source_id,s.source_id],ARRAY[s.unit_id,s.unit_id],ARRAY[0.8,0.8]) r;
DO $positive$
DECLARE result pg_temp.descriptor_result; single_result pg_temp.descriptor_result; s record;
BEGIN
    SELECT * INTO STRICT result FROM descriptor_admitted;
    SELECT * INTO STRICT s FROM descriptor_source;
    IF result.source_form_count<>2 OR cardinality(result.descriptor_ids)<>2 OR cardinality(result.view_ids)<>2
       OR result.descriptor_ids[1]=result.descriptor_ids[2] OR result.view_ids[1]=result.view_ids[2]
       OR result.generated_source_id<>(SELECT root_id FROM converse.text_root_placements(ARRAY['substrate/source/PhysicalityDescriptorAdmission/v1']))
       OR octet_length(result.floor_receipt)<>16 OR result.snapshot_receipt NOT LIKE 'active-mvcc-v1;%'
       OR result.missing_content_count<>0 OR result.current_content_count<>0 OR result.provider_rounds<>0
       OR result.database_operations<>2+2*result.provider_rounds
       OR result.reserved_peak_bytes>268435456 OR result.raw_logical_work<=4
       OR result.tuple_bytes<=0 OR cardinality(result.entities)<>3 OR cardinality(result.physicalities)<>3
       OR cardinality(result.attestations)<>3 OR octet_length(result.attestations[3])=0 THEN
        RAISE EXCEPTION 'physicality admission lost source forms, tuple output, source evidence or bounded provider receipts'
            USING DETAIL=pg_temp.descriptor_receipt(result)::text;
    END IF;
    SELECT * INTO STRICT single_result FROM pg_temp.descriptor_call(
        (SELECT tuples FROM descriptor_frames WHERE variant=0),(SELECT tuples FROM descriptor_frames WHERE variant=0),
        ARRAY[s.source_id],ARRAY[s.unit_id],ARRAY[0.8]);
    IF single_result.descriptor_ids[1]<>result.descriptor_ids[1] OR single_result.view_ids[1]<>result.view_ids[1] THEN
        RAISE EXCEPTION 'batch neighbors changed the original immutable body or source-scoped view';
    END IF;
    RAISE NOTICE 'physicality admission: two raw forms, exact source mapping, floor-only zero frontier, batch/single identity and finite receipts';
END
$positive$;
CREATE TEMP TABLE descriptor_nested_admitted ON COMMIT DROP AS
SELECT r.* FROM descriptor_source s CROSS JOIN LATERAL pg_temp.descriptor_call(
    (SELECT tuples FROM descriptor_nested_frame),(SELECT tuples FROM descriptor_frames WHERE variant=0),
    ARRAY[s.source_id],ARRAY[s.unit_id],ARRAY[0.8]) r;
DO $nested$
DECLARE result pg_temp.descriptor_result;
BEGIN
    SELECT * INTO STRICT result FROM descriptor_nested_admitted;
    IF result.source_form_count<>1 OR cardinality(result.descriptor_ids)<>1 OR cardinality(result.view_ids)<>1
       OR result.missing_content_count<>1 OR result.current_content_count<>0 OR result.provider_rounds<>1
       OR result.database_operations<>2*result.provider_rounds+2 OR result.raw_logical_work<=4 THEN
        RAISE EXCEPTION 'non-atom carrier did not use explicit absence and its actual admitted winner'
            USING DETAIL=pg_temp.descriptor_receipt(result)::text;
    END IF;
    RAISE NOTICE 'physicality admission: non-atom RLE parent resolves one missing Content carrier from its admitted winner';
END
$nested$;
DO $refusals$
DECLARE failures integer := 0; s record; raw bytea; winner bytea;
BEGIN
    SELECT * INTO STRICT s FROM descriptor_source;
    SELECT string_agg(tuples,decode('','hex') ORDER BY variant) INTO raw FROM descriptor_frames;
    SELECT tuples INTO winner FROM descriptor_frames WHERE variant=0;
    BEGIN PERFORM * FROM pg_temp.descriptor_call(raw,winner,ARRAY[s.source_id],ARRAY[s.unit_id],ARRAY[0.8]);
      EXCEPTION WHEN invalid_parameter_value THEN failures:=failures+1; END;
    BEGIN PERFORM * FROM pg_temp.descriptor_call(raw,winner,ARRAY[s.source_id,s.source_id],ARRAY[s.unit_id,s.unit_id],ARRAY[0.8,'NaN'::float8]);
      EXCEPTION WHEN invalid_parameter_value THEN failures:=failures+1; END;
    BEGIN PERFORM * FROM pg_temp.descriptor_call(raw,winner,ARRAY[s.source_id,s.source_id],ARRAY[s.unit_id,s.unit_id],ARRAY[0.8,1.1]);
      EXCEPTION WHEN invalid_parameter_value THEN failures:=failures+1; END;
    BEGIN PERFORM * FROM pg_temp.descriptor_call(raw,winner,ARRAY[NULL::bytea,s.source_id],ARRAY[s.unit_id,s.unit_id],ARRAY[0.8,0.8]);
      EXCEPTION WHEN invalid_parameter_value THEN failures:=failures+1; END;
    BEGIN PERFORM * FROM pg_temp.descriptor_call(raw || decode('00','hex'),winner,ARRAY[s.source_id,s.source_id],ARRAY[s.unit_id,s.unit_id],ARRAY[0.8,0.8]);
      EXCEPTION WHEN invalid_parameter_value THEN failures:=failures+1; END;
    BEGIN PERFORM * FROM pg_temp.descriptor_call(raw,winner,ARRAY[s.source_id,s.source_id],ARRAY[s.unit_id,s.unit_id],ARRAY[0.8,0.8],1024);
      EXCEPTION WHEN program_limit_exceeded THEN failures:=failures+1; END;
    BEGIN PERFORM * FROM pg_temp.descriptor_call(raw,winner,ARRAY[s.source_id,s.source_id],ARRAY[s.unit_id,s.unit_id],ARRAY[0.8,0.8],268435456,1);
      EXCEPTION WHEN program_limit_exceeded THEN failures:=failures+1; END;
    BEGIN PERFORM * FROM pg_temp.descriptor_call(raw,winner,ARRAY[s.source_id,s.source_id],ARRAY[s.unit_id,s.unit_id],ARRAY[0.8,0.8],268435456,32,1);
      EXCEPTION WHEN program_limit_exceeded THEN failures:=failures+1; END;
    BEGIN PERFORM * FROM pg_temp.descriptor_call((SELECT tuples FROM descriptor_nested_frame),decode('','hex'),
        ARRAY[s.source_id],ARRAY[s.unit_id],ARRAY[0.8]);
      EXCEPTION WHEN invalid_parameter_value THEN failures:=failures+1; END;
    IF failures<>9 THEN RAISE EXCEPTION 'physicality admission accepted invalid transport/provider/resource inputs: %',failures; END IF;
    RAISE NOTICE 'physicality admission: nine malformed-source, prior, absent-provider and finite-grant controls refused';
END
$refusals$;
INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,n_constituents,observed_at)
SELECT placement_id,entity_id,1,coord,public.laplace_hilbert_encode(coord),trajectory,2,
    '2000-01-01 00:00:01+00'::timestamptz FROM descriptor_source;
DO $current$
DECLARE result pg_temp.descriptor_result; s record;
BEGIN
    SELECT * INTO STRICT s FROM descriptor_source;
    SELECT * INTO STRICT result FROM pg_temp.descriptor_call(
        (SELECT tuples FROM descriptor_nested_frame),decode('','hex'),
        ARRAY[s.source_id],ARRAY[s.unit_id],ARRAY[0.8]);
    IF result.current_content_count<>1 OR result.missing_content_count<>0 OR result.provider_rounds<>1
       OR result.descriptor_ids<>(SELECT descriptor_ids FROM descriptor_nested_admitted)
       OR result.view_ids<>(SELECT view_ids FROM descriptor_nested_admitted)
       OR result.database_operations<>2+2*result.provider_rounds THEN
        RAISE EXCEPTION 'actual current Content provider changed exact original body or lost batched snapshot receipts'
            USING DETAIL=pg_temp.descriptor_receipt(result)::text;
    END IF;
    RAISE NOTICE 'physicality admission: actual current Content set resolves without an admitted fallback';
END
$current$;
ROLLBACK;
