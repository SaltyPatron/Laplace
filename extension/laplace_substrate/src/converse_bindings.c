#include "postgres.h"

#include <ctype.h>
#include <limits.h>
#include <math.h>
#include <string.h>

#include "access/genam.h"
#include "access/relscan.h"
#include "access/skey.h"
#include "access/table.h"
#include "access/tableam.h"
#include "access/xact.h"
#include "catalog/index.h"
#include "catalog/namespace.h"
#include "catalog/pg_am_d.h"
#include "catalog/pg_inherits.h"
#include "catalog/pg_type.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "storage/bufmgr.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/fmgroids.h"
#include "utils/hsearch.h"
#include "utils/inval.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/numeric.h"
#include "utils/rel.h"
#include "utils/snapmgr.h"

#include "laplace/core/glicko2.h"
#include "laplace/core/hash128.h"
#include "laplace/core/language_law.h"
#include "laplace/core/relation_law.h"
#include "laplace_pg/executor.h"

#include "consensus_scan.h"
#include "converse_bindings.h"
#include "edges_raw_internal.h"
#include "public_direct.h"
#include "relations_internal.h"
#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_laplace_lexicon_texts);
PG_FUNCTION_INFO_V1(pg_laplace_binding_evidence_impl);
PG_FUNCTION_INFO_V1(pg_laplace_key_binding_evidence_impl);
PG_FUNCTION_INFO_V1(pg_laplace_word_language_candidates_batch);
PG_FUNCTION_INFO_V1(pg_laplace_key_facts);
PG_FUNCTION_INFO_V1(pg_laplace_converse_couple);
PG_FUNCTION_INFO_V1(pg_laplace_terms_language);
PG_FUNCTION_INFO_V1(pg_laplace_converse_translate);
PG_FUNCTION_INFO_V1(pg_laplace_senses_batch_impl);

static HTAB *
cb_hash_create_id(MemoryContext ctx, const char *name, Size entry_size, long init)
{
    HASHCTL ctl;

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = entry_size;
    ctl.hcxt = ctx;
    return hash_create(name, Max(init, 16), &ctl,
                       HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
}

static HTAB *
cb_hash_create_key(MemoryContext ctx, const char *name, Size key_size,
                   Size entry_size, long init)
{
    HASHCTL ctl;

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = key_size;
    ctl.entrysize = entry_size;
    ctl.hcxt = ctx;
    return hash_create(name, Max(init, 16), &ctl,
                       HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
}

static int
cb_hash128_cmp(const void *a, const void *b)
{
    return memcmp(a, b, sizeof(hash128_t));
}

static int
cb_dedup_ids(const hash128_t *src, int n, hash128_t *dst)
{
    int out = 0;

    if (n <= 0 || src == NULL)
        return 0;
    memcpy(dst, src, sizeof(hash128_t) * (Size) n);
    if (n > 1)
        qsort(dst, (Size) n, sizeof(hash128_t), cb_hash128_cmp);
    dst[out++] = dst[0];
    for (int i = 1; i < n; i++)
    {
        if (!hash128_eq(&dst[out - 1], &dst[i]))
            dst[out++] = dst[i];
    }
    return out;
}

static hash128_t *
cb_deconstruct_bytea_ids(ArrayType *arr, int *out_count)
{
    Datum     *vals;
    bool      *nulls;
    int        n = 0;
    hash128_t *ids;
    int        valid = 0;

    *out_count = 0;
    if (arr == NULL)
        return NULL;
    deconstruct_array(arr, BYTEAOID, -1, false, TYPALIGN_INT, &vals, &nulls, &n);
    if (n <= 0)
        return NULL;
    ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n);
    for (int i = 0; i < n; i++)
    {
        if (!nulls[i])
            ids[valid++] = datum_to_hash128(vals[i]);
    }
    pfree(vals);
    pfree(nulls);
    *out_count = valid;
    return ids;
}

static int
cb_deconstruct_blake3_ids_nonnull(ArrayType *arr, hash128_t **out_ids)
{
    ArrayIterator it;
    Datum         val;
    bool          isnull = false;
    int           cap = 16;
    int           n = 0;
    hash128_t    *buf;

    *out_ids = NULL;
    if (arr == NULL || ArrayGetNItems(ARR_NDIM(arr), ARR_DIMS(arr)) == 0)
        return 0;

    buf = (hash128_t *) palloc(sizeof(hash128_t) * (Size) cap);
    it = array_create_iterator(arr, 0, NULL);
    while (array_iterate(it, &val, &isnull))
    {
        if (isnull)
            continue;
        if (n >= cap)
        {
            cap *= 2;
            buf = (hash128_t *) repalloc(buf, sizeof(hash128_t) * (Size) cap);
        }
        buf[n++] = laplace_pub_blake3_to_hash128(val);
    }
    array_free_iterator(it);
    *out_ids = buf;
    return n;
}

/* ========================================================================
 * 1. converse.lexicon_texts
 * ======================================================================== */

typedef struct LexFormEntry
{
    hash128_t entity;
    int16     kind_slot;
    bool      is_tuple;
} LexFormEntry;

typedef struct LexConstitEntry
{
    hash128_t entity;
    int       count;
    bool      all_run_1;
    int32     ord[2];
    hash128_t child[2];
} LexConstitEntry;

typedef struct LexIdEntry
{
    hash128_t id;
} LexIdEntry;

typedef struct LexLiteralEntry
{
    hash128_t entity;
    hash128_t literal;
} LexLiteralEntry;

typedef struct LexLabelEntry
{
    hash128_t entity;
    char     *content;
    bool      content_null;
} LexLabelEntry;

typedef struct LexPhysCtx
{
    MemoryContext caller;
    HTAB         *forms;
} LexPhysCtx;

static bool
cb_visit_lex_physicality(TupleTableSlot *slot, void *opaque)
{
    LexPhysCtx   *ctx = (LexPhysCtx *) opaque;
    bool          isnull = false;
    Datum         d_ent, d_mask;
    hash128_t     ent;
    int16         kind_slot = -1;
    bool          is_tuple = false;
    LexFormEntry *fe;
    bool          found = false;
    MemoryContext prev;

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;
    ent = laplace_pub_blake3_to_hash128(d_ent);

    d_mask = slot_getattr(slot, 6, &isnull);
    if (!isnull)
    {
        kind_slot = laplace_pub_mask_value(d_mask, "kind");
        is_tuple = laplace_pub_mask_has_value(d_mask, "kind", "tuple");
    }

    prev = MemoryContextSwitchTo(ctx->caller);
    fe = (LexFormEntry *) hash_search(ctx->forms, &ent, HASH_ENTER, &found);
    if (!found)
    {
        fe->kind_slot = kind_slot;
        fe->is_tuple = is_tuple;
    }
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct LexConstitCtx
{
    MemoryContext caller;
    HTAB         *pairs;
} LexConstitCtx;

static bool
cb_visit_lex_constituent(TupleTableSlot *slot, void *opaque)
{
    LexConstitCtx   *ctx = (LexConstitCtx *) opaque;
    bool             isnull = false;
    Datum            d_ent, d_ord, d_child, d_run;
    hash128_t        ent, child;
    int32            ord, run_len;
    LexConstitEntry *ce;
    bool             found = false;
    MemoryContext    prev;

    d_ent = slot_getattr(slot, 1, &isnull);
    if (isnull)
        return true;
    ent = laplace_pub_blake3_to_hash128(d_ent);

    d_ord = slot_getattr(slot, 2, &isnull);
    ord = isnull ? 0 : DatumGetInt32(d_ord);

    d_child = slot_getattr(slot, 3, &isnull);
    if (isnull)
        return true;
    child = laplace_pub_blake3_to_hash128(d_child);

    d_run = slot_getattr(slot, 4, &isnull);
    run_len = isnull ? 0 : DatumGetInt32(d_run);

    prev = MemoryContextSwitchTo(ctx->caller);
    ce = (LexConstitEntry *) hash_search(ctx->pairs, &ent, HASH_ENTER, &found);
    if (!found)
    {
        ce->count = 1;
        ce->all_run_1 = (run_len == 1);
        ce->ord[0] = ord;
        ce->child[0] = child;
    }
    else
    {
        if (run_len != 1)
            ce->all_run_1 = false;
        if (ce->count == 1)
        {
            if (ord < ce->ord[0])
            {
                ce->ord[1] = ce->ord[0];
                ce->child[1] = ce->child[0];
                ce->ord[0] = ord;
                ce->child[0] = child;
            }
            else
            {
                ce->ord[1] = ord;
                ce->child[1] = child;
            }
        }
        ce->count++;
    }
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct LexLangCtx
{
    MemoryContext caller;
    HTAB         *valid_langs;
} LexLangCtx;

static bool
cb_visit_lex_lang_label(TupleTableSlot *slot, void *opaque)
{
    LexLangCtx *ctx = (LexLangCtx *) opaque;
    bool        isnull = false;
    Datum       d_id, d_lbl;
    hash128_t   id;
    char       *tag;
    const char *canonical = NULL;
    MemoryContext prev;

    d_id = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;
    d_lbl = slot_getattr(slot, 3, &isnull);
    if (isnull)
        return true;

    id = laplace_pub_blake3_to_hash128(d_id);
    tag = TextDatumGetCString(d_lbl);
    if (laplace_language_canonical(tag, &canonical) == 0 && canonical != NULL)
    {
        bool found = false;
        prev = MemoryContextSwitchTo(ctx->caller);
        (void) hash_search(ctx->valid_langs, &id, HASH_ENTER, &found);
        MemoryContextSwitchTo(prev);
    }
    pfree(tag);
    return true;
}

typedef struct LexLabelCtx
{
    MemoryContext caller;
    HTAB         *labels;
} LexLabelCtx;

static bool
cb_visit_lex_label(TupleTableSlot *slot, void *opaque)
{
    LexLabelCtx   *ctx = (LexLabelCtx *) opaque;
    bool           isnull_id = false;
    bool           isnull_lbl = false;
    Datum          d_id, d_lbl;
    hash128_t      id;
    char          *txt = NULL;
    LexLabelEntry *le;
    bool           found = false;
    MemoryContext  prev;

    d_id = slot_getattr(slot, 2, &isnull_id);
    if (isnull_id)
        return true;
    id = laplace_pub_blake3_to_hash128(d_id);

    d_lbl = slot_getattr(slot, 3, &isnull_lbl);
    if (!isnull_lbl)
        txt = TextDatumGetCString(d_lbl);

    prev = MemoryContextSwitchTo(ctx->caller);
    le = (LexLabelEntry *) hash_search(ctx->labels, &id, HASH_ENTER, &found);
    if (!found)
    {
        le->content_null = isnull_lbl;
        le->content = isnull_lbl ? NULL : pstrdup(txt);
    }
    MemoryContextSwitchTo(prev);

    if (txt != NULL)
        pfree(txt);
    return true;
}

ConverseLexiconTextRow *
converse_lexicon_texts_c(const hash128_t *ids, int n_ids, int *out_count)
{
    MemoryContext          caller = CurrentMemoryContext;
    MemoryContext          work, old;
    hash128_t             *req;
    int                    n_req;
    HTAB                  *forms, *literals, *labels;
    ArrayType             *req_arr;
    Oid                    b3_arr_oid = laplace_pub_blake3_array_oid();
    ConverseLexiconTextRow *rows = NULL;
    int                    n_out = 0;

    *out_count = 0;
    if (ids == NULL || n_ids <= 0)
        return NULL;

    work = AllocSetContextCreate(caller, "converse_lexicon_texts",
                                 ALLOCSET_DEFAULT_SIZES);
    old = MemoryContextSwitchTo(work);

    req = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_ids);
    n_req = cb_dedup_ids(ids, n_ids, req);

    forms = cb_hash_create_id(work, "lt forms", sizeof(LexFormEntry), n_req);
    literals = cb_hash_create_id(work, "lt literals", sizeof(LexLiteralEntry), 16);
    labels = cb_hash_create_id(work, "lt labels", sizeof(LexLabelEntry), n_req);

    req_arr = laplace_pub_blake3_array(req, n_req);
    {
        Datum      args[1] = { PointerGetDatum(req_arr) };
        bool       nulls[1] = { false };
        LexPhysCtx pctx = { work, forms };

        (void) laplace_pub_call_srf("public", "laplace_physicalities", 1,
                                    &b3_arr_oid, args, nulls,
                                    cb_visit_lex_physicality, &pctx);
    }
    pfree(req_arr);

    /* Extract (entity -> literal) from 2-child kind='tuple' pairs with canonical lang. */
    {
        hash128_t       *tuple_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_req);
        int              n_tuples = 0;
        HASH_SEQ_STATUS  seq;
        LexFormEntry    *fe;

        hash_seq_init(&seq, forms);
        while ((fe = (LexFormEntry *) hash_seq_search(&seq)) != NULL)
        {
            if (fe->is_tuple)
                tuple_ids[n_tuples++] = fe->entity;
        }

        if (n_tuples > 0)
        {
            HTAB          *pairs = cb_hash_create_id(work, "lt pairs",
                                                     sizeof(LexConstitEntry), n_tuples);
            ArrayType     *tup_arr = laplace_pub_blake3_array(tuple_ids, n_tuples);
            Datum          args[1] = { PointerGetDatum(tup_arr) };
            bool           nulls[1] = { false };
            LexConstitCtx  cctx = { work, pairs };
            hash128_t     *lang_ids;
            int            n_langs = 0;
            LexConstitEntry *ce;

            (void) laplace_pub_call_srf("public", "laplace_constituents", 1,
                                        &b3_arr_oid, args, nulls,
                                        cb_visit_lex_constituent, &cctx);
            pfree(tup_arr);

            lang_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) Max(n_tuples, 1));
            hash_seq_init(&seq, pairs);
            while ((ce = (LexConstitEntry *) hash_seq_search(&seq)) != NULL)
            {
                if (ce->count == 2 && ce->all_run_1)
                    lang_ids[n_langs++] = ce->child[1];
            }

            if (n_langs > 0)
            {
                hash128_t *uniq_langs = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_langs);
                int        n_uniq_langs = cb_dedup_ids(lang_ids, n_langs, uniq_langs);
                HTAB      *valid_langs = cb_hash_create_id(work, "lt valid langs",
                                                           sizeof(LexIdEntry), n_uniq_langs);
                ArrayType *lang_arr = laplace_pub_blake3_array(uniq_langs, n_uniq_langs);
                Datum      largs[1] = { PointerGetDatum(lang_arr) };
                bool       lnulls[1] = { false };
                LexLangCtx lctx = { work, valid_langs };

                (void) laplace_pub_call_srf("public", "laplace_labels", 1,
                                            &b3_arr_oid, largs, lnulls,
                                            cb_visit_lex_lang_label, &lctx);
                pfree(lang_arr);

                hash_seq_init(&seq, pairs);
                while ((ce = (LexConstitEntry *) hash_seq_search(&seq)) != NULL)
                {
                    if (ce->count == 2 && ce->all_run_1 &&
                        hash_search(valid_langs, &ce->child[1], HASH_FIND, NULL) != NULL)
                    {
                        bool             found = false;
                        LexLiteralEntry *lit = (LexLiteralEntry *) hash_search(
                            literals, &ce->entity, HASH_ENTER, &found);
                        lit->literal = ce->child[0];
                    }
                }
            }
        }
    }

    /* Resolve final labels for coalesce(literal, r.id). */
    {
        hash128_t *target_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_req);
        hash128_t *uniq_targets = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_req);
        int        n_uniq_targets;
        ArrayType *targ_arr;

        for (int i = 0; i < n_req; i++)
        {
            LexLiteralEntry *lit = (LexLiteralEntry *) hash_search(
                literals, &req[i], HASH_FIND, NULL);
            target_ids[i] = lit ? lit->literal : req[i];
        }
        n_uniq_targets = cb_dedup_ids(target_ids, n_req, uniq_targets);
        targ_arr = laplace_pub_blake3_array(uniq_targets, n_uniq_targets);
        {
            Datum       args[1] = { PointerGetDatum(targ_arr) };
            bool        nulls[1] = { false };
            LexLabelCtx lblctx = { work, labels };

            (void) laplace_pub_call_srf("public", "laplace_labels", 1,
                                        &b3_arr_oid, args, nulls,
                                        cb_visit_lex_label, &lblctx);
        }
        pfree(targ_arr);

        MemoryContextSwitchTo(caller);
        rows = (ConverseLexiconTextRow *) MemoryContextAlloc(
            caller, sizeof(ConverseLexiconTextRow) * (Size) n_req);
        for (int i = 0; i < n_req; i++)
        {
            LexLiteralEntry *lit = (LexLiteralEntry *) hash_search(
                literals, &req[i], HASH_FIND, NULL);
            LexFormEntry    *fe = (LexFormEntry *) hash_search(
                forms, &req[i], HASH_FIND, NULL);
            hash128_t        target = lit ? lit->literal : req[i];
            LexLabelEntry   *le = (LexLabelEntry *) hash_search(
                labels, &target, HASH_FIND, NULL);

            if (le == NULL)
                continue;
            rows[n_out].entity = req[i];
            rows[n_out].content_null = le->content_null;
            rows[n_out].content = (le->content_null || le->content == NULL)
                                  ? NULL : pstrdup(le->content);
            rows[n_out].textual = (lit != NULL) || (fe == NULL) || (fe->kind_slot == -1);
            n_out++;
        }
    }

    MemoryContextSwitchTo(caller);
    MemoryContextDelete(work);
    *out_count = n_out;
    if (n_out == 0 && rows != NULL)
    {
        pfree(rows);
        return NULL;
    }
    return rows;
}

Datum
pg_laplace_lexicon_texts(PG_FUNCTION_ARGS)
{
    ReturnSetInfo          *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    hash128_t              *ids = NULL;
    int                     n_ids;
    ConverseLexiconTextRow *rows;
    int                     n_rows = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;

    n_ids = cb_deconstruct_blake3_ids_nonnull(PG_GETARG_ARRAYTYPE_P(0), &ids);
    if (n_ids == 0)
    {
        if (ids != NULL)
            pfree(ids);
        return (Datum) 0;
    }

    rows = converse_lexicon_texts_c(ids, n_ids, &n_rows);
    for (int i = 0; i < n_rows; i++)
    {
        Datum values[3];
        bool  nulls[3] = { false, rows[i].content_null, false };

        values[0] = laplace_pub_hash128_to_blake3(&rows[i].entity);
        values[1] = rows[i].content_null ? (Datum) 0 : CStringGetTextDatum(rows[i].content);
        values[2] = BoolGetDatum(rows[i].textual);
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        pfree(DatumGetPointer(values[0]));
        if (!nulls[1])
            pfree(DatumGetPointer(values[1]));
    }
    return (Datum) 0;
}

