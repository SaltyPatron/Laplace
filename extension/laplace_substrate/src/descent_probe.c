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

/*
 * Shared batch-presence core used by both laplace_entities_present_bitmap()
 * and laplace_tier_batch_existence_probe(). `bm` is assumed pre-zeroed by
 * the caller (palloc0'd result buffer) -- this function only ever SETS bits
 * for ids it can positively confirm present, via:
 *   1. a perfcache fast-path lookup (tier-0 codepoints resolve without a
 *      DB round-trip at all), applied uniformly to every candidate
 *      regardless of what tier it's actually at -- codepoint ids simply
 *      won't match for tier>0 candidates, so this is a pure accelerant,
 *      never a special case; then
 *   2. exactly one SPI batch query for everything the perfcache fast path
 *      didn't resolve.
 * No default-present assumption, no tree-walk, no short-circuiting based on
 * an unconfirmed guess -- a bit is 1 iff this function actually confirmed
 * that id has a committed row in the probed table.
 *
 * `ordinals_sql` selects which table's present-ordinals probe answers the
 * batch query. `use_perfcache` gates the tier-0 codepoint fast path: valid
 * only when probing `entities` (a codepoint id IS an entity id by axiom);
 * other tables' ids derive differently and must always hit the real query.
 */
static int
batch_presence_core(ArrayType *ids_array, uint8_t *bm, int candidate_count,
                    const char *ordinals_sql, bool use_perfcache)
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
        return SPI_OK_SELECT;

    deconstruct_array(ids_array, BYTEAOID, -1, false, 'i', &elems, &nulls, &nelems);
    if (nelems != candidate_count)
    {
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("batch_presence_core: array length mismatch")));
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

        if (use_perfcache && laplace_perfcache_ready())
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
        return SPI_OK_SELECT;
    }

    probe_array = construct_array(probe_elems, probe_n, BYTEAOID, -1, false, 'i');
    sub_bm = (uint8_t *) palloc0((probe_n + 7) / 8);
    argtypes[0] = BYTEAARRAYOID;
    args[0] = PointerGetDatum(probe_array);

    spi_rc = spi_mark_present_ordinals(
        ordinals_sql,
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

int
laplace_entities_present_bitmap(ArrayType *ids_array, uint8_t *bm, int candidate_count)
{
    return batch_presence_core(ids_array, bm, candidate_count,
                               "SELECT idx FROM laplace.entities_present_ordinals($1)",
                               true);
}

int
laplace_tier_batch_existence_probe(ArrayType *ids_array, uint8_t *bm, int candidate_count)
{
    return batch_presence_core(ids_array, bm, candidate_count,
                               "SELECT idx FROM laplace.entities_present_ordinals($1)",
                               true);
}

int
laplace_attestations_present_bitmap(ArrayType *ids_array, uint8_t *bm, int candidate_count)
{
    /* Attestation ids derive from (subject,type,object,source,context) --
     * never codepoint ids -- so the perfcache fast path is off by
     * construction, not merely expected-not-to-match. */
    return batch_presence_core(ids_array, bm, candidate_count,
                               "SELECT idx FROM laplace.attestations_present_ordinals($1)",
                               false);
}

int
laplace_physicalities_present_bitmap(ArrayType *ids_array, uint8_t *bm, int candidate_count)
{
    /* Physicality ids are their own content hashes, never codepoint ids --
     * perfcache fast path off by construction. Serves the write lane's
     * in-transaction verification: a physicality row may legitimately be
     * staged for an entity that already exists (projections, building
     * blocks land after the entity), so presence is decided by the
     * physicality's OWN id, never inferred from its entity. */
    return batch_presence_core(ids_array, bm, candidate_count,
                               "SELECT idx FROM laplace.physicalities_present_ordinals($1)",
                               false);
}

int
laplace_entities_stored_bitmap(ArrayType *ids_array, uint8_t *bm, int candidate_count)
{
    /* Perfcache fast path deliberately OFF: this probe answers "is there a
     * committed entities ROW", not "is this id resolvable". The write lane's
     * in-transaction verification is what makes tier-0 codepoint rows stored
     * in the first place (the unicode seed) -- answering their presence
     * axiomatically here would subtract them from the write list and the
     * rows would never land. */
    return batch_presence_core(ids_array, bm, candidate_count,
                               "SELECT idx FROM laplace.entities_present_ordinals($1)",
                               false);
}
