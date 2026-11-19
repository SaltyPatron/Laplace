#include "postgres.h"

#include <limits.h>
#include <math.h>
#include <string.h>

#include "catalog/pg_type.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "nodes/params.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "laplace/core/glicko2.h"
#include "laplace/core/sql_catalog.h"
#include "laplace_pg/executor.h"

#include "consensus_scan.h"
#include "converse_bindings.h"
#include "edges_raw_internal.h"
#include "public_direct.h"
#include "relations_internal.h"
#include "spi_common.h"

typedef struct EdgeDedupKey
{
    hash128_t subject_id;
    hash128_t type_id;
    hash128_t neighbour_id;
    uint8     dir;
    uint8     type_null;
    uint8     neighbour_null;
} EdgeDedupKey;

typedef struct EdgeDedupEntry
{
    EdgeDedupKey      key;
    LaplaceEdgeRawRow row;
    bool              has_entity;
} EdgeDedupEntry;

typedef struct NativeEdgesCtx
{
    MemoryContext    caller;
    HTAB            *dedup;
    const hash128_t *subj_ids;
    int              subj_count;
    const hash128_t *allowed_canon;
    int              n_allowed_canon;
    bool             has_type_filter;
    int              dir_mode;
    bool             include_refuted;
    uint64           rows_seen;
} NativeEdgesCtx;

typedef struct StoredEdgesCtx
{
    MemoryContext caller;
    HTAB         *dedup;
    int           dir_mode;
    bool          include_refuted;
    bool          reverse;
} StoredEdgesCtx;

static inline int
hash128_cmp(const hash128_t *a, const hash128_t *b)
{
    return memcmp(a, b, sizeof(hash128_t));
}

static int
hash128_qsort_cmp(const void *pa, const void *pb)
{
    return memcmp(pa, pb, sizeof(hash128_t));
}

static ArrayType *
normalize_id_array(ArrayType *arr, int *out_count, bool *out_empty)
{
    Datum     *elems;
    bool      *nulls;
    int        count;
    hash128_t *ids;
    int        n = 0;

    *out_count = 0;
    *out_empty = true;
    if (arr == NULL)
        return NULL;
    if (ARR_NDIM(arr) > 1 || ARR_ELEMTYPE(arr) != BYTEAOID)
        ereport(ERROR, (errmsg("edges_raw_batch: expected a 1-D bytea[] identity array")));
    deconstruct_array(arr, BYTEAOID, -1, false, TYPALIGN_INT, &elems, &nulls, &count);
    if (count == 0)
    {
        pfree(elems);
        pfree(nulls);
        return construct_empty_array(BYTEAOID);
    }
    ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) count);
    for (int i = 0; i < count; ++i)
    {
        if (nulls[i])
            continue;
        ids[n++] = datum_to_hash128(elems[i]);
    }
    pfree(elems);
    pfree(nulls);
    if (n == 0)
    {
        pfree(ids);
        return construct_empty_array(BYTEAOID);
    }
    qsort(ids, (Size) n, sizeof(hash128_t), hash128_qsort_cmp);
    {
        int w = 1;
        for (int i = 1; i < n; ++i)
        {
            if (memcmp(&ids[w - 1], &ids[i], sizeof(hash128_t)) != 0)
                ids[w++] = ids[i];
        }
        n = w;
    }
    *out_count = n;
    *out_empty = false;
    {
        ArrayType *out = hash128_array_from_ids(ids, n);
        pfree(ids);
        return out;
    }
}

