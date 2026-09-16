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
-- A loaded floor resolves canonical atoms but does not prove their E rows are
-- persisted in this fresh regression database. Declare this fixture's bounded
-- printable-ASCII basis through the actual native floor owner; generated source
-- and vocabulary stages intentionally do not redeclare tier-zero entities.
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
INSERT INTO laplace.entities(id,tier,type_id,first_observed_by)
SELECT root_id,0,laplace.entity_type_id('Codepoint'),
       realize.canonical_id('substrate/source/UnicodeDecomposer/v1')
FROM descriptor_floor
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
    raw_logical_work bigint,stored_vertices bigint,generated_source_id bytea,
    view_states smallint[],view_missing_first bigint[],view_missing_count bigint[],view_missing_ids bytea[]);
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
    refused boolean:=false; message text;
BEGIN
    SELECT * INTO STRICT a FROM physicality_readback_fixture.admitted;
    -- The floor cache must not silently substitute for a missing persisted E.
    -- Subtransaction rollback restores A before the successful deposit below.
    BEGIN
        DELETE FROM laplace.entities
        WHERE id=(SELECT root_id FROM physicality_readback_fixture.atoms WHERE ord=1);
        PERFORM pg_temp.deposit_generated(a.entities,a.physicalities,a.attestations,
            100000,268435456,10000000,1024);
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS message=MESSAGE_TEXT;
        IF message<>'generated stage sink: referenced entity is not admitted' THEN RAISE; END IF;
        refused:=true;
    END;
    IF NOT refused THEN RAISE EXCEPTION 'sink accepted a floor reference without its persisted entity'; END IF;
    RAISE NOTICE 'physicality readback: loaded floor does not replace a missing persisted entity';
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
-- A declared typed observation may reference an opaque named anchor with no
-- Content placement, as ParseStructure does. The coordinate below is the
-- actual AB placement used as this fixture's observed sentence location; it
-- is never assigned to the anchor. The native owner constructs D retention.
CREATE TABLE physicality_readback_fixture.unavailable_source AS
SELECT public.laplace_hash128_merkle(4::smallint,ARRAY[n.anchor_id,s.entity_id]) AS entity_id,
       n.anchor_id,s.entity_id AS ab_id,s.coord,
       public.ST_MakeLine(ARRAY[public.laplace_mantissa_pack(n.anchor_id,1,1,0),
                               public.laplace_mantissa_pack(s.entity_id,2,1,0)]) AS trajectory,
       s.source_id,public.laplace_hash128_blake3(convert_to('physicality-readback/opaque-unit/v1','UTF8')) AS unit_id
FROM physicality_readback_fixture.source s CROSS JOIN LATERAL
    (SELECT public.laplace_hash128_blake3(convert_to('physicality-readback/opaque-anchor/v1','UTF8')) AS anchor_id) n;
INSERT INTO laplace.entities(id,tier,type_id,first_observed_by)
SELECT anchor_id,2,laplace.entity_type_id('UdAnnotationMarker'),source_id
FROM physicality_readback_fixture.unavailable_source
UNION ALL SELECT entity_id,4,laplace.entity_type_id('UdParse'),source_id
FROM physicality_readback_fixture.unavailable_source;
CREATE TABLE physicality_readback_fixture.unavailable_admitted AS
SELECT r.* FROM physicality_readback_fixture.unavailable_source s CROSS JOIN LATERAL
pg_temp.descriptor_call(
    int2send(10::smallint) ||
    pg_temp.descriptor_field(public.laplace_hash128_blake3(s.entity_id||decode('0800','hex'))) ||
    pg_temp.descriptor_field(s.entity_id) || pg_temp.descriptor_field(int2send(8::smallint)) ||
    pg_temp.descriptor_field(public.ST_AsEWKB(s.coord,'NDR')) ||
    pg_temp.descriptor_field(public.laplace_hilbert_encode(s.coord)) ||
    pg_temp.descriptor_field(public.ST_AsEWKB(s.trajectory,'NDR')) || pg_temp.descriptor_field(int4send(2)) ||
    pg_temp.descriptor_field(NULL) || pg_temp.descriptor_field(NULL) ||
    pg_temp.descriptor_field(int8send(1000000::bigint)),
    decode('','hex'),ARRAY[s.source_id],ARRAY[s.unit_id],ARRAY[0.8]) r;
