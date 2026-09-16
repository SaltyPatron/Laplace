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
DROP TABLE physicality_readback_fixture.incidental,physicality_readback_fixture.warm,
    physicality_readback_fixture.admitted,physicality_readback_fixture.frames,
    physicality_readback_fixture.source,physicality_readback_fixture.atoms;
DROP SCHEMA physicality_readback_fixture;
COMMIT;
