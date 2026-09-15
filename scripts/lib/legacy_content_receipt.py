"""One receipt projection for the existing legacy Content repair recipe.

Only SQL framing lives here. The recipe supplies its context and summary
SELECTs after constructing its canonical temporary plan. The transaction helper
owns resource admission, streaming, durable retention and application.
"""
from __future__ import annotations


def receipt_stream_sql() -> str:
    """Stream the same records measured by the preamble, in protocol order."""
    return """SELECT record FROM pg_temp.repair_receipt_records
ORDER BY phase,sort_key;
"""


def frozen_receipt_sql(context_sql: str, summary_sql: str, *,
                       resource_preamble: bool = True) -> str:
    """Freeze recipe-owned single-column SELECTs and define their receipt view.

    Inputs are fixed recipe SQL, not a caller-supplied SQL execution interface.
    The non-preamble form uses the identical frozen projection and stream for
    existing recipe tests. No native-input or physicality payload is copied into
    another temporary table: only the two control records and scalar sizes are
    materialized. The helper must wait for admission before requesting the stream.
    """
    context = context_sql.strip().removesuffix(";")
    summary = summary_sql.strip().removesuffix(";")
    if not context or not summary:
        raise ValueError("receipt context and summary SELECTs must be nonempty")
    setup = f"""
SET LOCAL client_encoding='UTF8';
CREATE TEMP TABLE repair_receipt_control ON COMMIT DROP AS
SELECT 0::smallint AS phase,frozen.record::jsonb AS record
FROM ({context}) AS frozen(record)
UNION ALL
SELECT 3::smallint AS phase,frozen.record::jsonb AS record
FROM ({summary}) AS frozen(record);

CREATE TEMP VIEW repair_receipt_records AS
SELECT phase,NULL::bytea AS sort_key,record FROM pg_temp.repair_receipt_control
UNION ALL
SELECT 1::smallint,entity_id,
  jsonb_build_object('kind','native-input','entity_id',encode(entity_id,'hex'),
    'entity',entity,'content',content)
FROM pg_temp.repair_native_inputs
UNION ALL
SELECT 2::smallint,old_id,
  jsonb_build_object('kind','physicality','repair_kind',repair_kind,'disposition',disposition,
    'action',action,'original',original,'existing_target',existing_target,
    'migration_proposal',migration_proposal,'proposed',proposed,'evidence',evidence)
FROM pg_temp.repair_plan;
"""
    if not resource_preamble:
        return setup + receipt_stream_sql()
    return setup + """WITH record_sizes AS MATERIALIZED (
  SELECT phase,octet_length(convert_to(record::text,'UTF8'))::bigint AS line_bytes
  FROM pg_temp.repair_receipt_records
), temporary_storage AS MATERIALIZED (
  -- Each heap/materialized table owns its indexes and TOAST in this total.
  -- Separate index/TOAST rows must not be summed a second time.
  SELECT count(*) AS relation_count,
    COALESCE(sum(pg_total_relation_size(c.oid)::numeric),0) AS relation_bytes
  FROM pg_catalog.pg_class c
  WHERE c.relnamespace=pg_my_temp_schema() AND c.relkind IN ('r','m')
)
SELECT jsonb_build_object(
  'kind','resources','schema','laplace.legacy-content-repair-resources/v1',
  'physicality_rows',count(*) FILTER(WHERE phase=2),
  'native_input_rows',count(*) FILTER(WHERE phase=1),
  'context_rows',count(*) FILTER(WHERE phase=0),
  'summary_rows',count(*) FILTER(WHERE phase=3),
  'plan_bytes',COALESCE(sum(line_bytes::numeric+1),0),
  'max_line_bytes',COALESCE(max(line_bytes),0),
  'max_jsonl_line_bytes',COALESCE(max(line_bytes+1),0),
  'temporary_relation_bytes',(SELECT relation_bytes FROM temporary_storage),
  'temporary_relation_count',(SELECT relation_count FROM temporary_storage),
  'temporary_relation_scope','Observed after plan materialization: current session heap/materialized tables including their indexes and TOAST; excludes executor spill and is not a preallocation or disk bound.',
  -- Copy only bounded identity/recipe metadata from the already captured context.
  -- Do not refresh its clock, transaction, producer, epoch or catalog witnesses.
  -- The helper's 64 KiB resource-record bound also applies to this projection.
  'plan_context',(SELECT jsonb_build_object(
    'database',record->'database','database_oid',record->'database_oid',
    'system_identifier',record->'system_identifier','transaction',record->'transaction',
    'observed_at',record->'observed_at','producer_generation',record->'producer_generation',
    'chess_coordinate_recipe',record->'chess_coordinate_recipe',
    'write_epoch_before',record->'write_epoch_before',
    'substrate_extension_version',record->'substrate_extension_version',
    'geometry_extension_version',record->'geometry_extension_version')
    FROM pg_temp.repair_receipt_control WHERE phase=0),
  'plan_summary',(SELECT record FROM pg_temp.repair_receipt_control WHERE phase=3))
FROM record_sizes;
"""