DO $unavailable_deposit$
DECLARE a record; s record; receipt bigint[]; replay bigint[]; frontier bytea[];
BEGIN
    SELECT * INTO STRICT a FROM physicality_readback_fixture.unavailable_admitted;
    SELECT * INTO STRICT s FROM physicality_readback_fixture.unavailable_source;
    SELECT array_agg(id ORDER BY id) INTO frontier FROM unnest(ARRAY[s.anchor_id,s.ab_id]) AS u(id);
    IF a.view_states IS DISTINCT FROM ARRAY[1]::smallint[]
       OR a.view_ids IS DISTINCT FROM ARRAY[NULL::bytea]
       OR a.view_missing_first IS DISTINCT FROM ARRAY[0]::bigint[]
       OR a.view_missing_count IS DISTINCT FROM ARRAY[2]::bigint[]
       OR a.view_missing_ids IS DISTINCT FROM frontier
       OR cardinality(a.descriptor_ids)<>1 THEN
        RAISE EXCEPTION 'opaque source did not retain exact descriptor and sorted missing-reference disposition';
    END IF;
    receipt:=pg_temp.deposit_generated(a.entities,a.physicalities,a.attestations,100000,268435456,10000000,1024);
    replay:=pg_temp.deposit_generated(a.entities,a.physicalities,a.attestations,100000,268435456,10000000,1024);
    IF receipt[2]<=0 OR replay[1:7] IS DISTINCT FROM ARRAY[0,0,0,0,0,0,0]::bigint[]
       OR EXISTS(SELECT FROM laplace.physicalities WHERE entity_id=ANY(frontier) AND type=1)
       OR NOT EXISTS(SELECT FROM laplace.physicalities WHERE entity_id=a.descriptor_ids[1] AND type=9) THEN
        RAISE EXCEPTION 'descriptor-only replay amplified output or invented anchor/AB Content';
    END IF;
    RAISE NOTICE 'physicality readback: opaque typed source retains native descriptor without fabricated Content or available view';
END
$unavailable_deposit$;
COMMIT;
-- Deposition requires the ordinary READ COMMITTED writer boundary. Paging and
-- its subsequent typed reads now use one stable snapshot in a new transaction.
BEGIN ISOLATION LEVEL REPEATABLE READ;
CREATE TABLE physicality_readback_fixture.warm AS
SELECT r.* FROM physicality_readback_fixture.admitted a CROSS JOIN LATERAL
structural.physicality_descriptor_read(ARRAY[a.descriptor_ids[2],a.descriptor_ids[1],a.descriptor_ids[2]],
    268435456,2048,10000000) r;
CREATE TABLE physicality_readback_fixture.unavailable_warm AS
SELECT r.* FROM physicality_readback_fixture.unavailable_admitted a CROSS JOIN LATERAL
structural.physicality_descriptor_read(a.descriptor_ids,268435456,2048,10000000) r;
DO $unavailable_read$
DECLARE r record; s record; a record; indexed record;
BEGIN
    SELECT * INTO STRICT r FROM physicality_readback_fixture.unavailable_warm;
    SELECT * INTO STRICT s FROM physicality_readback_fixture.unavailable_source;
    SELECT * INTO STRICT a FROM physicality_readback_fixture.unavailable_admitted;
    SELECT * INTO STRICT indexed FROM structural.physicality_forms(ARRAY[s.entity_id],decode('','hex'),10,268435456,2048,10000000);
    IF r.descriptor_ids IS DISTINCT FROM a.descriptor_ids
       OR r.entity_ids IS DISTINCT FROM ARRAY[s.entity_id]
       OR r.physicality_types IS DISTINCT FROM ARRAY[8]::smallint[]
       OR r.coordinate_bits IS DISTINCT FROM ARRAY[substring(public.ST_AsEWKB(s.coord,'NDR') FROM 6)]
       OR r.trajectory_bits IS DISTINCT FROM ARRAY[substring(public.ST_AsEWKB(s.trajectory,'NDR') FROM 10)]
       OR r.hilbert_indices IS DISTINCT FROM ARRAY[public.laplace_hilbert_encode(s.coord)]
       OR r.constituent_counts IS DISTINCT FROM ARRAY[2]
       OR r.alignment_residual_bits IS DISTINCT FROM ARRAY[NULL::bytea]
       OR r.source_dimensions IS DISTINCT FROM ARRAY[NULL::integer]
       OR indexed.descriptor_ids IS DISTINCT FROM a.descriptor_ids OR NOT indexed.exhausted THEN
        RAISE EXCEPTION 'unavailable view prevented exact typed descriptor readback or indexed discovery';
    END IF;
    RAISE NOTICE 'physicality readback: descriptor-only type9 direct and indexed reads preserve exact opaque body bits';
