/* Set read of the consensus face: every folded (subject, type, object) standing
 * cell whose endpoints and type lie in the caller's id sets, streamed to a
 * consumer. Partition routing and B-tree array scans go through PostgreSQL
 * access methods directly, with the caller's snapshot, ACL and RLS checks. */
#include "postgres.h"

#include <math.h>
#include <string.h>

#include "access/genam.h"
#include "access/nbtree.h"
#include "access/table.h"
#include "access/tableam.h"
#include "catalog/namespace.h"
#include "catalog/pg_am_d.h"
#include "catalog/pg_opfamily_d.h"
#include "catalog/pg_type.h"
#include "executor/executor.h"
#include "executor/tuptable.h"
#include "miscadmin.h"
#include "nodes/primnodes.h"
#include "partitioning/partbounds.h"
#include "partitioning/partdesc.h"
#include "utils/acl.h"
#include "utils/builtins.h"
#include "utils/fmgroids.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/partcache.h"
#include "utils/rel.h"
#include "utils/rls.h"
#include "utils/snapmgr.h"
#include "portability/instr_time.h"

#include "consensus_scan.h"
#include "converse_bindings.h"
#include "public_direct.h"
#include "relations_internal.h"
#include "spi_common.h"
#include "laplace/core/sql_catalog.h"
#include "laplace_pg/executor.h"

static int
cs_cmp_hash128(const void *a, const void *b)
{
    return memcmp(a, b, sizeof(hash128_t));
}

static bool
cs_sorted_has_id(const hash128_t *arr, int count, const hash128_t *id)
{
    int lo = 0, hi = count;
    while (lo < hi)
    {
        int mid = lo + (hi - lo) / 2;
        int c = memcmp(&arr[mid], id, sizeof(hash128_t));
        if (c < 0) lo = mid + 1;
        else if (c > 0) hi = mid;
        else return true;
    }
    return false;
}

static hash128_t *
cs_sort_dedup_ids(const hash128_t *in, int n, int *out_n)
{
    hash128_t *arr;
    int        w;

    *out_n = 0;
    if (in == NULL || n <= 0)
        return NULL;
    arr = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n);
    memcpy(arr, in, sizeof(hash128_t) * (Size) n);
    qsort(arr, (Size) n, sizeof(hash128_t), cs_cmp_hash128);
    w = 1;
    for (int i = 1; i < n; ++i)
    {
        if (memcmp(&arr[w - 1], &arr[i], sizeof(hash128_t)) != 0)
            arr[w++] = arr[i];
    }
    *out_n = w;
    return arr;
}

static hash128_t *
cs_extract_sorted_ids(ArrayType *arr, int *out_n)
{
    Datum     *elems;
    bool      *nulls;
    int        count;
    hash128_t *tmp;
    int        n = 0;
    hash128_t *res;

    *out_n = 0;
    if (arr == NULL)
        return NULL;
    deconstruct_array(arr, BYTEAOID, -1, false, TYPALIGN_INT, &elems, &nulls, &count);
    if (count == 0)
    {
        pfree(elems);
        pfree(nulls);
        return NULL;
    }
    tmp = (hash128_t *) palloc(sizeof(hash128_t) * (Size) count);
    for (int i = 0; i < count; ++i)
    {
        if (!nulls[i])
            tmp[n++] = datum_to_hash128(elems[i]);
    }
    pfree(elems);
    pfree(nulls);
    res = cs_sort_dedup_ids(tmp, n, out_n);
    pfree(tmp);
    return res;
}

typedef struct NativeCellItem
{
    LaplaceConsensusRow row;
    double              standing;
} NativeCellItem;

typedef struct NativeConsensusRead
{
    MemoryContext    caller;
    HTAB            *seen_entities;
    const hash128_t *s_ids;
    int              n_s;
    const hash128_t *o_ids;
    int              n_o;
    const hash128_t *allowed_canon;
    int              n_allowed_canon;
    bool             has_type_filter;
    bool             witnesses;
    NativeCellItem  *items;
    int              count;
    int              cap;
    uint64           rows_seen;
} NativeConsensusRead;

static int
cmp_native_cell_item(const void *pa, const void *pb)
{
    const NativeCellItem *a = (const NativeCellItem *) pa;
    const NativeCellItem *b = (const NativeCellItem *) pb;
    bool na = isnan(a->standing);
    bool nb = isnan(b->standing);

    if (na != nb)
        return na ? -1 : 1; /* PG float8 DESC puts NaN first */
    if (!na && a->standing != b->standing)
        return (a->standing > b->standing) ? -1 : 1;
    return memcmp(&a->row.claim, &b->row.claim, sizeof(hash128_t));
}

static bool
visit_native_consensus_claim(TupleTableSlot *slot, void *context)
{
    NativeConsensusRead *read = (NativeConsensusRead *) context;
    bool                 isnull = false;
    Datum                d_ent, d_path, d_rat, d_dev, d_vol, d_mat;
    hash128_t            entity;
    bool                 seen = false;
    lp_path              path;
    size_t               n_parts;
    lp_id                parts[4];
    hash128_t            p1, p2, p3;
    bool                 has_meta = false, m_inv = false;
    hash128_t            m_canon = {0, 0};
    hash128_t            subj_id, type_id, obj_id;
    double               raw_rat, raw_dev, raw_vol;
    int64                matches;
    MemoryContext        prev;

    if ((++read->rows_seen & 4095) == 0)
        CHECK_FOR_INTERRUPTS();

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    entity = laplace_pub_blake3_to_hash128(d_ent);

    prev = MemoryContextSwitchTo(read->caller);
    (void) hash_search(read->seen_entities, &entity, HASH_ENTER, &seen);
    MemoryContextSwitchTo(prev);
    if (seen)
        return true;

    d_path = slot_getattr(slot, 3, &isnull);
    if (isnull) return true;
    path = laplace_pub_geo_path(d_path);
    n_parts = lp_path_len(path);
    if (n_parts != 3)
        return true;
    if (lp_path_expand(path, parts, 3) != 3)
        return true;

    memcpy(&p1, parts[0].b, sizeof(hash128_t));
    memcpy(&p2, parts[1].b, sizeof(hash128_t));
    memcpy(&p3, parts[2].b, sizeof(hash128_t));

    has_meta = laplace_relation_metadata_lookup_c(&p2, &m_canon, NULL, &m_inv, NULL);
    subj_id = m_inv ? p3 : p1;
    type_id = has_meta ? m_canon : p2;
    obj_id  = m_inv ? p1 : p3;

    if (read->witnesses && read->s_ids != NULL && read->o_ids != NULL)
    {
        if (!cs_sorted_has_id(read->s_ids, read->n_s, &subj_id) &&
            !cs_sorted_has_id(read->o_ids, read->n_o, &obj_id))
            return true;
    }
    else
    {
        if (read->s_ids != NULL && !cs_sorted_has_id(read->s_ids, read->n_s, &subj_id))
            return true;
        if (read->o_ids != NULL && !cs_sorted_has_id(read->o_ids, read->n_o, &obj_id))
            return true;
    }

    if (read->has_type_filter &&
        !cs_sorted_has_id(read->allowed_canon, read->n_allowed_canon, &type_id))
        return true;

    d_rat = slot_getattr(slot, 4, &isnull);
    if (isnull) elog(ERROR, "Native consensus projection has a null rating");
    raw_rat = DatumGetFloat8(d_rat);

    d_dev = slot_getattr(slot, 5, &isnull);
    if (isnull) elog(ERROR, "Native consensus projection has a null deviation");
    raw_dev = DatumGetFloat8(d_dev);

    d_vol = slot_getattr(slot, 6, &isnull);
    if (isnull) elog(ERROR, "Native consensus projection has a null volatility");
    raw_vol = DatumGetFloat8(d_vol);

    d_mat = slot_getattr(slot, 7, &isnull);
    if (isnull) elog(ERROR, "Native consensus projection has a null matches");
    matches = DatumGetInt64(d_mat);

    prev = MemoryContextSwitchTo(read->caller);
    if (read->count == read->cap)
    {
        int new_cap = read->cap == 0 ? 64 : read->cap * 2;
        read->items = read->items == NULL
            ? (NativeCellItem *) palloc(sizeof(NativeCellItem) * (Size) new_cap)
            : (NativeCellItem *) repalloc(read->items, sizeof(NativeCellItem) * (Size) new_cap);
        read->cap = new_cap;
    }
    read->items[read->count++] = (NativeCellItem){
        .row = (LaplaceConsensusRow){
            .subject      = subj_id,
            .type         = type_id,
            .object       = obj_id,
            .rating       = llround(raw_rat * 1e9),
            .rd           = llround(raw_dev * 1e9),
            .volatility   = llround(raw_vol * 1e9),
            .witnesses    = matches > 1 ? matches : 1,
            .object_is_null = false,
            .claim        = entity,
            .native_claim = true
        },
        .standing = raw_rat - 2.0 * raw_dev
    };
    MemoryContextSwitchTo(prev);
    return true;
}

