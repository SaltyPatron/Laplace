#include "laplace/core/sql_catalog.h"
#include "postgres.h"

#include "access/xact.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "nodes/parsenodes.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/memutils.h"
#include "utils/snapmgr.h"
#include "utils/hsearch.h"
#include "miscadmin.h"

#include "spi_common.h"
#include "spi_nested.h"
#include "spi_stream.h"

#include "laplace/core/highway_table.h"
#include "laplace/core/entity_type_law.h"
#include "laplace/core/attestation_engine.h"

#include "highway_mask_internal.h"
#include "perfcache_native.h"
#include "entity_mask_write.h"

/* Without a registry, the (entity, relation) pairs are persisted as pending in
 * one set write; no bits are computed. Inserting in pair order keeps
 * overlapping callers from taking unique-index locks in opposite order. */
static void
deposit_retain_pending(ArrayType *entities, ArrayType *types)
{
    static SPIPlanPtr pending_plan = NULL;
    Oid argtypes[2] = {BYTEAARRAYOID, BYTEAARRAYOID};
    Datum args[2] = {PointerGetDatum(entities), PointerGetDatum(types)};
    bool spi_top = false;

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "highway_mask_deposit: pending SPI_connect failed");
    if (!pending_plan)
    {
        /* An INSERT plan: no CURSOR_OPT_PARALLEL_OK. */
        SPIPlanPtr plan = SPI_prepare_cursor(
            laplace_sql_query_text("entities.mask_pending"), 2, argtypes, 0);
        if (!plan || SPI_keepplan(plan) != 0)
            elog(ERROR, "highway_mask_deposit: pending prepare failed");
        pending_plan = plan;
    }
    int rc = SPI_execute_plan(pending_plan, args, NULL, false, 0);
    if (rc != SPI_OK_INSERT)
        elog(ERROR, "highway_mask_deposit: retaining exact pairs failed: %s",
             SPI_result_code_string(rc));
    laplace_spi_finish(spi_top);
}

/* Relation id -> mask bit, shared by deposit and refresh. A governed relation
 * is a registry lookup in memory. A relation not in the registry takes the bit
 * of the registered family it IS_A in consensus, read for all misses in one
 * prepared set read. `types` must be sorted by id. */
static void
deposit_resolve_types(highway_deposit_type *types, int n_types, bool *spi_top)
{
    {
        int n_missing = 0;
        for (int i = 0; i < n_types; i++)
        {
            hash128_t tid;
            uint8_t bit;
            float rank;
            uint8_t band;

            memcpy(&tid, types[i].id, sizeof(tid));
            if (highway_table_relation_by_hash(&tid, &bit, &rank, &band) == 0)
                deposit_mask_set(&types[i].mask, bit);
            else
                n_missing++;
        }

        if (n_missing > 0)
        {
            static SPIPlanPtr family_plan = NULL;
            Datum *missing = (Datum *) palloc(sizeof(Datum) * n_missing);
            ArrayType *missing_arr;
            hash128_t isa;
            Oid argtypes[2] = {BYTEAARRAYOID, BYTEAOID};
            Datum args[2];
            int m = 0;

            for (int i = 0; i < n_types; i++)
                if (deposit_mask_empty(&types[i].mask))
                    missing[m++] = PointerGetDatum(
                        deposit_bytea(types[i].id, HASH128_BYTES));
            missing_arr = construct_array(missing, n_missing, BYTEAOID, -1,
                                          false, TYPALIGN_INT);
            if (laplace_relation_type_id("IS_A", &isa) < 0)
                elog(ERROR, "highway_mask_deposit: cannot derive IS_A relation id");
            args[0] = PointerGetDatum(missing_arr);
            args[1] = hash128_to_datum(&isa);

            if (!*spi_top && laplace_spi_connect(spi_top) != SPI_OK_CONNECT)
                elog(ERROR, "highway_mask_deposit: SPI_connect failed");
            if (!family_plan)
            {
                SPIPlanPtr plan = SPI_prepare_cursor(
                    laplace_sql_query_text("entities.mask_families"),
                    2, argtypes, CURSOR_OPT_PARALLEL_OK);
                if (!plan || SPI_keepplan(plan) != 0)
                    elog(ERROR, "highway_mask_deposit: family prepare failed");
                family_plan = plan;
            }
            int rc = SPI_execute_plan(family_plan, args, NULL, true, 0);
            if (rc != SPI_OK_SELECT)
                elog(ERROR, "highway_mask_deposit: family lookup failed: %s",
                     SPI_result_code_string(rc));

            for (uint64 i = 0; i < SPI_processed; i++)
            {
                HeapTuple tuple = SPI_tuptable->vals[i];
                TupleDesc desc = SPI_tuptable->tupdesc;
                bool subj_null;
                bool obj_null;
                Datum subj = SPI_getbinval(tuple, desc, 1, &subj_null);
                Datum obj = SPI_getbinval(tuple, desc, 2, &obj_null);
                unsigned char sid[HASH128_BYTES];
                unsigned char oid[HASH128_BYTES];
                highway_deposit_type *entry;
                hash128_t family_id;
                uint8_t bit;
                float rank;
                uint8_t band;

                if (subj_null || obj_null)
                    continue;
                deposit_read_id(subj, sid, "dynamic relation id");
                deposit_read_id(obj, oid, "relation family id");
                entry = (highway_deposit_type *) bsearch(
                    sid, types, n_types, sizeof(*types), deposit_id_cmp);
                if (entry == NULL)
                    continue;
                memcpy(&family_id, oid, sizeof(family_id));
                if (highway_table_relation_by_hash(&family_id, &bit, &rank, &band) == 0)
                    deposit_mask_set(&entry->mask, bit);
            }
            SPI_freetuptable(SPI_tuptable);
        }
    }
}