static void
dedup_insert_row(HTAB *dedup, const LaplaceEdgeRawRow *cand, bool has_entity)
{
    EdgeDedupKey    key;
    EdgeDedupEntry *entry;
    bool            found = false;

    memset(&key, 0, sizeof(key));
    key.subject_id     = cand->subject_id;
    key.type_id        = cand->type_null ? (hash128_t){0, 0} : cand->type_id;
    key.neighbour_id   = cand->neighbour_null ? (hash128_t){0, 0} : cand->neighbour_id;
    key.dir            = cand->dir;
    key.type_null      = cand->type_null ? 1 : 0;
    key.neighbour_null = cand->neighbour_null ? 1 : 0;

    entry = (EdgeDedupEntry *) hash_search(dedup, &key, HASH_ENTER, &found);
    if (!found)
    {
        entry->row        = *cand;
        entry->has_entity = has_entity;
        return;
    }

    {
        __int128 new_mu = (__int128) cand->rating - 2 * (__int128) cand->rd;
        __int128 old_mu = (__int128) entry->row.rating - 2 * (__int128) entry->row.rd;
        bool     better = false;

        if (new_mu != old_mu)
            better = new_mu > old_mu;
        else if (cand->witness_count != entry->row.witness_count)
            better = cand->witness_count > entry->row.witness_count;
        else if (has_entity && entry->has_entity)
            better = memcmp(&cand->entity, &entry->row.entity, sizeof(hash128_t)) < 0;

        if (better)
        {
            entry->row        = *cand;
            entry->has_entity = has_entity;
        }
    }
}

static bool
hash128_sorted_contains(const hash128_t *arr, int count, const hash128_t *target)
{
    int lo = 0;
    int hi = count;

    while (lo < hi)
    {
        int mid = lo + (hi - lo) / 2;
        int c = memcmp(&arr[mid], target, sizeof(hash128_t));
        if (c < 0)
            lo = mid + 1;
        else if (c > 0)
            hi = mid;
        else
            return true;
    }
    return false;
}