/* Remove untyped 3-part lexical sense tuples ([lemma, lang, ILI]) whose middle
 * entity is a pure ISO 639 language tag rather than a relation predicate. */
static void
cs_filter_iso639_predicates(NativeConsensusRead *read)
{
    hash128_t *cand_ids;
    int        n_cand = 0;
    bool      *is_lang;
    int        out = 0;

    if (read->has_type_filter || read->count <= 0)
        return;

    cand_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) read->count);
    for (int i = 0; i < read->count; i++)
    {
        const hash128_t              *tid = &read->items[i].row.type;
        const laplace_relation_def_t *def = NULL;
        bool                          dup = false;

        if (laplace_relation_metadata_lookup_c(tid, NULL, NULL, NULL, NULL) ||
            (laplace_relation_lookup(tid, &def) == 0 && def != NULL))
            continue;
        for (int j = 0; j < n_cand; j++)
        {
            if (memcmp(&cand_ids[j], tid, sizeof(hash128_t)) == 0)
            {
                dup = true;
                break;
            }
        }
        if (!dup)
            cand_ids[n_cand++] = *tid;
    }

    if (n_cand == 0)
    {
        pfree(cand_ids);
        return;
    }

    is_lang = (bool *) palloc0(sizeof(bool) * (Size) n_cand);
    laplace_are_iso639_lang_ids(cand_ids, n_cand, is_lang);

    for (int i = 0; i < read->count; i++)
    {
        const hash128_t *tid = &read->items[i].row.type;
        bool             drop = false;

        for (int j = 0; j < n_cand; j++)
        {
            if (memcmp(&cand_ids[j], tid, sizeof(hash128_t)) == 0)
            {
                drop = is_lang[j];
                break;
            }
        }
        if (!drop)
        {
            if (out != i)
                read->items[out] = read->items[i];
            out++;
        }
    }
    read->count = out;

    pfree(is_lang);
    pfree(cand_ids);
}

/* The legacy cell layout is only a transport projection for typed Native
 * claims. Source value claims remain available through the claim readers;
 * they do not acquire an invented predicate in the binary relation graph. */
static void
scan_native(ArrayType *subjects, ArrayType *objects, ArrayType *types, bool witnesses,
            LaplaceConsensusConsumer consume, void *context,
            LaplaceConsensusScanStats *stats)
{
    MemoryContext       caller = CurrentMemoryContext;
    hash128_t          *s_ids = NULL, *o_ids = NULL, *t_ids = NULL;
    int                 n_s = 0, n_o = 0, n_t = 0;
    bool                has_inverse = false;
    hash128_t          *allowed_canon = NULL;
    int                 n_allowed_canon = 0;
    ArrayType          *k_types = NULL;
    const hash128_t    *k_ids = NULL;
    int                 n_k_ids = 0;
    const hash128_t    *k_opp = NULL;
    int                 n_k_opp = 0;
    bool                k_has_inverse;
    bool                s_diff_o;
    bool                choose_o;
    ArrayType          *ids_b3_arr;
    ArrayType          *empty_b3_arr;
    ArrayType          *opp_b3_arr = NULL;
    Datum               claim_bit_datum;
    ArrayType          *bits_arr;
    Oid                 b3_arr_oid = laplace_pub_blake3_array_oid();
    Oid                 argtypes[8] = {
        b3_arr_oid, INT8OID, INT2ARRAYOID, b3_arr_oid, INT2OID, b3_arr_oid, INT8OID, b3_arr_oid
    };
    Datum               args[8];
    bool                argnulls[8] = { false, true, false, false, false, false, false, false };
    HASHCTL             hctl;
    NativeConsensusRead read;
    instr_time          started, elapsed;

    s_ids = cs_extract_sorted_ids(subjects, &n_s);
    o_ids = cs_extract_sorted_ids(objects, &n_o);

    if (types != NULL)
    {
        t_ids = cs_extract_sorted_ids(types, &n_t);
        if (n_t > 0)
        {
            int                     n_surfs = 0;
            LaplaceRelationSurface *surfs = laplace_relation_surfaces_expand_c(t_ids, n_t, &n_surfs);
            hash128_t              *surf_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) Max(n_surfs, 1));
            hash128_t              *canon_raw = (hash128_t *) palloc(sizeof(hash128_t) * (Size) (n_t + n_surfs));
            int                     n_surf_raw = 0, n_canon_raw = 0;
            int                     n_surf_uniq = 0;
            hash128_t              *surf_uniq;

            for (int i = 0; i < n_t; ++i)
                canon_raw[n_canon_raw++] = t_ids[i];
            for (int i = 0; i < n_surfs; ++i)
            {
                if (surfs[i].inverse)
                    has_inverse = true;
                surf_ids[n_surf_raw++] = surfs[i].surface;
                canon_raw[n_canon_raw++] = surfs[i].canonical;
            }
            surf_uniq = cs_sort_dedup_ids(surf_ids, n_surf_raw, &n_surf_uniq);
            k_types = laplace_pub_blake3_array(surf_uniq, n_surf_uniq);
            allowed_canon = cs_sort_dedup_ids(canon_raw, n_canon_raw, &n_allowed_canon);
            if (surf_uniq) pfree(surf_uniq);
            pfree(surf_ids);
            pfree(canon_raw);
            if (surfs) pfree(surfs);
        }
        else
        {
            k_types = laplace_pub_blake3_array(NULL, 0);
        }
    }
    else
    {
        has_inverse = true;
    }

    s_diff_o = (s_ids != NULL) != (o_ids != NULL) ||
               (s_ids != NULL && (n_s != n_o || memcmp(s_ids, o_ids, sizeof(hash128_t) * (Size) n_s) != 0));
    choose_o = (s_ids != NULL && o_ids != NULL && !witnesses && has_inverse && s_diff_o && n_o < n_s);

    if (choose_o)
    {
        k_ids   = o_ids;
        n_k_ids = n_o;
    }
    else if (s_ids != NULL)
    {
        k_ids   = s_ids;
        n_k_ids = n_s;
    }
    else
    {
        k_ids   = o_ids;
        n_k_ids = n_o;
    }

    if (s_ids == NULL || o_ids == NULL || witnesses || (has_inverse && s_diff_o))
    {
        k_opp   = NULL;
        n_k_opp = 0;
    }
    else if (has_inverse && n_o < n_s)
    {
        k_opp   = s_ids;
        n_k_opp = n_s;
    }
    else
    {
        k_opp   = o_ids;
        n_k_opp = n_o;
    }

    k_has_inverse = (witnesses && s_ids != NULL && o_ids != NULL) ||
                    (has_inverse && (s_ids == NULL || o_ids == NULL || witnesses || s_diff_o));

    ids_b3_arr   = laplace_pub_blake3_array(k_ids, n_k_ids);
    empty_b3_arr = laplace_pub_blake3_array(NULL, 0);
    if (k_opp != NULL)
        opp_b3_arr = laplace_pub_blake3_array(k_opp, n_k_opp);
    claim_bit_datum = Int16GetDatum(laplace_pub_bank_bit("kind", "claim"));
    bits_arr = construct_array(&claim_bit_datum, 1, INT2OID, sizeof(int16), true, TYPALIGN_SHORT);

    args[0] = PointerGetDatum(ids_b3_arr);
    args[1] = (Datum) 0;
    args[2] = PointerGetDatum(bits_arr);
    args[3] = PointerGetDatum(empty_b3_arr);
    args[4] = Int16GetDatum(k_has_inverse ? 3 : (s_ids != NULL ? 1 : 2));
    args[5] = PointerGetDatum(k_types);
    argnulls[5] = (k_types == NULL);
    args[6] = Int64GetDatum(3);
    args[7] = PointerGetDatum(opp_b3_arr);
    argnulls[7] = (opp_b3_arr == NULL);

    memset(&hctl, 0, sizeof(hctl));
    hctl.keysize   = sizeof(hash128_t);
    hctl.entrysize = sizeof(hash128_t);
    hctl.hcxt      = caller;

    memset(&read, 0, sizeof(read));
    read.caller          = caller;
    read.seen_entities   = hash_create("native consensus cells", 128, &hctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    read.s_ids           = s_ids;
    read.n_s             = n_s;
    read.o_ids           = o_ids;
    read.n_o             = n_o;
    read.allowed_canon   = allowed_canon;
    read.n_allowed_canon = n_allowed_canon;
    read.has_type_filter = (types != NULL);
    read.witnesses       = witnesses;

    INSTR_TIME_SET_CURRENT(started);
    elog(DEBUG1, "Native consensus cells: subjects %d; objects %d; types %d; witnesses %s",
         n_s, n_o, types ? ArrayGetNItems(ARR_NDIM(types), ARR_DIMS(types)) : -1,
         witnesses ? "yes" : "no");
    (void) laplace_pub_call_srf("public", "laplace_claims_each", 8, argtypes, args, argnulls,
                                visit_native_consensus_claim, &read);
    cs_filter_iso639_predicates(&read);
    if (read.count > 1)
        qsort(read.items, (Size) read.count, sizeof(NativeCellItem), cmp_native_cell_item);
    for (int i = 0; i < read.count; ++i)
    {
        stats->rows_read++;
        stats->rows_matched++;
        consume(&read.items[i].row, context);
    }
    INSTR_TIME_SET_CURRENT(elapsed);
    INSTR_TIME_SUBTRACT(elapsed, started);
    elog(DEBUG1, "Native consensus cells: %d rows; %.3f ms",
         read.count, INSTR_TIME_GET_MILLISEC(elapsed));

    hash_destroy(read.seen_entities);
    if (read.items) pfree(read.items);
    pfree(ids_b3_arr);
    pfree(empty_b3_arr);
    pfree(bits_arr);
    if (opp_b3_arr) pfree(opp_b3_arr);
    if (k_types) pfree(k_types);
    if (allowed_canon) pfree(allowed_canon);
    if (s_ids) pfree(s_ids);
    if (o_ids) pfree(o_ids);
    if (t_ids) pfree(t_ids);
}