END
$unavailable_read$;
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
    -- Select one fixed field from the actual native nine-field retention root;
    -- the Content-only reader must not be used for a type9 manifest.
    SELECT u.entity_id INTO STRICT typed_child FROM laplace.physicalities p CROSS JOIN LATERAL
        public.laplace_mantissa_unpack(public.ST_PointN(p.trajectory,3)) u
        WHERE p.id=public.laplace_hash128_blake3(a.descriptor_ids[1]||decode('0900','hex'))
          AND p.type=9 AND u.ordinal=3 AND u.run_length=1;
    BEGIN
        DELETE FROM laplace.physicalities WHERE id=public.laplace_hash128_blake3(typed_child||decode('0900','hex'));
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
COMMIT;
-- The genuine historical stage uses the same normal writer isolation as the
-- fresh stages above; subsequent parity reads resume a pinned snapshot.
BEGIN ISOLATION LEVEL READ COMMITTED;
-- Genuine pre-type9 output, generated by the unchanged native owner at
-- 6020f4f1c6d5524d82220a57a9778654d8e9ef1b with library SHA256 22b3502bc97eed57c9a4ae8fe09e9503de7e14be467550376216162639d06bcf
-- and controlled ASCII floor SHA256 505458c9cfb9781ec6ebb00700f8a93d4c67062345dc77bfd20732ce69bb9a38.
-- Original captured artifact SHA256 b0d5c581d71aaea9256c65ddc7da1d4a499f815e8a0bfbd3baec8e9b73d86bee.
-- These ten unchanged Content rows are the old source capture's exact native
-- descriptor-plan closure. Their coordinates are a retained legacy observation,
-- not the current host's atom geometry. The raw A body is materialized again by
-- the current owner below; no type9 geometry is relabeled as Content.
CREATE TABLE physicality_readback_fixture.legacy_frames AS
SELECT decode('000a000000108ee336d3818c5a69edacaa36a6f25d4e0000001032684bfa28c0c84d6f210511aace0efc0000000200010000002501010000c000ab5e91e3fae43f7e8e7abd0850d2bf3a3e069f8b80e5bf263ee31dec8bc8bf00000010f30a2f3c4c51f7eff03600f250122c81ffffffff0000000400000000ffffffffffffffff00000008fffca2fec4d76240','hex') AS raw,
       decode('000400000010102847bae1957810693efa8df959938b00000002000400000010b74c73b8b8f12b8c680f96c713faa45500000010d5d07ba2f3621f64cf4f2d46798bfd52000400000010b29eba4341e0c74813bd1689cb6663f100000002000400000010b74c73b8b8f12b8c680f96c713faa45500000010d5d07ba2f3621f64cf4f2d46798bfd52000400000010b9b209d946ce849b24a24545d881ecc400000002000400000010b74c73b8b8f12b8c680f96c713faa45500000010d5d07ba2f3621f64cf4f2d46798bfd52000400000010c842b567d1934d033222fe2d95a1e2d300000002000400000010b74c73b8b8f12b8c680f96c713faa45500000010d5d07ba2f3621f64cf4f2d46798bfd520004000000104a5c6734b3c133cd738455e936c498da00000002000400000010b74c73b8b8f12b8c680f96c713faa45500000010d5d07ba2f3621f64cf4f2d46798bfd52000400000010385a649a3fbde6f3da8757b144d5b73f00000002000400000010b74c73b8b8f12b8c680f96c713faa45500000010d5d07ba2f3621f64cf4f2d46798bfd52000400000010fbf2b7c486ee51aa3174a5637af590f000000002000400000010b74c73b8b8f12b8c680f96c713faa45500000010d5d07ba2f3621f64cf4f2d46798bfd52000400000010a5bc76f79ccabb2789b91e266edc7a1900000002000400000010b74c73b8b8f12b8c680f96c713faa45500000010d5d07ba2f3621f64cf4f2d46798bfd52000400000010501897d0f216cc6e48ce6427e4af08de00000002000400000010b74c73b8b8f12b8c680f96c713faa45500000010d5d07ba2f3621f64cf4f2d46798bfd520004000000104be71e6c314f112af141920b93acffd400000002000400000010b74c73b8b8f12b8c680f96c713faa45500000010d5d07ba2f3621f64cf4f2d46798bfd52','hex') AS entities,
       decode('000a0000001052e1fef9e055d293828bf4ff58c5789700000010102847bae1957810693efa8df959938b0000000200010000002501010000c02a755138c82dc43f22c152721467cb3ff24d57180bacb73f50e86ab30ec8cebf00000010b11ccb21deb29c1fc23e7e0c1c44f0de0000006901020000c0030000008a7ca22c3735f83fe0f0a9de19bdf1bf8f9e18000000f03f010001000000f03ffea371965a64f13ff7b0dec94635f9bf6b6430000000f03f020001000000f03f7e8220c97935ffbfd76d32889b62f5bf9ca612000000f03f030001000000f03f0000000400000003ffffffffffffffff000000080002ad22dce66000000a00000010759ae9da1967df60591da269665af98e00000010b29eba4341e0c74813bd1689cb6663f10000000200010000002501010000c0005c8adcac5a91bff5462767cec4a73fa3bf4ca8a8de94bf43b9566f0e0783bf0000001071111271926eb9b8a27d77e04aa037d80000012901020000c0090000004cac139a73d7f93ff12edd5f753ffb3fde1d30000000f03f010001000000f03f7e8220c97935ffbfd76d32889b62f5bf9ca612000000f03f020001000000f03fe509bd589839f53f9e531982586efe3f4a4405000000f03f030001000000f03f288894a37443fa3f2cc5d0a5bdd0f6bf9eda12000000f03f040001000000f03f32018498c1effbbfef12628275c3fb3f1b1c02000000f03f050001000000f03f7b8a50a9020af5bfd820565deacef7bf64f921000000f03f060001000000f03f070a83ac56f0f13f563c98a040d7fb3f779022000000f03f070001000000f03f10136e5ba181f6bf6cf9ef7a586ef73f1cb91f000000f03f080001000000f03f59093dc07c3efabf1a3c92f48d13fc3f9c261b000000f03f090001000000f03f0000000400000009ffffffffffffffff000000080002ad22dce66000000a0000001037d27c7a53a44778dd344db3e0930b1000000010b9b209d946ce849b24a24545d881ecc40000000200010000002501010000c008c2b53beecac1bf41d802fdbb91ce3fea1df2d9fe2f8abffd43c9e5fd1aa43f00000010600e9a5f77759db14a47a9ab856d2c760000012901020000c0090000004cac139a73d7f93ff12edd5f753ffb3fde1d30000000f03f010001000000f03f2904d7cc4e24fc3f1fab3c40603df4bf985e1a000000f03f020001000000f03f0677a4b9d0ebf63f5411daf65ff2fdbf099200000000f03f030001000000f03f165c162bebf8f43f9969e44daf4bfb3fd3f21f000000f03f040001000000f03f92d8227d4efafa3f97ed27e685bcf5bfb2fe0c000000f03f050001000000f03f999ae42cb87cf5bf992f1b2c899df73fcba025000000f03f060001000000f03f3886a2552702ff3fd70ed8ed2f1cf23f5ead13000000f03f070001000000f03f6991afa7d4d6f0bf47e246e16c58fb3f68d419000000f03f080001000000f03ffa63b91b6c09f63f2c02f014731bfe3fb5a131000000f03f090001000000f03f0000000400000009ffffffffffffffff000000080002ad22dce66000000a00000010838cf409fe60153c3e2000f6c6640ee000000010c842b567d1934d033222fe2d95a1e2d30000000200010000002501010000c0ccf65c69d0e1d1bf4afc32371a13983f0d92640c1416a9bfa9f32a837965b5bf0000001071e9a4177faab8e78e8221871e5652410000012901020000c0090000004cac139a73d7f93ff12edd5f753ffb3fde1d30000000f03f010001000000f03fed79ce8d87c9fbbf6b4fa5c89ceef7bf642f25000000f03f020001000000f03f24085a610eacf8bf3dc34e75c85ef33f92723d000000f03f030001000000f03f6930b718fd46f63f68bb45a25dafffbf4d251a000000f03f040001000000f03f1b4b41c12b2cfc3f2ac6214a7e50f23f8c2635000000f03f050001000000f03f71ef3e8b633ef8bf4a7db1a5ad02fe3f1b5316000000f03f060001000000f03f7df798c6184ff63f2b07766e97a8f3bfd5c939000000f03f070001000000f03ffb43070d34d5febf9ee995f0949dfdbf6a5627000000f03f080001000000f03ffa63b91b6c09f63f2c02f014731bfe3fb5a131000000f03f090001000000f03f0000000400000009ffffffffffffffff000000080002ad22dce66000000a0000001016767ce524a42fb897d8eef3fcdeef5f000000104a5c6734b3c133cd738455e936c498da0000000200010000002501010000c0c6c8bd3ba516c4bf9afbad6df93ab9bf2087493bcbd8babf6821a1064a56babf000000100aad78e29160b9e0b20dd0a38687861f0000012901020000c0090000004cac139a73d7f93ff12edd5f753ffb3fde1d30000000f03f010001000000f03fb9eff7bd47a9f53fdbcea9a1c47df83f33db35000000f03f020001000000f03f24085a610eacf8bf3dc34e75c85ef33f92723d000000f03f030001000000f03f7b8a50a9020af5bfd820565deacef7bf64f921000000f03f040001000000f03f005e27a14f8afbbfeb22aa3f2612f1bf261306000000f03f050001000000f03ff26b2fb39656f33fe2295645c6d5ffbff43413000000f03f060001000000f03f71ef3e8b633ef8bf4a7db1a5ad02fe3f1b5316000000f03f070001000000f03f8c5041dfd0e5f63f01c02ee1c4fffbbfe04821000000f03f080001000000f03ffa63b91b6c09f63f2c02f014731bfe3fb5a131000000f03f090001000000f03f0000000400000009ffffffffffffffff000000080002ad22dce66000000a000000107fd302e5035f1da3ff81b9470b796eb700000010385a649a3fbde6f3da8757b144d5b73f0000000200010000002501010000c04f695d48c748c5bfec7a3487b88eb43f98742a7d698fa6bf74188dddbc2da1bf000000107116e4e78e47fa6a3b612c4314e9b56a000000a901020000c005000000eb36f03e69f7f83f9a61f41c6adef83f4abd2e000000f03f010001000000f03f13bd1689cb66f33f8b97f5d41d0af23ff83112000000f03f020001000000f03f24a24545d881fc3f27ce954dc836f2bf33e126000000f03f030001000000f03f3222fe2d95a1f23f9f4616aa3d8bfebf64d300000000f03f040001000000f03f738455e936c4f8bfd456e23aa399fd3ff04c33000000f03f050001000000f03f0000000400000005ffffffffffffffff000000080002ad22dce66000000a0000001037811642ba7a9c8f9b068b7de05d85f400000010fbf2b7c486ee51aa3174a5637af590f00000000200010000002501010000c04267c1b636a4b63ff63a71d68e06ba3fe0fb6503a660ad3f269bc5f5dbf392bf00000010b11175e2f41c16869913e4cd0a5b4a430000022901020000c011000000a929f7a2a1d0febf3e01b337ac41f13ff03431000000f03f010001000000f03ffa4a453ccc86fbbfd619d963abaef23f151f13000000f03f020001000000f03f3cb93e4e3fb2f1bf8a0d52c18969febfbbf72b000000f03f030001000000f03f7b2b4d3de112f43f7eb7df11e097fd3f0e132a000000f03f040001000000f03f2c24687807cffdbf41a9589ead55f8bf927b09000000f03f050001000000f03f98036b03c091f5bf2b17981c10befd3fe85a11000000f03f060001000000f03f8e860528c84ff33f02023ccb82d5febf61e738000000f03f070001000000f03fe6612a0a83b2f73f2971b180a1d0f4bfe85331000000f03f080001000000f03f098c9607cd5ff3bfd28a7f99a04ffbbf199f12000000f03f090001000000f03ff511208eb706f8bf0e85c084f8daf3bfed3710000000f03f0a0001000000f03f91756ff62c80f63fb3b080350878f0bf139f03000000f03f0b0001000000f03f7e8220c97935ffbfd76d32889b62f5bf9ca612000000f03f0c0001000000f03f50a1f6038ddbf5bfda78deaea6ddf03f43d311000000f03f0d0001000000f03f3886a2552702ff3fd70ed8ed2f1cf23f5ead13000000f03f0e0001000000f03f19ed26542debfdbf2a31c6ba4ed4fbbfb59c3c000000f03f0f0001000000f03f275ee2e0cb63f8bff819b61cc3eaf9bffe5619000000f03f100001000000f03fb07d2b5c8cadf4bf31cc5709411bfebfd5bb26000000f03f110001000000f03f0000000400000011ffffffffffffffff000000080002ad22dce66000000a00000010e23f13d38e6e1fa0f815b1a25d12f1a300000010a5bc76f79ccabb2789b91e266edc7a190000000200010000002501010000c002ccefa30efbc7bf37143c06fce8b93ff61c40d10c0f993f92130e0c86c283bf000000104eef78c1c2e1e8f032dbfc2bc74ddafc0000004901020000c002000000117d5263daa1f3bf9b33d61c84aef93f4e1c06000000f03f010001000000f03f3b72776a950efa3fbf96ab67dcb4fe3fd57618000000f03f020001000000f03f0000000400000002ffffffffffffffff000000080002ad22dce66000000a00000010fda220f15537bcc35273efa3c5f1896e00000010501897d0f216cc6e48ce6427e4af08de0000000200010000002501010000c0d83dc37b1193dc3f62d718b5c920bbbf3aa0fdaba745dcbf40a0cfc7aef1dcbf00000010f5c2145c66d40ec39c7e9675ec627010000000a901020000c005000000853cafda860ffd3fd36967f44a6bf5bfe22a19000000f03f010001000000f03f7e8220c97935ffbfd76d32889b62f5bf9ca612000000f03f020001000000f03f7e8220c97935ffbfd76d32889b62f5bf9ca612000000f03f030001000000f03f7e8220c97935ffbfd76d32889b62f5bf9ca612000000f03f040001000000f03f7e8220c97935ffbfd76d32889b62f5bf9ca612000000f03f050001000000f03f0000000400000005ffffffffffffffff000000080002ad22dce66000000a000000101000b076d07e1c6c24014773b9e92f3d000000104be71e6c314f112af141920b93acffd40000000200010000002501010000c02758017c2795a73fa0e692213c21913f004eef022452babfd579fc3c73b8bbbf000000108eee01b122f549c62d862fcffd26ab480000012901020000c009000000d566473b2846f33f9e69ae597c46f7bf92550b000000f03f010001000000f03f6f210511aacefe3fe097415bd247f13f307213000000f03f020001000000f03f693efa8df959f3bf5c844039d20dff3f251e04000000f03f030001000000f03fda8757b144d5f7bffdc1d122d3fcf93faff93c000000f03f040001000000f03f3174a5637af5f0bf84df97bf2536f4bf7b942a000000f03f050001000000f03f89b91e266edcfabfcb28e5b5bbe7f4bff2ee09000000f03f060001000000f03f48ce6427e4aff83ff086c2b88496f7bf05b31b000000f03f070001000000f03f3b72776a950efa3fbf96ab67dcb4fe3fd57618000000f03f080001000000f03f3b72776a950efa3fbf96ab67dcb4fe3fd57618000000f03f090001000000f03f0000000400000009ffffffffffffffff000000080002ad22dce66000','hex') AS physicalities,
       ARRAY[decode('102847bae1957810693efa8df959938b','hex'),decode('b29eba4341e0c74813bd1689cb6663f1','hex'),decode('b9b209d946ce849b24a24545d881ecc4','hex'),decode('c842b567d1934d033222fe2d95a1e2d3','hex'),decode('4a5c6734b3c133cd738455e936c498da','hex'),decode('385a649a3fbde6f3da8757b144d5b73f','hex'),decode('fbf2b7c486ee51aa3174a5637af590f0','hex'),decode('a5bc76f79ccabb2789b91e266edc7a19','hex'),decode('501897d0f216cc6e48ce6427e4af08de','hex'),decode('4be71e6c314f112af141920b93acffd4','hex')] AS node_ids,
       decode('4be71e6c314f112af141920b93acffd4','hex') AS descriptor_id,
       decode('32684bfa28c0c84d6f210511aace0efc','hex') AS entity_id,
       decode('00ab5e91e3fae43f7e8e7abd0850d2bf3a3e069f8b80e5bf263ee31dec8bc8bf','hex') AS coordinate_bits,
       decode('f30a2f3c4c51f7eff03600f250122c81','hex') AS hilbert;