/* ========================================================================
 * 2. Legacy consensus / attestations helpers (zero SPI)
 * ======================================================================== */

static bool          cb_legacy_inval_registered = false;
static Oid           cb_legacy_root_oid = InvalidOid;
static Oid          *cb_legacy_leaf_oids = NULL;
static int           cb_legacy_n_leaves = 0;
static bool          cb_legacy_leaves_valid = false;
static bool          cb_legacy_snap_valid = false;
static Oid           cb_legacy_snap_root = InvalidOid;
static TransactionId cb_legacy_snap_xmin = InvalidTransactionId;
static TransactionId cb_legacy_snap_xmax = InvalidTransactionId;
static uint32        cb_legacy_snap_xcnt = 0;
static CommandId     cb_legacy_snap_cid = InvalidCommandId;
static bool          cb_legacy_has_rows_cached = false;

static void
cb_legacy_relcache_inval(Datum arg, Oid relid)
{
    (void) arg;
    cb_legacy_snap_valid = false;
    if (!OidIsValid(relid) || relid == cb_legacy_root_oid)
    {
        cb_legacy_leaves_valid = false;
        cb_legacy_n_leaves = 0;
        if (cb_legacy_leaf_oids != NULL)
        {
            pfree(cb_legacy_leaf_oids);
            cb_legacy_leaf_oids = NULL;
        }
        if (!OidIsValid(relid))
            cb_legacy_root_oid = InvalidOid;
    }
    else if (cb_legacy_leaves_valid && cb_legacy_leaf_oids != NULL)
    {
        for (int i = 0; i < cb_legacy_n_leaves; i++)
        {
            if (cb_legacy_leaf_oids[i] == relid)
            {
                cb_legacy_leaves_valid = false;
                cb_legacy_n_leaves = 0;
                pfree(cb_legacy_leaf_oids);
                cb_legacy_leaf_oids = NULL;
                break;
            }
        }
    }
}

static void
cb_legacy_xact_cb(XactEvent event, void *arg)
{
    (void) arg;
    if (event == XACT_EVENT_ABORT || event == XACT_EVENT_PARALLEL_ABORT)
        cb_legacy_snap_valid = false;
}

static void
cb_collect_leaf_oids_rec(Oid relid, Oid **oids, int *count, int *cap)
{
    char relkind = get_rel_relkind(relid);

    if (relkind == '\0')
        return;
    if (relkind == RELKIND_PARTITIONED_TABLE)
    {
        List     *children = find_inheritance_children(relid, AccessShareLock);
        ListCell *lc;

        foreach(lc, children)
            cb_collect_leaf_oids_rec(lfirst_oid(lc), oids, count, cap);
        list_free(children);
        return;
    }
    if (*count >= *cap)
    {
        int new_cap = (*cap == 0) ? 256 : (*cap * 2);
        *oids = *oids == NULL
            ? (Oid *) palloc(sizeof(Oid) * (Size) new_cap)
            : (Oid *) repalloc(*oids, sizeof(Oid) * (Size) new_cap);
        *cap = new_cap;
    }
    (*oids)[(*count)++] = relid;
}

static bool
cb_rel_has_any_live_row_uncached(Oid relid, Snapshot snap)
{
    char relkind = get_rel_relkind(relid);
    bool found = false;

    if (relkind == '\0')
        return false;
    if (relkind == RELKIND_PARTITIONED_TABLE)
    {
        List     *children = find_inheritance_children(relid, AccessShareLock);
        ListCell *lc;

        foreach(lc, children)
        {
            found = cb_rel_has_any_live_row_uncached(lfirst_oid(lc), snap);
            if (found)
                break;
        }
        list_free(children);
        return found;
    }

    {
        Relation rel = try_table_open(relid, AccessShareLock);
        if (rel == NULL)
            return false;
        if (RelationGetNumberOfBlocks(rel) > 0)
        {
            Snapshot        scan_snap = snap ? snap : GetTransactionSnapshot();
            TableScanDesc   scan = table_beginscan(rel, scan_snap, 0, NULL);
            TupleTableSlot *slot = table_slot_create(rel, NULL);

            found = table_scan_getnextslot(scan, ForwardScanDirection, slot);
            ExecDropSingleTupleTableSlot(slot);
            table_endscan(scan);
        }
        table_close(rel, AccessShareLock);
    }
    return found;
}

static bool
cb_legacy_consensus_has_rows(void)
{
    Oid       ns;
    Oid       relid;
    Snapshot  snap;
    CommandId cid;
    bool      can_memo;
    bool      found = false;

    if (!cb_legacy_inval_registered)
    {
        CacheRegisterRelcacheCallback(cb_legacy_relcache_inval, (Datum) 0);
        RegisterXactCallback(cb_legacy_xact_cb, NULL);
        cb_legacy_inval_registered = true;
    }

    ns = get_namespace_oid("laplace", true);
    if (!OidIsValid(ns))
        return false;
    relid = get_relname_relid("consensus", ns);
    if (!OidIsValid(relid))
        return false;

    snap = ActiveSnapshotSet() ? GetActiveSnapshot() : NULL;
    cid = GetCurrentCommandId(false);
    can_memo = (snap != NULL);

    if (can_memo &&
        cb_legacy_snap_valid &&
        cb_legacy_snap_root == relid &&
        TransactionIdEquals(cb_legacy_snap_xmin, snap->xmin) &&
        TransactionIdEquals(cb_legacy_snap_xmax, snap->xmax) &&
        cb_legacy_snap_xcnt == snap->xcnt &&
        cb_legacy_snap_cid == cid)
    {
        return cb_legacy_has_rows_cached;
    }

    if (!cb_legacy_leaves_valid || cb_legacy_root_oid != relid)
    {
        Oid *tmp = NULL;
        int  n_leaves = 0;
        int  cap = 0;

        cb_collect_leaf_oids_rec(relid, &tmp, &n_leaves, &cap);
        if (cb_legacy_leaf_oids != NULL)
        {
            pfree(cb_legacy_leaf_oids);
            cb_legacy_leaf_oids = NULL;
        }
        cb_legacy_n_leaves = 0;
        cb_legacy_leaves_valid = false;
        if (n_leaves > 0 && tmp != NULL)
        {
            cb_legacy_leaf_oids = (Oid *) MemoryContextAlloc(
                TopMemoryContext, sizeof(Oid) * (Size) n_leaves);
            memcpy(cb_legacy_leaf_oids, tmp, sizeof(Oid) * (Size) n_leaves);
            cb_legacy_n_leaves = n_leaves;
        }
        if (tmp != NULL)
            pfree(tmp);
        cb_legacy_root_oid = relid;
        cb_legacy_leaves_valid = true;
    }

    for (int i = 0; i < cb_legacy_n_leaves; i++)
    {
        Relation rel = try_table_open(cb_legacy_leaf_oids[i], AccessShareLock);
        if (rel == NULL)
        {
            cb_legacy_leaves_valid = false;
            found = cb_rel_has_any_live_row_uncached(relid, snap);
            break;
        }
        if (RelationGetNumberOfBlocks(rel) > 0)
        {
            Snapshot        scan_snap = snap ? snap : GetTransactionSnapshot();
            TableScanDesc   scan = table_beginscan(rel, scan_snap, 0, NULL);
            TupleTableSlot *slot = table_slot_create(rel, NULL);

            found = table_scan_getnextslot(scan, ForwardScanDirection, slot);
            ExecDropSingleTupleTableSlot(slot);
            table_endscan(scan);
        }
        table_close(rel, AccessShareLock);
        if (found)
            break;
    }

    if (can_memo)
    {
        cb_legacy_snap_valid = true;
        cb_legacy_snap_root = relid;
        cb_legacy_snap_xmin = snap->xmin;
        cb_legacy_snap_xmax = snap->xmax;
        cb_legacy_snap_xcnt = snap->xcnt;
        cb_legacy_snap_cid = cid;
        cb_legacy_has_rows_cached = found;
    }

    return found;
}

typedef struct LegacyAttestAccum
{
    hash128_t claim;
    hash128_t term;
    hash128_t type_id;
    hash128_t key;
    int64     rating;
    int64     rd;
    int64     witnesses;
    int64     games;
    bool      has_p_lang;
    bool      has_any_ctx;
    hash128_t max_ctx;
} LegacyAttestAccum;

typedef struct LegacyAttestCollect
{
    MemoryContext      caller;
    bool               has_lang_filter;
    hash128_t          lang_filter;
    HTAB              *by_triple; /* keyed by (term, type_id, key) -> index */
    LegacyAttestAccum *rows;
    int                count;
    int                cap;
} LegacyAttestCollect;

typedef struct LegacyTripleKey
{
    hash128_t term;
    hash128_t type_id;
    hash128_t key;
} LegacyTripleKey;

typedef struct LegacyTripleEntry
{
    LegacyTripleKey tk;
    int             idx;
} LegacyTripleEntry;

static void
cb_visit_legacy_consensus_cell(const LaplaceConsensusRow *row, void *opaque)
{
    LegacyAttestCollect *col = (LegacyAttestCollect *) opaque;
    LegacyTripleKey      tk;
    LegacyTripleEntry   *te;
    bool                 found = false;
    MemoryContext        prev;

    if (row->object_is_null || laplace_glicko2_refuted(row->rating, row->rd))
        return;

    tk.term = row->subject;
    tk.type_id = row->type;
    tk.key = row->object;

    prev = MemoryContextSwitchTo(col->caller);
    te = (LegacyTripleEntry *) hash_search(col->by_triple, &tk, HASH_ENTER, &found);
    if (!found)
    {
        int idx;
        if (col->count >= col->cap)
        {
            Size sz;
            col->cap = col->cap ? col->cap * 2 : 16;
            sz = sizeof(LegacyAttestAccum) * (Size) col->cap;
            col->rows = col->rows
                ? (LegacyAttestAccum *) repalloc(col->rows, sz)
                : (LegacyAttestAccum *) palloc(sz);
        }
        idx = col->count++;
        te->idx = idx;
        MemSet(&col->rows[idx], 0, sizeof(LegacyAttestAccum));
        col->rows[idx].claim = row->claim;
        col->rows[idx].term = row->subject;
        col->rows[idx].type_id = row->type;
        col->rows[idx].key = row->object;
        col->rows[idx].rating = row->rating;
        col->rows[idx].rd = row->rd;
        col->rows[idx].witnesses = row->witnesses;
    }
    MemoryContextSwitchTo(prev);
}

static AttrNumber
cb_find_attnum(TupleDesc desc, const char *name)
{
    for (int i = 0; i < desc->natts; i++)
    {
        Form_pg_attribute attr = TupleDescAttr(desc, i);
        if (!attr->attisdropped && strcmp(NameStr(attr->attname), name) == 0)
            return attr->attnum;
    }
    return InvalidAttrNumber;
}

static Relation
cb_find_attestations_relation_index(Relation rel, AttrNumber a_subj,
                                    AttrNumber a_type, AttrNumber a_obj)
{
    List     *indexes = RelationGetIndexList(rel);
    ListCell *lc;
    Relation  best = NULL;

    foreach(lc, indexes)
    {
        Oid      idx_oid = lfirst_oid(lc);
        Relation idx = index_open(idx_oid, AccessShareLock);
        if (idx->rd_rel->relam == BTREE_AM_OID &&
            idx->rd_index->indisvalid && idx->rd_index->indisready &&
            idx->rd_index->indnkeyatts >= 3 &&
            idx->rd_index->indkey.values[0] == a_subj &&
            idx->rd_index->indkey.values[1] == a_type &&
            idx->rd_index->indkey.values[2] == a_obj)
        {
            best = idx;
            break;
        }
        index_close(idx, AccessShareLock);
    }
    list_free(indexes);
    return best;
}

static void
cb_scan_attestations_rel(Oid relid, LegacyAttestCollect *col)
{
    char     relkind = get_rel_relkind(relid);
    Snapshot snap = GetActiveSnapshot();

    if (relkind == RELKIND_PARTITIONED_TABLE)
    {
        List     *children = find_inheritance_children(relid, AccessShareLock);
        ListCell *lc;
        foreach(lc, children)
            cb_scan_attestations_rel(lfirst_oid(lc), col);
        list_free(children);
        return;
    }

    {
        Relation   rel = table_open(relid, AccessShareLock);
        TupleDesc  desc = RelationGetDescr(rel);
        AttrNumber a_subj = cb_find_attnum(desc, "subject_id");
        AttrNumber a_type = cb_find_attnum(desc, "type_id");
        AttrNumber a_obj = cb_find_attnum(desc, "object_id");
        AttrNumber a_ctx = cb_find_attnum(desc, "context_id");
        AttrNumber a_obs = cb_find_attnum(desc, "observation_count");
        Relation   idx;
        TupleTableSlot *slot;

        if (a_subj == InvalidAttrNumber || a_type == InvalidAttrNumber ||
            a_obj == InvalidAttrNumber || a_ctx == InvalidAttrNumber ||
            a_obs == InvalidAttrNumber)
        {
            table_close(rel, AccessShareLock);
            return;
        }

        idx = cb_find_attestations_relation_index(rel, a_subj, a_type, a_obj);
        slot = table_slot_create(rel, NULL);
        if (idx != NULL)
        {
            IndexScanDesc scan = index_beginscan(rel, idx, snap, NULL, 3, 0);
            for (int i = 0; i < col->count; i++)
            {
                ScanKeyData sk[3];
                bytea *sb = (bytea *) hash128_to_datum(&col->rows[i].term);
                bytea *tb = (bytea *) hash128_to_datum(&col->rows[i].type_id);
                bytea *ob = (bytea *) hash128_to_datum(&col->rows[i].key);

                ScanKeyEntryInitialize(&sk[0], 0, 1, BTEqualStrategyNumber,
                    BYTEAOID, InvalidOid, F_BYTEAEQ, PointerGetDatum(sb));
                ScanKeyEntryInitialize(&sk[1], 0, 2, BTEqualStrategyNumber,
                    BYTEAOID, InvalidOid, F_BYTEAEQ, PointerGetDatum(tb));
                ScanKeyEntryInitialize(&sk[2], 0, 3, BTEqualStrategyNumber,
                    BYTEAOID, InvalidOid, F_BYTEAEQ, PointerGetDatum(ob));
                index_rescan(scan, sk, 3, NULL, 0);
                while (index_getnext_slot(scan, ForwardScanDirection, slot))
                {
                    bool  isnull = false;
                    Datum d_obs = slot_getattr(slot, a_obs, &isnull);
                    Datum d_ctx = slot_getattr(slot, a_ctx, &isnull);
                    if (!isnull)
                    {
                        hash128_t ctx = datum_to_hash128(d_ctx);
                        if (col->has_lang_filter && hash128_eq(&ctx, &col->lang_filter))
                            col->rows[i].has_p_lang = true;
                        if (!col->rows[i].has_any_ctx ||
                            memcmp(&ctx, &col->rows[i].max_ctx, sizeof(hash128_t)) > 0)
                        {
                            col->rows[i].has_any_ctx = true;
                            col->rows[i].max_ctx = ctx;
                        }
                    }
                    {
                        bool obs_null = false;
                        Datum ov = slot_getattr(slot, a_obs, &obs_null);
                        if (!obs_null)
                            col->rows[i].games += DatumGetInt64(ov);
                    }
                    (void) d_obs;
                    ExecClearTuple(slot);
                }
                pfree(sb);
                pfree(tb);
                pfree(ob);
            }
            index_endscan(scan);
            index_close(idx, AccessShareLock);
        }
        else
        {
            TableScanDesc scan = table_beginscan(rel, snap, 0, NULL);
            while (table_scan_getnextslot(scan, ForwardScanDirection, slot))
            {
                bool             isnull = false;
                Datum            d;
                LegacyTripleKey  tk;
                LegacyTripleEntry *te;

                d = slot_getattr(slot, a_obj, &isnull);
                if (isnull)
                    continue;
                tk.key = datum_to_hash128(d);
                d = slot_getattr(slot, a_subj, &isnull);
                if (isnull)
                    continue;
                tk.term = datum_to_hash128(d);
                d = slot_getattr(slot, a_type, &isnull);
                if (isnull)
                    continue;
                tk.type_id = datum_to_hash128(d);
                te = (LegacyTripleEntry *) hash_search(col->by_triple, &tk, HASH_FIND, NULL);
                if (te != NULL)
                {
                    int   idx_row = te->idx;
                    Datum d_obs = slot_getattr(slot, a_obs, &isnull);
                    if (!isnull)
                        col->rows[idx_row].games += DatumGetInt64(d_obs);
                    d = slot_getattr(slot, a_ctx, &isnull);
                    if (!isnull)
                    {
                        hash128_t ctx = datum_to_hash128(d);
                        if (col->has_lang_filter && hash128_eq(&ctx, &col->lang_filter))
                            col->rows[idx_row].has_p_lang = true;
                        if (!col->rows[idx_row].has_any_ctx ||
                            memcmp(&ctx, &col->rows[idx_row].max_ctx, sizeof(hash128_t)) > 0)
                        {
                            col->rows[idx_row].has_any_ctx = true;
                            col->rows[idx_row].max_ctx = ctx;
                        }
                    }
                }
                ExecClearTuple(slot);
            }
            table_endscan(scan);
        }
        ExecDropSingleTupleTableSlot(slot);
        table_close(rel, AccessShareLock);
    }
}

static void
cb_fill_legacy_attestations(LegacyAttestCollect *col)
{
    Oid ns = get_namespace_oid("laplace", true);
    Oid relid;

    if (col->count == 0 || !OidIsValid(ns))
        return;
    relid = get_relname_relid("attestations", ns);
    if (!OidIsValid(relid))
        return;
    cb_scan_attestations_rel(relid, col);
}

/* ========================================================================
 * 3. Shared converse.binding_evidence / converse.key_binding_evidence core
 * ======================================================================== */

typedef struct CbHolderItem
{
    hash128_t anchor; /* word if !by_key, key if by_key */
    hash128_t entity;
    hash128_t parts[3];
} CbHolderItem;

typedef struct CbHolderCtx
{
    MemoryContext caller;
    HTAB         *roots;
    bool          by_key;
    bool          had_any_tuple;
    CbHolderItem *items;
    int           count;
    int           cap;
} CbHolderCtx;

