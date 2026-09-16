-- Cold backend reads the exact committed descriptor graph from the prior fixture.
\set ECHO none
BEGIN ISOLATION LEVEL REPEATABLE READ;
DO $cold$
DECLARE warm_result record; cold_result record;
BEGIN
    SELECT * INTO STRICT warm_result FROM physicality_readback_fixture.warm;
    SELECT * INTO STRICT cold_result FROM structural.physicality_descriptor_read(warm_result.descriptor_ids,268435456,2048,10000000);
    IF cold_result.descriptor_ids<>warm_result.descriptor_ids OR cold_result.entity_ids<>warm_result.entity_ids
       OR cold_result.physicality_types<>warm_result.physicality_types OR cold_result.coordinate_bits<>warm_result.coordinate_bits
       OR cold_result.hilbert_indices<>warm_result.hilbert_indices OR cold_result.trajectory_bits<>warm_result.trajectory_bits
       OR cold_result.constituent_counts<>warm_result.constituent_counts
       OR cold_result.alignment_residual_bits IS DISTINCT FROM warm_result.alignment_residual_bits
       OR cold_result.source_dimensions IS DISTINCT FROM warm_result.source_dimensions
       OR cold_result.floor_receipt<>warm_result.floor_receipt THEN
        RAISE EXCEPTION 'cold backend changed immutable descriptor body readback';
    END IF;
    RAISE NOTICE 'physicality readback: independent cold backend exactly reproduces all retained body fields';
END
$cold$;
DO $unavailable_cold$
DECLARE warm_result record; cold_result record; indexed record; source record;
BEGIN
    SELECT * INTO STRICT warm_result FROM physicality_readback_fixture.unavailable_warm;
    SELECT * INTO STRICT source FROM physicality_readback_fixture.unavailable_source;
    SELECT * INTO STRICT cold_result FROM structural.physicality_descriptor_read(warm_result.descriptor_ids,268435456,2048,10000000);
    SELECT * INTO STRICT indexed FROM structural.physicality_forms(ARRAY[source.entity_id],decode('','hex'),10,268435456,2048,10000000);
    IF cold_result.descriptor_ids IS DISTINCT FROM warm_result.descriptor_ids
       OR cold_result.entity_ids IS DISTINCT FROM warm_result.entity_ids
       OR cold_result.physicality_types IS DISTINCT FROM warm_result.physicality_types
       OR cold_result.coordinate_bits IS DISTINCT FROM warm_result.coordinate_bits
       OR cold_result.hilbert_indices IS DISTINCT FROM warm_result.hilbert_indices
       OR cold_result.trajectory_bits IS DISTINCT FROM warm_result.trajectory_bits
       OR cold_result.constituent_counts IS DISTINCT FROM warm_result.constituent_counts
       OR cold_result.alignment_residual_bits IS DISTINCT FROM warm_result.alignment_residual_bits
       OR cold_result.source_dimensions IS DISTINCT FROM warm_result.source_dimensions
       OR cold_result.floor_receipt IS DISTINCT FROM warm_result.floor_receipt
       OR indexed.descriptor_ids IS DISTINCT FROM warm_result.descriptor_ids OR NOT indexed.exhausted THEN
        RAISE EXCEPTION 'cold descriptor-only direct or indexed readback changed exact retained body';
    END IF;
    RAISE NOTICE 'physicality readback: independent cold backend preserves descriptor-only opaque body and indexed discovery';
END
$unavailable_cold$;
DO $legacy_cold$
DECLARE warm_result record; cold_result record; indexed record; f record; removed bigint;
BEGIN
    SELECT * INTO STRICT warm_result FROM physicality_readback_fixture.legacy_warm;
    SELECT * INTO STRICT f FROM physicality_readback_fixture.legacy_frames;
    -- This backend has never seen these rows. Force the authentic historical
    -- Content closure, then restore type9 through subtransaction rollback.
    BEGIN
        DELETE FROM laplace.physicalities WHERE entity_id=ANY(f.node_ids) AND type=9;
        GET DIAGNOSTICS removed=ROW_COUNT;
        IF removed<>10 THEN RAISE EXCEPTION 'cold legacy control lost its exact native type9 closure'; END IF;
        SELECT * INTO STRICT cold_result FROM structural.physicality_descriptor_read(warm_result.descriptor_ids,268435456,2048,10000000);
        SELECT * INTO STRICT indexed FROM structural.physicality_forms(ARRAY[f.entity_id],decode('','hex'),32,268435456,2048,10000000);
        IF ROW(cold_result.descriptor_ids,cold_result.entity_ids,cold_result.physicality_types,cold_result.coordinate_bits,
               cold_result.hilbert_indices,cold_result.trajectory_bits,cold_result.constituent_counts,
               cold_result.alignment_residual_bits,cold_result.source_dimensions)
           IS DISTINCT FROM ROW(warm_result.descriptor_ids,warm_result.entity_ids,warm_result.physicality_types,warm_result.coordinate_bits,
               warm_result.hilbert_indices,warm_result.trajectory_bits,warm_result.constituent_counts,
               warm_result.alignment_residual_bits,warm_result.source_dimensions)
           OR indexed.descriptor_ids IS DISTINCT FROM warm_result.descriptor_ids OR NOT indexed.exhausted THEN
            RAISE EXCEPTION 'cold genuine legacy Content read changed exact descriptor body or discovery';
        END IF;
        RAISE EXCEPTION USING ERRCODE='LP002',MESSAGE='restore current retention after cold legacy parity';
    EXCEPTION WHEN SQLSTATE 'LP002' THEN NULL;
    END;
    RAISE NOTICE 'physicality readback: independent cold backend reads genuine legacy Content with exact type9 body and index parity';
END
$legacy_cold$;
DROP TABLE physicality_readback_fixture.incidental,physicality_readback_fixture.warm,
    physicality_readback_fixture.legacy_warm,physicality_readback_fixture.legacy_admitted,
    physicality_readback_fixture.legacy_frames,
    physicality_readback_fixture.unavailable_warm,physicality_readback_fixture.unavailable_admitted,
    physicality_readback_fixture.unavailable_source,
    physicality_readback_fixture.admitted,physicality_readback_fixture.frames,
    physicality_readback_fixture.source,physicality_readback_fixture.atoms;
DROP SCHEMA physicality_readback_fixture;
COMMIT;