CREATE TABLE physicality_readback_fixture.legacy_admitted AS
SELECT r.* FROM physicality_readback_fixture.legacy_frames f
CROSS JOIN physicality_readback_fixture.source s CROSS JOIN LATERAL
pg_temp.descriptor_call(f.raw,f.raw,ARRAY[s.source_id],ARRAY[s.unit_id],ARRAY[0.8]) r;
DO $legacy_deposit$
DECLARE a record; f record; receipt bigint[];
BEGIN
    SELECT * INTO STRICT a FROM physicality_readback_fixture.legacy_admitted;
    SELECT * INTO STRICT f FROM physicality_readback_fixture.legacy_frames;
    IF a.descriptor_ids IS DISTINCT FROM ARRAY[f.descriptor_id]
       OR a.view_states IS DISTINCT FROM ARRAY[0]::smallint[] OR a.view_ids[1] IS NULL THEN
        RAISE EXCEPTION 'current native producer changed the genuine legacy raw-body descriptor';
    END IF;
    receipt:=pg_temp.deposit_generated(a.entities,a.physicalities,a.attestations,100000,268435456,10000000,1024);
    -- Actual legacy E/P tuple bytes use the same canonical sink as fresh output.
    receipt:=pg_temp.deposit_generated(ARRAY[decode('','hex'),decode('','hex'),f.entities],
        ARRAY[decode('','hex'),decode('','hex'),f.physicalities],
        ARRAY[decode('','hex'),decode('','hex'),decode('','hex')],
        100000,268435456,10000000,1024);
    IF receipt[2]<>10 OR (SELECT count(*) FROM laplace.physicalities WHERE entity_id=ANY(f.node_ids) AND type=9)<>10 THEN
        RAISE EXCEPTION 'legacy fixture did not retain both genuine Content and current type9 closures';
    END IF;