static bool
cb_visit_holder_tuple(TupleTableSlot *slot, void *opaque)
{
    CbHolderCtx *ctx = (CbHolderCtx *) opaque;
    bool         isnull = false;
    Datum        d_ent, d_path, d_mask;
    lp_path      path;
    lp_id        parts[3];
    hash128_t    p0, p1, p2, anchor;
    MemoryContext prev;

    d_mask = slot_getattr(slot, 5, &isnull);
    if (isnull || !laplace_pub_mask_has_value(d_mask, "kind", "tuple"))
        return true;

    d_path = slot_getattr(slot, 3, &isnull);
    if (isnull)
        return true;
    path = laplace_pub_geo_path(d_path);
    if (lp_path_len(path) != 3 || lp_path_expand(path, parts, 3) != 3)
        return true;

    ctx->had_any_tuple = true;

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;

    memcpy(&p0, parts[0].b, sizeof(hash128_t));
    memcpy(&p1, parts[1].b, sizeof(hash128_t));
    memcpy(&p2, parts[2].b, sizeof(hash128_t));
    anchor = ctx->by_key ? p2 : p0;
    if (hash_search(ctx->roots, &anchor, HASH_FIND, NULL) == NULL)
        return true;

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count >= ctx->cap)
    {
        Size sz;
        ctx->cap = ctx->cap ? ctx->cap * 2 : 32;
        sz = sizeof(CbHolderItem) * (Size) ctx->cap;
        ctx->items = ctx->items
            ? (CbHolderItem *) repalloc(ctx->items, sz)
            : (CbHolderItem *) palloc(sz);
    }
    ctx->items[ctx->count].anchor = anchor;
    ctx->items[ctx->count].entity = laplace_pub_blake3_to_hash128(d_ent);
    ctx->items[ctx->count].parts[0] = p0;
    ctx->items[ctx->count].parts[1] = p1;
    ctx->items[ctx->count].parts[2] = p2;
    ctx->count++;
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct CbClaimCell
{
    hash128_t entity;
    int       n_parts;
    hash128_t parts[3];
    double    rating;
    double    deviation;
    int64     matches;
} CbClaimCell;

typedef struct CbClaimsCtx
{
    MemoryContext caller;
    CbClaimCell  *items;
    int           count;
    int           cap;
} CbClaimsCtx;

static bool
cb_visit_claim_cell(TupleTableSlot *slot, void *opaque)
{
    CbClaimsCtx  *ctx = (CbClaimsCtx *) opaque;
    bool          isnull = false;
    Datum         d_ent, d_path, d_rat, d_dev, d_mat;
    lp_path       path;
    uint32        len;
    lp_id         parts[3];
    double        rating = 0.0, dev = 0.0;
    int64         matches = 0;
    MemoryContext prev;

    d_path = slot_getattr(slot, 3, &isnull);
    if (isnull)
        return true;
    path = laplace_pub_geo_path(d_path);
    len = lp_path_len(path);
    if (len != 2 && len != 3)
        return true;
    if (lp_path_expand(path, parts, (int) len) != len)
        return true;

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;
    d_rat = slot_getattr(slot, 4, &isnull);
    if (!isnull)
        rating = DatumGetFloat8(d_rat);
    d_dev = slot_getattr(slot, 5, &isnull);
    if (!isnull)
        dev = DatumGetFloat8(d_dev);
    d_mat = slot_getattr(slot, 7, &isnull);
    if (!isnull)
        matches = DatumGetInt64(d_mat);

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count >= ctx->cap)
    {
        Size sz;
        ctx->cap = ctx->cap ? ctx->cap * 2 : 64;
        sz = sizeof(CbClaimCell) * (Size) ctx->cap;
        ctx->items = ctx->items
            ? (CbClaimCell *) repalloc(ctx->items, sz)
            : (CbClaimCell *) palloc(sz);
    }
    ctx->items[ctx->count].entity = laplace_pub_blake3_to_hash128(d_ent);
    ctx->items[ctx->count].n_parts = (int) len;
    for (uint32 i = 0; i < len; i++)
        memcpy(&ctx->items[ctx->count].parts[i], parts[i].b, sizeof(hash128_t));
    ctx->items[ctx->count].rating = rating;
    ctx->items[ctx->count].deviation = dev;
    ctx->items[ctx->count].matches = matches;
    ctx->count++;
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct CbSenseIndex
{
    hash128_t sense;
    int       first;
} CbSenseIndex;

typedef struct CbWitnessEntry
{
    hash128_t claim;
    hash128_t trunk;
} CbWitnessEntry;

typedef struct CbWitnessCount
{
    hash128_t claim;
    int64     witnesses;
} CbWitnessCount;

typedef struct CbAttestCtx
{
    MemoryContext caller;
    HTAB         *seen;
    HTAB         *counts;
} CbAttestCtx;

