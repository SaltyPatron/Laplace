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
-- The realized AB entity is an explicit declaration from the actual native
-- text owner. Its Content P is deliberately absent during descriptor reads.
-- The ordinary source label above is emitted in the generated source stage.
DO $subject$
BEGIN
    IF (SELECT tier FROM physicality_readback_fixture.atoms WHERE ord=3)<>2 THEN
        RAISE EXCEPTION 'native AB fixture must have the ordinary Word tier';
    END IF;
END
$subject$;
INSERT INTO laplace.entities(id,tier,type_id,first_observed_by)
SELECT p.root_id,p.tier,laplace.entity_type_id('Word'),s.source_id
FROM physicality_readback_fixture.atoms p CROSS JOIN physicality_readback_fixture.source s WHERE p.ord=3
ON CONFLICT DO NOTHING;
-- Exact native A/B/AB content and geometry above are the fixture's provider.
-- The second body is an explicit alternate observation, not composer output.
-- Native COPY geometry fields use EWKB Z/M flag bits. ST_AsBinary emits ISO
-- type3001/3002 for these geometries and is not the native stage wire format.
CREATE TABLE physicality_readback_fixture.frames AS
SELECT variant,
    int2send(10::smallint) || pg_temp.descriptor_field(s.placement_id) || pg_temp.descriptor_field(s.entity_id) ||
    pg_temp.descriptor_field(int2send(1::smallint)) || pg_temp.descriptor_field(public.ST_AsEWKB(c.coord,'NDR')) ||
    pg_temp.descriptor_field(public.laplace_hilbert_encode(c.coord)) ||
    pg_temp.descriptor_field(public.ST_AsEWKB(s.trajectory,'NDR')) ||
    pg_temp.descriptor_field(int4send(2)) || pg_temp.descriptor_field(CASE WHEN variant=1 THEN float8send('-0'::float8) END) ||
    pg_temp.descriptor_field(CASE WHEN variant=1 THEN int4send(7) END) || pg_temp.descriptor_field(int8send(1000000::bigint + variant)) AS tuples
FROM physicality_readback_fixture.source s CROSS JOIN (VALUES(0),(1)) v(variant)
CROSS JOIN LATERAL (SELECT CASE WHEN variant=0 THEN s.coord ELSE
    public.ST_MakePoint(CASE WHEN public.ST_X(s.coord)=0 THEN 0.125 ELSE public.ST_X(s.coord)*0.9 END,
        public.ST_Y(s.coord)*0.9,public.ST_Z(s.coord)*0.9,public.ST_M(s.coord)*0.9) END AS coord) c;
DELETE FROM laplace.physicalities WHERE id=(SELECT placement_id FROM physicality_readback_fixture.source);
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
CREATE TABLE physicality_readback_fixture.admitted AS
SELECT r.* FROM physicality_readback_fixture.source s CROSS JOIN LATERAL pg_temp.descriptor_call(
    (SELECT string_agg(tuples,decode('','hex') ORDER BY variant) FROM physicality_readback_fixture.frames),
    (SELECT tuples FROM physicality_readback_fixture.frames WHERE variant=0),
    ARRAY[s.source_id,s.source_id],ARRAY[s.unit_id,s.unit_id],ARRAY[0.8,0.8]) r;
-- The fixture adapter imports and deposits actual emitted native stage tuples
-- through the same generated-stage sink as server-owned derivations. Resolve
-- its versioned library from the installed materializer, never an ambient .so.
DO $adapter$
DECLARE library text;
BEGIN
    SELECT p.probin INTO STRICT library FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
    WHERE n.nspname='ops' AND p.proname='physicality_descriptor_materialize';
    EXECUTE format('CREATE FUNCTION pg_temp.deposit_generated(bytea[],bytea[],bytea[],bigint,bigint,bigint,integer) '
        'RETURNS bigint[] AS %L,%L LANGUAGE C STRICT',library,'pg_laplace_generated_stage_sink_test');
END
$adapter$;
DO $deposit$
DECLARE a record; receipt bigint[]; replay bigint[];
    counts_before bigint[]; counts_after bigint[]; consensus_before jsonb; consensus_after jsonb;
