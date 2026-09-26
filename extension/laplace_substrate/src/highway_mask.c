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

#include "laplace/core/highway_table.h"
#include "laplace/core/entity_type_law.h"
#include "laplace/core/attestation_engine.h"

#include "perfcache_native.h"
#include "entity_mask_write.h"

#if defined(_MSC_VER) && !defined(__clang__)
#include <intrin.h>
#endif

/*
 * Highway masks: a 256-bit set per entity of the governed relation bits it
 * takes part in as a consensus subject or object. The mask is a derived index
 * over the consensus face; plane selection in COUPLE and ROUTE tests it with a
 * few uint64 ops instead of reading cells.
 *
 * Byte order: a mask travels as the 32 raw bytes of laplace_mask256_t
 * (uint64 w[4], little-endian), bit b at byte b/8, value 1 << (b%8). memcpy
 * between bytea and laplace_mask256_t is the identity mapping.
 */

#define HIGHWAY_MASK_BYTES 32
#define HASH128_BYTES 16

typedef struct highway_deposit_type
{
    unsigned char id[HASH128_BYTES];
    laplace_mask256_t mask;
} highway_deposit_type;

typedef LaplaceEntityMaskDelta highway_deposit_entity;

static int
deposit_id_cmp(const void *a, const void *b)
{
    return memcmp(a, b, HASH128_BYTES);
}

static bytea *
deposit_bytea(const void *bytes, Size len)
{
    bytea *out = (bytea *) palloc(VARHDRSZ + len);

    SET_VARSIZE(out, VARHDRSZ + len);
    memcpy(VARDATA(out), bytes, len);
    return out;
}

static void
deposit_read_id(Datum value, unsigned char out[HASH128_BYTES], const char *which)
{
    bytea *id = DatumGetByteaPP(value);
    Size len = VARSIZE_ANY_EXHDR(id);

    if (len != HASH128_BYTES)
        ereport(ERROR,
                (errcode(ERRCODE_STRING_DATA_LENGTH_MISMATCH),
                 errmsg("highway_mask_deposit: %s must be exactly 16 bytes (got %zu)",
                        which, (size_t) len)));
    memcpy(out, VARDATA_ANY(id), HASH128_BYTES);
}

static inline void
deposit_mask_set(laplace_mask256_t *mask, uint8_t bit)
{
    unsigned char *bytes = (unsigned char *) mask;

    bytes[bit >> 3] |= (unsigned char) (1u << (bit & 7));
}

static inline void
deposit_mask_or(laplace_mask256_t *dst, const laplace_mask256_t *src)
{
    for (int i = 0; i < 4; i++)
        dst->w[i] |= src->w[i];
}

static inline bool
deposit_mask_empty(const laplace_mask256_t *mask)
{
    return (mask->w[0] | mask->w[1] | mask->w[2] | mask->w[3]) == 0;
}

static inline int
popcount64(uint64 v)
{
#if defined(_MSC_VER) && !defined(__clang__)
    return (int) __popcnt64(v);
#else
    return __builtin_popcountll(v);
#endif
}

static inline int
ctz32(unsigned int v)
{
#if defined(_MSC_VER) && !defined(__clang__)
    unsigned long idx;
    _BitScanForward(&idx, v);
    return (int) idx;
#else
    return __builtin_ctz(v);
#endif
}

PG_FUNCTION_INFO_V1(pg_laplace_highway_match);

/* (mask bytea, band_mask bytea) -> bool: any shared bit. NULL input -> false,
 * so a NULL mask never passes a WHERE filter. Length mismatch is an error. */
