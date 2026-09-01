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
#define HASH128_BYTES 16

typedef struct highway_deposit_type
{
    unsigned char id[HASH128_BYTES];
    laplace_mask256_t mask;
} highway_deposit_type;

typedef struct highway_deposit_entity
{
    unsigned char id[HASH128_BYTES];
    laplace_mask256_t mask;
} highway_deposit_entity;

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

static inline bool
deposit_mask_contains(const laplace_mask256_t *have,
                      const laplace_mask256_t *wanted)
{
    for (int i = 0; i < 4; i++)
        if ((have->w[i] & wanted->w[i]) != wanted->w[i])
            return false;
    return true;
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

PG_FUNCTION_INFO_V1(pg_laplace_highway_mask_from_bits);

/*
 * (bits int4[]) -> bytea(32): the inverse of laplace_highway_mask_bits. Build a
 * 256-bit mask by setting one bit per position, LSB-first within each byte
 * (mask[b>>3] |= 1 << (b&7)) -- byte-identical to the prior plpgsql set_bit()
 * loop, whose numbering PostgreSQL defines as 1 << (n % 8) at byte n / 8, and
 * exactly the numbering laplace_highway_mask_bits decodes via ctz. Out-of-range
 * (or NULL) positions are skipped. NULL input -> NULL; a mask with no bits set
 * -> NULL (the plpgsql any_set contract), so an empty or all-skipped array
 * yields NULL, never a zero mask. Called by highway_mask_refresh every fold
 * epoch, so both the round trip mask_bits(mask_from_bits(x)) = sorted(x) and the
 * emitted bytes must be preserved.
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

PG_FUNCTION_INFO_V1(pg_laplace_highway_ready);

/* () -> bool: whether the highway perfcache is mmap'd and the bit table is
 * usable. Write paths (highway_mask_refresh) gate on this instead of faulting
 * the ingest fold on hosts whose GUC is not configured yet. */
Datum
pg_laplace_highway_ready(PG_FUNCTION_ARGS)
{
    PG_RETURN_BOOL(laplace_highway_ready());
}

/*
 * Native deposit lane. Pair reduction and all highway-table work stay in C;
 * SPI is used only for dynamic-relation family lookup and the indexed
 * pre-read, bytewise-ordered row lock, and keyed storage update. The caller owns the
 * apply_write_epoch bump -- this function deliberately does not advance it.
 */
PG_FUNCTION_INFO_V1(pg_laplace_highway_mask_deposit);

Datum
pg_laplace_highway_mask_deposit(PG_FUNCTION_ARGS)
{
    static const char *family_query =
        "SELECT subject_id, object_id "
        "FROM laplace.consensus "
        "WHERE subject_id = ANY($1) AND type_id = $2";
    static const char *read_query =
        "SELECT e.id, e.tier, e.highway_mask "
        "FROM laplace.entities e "
        "WHERE e.id = ANY($1) "
        "ORDER BY e.id, e.tier";
    static const char *lock_query =
        "SELECT e.id, e.tier, e.highway_mask "
        "FROM laplace.entities e "
        "WHERE e.id = ANY($1) "
        "ORDER BY e.id, e.tier "
        "FOR NO KEY UPDATE OF e";
    static const char *update_query =
        "UPDATE laplace.entities e SET highway_mask = u.mask "
        "FROM unnest($1::bytea[], $2::int2[], $3::bytea[]) "
        "     AS u(id, tier, mask) "
        "WHERE e.id = u.id AND e.tier = u.tier";
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
    int rc;

    if (!laplace_highway_ready())
    {
        ereport(WARNING,
                (errmsg("highway_mask_deposit: highway perfcache not configured — masks not deposited (set laplace_substrate.highway_perfcache_path)")));
        PG_RETURN_INT64(0);
    }

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

    /* Validate before doing any native hash lookup. A suffix-bearing bytea
     * must never alias its first 16 bytes to a governed relation. NULL array
     * elements retain unnest's SQL semantics and contribute no pair. */
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

    if (n_entities == 0 || n_types_in == 0)
        PG_RETURN_INT64(0);

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

    /* Canonical relations resolve without SQL. Only misses can be dynamic
     * family members and need the single IS_A consensus probe below. */
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

            if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
                elog(ERROR, "highway_mask_deposit: SPI_connect failed");
            rc = SPI_execute_with_args(family_query, 2, argtypes, args, NULL, true, 0);
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
        }
    }

    /* Map the zipped arrays and collapse duplicate entities to one 256-bit
     * delta. qsort is bytea's memcmp order, matching the writer and lock ORDER. */
    deposits = (highway_deposit_entity *)
        palloc0(sizeof(*deposits) * Min(n_entities, n_types_in));
    for (int i = 0; i < Min(n_entities, n_types_in); i++)
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

    {
        Datum *ids = (Datum *) palloc(sizeof(Datum) * n_deposits);
        ArrayType *ids_arr;
        Oid argtypes[1] = {BYTEAARRAYOID};
        Datum args[1];
        Datum *candidate_ids;
        int n_candidates = 0;

        for (int i = 0; i < n_deposits; i++)
            ids[i] = PointerGetDatum(deposit_bytea(deposits[i].id, HASH128_BYTES));
        ids_arr = construct_array(ids, n_deposits, BYTEAOID, -1, false,
                                  TYPALIGN_INT);
        args[0] = PointerGetDatum(ids_arr);

        if (!spi_top && laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
            elog(ERROR, "highway_mask_deposit: SPI_connect failed");
        rc = SPI_execute_with_args(read_query, 1, argtypes, args, NULL, true, 0);
        if (rc != SPI_OK_SELECT)
            elog(ERROR, "highway_mask_deposit: target read failed: %s",
                 SPI_result_code_string(rc));

        /* Avoid minting tuple locks for the overwhelmingly common replay/no-op
         * case. This is only a hint read: overlapping writers can change a mask
         * before the lock, so the locked version is checked again below. */
        candidate_ids = (Datum *) palloc(sizeof(Datum) * SPI_processed);
        for (uint64 i = 0; i < SPI_processed; i++)
        {
            HeapTuple tuple = SPI_tuptable->vals[i];
            TupleDesc desc = SPI_tuptable->tupdesc;
            bool id_null;
            bool old_null;
            Datum id = SPI_getbinval(tuple, desc, 1, &id_null);
            Datum old = SPI_getbinval(tuple, desc, 3, &old_null);
            unsigned char eid[HASH128_BYTES];
            highway_deposit_entity *delta;
            laplace_mask256_t old_mask;

            Assert(!id_null);
            deposit_read_id(id, eid, "stored entity id");
            delta = (highway_deposit_entity *) bsearch(
                eid, deposits, n_deposits, sizeof(*deposits), deposit_id_cmp);
            Assert(delta != NULL);
            memset(&old_mask, 0, sizeof(old_mask));
            if (!old_null)
            {
                bytea *mask_value = DatumGetByteaPP(old);
                if (VARSIZE_ANY_EXHDR(mask_value) != HIGHWAY_MASK_BYTES)
                    elog(ERROR, "highway_mask_deposit: stored highway mask is not 32 bytes");
                memcpy(&old_mask, VARDATA_ANY(mask_value), HIGHWAY_MASK_BYTES);
            }
            if (!deposit_mask_contains(&old_mask, &delta->mask))
                candidate_ids[n_candidates++] = PointerGetDatum(
                    deposit_bytea(eid, HASH128_BYTES));
        }

        if (n_candidates == 0)
        {
            laplace_spi_finish(spi_top);
            PG_RETURN_INT64(0);
        }

        /* Candidate ids are already in bytewise order from read_query. Duplicate
         * ids (the schema permits multiple tiers, though construction does not)
         * are harmless to ANY; keeping them avoids another allocation/sort. */
        ids_arr = construct_array(candidate_ids, n_candidates, BYTEAOID, -1,
                                  false, TYPALIGN_INT);
        args[0] = PointerGetDatum(ids_arr);
        rc = SPI_execute_with_args(lock_query, 1, argtypes, args, NULL, false, 0);
        if (rc != SPI_OK_SELECT)
            elog(ERROR, "highway_mask_deposit: target lock failed: %s",
                 SPI_result_code_string(rc));

        {
            uint64 n_locked = SPI_processed;
            Datum *update_ids = (Datum *) palloc(sizeof(Datum) * n_locked);
            Datum *update_tiers = (Datum *) palloc(sizeof(Datum) * n_locked);
            Datum *update_masks = (Datum *) palloc(sizeof(Datum) * n_locked);
            int n_update = 0;
            ArrayType *update_id_arr;
            ArrayType *update_tier_arr;
            ArrayType *update_mask_arr;
            Oid update_types[3] = {BYTEAARRAYOID, INT2ARRAYOID, BYTEAARRAYOID};
            Datum update_args[3];

            for (uint64 i = 0; i < n_locked; i++)
            {
                HeapTuple tuple = SPI_tuptable->vals[i];
                TupleDesc desc = SPI_tuptable->tupdesc;
                bool id_null;
                bool tier_null;
                bool old_null;
                Datum id = SPI_getbinval(tuple, desc, 1, &id_null);
                Datum tier = SPI_getbinval(tuple, desc, 2, &tier_null);
                Datum old = SPI_getbinval(tuple, desc, 3, &old_null);
                unsigned char eid[HASH128_BYTES];
                highway_deposit_entity *delta;
                laplace_mask256_t final_mask;
                bytea *mask_value;

                Assert(!id_null && !tier_null);
                deposit_read_id(id, eid, "stored entity id");
                delta = (highway_deposit_entity *) bsearch(
                    eid, deposits, n_deposits, sizeof(*deposits), deposit_id_cmp);
                Assert(delta != NULL);
                memset(&final_mask, 0, sizeof(final_mask));
                if (!old_null)
                {
                    mask_value = DatumGetByteaPP(old);
                    if (VARSIZE_ANY_EXHDR(mask_value) != HIGHWAY_MASK_BYTES)
                        elog(ERROR, "highway_mask_deposit: stored highway mask is not 32 bytes");
                    memcpy(&final_mask, VARDATA_ANY(mask_value), HIGHWAY_MASK_BYTES);
                }
                /* Recheck the tuple version obtained after waiting for its lock.
                 * If the winner deposited our complete delta, this caller is a
                 * no-op and must not inflate the bigint updated count. */
                if (deposit_mask_contains(&final_mask, &delta->mask))
                    continue;
                deposit_mask_or(&final_mask, &delta->mask);

                update_ids[n_update] = PointerGetDatum(deposit_bytea(
                    eid, HASH128_BYTES));
                update_tiers[n_update] = Int16GetDatum(DatumGetInt16(tier));
                update_masks[n_update] = PointerGetDatum(deposit_bytea(
                    &final_mask, HIGHWAY_MASK_BYTES));
                n_update++;
            }

            if (n_update == 0)
            {
                laplace_spi_finish(spi_top);
                PG_RETURN_INT64(0);
            }

            update_id_arr = construct_array(update_ids, n_update,
                                            BYTEAOID, -1, false, TYPALIGN_INT);
            update_tier_arr = construct_array(update_tiers, n_update,
                                              INT2OID, 2, true, TYPALIGN_SHORT);
            update_mask_arr = construct_array(update_masks, n_update,
                                              BYTEAOID, -1, false, TYPALIGN_INT);
            update_args[0] = PointerGetDatum(update_id_arr);
            update_args[1] = PointerGetDatum(update_tier_arr);
            update_args[2] = PointerGetDatum(update_mask_arr);
            rc = SPI_execute_with_args(update_query, 3, update_types, update_args,
                                       NULL, false, 0);
            if (rc != SPI_OK_UPDATE)
                elog(ERROR, "highway_mask_deposit: update failed: %s",
                     SPI_result_code_string(rc));
            n_locked = SPI_processed;

            laplace_spi_finish(spi_top);
            PG_RETURN_INT64((int64) n_locked);
        }
    }
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
 * consensus.band_edges(band, min_eff_mu, limit): every unrefuted consensus
 * edge whose relation type belongs to the given salience band, strongest
 * first. This is the plane-selection primitive the foundry/synthesis and any
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
    "  AND NOT consensus.refuted(rating, rd) "
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