END
$legacy_deposit$;
COMMIT;
BEGIN ISOLATION LEVEL REPEATABLE READ;
CREATE TABLE physicality_readback_fixture.legacy_warm AS
SELECT r.* FROM physicality_readback_fixture.legacy_frames f CROSS JOIN LATERAL
structural.physicality_descriptor_read(ARRAY[f.descriptor_id],268435456,2048,10000000) r;
DO $legacy_parity$
DECLARE f record; warm record; legacy record; indexed record; removed bigint;
    refused integer:=0; message text;
BEGIN
    SELECT * INTO STRICT f FROM physicality_readback_fixture.legacy_frames;
    SELECT * INTO STRICT warm FROM physicality_readback_fixture.legacy_warm;
    IF warm.entity_ids IS DISTINCT FROM ARRAY[f.entity_id]
       OR warm.physicality_types IS DISTINCT FROM ARRAY[1]::smallint[]
       OR warm.coordinate_bits IS DISTINCT FROM ARRAY[f.coordinate_bits]
       OR warm.hilbert_indices IS DISTINCT FROM ARRAY[f.hilbert]
       OR warm.trajectory_bits IS DISTINCT FROM ARRAY[NULL::bytea]
       OR warm.constituent_counts IS DISTINCT FROM ARRAY[0] THEN
        RAISE EXCEPTION 'current type9 read changed the retained legacy atomic observation';
    END IF;
    -- Both indexed forms converge on D even though two genuine typed storage
    -- rows now contain it. Neither route may duplicate that descriptor.
    SELECT * INTO STRICT indexed FROM structural.physicality_forms(ARRAY[f.entity_id],decode('','hex'),32,268435456,2048,10000000);
    IF indexed.descriptor_ids IS DISTINCT FROM ARRAY[f.descriptor_id] OR NOT indexed.exhausted THEN
        RAISE EXCEPTION 'combined legacy/type9 index duplicated or lost exact D';
    END IF;
    BEGIN
        DELETE FROM laplace.physicalities WHERE entity_id=ANY(f.node_ids) AND type=9;
        GET DIAGNOSTICS removed=ROW_COUNT;
        IF removed<>10 THEN RAISE EXCEPTION 'legacy parity did not remove exactly its type9 closure'; END IF;
        SELECT * INTO STRICT legacy FROM structural.physicality_descriptor_read(ARRAY[f.descriptor_id],268435456,2048,10000000);
        SELECT * INTO STRICT indexed FROM structural.physicality_forms(ARRAY[f.entity_id],decode('','hex'),32,268435456,2048,10000000);
        IF ROW(legacy.descriptor_ids,legacy.entity_ids,legacy.physicality_types,legacy.coordinate_bits,
               legacy.hilbert_indices,legacy.trajectory_bits,legacy.constituent_counts,
               legacy.alignment_residual_bits,legacy.source_dimensions)
           IS DISTINCT FROM ROW(warm.descriptor_ids,warm.entity_ids,warm.physicality_types,warm.coordinate_bits,
               warm.hilbert_indices,warm.trajectory_bits,warm.constituent_counts,
               warm.alignment_residual_bits,warm.source_dimensions)
           OR indexed.descriptor_ids IS DISTINCT FROM warm.descriptor_ids OR NOT indexed.exhausted THEN
            RAISE EXCEPTION 'genuine legacy type1 direct/indexed read differs from current type9';
        END IF;
        RAISE EXCEPTION USING ERRCODE='LP002',MESSAGE='restore current retention after genuine legacy parity';
    EXCEPTION WHEN SQLSTATE 'LP002' THEN NULL;
    END;
    -- A present corrupt type9 row must refuse, not fall back to good legacy1.
    BEGIN
        UPDATE laplace.physicalities SET trajectory=NULL
        WHERE id=public.laplace_hash128_blake3(f.descriptor_id||decode('0900','hex'));
        PERFORM * FROM structural.physicality_descriptor_read(ARRAY[f.descriptor_id],268435456,2048,10000000);
    EXCEPTION WHEN data_corrupted THEN
        GET STACKED DIAGNOSTICS message=MESSAGE_TEXT;
        IF message<>'typed trajectory read found a NULL required manifest' THEN RAISE; END IF;
        refused:=refused+1;
    END;
    BEGIN
        UPDATE laplace.physicalities SET type=1
        WHERE id=public.laplace_hash128_blake3(f.descriptor_id||decode('0900','hex'));
        PERFORM * FROM structural.physicality_descriptor_read(ARRAY[f.descriptor_id],268435456,2048,10000000);
    EXCEPTION WHEN data_corrupted THEN
        GET STACKED DIAGNOSTICS message=MESSAGE_TEXT;
        IF message<>'typed trajectory read found an unexpected physicality kind' THEN RAISE; END IF;
        refused:=refused+1;
    END;
    IF refused<>2 THEN RAISE EXCEPTION 'corrupt retained type9 silently fell back to a legacy body'; END IF;
    RAISE NOTICE 'physicality readback: genuine legacy Content and native type9 preserve direct/indexed body parity; corrupt type9 refuses fallback';
END
$legacy_parity$;

-- pg_regress launches the following cold fixture in a new backend.
COMMIT;
