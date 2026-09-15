#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/timestamp.h"

#include "blake3.h"
#include "laplace/cognition_observation_request.h"
#include "laplace/core/sql_catalog.h"

#include <limits.h>
#include <stdint.h>
#include <string.h>

/*
 * Compatibility boundary only. PostgreSQL owns durable legacy storage and
 * bounded retrieval; Laplace-Refactor owns request compilation, search-state
 * transitions, A*, cognition operations, completion and receipts. Ordered
 * trajectory relation crossings are derived here from the canonical expanded
 * manifest returned by laplace_geom, not reimplemented in SQL.
 */

#define LAPLACE_REFACTOR_MAX_RESULTS 64
#define LAPLACE_REFACTOR_CANDIDATE_CAPACITY 256

static SPIPlanPtr candidate_plan = NULL;

typedef struct logical_run
{
    laplace_id128 entity_id;
    uint64_t start_ordinal;
    uint64_t run_length;
} logical_run;

static void
hash_start(blake3_hasher *hasher, const char *domain)
{
    uint32_t n = (uint32_t) strlen(domain);
    blake3_hasher_init(hasher);
    blake3_hasher_update(hasher, &n, sizeof(n));
    blake3_hasher_update(hasher, domain, n);
}

static void
hash_finish(blake3_hasher *hasher, laplace_digest256 *out)
{
    blake3_hasher_finalize(hasher, out->bytes, sizeof(out->bytes));
}

static void
hash_domain_bytes(const char *domain, const void *data, size_t size,
                  laplace_digest256 *out)
{
    blake3_hasher hasher;
    hash_start(&hasher, domain);
    if (data != NULL && size != 0)
        blake3_hasher_update(&hasher, data, size);
    hash_finish(&hasher, out);
}

static void
bytea_to_id128(bytea *value, laplace_id128 *out, const char *field)
{
    int len = VARSIZE_ANY_EXHDR(value);
    if (len != (int) sizeof(out->bytes))
        ereport(ERROR,
                (errmsg("refactor cognition: %s must be exactly %zu bytes, got %d",
                        field, sizeof(out->bytes), len)));
    memcpy(out->bytes, VARDATA_ANY(value), sizeof(out->bytes));
}

static bytea *
id128_to_bytea(const laplace_id128 *id)
{
    bytea *value = (bytea *) palloc(VARHDRSZ + sizeof(id->bytes));
    SET_VARSIZE(value, VARHDRSZ + sizeof(id->bytes));
    memcpy(VARDATA(value), id->bytes, sizeof(id->bytes));
    return value;
}

static bytea *
digest_to_bytea(const laplace_digest256 *digest)
{
    bytea *value = (bytea *) palloc(VARHDRSZ + sizeof(digest->bytes));
    SET_VARSIZE(value, VARHDRSZ + sizeof(digest->bytes));
    memcpy(VARDATA(value), digest->bytes, sizeof(digest->bytes));
    return value;
}

static bool
id128_equal(const laplace_id128 *left, const laplace_id128 *right)
{
    return memcmp(left->bytes, right->bytes, sizeof(left->bytes)) == 0;
}

static uint64_t
read_write_epoch(void)
{
    const char *sql = laplace_sql_query_text("refactor_cognition.write_epoch");
    if (sql == NULL)
        ereport(ERROR,
                (errmsg("refactor cognition: write-epoch query missing from native catalog")));
    int rc = SPI_execute(sql, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed != 1)
        ereport(ERROR,
                (errmsg("refactor cognition: could not read apply_write_epoch")));
    bool isnull = false;
    Datum value = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc,
                                1, &isnull);
    if (isnull)
        ereport(ERROR,
                (errmsg("refactor cognition: apply_write_epoch is NULL")));
    int64 epoch = DatumGetInt64(value);
    SPI_freetuptable(SPI_tuptable);
    if (epoch < 0)
        ereport(ERROR,
                (errmsg("refactor cognition: negative apply_write_epoch")));
    return (uint64_t) epoch;
}

static void
ensure_candidate_plan(void)
{
    if (candidate_plan != NULL)
        return;
    const char *sql = laplace_sql_query_text("refactor_cognition.physicalities");
    if (sql == NULL)
        ereport(ERROR,
                (errmsg("refactor cognition: physicality query missing from native catalog")));
    Oid argtypes[3] = { BYTEAOID, INT4OID, INT4OID };
    SPIPlanPtr plan = SPI_prepare(sql, 3, argtypes);
    if (plan == NULL)
        ereport(ERROR,
                (errmsg("refactor cognition: SPI_prepare candidate query failed: %s",
                        SPI_result_code_string(SPI_result))));
    if (SPI_keepplan(plan) != 0)
        ereport(ERROR,
                (errmsg("refactor cognition: SPI_keepplan candidate query failed")));
    candidate_plan = plan;
}