BEGIN
    SELECT * INTO STRICT a FROM physicality_readback_fixture.admitted;
    receipt:=pg_temp.deposit_generated(a.entities,a.physicalities,a.attestations,100000,268435456,10000000,1024);
    IF receipt[2]<=0 OR receipt[11]>268435456 OR receipt[12]>1024 THEN
        RAISE EXCEPTION 'real generated-stage deposition receipt is incomplete';
    END IF;
    counts_before:=ARRAY[(SELECT count(*) FROM laplace.entities),
        (SELECT count(*) FROM laplace.physicalities),(SELECT count(*) FROM laplace.attestations)];
    SELECT coalesce(jsonb_agg(to_jsonb(c) ORDER BY c.id,c.type_id,c.subject_id),'[]'::jsonb)
      INTO consensus_before FROM laplace.consensus c;
    replay:=pg_temp.deposit_generated(a.entities,a.physicalities,a.attestations,100000,268435456,10000000,1024);
    counts_after:=ARRAY[(SELECT count(*) FROM laplace.entities),
        (SELECT count(*) FROM laplace.physicalities),(SELECT count(*) FROM laplace.attestations)];
    SELECT coalesce(jsonb_agg(to_jsonb(c) ORDER BY c.id,c.type_id,c.subject_id),'[]'::jsonb)
      INTO consensus_after FROM laplace.consensus c;
    IF replay[1:7]<>ARRAY[0,0,0,0,0,0,0]::bigint[] OR counts_after<>counts_before
       OR consensus_after IS DISTINCT FROM consensus_before THEN
        RAISE EXCEPTION 'native generated-stage replay changed retained rows or consensus';
    END IF;
    RAISE NOTICE 'physicality readback: actual native stage deposition and replay preserve E/P/A and exact consensus';
END
$deposit$;
DO $rollback$
DECLARE s record; fresh record; unit bytea; counts_before bigint[]; counts_after bigint[];
    consensus_before jsonb; consensus_after jsonb; refused boolean:=false; message text;
BEGIN
    SELECT * INTO STRICT s FROM physicality_readback_fixture.source;
    unit:=public.laplace_hash128_blake3(convert_to('physicality-readback/rollback-unit','UTF8'));
    SELECT * INTO STRICT fresh FROM pg_temp.descriptor_call(
        (SELECT string_agg(tuples,decode('','hex') ORDER BY variant) FROM physicality_readback_fixture.frames),
        (SELECT tuples FROM physicality_readback_fixture.frames WHERE variant=0),
        ARRAY[s.source_id,s.source_id],ARRAY[unit,unit],ARRAY[0.8,0.8]);
    counts_before:=ARRAY[(SELECT count(*) FROM laplace.entities),
        (SELECT count(*) FROM laplace.physicalities),(SELECT count(*) FROM laplace.attestations)];
    SELECT coalesce(jsonb_agg(to_jsonb(c) ORDER BY c.id,c.type_id,c.subject_id),'[]'::jsonb)
      INTO consensus_before FROM laplace.consensus c;
    -- All sink plans were warmed by the actual successful write above. The
    -- warm path is lock, presence, epoch, E, P, A, fold, masks. Grant six thus
    -- fails after the new A INSERT (or after fold if E was already retained).
    BEGIN
        PERFORM pg_temp.deposit_generated(fresh.entities,fresh.physicalities,fresh.attestations,
            100000,268435456,10000000,6);
    EXCEPTION WHEN program_limit_exceeded THEN
        GET STACKED DIAGNOSTICS message=MESSAGE_TEXT;
        IF message<>'generated stage sink: database operation grant exhausted' THEN RAISE; END IF;
        refused:=true;
    END;
    counts_after:=ARRAY[(SELECT count(*) FROM laplace.entities),
        (SELECT count(*) FROM laplace.physicalities),(SELECT count(*) FROM laplace.attestations)];
    SELECT coalesce(jsonb_agg(to_jsonb(c) ORDER BY c.id,c.type_id,c.subject_id),'[]'::jsonb)
      INTO consensus_after FROM laplace.consensus c;
    IF NOT refused OR counts_after<>counts_before OR consensus_after IS DISTINCT FROM consensus_before THEN
        RAISE EXCEPTION 'post-insert operation refusal did not roll back the generated evidence and fold';
    END IF;
    RAISE NOTICE 'physicality readback: exhausted post-insert sink grant rolls back E/P/A and exact consensus';
