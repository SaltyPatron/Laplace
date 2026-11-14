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

void
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
bool
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
        /* The relation set and page size select the typed ranking index. A
         * generic LIMIT estimate can instead choose score-only scans and sort
         * every tied row before returning a small page. Keep the prepared query,
         * but plan its indexed reads with the actual arguments. */
        SPIPlanPtr plan = SPI_prepare_cursor(laplace_sql_query_text("consensus.band_edges"),
            3, argtypes, CURSOR_OPT_CUSTOM_PLAN | CURSOR_OPT_PARALLEL_OK);
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