static bool
cb_visit_attested_count(TupleTableSlot *slot, void *opaque)
{
    CbAttestCtx    *ctx = (CbAttestCtx *) opaque;
    bool            isnull = false;
    Datum           d_wit, d_claim, d_trunk;
    CbWitnessEntry  tk;
    bool            found = false;
    MemoryContext   prev;

    d_wit = slot_getattr(slot, 8, &isnull);
    if (isnull || !DatumGetBool(d_wit))
        return true;
    d_claim = slot_getattr(slot, 1, &isnull);
    if (isnull)
        return true;
    d_trunk = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;

    tk.claim = laplace_pub_blake3_to_hash128(d_claim);
    tk.trunk = laplace_pub_blake3_to_hash128(d_trunk);

    prev = MemoryContextSwitchTo(ctx->caller);
    (void) hash_search(ctx->seen, &tk, HASH_ENTER, &found);
    if (!found)
    {
        bool            cf = false;
        CbWitnessCount *wc = (CbWitnessCount *) hash_search(
            ctx->counts, &tk.claim, HASH_ENTER, &cf);
        wc->witnesses = cf ? (wc->witnesses + 1) : 1;
    }
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct CbUsageFreq
{
    hash128_t obj;
    int64     freq;
} CbUsageFreq;

typedef struct CbUsageFreqCtx
{
    MemoryContext caller;
    HTAB         *freqs;
} CbUsageFreqCtx;

static bool
cb_parse_u18_bigint(const char *s, int64 *out_val)
{
    size_t len;
    int64  acc = 0;

    if (s == NULL)
        return false;
    len = strlen(s);
    if (len == 0 || len > 18)
        return false;
    for (size_t i = 0; i < len; i++)
    {
        unsigned char c = (unsigned char) s[i];
        if (c < '0' || c > '9')
            return false;
        acc = acc * 10 + (int64) (c - '0');
    }
    *out_val = acc;
    return true;
}

static bool
cb_visit_usage_label(TupleTableSlot *slot, void *opaque)
{
    CbUsageFreqCtx *ctx = (CbUsageFreqCtx *) opaque;
    bool            isnull = false;
    Datum           d_id, d_lbl;
    hash128_t       id;
    char           *str;
    int64           freq = 0;
    MemoryContext   prev;

    d_id = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;
    d_lbl = slot_getattr(slot, 3, &isnull);
    if (isnull)
        return true;

    id = laplace_pub_blake3_to_hash128(d_id);
    str = TextDatumGetCString(d_lbl);
    if (cb_parse_u18_bigint(str, &freq))
    {
        bool         found = false;
        CbUsageFreq *uf;
        prev = MemoryContextSwitchTo(ctx->caller);
        uf = (CbUsageFreq *) hash_search(ctx->freqs, &id, HASH_ENTER, &found);
        uf->freq = freq;
        MemoryContextSwitchTo(prev);
    }
    pfree(str);
    return true;
}

typedef struct CbSenseUsage
{
    hash128_t sense;
    double    max_eff_mu;
    int64     max_matches;
} CbSenseUsage;

typedef struct CbTupleDedupKey
{
    hash128_t term;
    hash128_t key;
    hash128_t binding;
} CbTupleDedupKey;

typedef struct CbTupleWinner
{
    CbTupleDedupKey k;
    hash128_t       claim;
    hash128_t       language;
    bool            in_lang;
    double          eff_mu;
    int64           witnesses;
    bool            witnesses_null;
    int64           games;
} CbTupleWinner;

typedef struct CbKbSurfEntry
{
    hash128_t surface;
    bool      inverse;
} CbKbSurfEntry;

typedef struct CbDirectClaimRow
{
    hash128_t term;
    hash128_t key;
    hash128_t entity;
    double    rating;
    double    deviation;
    int64     matches;
} CbDirectClaimRow;

typedef struct CbDirectClaimCtx
{
    MemoryContext    caller;
    HTAB            *surfaces;
    HTAB            *roots;
    bool             by_key;
    CbDirectClaimRow *items;
    int              count;
    int              cap;
} CbDirectClaimCtx;

static bool
cb_visit_direct_claim(TupleTableSlot *slot, void *opaque)
{
    CbDirectClaimCtx *ctx = (CbDirectClaimCtx *) opaque;
    bool              isnull = false;
    Datum             d_ent, d_path, d_rat, d_dev, d_mat;
    lp_path           path;
    lp_id             parts[3];
    hash128_t         p0, p1, p2, term, key, anchor;
    double            rating = 0.0, dev = 0.0;
    int64             matches = 0;
    CbKbSurfEntry    *se;
    MemoryContext     prev;

    d_mat = slot_getattr(slot, 7, &isnull);
    if (!isnull)
        matches = DatumGetInt64(d_mat);
    if (matches <= 0)
        return true;

    d_rat = slot_getattr(slot, 4, &isnull);
    if (!isnull)
        rating = DatumGetFloat8(d_rat);
    d_dev = slot_getattr(slot, 5, &isnull);
    if (!isnull)
        dev = DatumGetFloat8(d_dev);
    if ((rating - 2.0 * dev) <= 0.0)
        return true;

    d_path = slot_getattr(slot, 3, &isnull);
    if (isnull)
        return true;
    path = laplace_pub_geo_path(d_path);
    if (lp_path_len(path) != 3 || lp_path_expand(path, parts, 3) != 3)
        return true;

    memcpy(&p0, parts[0].b, sizeof(hash128_t));
    memcpy(&p1, parts[1].b, sizeof(hash128_t));
    memcpy(&p2, parts[2].b, sizeof(hash128_t));

    se = (CbKbSurfEntry *) hash_search(ctx->surfaces, &p1, HASH_FIND, NULL);
    if (se == NULL)
        return true;

    term = se->inverse ? p2 : p0;
    key  = se->inverse ? p0 : p2;
    anchor = ctx->by_key ? key : term;
    if (hash_search(ctx->roots, &anchor, HASH_FIND, NULL) == NULL)
        return true;

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count >= ctx->cap)
    {
        Size sz;
        ctx->cap = ctx->cap ? ctx->cap * 2 : 32;
        sz = sizeof(CbDirectClaimRow) * (Size) ctx->cap;
        ctx->items = ctx->items
            ? (CbDirectClaimRow *) repalloc(ctx->items, sz)
            : (CbDirectClaimRow *) palloc(sz);
    }
    ctx->items[ctx->count].term = term;
    ctx->items[ctx->count].key = key;
    ctx->items[ctx->count].entity = laplace_pub_blake3_to_hash128(d_ent);
    ctx->items[ctx->count].rating = rating;
    ctx->items[ctx->count].deviation = dev;
    ctx->items[ctx->count].matches = matches;
    ctx->count++;
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct CbTupleMaskCtx
{
    MemoryContext caller;
    HTAB         *tuple_ids;
} CbTupleMaskCtx;

static bool
cb_visit_tuple_mask(TupleTableSlot *slot, void *opaque)
{
    CbTupleMaskCtx *ctx = (CbTupleMaskCtx *) opaque;
    bool            isnull = false;
    Datum           d_ent, d_mask;
    hash128_t       ent;
    MemoryContext   prev;

    d_mask = slot_getattr(slot, 6, &isnull);
    if (isnull || !laplace_pub_mask_has_value(d_mask, "kind", "tuple"))
        return true;
    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;
    ent = laplace_pub_blake3_to_hash128(d_ent);

    prev = MemoryContextSwitchTo(ctx->caller);
    (void) hash_search(ctx->tuple_ids, &ent, HASH_ENTER, &isnull);
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct CbTermLangWinner
{
    hash128_t term;
    hash128_t lang;
    bool      is_req_lang;
    int64     eff;
} CbTermLangWinner;

static int
cb_winner_cmp(const void *a, const void *b)
{
    const ConverseBindingRow *x = (const ConverseBindingRow *) a;
    const ConverseBindingRow *y = (const ConverseBindingRow *) b;
    int c;

    c = memcmp(&x->term, &y->term, sizeof(hash128_t));
    if (c != 0)
        return c;
    c = memcmp(&x->key, &y->key, sizeof(hash128_t));
    if (c != 0)
        return c;
    return memcmp(&x->binding, &y->binding, sizeof(hash128_t));
}

static int
cb_winner_bykey_cmp(const void *a, const void *b)
{
    const ConverseBindingRow *x = (const ConverseBindingRow *) a;
    const ConverseBindingRow *y = (const ConverseBindingRow *) b;
    int c;

    c = memcmp(&x->key, &y->key, sizeof(hash128_t));
    if (c != 0)
        return c;
    c = memcmp(&x->term, &y->term, sizeof(hash128_t));
    if (c != 0)
        return c;
    return memcmp(&x->binding, &y->binding, sizeof(hash128_t));
}

static ConverseBindingRow *
converse_binding_evidence_core(const hash128_t *inputs, int n_inputs,
                               const hash128_t *lang, bool support,
                               bool by_key, int *out_count)
{
    MemoryContext       caller = CurrentMemoryContext;
    MemoryContext       work, old;
    hash128_t          *roots;
    int                 n_roots;
    HTAB               *roots_set;
    Oid                 b3_arr_oid = laplace_pub_blake3_array_oid();
    int16               tuple_bit, claim_bit;
    ConverseBindingRow *result = NULL;
    int                 n_out = 0;

    *out_count = 0;
    if (inputs == NULL || n_inputs <= 0)
        return NULL;

    tuple_bit = laplace_pub_bank_bit("kind", "tuple");
    claim_bit = laplace_pub_bank_bit("kind", "claim");

    work = AllocSetContextCreate(caller, "converse_binding_evidence",
                                 ALLOCSET_DEFAULT_SIZES);
    old = MemoryContextSwitchTo(work);

    roots = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_inputs);
    n_roots = cb_dedup_ids(inputs, n_inputs, roots);

    roots_set = cb_hash_create_id(work, "cb roots", sizeof(LexIdEntry), n_roots);
    for (int i = 0; i < n_roots; i++)
    {
        bool f = false;
        (void) hash_search(roots_set, &roots[i], HASH_ENTER, &f);
    }

    /* ---- Path 1: Native 3-part lexical tuples [word, lang, key] ---- */
    {
        CbHolderCtx hctx;
        ArrayType  *roots_b3 = laplace_pub_blake3_array(roots, n_roots);
        ArrayType  *empty_b3 = laplace_pub_blake3_array(NULL, 0);
        Datum       bits_d[1] = { Int16GetDatum(tuple_bit) };
        ArrayType  *bits_arr = construct_array(bits_d, 1, INT2OID, sizeof(int16), true, TYPALIGN_SHORT);
        Oid         h_types[8] = {
            b3_arr_oid, b3_arr_oid, BOOLOID, INT2OID,
            INT8OID, INT2OID, INT2ARRAYOID, INT2OID
        };
        Datum       h_args[8] = {
            PointerGetDatum(roots_b3),
            PointerGetDatum(empty_b3),
            BoolGetDatum(false),
            Int16GetDatum(-1),
            Int64GetDatum(-1),
            Int16GetDatum(2),
            PointerGetDatum(bits_arr),
            Int16GetDatum(by_key ? 2 : 1)
        };
        bool        h_nulls[8] = { false, false, false, false, false, false, false, false };

        MemSet(&hctx, 0, sizeof(hctx));
        hctx.caller = work;
        hctx.roots = roots_set;
        hctx.by_key = by_key;

        (void) laplace_pub_call_srf("public", "laplace_holders", 8,
                                    h_types, h_args, h_nulls,
                                    cb_visit_holder_tuple, &hctx);

        if (hctx.count > 0)
        {
            hash128_t     *lang_cands = (hash128_t *) palloc(sizeof(hash128_t) * (Size) hctx.count);
            hash128_t     *uniq_langs = (hash128_t *) palloc(sizeof(hash128_t) * (Size) hctx.count);
            int            n_uniq_langs;
            HTAB          *valid_langs;
            ArrayType     *lang_arr;
            CbHolderItem  *lexical = (CbHolderItem *) palloc(sizeof(CbHolderItem) * (Size) hctx.count);
            int            n_lex = 0;

            for (int i = 0; i < hctx.count; i++)
                lang_cands[i] = hctx.items[i].parts[1];
            n_uniq_langs = cb_dedup_ids(lang_cands, hctx.count, uniq_langs);
            valid_langs = cb_hash_create_id(work, "cb valid langs", sizeof(LexIdEntry), n_uniq_langs);
            lang_arr = laplace_pub_blake3_array(uniq_langs, n_uniq_langs);
            {
                Datum      la[1] = { PointerGetDatum(lang_arr) };
                bool       ln[1] = { false };
                LexLangCtx lctx = { work, valid_langs };

                (void) laplace_pub_call_srf("public", "laplace_labels", 1,
                                            &b3_arr_oid, la, ln,
                                            cb_visit_lex_lang_label, &lctx);
            }
            pfree(lang_arr);

            for (int i = 0; i < hctx.count; i++)
            {
                if (hash_search(valid_langs, &hctx.items[i].parts[1], HASH_FIND, NULL) == NULL)
                    continue;
                if (by_key && lang != NULL && !hash128_eq(&hctx.items[i].parts[1], lang))
                    continue;
                lexical[n_lex++] = hctx.items[i];
            }

            if (n_lex > 0)
            {
                hash128_t   *lex_ents = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_lex);
                hash128_t   *uniq_lex = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_lex);
                int          n_uniq_lex;
                ArrayType   *lex_arr;
                Datum        cbits_d[1] = { Int16GetDatum(claim_bit) };
                ArrayType   *cbits_arr = construct_array(cbits_d, 1, INT2OID, sizeof(int16), true, TYPALIGN_SHORT);
                Oid          c_types[4] = { b3_arr_oid, INT8OID, INT2ARRAYOID, b3_arr_oid };
                Datum        c_args[4];
                bool         c_nulls[4] = { false, true, false, false };
                CbClaimsCtx  cctx;

                for (int i = 0; i < n_lex; i++)
                    lex_ents[i] = lexical[i].entity;
                n_uniq_lex = cb_dedup_ids(lex_ents, n_lex, uniq_lex);
                lex_arr = laplace_pub_blake3_array(uniq_lex, n_uniq_lex);

                c_args[0] = PointerGetDatum(lex_arr);
                c_args[1] = (Datum) 0;
                c_args[2] = PointerGetDatum(cbits_arr);
                c_args[3] = PointerGetDatum(empty_b3);

                MemSet(&cctx, 0, sizeof(cctx));
                cctx.caller = work;
                (void) laplace_pub_call_srf("public", "laplace_claims_each", 4,
                                            c_types, c_args, c_nulls,
                                            cb_visit_claim_cell, &cctx);
                pfree(lex_arr);
                pfree(cbits_arr);

                if (cctx.count > 0)
                {
                    /* Index sense_id -> lexical[] list via linked slots. */
                    HTAB         *sense_head = cb_hash_create_id(work, "cb sense head",
                                                                 sizeof(CbSenseIndex), n_uniq_lex);
                    int          *lex_next = (int *) palloc(sizeof(int) * (Size) n_lex);
                    HTAB         *usage_freq = NULL;
                    HTAB         *sense_usage;
                    HTAB         *witness_counts = NULL;
                    HTAB         *winners;
                    int           n_bind = 0;
                    int          *bind_lex = (int *) palloc(sizeof(int) * (Size) (cctx.count * 2 + 16));
                    int          *bind_cell = (int *) palloc(sizeof(int) * (Size) (cctx.count * 2 + 16));
                    int           bind_cap = cctx.count * 2 + 16;
                    hash128_t    *usage_objs = NULL;
                    int           n_usage_objs = 0;
                    int           usage_cap = 0;

                    for (int li = 0; li < n_lex; li++)
                    {
                        bool          f = false;
                        CbSenseIndex *si = (CbSenseIndex *) hash_search(
                            sense_head, &lexical[li].entity, HASH_ENTER, &f);
                        lex_next[li] = f ? si->first : -1;
                        si->first = li;
                    }

                    /* Scan claims against matching senses for binding_cells and usage_objs. */
                    for (int ci = 0; ci < cctx.count; ci++)
                    {
                        CbClaimCell *cell = &cctx.items[ci];
                        int          cand_slots[2] = { 0, cell->n_parts - 1 };
                        bool         same_ends = hash128_eq(&cell->parts[0], &cell->parts[cell->n_parts - 1]);
                        int          n_slots = same_ends ? 1 : 2;
                        double       cell_mu = cell->rating - 2.0 * cell->deviation;

                        for (int s = 0; s < n_slots; s++)
                        {
                            int           s_idx = cand_slots[s];
                            int           other_idx = (s_idx == 0) ? (cell->n_parts - 1) : 0;
                            CbSenseIndex *si = (CbSenseIndex *) hash_search(
                                sense_head, &cell->parts[s_idx], HASH_FIND, NULL);
                            if (si == NULL)
                                continue;

                            for (int li = si->first; li >= 0; li = lex_next[li])
                            {
                                const CbHolderItem *t = &lexical[li];
                                bool matches_binding = hash128_eq(&cell->parts[other_idx], &t->parts[0]) ||
                                                       hash128_eq(&cell->parts[other_idx], &t->parts[2]);
                                if (matches_binding)
                                {
                                    if (!by_key || (cell->matches > 0 && cell_mu > 0.0))
                                    {
                                        if (n_bind >= bind_cap)
                                        {
                                            bind_cap *= 2;
                                            bind_lex = (int *) repalloc(bind_lex, sizeof(int) * (Size) bind_cap);
                                            bind_cell = (int *) repalloc(bind_cell, sizeof(int) * (Size) bind_cap);
                                        }
                                        bind_lex[n_bind] = li;
                                        bind_cell[n_bind] = ci;
                                        n_bind++;
                                    }
                                }

                                /* usage_objs / sense_usage: c.parts[0] == sense && len == 3 && matches > 1 */
                                if (s_idx == 0 && cell->n_parts == 3 && cell->matches > 1 &&
                                    cell_mu > 0.0 &&
                                    !hash128_eq(&cell->parts[2], &t->parts[0]) &&
                                    !hash128_eq(&cell->parts[2], &t->parts[2]))
                                {
                                    if (n_usage_objs >= usage_cap)
                                    {
                                        usage_cap = usage_cap ? usage_cap * 2 : cctx.count;
                                        usage_objs = usage_objs
                                            ? (hash128_t *) repalloc(usage_objs, sizeof(hash128_t) * (Size) usage_cap)
                                            : (hash128_t *) palloc(sizeof(hash128_t) * (Size) usage_cap);
                                    }
                                    usage_objs[n_usage_objs++] = cell->parts[2];
                                }
                            }
                        }
                    }

                    /* Resolve usage_objs integer frequencies if any. */
                    usage_freq = cb_hash_create_id(work, "cb usage freq", sizeof(CbUsageFreq),
                                                   Max(n_usage_objs, 16));
                    if (n_usage_objs > 0)
                    {
                        hash128_t     *uniq_uo = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_usage_objs);
                        int            n_uniq_uo = cb_dedup_ids(usage_objs, n_usage_objs, uniq_uo);
                        ArrayType     *uo_arr = laplace_pub_blake3_array(uniq_uo, n_uniq_uo);
                        Datum          ua[1] = { PointerGetDatum(uo_arr) };
                        bool           un[1] = { false };
                        CbUsageFreqCtx ufctx = { work, usage_freq };

                        (void) laplace_pub_call_srf("public", "laplace_labels", 1,
                                                    &b3_arr_oid, ua, un,
                                                    cb_visit_usage_label, &ufctx);
                        pfree(uo_arr);
                    }

                    /* Build sense_usage per sense entity. */
                    sense_usage = cb_hash_create_id(work, "cb sense usage",
                                                    sizeof(CbSenseUsage), n_uniq_lex);
                    for (int ci = 0; ci < cctx.count; ci++)
                    {
                        CbClaimCell  *cell = &cctx.items[ci];
                        CbSenseIndex *si;
                        double        cell_mu = cell->rating - 2.0 * cell->deviation;
                        CbUsageFreq  *uf;
                        int64         freq = 0;
                        double        eff;
                        int64         mm;

                        if (cell->n_parts != 3 || cell->matches <= 1 || cell_mu <= 0.0)
                            continue;
                        si = (CbSenseIndex *) hash_search(sense_head, &cell->parts[0], HASH_FIND, NULL);
                        if (si == NULL)
                            continue;

                        uf = (CbUsageFreq *) hash_search(usage_freq, &cell->parts[2], HASH_FIND, NULL);
                        if (uf != NULL)
                            freq = uf->freq;
                        eff = cell_mu + (double) freq;
                        mm = (cell->matches > freq) ? cell->matches : freq;

                        for (int li = si->first; li >= 0; li = lex_next[li])
                        {
                            const CbHolderItem *t = &lexical[li];
                            if (!hash128_eq(&cell->parts[2], &t->parts[0]) &&
                                !hash128_eq(&cell->parts[2], &t->parts[2]))
                            {
                                bool          f = false;
                                CbSenseUsage *su = (CbSenseUsage *) hash_search(
                                    sense_usage, &t->entity, HASH_ENTER, &f);
                                if (!f)
                                {
                                    su->max_eff_mu = eff;
                                    su->max_matches = mm;
                                }
                                else
                                {
                                    if (eff > su->max_eff_mu)
                                        su->max_eff_mu = eff;
                                    if (mm > su->max_matches)
                                        su->max_matches = mm;
                                }
                            }
                        }
                    }

                    /* Witness counts when !by_key && support. */
                    if (!by_key && support && n_bind > 0)
                    {
                        hash128_t   *claim_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_bind);
                        hash128_t   *uniq_claims = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_bind);
                        int          n_uniq_claims;
                        HTAB        *seen_tk;
                        ArrayType   *claims_arr;
                        Oid          at_types[2] = { b3_arr_oid, BOOLOID };
                        Datum        at_args[2];
                        bool         at_nulls[2] = { false, false };
                        CbAttestCtx  actx;

                        for (int i = 0; i < n_bind; i++)
                            claim_ids[i] = cctx.items[bind_cell[i]].entity;
                        n_uniq_claims = cb_dedup_ids(claim_ids, n_bind, uniq_claims);

                        seen_tk = cb_hash_create_key(work, "cb attest seen",
                                                     sizeof(CbWitnessEntry),
                                                     sizeof(CbWitnessEntry),
                                                     n_uniq_claims * 4);
                        witness_counts = cb_hash_create_id(work, "cb witness cnt",
                                                           sizeof(CbWitnessCount),
                                                           n_uniq_claims);
                        claims_arr = laplace_pub_blake3_array(uniq_claims, n_uniq_claims);
                        at_args[0] = PointerGetDatum(claims_arr);
                        at_args[1] = BoolGetDatum(false);

                        actx.caller = work;
                        actx.seen = seen_tk;
                        actx.counts = witness_counts;
                        (void) laplace_pub_call_srf("public", "laplace_attested", 2,
                                                    at_types, at_args, at_nulls,
                                                    cb_visit_attested_count, &actx);
                        pfree(claims_arr);
                    }

                    /* Deduplicate per (term, key, binding) selecting best claim. */
                    winners = cb_hash_create_key(work, "cb winners",
                                                 sizeof(CbTupleDedupKey),
                                                 sizeof(CbTupleWinner),
                                                 Max(n_lex, 16));
                    for (int bi = 0; bi < n_bind; bi++)
                    {
                        const CbHolderItem *t = &lexical[bind_lex[bi]];
                        const CbClaimCell  *c = &cctx.items[bind_cell[bi]];
                        CbSenseUsage       *su = (CbSenseUsage *) hash_search(
                            sense_usage, &t->entity, HASH_FIND, NULL);
                        int64               w_cnt = 0;
                        bool                w_null = false;
                        double              base_mu = c->rating - 2.0 * c->deviation;
                        double              rhs_mu = su ? su->max_eff_mu : 0.0;
                        double              eff_mu = (base_mu > rhs_mu) ? base_mu : rhs_mu;
                        int64               rhs_m = su ? su->max_matches : (by_key ? 1 : 0);
                        int64               games_m = su ? su->max_matches : 0;
                        int64               games = (c->matches > games_m) ? c->matches : games_m;
                        CbTupleDedupKey     dk;
                        CbTupleWinner      *w;
                        bool                found = false;

                        if (by_key)
                        {
                            w_cnt = (c->matches > rhs_m) ? c->matches : rhs_m;
                        }
                        else if (support)
                        {
                            CbWitnessCount *wc = witness_counts
                                ? (CbWitnessCount *) hash_search(witness_counts, &c->entity, HASH_FIND, NULL)
                                : NULL;
                            if (wc == NULL || wc->witnesses <= 0)
                                continue;
                            w_cnt = wc->witnesses;
                        }
                        else
                        {
                            if (c->matches <= 0)
                                continue;
                            w_null = true;
                        }

                        dk.term = t->parts[0];
                        dk.key = t->parts[2];
                        dk.binding = t->entity;
                        w = (CbTupleWinner *) hash_search(winners, &dk, HASH_ENTER, &found);
                        if (!found)
                        {
                            w->claim = c->entity;
                            w->language = t->parts[1];
                            w->in_lang = lang ? hash128_eq(&t->parts[1], lang) : (by_key ? true : false);
                            w->eff_mu = eff_mu;
                            w->witnesses = w_cnt;
                            w->witnesses_null = w_null;
                            w->games = games;
                        }
                        else
                        {
                            bool better = false;
                            if (eff_mu > w->eff_mu)
                                better = true;
                            else if (eff_mu == w->eff_mu && games > w->games)
                                better = true;
                            else if (eff_mu == w->eff_mu && games == w->games &&
                                     memcmp(&c->entity, &w->claim, sizeof(hash128_t)) < 0)
                                better = true;
                            if (better)
                            {
                                w->claim = c->entity;
                                w->witnesses = w_cnt;
                                w->witnesses_null = w_null;
                                w->games = games;
                            }
                        }
                    }

                    n_out = hash_get_num_entries(winners);
                    if (n_out > 0)
                    {
                        HASH_SEQ_STATUS seq;
                        CbTupleWinner  *w;
                        int             idx = 0;

                        MemoryContextSwitchTo(caller);
                        result = (ConverseBindingRow *) MemoryContextAlloc(
                            caller, sizeof(ConverseBindingRow) * (Size) n_out);
                        hash_seq_init(&seq, winners);
                        while ((w = (CbTupleWinner *) hash_seq_search(&seq)) != NULL)
                        {
                            ConverseBindingRow *dst = &result[idx++];
                            dst->term = w->k.term;
                            dst->key = w->k.key;
                            dst->binding = w->k.binding;
                            dst->claim = w->claim;
                            dst->language = w->language;
                            dst->language_null = false;
                            dst->in_lang = w->in_lang;
                            dst->eff_mu = w->eff_mu;
                            dst->witnesses = w->witnesses;
                            dst->witnesses_null = w->witnesses_null;
                            dst->games = w->games;
                            dst->rating = (int64) llround(w->eff_mu * 1e9);
                            dst->rd = 0;
                        }
                        if (n_out > 1)
                            qsort(result, (Size) n_out, sizeof(ConverseBindingRow),
                                  by_key ? cb_winner_bykey_cmp : cb_winner_cmp);

                        MemoryContextDelete(work);
                        *out_count = n_out;
                        return result;
                    }
                }
            }
        }

        /* _key_binding_evidence_impl lines 320-330: if p_lang IS NOT NULL and
         * a 3-part kind='tuple' holder existed at endpoint=2, return empty. */
        if (by_key && lang != NULL && hctx.had_any_tuple)
        {
            MemoryContextSwitchTo(caller);
            MemoryContextDelete(work);
            return NULL;
        }
    }

    /* ---- Path 2: Direct KEY_BINDING claims ---- */
    {
        const hash128_t        *kb_set = NULL;
        size_t                  n_kb_set = 0;
        LaplaceRelationSurface *surfaces = NULL;
        int                     n_surfaces = 0;
        HTAB                   *surf_map;
        hash128_t              *surf_ids;
        hash128_t              *uniq_surfs;
        int                     n_uniq_surfs;
        ArrayType              *roots_b3 = laplace_pub_blake3_array(roots, n_roots);
        ArrayType              *empty_b3 = laplace_pub_blake3_array(NULL, 0);
        ArrayType              *surf_arr;
        Datum                   cbits_d[1] = { Int16GetDatum(claim_bit) };
        ArrayType              *cbits_arr = construct_array(cbits_d, 1, INT2OID, sizeof(int16), true, TYPALIGN_SHORT);
        Oid                     c_types[7] = {
            b3_arr_oid, INT8OID, INT2ARRAYOID, b3_arr_oid,
            INT2OID, b3_arr_oid, INT8OID
        };
        Datum                   c_args[7];
        bool                    c_nulls[7] = { false, true, false, false, false, false, false };
        CbDirectClaimCtx        dctx;

        if (laplace_relation_set_ids("KEY_BINDING", &kb_set, &n_kb_set) != 0 || n_kb_set == 0)
            elog(ERROR, "converse_binding_evidence: KEY_BINDING relation set unavailable");

        surfaces = laplace_relation_surfaces_expand_c(kb_set, (int) n_kb_set, &n_surfaces);
        if (surfaces != NULL && n_surfaces > 0)
        {
            surf_map = cb_hash_create_id(work, "cb kb surfs", sizeof(CbKbSurfEntry), n_surfaces);
            surf_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_surfaces);
            uniq_surfs = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_surfaces);
            for (int i = 0; i < n_surfaces; i++)
            {
                bool           f = false;
                CbKbSurfEntry *se = (CbKbSurfEntry *) hash_search(
                    surf_map, &surfaces[i].surface, HASH_ENTER, &f);
                if (!f)
                    se->inverse = surfaces[i].inverse;
                surf_ids[i] = surfaces[i].surface;
            }
            n_uniq_surfs = cb_dedup_ids(surf_ids, n_surfaces, uniq_surfs);
            surf_arr = laplace_pub_blake3_array(uniq_surfs, n_uniq_surfs);

            c_args[0] = PointerGetDatum(roots_b3);
            c_args[1] = (Datum) 0;
            c_args[2] = PointerGetDatum(cbits_arr);
            c_args[3] = PointerGetDatum(empty_b3);
            c_args[4] = Int16GetDatum(3);
            c_args[5] = PointerGetDatum(surf_arr);
            c_args[6] = Int64GetDatum(3);

            MemSet(&dctx, 0, sizeof(dctx));
            dctx.caller = work;
            dctx.surfaces = surf_map;
            dctx.roots = roots_set;
            dctx.by_key = by_key;

            (void) laplace_pub_call_srf("public", "laplace_claims_each", 7,
                                        c_types, c_args, c_nulls,
                                        cb_visit_direct_claim, &dctx);
            pfree(surf_arr);

            if (dctx.count > 0)
            {
                /* Exclude keys (if !by_key) or terms (if by_key) that are kind='tuple'. */
                hash128_t     *opp_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) dctx.count);
                hash128_t     *uniq_opp = (hash128_t *) palloc(sizeof(hash128_t) * (Size) dctx.count);
                int            n_uniq_opp;
                HTAB          *tuple_opp = cb_hash_create_id(work, "cb tuple opp",
                                                             sizeof(LexIdEntry), dctx.count);
                ArrayType     *opp_arr;
                CbTupleMaskCtx mctx = { work, tuple_opp };
                hash128_t     *term_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) dctx.count);
                int            n_term_ids = 0;

                for (int i = 0; i < dctx.count; i++)
                    opp_ids[i] = by_key ? dctx.items[i].term : dctx.items[i].key;
                n_uniq_opp = cb_dedup_ids(opp_ids, dctx.count, uniq_opp);
                opp_arr = laplace_pub_blake3_array(uniq_opp, n_uniq_opp);
                {
                    Datum ma[1] = { PointerGetDatum(opp_arr) };
                    bool  mn[1] = { false };
                    (void) laplace_pub_call_srf("public", "laplace_physicalities", 1,
                                                &b3_arr_oid, ma, mn,
                                                cb_visit_tuple_mask, &mctx);
                }
                pfree(opp_arr);

                for (int i = 0; i < dctx.count; i++)
                {
                    const hash128_t *opp = by_key ? &dctx.items[i].term : &dctx.items[i].key;
                    if (hash_search(tuple_opp, opp, HASH_FIND, NULL) == NULL)
                        term_ids[n_term_ids++] = dctx.items[i].term;
                }

                if (n_term_ids > 0)
                {
                    hash128_t        *uniq_terms = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_term_ids);
                    int               n_uniq_terms = cb_dedup_ids(term_ids, n_term_ids, uniq_terms);
                    ArrayType        *terms_bytea = hash128_array_from_ids(uniq_terms, n_uniq_terms);
                    hash128_t         has_lang_id;
                    ArrayType        *lang_type_arr;
                    LaplaceEdgeRawRow *lang_edges;
                    int               n_lang_edges = 0;
                    HTAB             *best_lang = cb_hash_create_id(work, "cb direct lang",
                                                                    sizeof(CbTermLangWinner), n_uniq_terms);
                    ConverseBindingRow *tmp = (ConverseBindingRow *) palloc(
                        sizeof(ConverseBindingRow) * (Size) n_term_ids);
                    int               n_kept = 0;

                    if (laplace_relation_type_id("HAS_LANGUAGE", &has_lang_id) != 0)
                        elog(ERROR, "converse_binding_evidence: HAS_LANGUAGE type unavailable");
                    lang_type_arr = hash128_array_from_ids(&has_lang_id, 1);

                    lang_edges = laplace_edges_raw_batch_c(
                        terms_bytea, "out", lang_type_arr, true, 8, true, "edge_rank", &n_lang_edges);
                    pfree(terms_bytea);
                    pfree(lang_type_arr);

                    for (int ei = 0; ei < n_lang_edges; ei++)
                    {
                        const LaplaceEdgeRawRow *er = &lang_edges[ei];
                        bool              is_req;
                        int64             eff;
                        bool              f = false;
                        CbTermLangWinner *lw;

                        if (er->neighbour_null)
                            continue;
                        is_req = (lang != NULL) && hash128_eq(&er->neighbour_id, lang);
                        eff = laplace_effective_mu_fp(er->rating, er->rd);
                        lw = (CbTermLangWinner *) hash_search(best_lang, &er->subject_id, HASH_ENTER, &f);
                        if (!f)
                        {
                            lw->lang = er->neighbour_id;
                            lw->is_req_lang = is_req;
                            lw->eff = eff;
                        }
                        else
                        {
                            bool better = false;
                            if (is_req != lw->is_req_lang)
                                better = is_req;
                            else if (eff > lw->eff)
                                better = true;
                            else if (eff == lw->eff &&
                                     memcmp(&er->neighbour_id, &lw->lang, sizeof(hash128_t)) < 0)
                                better = true;
                            if (better)
                            {
                                lw->lang = er->neighbour_id;
                                lw->is_req_lang = is_req;
                                lw->eff = eff;
                            }
                        }
                    }
                    if (lang_edges != NULL)
                        pfree(lang_edges);

                    for (int i = 0; i < dctx.count; i++)
                    {
                        const CbDirectClaimRow *dr = &dctx.items[i];
                        const hash128_t        *opp = by_key ? &dr->term : &dr->key;
                        CbTermLangWinner       *lw;
                        bool                    has_l;
                        bool                    matches_req;

                        if (hash_search(tuple_opp, opp, HASH_FIND, NULL) != NULL)
                            continue;
                        lw = (CbTermLangWinner *) hash_search(best_lang, &dr->term, HASH_FIND, NULL);
                        has_l = (lw != NULL);
                        matches_req = has_l && lang != NULL && hash128_eq(&lw->lang, lang);

                        if (by_key && lang != NULL && !matches_req)
                            continue;

                        tmp[n_kept].term = dr->term;
                        tmp[n_kept].key = dr->key;
                        tmp[n_kept].binding = dr->key;
                        tmp[n_kept].claim = dr->entity;
                        if (has_l)
                        {
                            tmp[n_kept].language = lw->lang;
                            tmp[n_kept].language_null = false;
                        }
                        else
                        {
                            MemSet(&tmp[n_kept].language, 0, sizeof(hash128_t));
                            tmp[n_kept].language_null = true;
                        }
                        tmp[n_kept].in_lang = by_key ? (lang != NULL ? matches_req : true)
                                                     : matches_req;
                        tmp[n_kept].eff_mu = dr->rating - 2.0 * dr->deviation;
                        if (by_key)
                        {
                            tmp[n_kept].witnesses = dr->matches > 1 ? dr->matches : 1;
                            tmp[n_kept].witnesses_null = false;
                        }
                        else if (support)
                        {
                            tmp[n_kept].witnesses = dr->matches > 1 ? dr->matches : 1;
                            tmp[n_kept].witnesses_null = false;
                        }
                        else
                        {
                            tmp[n_kept].witnesses = 0;
                            tmp[n_kept].witnesses_null = true;
                        }
                        tmp[n_kept].games = dr->matches;
                        tmp[n_kept].rating = (int64) llround(dr->rating * 1e9);
                        tmp[n_kept].rd = (int64) llround(dr->deviation * 1e9);
                        n_kept++;
                    }

                    if (n_kept > 0)
                    {
                        MemoryContextSwitchTo(caller);
                        result = (ConverseBindingRow *) MemoryContextAlloc(
                            caller, sizeof(ConverseBindingRow) * (Size) n_kept);
                        memcpy(result, tmp, sizeof(ConverseBindingRow) * (Size) n_kept);
                        MemoryContextDelete(work);
                        *out_count = n_kept;
                        return result;
                    }
                }
            }
        }
        pfree(roots_b3);
        pfree(empty_b3);
        pfree(cbits_arr);
    }

    /* ---- Path 3: Legacy laplace.consensus + laplace.attestations fallback ---- */
    if (!cb_legacy_consensus_has_rows())
    {
        MemoryContextSwitchTo(caller);
        MemoryContextDelete(work);
        return NULL;
    }

    {
        const hash128_t    *kb_set = NULL;
        size_t              n_kb_set = 0;
        ArrayType          *roots_bytea = hash128_array_from_ids(roots, n_roots);
        ArrayType          *kb_arr;
        LegacyAttestCollect col;

        if (laplace_relation_set_ids("KEY_BINDING", &kb_set, &n_kb_set) != 0 || n_kb_set == 0)
        {
            MemoryContextSwitchTo(caller);
            MemoryContextDelete(work);
            return NULL;
        }
        kb_arr = hash128_array_from_ids(kb_set, (int) n_kb_set);

        MemSet(&col, 0, sizeof(col));
        col.caller = work;
        col.has_lang_filter = (lang != NULL);
        if (lang != NULL)
            col.lang_filter = *lang;
        col.by_triple = cb_hash_create_key(work, "cb legacy triples",
                                           sizeof(LegacyTripleKey),
                                           sizeof(LegacyTripleEntry), 32);

        laplace_consensus_candidates_stored(by_key ? NULL : roots_bytea,
                                            by_key ? roots_bytea : NULL,
                                            kb_arr,
                                            cb_visit_legacy_consensus_cell, &col);
        pfree(roots_bytea);
        pfree(kb_arr);

        if (col.count > 0)
        {
            ConverseBindingRow *tmp = (ConverseBindingRow *) palloc(
                sizeof(ConverseBindingRow) * (Size) col.count);
            int n_kept = 0;

            cb_fill_legacy_attestations(&col);
            for (int i = 0; i < col.count; i++)
            {
                const LegacyAttestAccum *ac = &col.rows[i];
                int64 eff_fp;
                double eff_disp;

                if (by_key && lang != NULL && !ac->has_p_lang)
                    continue;

                eff_fp = laplace_effective_mu_fp(ac->rating, ac->rd);
                eff_disp = round((double) eff_fp / 1000000.0) / 1000.0;

                tmp[n_kept].term = ac->term;
                tmp[n_kept].key = ac->key;
                tmp[n_kept].binding = ac->key;
                tmp[n_kept].claim = ac->claim;
                if (ac->has_p_lang && lang != NULL)
                {
                    tmp[n_kept].language = *lang;
                    tmp[n_kept].language_null = false;
                }
                else if (lang == NULL && ac->has_any_ctx)
                {
                    tmp[n_kept].language = ac->max_ctx;
                    tmp[n_kept].language_null = false;
                }
                else
                {
                    MemSet(&tmp[n_kept].language, 0, sizeof(hash128_t));
                    tmp[n_kept].language_null = true;
                }
                tmp[n_kept].in_lang = by_key ? (lang == NULL || ac->has_p_lang) : ac->has_p_lang;
                tmp[n_kept].eff_mu = eff_disp;
                tmp[n_kept].witnesses = ac->witnesses;
                tmp[n_kept].witnesses_null = false;
                tmp[n_kept].games = ac->games;
                tmp[n_kept].rating = ac->rating;
                tmp[n_kept].rd = ac->rd;
                n_kept++;
            }

            if (n_kept > 0)
            {
                MemoryContextSwitchTo(caller);
                result = (ConverseBindingRow *) MemoryContextAlloc(
                    caller, sizeof(ConverseBindingRow) * (Size) n_kept);
                memcpy(result, tmp, sizeof(ConverseBindingRow) * (Size) n_kept);
                MemoryContextDelete(work);
                *out_count = n_kept;
                return result;
            }
        }
    }

    MemoryContextSwitchTo(caller);
    MemoryContextDelete(work);
    return NULL;
}

