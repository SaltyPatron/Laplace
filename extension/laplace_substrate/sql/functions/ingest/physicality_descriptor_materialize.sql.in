-- One call in the existing writer control transaction. Arrays are native COPY
-- tuple bodies, without a stream header/trailer, aligned E/P/A by stage.
-- Raw source forms precede placement dedup. Admitted providers are the actual
-- legacy Content placement winners, used only after an explicit database miss.
CREATE OR REPLACE FUNCTION ops.physicality_descriptor_materialize(
    source_entities bytea[],
    source_physicalities bytea[],
    source_attestations bytea[],
    admitted_entities bytea[],
    admitted_physicalities bytea[],
    admitted_attestations bytea[],
    observation_source_ids bytea[],
    observation_source_unit_ids bytea[],
    observation_source_trust float8[],
    generated_at_unix_us bigint,
    maximum_bytes bigint,
    maximum_database_operations integer,
    maximum_raw_logical_occurrences bigint)
RETURNS TABLE (
    entities bytea[],
    physicalities bytea[],
    attestations bytea[],
    descriptor_ids bytea[],
    view_ids bytea[],
    floor_receipt bytea,
    snapshot_receipt text,
    source_form_count bigint,
    current_content_count bigint,
    missing_content_count bigint,
    provider_rounds integer,
    database_operations integer,
    reserved_peak_bytes bigint,
    tuple_bytes bigint,
    floor_index_added_bytes bigint,
    raw_logical_work bigint,
    stored_vertices bigint,
    generated_source_id bytea,
    view_states smallint[],
    view_missing_first bigint[],
    view_missing_count bigint[],
    view_missing_ids bytea[])
AS 'EXECUTION_LIBRARY', 'pg_laplace_physicality_descriptor_materialize'
LANGUAGE C STABLE STRICT PARALLEL RESTRICTED;

COMMENT ON FUNCTION ops.physicality_descriptor_materialize(
    bytea[],bytea[],bytea[],bytea[],bytea[],bytea[],bytea[],bytea[],float8[],
    bigint,bigint,integer,bigint) IS
'Trusted native ingest boundary: exact raw observations plus explicit producer source priors; one active SQL snapshot for current Content closure. Output stage arrays contain the native source declaration, vocabulary, then generated descriptors/views/structural observations. The returned generated source id is the actual ordinary source-label content root. Descriptors retain exact typed manifests independently of selected child geometry. View state 0 has a non-NULL view id; state 1 has NULL and an exact missing-id slice addressed by zero-based first/count arrays. Receipts describe this operation, not a historical child-geometry witness. Raw logical work counts expanded Content carrier validation; native generated plan allocation has the separate byte ceiling. Reserved bytes are admitted buffers, not backend RSS.';