static bool
visit_native_edge(TupleTableSlot *slot, void *context)
{
    NativeEdgesCtx   *ctx = (NativeEdgesCtx *) context;
    bool              isnull = false;
    Datum             d;
    int64             ord;
    hash128_t         subj_b3;
    hash128_t         entity;
    lp_path           path;
    double            rating_d, dev_d, vol_d;
    int64             matches;
    lp_id             stack_parts[64];
    lp_id            *parts = stack_parts;
    size_t            n_parts;
    bool              starts;
    bool              ends;
    bool              has_meta = false;
    bool              m_inv = false;
    hash128_t         m_canon = {0, 0};
    LaplaceEdgeRawRow row;
    MemoryContext     previous;

    if ((++ctx->rows_seen & 4095) == 0)
        CHECK_FOR_INTERRUPTS();

    d = slot_getattr(slot, 1, &isnull);
    if (isnull)
        return true;
    ord = DatumGetInt64(d);
    if (ord < 1 || ord > ctx->subj_count)
        return true;
    subj_b3 = ctx->subj_ids[ord - 1];

    d = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;
    entity = laplace_pub_blake3_to_hash128(d);

    d = slot_getattr(slot, 3, &isnull);
    if (isnull)
        return true;
    path = laplace_pub_geo_path(d);
    n_parts = lp_path_len(path);
    if (n_parts == 0)
        return true;
    if (n_parts > 64)
        parts = (lp_id *) palloc(sizeof(lp_id) * n_parts);
    n_parts = lp_path_expand(path, parts, n_parts);
    if (n_parts == 0)
    {
        if (parts != stack_parts) pfree(parts);
        return true;
    }

    starts = (memcmp(parts[0].b, &subj_b3, sizeof(hash128_t)) == 0);
    ends   = (memcmp(parts[n_parts - 1].b, &subj_b3, sizeof(hash128_t)) == 0);
    if (!starts && !ends)
    {
        if (parts != stack_parts) pfree(parts);
        return true;
    }

    if (n_parts == 3)
    {
        hash128_t rel_id;
        memcpy(&rel_id, parts[1].b, sizeof(hash128_t));
        has_meta = laplace_relation_metadata_lookup_c(&rel_id, &m_canon, NULL, &m_inv, NULL);
    }

    if (ctx->dir_mode == 1)
    {
        if (!((!m_inv && starts) || (m_inv && n_parts == 3 && ends)))
        {
            if (parts != stack_parts) pfree(parts);
            return true;
        }
    }
    else if (ctx->dir_mode == 2)
    {
        if (!((!m_inv && ends) || (m_inv && n_parts == 3 && starts)))
        {
            if (parts != stack_parts) pfree(parts);
            return true;
        }
    }

    d = slot_getattr(slot, 4, &isnull);
    rating_d = isnull ? 0.0 : DatumGetFloat8(d);
    d = slot_getattr(slot, 5, &isnull);
    dev_d = isnull ? 0.0 : DatumGetFloat8(d);
    d = slot_getattr(slot, 6, &isnull);
    vol_d = isnull ? 0.0 : DatumGetFloat8(d);
    d = slot_getattr(slot, 7, &isnull);
    matches = isnull ? 1 : DatumGetInt64(d);

    memset(&row, 0, sizeof(row));
    row.subject_id = subj_b3;
    row.entity     = entity;

    if (ctx->dir_mode == 1)
        row.dir = 0;
    else if (ctx->dir_mode == 2)
        row.dir = 1;
    else if (m_inv && n_parts == 3)
        row.dir = ends ? 0 : 1;
    else
        row.dir = starts ? 0 : 1;

    if (n_parts == 3)
    {
        memcpy(&row.relation_id, parts[1].b, sizeof(hash128_t));
        row.relation_null = false;
        row.type_id       = has_meta ? m_canon : row.relation_id;
        row.type_null     = false;
    }
    else
    {
        row.relation_null = true;
        row.type_null     = true;
    }

    if (ctx->has_type_filter)
    {
        if (row.type_null ||
            !hash128_sorted_contains(ctx->allowed_canon, ctx->n_allowed_canon, &row.type_id))
        {
            if (parts != stack_parts) pfree(parts);
            return true;
        }
    }

    if (ctx->dir_mode == 1)
    {
        if (m_inv && n_parts == 3)
            memcpy(&row.neighbour_id, parts[0].b, sizeof(hash128_t));
        else if (n_parts <= 3)
            memcpy(&row.neighbour_id, parts[n_parts - 1].b, sizeof(hash128_t));
        else
            row.neighbour_id = entity;
    }
    else if (ctx->dir_mode == 2)
    {
        if (m_inv && n_parts == 3)
            memcpy(&row.neighbour_id, parts[2].b, sizeof(hash128_t));
        else
            memcpy(&row.neighbour_id, parts[0].b, sizeof(hash128_t));
    }
    else if (m_inv && n_parts == 3)
    {
        memcpy(&row.neighbour_id, ends ? parts[0].b : parts[2].b, sizeof(hash128_t));
    }
    else if (starts)
    {
        if (n_parts <= 3)
            memcpy(&row.neighbour_id, parts[n_parts - 1].b, sizeof(hash128_t));
        else
            row.neighbour_id = entity;
    }
    else
    {
        memcpy(&row.neighbour_id, parts[0].b, sizeof(hash128_t));
    }
    row.neighbour_null = false;

    if (parts != stack_parts)
        pfree(parts);

    if (ctx->dir_mode == 3 && row.dir == 1 &&
        memcmp(&row.neighbour_id, &row.subject_id, sizeof(hash128_t)) == 0)
        return true;

    row.rating        = llround(rating_d * 1e9);
    row.rd            = llround(dev_d * 1e9);
    row.volatility    = llround(vol_d * 1e9);
    row.witness_count = matches > 1 ? matches : 1;

    if (!ctx->include_refuted && laplace_glicko2_refuted(row.rating, row.rd))
        return true;

    previous = MemoryContextSwitchTo(ctx->caller);
    dedup_insert_row(ctx->dedup, &row, true);
    MemoryContextSwitchTo(previous);
    return true;
}

