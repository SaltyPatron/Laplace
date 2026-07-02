#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"

#include "spi_common.h"
#include "spi_nested.h"

#include "laplace/core/highway_table.h"

#include "perfcache_native.h"

#if defined(_MSC_VER) && !defined(__clang__)
#include <intrin.h>
#endif

/*
 * Native highway-mask primitives. The prior SQL implementations were the
 * canonical Rule #1 violation: laplace_highway_popcount computed POPCNT by
 * casting each byte to bit(8)::text and string-replacing '0's inside a
 * generate_series loop, and laplace_highway_match ran a per-row bytea AND
 * through the SQL executor. Both are hot-path predicates for plane selection
 * over 256-bit masks; here they are a handful of uint64 ops.
 *
 * Byte-order contract: a highway mask travels as 32 raw bytes of the
 * lasting in-memory representation (intent_stage.c writes the C#/native
 * Mask256 struct memory verbatim; laplace_mask256_t is uint64 w[4] on the
 * same little-endian targets). memcpy between bytea and laplace_mask256_t is
 * therefore the identity mapping used everywhere else in this codebase.
 */

#define HIGHWAY_MASK_BYTES 32

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

/* (mask bytea, band_mask bytea) -> bool; NULL input -> false (matches the
 * prior SQL's explicit NULL handling, so WHERE-clause semantics are
 * unchanged). Length mismatch is an error, as the SQL `&` operator's was. */
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

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
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

/* (mask bytea) -> int4; NULL -> 0 (matches the prior SQL's COALESCE). */
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

/* (mask bytea) -> int4[] of set bit positions; NULL -> NULL. This is the
 * indexable representation of a mask: a GIN index over these arrays serves
 * bit-overlap queries (bits && band_bits) with compressed posting lists that
 * handle massive key duplication properly -- the structural replacement for
 * the removed highway_hash indexes, whose overflow chains cost ~700 buffer
 * hits per write on a 66-distinct-value column (Issue 36). */
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
 * highway table (i.e. not a governed canonical). */
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

/*
 * consensus_band_edges(band, min_eff_mu, limit): every unrefuted consensus
 * edge whose relation type belongs to the given salience band, strongest
 * first. This is the plane-selection primitive the foundry/pour and any
 * band-scoped reader should use.
 *
 * Shape follows the define_fast pattern: the band's relation-type id set is
 * computed entirely in memory from the highway table (no DB round trip —
 * bit -> canonical name -> BLAKE3 type id via the static relation law), then
 * ONE indexed SPI query does the fetch. consensus_type_btree carries the
 * type_id = ANY($1) filter; the eff_mu expression index carries the ordering.
 */
static const char *BAND_EDGES_QUERY =
    "SELECT subject_id, type_id, object_id, rating, rd, witness_count, "
    "       (rating - 2 * rd) AS eff_mu "
    "FROM laplace.consensus "
    "WHERE type_id = ANY($1) "
    "  AND object_id IS NOT NULL "
    "  AND NOT laplace.refuted(rating, rd) "
    "  AND (rating - 2 * rd) >= $2 "
    "ORDER BY (rating - 2 * rd) DESC "
    "LIMIT $3";

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

    /* Collect the band's relation-type ids from the highway table: at most
     * 256 bit slots, resolved in memory with zero DB round trips. */
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

    type_arr = construct_array(type_ids, n_types, BYTEAOID, -1, false, TYPALIGN_INT);

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "consensus_band_edges: SPI_connect failed");

    args[0] = PointerGetDatum(type_arr);
    args[1] = Int64GetDatum(min_eff_mu);
    args[2] = Int64GetDatum(limit_rows);
    rc = SPI_execute_with_args(BAND_EDGES_QUERY, 3, argtypes, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "consensus_band_edges: query failed: %s",
             SPI_result_code_string(rc));

    spi_emit_all_rows(rsinfo);

    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