/*
 * (entities bytea[], types bytea[]) -> int8: fold the relation bit of each
 * zipped (entity, relation) pair into the entity's mask during ingest. Pairs
 * reduce in memory to one 256-bit delta per entity; the shared native writer
 * locks and updates rows in bytewise id order and rechecks masks after lock
 * waits. Returns entity masks updated. The caller advances apply_write_epoch.
 */
PG_FUNCTION_INFO_V1(pg_laplace_highway_mask_deposit);

Datum
pg_laplace_highway_mask_deposit(PG_FUNCTION_ARGS)
{
    ArrayType *entity_arr = NULL;
    ArrayType *type_arr = NULL;
    Datum *entity_values = NULL;
    Datum *type_values = NULL;
    bool *entity_nulls = NULL;
    bool *type_nulls = NULL;
    int n_entities = 0;
    int n_types_in = 0;
    highway_deposit_type *types;
    highway_deposit_entity *deposits;
    int n_types = 0;
    int n_deposits = 0;
    bool spi_top = false;

    if (!PG_ARGISNULL(0))
    {
        entity_arr = PG_GETARG_ARRAYTYPE_P(0);
        deconstruct_array(entity_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                          &entity_values, &entity_nulls, &n_entities);
    }
    if (!PG_ARGISNULL(1))
    {
        type_arr = PG_GETARG_ARRAYTYPE_P(1);
        deconstruct_array(type_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                          &type_values, &type_nulls, &n_types_in);
    }
    if (n_entities != n_types_in)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("highway_mask_deposit: entity/type arrays must share length (%d vs %d)",
                        n_entities, n_types_in)));

    /* Every id is validated before either path, so both apply the same
     * identity contract. A pair with a NULL side contributes nothing. */
    for (int i = 0; i < n_entities; i++)
        if (!entity_nulls[i])
        {
            unsigned char ignored[HASH128_BYTES];
            deposit_read_id(entity_values[i], ignored, "entity id");
        }
    for (int i = 0; i < n_types_in; i++)
        if (!type_nulls[i])
        {
            unsigned char ignored[HASH128_BYTES];
            deposit_read_id(type_values[i], ignored, "type id");
        }

    if (n_entities == 0)
        PG_RETURN_INT64(0);
    if (!highway_registry_available())
    {
        deposit_retain_pending(entity_arr, type_arr);
        /* Zero masks updated; the pairs are retained as pending. */
        PG_RETURN_INT64(0);
    }

    types = (highway_deposit_type *) palloc0(sizeof(*types) * n_types_in);
    for (int i = 0; i < n_types_in; i++)
    {
        if (type_nulls[i])
            continue;
        deposit_read_id(type_values[i], types[n_types].id, "type id");
        n_types++;
    }
    if (n_types == 0)
        PG_RETURN_INT64(0);

    qsort(types, n_types, sizeof(*types), deposit_id_cmp);
    {
        int out = 0;
        for (int i = 0; i < n_types; i++)
        {
            if (out > 0 && memcmp(types[out - 1].id, types[i].id, HASH128_BYTES) == 0)
                continue;
            if (out != i)
                types[out] = types[i];
            out++;
        }
        n_types = out;
    }

    deposit_resolve_types(types, n_types, &spi_top);

    /* One OR-ed delta per entity, sorted in bytea memcmp order: the order the
     * writer locks in. */
    deposits = (highway_deposit_entity *)
        palloc0(sizeof(*deposits) * n_entities);
    for (int i = 0; i < n_entities; i++)
    {
        unsigned char eid[HASH128_BYTES];
        unsigned char tid[HASH128_BYTES];
        highway_deposit_type *entry;

        if (entity_nulls[i] || type_nulls[i])
            continue;
        deposit_read_id(entity_values[i], eid, "entity id");
        deposit_read_id(type_values[i], tid, "type id");
        entry = (highway_deposit_type *) bsearch(
            tid, types, n_types, sizeof(*types), deposit_id_cmp);
        if (entry == NULL || deposit_mask_empty(&entry->mask))
            continue;
        memcpy(deposits[n_deposits].id, eid, HASH128_BYTES);
        deposits[n_deposits].mask = entry->mask;
        n_deposits++;
    }

    if (n_deposits == 0)
    {
        laplace_spi_finish(spi_top);
        PG_RETURN_INT64(0);
    }
    qsort(deposits, n_deposits, sizeof(*deposits), deposit_id_cmp);
    {
        int out = 0;
        for (int i = 0; i < n_deposits; i++)
        {
            if (out > 0 && memcmp(deposits[out - 1].id, deposits[i].id,
                                  HASH128_BYTES) == 0)
            {
                deposit_mask_or(&deposits[out - 1].mask, &deposits[i].mask);
                continue;
            }
            if (out != i)
                deposits[out] = deposits[i];
            out++;
        }
        n_deposits = out;
    }

    if (!spi_top && laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "highway_mask_deposit: SPI_connect failed");
    int64 updated = laplace_entity_masks_apply(deposits, n_deposits);
    laplace_spi_finish(spi_top);
    PG_RETURN_INT64(updated);
}