typedef struct ClassBankCacheEntry
{
    hash128_t id;
    bool      match;
} ClassBankCacheEntry;

typedef struct ClassificationRead
{
    MemoryContext           caller;
    const char             *bank;
    HTAB                   *bank_cache;
    LaplaceClassificationRow *items;
    int                     count;
    int                     cap;
} ClassificationRead;

static int
cmp_classification_row(const void *pa, const void *pb)
{
    const LaplaceClassificationRow *a = (const LaplaceClassificationRow *) pa;
    const LaplaceClassificationRow *b = (const LaplaceClassificationRow *) pb;
    return memcmp(&a->claim, &b->claim, sizeof(hash128_t));
}

static bool
visit_classification_claim(TupleTableSlot *slot, void *context)
{
    ClassificationRead  *read = (ClassificationRead *) context;
    bool                 isnull = false;
    Datum                d_ent, d_path, d_rat, d_dev, d_vol, d_mat;
    int64                matches;
    lp_path              path;
    lp_id                parts[2];
    hash128_t            subj, val, entity;
    ClassBankCacheEntry *ce;
    bool                 found = false;
    double               raw_rat, raw_dev, raw_vol;
    MemoryContext        prev;

    d_mat = slot_getattr(slot, 7, &isnull);
    if (isnull) return true;
    matches = DatumGetInt64(d_mat);
    if (matches <= 0)
        return true;

    d_path = slot_getattr(slot, 3, &isnull);
    if (isnull) return true;
    path = laplace_pub_geo_path(d_path);
    if (lp_path_len(path) != 2 || lp_path_expand(path, parts, 2) != 2)
        return true;

    memcpy(&subj, parts[0].b, sizeof(hash128_t));
    memcpy(&val, parts[1].b, sizeof(hash128_t));

    ce = (ClassBankCacheEntry *) hash_search(read->bank_cache, &val, HASH_ENTER, &found);
    if (!found)
    {
        Oid   b3_oid = laplace_pub_blake3_oid();
        Datum val_arg = laplace_pub_hash128_to_blake3(&val);
        bool  val_null = false;
        bool  rec_null = false;
        Datum rec_d = laplace_pub_call("public", "laplace_bank_of", 1, &b3_oid, &val_arg, &val_null, &rec_null);
        bool  match = false;

        if (!rec_null)
        {
            HeapTupleHeader hdr = DatumGetHeapTupleHeader(rec_d);
            bool            b_null = false;
            Datum           b_datum = GetAttributeByNum(hdr, 1, &b_null);
            if (!b_null)
            {
                char *b_str = TextDatumGetCString(b_datum);
                match = (strcmp(b_str, read->bank) == 0);
                pfree(b_str);
            }
        }
        ce->match = match;
    }
    if (!ce->match)
        return true;

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull) elog(ERROR, "Native classification has a null entity");
    entity = laplace_pub_blake3_to_hash128(d_ent);

    d_rat = slot_getattr(slot, 4, &isnull);
    if (isnull) elog(ERROR, "Native classification has a null rating");
    raw_rat = DatumGetFloat8(d_rat);

    d_dev = slot_getattr(slot, 5, &isnull);
    if (isnull) elog(ERROR, "Native classification has a null deviation");
    raw_dev = DatumGetFloat8(d_dev);

    d_vol = slot_getattr(slot, 6, &isnull);
    if (isnull) elog(ERROR, "Native classification has a null volatility");
    raw_vol = DatumGetFloat8(d_vol);

    prev = MemoryContextSwitchTo(read->caller);
    if (read->count == read->cap)
    {
        int new_cap = read->cap == 0 ? 64 : read->cap * 2;
        read->items = read->items == NULL
            ? (LaplaceClassificationRow *) palloc(sizeof(LaplaceClassificationRow) * (Size) new_cap)
            : (LaplaceClassificationRow *) repalloc(read->items, sizeof(LaplaceClassificationRow) * (Size) new_cap);
        read->cap = new_cap;
    }
    read->items[read->count++] = (LaplaceClassificationRow){
        .subject    = subj,
        .value      = val,
        .rating     = llround(raw_rat * 1e9),
        .rd         = llround(raw_dev * 1e9),
        .volatility = llround(raw_vol * 1e9),
        .games      = matches,
        .claim      = entity
    };
    MemoryContextSwitchTo(prev);
    return true;
}