ConverseBindingRow *
converse_binding_evidence_c(const hash128_t *terms, int n_terms,
                            const hash128_t *lang, bool support, int *out_count)
{
    return converse_binding_evidence_core(terms, n_terms, lang, support, false, out_count);
}

ConverseBindingRow *
converse_key_binding_evidence_c(const hash128_t *keys, int n_keys,
                                const hash128_t *lang, bool support, int *out_count)
{
    return converse_binding_evidence_core(keys, n_keys, lang, support, true, out_count);
}

static void
cb_emit_binding_rows(ReturnSetInfo *rsinfo, const ConverseBindingRow *rows,
                     int n_rows, bool key_first)
{
    for (int i = 0; i < n_rows; i++)
    {
        const ConverseBindingRow *r = &rows[i];
        Datum values[11];
        bool  nulls[11];

        MemSet(nulls, 0, sizeof(nulls));
        values[0] = key_first ? hash128_to_datum(&r->key) : hash128_to_datum(&r->term);
        values[1] = key_first ? hash128_to_datum(&r->term) : hash128_to_datum(&r->key);
        values[2] = hash128_to_datum(&r->binding);
        values[3] = hash128_to_datum(&r->claim);
        if (r->language_null)
        {
            nulls[4] = true;
            values[4] = (Datum) 0;
        }
        else
        {
            values[4] = hash128_to_datum(&r->language);
        }
        values[5] = BoolGetDatum(r->in_lang);
        values[6] = DirectFunctionCall1(float8_numeric, Float8GetDatum(r->eff_mu));
        if (r->witnesses_null)
        {
            nulls[7] = true;
            values[7] = (Datum) 0;
        }
        else
        {
            values[7] = Int64GetDatum(r->witnesses);
        }
        values[8] = Int64GetDatum(r->games);

        if (key_first)
        {
            values[9] = Int64GetDatum(r->rating);
            values[10] = Int64GetDatum(r->rd);
        }

        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        for (int k = 0; k < 5; k++)
        {
            if (!nulls[k])
                pfree(DatumGetPointer(values[k]));
        }
        pfree(DatumGetPointer(values[6]));
    }
}

Datum
pg_laplace_binding_evidence_impl(PG_FUNCTION_ARGS)
{
    ReturnSetInfo      *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    hash128_t          *terms;
    int                 n_terms = 0;
    hash128_t           lang_val;
    const hash128_t    *lang_ptr = NULL;
    bool                support = true;
    ConverseBindingRow *rows;
    int                 n_rows = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;
    terms = cb_deconstruct_bytea_ids(PG_GETARG_ARRAYTYPE_P(0), &n_terms);
    if (n_terms == 0)
    {
        if (terms != NULL)
            pfree(terms);
        return (Datum) 0;
    }

    if (!PG_ARGISNULL(1))
    {
        lang_val = datum_to_hash128(PG_GETARG_DATUM(1));
        lang_ptr = &lang_val;
    }
    if (!PG_ARGISNULL(2))
        support = PG_GETARG_BOOL(2);

    rows = converse_binding_evidence_c(terms, n_terms, lang_ptr, support, &n_rows);
    pfree(terms);
    if (rows != NULL)
    {
        cb_emit_binding_rows(rsinfo, rows, n_rows, false);
        pfree(rows);
    }
    return (Datum) 0;
}

Datum
pg_laplace_key_binding_evidence_impl(PG_FUNCTION_ARGS)
{
    ReturnSetInfo      *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    hash128_t          *keys;
    int                 n_keys = 0;
    hash128_t           lang_val;
    const hash128_t    *lang_ptr = NULL;
    bool                support = false;
    ConverseBindingRow *rows;
    int                 n_rows = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;
    keys = cb_deconstruct_bytea_ids(PG_GETARG_ARRAYTYPE_P(0), &n_keys);
    if (n_keys == 0)
    {
        if (keys != NULL)
            pfree(keys);
        return (Datum) 0;
    }

    if (!PG_ARGISNULL(1))
    {
        lang_val = datum_to_hash128(PG_GETARG_DATUM(1));
        lang_ptr = &lang_val;
    }
    if (!PG_ARGISNULL(2))
        support = PG_GETARG_BOOL(2);

    rows = converse_key_binding_evidence_c(keys, n_keys, lang_ptr, support, &n_rows);
    pfree(keys);
    if (rows != NULL)
    {
        cb_emit_binding_rows(rsinfo, rows, n_rows, true);
        pfree(rows);
    }
    return (Datum) 0;
}

/* ========================================================================
 * 4. converse.word_language_candidates_batch
 * ======================================================================== */

typedef struct WlKey
{
    hash128_t word;
    hash128_t lang;
} WlKey;

typedef struct WlEntry
{
    WlKey  k;
    double mu;
} WlEntry;

typedef struct WlStoredCtx
{
    MemoryContext caller;
    HTAB         *map;
} WlStoredCtx;

static void
cb_visit_wl_stored(const LaplaceConsensusRow *row, void *opaque)
{
    WlStoredCtx  *ctx = (WlStoredCtx *) opaque;
    WlKey         key;
    WlEntry      *entry;
    double        mu;
    bool          found = false;
    MemoryContext prev;

    if (row->object_is_null)
        return;

    key.word = row->subject;
    key.lang = row->object;
    mu = (double) laplace_effective_mu_fp(row->rating, row->rd);

    prev = MemoryContextSwitchTo(ctx->caller);
    entry = (WlEntry *) hash_search(ctx->map, &key, HASH_ENTER, &found);
    if (!found || mu > entry->mu)
        entry->mu = mu;
    MemoryContextSwitchTo(prev);
}

static int
cb_wl_cmp(const void *a, const void *b)
{
    const ConverseWordLangRow *x = (const ConverseWordLangRow *) a;
    const ConverseWordLangRow *y = (const ConverseWordLangRow *) b;
    int c = memcmp(&x->word, &y->word, sizeof(hash128_t));

    if (c != 0)
        return c;
    return memcmp(&x->lang, &y->lang, sizeof(hash128_t));
}

ConverseWordLangRow *
converse_word_language_candidates_batch_c(const hash128_t *words, int n_words,
                                          int *out_count)
{
    MemoryContext        caller = CurrentMemoryContext;
    MemoryContext        work, old;
    ConverseBindingRow  *brows;
    int                  n_brows = 0;
    HTAB                *map;
    HASH_SEQ_STATUS      seq;
    WlEntry             *e;
    ConverseWordLangRow *result = NULL;
    int                  n_out = 0;

    *out_count = 0;
    if (words == NULL || n_words <= 0)
        return NULL;

    work = AllocSetContextCreate(caller, "converse_word_lang",
                                 ALLOCSET_DEFAULT_SIZES);
    old = MemoryContextSwitchTo(work);

    map = cb_hash_create_key(work, "wl map", sizeof(WlKey), sizeof(WlEntry),
                             Max(n_words * 2, 16));

    brows = converse_binding_evidence_c(words, n_words, NULL, false, &n_brows);
    for (int i = 0; i < n_brows; i++)
    {
        WlKey    k;
        WlEntry *we;
        double   mu;
        bool     found = false;

        if (brows[i].language_null)
            continue;
        k.word = brows[i].term;
        k.lang = brows[i].language;
        mu = brows[i].eff_mu * 1e9;
        we = (WlEntry *) hash_search(map, &k, HASH_ENTER, &found);
        if (!found || mu > we->mu)
            we->mu = mu;
    }

    if (hash_get_num_entries(map) == 0 && cb_legacy_consensus_has_rows())
    {
        hash128_t   *uniq = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_words);
        int          n_uniq = cb_dedup_ids(words, n_words, uniq);
        ArrayType   *words_arr = hash128_array_from_ids(uniq, n_uniq);
        hash128_t    has_lang_id;
        ArrayType   *type_arr;
        WlStoredCtx  sctx = { work, map };

        if (laplace_relation_type_id("HAS_LANGUAGE", &has_lang_id) == 0)
        {
            type_arr = hash128_array_from_ids(&has_lang_id, 1);
            laplace_consensus_candidates_stored(words_arr, NULL, type_arr,
                                                cb_visit_wl_stored, &sctx);
            pfree(type_arr);
        }
        pfree(words_arr);
    }

    n_out = hash_get_num_entries(map);
    if (n_out > 0)
    {
        int i = 0;
        MemoryContextSwitchTo(caller);
        result = (ConverseWordLangRow *) MemoryContextAlloc(
            caller, sizeof(ConverseWordLangRow) * (Size) n_out);
        hash_seq_init(&seq, map);
        while ((e = (WlEntry *) hash_seq_search(&seq)) != NULL)
        {
            result[i].word = e->k.word;
            result[i].lang = e->k.lang;
            result[i].mu = e->mu;
            i++;
        }
        if (n_out > 1)
            qsort(result, (Size) n_out, sizeof(ConverseWordLangRow), cb_wl_cmp);
    }

    MemoryContextSwitchTo(caller);
    MemoryContextDelete(work);
    *out_count = n_out;
    return result;
}

