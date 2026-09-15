-- Canonical selected carrier batches preserve stored metadata and native runs.
\set ECHO none
BEGIN;
CREATE TEMP TABLE carrier_input ON COMMIT DROP AS
SELECT i, public.laplace_hash128_blake3('carrier-test/parent/' || i) AS parent,
    public.laplace_hash128_blake3('carrier-test/child/' || i) AS child,
    public.laplace_hash128_blake3('carrier-test/second/' || i) AS second
FROM generate_series(1,100) i;
ALTER TABLE carrier_input ADD COLUMN physicality bytea;
UPDATE carrier_input SET physicality = public.laplace_hash128_blake3(parent || decode('0100','hex'));
INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,n_constituents)
SELECT physicality,parent,CASE WHEN i=98 THEN 2 ELSE 1 END,
    public.ST_MakePoint(0,0,0,0),decode(repeat('00',16),'hex'),
    CASE WHEN i=99 THEN NULL ELSE public.ST_MakeLine(ARRAY[
        public.laplace_mantissa_pack(child,17,3,4294967297),
        public.laplace_mantissa_pack(second,1,0,7)]) END,
    CASE WHEN i=1 THEN 999 ELSE 4 END
FROM carrier_input WHERE i<100;
CREATE TEMP TABLE carrier_actual ON COMMIT DROP AS
SELECT * FROM structural.content_carrier_vertices(
    (SELECT array_agg(parent ORDER BY i) FROM carrier_input),
    (SELECT array_agg(physicality ORDER BY i) FROM carrier_input));
DO $stored$
BEGIN
    IF (SELECT count(*) FROM carrier_actual) <> 194 OR EXISTS (
        SELECT 1 FROM carrier_actual a JOIN carrier_input i ON i.parent=a.parent_id
        WHERE a.n_constituents <> CASE WHEN i.i=1 THEN 999 ELSE 4 END
           OR (a.ordinal=1 AND (a.child_id<>i.child OR a.run_length<>3 OR a.flags<>4294967297))
           OR (a.ordinal=4 AND (a.child_id<>i.second OR a.run_length<>1 OR a.flags<>7))
           OR a.ordinal NOT IN (1,4) OR i.i>=98) THEN
        RAISE EXCEPTION 'carrier batch lost stored counts, flags, runs, ordered child IDs or exact type selection';
    END IF;
    IF EXISTS (SELECT parent_id FROM carrier_actual GROUP BY parent_id HAVING count(*)<>2) THEN
        RAISE EXCEPTION 'carrier batch has duplicate or missing vertices';
    END IF;
    -- The scalar surface now delegates to the same core visitor; compare every
    -- field independently for all selected physical geometries.
    IF EXISTS (
        (SELECT a.parent_id,a.ordinal,a.child_id,a.run_length,a.flags FROM carrier_actual a
         EXCEPT SELECT p.entity_id,c.ordinal::bigint,c.entity_id,c.run_length::bigint,c.flags
         FROM laplace.physicalities p JOIN carrier_input i ON p.id=i.physicality
         CROSS JOIN LATERAL public.laplace_trajectory_constituents(p.trajectory) c
         WHERE p.type=1 AND p.trajectory IS NOT NULL)
        UNION ALL
        (SELECT p.entity_id,c.ordinal::bigint,c.entity_id,c.run_length::bigint,c.flags
         FROM laplace.physicalities p JOIN carrier_input i ON p.id=i.physicality
         CROSS JOIN LATERAL public.laplace_trajectory_constituents(p.trajectory) c
         WHERE p.type=1 AND p.trajectory IS NOT NULL
         EXCEPT SELECT a.parent_id,a.ordinal,a.child_id,a.run_length,a.flags FROM carrier_actual a)) THEN
        RAISE EXCEPTION 'native scalar and batch carrier surfaces differ';
    END IF;
    RAISE NOTICE 'content carrier batch: exact selected metadata, logical ordinals, runs, flags and scalar parity';
END
$stored$;
DO $inputs$
DECLARE
    entity bytea;
    placement bytea;
    failures integer := 0;
BEGIN
    SELECT parent,physicality INTO entity,placement FROM carrier_input WHERE i=2;
    IF EXISTS (SELECT 1 FROM structural.content_carrier_vertices('{}','{}'))
       OR (SELECT count(*) FROM structural.content_carrier_vertices(
           ARRAY[entity,entity],ARRAY[placement,placement])) <> 2 THEN
        RAISE EXCEPTION 'empty or duplicate selected-set semantics changed';
    END IF;
    BEGIN
        PERFORM * FROM structural.content_carrier_vertices(ARRAY[entity],ARRAY[]::bytea[]);
    EXCEPTION WHEN invalid_parameter_value THEN failures := failures+1; END;
    BEGIN
        PERFORM * FROM structural.content_carrier_vertices(ARRAY[entity],ARRAY[entity]);
    EXCEPTION WHEN invalid_parameter_value THEN failures := failures+1; END;
    BEGIN
        PERFORM * FROM structural.content_carrier_vertices(ARRAY[NULL::bytea],ARRAY[placement]);
    EXCEPTION WHEN invalid_parameter_value THEN failures := failures+1; END;
    BEGIN
        PERFORM * FROM structural.content_carrier_vertices(ARRAY[decode('01','hex')],ARRAY[placement]);
    EXCEPTION WHEN invalid_parameter_value THEN failures := failures+1; END;
    BEGIN
        PERFORM * FROM structural.content_carrier_vertices(NULL::bytea[],ARRAY[placement]);
    EXCEPTION WHEN invalid_parameter_value THEN failures := failures+1; END;
    BEGIN
        PERFORM * FROM structural.content_carrier_vertices(ARRAY[[entity,entity]],ARRAY[placement,placement]);
    EXCEPTION WHEN invalid_parameter_value THEN failures := failures+1; END;
    IF failures<>6 THEN RAISE EXCEPTION 'malformed selected identity batches were accepted: %',failures; END IF;
    -- A corrupt stored entity link must never be attributed to the requested parent.
    UPDATE laplace.physicalities SET entity_id=public.laplace_hash128_blake3('carrier-test/wrong-owner')
    WHERE id=placement;
    BEGIN
        PERFORM * FROM structural.content_carrier_vertices(ARRAY[entity],ARRAY[placement]);
    EXCEPTION WHEN data_corrupted THEN failures := failures+1; END;
    IF failures<>7 THEN RAISE EXCEPTION 'corrupt stored physicality ownership was accepted'; END IF;
    RAISE NOTICE 'content carrier batch: missing carriers stay absent, duplicates form a set, malformed selections and corrupt ownership reject';
END
$inputs$;
ROLLBACK;