void
laplace_classification_scan(ArrayType *subjects, const char *bank,
                            LaplaceClassificationConsumer consume, void *context)
{
    MemoryContext      caller = CurrentMemoryContext;
    hash128_t         *s_ids;
    int                n_s = 0;
    ArrayType         *ids_b3_arr;
    ArrayType         *empty_b3_arr;
    Datum              claim_bit_datum;
    ArrayType         *bits_arr;
    Oid                b3_arr_oid = laplace_pub_blake3_array_oid();
    Oid                argtypes[7] = {
        b3_arr_oid, INT8OID, INT2ARRAYOID, b3_arr_oid, INT2OID, b3_arr_oid, INT8OID
    };
    Datum              args[7];
    bool               argnulls[7] = { false, true, false, false, false, true, false };
    HASHCTL            hctl;
    ClassificationRead read;

    if (!subjects || !ArrayGetNItems(ARR_NDIM(subjects), ARR_DIMS(subjects)))
        return;
    if (!bank || !*bank || !consume)
        elog(ERROR, "Native classification requires its bank and consumer");

    s_ids = cs_extract_sorted_ids(subjects, &n_s);
    if (n_s == 0)
    {
        if (s_ids) pfree(s_ids);
        return;
    }

    ids_b3_arr      = laplace_pub_blake3_array(s_ids, n_s);
    empty_b3_arr    = laplace_pub_blake3_array(NULL, 0);
    claim_bit_datum = Int16GetDatum(laplace_pub_bank_bit("kind", "claim"));
    bits_arr        = construct_array(&claim_bit_datum, 1, INT2OID, sizeof(int16), true, TYPALIGN_SHORT);

    args[0] = PointerGetDatum(ids_b3_arr);
    args[1] = (Datum) 0;
    args[2] = PointerGetDatum(bits_arr);
    args[3] = PointerGetDatum(empty_b3_arr);
    args[4] = Int16GetDatum(1);
    args[5] = (Datum) 0;
    args[6] = Int64GetDatum(2);

    memset(&hctl, 0, sizeof(hctl));
    hctl.keysize   = sizeof(hash128_t);
    hctl.entrysize = sizeof(ClassBankCacheEntry);
    hctl.hcxt      = caller;

    memset(&read, 0, sizeof(read));
    read.caller     = caller;
    read.bank       = bank;
    read.bank_cache = hash_create("native classification banks", 64, &hctl,
                                  HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    (void) laplace_pub_call_srf("public", "laplace_claims_each", 7, argtypes, args, argnulls,
                                visit_classification_claim, &read);

    if (read.count > 1)
        qsort(read.items, (Size) read.count, sizeof(LaplaceClassificationRow), cmp_classification_row);
    for (int i = 0; i < read.count; ++i)
        consume(&read.items[i], context);

    hash_destroy(read.bank_cache);
    if (read.items) pfree(read.items);
    pfree(ids_b3_arr);
    pfree(empty_b3_arr);
    pfree(bits_arr);
    pfree(s_ids);
}

typedef struct ScanId { char bytes[16]; } ScanId;
typedef struct ScanSet
{
    ArrayType *array;
    Datum *values;
    int count;
    HTAB *ids;
} ScanSet;

static void
scan_set_init(ScanSet *set, ArrayType *array)
{
    HASHCTL ctl = {0};
    Datum *values;
    bool *nulls;
    int count;

    memset(set, 0, sizeof(*set));
    if (array == NULL) return;
    if (ARR_ELEMTYPE(array) != BYTEAOID || ARR_NDIM(array) > 1)
        ereport(ERROR, (errmsg("consensus scan requires one-dimensional identity arrays")));
    ctl.keysize = sizeof(ScanId);
    ctl.entrysize = sizeof(ScanId);
    ctl.hcxt = CurrentMemoryContext;
    set->ids = hash_create("consensus scan identities", 64, &ctl,
                          HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    deconstruct_array(array, BYTEAOID, -1, false, TYPALIGN_INT,
                      &values, &nulls, &count);
    set->values = palloc(sizeof(Datum) * Max(count, 1));
    for (int i = 0; i < count; ++i)
    {
        bytea *id;
        bool found;
        if (nulls[i]) continue;
        id = DatumGetByteaPP(values[i]);
        if (VARSIZE_ANY_EXHDR(id) != 16)
            ereport(ERROR, (errmsg("consensus scan identities must contain exactly 16 bytes")));
        hash_search(set->ids, VARDATA_ANY(id), HASH_ENTER, &found);
        if (!found) set->values[set->count++] = PointerGetDatum(id);
    }
    set->array = set->count == 0 ? construct_empty_array(BYTEAOID) :
        construct_array(set->values, set->count, BYTEAOID, -1, false, TYPALIGN_INT);
    pfree(values);
    pfree(nulls);
}

static bool
scan_set_contains(const ScanSet *set, Datum value, bool isnull)
{
    bytea *id;
    if (set->array == NULL) return true;
    if (isnull) return false;
    id = DatumGetByteaPP(value);
    return VARSIZE_ANY_EXHDR(id) == 16 &&
        hash_search(set->ids, VARDATA_ANY(id), HASH_FIND, NULL) != NULL;
}

static void
scan_set_destroy(ScanSet *set)
{
    if (set->ids != NULL) hash_destroy(set->ids);
    if (set->array != NULL) pfree(set->array);
    if (set->values != NULL) pfree(set->values);
}

/* Partition metadata is read per statement, so leaf selection always follows
 * the current partition topology. */
static List *
scan_children(Relation relation, const ScanSet *subjects, const ScanSet *types)
{
    PartitionKey key = RelationGetPartitionKey(relation);
    PartitionDesc desc = RelationGetPartitionDesc(relation, true);
    const ScanSet *set = NULL;
    bool *selected = palloc0(sizeof(bool) * Max(desc->nparts, 1));
    List *children = NIL;

    if (key != NULL && key->partnatts == 1 && key->partattrs[0] > 0 &&
        key->parttypid[0] == BYTEAOID)
    {
        const char *name = NameStr(TupleDescAttr(RelationGetDescr(relation),
                                               key->partattrs[0] - 1)->attname);
        if (strcmp(name, "subject_id") == 0) set = subjects;
        else if (strcmp(name, "type_id") == 0) set = types;
    }
    if (set != NULL && set->array != NULL &&
        (key->strategy == PARTITION_STRATEGY_LIST ||
         key->strategy == PARTITION_STRATEGY_HASH))
    {
        for (int i = 0; i < set->count; ++i)
        {
            int part;
            if (key->strategy == PARTITION_STRATEGY_LIST)
            {
                bool equal;
                int at = partition_list_bsearch(key->partsupfunc,
                    key->partcollation, desc->boundinfo, set->values[i], &equal);
                part = equal ? desc->boundinfo->indexes[at] :
                               desc->boundinfo->default_index;
            }
            else
            {
                bool isnull = false;
                uint64 hash = compute_partition_hash_value(1, key->partsupfunc,
                    key->partcollation, &set->values[i], &isnull);
                part = desc->boundinfo->indexes[hash % desc->boundinfo->nindexes];
            }
            if (part >= 0 && part < desc->nparts) selected[part] = true;
        }
    }
    else
        memset(selected, true, sizeof(bool) * desc->nparts);

    for (int i = 0; i < desc->nparts; ++i)
        if (selected[i]) children = lappend_oid(children, desc->oids[i]);
    pfree(selected);
    return children;
}

static AttrNumber
scan_attribute(Relation relation, const char *name, Oid expected)
{
    TupleDesc desc = RelationGetDescr(relation);
    for (int i = 0; i < desc->natts; ++i)
    {
        Form_pg_attribute attr = TupleDescAttr(desc, i);
        if (!attr->attisdropped && strcmp(NameStr(attr->attname), name) == 0)
        {
            if (attr->atttypid != expected)
                ereport(ERROR, (errmsg("consensus scan: column %s has an incompatible type", name)));
            return i + 1;
        }
    }
    ereport(ERROR, (errmsg("consensus scan: column %s is missing", name)));
    return InvalidAttrNumber;
}

static bool
scan_index_predicate(Relation index, AttrNumber object, bool object_required)
{
    List *predicate = RelationGetIndexPredicate(index);
    NullTest *test;
    Var *var;
    if (predicate == NIL) return true;
    /* An equality/array probe of object proves object IS NOT NULL. Accept
     * precisely that installed partial-index predicate, never an arbitrary
     * partial index whose omitted rows might satisfy this operation. */
    if (!object_required || list_length(predicate) != 1 ||
        !IsA(linitial(predicate), NullTest)) return false;
    test = linitial_node(NullTest, predicate);
    if (test->nulltesttype != IS_NOT_NULL || test->argisrow ||
        !IsA(test->arg, Var)) return false;
    var = (Var *) test->arg;
    return var->varattno == object && var->vartype == BYTEAOID &&
           var->varlevelsup == 0;
}

static Relation
scan_index(Relation relation, AttrNumber endpoint,
           AttrNumber object, bool object_required,
           AttrNumber type, bool type_required)
{
    List *indexes = RelationGetIndexList(relation);
    ListCell *cell;
    Relation selected = NULL;
    int selected_prefix = 0;
    foreach(cell, indexes)
    {
        Relation index = index_open(lfirst_oid(cell), AccessShareLock);
        if (index->rd_rel->relam == BTREE_AM_OID &&
            index->rd_index->indisvalid && index->rd_index->indisready &&
            index->rd_index->indnkeyatts > 0 &&
            index->rd_index->indkey.values[0] == endpoint &&
            scan_index_predicate(index, object, object_required))
        {
            int prefix = 1;
            /* Push the complete constrained prefix into the B-tree. Steering
             * binds both endpoints; neighborhood reads may bind a relation
             * family. Neither should fetch the excluded heap rows first. */
            while (prefix < index->rd_index->indnkeyatts)
            {
                AttrNumber attr = index->rd_index->indkey.values[prefix];
                if ((attr == object && object_required) ||
                    (attr == type && type_required))
                    prefix++;
                else
                    break;
            }
            if (prefix > selected_prefix)
            {
                if (selected != NULL) index_close(selected, AccessShareLock);
                selected = index;
                selected_prefix = prefix;
                continue;
            }
        }
        index_close(index, AccessShareLock);
    }
    list_free(indexes);
    if (selected == NULL)
        ereport(ERROR, (errmsg("consensus scan: %s lacks its endpoint btree index",
                                RelationGetRelationName(relation))));
    return selected;
}

/* The ordered access path is recognized by its rating - 2*rd (effective mu)
 * expression, not by index name. */
static bool
scan_eff_mu_expression(Node *node, AttrNumber rating, AttrNumber rd)
{
    OpExpr *minus, *times;
    Var *rating_var, *rd_var;
    Const *two;
    Oid multiply;
    if (!IsA(node, OpExpr)) return false;
    minus = (OpExpr *) node;
    if (list_length(minus->args) != 2 || !IsA(linitial(minus->args), Var) ||
        !IsA(lsecond(minus->args), OpExpr)) return false;
    if (get_opcode(minus->opno) != F_INT8MI) return false;
    rating_var = linitial_node(Var, minus->args);
    times = lsecond_node(OpExpr, minus->args);
    if (list_length(times->args) != 2 || !IsA(linitial(times->args), Const) ||
        !IsA(lsecond(times->args), Var)) return false;
    multiply = get_opcode(times->opno);
    if (multiply != F_INT48MUL && multiply != F_INT8MUL) return false;
    two = linitial_node(Const, times->args);
    rd_var = lsecond_node(Var, times->args);
    return !two->constisnull &&
        ((two->consttype == INT4OID && DatumGetInt32(two->constvalue) == 2) ||
         (two->consttype == INT8OID && DatumGetInt64(two->constvalue) == 2)) &&
        rating_var->varattno == rating && rating_var->vartype == INT8OID &&
        rating_var->varlevelsup == 0 && rd_var->varattno == rd &&
        rd_var->vartype == INT8OID && rd_var->varlevelsup == 0;
}

static Relation
scan_rank_index(Relation relation, AttrNumber endpoint, AttrNumber object,
                AttrNumber rating, AttrNumber rd)
{
    List *indexes = RelationGetIndexList(relation);
    ListCell *cell;
    Relation selected = NULL;
    foreach(cell, indexes)
    {
        Relation index = index_open(lfirst_oid(cell), AccessShareLock);
        List *expressions;
        if (index->rd_rel->relam == BTREE_AM_OID &&
            index->rd_index->indisvalid && index->rd_index->indisready &&
            index->rd_index->indnkeyatts >= 2 &&
            index->rd_index->indkey.values[0] == endpoint &&
            index->rd_index->indkey.values[1] == 0 &&
            index->rd_opfamily[0] == BYTEA_BTREE_FAM_OID &&
            index->rd_opfamily[1] == INTEGER_BTREE_FAM_OID &&
            (index->rd_indoption[1] & INDOPTION_DESC) != 0 &&
            scan_index_predicate(index, object, true))
        {
            expressions = RelationGetIndexExpressions(index);
            if (expressions != NIL && scan_eff_mu_expression(linitial(expressions), rating, rd))
            {
                selected = index;
                break;
            }
        }
        index_close(index, AccessShareLock);
    }
    list_free(indexes);
    return selected;
}

#include "consensus_plane_scan.h"

static void
scan_leaf(Relation relation, const ScanSet *subjects, const ScanSet *objects,
          const ScanSet *types, LaplaceConsensusConsumer consume, void *context,
          LaplaceConsensusScanStats *stats, LaplaceConsensusCutoff cutoff,
          bool plane_local)
{
    if (RelationGetNumberOfBlocks(relation) == 0)
        return;
    AttrNumber subject = scan_attribute(relation, "subject_id", BYTEAOID);
    AttrNumber object = scan_attribute(relation, "object_id", BYTEAOID);
    AttrNumber type = scan_attribute(relation, "type_id", BYTEAOID);
    AttrNumber rating = scan_attribute(relation, "rating", INT8OID);
    AttrNumber rd = scan_attribute(relation, "rd", INT8OID);
    AttrNumber volatility = scan_attribute(relation, "volatility", INT8OID);
    AttrNumber witnesses = scan_attribute(relation, "witness_count", INT8OID);
    const ScanSet *probe = subjects->array != NULL ? subjects : objects;
    AttrNumber endpoint = subjects->array != NULL ? subject : object;
    Relation index = (plane_local && cutoff != NULL)
        ? scan_plane_index(relation, endpoint, type, object, rating, rd)
        : cutoff != NULL ? scan_rank_index(relation, endpoint, object, rating, rd) : NULL;
    if (plane_local && cutoff != NULL && index != NULL)
    {
        scan_plane_ranges(relation, index, subject, object, type, rating, rd,
                          volatility, witnesses, subjects, objects, types,
                          consume, cutoff, context, stats);
        index_close(index, AccessShareLock);
        return;
    }
    /* A type-local cutoff is not valid on an endpoint-wide rank index. When
     * the typed index is absent, every matching plane is read through the
     * complete keyed batch scan, so index availability never hides a cell. */
    bool ranked = !plane_local && cutoff != NULL && index != NULL;
    if (index == NULL) index = scan_index(relation, endpoint,
                               object, objects->array != NULL,
                               type, types->array != NULL);
    TupleTableSlot *slot = table_slot_create(relation, NULL);
    IndexScanDesc scan;
    ScanKeyData keys[INDEX_MAX_KEYS];
    int nkeys = 1;

    ScanKeyEntryInitialize(&keys[0], SK_SEARCHARRAY, 1, BTEqualStrategyNumber,
        BYTEAOID, InvalidOid, F_BYTEAEQ, PointerGetDatum(probe->array));
    while (!ranked && nkeys < index->rd_index->indnkeyatts)
    {
        AttrNumber attr = index->rd_index->indkey.values[nkeys];
        const ScanSet *set = attr == object ? objects : attr == type ? types : NULL;
        if (set == NULL || set->array == NULL) break;
        ScanKeyEntryInitialize(&keys[nkeys], SK_SEARCHARRAY, nkeys + 1, BTEqualStrategyNumber,
            BYTEAOID, InvalidOid, F_BYTEAEQ, PointerGetDatum(set->array));
        nkeys++;
    }
    scan = index_beginscan(relation, index, GetActiveSnapshot(), NULL, nkeys, 0);
    /* Reuse the open leaf/index. Each endpoint has an independent ordered
     * range: reaching its cutoff cannot skip any other endpoint. */
    for (int probe_index = 0; probe_index < (ranked ? probe->count : 1); ++probe_index)
    {
        if (ranked)
            ScanKeyEntryInitialize(&keys[0], 0, 1, BTEqualStrategyNumber,
                BYTEAOID, InvalidOid, F_BYTEAEQ, probe->values[probe_index]);
        index_rescan(scan, keys, nkeys, NULL, 0);
        stats->index_scans++;
        while (index_getnext_slot(scan, ForwardScanDirection, slot))
        {
            bool snull, onull, tnull, rnull, dnull, vnull, wnull;
            Datum s = slot_getattr(slot, subject, &snull);
            Datum o = slot_getattr(slot, object, &onull);
            Datum t = slot_getattr(slot, type, &tnull);
            LaplaceConsensusRow row={0};
            stats->rows_read++;
            if ((stats->rows_read & 4095) == 0) CHECK_FOR_INTERRUPTS();
            if (snull || tnull || !scan_set_contains(subjects, s, snull) ||
                !scan_set_contains(objects, o, onull) ||
                !scan_set_contains(types, t, tnull))
            {
                ExecClearTuple(slot);
                continue;
            }
            if (VARSIZE_ANY_EXHDR(DatumGetByteaPP(s)) != 16 ||
                VARSIZE_ANY_EXHDR(DatumGetByteaPP(t)) != 16 ||
                (!onull && VARSIZE_ANY_EXHDR(DatumGetByteaPP(o)) != 16))
                ereport(ERROR, (errmsg("consensus scan encountered a malformed stored identity")));
            memcpy(&row.subject, VARDATA_ANY(DatumGetByteaPP(s)), 16);
            memcpy(&row.type, VARDATA_ANY(DatumGetByteaPP(t)), 16);
            memset(&row.object, 0, sizeof(row.object));
            row.object_is_null = onull;
            if (!onull) memcpy(&row.object, VARDATA_ANY(DatumGetByteaPP(o)), 16);
            row.rating = DatumGetInt64(slot_getattr(slot, rating, &rnull));
            row.rd = DatumGetInt64(slot_getattr(slot, rd, &dnull));
            row.volatility = DatumGetInt64(slot_getattr(slot, volatility, &vnull));
            row.witnesses = DatumGetInt64(slot_getattr(slot, witnesses, &wnull));
            if (rnull || dnull || vnull || wnull)
                ereport(ERROR, (errmsg("consensus scan encountered incomplete standing")));
            if (ranked && cutoff(&row, context))
            {
                ExecClearTuple(slot);
                break;
            }
            stats->rows_matched++;
            consume(&row, context);
            ExecClearTuple(slot);
        }
    }
    index_endscan(scan);
    ExecDropSingleTupleTableSlot(slot);
    index_close(index, AccessShareLock);
}

static void
consensus_scan_impl(ArrayType *subject_ids, ArrayType *object_ids,
                    ArrayType *type_ids, bool default_only,
                    LaplaceConsensusConsumer consume,
                    void *context, LaplaceConsensusScanStats *stats,
                    LaplaceConsensusCutoff cutoff, bool plane_local, bool witnesses)
{
    ScanSet subjects, objects, types;
    LaplaceConsensusScanStats local_stats = {0};
    Oid root;
    AclResult acl;
    List *pending;

    if (subject_ids == NULL && object_ids == NULL)
        ereport(ERROR, (errmsg("consensus scan requires an endpoint identity set")));
    if (stats == NULL) stats = &local_stats;
    scan_set_init(&subjects, subject_ids);
    scan_set_init(&objects, object_ids);
    scan_set_init(&types, type_ids);
    if ((subjects.array != NULL && subjects.count == 0) ||
        (objects.array != NULL && objects.count == 0) ||
        (types.array != NULL && types.count == 0)) goto done;

    if (!default_only && !(plane_local && !witnesses && (cutoff == NULL || subjects.array == NULL)))
        scan_native(subjects.array,
                    (plane_local && !witnesses && objects.array == NULL) ? subjects.array : objects.array,
                    types.array, cutoff != NULL, consume, context, stats);

    root = get_relname_relid("consensus", get_namespace_oid("laplace", false));
    if (!OidIsValid(root))
        ereport(ERROR, (errmsg("laplace.consensus does not exist")));
    acl = pg_class_aclcheck(root, GetUserId(), ACL_SELECT);
    if (acl != ACLCHECK_OK)
        aclcheck_error(acl, OBJECT_TABLE, "laplace.consensus");
    if (check_enable_rls(root, InvalidOid, true) == RLS_ENABLED)
        ereport(ERROR, (errmsg("native consensus scan cannot bypass row security")));

    if (!converse_legacy_consensus_has_rows())
        goto done;

    if (default_only)
    {
        Relation relation = table_open(root, AccessShareLock);
        PartitionDesc desc;
        if (relation->rd_rel->relkind != RELKIND_PARTITIONED_TABLE)
        {
            table_close(relation, NoLock);
            goto done;
        }
        desc = RelationGetPartitionDesc(relation, true);
        root = desc->boundinfo->default_index >= 0 ?
            desc->oids[desc->boundinfo->default_index] : InvalidOid;
        table_close(relation, NoLock);
        if (!OidIsValid(root)) goto done;
    }

    pending = list_make1_oid(root);
    while (pending != NIL)
    {
        Oid oid = linitial_oid(pending);
        Relation relation;
        pending = list_delete_first(pending);
        CHECK_FOR_INTERRUPTS();
        relation = table_open(oid, AccessShareLock);
        if (relation->rd_rel->relkind == RELKIND_PARTITIONED_TABLE)
            pending = list_concat(pending, scan_children(relation, &subjects, &types));
        else
            scan_leaf(relation, &subjects, &objects, &types, consume, context,
                      stats, cutoff, plane_local);
        /* Retain relation locks through the transaction, as an ordinary SELECT
         * does, so partition topology cannot change beneath this snapshot. */
        table_close(relation, NoLock);
    }
done:
    scan_set_destroy(&subjects);
    scan_set_destroy(&objects);
    scan_set_destroy(&types);
}

void
laplace_consensus_scan(ArrayType *subjects, ArrayType *objects, ArrayType *types,
                       LaplaceConsensusConsumer consume, void *context,
                       LaplaceConsensusScanStats *stats)
{
    consensus_scan_impl(subjects, objects, types, false, consume, context, stats, NULL, false, true);
}

void
laplace_consensus_scan_default(ArrayType *subjects, ArrayType *objects,
                               LaplaceConsensusConsumer consume, void *context,
                               LaplaceConsensusScanStats *stats)
{
    consensus_scan_impl(subjects, objects, NULL, true, consume, context, stats, NULL, false, true);
}

void
laplace_consensus_scan_ranked(ArrayType *subjects, ArrayType *objects, ArrayType *types,
    bool default_only, LaplaceConsensusConsumer consume, LaplaceConsensusCutoff cutoff,
    void *context, LaplaceConsensusScanStats *stats)
{
    consensus_scan_impl(subjects, objects, types, default_only, consume, context,
                        stats, cutoff, false, true);
}

void
laplace_consensus_scan_ranked_planes(ArrayType *subjects, ArrayType *objects, ArrayType *types,
    bool default_only, LaplaceConsensusConsumer consume, LaplaceConsensusCutoff cutoff,
    void *context, LaplaceConsensusScanStats *stats)
{
    consensus_scan_impl(subjects, objects, types, default_only, consume, context,
                        stats, cutoff, true, true);
}

void laplace_consensus_candidates(ArrayType *subjects,ArrayType *objects,ArrayType *types,
    LaplaceConsensusConsumer consume,void *context,LaplaceConsensusScanStats *stats)
{
    consensus_scan_impl(subjects,objects,types,false,consume,context,stats,NULL,false,false);
}
void laplace_consensus_candidates_stored(ArrayType *subjects,ArrayType *objects,ArrayType *types,
    LaplaceConsensusConsumer consume,void *context)
{
    consensus_scan_impl(subjects,objects,types,false,consume,context,NULL,NULL,true,false);
}
void laplace_consensus_ranked_candidates(ArrayType *subjects,ArrayType *objects,ArrayType *types,
    bool default_only,bool planes,LaplaceConsensusConsumer consume,LaplaceConsensusCutoff cutoff,
    void *context,LaplaceConsensusScanStats *stats)
{
    consensus_scan_impl(subjects,objects,types,default_only,consume,context,stats,cutoff,planes,false);
}
typedef struct ClaimTrunkKey
{
    hash128_t claim;
    hash128_t trunk;
} ClaimTrunkKey;

typedef struct ClaimWitnessCountEntry
{
    hash128_t claim;
    int64     witnesses;
} ClaimWitnessCountEntry;

typedef struct NativeWitnessCounts
{
    MemoryContext caller;
    HTAB         *seen_trunks;
    HTAB         *counts_by_claim;
} NativeWitnessCounts;

static bool
visit_native_witness_row(TupleTableSlot *slot, void *context)
{
    NativeWitnessCounts    *read = (NativeWitnessCounts *) context;
    bool                    isnull = false;
    Datum                   d_claim, d_trunk, d_witness;
    ClaimTrunkKey           tk;
    bool                    found = false;
    MemoryContext           prev;

    d_witness = slot_getattr(slot, 8, &isnull);
    if (isnull || !DatumGetBool(d_witness))
        return true;

    d_claim = slot_getattr(slot, 1, &isnull);
    if (isnull)
        return true;
    d_trunk = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;

    tk.claim = laplace_pub_blake3_to_hash128(d_claim);
    tk.trunk = laplace_pub_blake3_to_hash128(d_trunk);

    prev = MemoryContextSwitchTo(read->caller);
    (void) hash_search(read->seen_trunks, &tk, HASH_ENTER, &found);
    if (!found)
    {
        bool                    c_found = false;
        ClaimWitnessCountEntry *ce = (ClaimWitnessCountEntry *) hash_search(
            read->counts_by_claim, &tk.claim, HASH_ENTER, &c_found);
        if (!c_found)
            ce->witnesses = 1;
        else
            ce->witnesses++;
    }
    MemoryContextSwitchTo(prev);
    return true;
}

void
laplace_consensus_witness_counts(const hash128_t *claims, int count, int64 *counts)
{
    MemoryContext       caller = CurrentMemoryContext;
    hash128_t          *uniq;
    int                 n_uniq = 0;
    ArrayType          *ids_b3_arr;
    Oid                 argtypes[2] = { laplace_pub_blake3_array_oid(), BOOLOID };
    Datum               args[2];
    bool                argnulls[2] = { false, false };
    HASHCTL             tctl, cctl;
    NativeWitnessCounts read;
    instr_time          started, elapsed;

    if (count <= 0)
        return;

    uniq = cs_sort_dedup_ids(claims, count, &n_uniq);
    ids_b3_arr = laplace_pub_blake3_array(uniq, n_uniq);
    args[0] = PointerGetDatum(ids_b3_arr);
    args[1] = BoolGetDatum(false);

    memset(&tctl, 0, sizeof(tctl));
    tctl.keysize   = sizeof(ClaimTrunkKey);
    tctl.entrysize = sizeof(ClaimTrunkKey);
    tctl.hcxt      = caller;

    memset(&cctl, 0, sizeof(cctl));
    cctl.keysize   = sizeof(hash128_t);
    cctl.entrysize = sizeof(ClaimWitnessCountEntry);
    cctl.hcxt      = caller;

    read.caller          = caller;
    read.seen_trunks     = hash_create("native witness trunks", Max(n_uniq * 2, 32), &tctl,
                                       HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    read.counts_by_claim = hash_create("native witness counts", Max(n_uniq, 16), &cctl,
                                       HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    INSTR_TIME_SET_CURRENT(started);
    elog(DEBUG1, "Native witness counts: %d claims", count);
    (void) laplace_pub_call_srf("public", "laplace_attested", 2, argtypes, args, argnulls,
                                visit_native_witness_row, &read);
    for (int i = 0; i < count; ++i)
    {
        ClaimWitnessCountEntry *ce = (ClaimWitnessCountEntry *) hash_search(
            read.counts_by_claim, &claims[i], HASH_FIND, NULL);
        counts[i] = ce ? ce->witnesses : 0;
    }
    INSTR_TIME_SET_CURRENT(elapsed);
    INSTR_TIME_SUBTRACT(elapsed, started);
    elog(DEBUG1, "Native witness counts: %d results; %.3f ms",
         count, INSTR_TIME_GET_MILLISEC(elapsed));

    hash_destroy(read.seen_trunks);
    hash_destroy(read.counts_by_claim);
    pfree(ids_b3_arr);
    pfree(uniq);
}
void laplace_consensus_enrich(void *rows,int count,Size stride,
    Size claim_offset,Size native_offset,Size witnesses_offset)
{
    if(count<=0)return;
    if(!rows || claim_offset>stride || stride-claim_offset<sizeof(hash128_t)
       || native_offset>=stride || witnesses_offset>stride || stride-witnesses_offset<sizeof(int64)
       || (Size)count>MaxAllocSize/sizeof(hash128_t) || (Size)count>MaxAllocSize/stride)
        elog(ERROR,"Native consensus enrichment has an invalid record layout");
    hash128_t *claims=palloc(sizeof(*claims)*(Size)count);
    int *ordinals=palloc(sizeof(*ordinals)*(Size)count),selected=0;
    for(int i=0;i<count;i++){
        char *row=(char *)rows+(Size)i*stride;
        if(!*(bool *)(row+native_offset))continue;
        memcpy(claims+selected,row+claim_offset,sizeof(hash128_t));ordinals[selected++]=i;
    }
    if(selected){
        int64 *counts=palloc(sizeof(*counts)*(Size)selected);
        laplace_consensus_witness_counts(claims,selected,counts);
        for(int i=0;i<selected;i++)memcpy((char *)rows+(Size)ordinals[i]*stride+witnesses_offset,counts+i,sizeof(int64));
        pfree(counts);
    }
    pfree(claims);pfree(ordinals);
}

static bool
scan_type_rank_predicate(Relation index, AttrNumber object)
{
    List       *predicate = RelationGetIndexPredicate(index);
    ListCell   *lc;
    bool        saw_obj_not_null = false;
    bool        saw_not_refuted = false;

    if (list_length(predicate) != 2)
        return false;

    foreach(lc, predicate)
    {
        Node *n = (Node *) lfirst(lc);
        if (IsA(n, NullTest))
        {
            NullTest *nt = (NullTest *) n;
            if (nt->nulltesttype == IS_NOT_NULL && !nt->argisrow && IsA(nt->arg, Var))
            {
                Var *v = (Var *) nt->arg;
                if (v->varattno == object && v->vartype == BYTEAOID && v->varlevelsup == 0)
                    saw_obj_not_null = true;
            }
        }
        else if (IsA(n, BoolExpr))
        {
            BoolExpr *be = (BoolExpr *) n;
            if (be->boolop == NOT_EXPR && list_length(be->args) == 1 &&
                IsA(linitial(be->args), FuncExpr))
            {
                FuncExpr *fe = linitial_node(FuncExpr, be->args);
                char     *fname = get_func_name(fe->funcid);
                if (fname != NULL && strcmp(fname, "refuted") == 0)
                    saw_not_refuted = true;
                if (fname != NULL)
                    pfree(fname);
            }
        }
    }

    return saw_obj_not_null && saw_not_refuted;
}

static Relation
scan_type_rank_index(Relation relation, AttrNumber type, AttrNumber object,
                     AttrNumber rating, AttrNumber rd)
{
    List     *indexes = RelationGetIndexList(relation);
    ListCell *cell;
    Relation  selected = NULL;

    foreach(cell, indexes)
    {
        Relation index = index_open(lfirst_oid(cell), AccessShareLock);
        if (index->rd_rel->relam == BTREE_AM_OID &&
            index->rd_index->indisvalid && index->rd_index->indisready &&
            index->rd_index->indnkeyatts >= 2 &&
            index->rd_index->indkey.values[0] == type &&
            index->rd_index->indkey.values[1] == 0 &&
            index->rd_opfamily[0] == BYTEA_BTREE_FAM_OID &&
            index->rd_opfamily[1] == INTEGER_BTREE_FAM_OID &&
            (index->rd_indoption[0] & INDOPTION_DESC) == 0 &&
            (index->rd_indoption[1] & INDOPTION_DESC) != 0 &&
            scan_type_rank_predicate(index, object))
        {
            List *expressions = RelationGetIndexExpressions(index);
            if (expressions != NIL &&
                scan_eff_mu_expression(linitial(expressions), rating, rd))
            {
                selected = index;
                break;
            }
        }
        index_close(index, AccessShareLock);
    }
    list_free(indexes);
    return selected;
}

static int
cmp_band_edge_row(const void *pa, const void *pb)
{
    const LaplaceBandEdgeRow *a = (const LaplaceBandEdgeRow *) pa;
    const LaplaceBandEdgeRow *b = (const LaplaceBandEdgeRow *) pb;
    int                       c;

    if (a->eff_mu != b->eff_mu)
        return (a->eff_mu > b->eff_mu) ? -1 : 1;
    c = memcmp(&a->subject_id, &b->subject_id, sizeof(hash128_t));
    if (c != 0)
        return c;
    c = memcmp(&a->type_id, &b->type_id, sizeof(hash128_t));
    if (c != 0)
        return c;
    return memcmp(&a->object_id, &b->object_id, sizeof(hash128_t));
}

static void
band_edge_append(MemoryContext caller, LaplaceBandEdgeRow **rows, int *count, int *cap,
                 const LaplaceBandEdgeRow *item)
{
    MemoryContext prev = MemoryContextSwitchTo(caller);
    if (*count == *cap)
    {
        int new_cap = *cap == 0 ? 64 : *cap * 2;
        if ((Size) new_cap > MaxAllocSize / sizeof(LaplaceBandEdgeRow))
            ereport(ERROR, (errmsg("consensus_band_edges: result count exceeds allocation capacity")));
        *rows = *rows == NULL
            ? (LaplaceBandEdgeRow *) palloc((Size) new_cap * sizeof(LaplaceBandEdgeRow))
            : (LaplaceBandEdgeRow *) repalloc(*rows, (Size) new_cap * sizeof(LaplaceBandEdgeRow));
        *cap = new_cap;
    }
    (*rows)[(*count)++] = *item;
    MemoryContextSwitchTo(prev);
}

static void
band_edges_scan_leaf(MemoryContext caller, Relation leaf,
                     const hash128_t *uniq_types, const Datum *type_datums, int n_uniq,
                     bool has_min_eff_mu, int64 min_eff_mu, int64 limit_rows,
                     LaplaceBandEdgeRow **rows, int *count, int *cap)
{
    AttrNumber      subject = scan_attribute(leaf, "subject_id", BYTEAOID);
    AttrNumber      object  = scan_attribute(leaf, "object_id", BYTEAOID);
    AttrNumber      type    = scan_attribute(leaf, "type_id", BYTEAOID);
    AttrNumber      rating  = scan_attribute(leaf, "rating", INT8OID);
    AttrNumber      rd      = scan_attribute(leaf, "rd", INT8OID);
    AttrNumber      witnesses = scan_attribute(leaf, "witness_count", INT8OID);
    Relation        rank_idx;
    bool            using_rank = true;
    TupleTableSlot *slot;
    IndexScanDesc   scan;
    ScanKeyData     skey;

    if (RelationGetNumberOfBlocks(leaf) == 0)
        return;

    rank_idx = scan_type_rank_index(leaf, type, object, rating, rd);
    if (rank_idx == NULL)
    {
        using_rank = false;
        rank_idx = scan_index(leaf, type, object, true, type, false);
    }

    slot = table_slot_create(leaf, NULL);
    scan = index_beginscan(leaf, rank_idx, GetActiveSnapshot(), NULL, 1, 0);

    for (int ti = 0; ti < n_uniq; ++ti)
    {
        int64 kept_here = 0;

        ScanKeyEntryInitialize(&skey, 0, 1, BTEqualStrategyNumber,
                               BYTEAOID, InvalidOid, F_BYTEAEQ, type_datums[ti]);
        index_rescan(scan, &skey, 1, NULL, 0);

        while (index_getnext_slot(scan, ForwardScanDirection, slot))
        {
            bool               isnull = false;
            Datum              d;
            int64              rat_v, rd_v, wit_v, eff_v;
            LaplaceBandEdgeRow item;

            CHECK_FOR_INTERRUPTS();

            d = slot_getattr(slot, object, &isnull);
            if (isnull)
            {
                ExecClearTuple(slot);
                continue;
            }
            item.object_id = datum_to_hash128(d);

            d = slot_getattr(slot, rating, &isnull);
            if (isnull) elog(ERROR, "consensus_band_edges: rating is NULL");
            rat_v = DatumGetInt64(d);

            d = slot_getattr(slot, rd, &isnull);
            if (isnull) elog(ERROR, "consensus_band_edges: rd is NULL");
            rd_v = DatumGetInt64(d);

            eff_v = (int64) ((__int128) rat_v - 2 * (__int128) rd_v);
            if (has_min_eff_mu && eff_v < min_eff_mu)
            {
                ExecClearTuple(slot);
                if (using_rank)
                    break;
                continue;
            }
            if (laplace_glicko2_refuted(rat_v, rd_v))
            {
                ExecClearTuple(slot);
                continue;
            }

            d = slot_getattr(slot, subject, &isnull);
            if (isnull) elog(ERROR, "consensus_band_edges: subject_id is NULL");
            item.subject_id = datum_to_hash128(d);

            item.type_id = uniq_types[ti];

            d = slot_getattr(slot, witnesses, &isnull);
            wit_v = isnull ? 1 : DatumGetInt64(d);

            item.rating        = rat_v;
            item.rd            = rd_v;
            item.witness_count = wit_v;
            item.eff_mu        = eff_v;

            band_edge_append(caller, rows, count, cap, &item);
            ExecClearTuple(slot);

            kept_here++;
            if (using_rank && kept_here >= limit_rows)
                break;
        }
    }

    index_endscan(scan);
    ExecDropSingleTupleTableSlot(slot);
    index_close(rank_idx, AccessShareLock);
}

LaplaceBandEdgeRow *
laplace_consensus_band_edges_scan_c(const hash128_t *types, int n_types,
                                    bool has_min_eff_mu, int64 min_eff_mu,
                                    int64 limit_rows, int *out_count)
{
    MemoryContext       caller = CurrentMemoryContext;
    hash128_t          *uniq;
    int                 n_uniq = 0;
    Datum              *type_datums;
    Oid                 root;
    AclResult           acl;
    List               *pending;
    LaplaceBandEdgeRow *rows = NULL;
    int                 count = 0;
    int                 cap = 0;

    *out_count = 0;
    if (types == NULL || n_types <= 0 || limit_rows <= 0)
        return NULL;

    uniq = cs_sort_dedup_ids(types, n_types, &n_uniq);
    type_datums = (Datum *) palloc(sizeof(Datum) * (Size) n_uniq);
    for (int i = 0; i < n_uniq; ++i)
        type_datums[i] = hash128_to_datum(&uniq[i]);

    root = get_relname_relid("consensus", get_namespace_oid("laplace", false));
    if (!OidIsValid(root))
        ereport(ERROR, (errmsg("laplace.consensus does not exist")));
    acl = pg_class_aclcheck(root, GetUserId(), ACL_SELECT);
    if (acl != ACLCHECK_OK)
        aclcheck_error(acl, OBJECT_TABLE, "laplace.consensus");
    if (check_enable_rls(root, InvalidOid, true) == RLS_ENABLED)
        ereport(ERROR, (errmsg("native consensus scan cannot bypass row security")));

    if (!converse_legacy_consensus_has_rows())
    {
        pfree(type_datums);
        pfree(uniq);
        return NULL;
    }

    pending = list_make1_oid(root);
    while (pending != NIL)
    {
        Oid      oid = linitial_oid(pending);
        Relation relation;

        pending = list_delete_first(pending);
        CHECK_FOR_INTERRUPTS();
        relation = table_open(oid, AccessShareLock);
        if (relation->rd_rel->relkind == RELKIND_PARTITIONED_TABLE)
        {
            PartitionDesc desc = RelationGetPartitionDesc(relation, true);
            for (int i = 0; i < desc->nparts; ++i)
                pending = lappend_oid(pending, desc->oids[i]);
        }
        else
        {
            band_edges_scan_leaf(caller, relation, uniq, type_datums, n_uniq,
                                 has_min_eff_mu, min_eff_mu, limit_rows,
                                 &rows, &count, &cap);
        }
        table_close(relation, NoLock);
    }

    for (int i = 0; i < n_uniq; ++i)
        pfree(DatumGetPointer(type_datums[i]));
    pfree(type_datums);
    pfree(uniq);

    if (count > 1)
        qsort(rows, (Size) count, sizeof(LaplaceBandEdgeRow), cmp_band_edge_row);
    if ((int64) count > limit_rows)
        count = (int) limit_rows;

    *out_count = count;
    return rows;
}