Datum
pg_laplace_word_language_candidates_batch(PG_FUNCTION_ARGS)
{
    ReturnSetInfo       *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    hash128_t           *words;
    int                  n_words = 0;
    ConverseWordLangRow *rows;
    int                  n_rows = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;
    words = cb_deconstruct_bytea_ids(PG_GETARG_ARRAYTYPE_P(0), &n_words);
    if (n_words == 0)
    {
        if (words != NULL)
            pfree(words);
        return (Datum) 0;
    }

    rows = converse_word_language_candidates_batch_c(words, n_words, &n_rows);
    pfree(words);
    for (int i = 0; i < n_rows; i++)
    {
        Datum values[3] = {
            hash128_to_datum(&rows[i].word),
            hash128_to_datum(&rows[i].lang),
            Float8GetDatum(rows[i].mu)
        };
        bool  nulls[3] = { false, false, false };

        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        pfree(DatumGetPointer(values[0]));
        pfree(DatumGetPointer(values[1]));
    }
    if (rows != NULL)
        pfree(rows);
    return (Datum) 0;
}

/* ========================================================================
 * 5. converse.key_facts, converse.terms_language, converse.translate,
 *    and converse.couple
 * ======================================================================== */

typedef struct KfPairKey
{
    hash128_t key;
    hash128_t type_id;
    hash128_t object_id;
} KfPairKey;

typedef struct KfFactEntry
{
    KfPairKey k;
    hash128_t relation_id;
    bool      relation_null;
    int64     rating;
    int64     rd;
    int64     volatility;
    int64     witness_count;
    int64     eff;
} KfFactEntry;

typedef struct KfExpandedLink
{
    hash128_t subj;
    int       first_root_idx;
} KfExpandedLink;

typedef struct KfRootLink
{
    hash128_t root_key;
    int       next;
} KfRootLink;

static int
cb_kf_cmp(const void *a, const void *b)
{
    const KfFactEntry *x = *(const KfFactEntry * const *) a;
    const KfFactEntry *y = *(const KfFactEntry * const *) b;
    int c;

    c = memcmp(&x->k.key, &y->k.key, sizeof(hash128_t));
    if (c != 0)
        return c;
    if (x->eff != y->eff)
        return x->eff > y->eff ? -1 : 1;
    c = memcmp(&x->k.type_id, &y->k.type_id, sizeof(hash128_t));
    if (c != 0)
        return c;
    return memcmp(&x->k.object_id, &y->k.object_id, sizeof(hash128_t));
}

typedef struct KfOutRow
{
    hash128_t key;
    hash128_t type_id;
    hash128_t object_id;
    int64     rating;
    int64     rd;
    int64     volatility;
    int64     witnesses;
    hash128_t relation_id;
    bool      relation_null;
} KfOutRow;

static const char *
cb_relation_canonical(const hash128_t *type_id)
{
    const laplace_relation_def_t *def = NULL;
    if (laplace_relation_lookup(type_id, &def) != 0 || def == NULL)
        return NULL;
    return def->canonical;
}

static KfOutRow *
converse_key_facts_c(const hash128_t *keys_in, int n_keys_in,
                     int32 per_key, int *out_count)
{
    MemoryContext     caller = CurrentMemoryContext;
    MemoryContext     work, old;
    hash128_t        *roots;
    int               n_roots;
    hash128_t         is_sense_of;
    const hash128_t  *kb_ids = NULL;
    size_t            n_kb = 0;
    const hash128_t  *ns_ids = NULL;
    size_t            n_ns = 0;
    HTAB             *excluded_names;
    ArrayType        *roots_arr, *sense_type_arr;
    LaplaceEdgeRawRow *sense_edges;
    int               n_sense_edges = 0;
    HTAB             *subj_map;
    KfRootLink       *rlinks;
    int               rlinks_cnt = 0;
    int               rlinks_cap;
    hash128_t        *exp_subjs;
    int               n_exp_subjs = 0;
    ArrayType        *exp_arr;
    LaplaceEdgeRawRow *batch_edges;
    int               n_batch = 0;
    HTAB             *dedup;
    HASH_SEQ_STATUS   seq;
    KfFactEntry      *fe;
    int               total;
    KfOutRow         *result = NULL;

    *out_count = 0;
    if (keys_in == NULL || n_keys_in <= 0 || per_key <= 0)
        return NULL;

    if (laplace_relation_type_id("IS_SENSE_OF", &is_sense_of) != 0)
        elog(ERROR, "key_facts: IS_SENSE_OF type unavailable");
    (void) laplace_relation_set_ids("KEY_BINDING", &kb_ids, &n_kb);
    (void) laplace_relation_set_ids("NON_SALIENT_STRUCTURAL", &ns_ids, &n_ns);

    work = AllocSetContextCreate(caller, "converse_key_facts",
                                 ALLOCSET_DEFAULT_SIZES);
    old = MemoryContextSwitchTo(work);

    /* Excluded canonical relation names matching consensus.relation_canonical. */
    {
        HASHCTL ctl;
        MemSet(&ctl, 0, sizeof(ctl));
        ctl.keysize = 64;
        ctl.entrysize = 64;
        ctl.hcxt = work;
        excluded_names = hash_create("kf excl", 32, &ctl,
                                     HASH_ELEM | HASH_STRINGS | HASH_CONTEXT);
        for (size_t i = 0; i < n_kb; i++)
        {
            const char *cname = cb_relation_canonical(&kb_ids[i]);
            if (cname != NULL)
                (void) hash_search(excluded_names, cname, HASH_ENTER, NULL);
        }
        for (size_t i = 0; i < n_ns; i++)
        {
            const char *cname = cb_relation_canonical(&ns_ids[i]);
            if (cname != NULL)
                (void) hash_search(excluded_names, cname, HASH_ENTER, NULL);
        }
    }

    roots = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_keys_in);
    n_roots = cb_dedup_ids(keys_in, n_keys_in, roots);

    roots_arr = hash128_array_from_ids(roots, n_roots);
    sense_type_arr = hash128_array_from_ids(&is_sense_of, 1);
    sense_edges = laplace_edges_raw_batch_c(
        roots_arr, "out", sense_type_arr, true, 8, false, "eff_mu", &n_sense_edges);
    pfree(roots_arr);
    pfree(sense_type_arr);

    rlinks_cap = n_roots + n_sense_edges + 8;
    rlinks = (KfRootLink *) palloc(sizeof(KfRootLink) * (Size) rlinks_cap);
    subj_map = cb_hash_create_id(work, "kf subj", sizeof(KfExpandedLink),
                                 Max(n_roots + n_sense_edges, 16));
    exp_subjs = (hash128_t *) palloc(sizeof(hash128_t) * (Size) (n_roots + n_sense_edges + 1));

    for (int i = 0; i < n_roots; i++)
    {
        bool            f = false;
        KfExpandedLink *el = (KfExpandedLink *) hash_search(subj_map, &roots[i], HASH_ENTER, &f);
        rlinks[rlinks_cnt].root_key = roots[i];
        rlinks[rlinks_cnt].next = f ? el->first_root_idx : -1;
        el->first_root_idx = rlinks_cnt++;
        if (!f)
            exp_subjs[n_exp_subjs++] = roots[i];
    }
    for (int i = 0; i < n_sense_edges; i++)
    {
        const LaplaceEdgeRawRow *se = &sense_edges[i];
        bool            f = false;
        KfExpandedLink *el;
        bool            dup_root = false;

        if (se->neighbour_null)
            continue;
        el = (KfExpandedLink *) hash_search(subj_map, &se->neighbour_id, HASH_ENTER, &f);
        if (!f)
        {
            el->first_root_idx = -1;
            exp_subjs[n_exp_subjs++] = se->neighbour_id;
        }
        else
        {
            for (int p = el->first_root_idx; p >= 0; p = rlinks[p].next)
            {
                if (hash128_eq(&rlinks[p].root_key, &se->subject_id))
                {
                    dup_root = true;
                    break;
                }
            }
        }
        if (!dup_root)
        {
            rlinks[rlinks_cnt].root_key = se->subject_id;
            rlinks[rlinks_cnt].next = el->first_root_idx;
            el->first_root_idx = rlinks_cnt++;
        }
    }
    if (sense_edges != NULL)
        pfree(sense_edges);

    exp_arr = hash128_array_from_ids(exp_subjs, n_exp_subjs);
    {
        int32 lim = per_key * 2 > 48 ? per_key * 2 : 48;
        batch_edges = laplace_edges_raw_batch_c(exp_arr, "out", NULL, true, lim,
                                                true, "eff_mu", &n_batch);
    }
    pfree(exp_arr);

    dedup = cb_hash_create_key(work, "kf dedup", sizeof(KfPairKey), sizeof(KfFactEntry),
                               Max(n_batch, 16));
    for (int i = 0; i < n_batch; i++)
    {
        const LaplaceEdgeRawRow *er = &batch_edges[i];
        const char              *cname;
        KfExpandedLink          *el;
        int64                    eff;

        if (er->neighbour_null || er->type_null)
            continue;
        cname = cb_relation_canonical(&er->type_id);
        if (cname == NULL)
            continue;
        if (hash_search(excluded_names, cname, HASH_FIND, NULL) != NULL)
            continue;
        el = (KfExpandedLink *) hash_search(subj_map, &er->subject_id, HASH_FIND, NULL);
        if (el == NULL)
            continue;
        eff = laplace_effective_mu_fp(er->rating, er->rd);

        for (int p = el->first_root_idx; p >= 0; p = rlinks[p].next)
        {
            KfPairKey    pk;
            KfFactEntry *fe_slot;
            bool         f = false;

            pk.key = rlinks[p].root_key;
            pk.type_id = er->type_id;
            pk.object_id = er->neighbour_id;
            fe_slot = (KfFactEntry *) hash_search(dedup, &pk, HASH_ENTER, &f);
            if (!f || eff > fe_slot->eff)
            {
                fe_slot->relation_id = er->relation_id;
                fe_slot->relation_null = er->relation_null;
                fe_slot->rating = er->rating;
                fe_slot->rd = er->rd;
                fe_slot->volatility = 60000000;
                fe_slot->witness_count = er->witness_count;
                fe_slot->eff = eff;
            }
        }
    }
    if (batch_edges != NULL)
        pfree(batch_edges);

    total = hash_get_num_entries(dedup);
    if (total > 0)
    {
        KfFactEntry **sorted = (KfFactEntry **) palloc(sizeof(KfFactEntry *) * (Size) total);
        int           cnt = 0;
        KfOutRow     *tmp = (KfOutRow *) palloc(sizeof(KfOutRow) * (Size) total);
        int           out_i = 0;
        int32         cur_rn = 0;

        hash_seq_init(&seq, dedup);
        while ((fe = (KfFactEntry *) hash_seq_search(&seq)) != NULL)
            sorted[cnt++] = fe;
        if (cnt > 1)
            qsort(sorted, (Size) cnt, sizeof(KfFactEntry *), cb_kf_cmp);

        for (int i = 0; i < cnt; i++)
        {
            if (i == 0 || !hash128_eq(&sorted[i]->k.key, &sorted[i - 1]->k.key))
                cur_rn = 1;
            else
                cur_rn++;
            if (cur_rn <= per_key)
            {
                tmp[out_i].key = sorted[i]->k.key;
                tmp[out_i].type_id = sorted[i]->k.type_id;
                tmp[out_i].object_id = sorted[i]->k.object_id;
                tmp[out_i].rating = sorted[i]->rating;
                tmp[out_i].rd = sorted[i]->rd;
                tmp[out_i].volatility = sorted[i]->volatility;
                tmp[out_i].witnesses = sorted[i]->witness_count;
                tmp[out_i].relation_id = sorted[i]->relation_id;
                tmp[out_i].relation_null = sorted[i]->relation_null;
                out_i++;
            }
        }

        if (out_i > 0)
        {
            MemoryContextSwitchTo(caller);
            result = (KfOutRow *) MemoryContextAlloc(caller, sizeof(KfOutRow) * (Size) out_i);
            memcpy(result, tmp, sizeof(KfOutRow) * (Size) out_i);
            *out_count = out_i;
        }
    }

    MemoryContextSwitchTo(caller);
    MemoryContextDelete(work);
    return result;
}

Datum
pg_laplace_key_facts(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    hash128_t     *keys;
    int            n_keys = 0;
    int32          per_key = PG_ARGISNULL(1) ? 24 : PG_GETARG_INT32(1);
    KfOutRow      *rows;
    int            n_rows = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || per_key <= 0)
        return (Datum) 0;
    keys = cb_deconstruct_bytea_ids(PG_GETARG_ARRAYTYPE_P(0), &n_keys);
    if (n_keys == 0)
    {
        if (keys != NULL)
            pfree(keys);
        return (Datum) 0;
    }

    rows = converse_key_facts_c(keys, n_keys, per_key, &n_rows);
    pfree(keys);
    for (int i = 0; i < n_rows; i++)
    {
        const KfOutRow *r = &rows[i];
        Datum values[9] = {
            hash128_to_datum(&r->key),
            hash128_to_datum(&r->type_id),
            hash128_to_datum(&r->object_id),
            eff_mu_display_numeric(r->rating, r->rd),
            Int64GetDatum(r->witnesses),
            Int64GetDatum(r->rating),
            Int64GetDatum(r->rd),
            Int64GetDatum(r->volatility),
            r->relation_null ? (Datum) 0 : hash128_to_datum(&r->relation_id)
        };
        bool nulls[9] = {
            false, false, false, false, false, false, false, false, r->relation_null
        };
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        for (int k = 0; k < 4; k++)
            pfree(DatumGetPointer(values[k]));
        if (!nulls[8])
            pfree(DatumGetPointer(values[8]));
    }
    if (rows != NULL)
        pfree(rows);
    return (Datum) 0;
}

typedef struct TlLangEntry
{
    hash128_t lang;
    int64     total_games;
} TlLangEntry;

Datum
pg_laplace_terms_language(PG_FUNCTION_ARGS)
{
    MemoryContext       caller = CurrentMemoryContext;
    hash128_t          *terms;
    int                 n_terms = 0;
    ConverseBindingRow *rows;
    int                 n_rows = 0;
    HTAB               *lang_sums;
    HASH_SEQ_STATUS     seq;
    TlLangEntry        *le;
    bool                has_winner = false;
    hash128_t           best_lang = {0, 0};
    int64               best_sum = 0;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    terms = cb_deconstruct_bytea_ids(PG_GETARG_ARRAYTYPE_P(0), &n_terms);
    if (n_terms == 0)
    {
        if (terms != NULL)
            pfree(terms);
        PG_RETURN_NULL();
    }

    rows = converse_binding_evidence_c(terms, n_terms, NULL, false, &n_rows);
    pfree(terms);
    if (rows == NULL || n_rows == 0)
        PG_RETURN_NULL();

    lang_sums = cb_hash_create_id(caller, "tl sums", sizeof(TlLangEntry), n_rows);
    for (int i = 0; i < n_rows; i++)
    {
        int64        g;
        bool         found = false;
        TlLangEntry *slot;

        if (rows[i].language_null)
            continue;
        g = rows[i].games > 1 ? rows[i].games : 1;
        slot = (TlLangEntry *) hash_search(lang_sums, &rows[i].language, HASH_ENTER, &found);
        slot->total_games = found ? (slot->total_games + g) : g;
    }
    pfree(rows);

    hash_seq_init(&seq, lang_sums);
    while ((le = (TlLangEntry *) hash_seq_search(&seq)) != NULL)
    {
        if (!has_winner || le->total_games > best_sum ||
            (le->total_games == best_sum &&
             memcmp(&le->lang, &best_lang, sizeof(hash128_t)) < 0))
        {
            has_winner = true;
            best_sum = le->total_games;
            best_lang = le->lang;
        }
    }
    hash_destroy(lang_sums);

    if (!has_winner)
        PG_RETURN_NULL();
    PG_RETURN_DATUM(hash128_to_datum(&best_lang));
}

typedef struct TrUpEntry
{
    hash128_t key;
    int64     up_rating;
    int64     up_rd;
    int64     up_witnesses;
} TrUpEntry;

typedef struct TrPairKey
{
    hash128_t key;
    hash128_t target;
} TrPairKey;

typedef struct TrDownEntry
{
    TrPairKey pk;
    int64     up_rating;
    int64     up_rd;
    int64     up_witnesses;
    int64     down_rating;
    int64     down_rd;
    int64     down_witnesses;
    int64     down_eff;
} TrDownEntry;