static void
visit_stored_edge(const LaplaceConsensusRow *crow, void *context)
{
    StoredEdgesCtx   *ctx = (StoredEdgesCtx *) context;
    LaplaceEdgeRawRow row;
    MemoryContext     previous;

    if (crow->object_is_null)
        return;
    if (ctx->reverse && ctx->dir_mode == 3 &&
        memcmp(&crow->subject, &crow->object, sizeof(hash128_t)) == 0)
        return;
    if (!ctx->include_refuted && laplace_glicko2_refuted(crow->rating, crow->rd))
        return;

    memset(&row, 0, sizeof(row));
    row.subject_id    = ctx->reverse ? crow->object : crow->subject;
    row.dir           = ctx->reverse ? 1 : 0;
    row.type_id       = crow->type;
    row.neighbour_id  = ctx->reverse ? crow->subject : crow->object;
    row.relation_id   = crow->type;
    row.rating        = crow->rating;
    row.rd            = crow->rd;
    row.volatility    = crow->volatility;
    row.witness_count = crow->witnesses;

    previous = MemoryContextSwitchTo(ctx->caller);
    dedup_insert_row(ctx->dedup, &row, false);
    MemoryContextSwitchTo(previous);
}

typedef struct EdgeIsoDropEntry
{
    EdgeDedupKey key;
    hash128_t    rel_id;
} EdgeIsoDropEntry;

/* Remove untyped 3-part lexical sense tuples ([lemma, lang, ILI]) whose middle
 * entity is a pure ISO 639 language tag rather than a relation predicate. */
static void
er_filter_iso639_predicates(HTAB *dedup)
{
    long              n_total = hash_get_num_entries(dedup);
    EdgeIsoDropEntry *cands;
    int               n_cands = 0;
    hash128_t        *uniq_rels;
    int               n_rels = 0;
    bool             *is_lang;
    HASH_SEQ_STATUS   seq;
    EdgeDedupEntry   *entry;

    if (n_total <= 0)
        return;

    cands = (EdgeIsoDropEntry *) palloc(sizeof(EdgeIsoDropEntry) * (Size) n_total);
    uniq_rels = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_total);

    hash_seq_init(&seq, dedup);
    while ((entry = (EdgeDedupEntry *) hash_seq_search(&seq)) != NULL)
    {
        const hash128_t              *rid = &entry->row.relation_id;
        const laplace_relation_def_t *def = NULL;
        bool                          dup = false;

        if (entry->row.type_null || entry->row.relation_null)
            continue;
        if (laplace_relation_metadata_lookup_c(rid, NULL, NULL, NULL, NULL) ||
            (laplace_relation_lookup(rid, &def) == 0 && def != NULL))
            continue;

        cands[n_cands].key = entry->key;
        cands[n_cands].rel_id = *rid;
        n_cands++;

        for (int j = 0; j < n_rels; j++)
        {
            if (memcmp(&uniq_rels[j], rid, sizeof(hash128_t)) == 0)
            {
                dup = true;
                break;
            }
        }
        if (!dup)
            uniq_rels[n_rels++] = *rid;
    }

    if (n_rels > 0)
    {
        is_lang = (bool *) palloc0(sizeof(bool) * (Size) n_rels);
        laplace_are_iso639_lang_ids(uniq_rels, n_rels, is_lang);
        for (int i = 0; i < n_cands; i++)
        {
            for (int j = 0; j < n_rels; j++)
            {
                if (memcmp(&cands[i].rel_id, &uniq_rels[j], sizeof(hash128_t)) == 0)
                {
                    if (is_lang[j])
                        (void) hash_search(dedup, &cands[i].key, HASH_REMOVE, NULL);
                    break;
                }
            }
        }
        pfree(is_lang);
    }

    pfree(uniq_rels);
    pfree(cands);
}