static void
candidate_observation_fingerprint(Datum physicality_id, uint32 relation,
                                  const laplace_id128 *source,
                                  const laplace_id128 *target,
                                  uint64 source_ordinal, uint64 target_ordinal,
                                  laplace_digest256 *out)
{
    bytea *physicality = DatumGetByteaPP(physicality_id);
    if (VARSIZE_ANY_EXHDR(physicality) != 16)
        ereport(ERROR,
                (errmsg("refactor cognition: physicality id must be exactly 16 bytes")));
    blake3_hasher hasher;
    hash_start(&hasher, "laplace.legacy.physicality-candidate/v1");
    blake3_hasher_update(&hasher, VARDATA_ANY(physicality),
                         (size_t) VARSIZE_ANY_EXHDR(physicality));
    blake3_hasher_update(&hasher, source->bytes, sizeof(source->bytes));
    blake3_hasher_update(&hasher, target->bytes, sizeof(target->bytes));
    blake3_hasher_update(&hasher, &source_ordinal, sizeof(source_ordinal));
    blake3_hasher_update(&hasher, &target_ordinal, sizeof(target_ordinal));
    blake3_hasher_update(&hasher, &relation, sizeof(relation));
    hash_finish(&hasher, out);
}

static logical_run *
decode_manifest_runs(ArrayType *manifest, size_t *run_count)
{
    *run_count = 0;
    if (ARR_NDIM(manifest) != 1 || ARR_ELEMTYPE(manifest) != BYTEAOID)
        ereport(ERROR,
                (errmsg("refactor cognition: expanded trajectory manifest must be bytea[]")));

    Datum *elements = NULL;
    bool *nulls = NULL;
    int element_count = 0;
    deconstruct_array(manifest, BYTEAOID, -1, false, TYPALIGN_INT,
                      &elements, &nulls, &element_count);
    if (element_count <= 0)
    {
        if (elements != NULL) pfree(elements);
        if (nulls != NULL) pfree(nulls);
        return NULL;
    }

    logical_run *runs = (logical_run *) palloc0(
        sizeof(logical_run) * (Size) element_count);
    for (int index = 0; index < element_count; ++index)
    {
        if (nulls != NULL && nulls[index])
            ereport(ERROR,
                    (errmsg("refactor cognition: expanded trajectory contains NULL")));
        bytea *member = DatumGetByteaPP(elements[index]);
        laplace_id128 id;
        bytea_to_id128(member, &id, "trajectory member");
        if (*run_count != 0 &&
            id128_equal(&runs[*run_count - 1].entity_id, &id))
        {
            ++runs[*run_count - 1].run_length;
        }
        else
        {
            logical_run *run = &runs[*run_count];
            run->entity_id = id;
            run->start_ordinal = (uint64_t) index + 1U;
            run->run_length = 1U;
            ++*run_count;
        }
    }
    pfree(elements);
    if (nulls != NULL) pfree(nulls);
    return runs;
}

static bool
append_candidate(laplace_cognition_observation_candidate *candidates,
                 size_t candidate_capacity, size_t *candidate_count,
                 laplace_cognition_observation_candidate_usage *usage,
                 Datum physicality_id,
                 const laplace_id128 *source,
                 const laplace_id128 *target,
                 uint64 source_ordinal, uint64 target_ordinal,
                 uint64 multiplicity, uint64 gap, uint32 relation,
                 size_t source_index)
{
    if (*candidate_count >= candidate_capacity)
        return false;
    laplace_cognition_observation_candidate *candidate =
        &candidates[*candidate_count];
    memset(candidate, 0, sizeof(*candidate));
    candidate->target_entity_id = *target;
    candidate->source_state_index = source_index;
    candidate->source_logical_ordinal = source_ordinal;
    candidate->target_logical_ordinal = target_ordinal;
    candidate->multiplicity = multiplicity;
    candidate->gap = gap;
    candidate->relation = relation;
    candidate->source_layer = LAPLACE_OBSERVATION_QUERY_SOURCE_PHYSICALITY;
    candidate_observation_fingerprint(
        physicality_id, relation, source, target,
        source_ordinal, target_ordinal,
        &candidate->observation_fingerprint);
    ++*candidate_count;
    ++usage->crossing_count;
    return true;
}