END
$rollback$;
COMMIT;
-- Deposition requires the ordinary READ COMMITTED writer boundary. Paging and
-- its subsequent typed reads now use one stable snapshot in a new transaction.
BEGIN ISOLATION LEVEL REPEATABLE READ;
CREATE TABLE physicality_readback_fixture.warm AS
SELECT r.* FROM physicality_readback_fixture.admitted a CROSS JOIN LATERAL
structural.physicality_descriptor_read(ARRAY[a.descriptor_ids[2],a.descriptor_ids[1],a.descriptor_ids[2]],
    268435456,2048,10000000) r;
DO $exact$
DECLARE r record; a record; s record; trajectory bytea;
BEGIN
    SELECT * INTO STRICT r FROM physicality_readback_fixture.warm;
    SELECT * INTO STRICT a FROM physicality_readback_fixture.admitted;
    SELECT * INTO STRICT s FROM physicality_readback_fixture.source;
    trajectory:=substring(public.ST_AsEWKB(s.trajectory,'NDR') FROM 10);
    IF r.descriptor_ids<>ARRAY[a.descriptor_ids[2],a.descriptor_ids[1],a.descriptor_ids[2]]
       OR r.entity_ids<>ARRAY[s.entity_id,s.entity_id,s.entity_id]
       OR r.physicality_types<>ARRAY[1,1,1]::smallint[] OR r.constituent_counts<>ARRAY[2,2,2]
       OR r.trajectory_bits<>ARRAY[trajectory,trajectory,trajectory]
       OR r.coordinate_bits[2]<>substring(public.ST_AsEWKB(s.coord,'NDR') FROM 6)
       OR r.hilbert_indices[2]<>public.laplace_hilbert_encode(s.coord)
       OR r.source_dimensions IS DISTINCT FROM ARRAY[7,NULL,7]
       OR r.alignment_residual_bits IS DISTINCT FROM ARRAY[decode('0000000000000080','hex'),NULL,decode('0000000000000080','hex')]
       OR r.coordinate_bits[1]<>r.coordinate_bits[3] OR r.coordinate_bits[1]=r.coordinate_bits[2]
       OR r.hydrated_nodes<=3 OR r.provider_rounds<=1 OR r.database_operations<r.provider_rounds
       OR r.reserved_peak_bytes>268435456 OR r.charged_logical_work>10000000
       OR r.floor_receipt<>a.floor_receipt OR r.snapshot_receipt NOT LIKE 'active-mvcc-v1;%'
       OR NOT r.exhausted THEN RAISE EXCEPTION 'exact body bits, duplicate/order, external leaves or receipts lost'; END IF;
    IF EXISTS(SELECT FROM laplace.physicalities WHERE entity_id=s.entity_id AND type=1) THEN
        RAISE EXCEPTION 'fixture expected external realized E to have no stored Content body';
    END IF;
    RAISE NOTICE 'physicality readback: exact native bodies, null/negative-zero bits, root order/duplicates and bounded typed closure';
END
$exact$;
-- An actual ordinary native two-child composition contains schema and E but
-- is not a nine-field descriptor. This is an indexed false-positive fixture,
-- not a fabricated descriptor or a claim of composer-derived coordinates.
CREATE TABLE physicality_readback_fixture.incidental AS
SELECT public.laplace_hash128_merkle(0::smallint,ARRAY[t.root_id,s.entity_id]) AS id,
       t.root_id AS schema_id,s.entity_id,
       public.ST_MakeLine(ARRAY[public.laplace_mantissa_pack(t.root_id,1,1,0),
                               public.laplace_mantissa_pack(s.entity_id,2,1,0)]) AS trajectory
FROM physicality_readback_fixture.source s CROSS JOIN
converse.text_root_placements(ARRAY['PhysicalityDescriptorV1']) t;
INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,n_constituents)
SELECT public.laplace_hash128_blake3(id||decode('0100','hex')),id,1,public.ST_MakePoint(0,0,0,0),
       public.laplace_hilbert_encode(public.ST_MakePoint(0,0,0,0)),trajectory,2