Datum
pg_laplace_highway_match(PG_FUNCTION_ARGS)
{
    bytea      *a;
    bytea      *b;
    const char *pa;
    const char *pb;
    Size        la;
    Size        lb;
    uint64      acc = 0;
    Size        i = 0;

    if (PG_ARGISNULL(0))
        PG_RETURN_BOOL(false);
    if (PG_ARGISNULL(1))
        PG_RETURN_BOOL(false);

    a = PG_GETARG_BYTEA_PP(0);
    b = PG_GETARG_BYTEA_PP(1);
    la = VARSIZE_ANY_EXHDR(a);
    lb = VARSIZE_ANY_EXHDR(b);
    if (la != lb)
        ereport(ERROR,
                (errcode(ERRCODE_STRING_DATA_LENGTH_MISMATCH),
                 errmsg("laplace_highway_match: mask lengths differ (%zu vs %zu)",
                        (size_t) la, (size_t) lb)));

    pa = VARDATA_ANY(a);
    pb = VARDATA_ANY(b);
    for (; i + 8 <= la; i += 8)
    {
        uint64 wa;
        uint64 wb;

        memcpy(&wa, pa + i, 8);
        memcpy(&wb, pb + i, 8);
        acc |= (wa & wb);
    }
    for (; i < la; i++)
        acc |= (uint64) ((unsigned char) pa[i] & (unsigned char) pb[i]);

    PG_RETURN_BOOL(acc != 0);
}

PG_FUNCTION_INFO_V1(pg_laplace_highway_popcount);

/* (mask bytea) -> int4 set-bit count; NULL -> 0. */
Datum
pg_laplace_highway_popcount(PG_FUNCTION_ARGS)
{
    bytea      *a;
    const char *p;
    Size        len;
    int         count = 0;
    Size        i = 0;

    if (PG_ARGISNULL(0))
        PG_RETURN_INT32(0);

    a = PG_GETARG_BYTEA_PP(0);
    p = VARDATA_ANY(a);
    len = VARSIZE_ANY_EXHDR(a);

    for (; i + 8 <= len; i += 8)
    {
        uint64 w;

        memcpy(&w, p + i, 8);
        count += popcount64(w);
    }
    for (; i < len; i++)
        count += popcount64((uint64) (unsigned char) p[i]);

    PG_RETURN_INT32(count);
}

PG_FUNCTION_INFO_V1(pg_laplace_highway_mask_bits);

/* (mask bytea) -> int4[] of set bit positions, ascending; NULL -> NULL. The
 * indexable form of a mask: a GIN index over these arrays answers bit overlap
 * (bits && band_bits), and its posting lists absorb the heavy key duplication
 * of a 256-value domain. */
Datum
pg_laplace_highway_mask_bits(PG_FUNCTION_ARGS)
{
    bytea      *a;
    const unsigned char *p;
    Size        len;
    Datum       bits[256];
    int         n = 0;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();

    a = PG_GETARG_BYTEA_PP(0);
    p = (const unsigned char *) VARDATA_ANY(a);
    len = VARSIZE_ANY_EXHDR(a);
    if (len > 32)
        len = 32;

    for (Size i = 0; i < len; i++)
    {
        unsigned char b = p[i];

        while (b)
        {
            int bit = ctz32((unsigned int) b);

            bits[n++] = Int32GetDatum((int32) (i * 8 + bit));
            b &= (unsigned char) (b - 1);
        }
    }

    PG_RETURN_ARRAYTYPE_P(construct_array(bits, n, INT4OID, 4, true, TYPALIGN_INT));
}

PG_FUNCTION_INFO_V1(pg_laplace_highway_mask_from_bits);

/*
 * (bits int4[]) -> bytea(32): inverse of laplace_highway_mask_bits, same bit
 * numbering (PostgreSQL set_bit order). NULL and out-of-range positions are
 * skipped. NULL input, or no bit set, -> NULL, never a zero mask.
 * mask_bits(mask_from_bits(x)) = sorted distinct x.
 */
Datum
pg_laplace_highway_mask_from_bits(PG_FUNCTION_ARGS)
{
    ArrayType        *arr;
    Datum            *elems;
    bool             *nulls;
    int               nelems;
    laplace_mask256_t mask;
    unsigned char    *mb = (unsigned char *) &mask;
    bool              any_set = false;
    bytea            *out;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();

    arr = PG_GETARG_ARRAYTYPE_P(0);
    deconstruct_array(arr, INT4OID, sizeof(int32), true, TYPALIGN_INT,
                      &elems, &nulls, &nelems);

    memset(&mask, 0, sizeof(mask));
    for (int i = 0; i < nelems; i++)
    {
        int32 b;

        if (nulls[i])
            continue;
        b = DatumGetInt32(elems[i]);
        if (b < 0 || b >= 256)
            continue;
        mb[b >> 3] |= (unsigned char) (1u << (b & 7));
        any_set = true;
    }

    if (!any_set)
        PG_RETURN_NULL();

    out = (bytea *) palloc(VARHDRSZ + HIGHWAY_MASK_BYTES);
    SET_VARSIZE(out, VARHDRSZ + HIGHWAY_MASK_BYTES);
    memcpy(VARDATA(out), &mask, HIGHWAY_MASK_BYTES);
    PG_RETURN_BYTEA_P(out);
}