/* Refresh one locked entity set. The indexed incident/family join is one
 * selection; Native folds its borrowed rows directly into the caller's masks.
 * No per-page relation query or incident-pair staging is required. */
typedef struct HighwayRefreshContext
{
    highway_deposit_entity *masks;
    int requested;
    ArrayType *ids;
} HighwayRefreshContext;

static void
highway_refresh_receive(TupleTableSlot *row, void *opaque)
{
    HighwayRefreshContext *context = opaque;
    bool entity_null, type_null, family_null;
    Datum entity = slot_getattr(row, 1, &entity_null);
    Datum type = slot_getattr(row, 2, &type_null);
    Datum family = slot_getattr(row, 3, &family_null);
    unsigned char id[HASH128_BYTES]; hash128_t relation;
    uint8_t bit, band; float rank;
    if (entity_null || type_null)
        elog(ERROR, "highway_mask_refresh: incident row has a null identity");
    deposit_read_id(entity, id, "entity id");
    highway_deposit_entity *target = bsearch(id, context->masks, context->requested,
        sizeof(*context->masks), deposit_id_cmp);
    if (!target) elog(ERROR, "highway_mask_refresh: incident row outside requested workset");
    deposit_read_id(type, (unsigned char *)&relation, "relation id");
    if (highway_table_relation_by_hash(&relation, &bit, &rank, &band) == 0)
        deposit_mask_set(&target->mask, bit);
    else if (!family_null) {
        deposit_read_id(family, (unsigned char *)&relation, "relation family id");
        if (highway_table_relation_by_hash(&relation, &bit, &rank, &band) == 0)
            deposit_mask_set(&target->mask, bit);
    }
}

/* Lock waits finish before this callback. READ COMMITTED refreshes its
 * snapshot here so a concurrent completed fold cannot leave a stale mask. */