static bool
emit_physicality_candidates(
    Datum physicality_id,
    const laplace_id128 *container,
    const logical_run *runs,
    size_t run_count,
    const laplace_id128 *source,
    uint32 relation_mask,
    size_t source_index,
    laplace_cognition_observation_candidate *candidates,
    size_t candidate_capacity,
    size_t *candidate_count,
    laplace_cognition_observation_candidate_usage *usage)
{
    if ((relation_mask & LAPLACE_OBSERVATION_QUERY_CONTAINER) != 0U)
    {
        for (size_t i = 0; i < run_count; ++i)
            if (id128_equal(&runs[i].entity_id, source) &&
                !append_candidate(candidates, candidate_capacity, candidate_count, usage,
                    physicality_id, source, container,
                    runs[i].start_ordinal, 0U, runs[i].run_length, 0U,
                    LAPLACE_OBSERVATION_QUERY_CONTAINER, source_index))
                return false;
    }

    if ((relation_mask & LAPLACE_OBSERVATION_QUERY_CONSTITUENT) != 0U &&
        id128_equal(container, source))
    {
        for (size_t i = 0; i < run_count; ++i)
            if (!append_candidate(candidates, candidate_capacity, candidate_count, usage,
                    physicality_id, source, &runs[i].entity_id,
                    0U, runs[i].start_ordinal, runs[i].run_length, 0U,
                    LAPLACE_OBSERVATION_QUERY_CONSTITUENT, source_index))
                return false;
    }

    if ((relation_mask & LAPLACE_OBSERVATION_QUERY_PREDECESSOR) != 0U)
    {
        for (size_t i = 0; i < run_count; ++i)
        {
            if (!id128_equal(&runs[i].entity_id, source)) continue;
            if (i > 0 &&
                !append_candidate(candidates, candidate_capacity, candidate_count, usage,
                    physicality_id, source, &runs[i - 1].entity_id,
                    runs[i].start_ordinal,
                    runs[i - 1].start_ordinal + runs[i - 1].run_length - 1U,
                    1U, 1U, LAPLACE_OBSERVATION_QUERY_PREDECESSOR, source_index))
                return false;
            if (runs[i].run_length > 1U &&
                !append_candidate(candidates, candidate_capacity, candidate_count, usage,
                    physicality_id, source, source,
                    runs[i].start_ordinal + 1U, runs[i].start_ordinal,
                    runs[i].run_length - 1U, 1U,
                    LAPLACE_OBSERVATION_QUERY_PREDECESSOR, source_index))
                return false;
        }
    }

    if ((relation_mask & LAPLACE_OBSERVATION_QUERY_SUCCESSOR) != 0U)
    {
        for (size_t i = 0; i < run_count; ++i)
        {
            if (!id128_equal(&runs[i].entity_id, source)) continue;
            if (runs[i].run_length > 1U &&
                !append_candidate(candidates, candidate_capacity, candidate_count, usage,
                    physicality_id, source, source,
                    runs[i].start_ordinal + runs[i].run_length - 2U,
                    runs[i].start_ordinal + runs[i].run_length - 1U,
                    runs[i].run_length - 1U, 1U,
                    LAPLACE_OBSERVATION_QUERY_SUCCESSOR, source_index))
                return false;
            if (i + 1U < run_count &&
                !append_candidate(candidates, candidate_capacity, candidate_count, usage,
                    physicality_id, source, &runs[i + 1U].entity_id,
                    runs[i].start_ordinal + runs[i].run_length - 1U,
                    runs[i + 1U].start_ordinal,
                    1U, 1U, LAPLACE_OBSERVATION_QUERY_SUCCESSOR, source_index))
                return false;
        }
    }

    if ((relation_mask & LAPLACE_OBSERVATION_QUERY_COOCCUR) != 0U)
    {
        for (size_t source_run = 0; source_run < run_count; ++source_run)
        {
            const logical_run *s = &runs[source_run];
            if (!id128_equal(&s->entity_id, source)) continue;
            for (size_t target_run = 0; target_run < run_count; ++target_run)
            {
                const logical_run *t = &runs[target_run];
                if (target_run == source_run)
                {
                    if (s->run_length <= 1U) continue;
                    if (!append_candidate(candidates, candidate_capacity, candidate_count, usage,
                            physicality_id, source, source,
                            s->start_ordinal, s->start_ordinal + 1U,
                            s->run_length - 1U, 1U,
                            LAPLACE_OBSERVATION_QUERY_COOCCUR, source_index))
                        return false;
                    continue;
                }
                uint64 gap = t->start_ordinal > s->start_ordinal
                    ? t->start_ordinal - s->start_ordinal
                    : s->start_ordinal - t->start_ordinal;
                if (!append_candidate(candidates, candidate_capacity, candidate_count, usage,
                        physicality_id, source, &t->entity_id,
                        s->start_ordinal, t->start_ordinal,
                        t->run_length, gap,
                        LAPLACE_OBSERVATION_QUERY_COOCCUR, source_index))
                    return false;
            }
        }
    }
    return true;
}