static void
require_highway_table(const char *fn)
{
    if (!laplace_highway_ready())
        ereport(ERROR,
                (errcode(ERRCODE_CONFIG_FILE_ERROR),
                 errmsg("%s: highway perfcache not configured", fn),
                 errhint("ALTER SYSTEM SET laplace_substrate.highway_perfcache_path = "
                         "'<laplace_highway_perfcache.bin>'; SELECT pg_reload_conf(); "
                         "(install-extensions.cmd stages and configures it).")));
}

/* Whether the highway registry perfcache is mapped. Only its configuration
 * error is caught (the loader has no SQL side effects); cancellation,
 * allocation and database errors propagate. Writers use this to retain work
 * when the registry is absent; readers call require_highway_table. */
static bool
highway_registry_available(void)
{
    volatile bool ready = false;
    MemoryContext caller = CurrentMemoryContext;

    PG_TRY();
    {
        ready = laplace_highway_ready();
    }
    PG_CATCH();
    {
        MemoryContextSwitchTo(caller);
        ErrorData *error = CopyErrorData();
        FlushErrorState();
        if (error->sqlerrcode != ERRCODE_CONFIG_FILE_ERROR)
            ReThrowError(error);
        ereport(LOG,
                (errmsg("Highway registry unavailable; exact mask work remains recoverable"),
                 errdetail_internal("%s", error->message)));
        FreeErrorData(error);
    }
    PG_END_TRY();
    return ready;
}

PG_FUNCTION_INFO_V1(pg_laplace_highway_ready);

/* () -> bool: registry availability. A deposit made while false is retained,
 * not discarded. */
Datum
pg_laplace_highway_ready(PG_FUNCTION_ARGS)
{
    PG_RETURN_BOOL(highway_registry_available());
}

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

/* Refresh recomputes each requested entity's mask from every consensus cell
 * incident to it, as subject or object. The cursor is read in pages to bound
 * marshalling, to exhaustion; each distinct relation resolves once per
 * request. An entity with no incident cells gets a zero mask, which clears it. */
#define HIGHWAY_REFRESH_PAGE 8192

typedef struct highway_refresh_pair
{
    unsigned char entity[HASH128_BYTES];
    unsigned char type[HASH128_BYTES];
} highway_refresh_pair;


typedef struct HighwayRefreshContext
{
    highway_deposit_entity *masks;
    int requested;
    ArrayType *ids;
    MemoryContext owner;
    bool *spi_top;
} HighwayRefreshContext;

/* Called by the writer after it has locked the whole target set. A read-only
 * SPI cursor would otherwise see the outer statement's snapshot, taken before
 * those lock waits, so a fresh snapshot is pushed (READ COMMITTED only) and
 * popped on success or error. */