FROM physicality_readback_fixture.incidental;
DO $pages$
DECLARE cursor bytea:=decode('','hex'); r record; a record; s record; seen bytea[]:='{}'; rejected bigint:=0; pages integer:=0;
BEGIN
    SELECT * INTO STRICT a FROM physicality_readback_fixture.admitted;
    SELECT * INTO STRICT s FROM physicality_readback_fixture.source;
    LOOP
        SELECT * INTO STRICT r FROM structural.physicality_forms(ARRAY[s.entity_id],cursor,1,268435456,2048,10000000);
        pages:=pages+1;rejected:=rejected+r.rejected_candidates;seen:=seen||r.descriptor_ids;
        IF r.candidates>1 OR (NOT r.exhausted AND r.next_cursor<=cursor) THEN
            RAISE EXCEPTION 'candidate cursor did not advance strictly'; END IF;
        cursor:=r.next_cursor;EXIT WHEN r.exhausted;
        IF pages>3 THEN RAISE EXCEPTION 'fixture cursor repeated an already consumed candidate'; END IF;
    END LOOP;
    IF pages<>3 OR rejected<>1 OR cardinality(seen)<>2 OR NOT(seen @> a.descriptor_ids AND seen <@ a.descriptor_ids) THEN
        RAISE EXCEPTION 'form discovery lost a form or returned incidental containment'; END IF;
    SELECT * INTO STRICT r FROM structural.physicality_forms(ARRAY[s.entity_id],cursor,1,268435456,2048,10000000);
    IF NOT r.exhausted OR cardinality(r.descriptor_ids)<>0 OR r.next_cursor<>cursor THEN
        RAISE EXCEPTION 'exhausted continuation changed cursor or returned rows'; END IF;
    RAISE NOTICE 'physicality readback: complete three-page indexed candidate traversal, native false-positive rejection and empty continuation';
END
$pages$;
DO $refusals$
DECLARE a record; s record; failures integer:=0; typed_child bytea; removed bigint;
BEGIN
    SELECT * INTO STRICT a FROM physicality_readback_fixture.admitted;
    SELECT * INTO STRICT s FROM physicality_readback_fixture.source;
    BEGIN PERFORM * FROM structural.physicality_descriptor_read(a.descriptor_ids,64,2048,10000000);
      EXCEPTION WHEN program_limit_exceeded THEN failures:=failures+1; END;
    BEGIN PERFORM * FROM structural.physicality_descriptor_read(a.descriptor_ids,268435456,2048,1);
      EXCEPTION WHEN program_limit_exceeded THEN failures:=failures+1; END;
    BEGIN PERFORM * FROM structural.physicality_descriptor_read(a.descriptor_ids,268435456,1,10000000);
      EXCEPTION WHEN program_limit_exceeded THEN failures:=failures+1; END;
    BEGIN PERFORM * FROM structural.physicality_descriptor_read(ARRAY[s.entity_id],268435456,2048,10000000);
      EXCEPTION WHEN data_corrupted THEN failures:=failures+1; END;
    BEGIN PERFORM * FROM structural.physicality_descriptor_read(ARRAY[(SELECT id FROM physicality_readback_fixture.incidental)],268435456,2048,10000000);
      EXCEPTION WHEN data_corrupted THEN failures:=failures+1; END;
    BEGIN PERFORM * FROM structural.physicality_forms(ARRAY[s.entity_id],decode('01','hex'),1,268435456,2048,10000000);
      EXCEPTION WHEN invalid_parameter_value THEN failures:=failures+1; END;
    SELECT child_id INTO STRICT typed_child FROM structural.content_carrier_vertices(
        ARRAY[a.descriptor_ids[1]],ARRAY[public.laplace_hash128_blake3(a.descriptor_ids[1]||decode('0100','hex'))])
        WHERE ordinal=3;
    BEGIN
        DELETE FROM laplace.physicalities WHERE id=public.laplace_hash128_blake3(typed_child||decode('0100','hex'));
        GET DIAGNOSTICS removed=ROW_COUNT;
        IF removed<>1 THEN RAISE EXCEPTION 'required typed child fixture was not retained'; END IF;
        PERFORM * FROM structural.physicality_descriptor_read(a.descriptor_ids,268435456,2048,10000000);
    EXCEPTION WHEN data_corrupted THEN failures:=failures+1; END;
    IF failures<>7 THEN RAISE EXCEPTION 'expected seven finite/corrupt/scope refusals, got %',failures; END IF;
    -- The refusal subtransaction restores the required field node exactly.
    PERFORM * FROM structural.physicality_descriptor_read(a.descriptor_ids,268435456,2048,10000000);
    RAISE NOTICE 'physicality readback: seven byte/work/operation, missing-root/typed-child, false-descriptor and malformed-cursor refusals';
END
$refusals$;
-- pg_regress launches the following cold fixture in a new backend.
COMMIT;