static int
legacy_enumerate_candidates(
    void *provider_state,
    const laplace_observation_query_binding *binding,
    const laplace_id128 *source_entity_ids,
    const laplace_query_search_state *frontier_states,
    const uint64_t *accumulated_costs,
    size_t frontier_state_count,
    laplace_cognition_observation_candidate *candidates,
    size_t candidate_capacity,
    size_t *candidate_count,
    laplace_cognition_observation_candidate_usage *usage)
{
    (void) provider_state;
    (void) frontier_states;
    (void) accumulated_costs;
    if (binding == NULL || source_entity_ids == NULL || candidates == NULL ||
        candidate_count == NULL || usage == NULL || frontier_state_count == 0 ||
        candidate_capacity == 0)
        return 1;

    ensure_candidate_plan();
    *candidate_count = 0;
    memset(usage, 0, sizeof(*usage));

    for (size_t source_index = 0; source_index < frontier_state_count; ++source_index)
    {
        size_t remaining = candidate_capacity - *candidate_count;
        if (remaining == 0)
        {
            usage->limiting_disposition = LAPLACE_QUERY_SEARCH_DISPOSITION_UNKNOWN;
            *candidate_count = 0;
            return 0;
        }

        bytea *source = id128_to_bytea(&source_entity_ids[source_index]);
        int32 fetch_limit = remaining >= (size_t) INT_MAX - 1U
                            ? INT_MAX
                            : (int32) remaining + 1;
        Datum args[3] = {
            PointerGetDatum(source),
            Int32GetDatum((int32) binding->relation_mask),
            Int32GetDatum(fetch_limit)
        };
        char nulls[3] = { ' ', ' ', ' ' };
        int rc = SPI_execute_plan(candidate_plan, args, nulls, true, 0);
        if (rc != SPI_OK_SELECT)
            return 2;

        usage->database_operations++;
        usage->index_plan_count++;
        usage->rows_examined += SPI_processed;
        if (SPI_processed > remaining)
        {
            SPI_freetuptable(SPI_tuptable);
            usage->limiting_disposition = LAPLACE_QUERY_SEARCH_DISPOSITION_UNKNOWN;
            *candidate_count = 0;
            return 0;
        }

        for (uint64 row = 0; row < SPI_processed; ++row)
        {
            HeapTuple tuple = SPI_tuptable->vals[row];
            TupleDesc desc = SPI_tuptable->tupdesc;
            bool isnull = false;
            Datum physicality_id = SPI_getbinval(tuple, desc, 1, &isnull);
            if (isnull) return 3;
            Datum container_d = SPI_getbinval(tuple, desc, 2, &isnull);
            if (isnull) return 4;
            Datum manifest_d = SPI_getbinval(tuple, desc, 3, &isnull);
            if (isnull) return 5;

            laplace_id128 container;
            bytea_to_id128(DatumGetByteaPP(container_d), &container, "container entity");
            ArrayType *manifest = DatumGetArrayTypeP(manifest_d);
            size_t run_count = 0;
            logical_run *runs = decode_manifest_runs(manifest, &run_count);
            bool complete = emit_physicality_candidates(
                physicality_id, &container, runs, run_count,
                &source_entity_ids[source_index], binding->relation_mask, source_index,
                candidates, candidate_capacity, candidate_count, usage);
            if (runs != NULL) pfree(runs);
            if (!complete)
            {
                SPI_freetuptable(SPI_tuptable);
                usage->limiting_disposition = LAPLACE_QUERY_SEARCH_DISPOSITION_UNKNOWN;
                *candidate_count = 0;
                return 0;
            }
        }
        SPI_freetuptable(SPI_tuptable);
    }
    return 0;
}