static void
highway_refresh_recompute(void *value)
{
    HighwayRefreshContext *context = value;
    highway_deposit_entity *masks = context->masks;
    int requested = context->requested;
    MemoryContext owner = context->owner;
    MemoryContext page;
    HASHCTL ctl = {0};
    HTAB *resolved_types;
    Portal cursor;
    static SPIPlanPtr incident_plan = NULL;
    Oid argtypes[1] = {BYTEAARRAYOID};
    Datum args[1] = {PointerGetDatum(context->ids)};

    PushActiveSnapshot(GetTransactionSnapshot());
    PG_TRY();
    {
        if (!incident_plan)
        {
            incident_plan = SPI_prepare_cursor(
                laplace_sql_query_text("entities.mask_incident_types"), 1, argtypes,
                CURSOR_OPT_PARALLEL_OK);
            if (!incident_plan || SPI_keepplan(incident_plan) != 0)
                elog(ERROR, "highway_mask_refresh: incident prepare failed");
        }
        cursor = SPI_cursor_open(NULL, incident_plan, args, NULL, true);
        if (!cursor)
            elog(ERROR, "highway_mask_refresh: incident cursor failed");
        ctl.keysize = HASH128_BYTES;
        ctl.entrysize = sizeof(highway_deposit_type);
        ctl.hcxt = owner;
        resolved_types = hash_create("Highway refresh relation masks", 256, &ctl,
                                     HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
        page = AllocSetContextCreate(owner, "Highway refresh page", ALLOCSET_DEFAULT_SIZES);
        for (;;)
        {
            MemoryContext previous;
            highway_refresh_pair *pairs;
            highway_deposit_type *missing;
            uint64 rows;
            int missing_count = 0;

            CHECK_FOR_INTERRUPTS();
            SPI_cursor_fetch(cursor, true, HIGHWAY_REFRESH_PAGE);
            rows = SPI_processed;
            if (!rows)
            {
                if (SPI_tuptable) SPI_freetuptable(SPI_tuptable);
                break;
            }
            previous = MemoryContextSwitchTo(page);
            pairs = palloc(sizeof(*pairs) * rows);
            missing = palloc0(sizeof(*missing) * rows);
            for (uint64 i = 0; i < rows; ++i)
            {
                HeapTuple tuple = SPI_tuptable->vals[i];
                TupleDesc desc = SPI_tuptable->tupdesc;
                bool entity_null, type_null, found;
                Datum entity = SPI_getbinval(tuple, desc, 1, &entity_null);
                Datum type = SPI_getbinval(tuple, desc, 2, &type_null);
                highway_deposit_type *entry;

                if (entity_null || type_null)
                    elog(ERROR, "highway_mask_refresh: incident row has a null identity");
                deposit_read_id(entity, pairs[i].entity, "entity id");
                deposit_read_id(type, pairs[i].type, "relation id");
                entry = hash_search(resolved_types, pairs[i].type, HASH_ENTER, &found);
                if (!found)
                {
                    memset(&entry->mask, 0, sizeof(entry->mask));
                    memcpy(missing[missing_count++].id, pairs[i].type, HASH128_BYTES);
                }
            }
            /* Tuples are freed before the nested family read reuses SPI. */
            SPI_freetuptable(SPI_tuptable);
            if (missing_count)
            {
                qsort(missing, missing_count, sizeof(*missing), deposit_id_cmp);
                deposit_resolve_types(missing, missing_count, context->spi_top);
                for (int i = 0; i < missing_count; ++i)
                {
                    highway_deposit_type *entry = hash_search(
                        resolved_types, missing[i].id, HASH_FIND, NULL);
                    entry->mask = missing[i].mask;
                }
            }
            for (uint64 i = 0; i < rows; ++i)
            {
                highway_deposit_entity *target = bsearch(
                    pairs[i].entity, masks, requested, sizeof(*masks), deposit_id_cmp);
                highway_deposit_type *type = hash_search(
                    resolved_types, pairs[i].type, HASH_FIND, NULL);
                if (!target || !type)
                    elog(ERROR, "highway_mask_refresh: incident row outside requested workset");
                deposit_mask_or(&target->mask, &type->mask);
            }
            MemoryContextSwitchTo(previous);
            MemoryContextReset(page);
        }
        SPI_cursor_close(cursor);
        hash_destroy(resolved_types);
        MemoryContextDelete(page);
    }
    PG_FINALLY();
    {
        PopActiveSnapshot();
    }
    PG_END_TRY();
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
    HighwayRefreshContext refresh = {masks, requested, ids, owner, &spi_top};
    updated = laplace_entity_masks_refresh(masks, requested,
                                           highway_refresh_recompute, &refresh);
    laplace_spi_finish(spi_top);
    MemoryContextSwitchTo(caller);
    MemoryContextDelete(owner);
    PG_RETURN_INT64(updated);
}

PG_FUNCTION_INFO_V1(pg_laplace_highway_band_mask);

/* (band int4) -> bytea(32): the 256-bit mask OR-ing every relation bit in the
 * given salience band. */
Datum
pg_laplace_highway_band_mask(PG_FUNCTION_ARGS)
{
    int32             band = PG_GETARG_INT32(0);
    laplace_mask256_t mask;
    bytea            *out;

    require_highway_table("laplace_highway_band_mask");
    if (band < 0 || band > 255 ||
        highway_table_band_mask((uint8_t) band, &mask) != 0)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("laplace_highway_band_mask: no such band %d", band)));

    out = (bytea *) palloc(VARHDRSZ + HIGHWAY_MASK_BYTES);
    SET_VARSIZE(out, VARHDRSZ + HIGHWAY_MASK_BYTES);
    memcpy(VARDATA(out), &mask, HIGHWAY_MASK_BYTES);
    PG_RETURN_BYTEA_P(out);
}