Datum
pg_laplace_converse_translate(PG_FUNCTION_ARGS)
{
    ReturnSetInfo      *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    MemoryContext       caller = CurrentMemoryContext;
    MemoryContext       work, old;
    hash128_t           term_id, to_id;
    hash128_t           from_id;
    const hash128_t    *from_ptr = NULL;
    ConverseBindingRow *up_rows, *dn_rows;
    int                 n_up = 0, n_dn = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        return (Datum) 0;
    term_id = datum_to_hash128(PG_GETARG_DATUM(0));
    to_id = datum_to_hash128(PG_GETARG_DATUM(1));
    if (!PG_ARGISNULL(2))
    {
        from_id = datum_to_hash128(PG_GETARG_DATUM(2));
        from_ptr = &from_id;
    }

    work = AllocSetContextCreate(caller, "converse_translate", ALLOCSET_DEFAULT_SIZES);
    old = MemoryContextSwitchTo(work);

    up_rows = converse_binding_evidence_c(&term_id, 1, from_ptr, false, &n_up);
    if (n_up > 0)
    {
        HTAB        *up_by_key = cb_hash_create_id(work, "tr up", sizeof(TrUpEntry), n_up);
        int          *up_next = (int *) palloc(sizeof(int) * (Size) n_up);
        TrUpEntry   *up_list = (TrUpEntry *) palloc(sizeof(TrUpEntry) * (Size) n_up);
        int          up_list_cnt = 0;
        hash128_t   *up_keys = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_up);
        int          n_up_keys = 0;
        hash128_t   *uniq_up_keys = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_up);
        int          n_uniq_keys;

        typedef struct TrUpHead { hash128_t key; int first; } TrUpHead;
        HTAB *up_heads = cb_hash_create_id(work, "tr up heads", sizeof(TrUpHead), n_up);
        (void) up_by_key;

        for (int i = 0; i < n_up; i++)
        {
            bool      f = false;
            TrUpHead *h;
            int64     wit;

            if (from_ptr != NULL && !up_rows[i].in_lang)
                continue;
            wit = !up_rows[i].witnesses_null ? up_rows[i].witnesses
                  : (up_rows[i].games > 0 ? up_rows[i].games : 1);
            up_list[up_list_cnt].key = up_rows[i].key;
            up_list[up_list_cnt].up_rating = (int64) llround(up_rows[i].eff_mu * 1e9);
            up_list[up_list_cnt].up_rd = 0;
            up_list[up_list_cnt].up_witnesses = wit;

            h = (TrUpHead *) hash_search(up_heads, &up_rows[i].key, HASH_ENTER, &f);
            up_next[up_list_cnt] = f ? h->first : -1;
            h->first = up_list_cnt++;
            up_keys[n_up_keys++] = up_rows[i].key;
        }

        if (n_up_keys > 0)
        {
            n_uniq_keys = cb_dedup_ids(up_keys, n_up_keys, uniq_up_keys);
            dn_rows = converse_key_binding_evidence_c(uniq_up_keys, n_uniq_keys, &to_id, false, &n_dn);
            if (n_dn > 0)
            {
                HTAB *winners = cb_hash_create_key(work, "tr down", sizeof(TrPairKey),
                                                   sizeof(TrDownEntry), n_dn);
                for (int j = 0; j < n_dn; j++)
                {
                    TrUpHead *h;
                    int64     deff;

                    if (hash128_eq(&dn_rows[j].term, &term_id))
                        continue;
                    h = (TrUpHead *) hash_search(up_heads, &dn_rows[j].key, HASH_FIND, NULL);
                    if (h == NULL)
                        continue;
                    deff = laplace_effective_mu_fp(dn_rows[j].rating, dn_rows[j].rd);

                    for (int ui = h->first; ui >= 0; ui = up_next[ui])
                    {
                        TrPairKey    pk = { dn_rows[j].key, dn_rows[j].term };
                        bool         f = false;
                        TrDownEntry *de = (TrDownEntry *) hash_search(winners, &pk, HASH_ENTER, &f);
                        if (!f || deff > de->down_eff)
                        {
                            de->up_rating = up_list[ui].up_rating;
                            de->up_rd = up_list[ui].up_rd;
                            de->up_witnesses = up_list[ui].up_witnesses;
                            de->down_rating = dn_rows[j].rating;
                            de->down_rd = dn_rows[j].rd;
                            de->down_witnesses = dn_rows[j].witnesses;
                            de->down_eff = deff;
                        }
                    }
                }

                {
                    HASH_SEQ_STATUS seq;
                    TrDownEntry    *de;
                    hash_seq_init(&seq, winners);
                    while ((de = (TrDownEntry *) hash_seq_search(&seq)) != NULL)
                    {
                        Datum values[8] = {
                            hash128_to_datum(&de->pk.key),
                            hash128_to_datum(&de->pk.target),
                            Int64GetDatum(de->up_rating),
                            Int64GetDatum(de->up_rd),
                            Int64GetDatum(de->up_witnesses),
                            Int64GetDatum(de->down_rating),
                            Int64GetDatum(de->down_rd),
                            Int64GetDatum(de->down_witnesses)
                        };
                        bool nulls[8] = { false, false, false, false, false, false, false, false };
                        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
                        pfree(DatumGetPointer(values[0]));
                        pfree(DatumGetPointer(values[1]));
                    }
                }
            }
        }
    }

    MemoryContextSwitchTo(old);
    MemoryContextDelete(work);
    return (Datum) 0;
}

bool
converse_legacy_consensus_has_rows(void)
{
    return cb_legacy_consensus_has_rows();
}

typedef struct CbIsoLangCacheEntry
{
    hash128_t id;
    bool      is_lang;
} CbIsoLangCacheEntry;

typedef struct CbIsoLangVisitCtx
{
    HTAB *cache;
} CbIsoLangVisitCtx;

static HTAB *cb_iso_lang_cache = NULL;

static bool
cb_is_pure_iso639_surface(const char *s)
{
    const char *canon = NULL;
    size_t      len;
    bool        has_dash = false;

    if (s == NULL)
        return false;
    len = strlen(s);
    if (len < 2 || len > 16)
        return false;
    for (size_t i = 0; i < len; i++)
    {
        unsigned char c = (unsigned char) s[i];
        if (c == ' ' || c == '_')
            return false;
        if (c == '-')
            has_dash = true;
    }
    if (!has_dash && len != 2 && len != 3)
        return false;
    if (has_dash && len < 4)
        return false;
    return (laplace_language_canonical(s, &canon) == 0 && canon != NULL);
}

static bool
cb_visit_iso_lang_label(TupleTableSlot *slot, void *opaque)
{
    CbIsoLangVisitCtx   *ctx = (CbIsoLangVisitCtx *) opaque;
    bool                 isnull = false;
    Datum                d_id, d_lbl;
    hash128_t            id;
    char                *txt;
    bool                 is_lang;
    bool                 found = false;
    CbIsoLangCacheEntry *ce;

    d_id = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;
    id = laplace_pub_blake3_to_hash128(d_id);

    d_lbl = slot_getattr(slot, 3, &isnull);
    if (isnull)
    {
        is_lang = false;
    }
    else
    {
        txt = TextDatumGetCString(d_lbl);
        is_lang = cb_is_pure_iso639_surface(txt);
        pfree(txt);
    }

    ce = (CbIsoLangCacheEntry *) hash_search(ctx->cache, &id, HASH_ENTER, &found);
    ce->is_lang = is_lang;
    return true;
}

void
laplace_are_iso639_lang_ids(const hash128_t *ids, int n_ids, bool *out_is_lang)
{
    hash128_t *missing;
    int        n_missing = 0;

    if (ids == NULL || n_ids <= 0 || out_is_lang == NULL)
        return;

    if (cb_iso_lang_cache == NULL)
        cb_iso_lang_cache = cb_hash_create_id(TopMemoryContext, "cb iso lang cache",
                                              sizeof(CbIsoLangCacheEntry), 256);

    missing = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_ids);
    for (int i = 0; i < n_ids; i++)
    {
        if (hash_search(cb_iso_lang_cache, &ids[i], HASH_FIND, NULL) == NULL)
        {
            bool dup = false;
            for (int j = 0; j < n_missing; j++)
            {
                if (hash128_eq(&missing[j], &ids[i]))
                {
                    dup = true;
                    break;
                }
            }
            if (!dup)
                missing[n_missing++] = ids[i];
        }
    }

    if (n_missing > 0)
    {
        Oid               b3_arr_oid = laplace_pub_blake3_array_oid();
        ArrayType        *arr = laplace_pub_blake3_array(missing, n_missing);
        Datum             args[1] = { PointerGetDatum(arr) };
        bool              argnulls[1] = { false };
        CbIsoLangVisitCtx ctx = { cb_iso_lang_cache };

        (void) laplace_pub_call_srf("public", "laplace_labels", 1,
                                    &b3_arr_oid, args, argnulls,
                                    cb_visit_iso_lang_label, &ctx);
        pfree(arr);

        for (int i = 0; i < n_missing; i++)
        {
            bool                 found = false;
            CbIsoLangCacheEntry *ce = (CbIsoLangCacheEntry *) hash_search(
                cb_iso_lang_cache, &missing[i], HASH_ENTER, &found);
            if (!found)
                ce->is_lang = false;
        }
    }
    pfree(missing);

    for (int i = 0; i < n_ids; i++)
    {
        CbIsoLangCacheEntry *ce = (CbIsoLangCacheEntry *) hash_search(
            cb_iso_lang_cache, &ids[i], HASH_FIND, NULL);
        out_is_lang[i] = (ce != NULL && ce->is_lang);
    }
}

/* ---- converse.couple ---- */

typedef struct CplBindingCand
{
    hash128_t key;
    bool      in_lang;
    int64     games;
    double    eff_mu;
} CplBindingCand;

static int
cb_cpl_bind_cmp(const void *a, const void *b)
{
    const CplBindingCand *x = (const CplBindingCand *) a;
    const CplBindingCand *y = (const CplBindingCand *) b;
    if (x->in_lang != y->in_lang)
        return x->in_lang ? -1 : 1;
    if (x->games != y->games)
        return x->games > y->games ? -1 : 1;
    if (x->eff_mu != y->eff_mu)
        return x->eff_mu > y->eff_mu ? -1 : 1;
    return memcmp(&x->key, &y->key, sizeof(hash128_t));
}

static int
cb_collect_top_keys(const hash128_t *term, const hash128_t *lang,
                    hash128_t *out_keys)
{
    ConverseBindingRow *rows;
    int                 n_rows = 0;
    CplBindingCand     *cands;
    int                 out = 0;

    rows = converse_binding_evidence_c(term, 1, lang, true, &n_rows);
    if (n_rows > 0)
    {
        cands = (CplBindingCand *) palloc(sizeof(CplBindingCand) * (Size) n_rows);
        for (int i = 0; i < n_rows; i++)
        {
            cands[i].key = rows[i].key;
            cands[i].in_lang = rows[i].in_lang;
            cands[i].games = rows[i].games;
            cands[i].eff_mu = rows[i].eff_mu;
        }
        qsort(cands, (Size) n_rows, sizeof(CplBindingCand), cb_cpl_bind_cmp);
        for (int i = 0; i < n_rows && i < 16; i++)
            out_keys[out++] = cands[i].key;
        pfree(cands);
    }
    if (rows != NULL)
        pfree(rows);
    out_keys[out++] = *term;
    return cb_dedup_ids(out_keys, out, out_keys);
}

typedef struct CplAboveCtx
{
    MemoryContext caller;
    HTAB         *set;
} CplAboveCtx;

static bool
cb_visit_above(TupleTableSlot *slot, void *opaque)
{
    CplAboveCtx *ctx = (CplAboveCtx *) opaque;
    bool         isnull = false;
    Datum        d = slot_getattr(slot, 1, &isnull);
    hash128_t    ent;
    MemoryContext prev;

    if (isnull)
        return true;
    ent = laplace_pub_blake3_to_hash128(d);
    prev = MemoryContextSwitchTo(ctx->caller);
    (void) hash_search(ctx->set, &ent, HASH_ENTER, &isnull);
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct CplUpKey
{
    hash128_t root;
    hash128_t node;
    char      side;
} CplUpKey;

typedef struct CplUpEntry
{
    CplUpKey k;
    int32    depth;
} CplUpEntry;

typedef struct CplUpCtx
{
    MemoryContext caller;
    HTAB         *entries;
} CplUpCtx;

static bool
cb_visit_upward_walk(TupleTableSlot *slot, void *opaque)
{
    CplUpCtx     *ctx = (CplUpCtx *) opaque;
    bool          isnull = false;
    Datum         d_root, d_side, d_node, d_depth;
    char         *side_str;
    CplUpKey      uk;
    CplUpEntry   *ue;
    int32         depth;
    bool          found = false;
    MemoryContext prev;

    d_root = slot_getattr(slot, 1, &isnull);
    if (isnull)
        return true;
    d_side = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;
    d_node = slot_getattr(slot, 3, &isnull);
    if (isnull)
        return true;
    d_depth = slot_getattr(slot, 4, &isnull);
    depth = isnull ? 0 : DatumGetInt32(d_depth);

    side_str = TextDatumGetCString(d_side);
    MemSet(&uk, 0, sizeof(uk));
    uk.root = datum_to_hash128(d_root);
    uk.node = datum_to_hash128(d_node);
    uk.side = side_str[0];
    pfree(side_str);

    prev = MemoryContextSwitchTo(ctx->caller);
    ue = (CplUpEntry *) hash_search(ctx->entries, &uk, HASH_ENTER, &found);
    if (!found || depth < ue->depth)
        ue->depth = depth;
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct CplOutRow
{
    hash128_t x_key;
    hash128_t y_key;
    char      channel_rank; /* 0 = ancestor, 1 = fact, 2 = usage */
    const char *channel;
    hash128_t relation;
    bool      relation_null;
    hash128_t via;
    int64     sharing;
    int32     depth;
} CplOutRow;

static int
cb_cpl_out_cmp(const void *a, const void *b)
{
    const CplOutRow *x = (const CplOutRow *) a;
    const CplOutRow *y = (const CplOutRow *) b;
    int c;

    if (x->sharing != y->sharing)
        return x->sharing < y->sharing ? -1 : 1;
    if (x->depth != y->depth)
        return x->depth < y->depth ? -1 : 1;
    c = strcmp(x->channel, y->channel);
    if (c != 0)
        return c;
    return memcmp(&x->via, &y->via, sizeof(hash128_t));
}

Datum
pg_laplace_converse_couple(PG_FUNCTION_ARGS)
{
    ReturnSetInfo    *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    MemoryContext     caller = CurrentMemoryContext;
    MemoryContext     work, old;
    hash128_t         x_id, y_id, lang_id;
    const hash128_t  *lang_ptr = NULL;
    int32             p_k = PG_ARGISNULL(3) ? 12 : PG_GETARG_INT32(3);
    hash128_t         xk[17], yk[17];
    int               n_xk, n_yk;
    CplOutRow        *out = NULL;
    int               n_out = 0, out_cap = 32;
    Oid               b3_arr_oid = laplace_pub_blake3_array_oid();
    hash128_t         is_sense_of;
    const hash128_t  *up_ids = NULL;
    size_t            n_up_ids = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1) || p_k <= 0)
        return (Datum) 0;
    x_id = datum_to_hash128(PG_GETARG_DATUM(0));
    y_id = datum_to_hash128(PG_GETARG_DATUM(1));
    if (!PG_ARGISNULL(2))
    {
        lang_id = datum_to_hash128(PG_GETARG_DATUM(2));
        lang_ptr = &lang_id;
    }

    if (laplace_relation_type_id("IS_SENSE_OF", &is_sense_of) != 0)
        elog(ERROR, "converse.couple: IS_SENSE_OF unavailable");
    if (laplace_relation_set_ids("PATH_UPWARD", &up_ids, &n_up_ids) != 0 || n_up_ids == 0)
        elog(ERROR, "converse.couple: PATH_UPWARD unavailable");

    work = AllocSetContextCreate(caller, "converse_couple", ALLOCSET_DEFAULT_SIZES);
    old = MemoryContextSwitchTo(work);
    out = (CplOutRow *) palloc(sizeof(CplOutRow) * (Size) out_cap);

    n_xk = cb_collect_top_keys(&x_id, lang_ptr, xk);
    n_yk = cb_collect_top_keys(&y_id, lang_ptr, yk);

    /* 1. usage: public.laplace_above([p_x], 1, 4096) JOIN public.laplace_above([p_y], 1, 4096) */
    {
        HTAB       *ax = cb_hash_create_id(work, "cpl ax", sizeof(LexIdEntry), 64);
        HTAB       *ay = cb_hash_create_id(work, "cpl ay", sizeof(LexIdEntry), 64);
        ArrayType  *ax_arr = laplace_pub_blake3_array(&x_id, 1);
        ArrayType  *ay_arr = laplace_pub_blake3_array(&y_id, 1);
        Oid         ab_types[3] = { b3_arr_oid, INT4OID, INT8OID };
        bool        ab_nulls[3] = { false, false, false };
        Datum       x_args[3] = { PointerGetDatum(ax_arr), Int32GetDatum(1), Int64GetDatum(4096) };
        Datum       y_args[3] = { PointerGetDatum(ay_arr), Int32GetDatum(1), Int64GetDatum(4096) };
        CplAboveCtx cx = { work, ax }, cy = { work, ay };
        HASH_SEQ_STATUS seq;
        LexIdEntry *e;

        (void) laplace_pub_call_srf("public", "laplace_above", 3, ab_types, x_args, ab_nulls, cb_visit_above, &cx);
        (void) laplace_pub_call_srf("public", "laplace_above", 3, ab_types, y_args, ab_nulls, cb_visit_above, &cy);
        pfree(ax_arr);
        pfree(ay_arr);

        hash_seq_init(&seq, ax);
        while ((e = (LexIdEntry *) hash_seq_search(&seq)) != NULL)
        {
            if (hash_search(ay, &e->id, HASH_FIND, NULL) != NULL)
            {
                if (n_out >= out_cap)
                {
                    out_cap *= 2;
                    out = (CplOutRow *) repalloc(out, sizeof(CplOutRow) * (Size) out_cap);
                }
                out[n_out].x_key = x_id;
                out[n_out].y_key = y_id;
                out[n_out].channel = "usage";
                out[n_out].relation_null = true;
                out[n_out].via = e->id;
                out[n_out].sharing = 1;
                out[n_out].depth = 0;
                n_out++;
            }
        }
    }

    /* 2. shared_fact: key_facts(xk, 64) JOIN key_facts(yk, 64) */
    {
        int       n_xf = 0, n_yf = 0;
        KfOutRow *xf = converse_key_facts_c(xk, n_xk, 64, &n_xf);
        KfOutRow *yf = converse_key_facts_c(yk, n_yk, 64, &n_yf);

        if (n_xf > 0 && n_yf > 0)
        {
            typedef struct { hash128_t t; hash128_t o; } CplFactShareKey;
            typedef struct { CplFactShareKey k; int64 cnt; } CplFactShareEnt;
            HTAB *share_cache = cb_hash_create_key(work, "cpl fshare",
                                                   sizeof(CplFactShareKey),
                                                   sizeof(CplFactShareEnt), 32);

            for (int i = 0; i < n_xf; i++)
            {
                for (int j = 0; j < n_yf; j++)
                {
                    CplFactShareKey  sk;
                    CplFactShareEnt *se;
                    bool             f = false;

                    if (!hash128_eq(&xf[i].type_id, &yf[j].type_id) ||
                        !hash128_eq(&xf[i].object_id, &yf[j].object_id))
                        continue;

                    sk.t = xf[i].type_id;
                    sk.o = xf[i].object_id;
                    se = (CplFactShareEnt *) hash_search(share_cache, &sk, HASH_ENTER, &f);
                    if (!f)
                    {
                        ArrayType        *oa = hash128_array_from_ids(&sk.o, 1);
                        ArrayType        *ta = hash128_array_from_ids(&sk.t, 1);
                        int               n_in = 0;
                        LaplaceEdgeRawRow *ir = laplace_edges_raw_batch_c(
                            oa, "in", ta, true, 256, true, "eff_mu", &n_in);
                        se->cnt = (int64) n_in;
                        if (ir != NULL)
                            pfree(ir);
                        pfree(oa);
                        pfree(ta);
                    }

                    if (n_out >= out_cap)
                    {
                        out_cap *= 2;
                        out = (CplOutRow *) repalloc(out, sizeof(CplOutRow) * (Size) out_cap);
                    }
                    out[n_out].x_key = xf[i].key;
                    out[n_out].y_key = yf[j].key;
                    out[n_out].channel = "fact";
                    out[n_out].relation = xf[i].type_id;
                    out[n_out].relation_null = false;
                    out[n_out].via = xf[i].object_id;
                    out[n_out].sharing = se->cnt;
                    out[n_out].depth = 0;
                    n_out++;
                }
            }
        }
    }

    /* 3. shared_ancestor: IS_SENSE_OF -> _couple_upward_walk */
    {
        hash128_t         all_k[34];
        int               n_all = 0;
        ArrayType        *all_arr, *sense_arr;
        LaplaceEdgeRawRow *sense_init;
        int               n_si = 0;
        typedef struct { hash128_t k; int first; } CplSenseHead;
        HTAB             *si_heads;
        int              *si_next = NULL;
        ArrayBuildState  *b_roots = NULL, *b_sides = NULL, *b_starts = NULL;
        HTAB             *up_entries = cb_hash_create_key(work, "cpl up",
                                                          sizeof(CplUpKey),
                                                          sizeof(CplUpEntry), 64);
        Oid               uw_types[6] = {
            BYTEAARRAYOID, TEXTARRAYOID, BYTEAARRAYOID,
            BYTEAARRAYOID, INT4OID, INT4OID
        };
        Datum             uw_args[6];
        bool              uw_nulls[6] = { false, false, false, false, false, false };
        CplUpCtx          uctx = { work, up_entries };

        memcpy(all_k, xk, sizeof(hash128_t) * (Size) n_xk);
        memcpy(all_k + n_xk, yk, sizeof(hash128_t) * (Size) n_yk);
        n_all = cb_dedup_ids(all_k, n_xk + n_yk, all_k);

        all_arr = hash128_array_from_ids(all_k, n_all);
        sense_arr = hash128_array_from_ids(&is_sense_of, 1);
        sense_init = laplace_edges_raw_batch_c(all_arr, "out", sense_arr, true, 4, false, "eff_mu", &n_si);
        pfree(all_arr);
        pfree(sense_arr);

        si_heads = cb_hash_create_id(work, "cpl si heads", sizeof(CplSenseHead), Max(n_all, 16));
        if (n_si > 0)
        {
            si_next = (int *) palloc(sizeof(int) * (Size) n_si);
            for (int i = 0; i < n_si; i++)
            {
                bool          f = false;
                CplSenseHead *h = (CplSenseHead *) hash_search(
                    si_heads, &sense_init[i].subject_id, HASH_ENTER, &f);
                si_next[i] = f ? h->first : -1;
                h->first = i;
            }
        }

        for (int pass = 0; pass < 2; pass++)
        {
            const hash128_t *ks = (pass == 0) ? xk : yk;
            int              nk = (pass == 0) ? n_xk : n_yk;
            Datum            side_d = CStringGetTextDatum(pass == 0 ? "x" : "y");

            for (int i = 0; i < nk; i++)
            {
                CplSenseHead *h = (CplSenseHead *) hash_search(si_heads, &ks[i], HASH_FIND, NULL);
                Datum         rk = hash128_to_datum(&ks[i]);
                if (h == NULL)
                {
                    b_roots = accumArrayResult(b_roots, rk, false, BYTEAOID, work);
                    b_sides = accumArrayResult(b_sides, side_d, false, TEXTOID, work);
                    b_starts = accumArrayResult(b_starts, rk, false, BYTEAOID, work);
                }
                else
                {
                    for (int p = h->first; p >= 0; p = si_next[p])
                    {
                        Datum st = sense_init[p].neighbour_null
                                   ? rk
                                   : hash128_to_datum(&sense_init[p].neighbour_id);
                        b_roots = accumArrayResult(b_roots, rk, false, BYTEAOID, work);
                        b_sides = accumArrayResult(b_sides, side_d, false, TEXTOID, work);
                        b_starts = accumArrayResult(b_starts, st, false, BYTEAOID, work);
                    }
                }
            }
        }
        if (sense_init != NULL)
            pfree(sense_init);

        uw_args[0] = b_roots ? makeArrayResult(b_roots, work) : PointerGetDatum(construct_empty_array(BYTEAOID));
        uw_args[1] = b_sides ? makeArrayResult(b_sides, work) : PointerGetDatum(construct_empty_array(TEXTOID));
        uw_args[2] = b_starts ? makeArrayResult(b_starts, work) : PointerGetDatum(construct_empty_array(BYTEAOID));
        uw_args[3] = PointerGetDatum(hash128_array_from_ids(up_ids, (int) n_up_ids));
        uw_args[4] = Int32GetDatum(4);
        uw_args[5] = Int32GetDatum(16);

        (void) laplace_pub_call_srf("converse", "_couple_upward_walk", 6,
                                    uw_types, uw_args, uw_nulls,
                                    cb_visit_upward_walk, &uctx);

        {
            int              total_up = hash_get_num_entries(up_entries);
            CplUpEntry     **xs = (CplUpEntry **) palloc(sizeof(CplUpEntry *) * (Size) Max(total_up, 1));
            CplUpEntry     **ys = (CplUpEntry **) palloc(sizeof(CplUpEntry *) * (Size) Max(total_up, 1));
            int              nx = 0, ny = 0;
            HASH_SEQ_STATUS  seq;
            CplUpEntry      *ue;
            ArrayType       *up_type_arr = hash128_array_from_ids(up_ids, (int) n_up_ids);
            typedef struct { hash128_t node; int64 cnt; } CplNodeShare;
            HTAB            *node_share = cb_hash_create_id(work, "cpl nshare",
                                                            sizeof(CplNodeShare), Max(total_up, 16));

            hash_seq_init(&seq, up_entries);
            while ((ue = (CplUpEntry *) hash_seq_search(&seq)) != NULL)
            {
                if (ue->k.side == 'x')
                    xs[nx++] = ue;
                else if (ue->k.side == 'y')
                    ys[ny++] = ue;
            }

            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    CplNodeShare *ns;
                    bool          f = false;

                    if (!hash128_eq(&xs[i]->k.node, &ys[j]->k.node))
                        continue;

                    ns = (CplNodeShare *) hash_search(node_share, &xs[i]->k.node, HASH_ENTER, &f);
                    if (!f)
                    {
                        ArrayType         *na = hash128_array_from_ids(&xs[i]->k.node, 1);
                        int                n_in = 0;
                        LaplaceEdgeRawRow *ir = laplace_edges_raw_batch_c(
                            na, "in", up_type_arr, true, 256, true, "eff_mu", &n_in);
                        ns->cnt = (int64) n_in;
                        if (ir != NULL)
                            pfree(ir);
                        pfree(na);
                    }

                    if (n_out >= out_cap)
                    {
                        out_cap *= 2;
                        out = (CplOutRow *) repalloc(out, sizeof(CplOutRow) * (Size) out_cap);
                    }
                    out[n_out].x_key = xs[i]->k.root;
                    out[n_out].y_key = ys[j]->k.root;
                    out[n_out].channel = "ancestor";
                    out[n_out].relation_null = true;
                    out[n_out].via = xs[i]->k.node;
                    out[n_out].sharing = ns->cnt;
                    out[n_out].depth = xs[i]->depth + ys[j]->depth;
                    n_out++;
                }
            }
            pfree(up_type_arr);
        }
    }

    if (n_out > 1)
        qsort(out, (Size) n_out, sizeof(CplOutRow), cb_cpl_out_cmp);
    {
        int emit_n = n_out < p_k ? n_out : p_k;
        for (int i = 0; i < emit_n; i++)
        {
            const CplOutRow *r = &out[i];
            Datum values[7] = {
                hash128_to_datum(&r->x_key),
                hash128_to_datum(&r->y_key),
                CStringGetTextDatum(r->channel),
                r->relation_null ? (Datum) 0 : hash128_to_datum(&r->relation),
                hash128_to_datum(&r->via),
                Int64GetDatum(r->sharing),
                Int32GetDatum(r->depth)
            };
            bool nulls[7] = { false, false, false, r->relation_null, false, false, false };

            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
            pfree(DatumGetPointer(values[0]));
            pfree(DatumGetPointer(values[1]));
            pfree(DatumGetPointer(values[2]));
            if (!nulls[3])
                pfree(DatumGetPointer(values[3]));
            pfree(DatumGetPointer(values[4]));
        }
    }

    MemoryContextSwitchTo(old);
    MemoryContextDelete(work);
    return (Datum) 0;
}