static void
fill_request_identity(laplace_cognition_observation_request *request,
                      bytea *context, uint64 epoch)
{
    TimestampTz now = GetCurrentTimestamp();
    Oid database_id = MyDatabaseId;
    blake3_hasher hasher;

    hash_domain_bytes("laplace.legacy.world/v1", &database_id,
                      sizeof(database_id), &request->world_id);
    hash_domain_bytes("laplace.legacy.time/v1", &now, sizeof(now),
                      &request->time_fingerprint);

    hash_start(&hasher, "laplace.legacy.context/v1");
    blake3_hasher_update(&hasher, request->anchor_entity_id.bytes,
                         sizeof(request->anchor_entity_id.bytes));
    if (context != NULL)
        blake3_hasher_update(&hasher, VARDATA_ANY(context),
                             (size_t) VARSIZE_ANY_EXHDR(context));
    hash_finish(&hasher, &request->context_fingerprint);

    hash_domain_bytes("laplace.legacy.evidence-boundary/v1", &epoch,
                      sizeof(epoch), &request->evidence_boundary);
    hash_domain_bytes("laplace.legacy.evidence-epoch/v1", &epoch,
                      sizeof(epoch), &request->evidence_epoch);
    hash_domain_bytes("laplace.legacy.authority/substrate/v1", NULL, 0,
                      &request->authority_id);
    hash_domain_bytes("laplace.cognition.entity-path-result/v1", NULL, 0,
                      &request->result_contract_fingerprint);
}

PG_FUNCTION_INFO_V1(pg_laplace_refactor_cognition);