static int
edge_rank_sort_cmp(const void *pa, const void *pb)
{
    const LaplaceEdgeRawRow *a = (const LaplaceEdgeRawRow *) pa;
    const LaplaceEdgeRawRow *b = (const LaplaceEdgeRawRow *) pb;
    int                      c;

    c = hash128_cmp(&a->subject_id, &b->subject_id);
    if (c != 0)
        return c;

    {
        bool na = isnan(a->score);
        bool nb = isnan(b->score);
        if (na != nb)
            return na ? 1 : -1; /* DESC NULLS LAST */
        if (!na && a->score != b->score)
            return (a->score > b->score) ? -1 : 1;
    }

    if (a->neighbour_null != b->neighbour_null)
        return a->neighbour_null ? 1 : -1; /* ASC NULLS LAST */
    if (!a->neighbour_null)
    {
        c = hash128_cmp(&a->neighbour_id, &b->neighbour_id);
        if (c != 0)
            return c;
    }

    if (a->type_null != b->type_null)
        return a->type_null ? 1 : -1; /* ASC NULLS LAST */
    if (!a->type_null)
    {
        c = hash128_cmp(&a->type_id, &b->type_id);
        if (c != 0)
            return c;
    }

    if (a->dir != b->dir)
        return a->dir < b->dir ? -1 : 1;

    return 0;
}