PG_FUNCTION_INFO_V1(pg_laplace_relation_highway_bit);

/* (type_id bytea) -> int4 bit position, or NULL if the relation is not in the
 * governed registry. */
Datum
pg_laplace_relation_highway_bit(PG_FUNCTION_ARGS)
{
    hash128_t type_id = datum_to_hash128(PG_GETARG_DATUM(0));
    uint8_t   bit_pos;
    float     rank;
    uint8_t   band;

    require_highway_table("laplace_relation_highway_bit");
    if (highway_table_relation_by_hash(&type_id, &bit_pos, &rank, &band) != 0)
        PG_RETURN_NULL();
    PG_RETURN_INT32((int32) bit_pos);
}

PG_FUNCTION_INFO_V1(pg_laplace_relation_highway_band);

/* (type_id bytea) -> int4 salience-band index, or NULL if ungoverned. */
Datum
pg_laplace_relation_highway_band(PG_FUNCTION_ARGS)
{
    hash128_t type_id = datum_to_hash128(PG_GETARG_DATUM(0));
    uint8_t   bit_pos;
    float     rank;
    uint8_t   band;

    require_highway_table("laplace_relation_highway_band");
    if (highway_table_relation_by_hash(&type_id, &bit_pos, &rank, &band) != 0)
        PG_RETURN_NULL();
    PG_RETURN_INT32((int32) band);
}

PG_FUNCTION_INFO_V1(pg_laplace_relation_type_id);

/* (name text) -> bytea: a relation label's content id from the native relation
 * law; a label outside the registry composes through Tier-0 like any content. */
Datum
pg_laplace_relation_type_id(PG_FUNCTION_ARGS)
{
    char     *name = text_to_cstring(PG_GETARG_TEXT_PP(0));
    hash128_t type_id;

    (void) laplace_perfcache_ready();
    if (laplace_relation_type_id(name, &type_id) < 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("relation_type_id: '%s' has no content identity", name)));
    PG_RETURN_DATUM(hash128_to_datum(&type_id));
}

PG_FUNCTION_INFO_V1(pg_laplace_entity_type_id);

/* (name text) -> bytea: the governed entity type's id; an undeclared type is an error. */
Datum
pg_laplace_entity_type_id(PG_FUNCTION_ARGS)
{
    char     *name = text_to_cstring(PG_GETARG_TEXT_PP(0));
    hash128_t type_id;

    if (laplace_entity_type_id(name, &type_id) != 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("entity_type_id: '%s' is not declared in engine/manifest/entity_types.toml", name)));
    PG_RETURN_DATUM(hash128_to_datum(&type_id));
}

PG_FUNCTION_INFO_V1(pg_laplace_entity_type_registry);

/*
 * laplace.entity_type_registry(): the governed entity types from the compiled
 * entity-type law. A type is a filter code on entity rows, not an entity; its
 * label is the registry's.
 */
Datum
pg_laplace_entity_type_registry(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;

    InitMaterializedSRF(fcinfo, 0);
    for (size_t i = 0; i < laplace_entity_type_count; i++)
    {
        hash128_t type_id;
        Datum     values[2];
        bool      nulls[2] = {false, false};

        if (laplace_entity_type_id(laplace_entity_type_canonical[i], &type_id) != 0)
            continue;
        values[0] = hash128_to_datum(&type_id);
        values[1] = CStringGetTextDatum(laplace_entity_type_canonical[i]);
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
    }
    return (Datum) 0;
}