static void
highway_refresh_recompute(void *value)
{
    HighwayRefreshContext *context = value;
    static SPIPlanPtr incident_plan;
    hash128_t isa;
    if (laplace_relation_type_id("IS_A", &isa) < 0)
        elog(ERROR, "highway_mask_refresh: cannot derive IS_A relation id");
    Oid types[] = {BYTEAARRAYOID, BYTEAOID};
    Datum args[] = {PointerGetDatum(context->ids), hash128_to_datum(&isa)};
    PushActiveSnapshot(GetTransactionSnapshot());
    PG_TRY(); {
        laplace_spi_stream(spi_common_ensure_plan(&incident_plan,
            laplace_sql_query_text("entities.mask_incident_families"), 2, types),
            2, types, args, NULL, highway_refresh_receive, context);
    } PG_FINALLY(); {
        PopActiveSnapshot();
    } PG_END_TRY();
    pfree(DatumGetPointer(args[1]));
}

PG_FUNCTION_INFO_V1(pg_laplace_highway_mask_refresh);

Datum
pg_laplace_highway_mask_refresh(PG_FUNCTION_ARGS)
{
    Datum *values;
    bool *nulls;
    int count, requested = 0;
    ArrayType *input, *ids;
    highway_deposit_entity *masks;
    bool spi_top = false;
    MemoryContext caller = CurrentMemoryContext;
    MemoryContext owner;
    Oid argtypes[1] = {BYTEAARRAYOID};
    Datum args[1];
    int64 updated;

    if (PG_ARGISNULL(0))
        PG_RETURN_INT64(0);
    if (IsolationUsesXactSnapshot())
        ereport(ERROR,
                (errcode(ERRCODE_T_R_SERIALIZATION_FAILURE),
                 errmsg("authoritative highway mask refresh requires READ COMMITTED"),
                 errhint("Retry this maintenance operation in a READ COMMITTED transaction.")));
    owner = AllocSetContextCreate(caller, "Highway refresh", ALLOCSET_DEFAULT_SIZES);
    MemoryContextSwitchTo(owner);
    input = PG_GETARG_ARRAYTYPE_P(0);
    deconstruct_array(input, BYTEAOID, -1, false, TYPALIGN_INT,
                      &values, &nulls, &count);
    if (count == 0)
    {
        MemoryContextSwitchTo(caller);
        MemoryContextDelete(owner);
        PG_RETURN_INT64(0);
    }
    if ((Size) count > MaxAllocSize / sizeof(*masks))
        elog(ERROR, "highway_mask_refresh: requested identity set exceeds allocation capacity");
    masks = palloc0(sizeof(*masks) * count);
    for (int i = 0; i < count; ++i)
    {
        if (nulls[i]) continue;
        deposit_read_id(values[i], masks[requested++].id, "entity id");
    }
    if (requested == 0)
    {
        MemoryContextSwitchTo(caller);
        MemoryContextDelete(owner);
        PG_RETURN_INT64(0);
    }
    qsort(masks, requested, sizeof(*masks), deposit_id_cmp);
    {
        int out = 0;
        for (int i = 0; i < requested; ++i)
        {
            if (out && memcmp(masks[out - 1].id, masks[i].id, HASH128_BYTES) == 0)
                continue;
            if (out != i) masks[out] = masks[i];
            ++out;
        }
        requested = out;
    }
    for (int i = 0; i < requested; ++i)
        values[i] = PointerGetDatum(deposit_bytea(masks[i].id, HASH128_BYTES));
    ids = construct_array(values, requested, BYTEAOID, -1, false, TYPALIGN_INT);
    args[0] = PointerGetDatum(ids);

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "highway_mask_refresh: SPI_connect failed");
    if (!highway_registry_available())
    {
        static SPIPlanPtr dirty_plan = NULL;
        if (!dirty_plan)
        {
            dirty_plan = SPI_prepare_cursor(laplace_sql_query_text("entities.mask_dirty"),
                                             1, argtypes, 0);
            if (!dirty_plan || SPI_keepplan(dirty_plan) != 0)
                elog(ERROR, "highway_mask_refresh: dirty prepare failed");
        }
        if (SPI_execute_plan(dirty_plan, args, NULL, false, 0) != SPI_OK_INSERT)
            elog(ERROR, "highway_mask_refresh: retaining dirty identities failed");
        laplace_spi_finish(spi_top);
        MemoryContextSwitchTo(caller);
        MemoryContextDelete(owner);
        PG_RETURN_INT64(0);
    }
    HighwayRefreshContext refresh = {masks, requested, ids};
    updated = laplace_entity_masks_refresh(masks, requested,
                                           highway_refresh_recompute, &refresh);
    laplace_spi_finish(spi_top);
    MemoryContextSwitchTo(caller);
    MemoryContextDelete(owner);
    PG_RETURN_INT64(updated);
}