LaplaceEdgeRawRow *
laplace_edges_raw_batch_c(
    ArrayType  *subjects,
    const char *direction,
    ArrayType  *types,
    bool        has_limit,
    int32       limit,
    bool        include_refuted,
    const char *rank_mode,
    int        *out_count)
{
    MemoryContext      caller = CurrentMemoryContext;
    ArrayType         *subj_clean;
    ArrayType         *types_clean = NULL;
    int                subj_count = 0;
    bool               subj_empty = true;
    int                types_count = 0;
    bool               types_empty = false;
    int                dir_mode;
    bool               use_edge_rank;
    HASHCTL            ctl;
    HTAB              *dedup;
    long               total;
    LaplaceEdgeRawRow *rows;
    int                idx = 0;
    int                kept = 0;
    HASH_SEQ_STATUS    seq;
    EdgeDedupEntry    *entry;

    *out_count = 0;
    if (subjects == NULL)
        return NULL;
    if (has_limit && limit <= 0)
        return NULL;

    subj_clean = normalize_id_array(subjects, &subj_count, &subj_empty);
    if (subj_empty || subj_count == 0)
    {
        if (subj_clean != NULL)
            pfree(subj_clean);
        return NULL;
    }

    if (types != NULL)
        types_clean = normalize_id_array(types, &types_count, &types_empty);

    if (direction != NULL && strcmp(direction, "out") == 0)
        dir_mode = 1;
    else if (direction != NULL && strcmp(direction, "in") == 0)
        dir_mode = 2;
    else
        dir_mode = 3;

    use_edge_rank = (rank_mode != NULL && strcmp(rank_mode, "edge_rank") == 0);

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize   = sizeof(EdgeDedupKey);
    ctl.entrysize = sizeof(EdgeDedupEntry);
    ctl.hcxt      = caller;
    dedup = hash_create("edges_raw_batch dedup", 128, &ctl,
                        HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    /* Step A: Native claims (unless p_types was explicitly empty array). */
    if (!types_empty)
    {
        Oid            b3_arr_oid = laplace_pub_blake3_array_oid();
        Oid            argtypes[7] = { b3_arr_oid, INT8OID, INT2ARRAYOID, b3_arr_oid, INT2OID, b3_arr_oid, INT8OID };
        Datum          args[7];
        bool           argnulls[7] = { false, true, false, false, false, true, false };
        Datum         *s_elems;
        bool          *s_nulls;
        int            s_len = 0;
        hash128_t     *subj_ids;
        int            n_subj_ids = 0;
        ArrayType     *subj_b3_arr;
        ArrayType     *empty_b3_arr;
        Datum          claim_bit_datum;
        ArrayType     *bits_arr;
        bool           has_inverse = false;
        ArrayType     *tf_b3_types = NULL;
        hash128_t     *allowed_canon = NULL;
        int            n_allowed_canon = 0;
        int16          endpoint;
        NativeEdgesCtx nctx;

        deconstruct_array(subj_clean, BYTEAOID, -1, false, TYPALIGN_INT, &s_elems, &s_nulls, &s_len);
        subj_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) Max(s_len, 1));
        for (int i = 0; i < s_len; ++i)
        {
            if (!s_nulls[i])
                subj_ids[n_subj_ids++] = datum_to_hash128(s_elems[i]);
        }
        pfree(s_elems);
        pfree(s_nulls);

        if (n_subj_ids > 0)
        {
            if (types_clean != NULL)
            {
                Datum                  *t_elems;
                bool                   *t_nulls;
                int                     t_len = 0;
                hash128_t              *req_types;
                int                     n_req_types = 0;
                int                     n_surfs = 0;
                LaplaceRelationSurface *surfs;

                deconstruct_array(types_clean, BYTEAOID, -1, false, TYPALIGN_INT, &t_elems, &t_nulls, &t_len);
                req_types = (hash128_t *) palloc(sizeof(hash128_t) * (Size) Max(t_len, 1));
                for (int i = 0; i < t_len; ++i)
                {
                    if (!t_nulls[i])
                        req_types[n_req_types++] = datum_to_hash128(t_elems[i]);
                }
                pfree(t_elems);
                pfree(t_nulls);

                surfs = laplace_relation_surfaces_expand_c(req_types, n_req_types, &n_surfs);

                {
                    hash128_t *surf_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) Max(n_surfs, 1));
                    int        n_surf_uniq = 0;

                    allowed_canon = (hash128_t *) palloc(sizeof(hash128_t) * (Size) Max(n_req_types + n_surfs, 1));
                    for (int i = 0; i < n_req_types; ++i)
                        allowed_canon[n_allowed_canon++] = req_types[i];
                    for (int i = 0; i < n_surfs; ++i)
                    {
                        if (surfs[i].inverse)
                            has_inverse = true;
                        surf_ids[n_surf_uniq++] = surfs[i].surface;
                        allowed_canon[n_allowed_canon++] = surfs[i].canonical;
                    }
                    if (n_surf_uniq > 1)
                    {
                        int w = 1;
                        qsort(surf_ids, (Size) n_surf_uniq, sizeof(hash128_t), hash128_qsort_cmp);
                        for (int i = 1; i < n_surf_uniq; ++i)
                            if (memcmp(&surf_ids[w - 1], &surf_ids[i], sizeof(hash128_t)) != 0)
                                surf_ids[w++] = surf_ids[i];
                        n_surf_uniq = w;
                    }
                    if (n_allowed_canon > 1)
                    {
                        int w = 1;
                        qsort(allowed_canon, (Size) n_allowed_canon, sizeof(hash128_t), hash128_qsort_cmp);
                        for (int i = 1; i < n_allowed_canon; ++i)
                            if (memcmp(&allowed_canon[w - 1], &allowed_canon[i], sizeof(hash128_t)) != 0)
                                allowed_canon[w++] = allowed_canon[i];
                        n_allowed_canon = w;
                    }
                    tf_b3_types = laplace_pub_blake3_array(surf_ids, n_surf_uniq);
                    pfree(surf_ids);
                }
                if (surfs != NULL)
                    pfree(surfs);
                pfree(req_types);
            }

            subj_b3_arr = laplace_pub_blake3_array(subj_ids, n_subj_ids);
            empty_b3_arr = laplace_pub_blake3_array(NULL, 0);
            claim_bit_datum = Int16GetDatum(laplace_pub_bank_bit("kind", "claim"));
            bits_arr = construct_array(&claim_bit_datum, 1, INT2OID, sizeof(int16), true, TYPALIGN_SHORT);
            endpoint = (dir_mode == 3 || has_inverse) ? 3 : (dir_mode == 1 ? 1 : 2);

            args[0] = PointerGetDatum(subj_b3_arr);
            args[1] = (Datum) 0;
            args[2] = PointerGetDatum(bits_arr);
            args[3] = PointerGetDatum(empty_b3_arr);
            args[4] = Int16GetDatum(endpoint);
            args[5] = PointerGetDatum(tf_b3_types);
            argnulls[5] = (tf_b3_types == NULL);
            args[6] = Int64GetDatum(0);

            nctx.caller          = caller;
            nctx.dedup           = dedup;
            nctx.subj_ids        = subj_ids;
            nctx.subj_count      = n_subj_ids;
            nctx.allowed_canon   = allowed_canon;
            nctx.n_allowed_canon = n_allowed_canon;
            nctx.has_type_filter = (types_clean != NULL);
            nctx.dir_mode        = dir_mode;
            nctx.include_refuted = include_refuted;
            nctx.rows_seen       = 0;

            (void) laplace_pub_call_srf("public", "laplace_claims_each",
                                        7, argtypes, args, argnulls,
                                        visit_native_edge, &nctx);

            if (!nctx.has_type_filter && hash_get_num_entries(dedup) > 0)
                er_filter_iso639_predicates(dedup);

            pfree(subj_b3_arr);
            pfree(empty_b3_arr);
            pfree(bits_arr);
            if (tf_b3_types != NULL)
                pfree(tf_b3_types);
            if (allowed_canon != NULL)
                pfree(allowed_canon);
        }
        pfree(subj_ids);
    }

    /* Step B: Stored laplace.consensus fallback only when Step A yielded nothing. */
    if (hash_get_num_entries(dedup) == 0 && !types_empty && converse_legacy_consensus_has_rows())
    {
        StoredEdgesCtx sctx;

        sctx.caller          = caller;
        sctx.dedup           = dedup;
        sctx.dir_mode        = dir_mode;
        sctx.include_refuted = include_refuted;

        if (dir_mode == 1 || dir_mode == 3)
        {
            sctx.reverse = false;
            laplace_consensus_candidates_stored(subj_clean, NULL, types_clean,
                                                visit_stored_edge, &sctx);
        }
        if (dir_mode == 2 || dir_mode == 3)
        {
            sctx.reverse = true;
            laplace_consensus_candidates_stored(NULL, subj_clean, types_clean,
                                                visit_stored_edge, &sctx);
        }
    }

    total = hash_get_num_entries(dedup);
    if (total == 0)
    {
        hash_destroy(dedup);
        pfree(subj_clean);
        if (types_clean != NULL)
            pfree(types_clean);
        return NULL;
    }
    if (total > INT_MAX || (Size) total > MaxAllocSize / sizeof(LaplaceEdgeRawRow))
        ereport(ERROR, (errmsg("edges_raw_batch: result count exceeds allocation limit")));

    rows = (LaplaceEdgeRawRow *) MemoryContextAlloc(caller, (Size) total * sizeof(LaplaceEdgeRawRow));
    hash_seq_init(&seq, dedup);
    while ((entry = (EdgeDedupEntry *) hash_seq_search(&seq)) != NULL)
    {
        LaplaceEdgeRawRow row = entry->row;
        double            eff_fp = (double) ((__int128) row.rating - 2 * (__int128) row.rd);

        if (use_edge_rank)
        {
            double rk = 0.0;
            if (!row.type_null && laplace_relation_rank_resolve_c(row.type_id, &rk))
                row.score = rk * eff_fp;
            else
                row.score = 0.0;
        }
        else
        {
            row.score = eff_fp / 1e9;
        }
        rows[idx++] = row;
    }

    hash_destroy(dedup);
    pfree(subj_clean);
    if (types_clean != NULL)
        pfree(types_clean);

    qsort(rows, (Size) idx, sizeof(LaplaceEdgeRawRow), edge_rank_sort_cmp);

    if (has_limit)
    {
        int32 rn = 0;
        for (int i = 0; i < idx; ++i)
        {
            if (i == 0 || hash128_cmp(&rows[i].subject_id, &rows[i - 1].subject_id) != 0)
                rn = 1;
            else
                rn++;
            if (rn <= limit)
                rows[kept++] = rows[i];
        }
    }
    else
    {
        kept = idx;
    }

    *out_count = kept;
    return rows;
}