/*
 * A type id's label from the relation or entity-type registry: the canonical
 * name lowercased with '_' as ' '. A type is a registry code, not content, so
 * an id neither registry declares has no label (NULL).
 */
static text *
type_label_text(Datum id_datum)
{
    hash128_t   id = datum_to_hash128(id_datum);
    const char *canonical = laplace_relation_canonical_for_type_id(&id);
    size_t      n;
    char       *label;

    if (canonical == NULL && laplace_entity_type_lookup(&id, &canonical) != 0)
        canonical = NULL;
    if (canonical == NULL)
        return NULL;
    n = strlen(canonical);
    label = palloc(n + 1);
    for (size_t i = 0; i < n; i++)
    {
        char c = canonical[i];

        label[i] = c == '_' ? ' ' : (c >= 'A' && c <= 'Z') ? (char) (c - 'A' + 'a') : c;
    }
    label[n] = '\0';
    return cstring_to_text(label);
}

PG_FUNCTION_INFO_V1(pg_laplace_type_label);

Datum
pg_laplace_type_label(PG_FUNCTION_ARGS)
{
    text *label = type_label_text(PG_GETARG_DATUM(0));

    if (label == NULL)
        PG_RETURN_NULL();
    PG_RETURN_TEXT_P(label);
}

PG_FUNCTION_INFO_V1(pg_laplace_type_label_batch);

/* (ids bytea[]) -> text[]: labels in input order; NULL for undeclared ids. */
Datum
pg_laplace_type_label_batch(PG_FUNCTION_ARGS)
{
    ArrayType *ids = PG_GETARG_ARRAYTYPE_P(0);
    Datum     *elems;
    bool      *elem_nulls;
    int        n;
    Datum     *labels;
    bool      *label_nulls;
    int        dims[1];
    int        lbs[1] = {1};

    deconstruct_array(ids, BYTEAOID, -1, false, TYPALIGN_INT, &elems, &elem_nulls, &n);
    labels = palloc(sizeof(Datum) * (n > 0 ? n : 1));
    label_nulls = palloc(sizeof(bool) * (n > 0 ? n : 1));
    for (int i = 0; i < n; i++)
    {
        text *label = elem_nulls[i] ? NULL : type_label_text(elems[i]);

        label_nulls[i] = label == NULL;
        labels[i] = label == NULL ? (Datum) 0 : PointerGetDatum(label);
    }
    dims[0] = n;
    if (n == 0)
        PG_RETURN_ARRAYTYPE_P(construct_empty_array(TEXTOID));
    PG_RETURN_ARRAYTYPE_P(construct_md_array(labels, label_nulls, 1, dims, lbs,
                                             TEXTOID, -1, false, TYPALIGN_INT));
}

PG_FUNCTION_INFO_V1(pg_laplace_relation_registry);

/*
 * laplace.relation_registry(): one row per assigned highway bit, from the
 * compiled relation law and the highway perfcache: bit, type id, name, band,
 * rank, symmetry, family root, parent. Relations are registry bits, not
 * entities; enumerating relation types reads this, not the entity table.
 */
