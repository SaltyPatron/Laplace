#include "descent_probe.h"

#include "executor/spi.h"
#include "utils/builtins.h"
#include "utils/memutils.h"

#include "perfcache_native.h"

static inline void
bitmap_set(uint8_t *bm, int pos)
{
    bm[pos >> 3] |= (uint8_t)(1u << (pos & 7u));
}

static inline void
bitmap_clear(uint8_t *bm, int pos)
{
    bm[pos >> 3] &= (uint8_t)~(1u << (pos & 7u));
}

static Datum
bytea16_datum(const uint8_t id[16])
{
    bytea *b = (bytea *) palloc(VARHDRSZ + 16);

    SET_VARSIZE(b, VARHDRSZ + 16);
    memcpy(VARDATA(b), id, 16);
    return PointerGetDatum(b);
}

static int
spi_mark_present_ordinals(const char *sql, int narg, Oid *argtypes, Datum *args,
                          uint8_t *bm, int candidate_count)
{
    int spi_rc = SPI_execute_with_args(sql, narg, argtypes, args, NULL, true, 0);

    if (spi_rc != SPI_OK_SELECT)
        return spi_rc;

    for (uint64 i = 0; i < SPI_processed; i++)
    {
        bool  isnull;
        Datum d = SPI_getbinval(SPI_tuptable->vals[i], SPI_tuptable->tupdesc, 1, &isnull);

        if (!isnull)
        {
            int pos = DatumGetInt32(d);

            if (pos >= 0 && pos < candidate_count)
                bitmap_set(bm, pos);
        }
    }
    return SPI_OK_SELECT;
}

int
laplace_entities_present_bitmap(ArrayType *ids_array, uint8_t *bm, int candidate_count)
{
    Datum      *elems;
    bool       *nulls;
    int         nelems;
    int        *remap;
    Datum      *probe_elems;
    int         probe_n = 0;
    int         i;
    Oid         argtypes[1];
    Datum       args[1];
    ArrayType  *probe_array;
    uint8_t    *sub_bm;
    int         spi_rc;

    if (candidate_count <= 0)
        return 0;

    deconstruct_array(ids_array, BYTEAOID, -1, false, 'i', &elems, &nulls, &nelems);
    if (nelems != candidate_count)
    {
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("entities_present_bitmap: array length mismatch")));
    }

    remap = (int *) palloc(sizeof(int) * candidate_count);
    probe_elems = (Datum *) palloc(sizeof(Datum) * candidate_count);

    for (i = 0; i < candidate_count; i++)
    {
        bytea       *b;
        const uint8_t *id;

        if (nulls[i])
            continue;
        b = DatumGetByteaPP(elems[i]);
        if (VARSIZE_ANY_EXHDR(b) != 16)
            continue;
        id = (const uint8_t *) VARDATA_ANY(b);

        if (laplace_perfcache_ready())
        {
            uint32_t cp;

            if (laplace_perfcache_codepoint_for_id(id, &cp))
            {
                bitmap_set(bm, i);
                continue;
            }
        }
        remap[probe_n] = i;
        probe_elems[probe_n++] = elems[i];
    }

    if (probe_n == 0)
    {
        pfree(remap);
        pfree(probe_elems);
        pfree(elems);
        pfree(nulls);
        return 0;
    }

    probe_array = construct_array(probe_elems, probe_n, BYTEAOID, -1, false, 'i');
    sub_bm = (uint8_t *) palloc0((probe_n + 7) / 8);
    argtypes[0] = BYTEAARRAYOID;
    args[0] = PointerGetDatum(probe_array);

    spi_rc = spi_mark_present_ordinals(
        "SELECT idx FROM laplace.entities_present_ordinals($1)",
        1, argtypes, args, sub_bm, probe_n);

    if (spi_rc == SPI_OK_SELECT)
    {
        for (i = 0; i < probe_n; i++)
        {
            if ((sub_bm[i >> 3] & (1u << (i & 7u))) != 0)
                bitmap_set(bm, remap[i]);
        }
    }

    pfree(sub_bm);
    pfree(probe_array);
    pfree(remap);
    pfree(probe_elems);
    pfree(elems);
    pfree(nulls);
    return spi_rc;
}

void
laplace_content_descent_bitmap_core(
    const uint8_t *ids16,
    const int32_t *parents,
    int n,
    uint8_t *bm)
{
    bool *entity_present;
    bool *visited;
    int  *stack;
    int   sp;
    int   i;

    if (n <= 0)
        return;

    entity_present = (bool *) palloc0(sizeof(bool) * n);
    visited = (bool *) palloc0(sizeof(bool) * n);
    stack = (int *) palloc(sizeof(int) * n);

    {
        Datum      *probe_elems;
        int        *remap;
        int         probe_n = 0;
        int         j;
        ArrayType  *probe_array;
        uint8_t    *sub_bm;
        Oid         argtypes[1];
        Datum       args[1];

        remap = (int *) palloc(sizeof(int) * n);
        probe_elems = (Datum *) palloc(sizeof(Datum) * n);

        for (i = 0; i < n; i++)
        {
            if (laplace_perfcache_ready())
            {
                uint32_t cp;

                if (laplace_perfcache_codepoint_for_id(ids16 + (size_t) i * 16, &cp))
                {
                    entity_present[i] = true;
                    continue;
                }
            }
            remap[probe_n] = i;
            probe_elems[probe_n++] = bytea16_datum(ids16 + (size_t) i * 16);
        }

        if (probe_n > 0)
        {
            probe_array = construct_array(probe_elems, probe_n, BYTEAOID, -1, false, 'i');
            sub_bm = (uint8_t *) palloc0((probe_n + 7) / 8);
            argtypes[0] = BYTEAARRAYOID;
            args[0] = PointerGetDatum(probe_array);

            if (SPI_connect() == SPI_OK_CONNECT)
            {
                if (spi_mark_present_ordinals(
                        "SELECT idx FROM laplace.entities_present_ordinals($1)",
                        1, argtypes, args, sub_bm, probe_n) == SPI_OK_SELECT)
                {
                    for (j = 0; j < probe_n; j++)
                    {
                        if ((sub_bm[j >> 3] & (1u << (j & 7u))) != 0)
                            entity_present[remap[j]] = true;
                    }
                }
                SPI_finish();
            }

            for (j = 0; j < probe_n; j++)
                pfree(DatumGetPointer(probe_elems[j]));
            pfree(sub_bm);
            pfree(probe_array);
        }

        pfree(remap);
        pfree(probe_elems);
    }

    sp = 0;
    for (i = 0; i < n; i++)
    {
        if (parents[i] >= 0)
            continue;
        stack[sp++] = i;
    }

    while (sp > 0)
    {
        int node = stack[--sp];

        if (visited[node])
            continue;
        visited[node] = true;

        if (entity_present[node])
            continue;

        bitmap_clear(bm, node);

        for (i = 0; i < n; i++)
        {
            if (parents[i] == node)
                stack[sp++] = i;
        }
    }

    pfree(entity_present);
    pfree(visited);
    pfree(stack);
}