PG_FUNCTION_INFO_V1(pg_laplace_edges_raw_batch);

Datum
pg_laplace_edges_raw_batch(PG_FUNCTION_ARGS)
{
    ReturnSetInfo     *rsinfo;
    ArrayType         *subjects;
    char              *direction = NULL;
    ArrayType         *types = NULL;
    bool               has_limit;
    int32              limit = 0;
    bool               include_refuted;
    char              *rank_mode = NULL;
    LaplaceEdgeRawRow *rows;
    int                n_rows = 0;
    Datum              dir_out;
    Datum              dir_in;

    InitMaterializedSRF(fcinfo, 0);
    rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;

    if (PG_ARGISNULL(0))
        return (Datum) 0;

    subjects        = PG_GETARG_ARRAYTYPE_P(0);
    direction       = PG_ARGISNULL(1) ? NULL : text_to_cstring(PG_GETARG_TEXT_PP(1));
    types           = PG_ARGISNULL(2) ? NULL : PG_GETARG_ARRAYTYPE_P(2);
    has_limit       = !PG_ARGISNULL(3);
    if (has_limit)
        limit       = PG_GETARG_INT32(3);
    include_refuted = PG_ARGISNULL(4) ? false : PG_GETARG_BOOL(4);
    rank_mode       = PG_ARGISNULL(5) ? NULL : text_to_cstring(PG_GETARG_TEXT_PP(5));

    rows = laplace_edges_raw_batch_c(subjects, direction, types,
                                     has_limit, limit, include_refuted,
                                     rank_mode, &n_rows);

    if (direction != NULL)
        pfree(direction);
    if (rank_mode != NULL)
        pfree(rank_mode);

    if (rows == NULL || n_rows == 0)
        return (Datum) 0;

    dir_out = CStringGetTextDatum("out");
    dir_in  = CStringGetTextDatum("in");

    for (int i = 0; i < n_rows; ++i)
    {
        const LaplaceEdgeRawRow *r = &rows[i];
        Datum vals[9];
        bool  nulls[9];

        memset(nulls, 0, sizeof(nulls));
        vals[0] = hash128_to_datum(&r->subject_id);
        vals[1] = (r->dir == 0) ? dir_out : dir_in;
        if (r->type_null)
        {
            vals[2]  = (Datum) 0;
            nulls[2] = true;
        }
        else
        {
            vals[2] = hash128_to_datum(&r->type_id);
        }
        if (r->neighbour_null)
        {
            vals[3]  = (Datum) 0;
            nulls[3] = true;
        }
        else
        {
            vals[3] = hash128_to_datum(&r->neighbour_id);
        }
        vals[4] = Int64GetDatum(r->rating);
        vals[5] = Int64GetDatum(r->rd);
        vals[6] = Int64GetDatum(r->volatility);
        vals[7] = Int64GetDatum(r->witness_count);
        if (r->relation_null)
        {
            vals[8]  = (Datum) 0;
            nulls[8] = true;
        }
        else
        {
            vals[8] = hash128_to_datum(&r->relation_id);
        }

        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
    }

    pfree(rows);
    return (Datum) 0;
}