Datum
pg_laplace_relation_registry(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;

    require_highway_table("laplace.relation_registry");
    InitMaterializedSRF(fcinfo, 0);

    for (int bit = 0; bit < 256; bit++)
    {
        const char                   *canonical = NULL;
        float                         rank;
        uint8_t                       band;
        hash128_t                     type_id;
        hash128_t                     family_id;
        hash128_t                     parent_id;
        const laplace_relation_def_t *def = NULL;
        Datum                         values[8];
        bool                          nulls[8] = {false};

        if (highway_table_relation_by_bit((uint8_t) bit, &canonical, &rank, &band) != 0)
            continue;
        if (laplace_relation_type_id(canonical, &type_id) < 0 ||
            laplace_relation_lookup(&type_id, &def) != 0 || def == NULL)
            continue;

        values[0] = Int16GetDatum((int16) bit);
        values[1] = hash128_to_datum(&type_id);
        values[2] = CStringGetTextDatum(canonical);
        values[3] = Int16GetDatum((int16) band);
        values[4] = Float4GetDatum(rank);
        values[5] = BoolGetDatum(def->symmetry == LAPLACE_REL_SYMMETRY_SYMMETRIC);
        nulls[6] = !(def->family_root_idx >= 0 &&
                     laplace_relation_type_id(laplace_relation_table[def->family_root_idx].canonical,
                                              &family_id) >= 0);
        if (!nulls[6])
            values[6] = hash128_to_datum(&family_id);
        nulls[7] = !(def->parent_idx >= 0 &&
                     laplace_relation_type_id(laplace_relation_table[def->parent_idx].canonical,
                                              &parent_id) >= 0);
        if (!nulls[7])
            values[7] = hash128_to_datum(&parent_id);
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
    }
    return (Datum) 0;
}

/*
 * consensus.band_edges(band, min_eff_mu, limit): consensus cells with an
 * object, not refuted, whose relation is in the given salience band, strongest
 * conservative standing (rating - 2 rd) first. The band's relation ids come
 * from the highway table in memory; one kept, indexed set read fetches the
 * cells, each relation contributing at most `limit` before the merged limit.
 */
static SPIPlanPtr band_edges_plan = NULL;

PG_FUNCTION_INFO_V1(pg_laplace_consensus_band_edges);

Datum
pg_laplace_consensus_band_edges(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    int32      band;
    int64      min_eff_mu;
    int64      limit_rows;
    Datum     *type_ids;
    int        n_types = 0;
    ArrayType *type_arr;
    Oid        argtypes[3] = { BYTEAARRAYOID, INT8OID, INT8OID };
    Datum      args[3];
    bool       spi_top = false;
    int        rc;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("consensus_band_edges: band must not be NULL")));
    band = PG_GETARG_INT32(0);
    min_eff_mu = PG_ARGISNULL(1) ? PG_INT64_MIN : PG_GETARG_INT64(1);
    limit_rows = PG_ARGISNULL(2) ? 1000 : PG_GETARG_INT64(2);
    if (limit_rows < 1)
        ereport(ERROR, (errmsg("consensus_band_edges: limit must be >= 1")));

    require_highway_table("consensus_band_edges");

    /* The band's relation ids, from at most 256 registry bits. */
    type_ids = (Datum *) palloc(sizeof(Datum) * 256);
    for (int bit = 0; bit < 256; bit++)
    {
        const char *canonical = NULL;
        float       rank;
        uint8_t     rec_band;
        hash128_t   type_id;

        if (highway_table_relation_by_bit((uint8_t) bit, &canonical, &rank, &rec_band) != 0)
            continue;
        if ((int32) rec_band != band)
            continue;
        if (laplace_relation_type_id(canonical, &type_id) < 0)
            continue;
        type_ids[n_types++] = hash128_to_datum(&type_id);
    }

    InitMaterializedSRF(fcinfo, 0);
    if (n_types == 0)
        return (Datum) 0;

    type_arr = construct_array(type_ids, n_types, BYTEAOID, -1,
                               false, TYPALIGN_INT);

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "consensus_band_edges: SPI_connect failed");

    args[0] = PointerGetDatum(type_arr);
    args[1] = Int64GetDatum(min_eff_mu);
    args[2] = Int64GetDatum(limit_rows);
    if (band_edges_plan == NULL)
    {
        SPIPlanPtr plan = SPI_prepare_cursor(laplace_sql_query_text("consensus.band_edges"),
            3, argtypes, CURSOR_OPT_GENERIC_PLAN);
        if (plan == NULL || SPI_keepplan(plan) != 0)
            elog(ERROR, "consensus_band_edges: cannot retain typed page plan");
        band_edges_plan = plan;
    }
    rc = SPI_execute_plan(band_edges_plan, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "consensus_band_edges: query failed: %s",
             SPI_result_code_string(rc));

    spi_emit_all_rows(rsinfo);

    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