Datum
pg_laplace_refactor_cognition(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    bytea *anchor = PG_GETARG_BYTEA_PP(0);
    int32 relation_mask = PG_GETARG_INT32(1);
    int32 maximum_results = PG_GETARG_INT32(2);
    bytea *goal = PG_ARGISNULL(3) ? NULL : PG_GETARG_BYTEA_PP(3);
    int32 max_depth = PG_GETARG_INT32(4);
    bytea *context = PG_ARGISNULL(5) ? NULL : PG_GETARG_BYTEA_PP(5);
    bool terminal_results = PG_GETARG_BOOL(6);

    if (relation_mask <= 0 ||
        (((uint32) relation_mask) & ~LAPLACE_OBSERVATION_QUERY_RELATION_MASK) != 0)
        ereport(ERROR, (errmsg("refactor cognition: invalid relation mask %d",
                               relation_mask)));
    if (maximum_results <= 0 || maximum_results > LAPLACE_REFACTOR_MAX_RESULTS)
        ereport(ERROR,
                (errmsg("refactor cognition: maximum_results must be in [1,%d]",
                        LAPLACE_REFACTOR_MAX_RESULTS)));
    if (max_depth <= 0 || max_depth > 16)
        ereport(ERROR,
                (errmsg("refactor cognition: max_depth must be in [1,16]")));

    InitMaterializedSRF(fcinfo, 0);
    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR, (errmsg("refactor cognition: SPI_connect failed")));

    laplace_cognition_observation_request request;
    memset(&request, 0, sizeof(request));
    bytea_to_id128(anchor, &request.anchor_entity_id, "anchor");
    if (goal != NULL)
    {
        bytea_to_id128(goal, &request.goal_entity_id, "goal");
        request.flags |= LAPLACE_COGNITION_OBSERVATION_REQUEST_GOAL_PRESENT;
    }
    if (terminal_results)
        request.flags |= LAPLACE_COGNITION_OBSERVATION_REQUEST_TERMINAL_RESULTS;
    request.flags |= LAPLACE_COGNITION_OBSERVATION_REQUEST_BOUNDARY_COMPLETE;
    request.version = LAPLACE_COGNITION_OBSERVATION_REQUEST_VERSION;
    request.relation_mask = (uint32) relation_mask;
    request.maximum_results = (uint32) maximum_results;

    uint64 epoch = read_write_epoch();
    fill_request_identity(&request, context, epoch);

    request.search_budget.max_expanded_states = 256;
    request.search_budget.max_transition_records = 4096;
    request.search_budget.max_emitted_states = 4096;
    request.search_budget.max_frontier_states = 1024;
    request.search_budget.max_memory_bytes = UINT64_C(67108864);
    request.search_budget.max_io_operations = 4096;
    request.search_budget.max_database_operations = 1024;
    request.search_budget.max_provider_calls = 256;
    request.search_budget.max_depth = (uint32) max_depth;
    request.search_budget.requested_path_count = (uint32) maximum_results;
    request.search_budget.frontier_batch_width = 16;
    request.search_budget.transition_batch_capacity =
        LAPLACE_REFACTOR_CANDIDATE_CAPACITY;

    request.forward_limits.max_layers = 8;
    request.forward_limits.max_provider_calls = 16;
    request.forward_limits.max_projected_queries = 16;
    request.forward_limits.max_candidate_operations = 16;
    request.forward_limits.max_resolutions = 8;
    request.forward_limits.max_resource_cost = UINT64_C(1000000);
    request.forward_limits.max_io_operations = 4096;
    request.forward_limits.max_database_operations = 1024;
    request.forward_limits.candidate_operation_capacity = 16;
    request.forward_limits.resolution_capacity = 8;

    laplace_cognition_observation_candidate_provider_v1 provider;
    memset(&provider, 0, sizeof(provider));
    provider.maximum_candidate_records_per_expansion =
        LAPLACE_REFACTOR_CANDIDATE_CAPACITY;
    provider.enumerate_candidates = legacy_enumerate_candidates;
    provider.abi_major =
        LAPLACE_COGNITION_OBSERVATION_CANDIDATE_PROVIDER_ABI_MAJOR;
    provider.abi_minor =
        LAPLACE_COGNITION_OBSERVATION_CANDIDATE_PROVIDER_ABI_MINOR;
    {
        blake3_hasher hasher;
        hash_start(&hasher, "laplace.legacy.candidate-provider/v1");
        blake3_hasher_update(&hasher, &MyDatabaseId, sizeof(MyDatabaseId));
        blake3_hasher_update(&hasher, &epoch, sizeof(epoch));
        hash_finish(&hasher, &provider.provider_fingerprint);
    }

    laplace_cognition_observation_result *observation_result = NULL;
    laplace_cognition_forward_result *forward_result = NULL;
    laplace_cognition_forward_receipt receipt;
    memset(&receipt, 0, sizeof(receipt));
    laplace_cognition_observation_request_status status =
        laplace_cognition_observation_request_execute_with_candidate_provider(
            &request, &provider, &observation_result, &forward_result, &receipt);

    if (status != LAPLACE_COGNITION_OBSERVATION_REQUEST_OK)
    {
        laplace_cognition_observation_result_destroy(&observation_result);
        laplace_cognition_forward_result_destroy(&forward_result);
        SPI_finish();
        ereport(ERROR,
                (errmsg("refactor cognition execution failed with status %d",
                        (int) status)));
    }

    size_t answer_count =
        laplace_cognition_observation_result_answer_count(observation_result);
    for (size_t index = 0; index < answer_count; ++index)
    {
        laplace_cognition_observation_answer answer;
        memset(&answer, 0, sizeof(answer));
        status = laplace_cognition_observation_result_answer(
            observation_result, index, &answer);
        if (status != LAPLACE_COGNITION_OBSERVATION_REQUEST_OK)
            ereport(ERROR,
                    (errmsg("refactor cognition answer read failed at %zu", index)));

        Datum values[12];
        bool nulls[12] = { false, false, false, false, false, false,
                           false, false, false, false, false, false };
        values[0] = Int32GetDatum((int32) answer.rank);
        values[1] = PointerGetDatum(id128_to_bytea(&answer.entity_id));
        values[2] = PointerGetDatum(digest_to_bytea(&answer.path_id));
        values[3] = PointerGetDatum(digest_to_bytea(&answer.terminal_state_id));
        values[4] = Int64GetDatum((int64) answer.total_cost);
        values[5] = Int64GetDatum((int64) answer.transition_count);
        values[6] = Int64GetDatum((int64) answer.independent_evidence_root_count);
        values[7] = Int32GetDatum((int32) answer.relation_family);
        values[8] = Int32GetDatum((int32) answer.source_layer);
        values[9] = Int32GetDatum((int32) answer.direction);
        values[10] = PointerGetDatum(digest_to_bytea(&receipt.receipt_id));
        values[11] = PointerGetDatum(digest_to_bytea(&receipt.output_fingerprint));
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
    }

    laplace_cognition_observation_result_destroy(&observation_result);
    laplace_cognition_forward_result_destroy(&forward_result);
    if (SPI_finish() != SPI_OK_FINISH)
        ereport(ERROR, (errmsg("refactor cognition: SPI_finish failed")));
    return (Datum) 0;
}