typedef struct
{
    ConverseBindingRow row;
    bool               has_dom;
} CbSenseCand;

typedef struct
{
    hash128_t lang;
    int64     total_games;
} CbSenseLangSum;

typedef struct
{
    hash128_t synset_id;
    hash128_t neighbour_id;
} CbSenseOfEntry;

static int
cb_sense_cand_cmp(const void *a, const void *b)
{
    const CbSenseCand *x = (const CbSenseCand *) a;
    const CbSenseCand *y = (const CbSenseCand *) b;
    int c = memcmp(&x->row.term, &y->row.term, sizeof(hash128_t));
    if (c != 0)
        return c;
    if (x->has_dom != y->has_dom)
        return x->has_dom ? -1 : 1;
    if (x->row.games != y->row.games)
        return x->row.games > y->row.games ? -1 : 1;
    if (x->row.eff_mu != y->row.eff_mu)
        return x->row.eff_mu > y->row.eff_mu ? -1 : 1;
    c = memcmp(&x->row.key, &y->row.key, sizeof(hash128_t));
    if (c != 0)
        return c;
    return memcmp(&x->row.binding, &y->row.binding, sizeof(hash128_t));
}

LaplaceSenseRow *
laplace_senses_batch_c(const hash128_t *words, int n_words, int *out_count)
{
    MemoryContext       caller = CurrentMemoryContext;
    MemoryContext       work, old;
    ConverseBindingRow *brows;
    int                 n_brows = 0;
    LaplaceSenseRow    *res = NULL;
    int                 n_kept = 0;

    *out_count = 0;
    if (words == NULL || n_words <= 0)
        return NULL;

    work = AllocSetContextCreate(caller, "laplace_senses_batch_c",
                                 ALLOCSET_DEFAULT_SIZES);
    old = MemoryContextSwitchTo(work);

    {
        hash128_t *uniq = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_words);
        int        n_uniq = cb_dedup_ids(words, n_words, uniq);
        brows = converse_binding_evidence_c(uniq, n_uniq, NULL, false, &n_brows);
    }

    if (brows != NULL && n_brows > 0)
    {
        HTAB           *lang_sums = cb_hash_create_id(work, "ls lang sums",
                                                      sizeof(CbSenseLangSum), n_brows);
        HASH_SEQ_STATUS seq;
        CbSenseLangSum *ls;
        bool            has_dom = false;
        hash128_t       dom_lang = {0, 0};
        int64           best_sum = 0;
        CbSenseCand    *cands = (CbSenseCand *) palloc(sizeof(CbSenseCand) * (Size) n_brows);
        CbSenseCand    *kept = (CbSenseCand *) palloc(sizeof(CbSenseCand) * (Size) n_brows);
        hash128_t      *ss_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_brows);
        int             n_ss = 0;
        HTAB           *sense_of_map = cb_hash_create_id(work, "ls sense_of",
                                                         sizeof(CbSenseOfEntry), Max(n_brows, 16));

        for (int i = 0; i < n_brows; i++)
        {
            int64           g;
            bool            found = false;
            CbSenseLangSum *slot;

            if (brows[i].language_null)
                continue;
            g = brows[i].games > 1 ? brows[i].games : 1;
            slot = (CbSenseLangSum *) hash_search(lang_sums, &brows[i].language, HASH_ENTER, &found);
            slot->total_games = found ? (slot->total_games + g) : g;
        }

        hash_seq_init(&seq, lang_sums);
        while ((ls = (CbSenseLangSum *) hash_seq_search(&seq)) != NULL)
        {
            if (!has_dom || ls->total_games > best_sum ||
                (ls->total_games == best_sum &&
                 memcmp(&ls->lang, &dom_lang, sizeof(hash128_t)) < 0))
            {
                has_dom = true;
                best_sum = ls->total_games;
                dom_lang = ls->lang;
            }
        }

        for (int i = 0; i < n_brows; i++)
        {
            cands[i].row = brows[i];
            cands[i].has_dom = has_dom && !brows[i].language_null &&
                               hash128_eq(&brows[i].language, &dom_lang);
        }
        if (n_brows > 1)
            qsort(cands, (Size) n_brows, sizeof(CbSenseCand), cb_sense_cand_cmp);

        {
            hash128_t cur_term = {0, 0};
            bool      have_term = false;
            int       rank = 0;

            for (int i = 0; i < n_brows; i++)
            {
                if (!have_term || !hash128_eq(&cands[i].row.term, &cur_term))
                {
                    cur_term = cands[i].row.term;
                    have_term = true;
                    rank = 0;
                }
                rank++;
                if (rank > 64)
                    continue;
                kept[n_kept++] = cands[i];
                if (hash128_eq(&cands[i].row.binding, &cands[i].row.key))
                    ss_ids[n_ss++] = cands[i].row.key;
            }
        }

        if (n_ss > 0)
        {
            hash128_t          is_sense_of_id;
            hash128_t         *uniq_ss = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_ss);
            int                n_uss = cb_dedup_ids(ss_ids, n_ss, uniq_ss);
            if (laplace_relation_type_id("IS_SENSE_OF", &is_sense_of_id) == 0)
            {
                ArrayType         *subj_arr = hash128_array_from_ids(uniq_ss, n_uss);
                ArrayType         *type_arr = hash128_array_from_ids(&is_sense_of_id, 1);
                int                n_edges = 0;
                LaplaceEdgeRawRow *edges = laplace_edges_raw_batch_c(
                    subj_arr, "out", type_arr, true, 1, false, "eff_mu", &n_edges);
                for (int ei = 0; ei < n_edges; ei++)
                {
                    bool            found = false;
                    CbSenseOfEntry *se;
                    if (edges[ei].neighbour_null)
                        continue;
                    se = (CbSenseOfEntry *) hash_search(sense_of_map, &edges[ei].subject_id,
                                                        HASH_ENTER, &found);
                    if (!found)
                        se->neighbour_id = edges[ei].neighbour_id;
                }
                if (edges != NULL)
                    pfree(edges);
                pfree(subj_arr);
                pfree(type_arr);
            }
        }

        if (n_kept > 0)
        {
            MemoryContextSwitchTo(caller);
            res = (LaplaceSenseRow *) MemoryContextAlloc(
                caller, sizeof(LaplaceSenseRow) * (Size) n_kept);
            for (int i = 0; i < n_kept; i++)
            {
                const ConverseBindingRow *b = &kept[i].row;
                hash128_t                 syn = b->key;
                if (hash128_eq(&b->binding, &b->key))
                {
                    CbSenseOfEntry *se = (CbSenseOfEntry *) hash_search(
                        sense_of_map, &b->key, HASH_FIND, NULL);
                    if (se != NULL)
                        syn = se->neighbour_id;
                }
                res[i].term = b->term;
                res[i].sense_id = b->binding;
                res[i].synset_id = syn;
                res[i].eff_mu = b->eff_mu;
                res[i].witnesses = !b->witnesses_null ? b->witnesses : (b->games > 1 ? b->games : 1);
            }
        }
    }

    MemoryContextSwitchTo(old);
    MemoryContextDelete(work);
    *out_count = n_kept;
    return res;
}

Datum
pg_laplace_senses_batch_impl(PG_FUNCTION_ARGS)
{
    ReturnSetInfo   *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    hash128_t       *words;
    int              n_words = 0;
    LaplaceSenseRow *rows;
    int              n_rows = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;
    words = cb_deconstruct_bytea_ids(PG_GETARG_ARRAYTYPE_P(0), &n_words);
    if (n_words == 0)
    {
        if (words != NULL)
            pfree(words);
        return (Datum) 0;
    }

    rows = laplace_senses_batch_c(words, n_words, &n_rows);
    pfree(words);
    if (rows != NULL)
    {
        for (int i = 0; i < n_rows; i++)
        {
            Datum vals[5] = {
                hash128_to_datum(&rows[i].term),
                hash128_to_datum(&rows[i].sense_id),
                hash128_to_datum(&rows[i].synset_id),
                DirectFunctionCall1(float8_numeric, Float8GetDatum(rows[i].eff_mu)),
                Int64GetDatum(rows[i].witnesses)
            };
            bool nulls[5] = { false, false, false, false, false };

            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
            pfree(DatumGetPointer(vals[0]));
            pfree(DatumGetPointer(vals[1]));
            pfree(DatumGetPointer(vals[2]));
            pfree(DatumGetPointer(vals[3]));
        }
        pfree(rows);
    }
    return (Datum) 0;
}

Datum
laplace_word_language_c(Datum word)
{
    hash128_t            w;
    ConverseWordLangRow *cands;
    int                  n = 0;
    int                  best = -1;

    if (word == (Datum) 0)
        return (Datum) 0;
    w = datum_to_hash128(word);
    cands = converse_word_language_candidates_batch_c(&w, 1, &n);
    if (cands == NULL || n == 0)
        return (Datum) 0;

    for (int i = 0; i < n; i++)
    {
        if (best < 0 || cands[i].mu > cands[best].mu ||
            (cands[i].mu == cands[best].mu &&
             memcmp(&cands[i].lang, &cands[best].lang, sizeof(hash128_t)) < 0))
            best = i;
    }
    {
        Datum res = (best >= 0) ? hash128_to_datum(&cands[best].lang) : (Datum) 0;
        pfree(cands);
        return res;
    }
}

int
laplace_fetch_synset_ids_c(Datum word, Datum **out_ids, int *out_n)
{
    hash128_t        w;
    LaplaceSenseRow *rows;
    int              n = 0;

    *out_ids = NULL;
    *out_n = 0;
    if (word == (Datum) 0)
        return 0;
    w = datum_to_hash128(word);
    rows = laplace_senses_batch_c(&w, 1, &n);
    if (rows == NULL || n == 0)
        return 0;

    *out_ids = (Datum *) palloc(sizeof(Datum) * (Size) n);
    for (int i = 0; i < n; i++)
        (*out_ids)[i] = hash128_to_datum(&rows[i].synset_id);
    pfree(rows);
    *out_n = n;
    return n;
}
