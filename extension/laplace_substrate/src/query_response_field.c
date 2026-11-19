#include "postgres.h"

#include <limits.h>
#include <math.h>
#include <string.h>

#include "catalog/pg_type.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/jsonb.h"
#include "utils/memutils.h"

#include "laplace/core/hash128.h"
#include "laplace_pg/executor.h"

#include "converse_bindings.h"
#include "edges_raw_internal.h"
#include "public_direct.h"
#include "query_program.h"
#include "query_response_field.h"
#include "relations_internal.h"
#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_laplace_query_declared_paths);
PG_FUNCTION_INFO_V1(pg_laplace_recall_declared_definitions);
PG_FUNCTION_INFO_V1(pg_laplace_query_lexicon_relations);
PG_FUNCTION_INFO_V1(pg_laplace_query_lexicon_descriptions);
PG_FUNCTION_INFO_V1(pg_laplace_query_lexicon_field);
PG_FUNCTION_INFO_V1(pg_laplace_query_response_field);

static HTAB *
qrf_hash_id(MemoryContext ctx, const char *name, Size entry_sz, long init)
{
    HASHCTL ctl;
    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = entry_sz;
    ctl.hcxt = ctx;
    return hash_create(name, Max(init, 16), &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
}

static HTAB *
qrf_hash_key(MemoryContext ctx, const char *name, Size key_sz, Size entry_sz, long init)
{
    HASHCTL ctl;
    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = key_sz;
    ctl.entrysize = entry_sz;
    ctl.hcxt = ctx;
    return hash_create(name, Max(init, 16), &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
}

static int
qrf_hash128_cmp(const void *a, const void *b)
{
    return memcmp(a, b, sizeof(hash128_t));
}

static int
qrf_dedup_ids(const hash128_t *src, int n, hash128_t *dst)
{
    int out = 0;
    if (n <= 0 || src == NULL)
        return 0;
    memcpy(dst, src, sizeof(hash128_t) * (Size) n);
    if (n > 1)
        qsort(dst, (Size) n, sizeof(hash128_t), qrf_hash128_cmp);
    dst[out++] = dst[0];
    for (int i = 1; i < n; i++)
    {
        if (!hash128_eq(&dst[out - 1], &dst[i]))
            dst[out++] = dst[i];
    }
    return out;
}

static hash128_t *
qrf_bytea_ids_nonnull(ArrayType *arr, bool dedup, int *out_count)
{
    Datum     *vals;
    bool      *nulls;
    int        n = 0, valid = 0;
    hash128_t *ids;

    *out_count = 0;
    if (arr == NULL)
        return NULL;
    deconstruct_array(arr, BYTEAOID, -1, false, TYPALIGN_INT, &vals, &nulls, &n);
    if (n <= 0)
    {
        pfree(vals);
        pfree(nulls);
        return NULL;
    }
    ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n);
    for (int i = 0; i < n; i++)
    {
        if (!nulls[i])
            ids[valid++] = datum_to_hash128(vals[i]);
    }
    pfree(vals);
    pfree(nulls);
    if (dedup && valid > 0)
        valid = qrf_dedup_ids(ids, valid, ids);
    *out_count = valid;
    return ids;
}

static int
qrf_hex_nibble(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

static bool
qrf_decode_hex128(const char *s, int len, hash128_t *out)
{
    uint8 bytes[16];
    if (s == NULL || len != 32)
        return false;
    for (int i = 0; i < 16; i++)
    {
        int hi = qrf_hex_nibble(s[i * 2]);
        int lo = qrf_hex_nibble(s[i * 2 + 1]);
        if (hi < 0 || lo < 0)
            return false;
        bytes[i] = (uint8) ((hi << 4) | lo);
    }
    memcpy(out, bytes, 16);
    return true;
}

static char *
qrf_id_text_alloc(const hash128_t *id)
{
    char *buf = (char *) palloc(33);
    lp_id_hex((const lp_id *) id, buf);
    return buf;
}

/* ========================================================================
 * Shared label / physicality / constituent collectors
 * ======================================================================== */

typedef struct
{
    hash128_t entity;
    int16     kind_slot;
    bool      is_tuple;
    int16     tier;
} QrfFormEntry;

typedef struct
{
    MemoryContext caller;
    HTAB         *forms;
} QrfPhysCtx;

static bool
qrf_visit_physicality(TupleTableSlot *slot, void *opaque)
{
    QrfPhysCtx   *ctx = (QrfPhysCtx *) opaque;
    bool          isnull = false;
    Datum         d_ent, d_tier, d_mask;
    hash128_t     ent;
    int16         tier = 0, kind_slot = -1;
    bool          is_tuple = false;
    QrfFormEntry *fe;
    bool          found = false;
    MemoryContext prev;

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    ent = laplace_pub_blake3_to_hash128(d_ent);

    d_tier = slot_getattr(slot, 3, &isnull);
    if (!isnull) tier = DatumGetInt16(d_tier);

    d_mask = slot_getattr(slot, 6, &isnull);
    if (!isnull)
    {
        kind_slot = laplace_pub_mask_value(d_mask, "kind");
        is_tuple = laplace_pub_mask_has_value(d_mask, "kind", "tuple");
    }

    prev = MemoryContextSwitchTo(ctx->caller);
    fe = (QrfFormEntry *) hash_search(ctx->forms, &ent, HASH_ENTER, &found);
    if (!found)
    {
        fe->kind_slot = kind_slot;
        fe->is_tuple = is_tuple;
        fe->tier = tier;
    }
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct
{
    hash128_t entity;
    char     *content;
    bool      content_null;
} QrfLabelEntry;

typedef struct
{
    MemoryContext caller;
    HTAB         *labels;
} QrfLabelCtx;

static bool
qrf_visit_label(TupleTableSlot *slot, void *opaque)
{
    QrfLabelCtx   *ctx = (QrfLabelCtx *) opaque;
    bool           isnull = false;
    Datum          d_ent, d_lbl;
    hash128_t      ent;
    char          *str = NULL;
    QrfLabelEntry *le;
    bool           found = false;
    MemoryContext  prev;

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    ent = laplace_pub_blake3_to_hash128(d_ent);
    d_lbl = slot_getattr(slot, 3, &isnull);
    if (!isnull) str = TextDatumGetCString(d_lbl);

    prev = MemoryContextSwitchTo(ctx->caller);
    le = (QrfLabelEntry *) hash_search(ctx->labels, &ent, HASH_ENTER, &found);
    if (!found)
    {
        le->content_null = isnull;
        le->content = isnull ? NULL : pstrdup(str);
    }
    MemoryContextSwitchTo(prev);
    if (str != NULL) pfree(str);
    return true;
}

typedef struct
{
    MemoryContext caller;
    HTAB         *target_children;
    HTAB         *matched_entities;
} QrfDescPredCtx;

static bool
qrf_visit_constituent_match(TupleTableSlot *slot, void *opaque)
{
    QrfDescPredCtx *ctx = (QrfDescPredCtx *) opaque;
    bool            isnull = false;
    Datum           d_ent, d_child;
    hash128_t       ent, child;
    MemoryContext   prev;

    d_child = slot_getattr(slot, 3, &isnull);
    if (isnull) return true;
    child = laplace_pub_blake3_to_hash128(d_child);
    if (hash_search(ctx->target_children, &child, HASH_FIND, NULL) == NULL)
        return true;

    d_ent = slot_getattr(slot, 1, &isnull);
    if (isnull) return true;
    ent = laplace_pub_blake3_to_hash128(d_ent);

    prev = MemoryContextSwitchTo(ctx->caller);
    (void) hash_search(ctx->matched_entities, &ent, HASH_ENTER, &isnull);
    MemoryContextSwitchTo(prev);
    return true;
}

/* ========================================================================
 * 1 & 2. Declared paths & declared definitions (chains JSONB + laplace_chains)
 * ======================================================================== */

typedef struct
{
    int32     program;
    int32     depth;
    hash128_t step;
    bool      inverse;
} QpStepKey;

typedef struct
{
    int32     root;
    int32     program;
    int64     path;
    int32     depth;
    hash128_t claim;
    hash128_t entity;
    hash128_t relation;
    double    rating;
    double    deviation;
    int64     matches;
} QpWalkRow;

typedef struct
{
    hash128_t entity;
    hash128_t predicate;
} QpPrevKey;

typedef struct
{
    QpPrevKey k;
    char     *type_label;
    char     *fact;
} QpPrevEntry;

typedef struct
{
    MemoryContext caller;
    QpWalkRow    *rows;
    int           count;
    int           cap;
    hash128_t    *claims;
    int           n_claims;
} QpWalkCtx;

static bool
qp_visit_walk(TupleTableSlot *slot, void *opaque)
{
    QpWalkCtx *ctx = (QpWalkCtx *) opaque;
    bool       isnull = false;
    Datum      d_root, d_prog, d_path, d_depth, d_claim, d_ent, d_rel, d_rat, d_dev, d_mat;
    MemoryContext prev;

    d_root  = slot_getattr(slot, 1, &isnull); if (isnull) return true;
    d_prog  = slot_getattr(slot, 2, &isnull); if (isnull) return true;
    d_path  = slot_getattr(slot, 3, &isnull); if (isnull) return true;
    d_depth = slot_getattr(slot, 4, &isnull); if (isnull) return true;
    d_claim = slot_getattr(slot, 5, &isnull); if (isnull) return true;
    d_ent   = slot_getattr(slot, 6, &isnull); if (isnull) return true;
    d_rel   = slot_getattr(slot, 7, &isnull); if (isnull) return true;
    d_rat   = slot_getattr(slot, 8, &isnull); if (isnull) return true;
    d_dev   = slot_getattr(slot, 9, &isnull); if (isnull) return true;
    d_mat   = slot_getattr(slot, 10, &isnull); if (isnull) return true;

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count >= ctx->cap)
    {
        Size sz;
        ctx->cap = ctx->cap ? ctx->cap * 2 : 64;
        sz = sizeof(QpWalkRow) * (Size) ctx->cap;
        ctx->rows = ctx->rows ? (QpWalkRow *) repalloc(ctx->rows, sz) : (QpWalkRow *) palloc(sz);
        ctx->claims = ctx->claims
            ? (hash128_t *) repalloc(ctx->claims, sizeof(hash128_t) * (Size) ctx->cap)
            : (hash128_t *) palloc(sizeof(hash128_t) * (Size) ctx->cap);
    }
    ctx->rows[ctx->count].root = DatumGetInt32(d_root);
    ctx->rows[ctx->count].program = DatumGetInt32(d_prog);
    ctx->rows[ctx->count].path = DatumGetInt64(d_path);
    ctx->rows[ctx->count].depth = DatumGetInt32(d_depth);
    ctx->rows[ctx->count].claim = laplace_pub_blake3_to_hash128(d_claim);
    ctx->rows[ctx->count].entity = laplace_pub_blake3_to_hash128(d_ent);
    ctx->rows[ctx->count].relation = laplace_pub_blake3_to_hash128(d_rel);
    ctx->rows[ctx->count].rating = DatumGetFloat8(d_rat);
    ctx->rows[ctx->count].deviation = DatumGetFloat8(d_dev);
    ctx->rows[ctx->count].matches = DatumGetInt64(d_mat);
    ctx->claims[ctx->n_claims++] = ctx->rows[ctx->count].claim;
    ctx->count++;
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct
{
    MemoryContext caller;
    HTAB         *prev_map;
} QpPrevCtx;

static bool
qp_visit_preview(TupleTableSlot *slot, void *opaque)
{
    QpPrevCtx   *ctx = (QpPrevCtx *) opaque;
    bool         isnull = false;
    Datum        d_ent, d_pred, d_tlbl, d_fact;
    QpPrevKey    pk;
    QpPrevEntry *pe;
    char        *tlbl = NULL, *fact = NULL;
    bool         found = false;
    MemoryContext prev;

    d_ent = slot_getattr(slot, 2, &isnull);  if (isnull) return true;
    d_pred = slot_getattr(slot, 3, &isnull); if (isnull) return true;
    d_tlbl = slot_getattr(slot, 5, &isnull); if (!isnull) tlbl = TextDatumGetCString(d_tlbl);
    d_fact = slot_getattr(slot, 6, &isnull); if (!isnull) fact = TextDatumGetCString(d_fact);

    MemSet(&pk, 0, sizeof(pk));
    pk.entity = laplace_pub_blake3_to_hash128(d_ent);
    pk.predicate = laplace_pub_blake3_to_hash128(d_pred);

    prev = MemoryContextSwitchTo(ctx->caller);
    pe = (QpPrevEntry *) hash_search(ctx->prev_map, &pk, HASH_ENTER, &found);
    if (!found)
    {
        pe->type_label = tlbl ? pstrdup(tlbl) : NULL;
        pe->fact = fact ? pstrdup(fact) : NULL;
    }
    MemoryContextSwitchTo(prev);
    if (tlbl) pfree(tlbl);
    if (fact) pfree(fact);
    return true;
}

static int
qp_step_cmp(const void *a, const void *b)
{
    const QpStepKey *x = (const QpStepKey *) a;
    const QpStepKey *y = (const QpStepKey *) b;
    int c;
    if (x->program != y->program) return x->program < y->program ? -1 : 1;
    if (x->depth != y->depth) return x->depth < y->depth ? -1 : 1;
    c = memcmp(&x->step, &y->step, sizeof(hash128_t));
    if (c != 0) return c;
    if (x->inverse != y->inverse) return x->inverse ? 1 : -1;
    return 0;
}

static int
qp_walk_cmp(const void *a, const void *b)
{
    const QpWalkRow *x = (const QpWalkRow *) a;
    const QpWalkRow *y = (const QpWalkRow *) b;
    if (x->root != y->root) return x->root < y->root ? -1 : 1;
    if (x->program != y->program) return x->program < y->program ? -1 : 1;
    if (x->path != y->path) return x->path < y->path ? -1 : 1;
    if (x->depth != y->depth) return x->depth < y->depth ? -1 : 1;
    return 0;
}

typedef struct
{
    int32     root_idx;
    int32     program_idx;
    int64     path_idx;
    int32     depth;
    hash128_t claim;
    hash128_t entity;
    char     *type_label;
    char     *fact;
    double    eff_mu;
    int64     witnesses;
} QpJoinedRow;

static int
qp_extract_steps_from_jsonb(Jsonb *jb, QpStepKey **out_steps)
{
    JsonbValue    v_key;
    JsonbValue   *chains;
    JsonbIterator *it_c;
    JsonbValue    val;
    JsonbIteratorToken rtok;
    int           prog = 0;
    HTAB         *seen = qrf_hash_key(CurrentMemoryContext, "qp steps",
                                      sizeof(QpStepKey), sizeof(QpStepKey), 32);
    QpStepKey    *steps = NULL;
    int           n_steps = 0, cap = 0;

    *out_steps = NULL;
    if (jb == NULL || JB_ROOT_IS_SCALAR(jb))
        return 0;
    v_key.type = jbvString;
    v_key.val.string.val = "chains";
    v_key.val.string.len = 6;
    chains = findJsonbValueFromContainer(&jb->root, JB_FOBJECT, &v_key);
    if (chains == NULL || chains->type != jbvBinary || chains->val.binary.data == NULL)
        return 0;

    it_c = JsonbIteratorInit(chains->val.binary.data);
    rtok = JsonbIteratorNext(&it_c, &val, true);
    if (rtok != WJB_BEGIN_ARRAY)
        return 0;

    while ((rtok = JsonbIteratorNext(&it_c, &val, true)) == WJB_ELEM)
    {
        JsonbIterator *it_r;
        int            depth = 0;
        prog++;
        if (val.type != jbvBinary || val.val.binary.data == NULL)
            continue;
        it_r = JsonbIteratorInit(val.val.binary.data);
        if (JsonbIteratorNext(&it_r, &val, true) != WJB_BEGIN_ARRAY)
            continue;
        while (JsonbIteratorNext(&it_r, &val, true) == WJB_ELEM)
        {
            if (val.type == jbvBinary && val.val.binary.data != NULL)
            {
                JsonbValue ek;
                JsonbValue *ev;
                hash128_t   rel;
                ek.type = jbvString;
                ek.val.string.val = "entity";
                ek.val.string.len = 6;
                ev = findJsonbValueFromContainer(val.val.binary.data, JB_FOBJECT, &ek);
                if (ev != NULL && ev->type == jbvString &&
                    qrf_decode_hex128(ev->val.string.val, ev->val.string.len, &rel))
                {
                    int                     n_s = 0;
                    LaplaceRelationSurface *surfs = laplace_relation_surfaces_expand_c(&rel, 1, &n_s);
                    for (int si = 0; si < n_s; si++)
                    {
                        QpStepKey sk;
                        bool      found = false;
                        MemSet(&sk, 0, sizeof(sk));
                        sk.program = prog;
                        sk.depth = depth;
                        sk.step = surfs[si].surface;
                        sk.inverse = surfs[si].inverse;
                        (void) hash_search(seen, &sk, HASH_ENTER, &found);
                        if (!found)
                        {
                            if (n_steps >= cap)
                            {
                                Size sz;
                                cap = cap ? cap * 2 : 32;
                                sz = sizeof(QpStepKey) * (Size) cap;
                                steps = steps ? (QpStepKey *) repalloc(steps, sz) : (QpStepKey *) palloc(sz);
                            }
                            steps[n_steps++] = sk;
                        }
                    }
                }
            }
            depth++;
        }
    }
    hash_destroy(seen);
    if (n_steps > 1)
        qsort(steps, (Size) n_steps, sizeof(QpStepKey), qp_step_cmp);
    *out_steps = steps;
    return n_steps;
}

static QpJoinedRow *
run_declared_chains_c(const hash128_t *roots, int n_roots,
                      Jsonb *program_jb, int64 cap, int *out_count)
{
    MemoryContext caller = CurrentMemoryContext;
    MemoryContext work, prev;
    QpStepKey    *steps = NULL;
    int           n_steps;
    Oid           b3_arr_oid = laplace_pub_blake3_array_oid();
    QpJoinedRow  *res = NULL;
    int           n_out = 0;

    *out_count = 0;
    if (n_roots <= 0 || program_jb == NULL)
        return NULL;

    work = AllocSetContextCreate(caller, "run_declared_chains", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    n_steps = qp_extract_steps_from_jsonb(program_jb, &steps);
    {
        hash128_t *step_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) Max(n_steps, 1));
        Datum     *prog_d = (Datum *) palloc(sizeof(Datum) * (Size) Max(n_steps, 1));
        Datum     *dep_d = (Datum *) palloc(sizeof(Datum) * (Size) Max(n_steps, 1));
        Datum     *inv_d = (Datum *) palloc(sizeof(Datum) * (Size) Max(n_steps, 1));
        ArrayType *roots_arr = laplace_pub_blake3_array(roots, n_roots);
        ArrayType *steps_arr, *progs_arr, *deps_arr, *invs_arr;
        Oid        ch_types[6] = { b3_arr_oid, b3_arr_oid, INT4ARRAYOID, INT4ARRAYOID, BOOLARRAYOID, INT8OID };
        Datum      ch_args[6];
        bool       ch_nulls[6] = { false, false, false, false, false, false };
        QpWalkCtx  wctx;

        for (int i = 0; i < n_steps; i++)
        {
            step_ids[i] = steps[i].step;
            prog_d[i] = Int32GetDatum(steps[i].program);
            dep_d[i] = Int32GetDatum(steps[i].depth);
            inv_d[i] = BoolGetDatum(steps[i].inverse);
        }
        steps_arr = laplace_pub_blake3_array(step_ids, n_steps);
        progs_arr = n_steps > 0 ? construct_array(prog_d, n_steps, INT4OID, sizeof(int32), true, TYPALIGN_INT)
                                : construct_empty_array(INT4OID);
        deps_arr  = n_steps > 0 ? construct_array(dep_d, n_steps, INT4OID, sizeof(int32), true, TYPALIGN_INT)
                                : construct_empty_array(INT4OID);
        invs_arr  = n_steps > 0 ? construct_array(inv_d, n_steps, BOOLOID, sizeof(bool), true, TYPALIGN_CHAR)
                                : construct_empty_array(BOOLOID);

        ch_args[0] = PointerGetDatum(roots_arr);
        ch_args[1] = PointerGetDatum(steps_arr);
        ch_args[2] = PointerGetDatum(progs_arr);
        ch_args[3] = PointerGetDatum(deps_arr);
        ch_args[4] = PointerGetDatum(invs_arr);
        ch_args[5] = Int64GetDatum(cap);

        MemSet(&wctx, 0, sizeof(wctx));
        wctx.caller = work;
        (void) laplace_pub_call_srf("public", "laplace_chains", 6,
                                    ch_types, ch_args, ch_nulls,
                                    qp_visit_walk, &wctx);

        if (wctx.count > 0)
        {
            hash128_t *uniq_cl = (hash128_t *) palloc(sizeof(hash128_t) * (Size) wctx.n_claims);
            int        n_ucl = qrf_dedup_ids(wctx.claims, wctx.n_claims, uniq_cl);
            ArrayType *cl_arr = laplace_pub_blake3_array(uniq_cl, n_ucl);
            HTAB      *prev_map = qrf_hash_key(work, "qp prev", sizeof(QpPrevKey), sizeof(QpPrevEntry), n_ucl);
            QpPrevCtx  pctx = { work, prev_map };
            Datum      pa = PointerGetDatum(cl_arr);
            bool       pn = false;

            (void) laplace_pub_call_srf("public", "laplace_claim_preview", 1,
                                        &b3_arr_oid, &pa, &pn, qp_visit_preview, &pctx);

            if (wctx.count > 1)
                qsort(wctx.rows, (Size) wctx.count, sizeof(QpWalkRow), qp_walk_cmp);

            MemoryContextSwitchTo(caller);
            res = (QpJoinedRow *) MemoryContextAlloc(caller, sizeof(QpJoinedRow) * (Size) wctx.count);
            for (int i = 0; i < wctx.count; i++)
            {
                const QpWalkRow *w = &wctx.rows[i];
                QpPrevKey        pk;
                QpPrevEntry     *pe;

                MemSet(&pk, 0, sizeof(pk));
                pk.entity = w->claim;
                pk.predicate = w->relation;
                pe = (QpPrevEntry *) hash_search(prev_map, &pk, HASH_FIND, NULL);
                if (pe == NULL)
                    continue;
                res[n_out].root_idx = w->root - 1;
                res[n_out].program_idx = w->program - 1;
                res[n_out].path_idx = w->path;
                res[n_out].depth = w->depth;
                res[n_out].claim = w->claim;
                res[n_out].entity = w->entity;
                res[n_out].type_label = pe->type_label ? pstrdup(pe->type_label) : NULL;
                res[n_out].fact = pe->fact ? pstrdup(pe->fact) : NULL;
                res[n_out].eff_mu = w->rating - 2.0 * w->deviation;
                res[n_out].witnesses = w->matches > 1 ? w->matches : 1;
                n_out++;
            }
        }
    }

    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    *out_count = n_out;
    return res;
}

Datum
pg_laplace_query_declared_paths(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    hash128_t     *roots;
    int            n_roots = 0;
    Jsonb         *prog_jb;
    int32          cap;
    QpJoinedRow   *rows;
    int            n_rows = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1) || PG_ARGISNULL(2))
        return (Datum) 0;
    roots = qrf_bytea_ids_nonnull(PG_GETARG_ARRAYTYPE_P(0), false, &n_roots);
    if (n_roots == 0)
    {
        if (roots) pfree(roots);
        return (Datum) 0;
    }
    prog_jb = PG_GETARG_JSONB_P(1);
    cap = PG_GETARG_INT32(2);
    rows = run_declared_chains_c(roots, n_roots, prog_jb, (int64) cap, &n_rows);
    pfree(roots);

    for (int i = 0; i < n_rows; i++)
    {
        const QpJoinedRow *r = &rows[i];
        Datum vals[10] = {
            Int32GetDatum(r->root_idx),
            Int32GetDatum(r->program_idx),
            Int64GetDatum(r->path_idx),
            Int32GetDatum(r->depth),
            hash128_to_datum(&r->claim),
            hash128_to_datum(&r->entity),
            r->type_label ? CStringGetTextDatum(r->type_label) : (Datum) 0,
            r->fact ? CStringGetTextDatum(r->fact) : (Datum) 0,
            DirectFunctionCall1(float8_numeric, Float8GetDatum(r->eff_mu)),
            Int64GetDatum(r->witnesses)
        };
        bool nulls[10] = {
            false, false, false, false, false, false,
            (r->type_label == NULL), (r->fact == NULL), false, false
        };
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
        pfree(DatumGetPointer(vals[4]));
        pfree(DatumGetPointer(vals[5]));
        if (!nulls[6]) pfree(DatumGetPointer(vals[6]));
        if (!nulls[7]) pfree(DatumGetPointer(vals[7]));
        pfree(DatumGetPointer(vals[8]));
    }
    return (Datum) 0;
}

/* ---- recall.declared_definitions(bytea) ---- */

typedef struct
{
    int64     path_idx;
    hash128_t sense;
} RdSenseEntry;

typedef struct
{
    hash128_t sense;
    int       first;
} RdSenseHead;

typedef struct
{
    hash128_t entity;
    double    rating;
    double    deviation;
    int64     matches;
    hash128_t parts[3];
    int       n_parts;
} RdClaimCell;

typedef struct
{
    MemoryContext caller;
    RdClaimCell  *items;
    int           count;
    int           cap;
} RdClaimCtx;

static bool
rd_visit_claim(TupleTableSlot *slot, void *opaque)
{
    RdClaimCtx *ctx = (RdClaimCtx *) opaque;
    bool        isnull = false;
    Datum       d_ent, d_path, d_rat, d_dev, d_mat;
    double      rat = 0.0, dev = 0.0;
    int64       mat = 0;
    lp_path     path;
    lp_id       parts[3];
    MemoryContext prev;

    d_mat = slot_getattr(slot, 7, &isnull);
    if (!isnull) mat = DatumGetInt64(d_mat);
    if (mat <= 1) return true;

    d_rat = slot_getattr(slot, 4, &isnull); if (!isnull) rat = DatumGetFloat8(d_rat);
    d_dev = slot_getattr(slot, 5, &isnull); if (!isnull) dev = DatumGetFloat8(d_dev);
    if ((rat - 2.0 * dev) <= 0.0) return true;

    d_path = slot_getattr(slot, 3, &isnull); if (isnull) return true;
    path = laplace_pub_geo_path(d_path);
    if (lp_path_len(path) != 3 || lp_path_expand(path, parts, 3) != 3)
        return true;

    d_ent = slot_getattr(slot, 2, &isnull); if (isnull) return true;

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count >= ctx->cap)
    {
        Size sz;
        ctx->cap = ctx->cap ? ctx->cap * 2 : 32;
        sz = sizeof(RdClaimCell) * (Size) ctx->cap;
        ctx->items = ctx->items ? (RdClaimCell *) repalloc(ctx->items, sz) : (RdClaimCell *) palloc(sz);
    }
    ctx->items[ctx->count].entity = laplace_pub_blake3_to_hash128(d_ent);
    ctx->items[ctx->count].rating = rat;
    ctx->items[ctx->count].deviation = dev;
    ctx->items[ctx->count].matches = mat;
    ctx->items[ctx->count].n_parts = 3;
    for (int i = 0; i < 3; i++)
        memcpy(&ctx->items[ctx->count].parts[i], parts[i].b, sizeof(hash128_t));
    ctx->count++;
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct { hash128_t obj; int64 freq; } RdFreqEntry;
typedef struct { MemoryContext caller; HTAB *freqs; } RdFreqCtx;

static bool
rd_parse_u18(const char *s, int64 *out)
{
    size_t len;
    int64  v = 0;
    if (!s) return false;
    len = strlen(s);
    if (len == 0 || len > 18) return false;
    for (size_t i = 0; i < len; i++)
    {
        if (s[i] < '0' || s[i] > '9') return false;
        v = v * 10 + (int64) (s[i] - '0');
    }
    *out = v;
    return true;
}

static bool
rd_visit_freq_label(TupleTableSlot *slot, void *opaque)
{
    RdFreqCtx *ctx = (RdFreqCtx *) opaque;
    bool       isnull = false;
    Datum      d_id, d_lbl;
    hash128_t  id;
    char      *str;
    int64      freq = 0;
    MemoryContext prev;

    d_id = slot_getattr(slot, 2, &isnull); if (isnull) return true;
    d_lbl = slot_getattr(slot, 3, &isnull); if (isnull) return true;
    id = laplace_pub_blake3_to_hash128(d_id);
    str = TextDatumGetCString(d_lbl);
    if (rd_parse_u18(str, &freq))
    {
        bool         f = false;
        RdFreqEntry *fe;
        prev = MemoryContextSwitchTo(ctx->caller);
        fe = (RdFreqEntry *) hash_search(ctx->freqs, &id, HASH_ENTER, &f);
        fe->freq = freq;
        MemoryContextSwitchTo(prev);
    }
    pfree(str);
    return true;
}

typedef struct { int64 path_idx; double boost_mu; int64 boost_w; } RdBoostEntry;

typedef struct
{
    hash128_t entity_id;
    char     *label;
    double    eff_mu;
    int64     witnesses;
    int32     program_index;
    int64     path_index;
} RdDefRow;

static int
rd_def_cmp(const void *a, const void *b)
{
    const RdDefRow *x = (const RdDefRow *) a;
    const RdDefRow *y = (const RdDefRow *) b;
    if (x->eff_mu != y->eff_mu) return x->eff_mu > y->eff_mu ? -1 : 1;
    if (x->program_index != y->program_index) return x->program_index < y->program_index ? -1 : 1;
    if (x->path_index != y->path_index) return x->path_index < y->path_index ? -1 : 1;
    return memcmp(&x->entity_id, &y->entity_id, sizeof(hash128_t));
}

typedef struct
{
    hash128_t entity_id;
    double    eff_mu;
    int64     witnesses;
    double    rank_score;
} RdLexPick;

static int
rd_pick_cmp(const void *a, const void *b)
{
    const RdLexPick *x = (const RdLexPick *) a;
    const RdLexPick *y = (const RdLexPick *) b;
    if (x->rank_score != y->rank_score) return x->rank_score > y->rank_score ? -1 : 1;
    return memcmp(&x->entity_id, &y->entity_id, sizeof(hash128_t));
}

RdDefOutRow *
laplace_recall_declared_definitions_c(const hash128_t *word, int *out_count)
{
    MemoryContext  caller = CurrentMemoryContext;
    MemoryContext  work, prev;
    RdDefOutRow   *out = NULL;
    int            cnt = 0;

    *out_count = 0;
    if (word == NULL)
        return NULL;

    work = AllocSetContextCreate(caller, "recall_declared_definitions", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    {
        Jsonb       *prog_jb = DatumGetJsonbP(DirectFunctionCall1(jsonb_in, CStringGetDatum(laplace_compiled_query_program)));
        int          n_paths = 0;
        QpJoinedRow *paths = run_declared_chains_c(word, 1, prog_jb, 4096, &n_paths);
        HTAB        *best_by_entity = qrf_hash_id(work, "rd best", sizeof(RdDefRow), Max(n_paths, 16));

        if (n_paths > 0)
        {
            /* Keep only max depth per path_index. */
            typedef struct { int64 pid; int32 max_d; int idx; } RdLastPath;
            HTAB       *last_map = qrf_hash_key(work, "rd last", sizeof(int64), sizeof(RdLastPath), n_paths);
            int        *sense_idx = (int *) palloc(sizeof(int) * (Size) n_paths);
            int         n_senses = 0;
            HTAB       *boost_map = qrf_hash_key(work, "rd boost", sizeof(int64), sizeof(RdBoostEntry), n_paths);

            for (int i = 0; i < n_paths; i++)
            {
                bool        f = false;
                RdLastPath *lp = (RdLastPath *) hash_search(last_map, &paths[i].path_idx, HASH_ENTER, &f);
                if (!f || paths[i].depth > lp->max_d)
                {
                    lp->max_d = paths[i].depth;
                    lp->idx = i;
                }
                if (paths[i].depth == 0)
                    sense_idx[n_senses++] = i;
            }

            if (n_senses > 0)
            {
                hash128_t *raw_s = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_senses);
                hash128_t *uniq_s = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_senses);
                int        n_us;
                Oid        b3_arr_oid = laplace_pub_blake3_array_oid();
                ArrayType *s_arr;
                ArrayType *empty_b3 = laplace_pub_blake3_array(NULL, 0);
                Datum      bit_d = Int16GetDatum(laplace_pub_bank_bit("kind", "claim"));
                ArrayType *bits_arr = construct_array(&bit_d, 1, INT2OID, sizeof(int16), true, TYPALIGN_SHORT);
                Oid        c_types[7] = { b3_arr_oid, INT8OID, INT2ARRAYOID, b3_arr_oid, INT2OID, b3_arr_oid, INT8OID };
                Datum      c_args[7];
                bool       c_nulls[7] = { false, true, false, false, false, true, false };
                RdClaimCtx cctx;

                for (int i = 0; i < n_senses; i++)
                    raw_s[i] = paths[sense_idx[i]].entity;
                n_us = qrf_dedup_ids(raw_s, n_senses, uniq_s);
                s_arr = laplace_pub_blake3_array(uniq_s, n_us);

                c_args[0] = PointerGetDatum(s_arr);
                c_args[1] = (Datum) 0;
                c_args[2] = PointerGetDatum(bits_arr);
                c_args[3] = PointerGetDatum(empty_b3);
                c_args[4] = Int16GetDatum(1);
                c_args[5] = (Datum) 0;
                c_args[6] = Int64GetDatum(3);

                MemSet(&cctx, 0, sizeof(cctx));
                cctx.caller = work;
                (void) laplace_pub_call_srf("public", "laplace_claims_each", 7,
                                            c_types, c_args, c_nulls, rd_visit_claim, &cctx);

                if (cctx.count > 0)
                {
                    hash128_t *objs = (hash128_t *) palloc(sizeof(hash128_t) * (Size) cctx.count);
                    hash128_t *uobjs = (hash128_t *) palloc(sizeof(hash128_t) * (Size) cctx.count);
                    int        n_uo;
                    HTAB      *freqs = qrf_hash_id(work, "rd freqs", sizeof(RdFreqEntry), cctx.count);
                    RdFreqCtx  fctx = { work, freqs };
                    ArrayType *o_arr;
                    Datum      oa;
                    bool       on = false;
                    HTAB      *s_heads = qrf_hash_id(work, "rd sh", sizeof(RdSenseHead), n_senses);
                    int       *s_next = (int *) palloc(sizeof(int) * (Size) n_senses);

                    for (int i = 0; i < cctx.count; i++)
                        objs[i] = cctx.items[i].parts[2];
                    n_uo = qrf_dedup_ids(objs, cctx.count, uobjs);
                    o_arr = laplace_pub_blake3_array(uobjs, n_uo);
                    oa = PointerGetDatum(o_arr);
                    (void) laplace_pub_call_srf("public", "laplace_labels", 1,
                                                &b3_arr_oid, &oa, &on, rd_visit_freq_label, &fctx);

                    for (int si = 0; si < n_senses; si++)
                    {
                        bool         f = false;
                        RdSenseHead *sh = (RdSenseHead *) hash_search(
                            s_heads, &paths[sense_idx[si]].entity, HASH_ENTER, &f);
                        s_next[si] = f ? sh->first : -1;
                        sh->first = si;
                    }

                    for (int ci = 0; ci < cctx.count; ci++)
                    {
                        const RdClaimCell *c = &cctx.items[ci];
                        RdFreqEntry *fe = (RdFreqEntry *) hash_search(freqs, &c->parts[2], HASH_FIND, NULL);
                        RdSenseHead *sh;
                        double       bmu;
                        int64        bw;
                        if (fe == NULL)
                            continue;
                        bmu = (c->rating - 2.0 * c->deviation) + (double) fe->freq;
                        bw = c->matches > fe->freq ? c->matches : fe->freq;
                        sh = (RdSenseHead *) hash_search(s_heads, &c->parts[0], HASH_FIND, NULL);
                        if (sh == NULL)
                            continue;
                        for (int si = sh->first; si >= 0; si = s_next[si])
                        {
                            int64         pid = paths[sense_idx[si]].path_idx;
                            bool          f = false;
                            RdBoostEntry *be = (RdBoostEntry *) hash_search(boost_map, &pid, HASH_ENTER, &f);
                            if (!f)
                            {
                                be->boost_mu = bmu;
                                be->boost_w = bw;
                            }
                            else
                            {
                                if (bmu > be->boost_mu) be->boost_mu = bmu;
                                if (bw > be->boost_w) be->boost_w = bw;
                            }
                        }
                    }
                }
            }

            {
                HASH_SEQ_STATUS seq;
                RdLastPath     *lp;
                hash_seq_init(&seq, last_map);
                while ((lp = (RdLastPath *) hash_seq_search(&seq)) != NULL)
                {
                    const QpJoinedRow *p = &paths[lp->idx];
                    RdBoostEntry      *be = (RdBoostEntry *) hash_search(boost_map, &p->path_idx, HASH_FIND, NULL);
                    double             eff_mu = p->eff_mu;
                    int64              wit = p->witnesses;
                    RdDefRow          *dr;
                    bool               f = false;

                    if (be != NULL)
                    {
                        if (be->boost_mu > eff_mu) eff_mu = be->boost_mu;
                        if (be->boost_w > wit) wit = be->boost_w;
                    }
                    dr = (RdDefRow *) hash_search(best_by_entity, &p->entity, HASH_ENTER, &f);
                    if (!f || eff_mu > dr->eff_mu ||
                        (eff_mu == dr->eff_mu && (p->program_idx < dr->program_index ||
                         (p->program_idx == dr->program_index && p->path_idx < dr->path_index))))
                    {
                        dr->label = p->fact;
                        dr->eff_mu = eff_mu;
                        dr->witnesses = wit;
                        dr->program_index = p->program_idx;
                        dr->path_index = p->path_idx;
                    }
                }
            }
        }
        else
        {
            /* Direct C lex_senses fallback when no path_ends */
            int              n_ls = 0;
            LaplaceSenseRow *ls_rows = laplace_senses_batch_c(word, 1, &n_ls);

            if (ls_rows != NULL && n_ls > 0)
            {
                typedef struct { hash128_t subj; int first; } LsHead;
                typedef struct { double smu; int64 sw; int next; } LsLink;
                HTAB              *ls_heads = qrf_hash_id(work, "rd lsh", sizeof(LsHead), n_ls * 2);
                LsLink            *links = (LsLink *) palloc(sizeof(LsLink) * (Size) (n_ls * 2));
                hash128_t         *raw_s = (hash128_t *) palloc(sizeof(hash128_t) * (Size) (n_ls * 2));
                hash128_t         *uniq_s = (hash128_t *) palloc(sizeof(hash128_t) * (Size) (n_ls * 2));
                int                n_links = 0, n_raw = 0, n_us;
                hash128_t          has_def_id;
                ArrayType         *subjs_arr, *type_arr;
                LaplaceEdgeRawRow *edges;
                int                n_edges = 0;

                for (int i = 0; i < n_ls; i++)
                {
                    hash128_t cands[2] = { ls_rows[i].sense_id, ls_rows[i].synset_id };
                    int       nc = hash128_eq(&cands[0], &cands[1]) ? 1 : 2;
                    for (int c = 0; c < nc; c++)
                    {
                        bool    f = false;
                        LsHead *h = (LsHead *) hash_search(ls_heads, &cands[c], HASH_ENTER, &f);
                        links[n_links].smu = ls_rows[i].eff_mu;
                        links[n_links].sw = ls_rows[i].witnesses;
                        links[n_links].next = f ? h->first : -1;
                        h->first = n_links++;
                        raw_s[n_raw++] = cands[c];
                    }
                }
                pfree(ls_rows);
                n_us = qrf_dedup_ids(raw_s, n_raw, uniq_s);
                subjs_arr = hash128_array_from_ids(uniq_s, n_us);
                if (laplace_relation_type_id("HAS_DEFINITION", &has_def_id) != 0)
                    elog(ERROR, "recall.declared_definitions: HAS_DEFINITION unavailable");
                type_arr = hash128_array_from_ids(&has_def_id, 1);
                edges = laplace_edges_raw_batch_c(subjs_arr, "out", type_arr, true, 16,
                                                  false, "eff_mu", &n_edges);
                pfree(subjs_arr);
                pfree(type_arr);

                if (n_edges > 0)
                {
                    HTAB *picked = qrf_hash_id(work, "rd picked", sizeof(RdLexPick), n_edges);
                    for (int ei = 0; ei < n_edges; ei++)
                    {
                        const LaplaceEdgeRawRow *er = &edges[ei];
                        double                   edge_mu;
                        LsHead                  *h;

                        if (er->neighbour_null)
                            continue;
                        {
                            int64 ef = laplace_effective_mu_fp(er->rating, er->rd);
                            edge_mu = round((double) ef / 1000000.0) / 1000.0;
                        }
                        h = (LsHead *) hash_search(ls_heads, &er->subject_id, HASH_FIND, NULL);
                        if (!h) continue;
                        for (int li = h->first; li >= 0; li = links[li].next)
                        {
                            double     smu = links[li].smu;
                            int64      sw = links[li].sw;
                            double     rs = smu + edge_mu;
                            double     mu = smu > edge_mu ? smu : edge_mu;
                            int64      wit = sw > er->witness_count ? sw : er->witness_count;
                            bool       f = false;
                            RdLexPick *pk;
                            if (wit < 1) wit = 1;
                            pk = (RdLexPick *) hash_search(picked, &er->neighbour_id, HASH_ENTER, &f);
                            if (!f || rs > pk->rank_score)
                            {
                                pk->eff_mu = mu;
                                pk->witnesses = wit;
                                pk->rank_score = rs;
                            }
                        }
                    }

                    {
                        int             npick = hash_get_num_entries(picked);
                        RdLexPick      *plist = (RdLexPick *) palloc(sizeof(RdLexPick) * (Size) Max(npick, 1));
                        HASH_SEQ_STATUS seq;
                        RdLexPick      *pk;
                        int             k = 0;

                        hash_seq_init(&seq, picked);
                        while ((pk = (RdLexPick *) hash_seq_search(&seq)) != NULL)
                            plist[k++] = *pk;
                        if (k > 1)
                            qsort(plist, (Size) k, sizeof(RdLexPick), rd_pick_cmp);

                        if (k > 0)
                        {
                            hash128_t *eids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) k);
                            ArrayType *earr, *txt_arr;
                            Oid        la_types[2] = { BYTEAARRAYOID, BOOLOID };
                            Datum      la_args[2];
                            bool       la_nulls[2] = { false, false };
                            bool       res_null = false;
                            Datum      res_d;
                            Datum     *tvals;
                            bool      *tnulls;
                            int        nt = 0;

                            for (int i = 0; i < k; i++)
                                eids[i] = plist[i].entity_id;
                            earr = hash128_array_from_ids(eids, k);
                            la_args[0] = PointerGetDatum(earr);
                            la_args[1] = BoolGetDatum(true);
                            res_d = laplace_pub_call("public", "laplace_label_array", 2,
                                                     la_types, la_args, la_nulls, &res_null);
                            if (!res_null)
                            {
                                txt_arr = DatumGetArrayTypeP(res_d);
                                deconstruct_array(txt_arr, TEXTOID, -1, false, TYPALIGN_INT,
                                                  &tvals, &tnulls, &nt);
                                for (int i = 0; i < k && i < nt; i++)
                                {
                                    if (tnulls[i])
                                        continue;
                                    {
                                        bool      f = false;
                                        RdDefRow *dr = (RdDefRow *) hash_search(
                                            best_by_entity, &plist[i].entity_id, HASH_ENTER, &f);
                                        if (!f)
                                        {
                                            dr->label = TextDatumGetCString(tvals[i]);
                                            dr->eff_mu = plist[i].eff_mu;
                                            dr->witnesses = plist[i].witnesses;
                                            dr->program_index = 100;
                                            dr->path_index = (int64) (i + 1);
                                        }
                                    }
                                }
                                pfree(tvals);
                                pfree(tnulls);
                            }
                            pfree(earr);
                        }
                    }
                    pfree(edges);
                }
            }
        }

        {
            int             n_defs = hash_get_num_entries(best_by_entity);
            RdDefRow       *drows = (RdDefRow *) palloc(sizeof(RdDefRow) * (Size) Max(n_defs, 1));
            HASH_SEQ_STATUS seq;
            RdDefRow       *dr;

            hash_seq_init(&seq, best_by_entity);
            while ((dr = (RdDefRow *) hash_seq_search(&seq)) != NULL)
                drows[cnt++] = *dr;
            if (cnt > 1)
                qsort(drows, (Size) cnt, sizeof(RdDefRow), rd_def_cmp);

            if (cnt > 0)
            {
                MemoryContextSwitchTo(caller);
                out = (RdDefOutRow *) MemoryContextAlloc(caller, sizeof(RdDefOutRow) * (Size) cnt);
                for (int i = 0; i < cnt; i++)
                {
                    out[i].entity_id = drows[i].entity_id;
                    out[i].label = drows[i].label ? MemoryContextStrdup(caller, drows[i].label) : NULL;
                    out[i].rating_fp = (int64) llround(drows[i].eff_mu * 1e9);
                    out[i].witnesses = drows[i].witnesses;
                }
            }
        }
    }

    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    *out_count = cnt;
    return out;
}

Datum
pg_laplace_recall_declared_definitions(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    hash128_t      word;
    RdDefOutRow   *rows;
    int            cnt = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;
    word = datum_to_hash128(PG_GETARG_DATUM(0));

    rows = laplace_recall_declared_definitions_c(&word, &cnt);
    for (int i = 0; i < cnt; i++)
    {
        Datum vals[4] = {
            hash128_to_datum(&rows[i].entity_id),
            rows[i].label ? CStringGetTextDatum(rows[i].label) : (Datum) 0,
            Int64GetDatum(rows[i].rating_fp),
            Int64GetDatum(rows[i].witnesses)
        };
        bool nulls[4] = { false, (rows[i].label == NULL), false, false };
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
        pfree(DatumGetPointer(vals[0]));
        if (!nulls[1]) pfree(DatumGetPointer(vals[1]));
        if (rows[i].label) pfree(rows[i].label);
    }
    if (rows) pfree(rows);
    return (Datum) 0;
}

/* ========================================================================
 * 3. query.lexicon_relations(bytea[], text[])
 * ======================================================================== */

Datum
pg_laplace_query_lexicon_relations(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    MemoryContext  caller = CurrentMemoryContext;
    MemoryContext  work, prev;
    hash128_t     *ids;
    int            n_ids = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;
    ids = qrf_bytea_ids_nonnull(PG_GETARG_ARRAYTYPE_P(0), true, &n_ids);
    if (n_ids == 0)
    {
        if (ids) pfree(ids);
        return (Datum) 0;
    }

    work = AllocSetContextCreate(caller, "query_lexicon_relations", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    {
        HTAB       *crit_ids = qrf_hash_id(work, "qlr crit", sizeof(hash128_t), 16);
        bool        has_def_crit = false, has_ex_crit = false;
        HTAB       *desc_set = qrf_hash_id(work, "qlr desc", sizeof(hash128_t), n_ids);
        HTAB       *forms = qrf_hash_id(work, "qlr forms", sizeof(QrfFormEntry), n_ids);
        Oid         b3_arr_oid = laplace_pub_blake3_array_oid();
        ArrayType  *ids_b3 = laplace_pub_blake3_array(ids, n_ids);
        Datum       ia = PointerGetDatum(ids_b3);
        bool        in = false;
        QrfDescPredCtx dctx = { work, crit_ids, desc_set };
        QrfPhysCtx     pctx = { work, forms };

        if (!PG_ARGISNULL(1))
        {
            Datum *tv;
            bool  *tn;
            int    nt = 0;
            deconstruct_array(PG_GETARG_ARRAYTYPE_P(1), TEXTOID, -1, false, TYPALIGN_INT, &tv, &tn, &nt);
            for (int i = 0; i < nt; i++)
            {
                char     *s;
                hash128_t tid;
                if (tn[i]) continue;
                s = TextDatumGetCString(tv[i]);
                if (strcmp(s, "definition") == 0) has_def_crit = true;
                if (strcmp(s, "example") == 0) has_ex_crit = true;
                if (laplace_pub_text_id_hash(s, strlen(s), &tid))
                    (void) hash_search(crit_ids, &tid, HASH_ENTER, NULL);
                pfree(s);
            }
            pfree(tv);
            pfree(tn);
        }

        (void) laplace_pub_call_srf("public", "laplace_constituents", 1, &b3_arr_oid,
                                    &ia, &in, qrf_visit_constituent_match, &dctx);
        (void) laplace_pub_call_srf("public", "laplace_physicalities", 1, &b3_arr_oid,
                                    &ia, &in, qrf_visit_physicality, &pctx);
        pfree(ids_b3);

        for (int i = 0; i < n_ids; i++)
        {
            const char   *cname = NULL;
            bool          inv = false;
            bool          has_meta = laplace_relation_metadata_lookup_c(&ids[i], NULL, &cname, &inv, NULL);
            QrfFormEntry *fe = (QrfFormEntry *) hash_search(forms, &ids[i], HASH_FIND, NULL);
            bool          descriptive = (hash_search(desc_set, &ids[i], HASH_FIND, NULL) != NULL) ||
                                        (has_meta && cname != NULL &&
                                         ((strcmp(cname, "HAS_DEFINITION") == 0 && has_def_crit) ||
                                          (strcmp(cname, "HAS_EXAMPLE") == 0 && has_ex_crit)));
            bool          untyped = (fe != NULL && fe->kind_slot == -1);
            Datum         vals[5] = {
                hash128_to_datum(&ids[i]),
                (has_meta && cname) ? CStringGetTextDatum(cname) : (Datum) 0,
                has_meta ? BoolGetDatum(inv) : (Datum) 0,
                BoolGetDatum(descriptive),
                BoolGetDatum(untyped)
            };
            bool nulls[5] = {
                false, !(has_meta && cname), !has_meta, false, false
            };

            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
            pfree(DatumGetPointer(vals[0]));
            if (!nulls[1]) pfree(DatumGetPointer(vals[1]));
        }
    }

    pfree(ids);
    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    return (Datum) 0;
}

/* ========================================================================
 * 4. query.lexicon_descriptions(bytea[])
 * ======================================================================== */

typedef struct
{
    hash128_t anchor;
    hash128_t claim;
    hash128_t parts[3];
    int       n_parts;
    double    standing;
    int64     matches;
} QldClaim;

typedef struct
{
    MemoryContext    caller;
    const hash128_t *roots;
    int              n_roots;
    QldClaim        *items;
    int              count;
    int              cap;
} QldClaimCtx;

static bool
qld_visit_claim(TupleTableSlot *slot, void *opaque)
{
    QldClaimCtx *ctx = (QldClaimCtx *) opaque;
    bool         isnull = false;
    Datum        d_i, d_ent, d_path, d_rat, d_dev, d_mat;
    int64        idx, mat = 0;
    double       rat = 0.0, dev = 0.0;
    lp_path      path;
    lp_id        parts[3];
    MemoryContext prev;

    d_i = slot_getattr(slot, 1, &isnull); if (isnull) return true;
    idx = DatumGetInt64(d_i);
    if (idx < 1 || idx > ctx->n_roots) return true;

    d_path = slot_getattr(slot, 3, &isnull); if (isnull) return true;
    path = laplace_pub_geo_path(d_path);
    if (lp_path_len(path) != 3 || lp_path_expand(path, parts, 3) != 3)
        return true;

    d_ent = slot_getattr(slot, 2, &isnull); if (isnull) return true;
    d_rat = slot_getattr(slot, 4, &isnull); if (!isnull) rat = DatumGetFloat8(d_rat);
    d_dev = slot_getattr(slot, 5, &isnull); if (!isnull) dev = DatumGetFloat8(d_dev);
    d_mat = slot_getattr(slot, 7, &isnull); if (!isnull) mat = DatumGetInt64(d_mat);

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count >= ctx->cap)
    {
        Size sz;
        ctx->cap = ctx->cap ? ctx->cap * 2 : 32;
        sz = sizeof(QldClaim) * (Size) ctx->cap;
        ctx->items = ctx->items ? (QldClaim *) repalloc(ctx->items, sz) : (QldClaim *) palloc(sz);
    }
    ctx->items[ctx->count].anchor = ctx->roots[idx - 1];
    ctx->items[ctx->count].claim = laplace_pub_blake3_to_hash128(d_ent);
    ctx->items[ctx->count].n_parts = 3;
    for (int i = 0; i < 3; i++)
        memcpy(&ctx->items[ctx->count].parts[i], parts[i].b, sizeof(hash128_t));
    ctx->items[ctx->count].standing = rat - 2.0 * dev;
    ctx->items[ctx->count].matches = mat;
    ctx->count++;
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct
{
    hash128_t anchor;
    char     *content;
    bool      has_desc;
    double    standing;
    hash128_t claim;
    bool      is_identifier;
} QldOutRow;

static int
qld_out_cmp(const void *a, const void *b)
{
    const QldOutRow *x = (const QldOutRow *) a;
    const QldOutRow *y = (const QldOutRow *) b;
    int c;
    if (x->has_desc != y->has_desc) return x->has_desc ? -1 : 1;
    if (x->has_desc && x->standing != y->standing) return x->standing > y->standing ? -1 : 1;
    c = memcmp(&x->anchor, &y->anchor, sizeof(hash128_t));
    if (c != 0) return c;
    if (x->has_desc && y->has_desc)
        return memcmp(&x->claim, &y->claim, sizeof(hash128_t));
    return 0;
}

Datum
pg_laplace_query_lexicon_descriptions(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    MemoryContext  caller = CurrentMemoryContext;
    MemoryContext  work, prev;
    hash128_t     *roots;
    int            n_roots = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;
    roots = qrf_bytea_ids_nonnull(PG_GETARG_ARRAYTYPE_P(0), true, &n_roots);
    if (n_roots == 0)
    {
        if (roots) pfree(roots);
        return (Datum) 0;
    }

    work = AllocSetContextCreate(caller, "query_lexicon_descriptions", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    {
        Oid         b3_arr_oid = laplace_pub_blake3_array_oid();
        ArrayType  *r_arr = laplace_pub_blake3_array(roots, n_roots);
        ArrayType  *empty_b3 = laplace_pub_blake3_array(NULL, 0);
        Datum       bit_d = Int16GetDatum(laplace_pub_bank_bit("kind", "claim"));
        ArrayType  *bits_arr = construct_array(&bit_d, 1, INT2OID, sizeof(int16), true, TYPALIGN_SHORT);
        Oid         c_types[5] = { b3_arr_oid, INT8OID, INT2ARRAYOID, b3_arr_oid, INT2OID };
        Datum       c_args[5] = { PointerGetDatum(r_arr), (Datum) 0, PointerGetDatum(bits_arr),
                                  PointerGetDatum(empty_b3), Int16GetDatum(1) };
        bool        c_nulls[5] = { false, true, false, false, false };
        QldClaimCtx cctx;
        hash128_t   id_vocab_id;
        HTAB       *identifiers = qrf_hash_id(work, "qld idents", sizeof(hash128_t), n_roots);
        HTAB       *pred_crit = qrf_hash_id(work, "qld pcrit", sizeof(hash128_t), 4);
        HTAB       *predicates = qrf_hash_id(work, "qld preds", sizeof(hash128_t), 32);

        (void) laplace_pub_text_id_hash("identifier vocabulary", 21, &id_vocab_id);
        {
            const char *cnames[2] = { "definition", "definitions" };
            for (int k = 0; k < 2; k++)
            {
                hash128_t cid;
                if (laplace_pub_text_id_hash(cnames[k], strlen(cnames[k]), &cid))
                    (void) hash_search(pred_crit, &cid, HASH_ENTER, NULL);
            }
        }

        MemSet(&cctx, 0, sizeof(cctx));
        cctx.caller = work;
        cctx.roots = roots;
        cctx.n_roots = n_roots;
        (void) laplace_pub_call_srf("public", "laplace_claims_each", 5,
                                    c_types, c_args, c_nulls, qld_visit_claim, &cctx);
        pfree(r_arr);
        pfree(empty_b3);
        pfree(bits_arr);

        if (cctx.count > 0)
        {
            hash128_t *preds = (hash128_t *) palloc(sizeof(hash128_t) * (Size) cctx.count);
            hash128_t *upreds = (hash128_t *) palloc(sizeof(hash128_t) * (Size) cctx.count);
            int        np = 0, nup;

            for (int i = 0; i < cctx.count; i++)
            {
                if (hash128_eq(&cctx.items[i].parts[1], &id_vocab_id))
                    (void) hash_search(identifiers, &cctx.items[i].anchor, HASH_ENTER, NULL);
                preds[np++] = cctx.items[i].parts[1];
            }
            nup = qrf_dedup_ids(preds, np, upreds);

            for (int i = 0; i < nup; i++)
            {
                const char *cname = NULL;
                if (laplace_relation_metadata_lookup_c(&upreds[i], NULL, &cname, NULL, NULL) &&
                    cname != NULL && strcmp(cname, "HAS_DEFINITION") == 0)
                    (void) hash_search(predicates, &upreds[i], HASH_ENTER, NULL);
            }

            {
                ArrayType     *pa = laplace_pub_blake3_array(upreds, nup);
                Datum          parg = PointerGetDatum(pa);
                bool           pn = false;
                QrfDescPredCtx dctx = { work, pred_crit, predicates };
                (void) laplace_pub_call_srf("public", "laplace_constituents", 1,
                                            &b3_arr_oid, &parg, &pn, qrf_visit_constituent_match, &dctx);
                pfree(pa);
            }

            {
                hash128_t *val_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) cctx.count);
                int        nv = 0;
                int       *sel_idx = (int *) palloc(sizeof(int) * (Size) cctx.count);
                int        nsel = 0;

                for (int i = 0; i < cctx.count; i++)
                {
                    const QldClaim *c = &cctx.items[i];
                    if (c->matches > 0 && hash128_eq(&c->parts[0], &c->anchor) &&
                        hash_search(predicates, &c->parts[1], HASH_FIND, NULL) != NULL)
                    {
                        sel_idx[nsel++] = i;
                        val_ids[nv++] = c->parts[2];
                    }
                }

                {
                    HTAB *text_labels = qrf_hash_id(work, "qld tlbl", sizeof(QrfLabelEntry), Max(nv, 16));
                    if (nv > 0)
                    {
                        int                     nt = 0;
                        ConverseLexiconTextRow *lt = converse_lexicon_texts_c(val_ids, nv, &nt);
                        for (int i = 0; i < nt; i++)
                        {
                            if (lt[i].textual)
                            {
                                bool           f = false;
                                QrfLabelEntry *le = (QrfLabelEntry *) hash_search(
                                    text_labels, &lt[i].entity, HASH_ENTER, &f);
                                if (!f)
                                {
                                    le->content_null = lt[i].content_null;
                                    le->content = lt[i].content ? pstrdup(lt[i].content) : NULL;
                                }
                            }
                        }
                    }

                    {
                        HTAB      *desc_anchors = qrf_hash_id(work, "qld da", sizeof(hash128_t), nsel);
                        QldOutRow *out = (QldOutRow *) palloc(sizeof(QldOutRow) * (Size) (nsel + n_roots));
                        int        n_out = 0;
                        HASH_SEQ_STATUS seq;
                        hash128_t *ip;

                        for (int si = 0; si < nsel; si++)
                        {
                            const QldClaim *c = &cctx.items[sel_idx[si]];
                            QrfLabelEntry  *le = (QrfLabelEntry *) hash_search(
                                text_labels, &c->parts[2], HASH_FIND, NULL);
                            if (le == NULL)
                                continue;
                            (void) hash_search(desc_anchors, &c->anchor, HASH_ENTER, NULL);
                            out[n_out].anchor = c->anchor;
                            out[n_out].content = le->content_null ? NULL : le->content;
                            out[n_out].has_desc = true;
                            out[n_out].standing = c->standing;
                            out[n_out].claim = c->claim;
                            out[n_out].is_identifier = (hash_search(identifiers, &c->anchor, HASH_FIND, NULL) != NULL);
                            n_out++;
                        }

                        hash_seq_init(&seq, identifiers);
                        while ((ip = (hash128_t *) hash_seq_search(&seq)) != NULL)
                        {
                            if (hash_search(desc_anchors, ip, HASH_FIND, NULL) != NULL)
                                continue;
                            out[n_out].anchor = *ip;
                            out[n_out].content = NULL;
                            out[n_out].has_desc = false;
                            out[n_out].standing = 0.0;
                            MemSet(&out[n_out].claim, 0, sizeof(hash128_t));
                            out[n_out].is_identifier = true;
                            n_out++;
                        }

                        if (n_out > 1)
                            qsort(out, (Size) n_out, sizeof(QldOutRow), qld_out_cmp);

                        for (int i = 0; i < n_out; i++)
                        {
                            Datum vals[3] = {
                                hash128_to_datum(&out[i].anchor),
                                out[i].content ? CStringGetTextDatum(out[i].content) : (Datum) 0,
                                BoolGetDatum(out[i].is_identifier)
                            };
                            bool nulls[3] = { false, (out[i].content == NULL), false };
                            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
                            pfree(DatumGetPointer(vals[0]));
                            if (!nulls[1]) pfree(DatumGetPointer(vals[1]));
                        }
                    }
                }
            }
        }
    }

    pfree(roots);
    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    return (Datum) 0;
}

/* ========================================================================
 * 5 & 6. query.lexicon_field & query.response_field (24-column result)
 * ======================================================================== */

typedef struct
{
    int   *items;
    int    count;
    int    cap;
} OrdList;

typedef struct
{
    hash128_t id;
    OrdList   ords;
} LexOriginEntry;

typedef struct
{
    hash128_t claim;
} ClaimIdEntry;

typedef struct
{
    hash128_t anchor;
    OrdList   claims;
} AnchorBindClaims;

static void
ord_push_sorted_uniq(OrdList *ol, int v)
{
    for (int i = 0; i < ol->count; i++)
    {
        if (ol->items[i] == v) return;
    }
    if (ol->count >= ol->cap)
    {
        Size sz;
        ol->cap = ol->cap ? ol->cap * 2 : 8;
        sz = sizeof(int) * (Size) ol->cap;
        ol->items = ol->items ? (int *) repalloc(ol->items, sz) : (int *) palloc(sz);
    }
    ol->items[ol->count++] = v;
    for (int i = ol->count - 1; i > 0 && ol->items[i - 1] > ol->items[i]; i--)
    {
        int t = ol->items[i - 1];
        ol->items[i - 1] = ol->items[i];
        ol->items[i] = t;
    }
}

static void
claim_push_uniq(AnchorBindClaims *abc, hash128_t cl)
{
    /* Small per-anchor list; avoid allocating separate hash per anchor */
    if (!abc->claims.items)
        abc->claims.cap = 0;
    (void) cl;
}

static ArrayType *
int32_array_from_ordlist(const OrdList *ol)
{
    if (ol == NULL || ol->count == 0)
        return construct_empty_array(INT4OID);
    {
        Datum     *d = (Datum *) palloc(sizeof(Datum) * (Size) ol->count);
        ArrayType *res;
        for (int i = 0; i < ol->count; i++)
            d[i] = Int32GetDatum((int32) ol->items[i]);
        res = construct_array(d, ol->count, INT4OID, sizeof(int32), true, TYPALIGN_INT);
        pfree(d);
        return res;
    }
}

typedef struct
{
    hash128_t *ids;
    int        count;
    int        cap;
} ClaimList;

typedef struct
{
    hash128_t anchor;
    ClaimList cl;
} AnchorClaimEntry;

static void
anchor_claim_add(HTAB *map, const hash128_t *anchor, const hash128_t *claim)
{
    bool              f = false;
    AnchorClaimEntry *e = (AnchorClaimEntry *) hash_search(map, anchor, HASH_ENTER, &f);
    if (!f)
    {
        e->cl.ids = NULL;
        e->cl.count = 0;
        e->cl.cap = 0;
    }
    for (int i = 0; i < e->cl.count; i++)
    {
        if (hash128_eq(&e->cl.ids[i], claim))
            return;
    }
    if (e->cl.count >= e->cl.cap)
    {
        Size sz;
        e->cl.cap = e->cl.cap ? e->cl.cap * 2 : 8;
        sz = sizeof(hash128_t) * (Size) e->cl.cap;
        e->cl.ids = e->cl.ids ? (hash128_t *) repalloc(e->cl.ids, sz) : (hash128_t *) palloc(sz);
    }
    e->cl.ids[e->cl.count++] = *claim;
}

static void
build_lexical_origin_maps(MemoryContext work, const hash128_t *roots, int n_roots,
                          const ConverseBindingRow *brows, int n_brows,
                          HTAB **out_origins, HTAB **out_bind_claims, HTAB **out_term_ords)
{
    HTAB *origins = qrf_hash_id(work, "lf origins", sizeof(LexOriginEntry), n_roots + n_brows * 2);
    HTAB *bclaims = qrf_hash_id(work, "lf bclaims", sizeof(AnchorClaimEntry), n_roots + n_brows * 2);
    HTAB *term_ords = qrf_hash_id(work, "lf tords", sizeof(LexOriginEntry), n_roots);

    (void) claim_push_uniq;

    for (int i = 0; i < n_roots; i++)
    {
        bool            f = false;
        LexOriginEntry *e = (LexOriginEntry *) hash_search(origins, &roots[i], HASH_ENTER, &f);
        LexOriginEntry *te = (LexOriginEntry *) hash_search(term_ords, &roots[i], HASH_ENTER, &f);
        if (!f)
        {
            MemSet(&e->ords, 0, sizeof(OrdList));
            MemSet(&te->ords, 0, sizeof(OrdList));
        }
        else
        {
            /* origins may already exist from earlier; ensure zeroed per-table */
        }
        ord_push_sorted_uniq(&e->ords, i + 1);
        ord_push_sorted_uniq(&te->ords, i + 1);
    }

    /* Reset cleanly: track term_ords separately from origins */
    hash_destroy(origins);
    hash_destroy(term_ords);
    origins = qrf_hash_id(work, "lf origins2", sizeof(LexOriginEntry), n_roots + n_brows * 2);
    term_ords = qrf_hash_id(work, "lf tords2", sizeof(LexOriginEntry), n_roots);

    for (int i = 0; i < n_roots; i++)
    {
        bool            f = false;
        LexOriginEntry *e = (LexOriginEntry *) hash_search(origins, &roots[i], HASH_ENTER, &f);
        if (!f) MemSet(&e->ords, 0, sizeof(OrdList));
        ord_push_sorted_uniq(&e->ords, i + 1);

        e = (LexOriginEntry *) hash_search(term_ords, &roots[i], HASH_ENTER, &f);
        if (!f) MemSet(&e->ords, 0, sizeof(OrdList));
        ord_push_sorted_uniq(&e->ords, i + 1);
    }

    for (int bi = 0; bi < n_brows; bi++)
    {
        LexOriginEntry *te = (LexOriginEntry *) hash_search(term_ords, &brows[bi].term, HASH_FIND, NULL);
        hash128_t       targets[2] = { brows[bi].key, brows[bi].binding };

        anchor_claim_add(bclaims, &brows[bi].key, &brows[bi].claim);
        anchor_claim_add(bclaims, &brows[bi].binding, &brows[bi].claim);
        if (te == NULL)
            continue;

        for (int t = 0; t < 2; t++)
        {
            bool            f = false;
            LexOriginEntry *oe = (LexOriginEntry *) hash_search(origins, &targets[t], HASH_ENTER, &f);
            if (!f) MemSet(&oe->ords, 0, sizeof(OrdList));
            for (int oi = 0; oi < te->ords.count; oi++)
                ord_push_sorted_uniq(&oe->ords, te->ords.items[oi]);
        }
    }

    *out_origins = origins;
    *out_bind_claims = bclaims;
    if (out_term_ords)
        *out_term_ords = term_ords;
}

/* ---- query.lexicon_field(bytea[]) ---- */

typedef struct
{
    hash128_t anchor;
    hash128_t via;
    hash128_t entity;
    hash128_t rel;
    bool      rel_null;
    bool      outbound;
    int64     matches;
    bool      matches_null;
    double    rating;
    bool      rating_null;
    double    rd;
    bool      rd_null;
    double    volatility;
    bool      vol_null;
} LfCand;

typedef struct
{
    MemoryContext    caller;
    const hash128_t *anchors;
    int              n_anchors;
    LfCand          *items;
    int              count;
    int              cap;
} LfClaimCtx;

static bool
lf_visit_claim(TupleTableSlot *slot, void *opaque)
{
    LfClaimCtx *ctx = (LfClaimCtx *) opaque;
    bool        isnull = false;
    bool        rat_null = false, dev_null = false, vol_null = false, mat_null = false;
    Datum       d_i, d_ent, d_path, d_mat, d_rat, d_dev, d_vol;
    int64       idx, mat = 0;
    double      rat = 0.0, dev = 0.0, vol = 0.0;
    hash128_t   anch;
    lp_path     path;
    uint32      len;
    lp_id       stack_parts[64];
    lp_id      *parts = stack_parts;
    hash128_t   p0, p_last, ent, rel;
    MemoryContext prev;

    d_i = slot_getattr(slot, 1, &isnull); if (isnull) return true;
    idx = DatumGetInt64(d_i);
    if (idx < 1 || idx > ctx->n_anchors) return true;
    anch = ctx->anchors[idx - 1];

    d_path = slot_getattr(slot, 3, &isnull); if (isnull) return true;
    path = laplace_pub_geo_path(d_path);
    len = lp_path_len(path);
    if (len < 2) return true;
    if (len > 64) parts = (lp_id *) palloc(sizeof(lp_id) * (Size) len);
    if (lp_path_expand(path, parts, (int) len) != len)
    {
        if (parts != stack_parts) pfree(parts);
        return true;
    }

    memcpy(&p0, parts[0].b, sizeof(hash128_t));
    memcpy(&p_last, parts[len - 1].b, sizeof(hash128_t));
    if (len >= 3)
        memcpy(&rel, parts[1].b, sizeof(hash128_t));
    else
        MemSet(&rel, 0, sizeof(hash128_t));
    if (parts != stack_parts) pfree(parts);

    d_ent = slot_getattr(slot, 2, &isnull); if (isnull) return true;
    d_rat = slot_getattr(slot, 4, &rat_null); if (!rat_null) rat = DatumGetFloat8(d_rat);
    d_dev = slot_getattr(slot, 5, &dev_null); if (!dev_null) dev = DatumGetFloat8(d_dev);
    d_vol = slot_getattr(slot, 6, &vol_null); if (!vol_null) vol = DatumGetFloat8(d_vol);
    d_mat = slot_getattr(slot, 7, &mat_null); if (!mat_null) mat = DatumGetInt64(d_mat);

    ent = laplace_pub_blake3_to_hash128(d_ent);

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count >= ctx->cap)
    {
        Size sz;
        ctx->cap = ctx->cap ? ctx->cap * 2 : 64;
        sz = sizeof(LfCand) * (Size) ctx->cap;
        ctx->items = ctx->items ? (LfCand *) repalloc(ctx->items, sz) : (LfCand *) palloc(sz);
    }
    ctx->items[ctx->count].anchor = anch;
    ctx->items[ctx->count].via = ent;
    ctx->items[ctx->count].outbound = hash128_eq(&p0, &anch);
    ctx->items[ctx->count].entity = ctx->items[ctx->count].outbound ? p_last : p0;
    ctx->items[ctx->count].rel_null = (len < 3);
    ctx->items[ctx->count].rel = rel;
    ctx->items[ctx->count].matches = mat;
    ctx->items[ctx->count].matches_null = mat_null;
    ctx->items[ctx->count].rating = rat;
    ctx->items[ctx->count].rating_null = rat_null;
    ctx->items[ctx->count].rd = dev;
    ctx->items[ctx->count].rd_null = dev_null;
    ctx->items[ctx->count].volatility = vol;
    ctx->items[ctx->count].vol_null = vol_null;
    ctx->count++;
    MemoryContextSwitchTo(prev);
    return true;
}

static int
lf_cand_cmp(const void *a, const void *b)
{
    const LfCand *x = (const LfCand *) a;
    const LfCand *y = (const LfCand *) b;
    int c = memcmp(&x->anchor, &y->anchor, sizeof(hash128_t));
    if (c != 0) return c;
    c = memcmp(&x->entity, &y->entity, sizeof(hash128_t));
    if (c != 0) return c;
    return memcmp(&x->via, &y->via, sizeof(hash128_t));
}

Datum
pg_laplace_query_lexicon_field(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    MemoryContext  caller = CurrentMemoryContext;
    MemoryContext  work, prev;
    hash128_t     *roots;
    int            n_roots = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;
    roots = qrf_bytea_ids_nonnull(PG_GETARG_ARRAYTYPE_P(0), false, &n_roots);
    if (n_roots == 0)
    {
        if (roots) pfree(roots);
        return (Datum) 0;
    }

    work = AllocSetContextCreate(caller, "query_lexicon_field", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    {
        ConverseBindingRow *brows;
        int                 n_brows = 0;
        HTAB               *origins = NULL, *bclaims = NULL;
        hash128_t          *anchors;
        int                 n_anchors = 0;
        HASH_SEQ_STATUS     seq;
        LexOriginEntry     *oe;
        Oid                 b3_arr_oid = laplace_pub_blake3_array_oid();

        brows = converse_binding_evidence_c(roots, n_roots, NULL, false, &n_brows);
        build_lexical_origin_maps(work, roots, n_roots, brows, n_brows,
                                  &origins, &bclaims, NULL);

        anchors = (hash128_t *) palloc(sizeof(hash128_t) * (Size) Max(hash_get_num_entries(origins), 1));
        hash_seq_init(&seq, origins);
        while ((oe = (LexOriginEntry *) hash_seq_search(&seq)) != NULL)
            anchors[n_anchors++] = oe->id;
        if (n_anchors > 1)
            qsort(anchors, (Size) n_anchors, sizeof(hash128_t), qrf_hash128_cmp);

        if (n_anchors > 0)
        {
            ArrayType *anch_arr = laplace_pub_blake3_array(anchors, n_anchors);
            ArrayType *empty_b3 = laplace_pub_blake3_array(NULL, 0);
            Datum      bit_d = Int16GetDatum(laplace_pub_bank_bit("kind", "claim"));
            ArrayType *bits_arr = construct_array(&bit_d, 1, INT2OID, sizeof(int16), true, TYPALIGN_SHORT);
            Oid        c_types[5] = { b3_arr_oid, INT8OID, INT2ARRAYOID, b3_arr_oid, INT2OID };
            Datum      c_args[5] = { PointerGetDatum(anch_arr), (Datum) 0, PointerGetDatum(bits_arr),
                                     PointerGetDatum(empty_b3), Int16GetDatum(3) };
            bool       c_nulls[5] = { false, true, false, false, false };
            LfClaimCtx cctx;

            MemSet(&cctx, 0, sizeof(cctx));
            cctx.caller = work;
            cctx.anchors = anchors;
            cctx.n_anchors = n_anchors;
            (void) laplace_pub_call_srf("public", "laplace_claims_each", 5,
                                        c_types, c_args, c_nulls, lf_visit_claim, &cctx);
            pfree(anch_arr);
            pfree(empty_b3);
            pfree(bits_arr);

            if (cctx.count > 0)
            {
                hash128_t *rels = (hash128_t *) palloc(sizeof(hash128_t) * (Size) cctx.count);
                hash128_t *urels = (hash128_t *) palloc(sizeof(hash128_t) * (Size) cctx.count);
                int        nr = 0, nur;
                hash128_t *fids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) (cctx.count * 2));
                hash128_t *ufids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) (cctx.count * 2));
                int        nf = 0, nuf;
                HTAB      *desc_crit = qrf_hash_id(work, "lf dcrit", sizeof(hash128_t), 4);
                HTAB      *desc_rels = qrf_hash_id(work, "lf drels", sizeof(hash128_t), 32);
                HTAB      *forms = qrf_hash_id(work, "lf forms", sizeof(QrfFormEntry), cctx.count * 2);
                hash128_t  id_vocab_id;
                const char *cnames[4] = { "definition", "definitions", "example", "examples" };
                LfCand    *kept = (LfCand *) palloc(sizeof(LfCand) * (Size) cctx.count);
                int        n_kept = 0;

                (void) laplace_pub_text_id_hash("identifier vocabulary", 21, &id_vocab_id);
                for (int i = 0; i < 4; i++)
                {
                    hash128_t cid;
                    if (laplace_pub_text_id_hash(cnames[i], strlen(cnames[i]), &cid))
                        (void) hash_search(desc_crit, &cid, HASH_ENTER, NULL);
                }

                for (int i = 0; i < cctx.count; i++)
                {
                    fids[nf++] = cctx.items[i].entity;
                    if (!cctx.items[i].rel_null)
                    {
                        rels[nr++] = cctx.items[i].rel;
                        fids[nf++] = cctx.items[i].rel;
                    }
                }
                nur = nr > 0 ? qrf_dedup_ids(rels, nr, urels) : 0;
                nuf = nf > 0 ? qrf_dedup_ids(fids, nf, ufids) : 0;

                if (nur > 0)
                {
                    for (int i = 0; i < nur; i++)
                    {
                        const char *cname = NULL;
                        if (laplace_relation_metadata_lookup_c(&urels[i], NULL, &cname, NULL, NULL) &&
                            cname != NULL &&
                            (strcmp(cname, "HAS_DEFINITION") == 0 || strcmp(cname, "HAS_EXAMPLE") == 0))
                            (void) hash_search(desc_rels, &urels[i], HASH_ENTER, NULL);
                    }
                    {
                        ArrayType     *ra = laplace_pub_blake3_array(urels, nur);
                        Datum          rarg = PointerGetDatum(ra);
                        bool           rn = false;
                        QrfDescPredCtx dctx = { work, desc_crit, desc_rels };
                        (void) laplace_pub_call_srf("public", "laplace_constituents", 1,
                                                    &b3_arr_oid, &rarg, &rn, qrf_visit_constituent_match, &dctx);
                        pfree(ra);
                    }
                }

                if (nuf > 0)
                {
                    ArrayType *fa = laplace_pub_blake3_array(ufids, nuf);
                    Datum      farg = PointerGetDatum(fa);
                    bool       fn = false;
                    QrfPhysCtx pctx = { work, forms };
                    (void) laplace_pub_call_srf("public", "laplace_physicalities", 1,
                                                &b3_arr_oid, &farg, &fn, qrf_visit_physicality, &pctx);
                    pfree(fa);
                }

                for (int i = 0; i < cctx.count; i++)
                {
                    const LfCand   *c = &cctx.items[i];
                    LexOriginEntry *ab = (LexOriginEntry *) hash_search(origins, &c->anchor, HASH_FIND, NULL);
                    LexOriginEntry *tb = (LexOriginEntry *) hash_search(origins, &c->entity, HASH_FIND, NULL);
                    QrfFormEntry   *ef = (QrfFormEntry *) hash_search(forms, &c->entity, HASH_FIND, NULL);
                    QrfFormEntry   *rf = !c->rel_null ? (QrfFormEntry *) hash_search(forms, &c->rel, HASH_FIND, NULL) : NULL;
                    bool            is_desc = !c->rel_null && (hash_search(desc_rels, &c->rel, HASH_FIND, NULL) != NULL);
                    const char     *cname = NULL;
                    bool            has_meta = !c->rel_null &&
                                               laplace_relation_metadata_lookup_c(&c->rel, NULL, &cname, NULL, NULL) &&
                                               cname != NULL;
                    bool            diff_side = false;
                    bool            single_root_ok;

                    if (ab == NULL)
                        continue;
                    if (!c->rel_null && hash128_eq(&c->rel, &id_vocab_id))
                        continue;

                    if (c->outbound && is_desc)
                    {
                        kept[n_kept++] = *c;
                        continue;
                    }

                    if (!(has_meta || (rf != NULL && rf->kind_slot == -1)))
                        continue;

                    if (tb != NULL)
                    {
                        for (int ai = 0; ai < ab->ords.count && !diff_side; ai++)
                            for (int bi = 0; bi < tb->ords.count; bi++)
                                if (ab->ords.items[ai] != tb->ords.items[bi])
                                { diff_side = true; break; }
                    }
                    single_root_ok = (n_roots == 1 && tb == NULL && ef != NULL &&
                                      (ef->kind_slot == -1 || ef->is_tuple));
                    if (diff_side || single_root_ok)
                        kept[n_kept++] = *c;
                }

                if (n_kept > 0)
                {
                    hash128_t *lids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) (n_kept * 3));
                    hash128_t *ulids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) (n_kept * 3));
                    int        nl = 0, nul, nt = 0;
                    HTAB      *lmap = qrf_hash_id(work, "lf lmap", sizeof(QrfLabelEntry), n_kept * 2);
                    typedef struct { hash128_t id; bool textual; } LfTextual;
                    HTAB      *tmap = qrf_hash_id(work, "lf tmap", sizeof(LfTextual), n_kept * 2);
                    ConverseLexiconTextRow *lt;

                    for (int i = 0; i < n_kept; i++)
                    {
                        lids[nl++] = kept[i].entity;
                        lids[nl++] = kept[i].anchor;
                        if (!kept[i].rel_null)
                            lids[nl++] = kept[i].rel;
                    }
                    nul = qrf_dedup_ids(lids, nl, ulids);
                    lt = converse_lexicon_texts_c(ulids, nul, &nt);
                    for (int i = 0; i < nt; i++)
                    {
                        bool f = false;
                        QrfLabelEntry *le = (QrfLabelEntry *) hash_search(lmap, &lt[i].entity, HASH_ENTER, &f);
                        if (!f)
                        {
                            le->content_null = lt[i].content_null;
                            le->content = lt[i].content ? pstrdup(lt[i].content) : NULL;
                        }
                        LfTextual *te = (LfTextual *) hash_search(tmap, &lt[i].entity, HASH_ENTER, &f);
                        te->textual = lt[i].textual;
                    }

                    if (n_kept > 1)
                        qsort(kept, (Size) n_kept, sizeof(LfCand), lf_cand_cmp);

                    for (int i = 0; i < n_kept; i++)
                    {
                        const LfCand     *r = &kept[i];
                        LexOriginEntry   *ab = (LexOriginEntry *) hash_search(origins, &r->anchor, HASH_FIND, NULL);
                        LexOriginEntry   *tb = (LexOriginEntry *) hash_search(origins, &r->entity, HASH_FIND, NULL);
                        QrfFormEntry     *ef = (QrfFormEntry *) hash_search(forms, &r->entity, HASH_FIND, NULL);
                        QrfLabelEntry    *al = (QrfLabelEntry *) hash_search(lmap, &r->anchor, HASH_FIND, NULL);
                        QrfLabelEntry    *el = (QrfLabelEntry *) hash_search(lmap, &r->entity, HASH_FIND, NULL);
                        QrfLabelEntry    *rl = r->rel_null ? NULL : (QrfLabelEntry *) hash_search(lmap, &r->rel, HASH_FIND, NULL);
                        LfTextual        *elt = (LfTextual *) hash_search(tmap, &r->entity, HASH_FIND, NULL);
                        AnchorClaimEntry *bc = (AnchorClaimEntry *) hash_search(bclaims, &r->anchor, HASH_FIND, NULL);
                        ArrayType        *orig_arr = int32_array_from_ordlist(&ab->ords);
                        ArrayType        *targ_arr = tb ? int32_array_from_ordlist(&tb->ords)
                                                        : construct_empty_array(INT4OID);
                        ArrayType        *bc_arr = bc ? hash128_array_from_ids(bc->cl.ids, bc->cl.count)
                                                      : construct_empty_array(BYTEAOID);
                        Datum             vals[24];
                        bool              nulls[24];

                        MemSet(nulls, 0, sizeof(nulls));
                        vals[0]  = Int32GetDatum(2);
                        vals[1]  = Int32GetDatum((int32) ab->ords.items[0]);
                        vals[2]  = hash128_to_datum(&r->anchor);
                        vals[3]  = (al && !al->content_null && al->content) ? CStringGetTextDatum(al->content) : (Datum) 0;
                        nulls[3] = (vals[3] == (Datum) 0);
                        vals[4]  = hash128_to_datum(&r->entity);
                        if (el && !el->content_null && el->content)
                            vals[5] = CStringGetTextDatum(el->content);
                        else
                        {
                            char *hx = qrf_id_text_alloc(&r->entity);
                            vals[5] = CStringGetTextDatum(hx);
                            pfree(hx);
                        }
                        vals[6]  = Int16GetDatum(0);
                        vals[7]  = hash128_to_datum(&r->via);
                        if (r->rel_null)
                        {
                            nulls[8] = true; vals[8] = (Datum) 0;
                            nulls[9] = true; vals[9] = (Datum) 0;
                        }
                        else
                        {
                            vals[8] = hash128_to_datum(&r->rel);
                            vals[9] = (rl && !rl->content_null && rl->content) ? CStringGetTextDatum(rl->content) : (Datum) 0;
                            nulls[9] = (vals[9] == (Datum) 0);
                        }
                        if (r->rating_null) { nulls[10] = true; vals[10] = (Datum) 0; }
                        else vals[10] = Float8GetDatum(r->rating);
                        if (r->rd_null) { nulls[11] = true; vals[11] = (Datum) 0; }
                        else vals[11] = Float8GetDatum(r->rd);
                        if (r->vol_null) { nulls[12] = true; vals[12] = (Datum) 0; }
                        else vals[12] = Float8GetDatum(r->volatility);
                        if (r->matches_null) { nulls[13] = true; vals[13] = (Datum) 0; }
                        else vals[13] = Int64GetDatum(r->matches);
                        nulls[14] = true; vals[14] = (Datum) 0;
                        vals[15] = Int16GetDatum(ef != NULL ? ef->tier : 0);
                        nulls[16] = true; vals[16] = (Datum) 0;
                        vals[17] = BoolGetDatum(r->outbound);
                        vals[18] = PointerGetDatum(orig_arr);
                        vals[19] = PointerGetDatum(targ_arr);
                        vals[20] = PointerGetDatum(bc_arr);
                        vals[21] = PointerGetDatum(orig_arr);
                        vals[22] = BoolGetDatum(elt ? elt->textual : false);
                        vals[23] = PointerGetDatum(orig_arr);

                        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
                        pfree(DatumGetPointer(vals[2]));
                        pfree(DatumGetPointer(vals[4]));
                        pfree(DatumGetPointer(vals[7]));
                        if (!nulls[8]) pfree(DatumGetPointer(vals[8]));
                    }
                }
            }
        }
    }

    pfree(roots);
    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    return (Datum) 0;
}

/* ---- query.response_field(bytea[], int4, int4) ---- */

typedef struct
{
    int32     round;
    int32     occ;
    hash128_t anchor;
    hash128_t entity;
    int16     route;
    hash128_t via;
    bool      via_null;
    hash128_t rel;
    bool      rel_null;
    int16     tier;
    double    distance;
    bool      distance_null;
    double    rating;
    double    rd;
    double    volatility;
    int64     matches;
    bool      has_claim;
    bool      outbound;
} RfRespRow;

typedef struct
{
    MemoryContext    caller;
    int32            round;
    const hash128_t *anchors;
    int              n_anchors;
    RfRespRow       *items;
    int              count;
    int              cap;
} RfCoupleCtx;

static bool
rf_visit_couple(TupleTableSlot *slot, void *opaque)
{
    RfCoupleCtx *ctx = (RfCoupleCtx *) opaque;
    bool         isnull = false;
    Datum        d_ent, d_occ, d_rte, d_via, d_rel, d_tier, d_dist, d_rat, d_dev, d_vol;
    int32        occ;
    double       rat = 0.0, dev = 0.0, vol = 0.0;
    MemoryContext prev;

    d_ent = slot_getattr(slot, 1, &isnull); if (isnull) return true;
    d_occ = slot_getattr(slot, 2, &isnull); if (isnull) return true;
    occ = DatumGetInt32(d_occ);
    if (occ < 1 || occ > ctx->n_anchors) return true;

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count >= ctx->cap)
    {
        Size sz;
        ctx->cap = ctx->cap ? ctx->cap * 2 : 64;
        sz = sizeof(RfRespRow) * (Size) ctx->cap;
        ctx->items = ctx->items ? (RfRespRow *) repalloc(ctx->items, sz) : (RfRespRow *) palloc(sz);
    }
    {
        RfRespRow *r = &ctx->items[ctx->count++];
        MemSet(r, 0, sizeof(*r));
        r->round = ctx->round;
        r->occ = occ;
        r->anchor = ctx->anchors[occ - 1];
        r->entity = laplace_pub_blake3_to_hash128(d_ent);

        d_rte = slot_getattr(slot, 3, &isnull);
        r->route = isnull ? 0 : DatumGetInt16(d_rte);

        d_rat = slot_getattr(slot, 4, &isnull); if (!isnull) rat = DatumGetFloat8(d_rat);
        d_dev = slot_getattr(slot, 5, &isnull); if (!isnull) dev = DatumGetFloat8(d_dev);
        d_vol = slot_getattr(slot, 6, &isnull); if (!isnull) vol = DatumGetFloat8(d_vol);
        r->rating = rat;
        r->rd = dev;
        r->volatility = vol;

        d_via = slot_getattr(slot, 7, &r->via_null);
        if (!r->via_null) r->via = laplace_pub_blake3_to_hash128(d_via);
        d_rel = slot_getattr(slot, 8, &r->rel_null);
        if (!r->rel_null) r->rel = laplace_pub_blake3_to_hash128(d_rel);
        d_tier = slot_getattr(slot, 9, &isnull);
        r->tier = isnull ? 0 : DatumGetInt16(d_tier);
        d_dist = slot_getattr(slot, 10, &r->distance_null);
        r->distance = r->distance_null ? 0.0 : DatumGetFloat8(d_dist);
        r->matches = 0;
        r->has_claim = false;
    }
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct
{
    hash128_t anchor;
    hash128_t via;
    int       n_parts;
    hash128_t parts[64];
    int64     matches;
    bool      matches_null;
    double    rating;
    bool      rating_null;
    double    rd;
    bool      rd_null;
    double    volatility;
    bool      vol_null;
} RfClaimCell;

typedef struct
{
    hash128_t anchor;
    int       first;
} RfClaimHead;

typedef struct
{
    MemoryContext    caller;
    const hash128_t *anchors;
    int              n_anchors;
    HTAB            *heads;
    RfClaimCell     *cells;
    int             *next;
    int              count;
    int              cap;
} RfClaimCtx;

static bool
rf_visit_claim(TupleTableSlot *slot, void *opaque)
{
    RfClaimCtx *ctx = (RfClaimCtx *) opaque;
    bool        isnull = false;
    bool        rat_null = false, dev_null = false, vol_null = false, mat_null = false;
    Datum       d_i, d_ent, d_path, d_rat, d_dev, d_vol, d_mat;
    int64       idx, mat = 0;
    double      rat = 0.0, dev = 0.0, vol = 0.0;
    lp_path     path;
    uint32      len;
    lp_id       parts[64];
    MemoryContext prev;

    d_i = slot_getattr(slot, 1, &isnull); if (isnull) return true;
    idx = DatumGetInt64(d_i);
    if (idx < 1 || idx > ctx->n_anchors) return true;

    d_path = slot_getattr(slot, 3, &isnull); if (isnull) return true;
    path = laplace_pub_geo_path(d_path);
    len = lp_path_len(path);
    if (len < 2 || len > 64 || lp_path_expand(path, parts, (int) len) != len)
        return true;

    d_ent = slot_getattr(slot, 2, &isnull); if (isnull) return true;
    d_rat = slot_getattr(slot, 4, &rat_null); if (!rat_null) rat = DatumGetFloat8(d_rat);
    d_dev = slot_getattr(slot, 5, &dev_null); if (!dev_null) dev = DatumGetFloat8(d_dev);
    d_vol = slot_getattr(slot, 6, &vol_null); if (!vol_null) vol = DatumGetFloat8(d_vol);
    d_mat = slot_getattr(slot, 7, &mat_null); if (!mat_null) mat = DatumGetInt64(d_mat);

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count >= ctx->cap)
    {
        Size sz;
        ctx->cap = ctx->cap ? ctx->cap * 2 : 64;
        sz = sizeof(RfClaimCell) * (Size) ctx->cap;
        ctx->cells = ctx->cells ? (RfClaimCell *) repalloc(ctx->cells, sz) : (RfClaimCell *) palloc(sz);
        ctx->next = ctx->next
            ? (int *) repalloc(ctx->next, sizeof(int) * (Size) ctx->cap)
            : (int *) palloc(sizeof(int) * (Size) ctx->cap);
    }
    {
        RfClaimCell *c = &ctx->cells[ctx->count];
        bool         f = false;
        RfClaimHead *hd;

        c->anchor = ctx->anchors[idx - 1];
        c->via = laplace_pub_blake3_to_hash128(d_ent);
        c->n_parts = (int) len;
        for (uint32 k = 0; k < len; k++)
            memcpy(&c->parts[k], parts[k].b, sizeof(hash128_t));
        c->matches = mat;
        c->matches_null = mat_null;
        c->rating = rat;
        c->rating_null = rat_null;
        c->rd = dev;
        c->rd_null = dev_null;
        c->volatility = vol;
        c->vol_null = vol_null;

        hd = (RfClaimHead *) hash_search(ctx->heads, &c->anchor, HASH_ENTER, &f);
        ctx->next[ctx->count] = f ? hd->first : -1;
        hd->first = ctx->count++;
    }
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct
{
    int32     round;
    int32     occ;
    hash128_t anchor;
    hash128_t entity;
    int16     route;
    hash128_t via;
    bool      via_null;
    hash128_t rel;
    bool      rel_null;
    double    rating;
    bool      rating_null;
    double    rd;
    bool      rd_null;
    double    volatility;
    bool      vol_null;
    int64     matches;
    bool      matches_null;
    int16     tier;
    double    distance;
    bool      distance_null;
    bool      outbound;
    bool      outbound_null;
} RfBoundRow;

static int
rf_bound_cmp(const void *a, const void *b)
{
    const RfBoundRow *x = (const RfBoundRow *) a;
    const RfBoundRow *y = (const RfBoundRow *) b;
    int c;
    if (x->round != y->round) return x->round < y->round ? -1 : 1;
    if (x->occ != y->occ) return x->occ < y->occ ? -1 : 1;
    if (x->route != y->route) return x->route < y->route ? -1 : 1;
    c = memcmp(&x->entity, &y->entity, sizeof(hash128_t));
    if (c != 0) return c;
    if (x->via_null != y->via_null) return x->via_null ? 1 : -1;
    if (!x->via_null && (c = memcmp(&x->via, &y->via, sizeof(hash128_t))) != 0) return c;
    if (x->rel_null != y->rel_null) return x->rel_null ? 1 : -1;
    if (!x->rel_null) return memcmp(&x->rel, &y->rel, sizeof(hash128_t));
    return 0;
}

Datum
pg_laplace_query_response_field(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    MemoryContext  caller = CurrentMemoryContext;
    MemoryContext  work, prev;
    hash128_t     *roots;
    int            n_roots = 0;
    int64          fan;
    int32          keep;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1) || PG_ARGISNULL(2))
        return (Datum) 0;
    roots = qrf_bytea_ids_nonnull(PG_GETARG_ARRAYTYPE_P(0), false, &n_roots);
    if (n_roots == 0)
    {
        if (roots) pfree(roots);
        return (Datum) 0;
    }
    fan = (int64) PG_GETARG_INT32(1);
    keep = PG_GETARG_INT32(2);

    work = AllocSetContextCreate(caller, "query_response_field", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    {
        ConverseBindingRow *brows;
        int                 n_brows = 0;
        HTAB               *origins = NULL, *bclaims = NULL;
        Oid                 b3_arr_oid = laplace_pub_blake3_array_oid();
        ArrayType          *roots_b3 = laplace_pub_blake3_array(roots, n_roots);
        ArrayType          *empty_b3 = laplace_pub_blake3_array(NULL, 0);
        Oid                 cpl_types[6] = {
            b3_arr_oid, INT8OID, b3_arr_oid, INT2OID, FLOAT8OID, INT4OID
        };
        Datum               cpl1[6] = {
            PointerGetDatum(roots_b3), Int64GetDatum(fan), PointerGetDatum(empty_b3),
            Int16GetDatum(0), Float8GetDatum(0.0), Int32GetDatum(keep)
        };
        bool                cpl_nulls[6] = { false, false, false, false, false, false };
        RfCoupleCtx         first_ctx, all_ctx;
        hash128_t          *front_raw;
        hash128_t          *front_uniq;
        int                 nf_raw = 0, nf_uniq;
        HTAB               *first_occ_by_ent;

        brows = converse_binding_evidence_c(roots, n_roots, NULL, false, &n_brows);
        build_lexical_origin_maps(work, roots, n_roots, brows, n_brows,
                                  &origins, &bclaims, NULL);

        MemSet(&first_ctx, 0, sizeof(first_ctx));
        first_ctx.caller = work;
        first_ctx.round = 1;
        first_ctx.anchors = roots;
        first_ctx.n_anchors = n_roots;
        (void) laplace_pub_call_srf("public", "laplace_couple", 6,
                                    cpl_types, cpl1, cpl_nulls, rf_visit_couple, &first_ctx);
        pfree(roots_b3);

        first_occ_by_ent = qrf_hash_id(work, "rf foc", sizeof(LexOriginEntry),
                                       Max(first_ctx.count, 16));
        front_raw = (hash128_t *) palloc(sizeof(hash128_t) * (Size) (first_ctx.count + n_brows * 2 + 1));
        for (int i = 0; i < first_ctx.count; i++)
        {
            front_raw[nf_raw++] = first_ctx.items[i].entity;
            if (first_ctx.items[i].route != 2)
            {
                bool f = false;
                LexOriginEntry *e = (LexOriginEntry *) hash_search(
                    first_occ_by_ent, &first_ctx.items[i].entity, HASH_ENTER, &f);
                if (!f) MemSet(&e->ords, 0, sizeof(OrdList));
                ord_push_sorted_uniq(&e->ords, first_ctx.items[i].occ);
            }
        }
        for (int i = 0; i < n_brows; i++)
        {
            front_raw[nf_raw++] = brows[i].key;
            front_raw[nf_raw++] = brows[i].binding;
        }
        nf_uniq = nf_raw > 0 ? qrf_dedup_ids(front_raw, nf_raw,
                                             (hash128_t *) palloc(sizeof(hash128_t) * (Size) nf_raw))
                             : 0;
        front_uniq = nf_uniq > 0 ? (hash128_t *) palloc(sizeof(hash128_t) * (Size) nf_uniq) : NULL;
        if (nf_uniq > 0)
            qrf_dedup_ids(front_raw, nf_raw, front_uniq);

        MemSet(&all_ctx, 0, sizeof(all_ctx));
        all_ctx.caller = work;
        if (first_ctx.count > 0)
        {
            all_ctx.cap = first_ctx.count * 2 + 32;
            all_ctx.items = (RfRespRow *) palloc(sizeof(RfRespRow) * (Size) all_ctx.cap);
            memcpy(all_ctx.items, first_ctx.items, sizeof(RfRespRow) * (Size) first_ctx.count);
            all_ctx.count = first_ctx.count;
        }

        if (nf_uniq > 0)
        {
            ArrayType *f_arr = laplace_pub_blake3_array(front_uniq, nf_uniq);
            Datum      cpl2[6] = {
                PointerGetDatum(f_arr), Int64GetDatum(fan), PointerGetDatum(empty_b3),
                Int16GetDatum(-1), Float8GetDatum(0.0), Int32GetDatum(0)
            };
            all_ctx.round = 2;
            all_ctx.anchors = front_uniq;
            all_ctx.n_anchors = nf_uniq;
            (void) laplace_pub_call_srf("public", "laplace_couple", 6,
                                        cpl_types, cpl2, cpl_nulls, rf_visit_couple, &all_ctx);
            pfree(f_arr);
        }
        pfree(empty_b3);

        if (all_ctx.count > 0)
        {
            hash128_t *anch_raw = (hash128_t *) palloc(sizeof(hash128_t) * (Size) all_ctx.count);
            hash128_t *anch_uniq = (hash128_t *) palloc(sizeof(hash128_t) * (Size) all_ctx.count);
            int        na_raw = 0, na_uniq = 0;
            RfClaimCtx cctx;

            for (int i = 0; i < all_ctx.count; i++)
            {
                if (all_ctx.items[i].route == 0)
                    anch_raw[na_raw++] = all_ctx.items[i].anchor;
            }
            if (na_raw > 0)
                na_uniq = qrf_dedup_ids(anch_raw, na_raw, anch_uniq);

            MemSet(&cctx, 0, sizeof(cctx));
            cctx.caller = work;
            cctx.anchors = anch_uniq;
            cctx.n_anchors = na_uniq;
            cctx.heads = qrf_hash_id(work, "rf cheads", sizeof(RfClaimHead), Max(na_uniq, 16));

            if (na_uniq > 0)
            {
                ArrayType *a_arr = laplace_pub_blake3_array(anch_uniq, na_uniq);
                ArrayType *e_arr = laplace_pub_blake3_array(NULL, 0);
                Datum      bit_d = Int16GetDatum(laplace_pub_bank_bit("kind", "claim"));
                ArrayType *bits_arr = construct_array(&bit_d, 1, INT2OID, sizeof(int16), true, TYPALIGN_SHORT);
                Oid        ct[4] = { b3_arr_oid, INT8OID, INT2ARRAYOID, b3_arr_oid };
                Datum      ca[4] = { PointerGetDatum(a_arr), (Datum) 0,
                                     PointerGetDatum(bits_arr), PointerGetDatum(e_arr) };
                bool       cn[4] = { false, true, false, false };

                (void) laplace_pub_call_srf("public", "laplace_claims_each", 4,
                                            ct, ca, cn, rf_visit_claim, &cctx);
                pfree(a_arr);
                pfree(e_arr);
                pfree(bits_arr);
            }

            /* Bind route=0 responses with matching claims (DISTINCT ON (round,occ,route,entity,c.entity)). */
            {
                typedef struct
                {
                    int32     round;
                    int32     occ;
                    int16     route;
                    hash128_t entity;
                    hash128_t via;
                    bool      via_null;
                } RfBoundKey;
                HTAB       *seen_bound = qrf_hash_key(work, "rf sb", sizeof(RfBoundKey),
                                                      sizeof(RfBoundKey), all_ctx.count * 2);
                RfBoundRow *bound = NULL;
                int         n_bound = 0, bcap = 0;

                for (int ri = 0; ri < all_ctx.count; ri++)
                {
                    const RfRespRow *r = &all_ctx.items[ri];
                    RfClaimHead     *hd = (r->route == 0)
                        ? (RfClaimHead *) hash_search(cctx.heads, &r->anchor, HASH_FIND, NULL) : NULL;
                    bool             matched_any = false;

                    if (hd != NULL)
                    {
                        for (int ci = hd->first; ci >= 0; ci = cctx.next[ci])
                        {
                            const RfClaimCell *c = &cctx.cells[ci];
                            bool               in_parts = false;
                            RfBoundKey         bk;
                            bool               f = false;

                            for (int pi = 0; pi < c->n_parts; pi++)
                            {
                                if (hash128_eq(&c->parts[pi], &r->entity))
                                { in_parts = true; break; }
                            }
                            if (!in_parts)
                                continue;
                            if (!(c->n_parts == 2 || (!r->rel_null && c->n_parts >= 3 && hash128_eq(&c->parts[1], &r->rel))))
                                continue;

                            matched_any = true;
                            MemSet(&bk, 0, sizeof(bk));
                            bk.round = r->round;
                            bk.occ = r->occ;
                            bk.route = r->route;
                            bk.entity = r->entity;
                            bk.via = c->via;
                            bk.via_null = false;
                            (void) hash_search(seen_bound, &bk, HASH_ENTER, &f);
                            if (f) continue;

                            if (n_bound >= bcap)
                            {
                                Size sz;
                                bcap = bcap ? bcap * 2 : 64;
                                sz = sizeof(RfBoundRow) * (Size) bcap;
                                bound = bound ? (RfBoundRow *) repalloc(bound, sz) : (RfBoundRow *) palloc(sz);
                            }
                            {
                                RfBoundRow *br = &bound[n_bound++];
                                MemSet(br, 0, sizeof(*br));
                                br->round = r->round;
                                br->occ = r->occ;
                                br->anchor = r->anchor;
                                br->entity = r->entity;
                                br->route = r->route;
                                br->via = c->via;
                                br->via_null = false;
                                if (c->n_parts >= 3)
                                    br->rel = c->parts[1];
                                else
                                    br->rel_null = true;
                                br->rating = c->rating;
                                br->rating_null = c->rating_null;
                                br->rd = c->rd;
                                br->rd_null = c->rd_null;
                                br->volatility = c->volatility;
                                br->vol_null = c->vol_null;
                                br->matches = c->matches;
                                br->matches_null = c->matches_null;
                                br->tier = r->tier;
                                br->distance = r->distance;
                                br->distance_null = r->distance_null;
                                br->outbound = hash128_eq(&c->parts[0], &r->anchor);
                                br->outbound_null = false;
                            }
                        }
                    }

                    if (!matched_any)
                    {
                        RfBoundKey bk;
                        bool       f = false;
                        MemSet(&bk, 0, sizeof(bk));
                        bk.round = r->round;
                        bk.occ = r->occ;
                        bk.route = r->route;
                        bk.entity = r->entity;
                        bk.via_null = true;
                        (void) hash_search(seen_bound, &bk, HASH_ENTER, &f);
                        if (!f)
                        {
                            if (n_bound >= bcap)
                            {
                                Size sz;
                                bcap = bcap ? bcap * 2 : 64;
                                sz = sizeof(RfBoundRow) * (Size) bcap;
                                bound = bound ? (RfBoundRow *) repalloc(bound, sz) : (RfBoundRow *) palloc(sz);
                            }
                            {
                                RfBoundRow *br = &bound[n_bound++];
                                MemSet(br, 0, sizeof(*br));
                                br->round = r->round;
                                br->occ = r->occ;
                                br->anchor = r->anchor;
                                br->entity = r->entity;
                                br->route = r->route;
                                br->via_null = true;
                                br->rel_null = true;
                                br->rating_null = true;
                                br->rd_null = true;
                                br->vol_null = true;
                                br->matches_null = true;
                                br->tier = r->tier;
                                br->distance = r->distance;
                                br->distance_null = r->distance_null;
                                br->outbound_null = true;
                            }
                        }
                    }
                }

                if (n_bound > 0)
                {
                    hash128_t *e_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) (n_bound * 3));
                    hash128_t *l_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) (n_bound * 3));
                    int        ne = 0, nl = 0, nue, nul;
                    hash128_t *ue, *ul;
                    HTAB      *forms = qrf_hash_id(work, "rf forms", sizeof(QrfFormEntry), n_bound * 2);
                    HTAB      *lmap = qrf_hash_id(work, "rf lmap", sizeof(QrfLabelEntry), n_bound * 2);

                    for (int i = 0; i < n_bound; i++)
                    {
                        e_ids[ne++] = bound[i].entity;
                        e_ids[ne++] = bound[i].anchor;
                        if (!bound[i].rel_null) e_ids[ne++] = bound[i].rel;
                        if (bound[i].route == 0)
                        {
                            l_ids[nl++] = bound[i].entity;
                            l_ids[nl++] = bound[i].anchor;
                            if (!bound[i].rel_null) l_ids[nl++] = bound[i].rel;
                        }
                    }

                    ue = (hash128_t *) palloc(sizeof(hash128_t) * (Size) Max(ne, 1));
                    nue = ne > 0 ? qrf_dedup_ids(e_ids, ne, ue) : 0;
                    if (nue > 0)
                    {
                        ArrayType *fa = laplace_pub_blake3_array(ue, nue);
                        Datum      farg = PointerGetDatum(fa);
                        bool       fn = false;
                        QrfPhysCtx pctx = { work, forms };
                        (void) laplace_pub_call_srf("public", "laplace_physicalities", 1,
                                                    &b3_arr_oid, &farg, &fn, qrf_visit_physicality, &pctx);
                        pfree(fa);
                    }

                    ul = (hash128_t *) palloc(sizeof(hash128_t) * (Size) Max(nl, 1));
                    nul = nl > 0 ? qrf_dedup_ids(l_ids, nl, ul) : 0;
                    if (nul > 0)
                    {
                        ArrayType  *la = laplace_pub_blake3_array(ul, nul);
                        Datum       larg = PointerGetDatum(la);
                        bool        ln = false;
                        QrfLabelCtx lctx = { work, lmap };
                        (void) laplace_pub_call_srf("public", "laplace_labels", 1,
                                                    &b3_arr_oid, &larg, &ln, qrf_visit_label, &lctx);
                        pfree(la);
                    }

                    if (n_bound > 1)
                        qsort(bound, (Size) n_bound, sizeof(RfBoundRow), rf_bound_cmp);

                    for (int i = 0; i < n_bound; i++)
                    {
                        const RfBoundRow  *r = &bound[i];
                        QrfLabelEntry     *al = (QrfLabelEntry *) hash_search(lmap, &r->anchor, HASH_FIND, NULL);
                        QrfLabelEntry     *el = (QrfLabelEntry *) hash_search(lmap, &r->entity, HASH_FIND, NULL);
                        QrfLabelEntry     *rl = r->rel_null ? NULL
                            : (QrfLabelEntry *) hash_search(lmap, &r->rel, HASH_FIND, NULL);
                        LexOriginEntry    *tb = (LexOriginEntry *) hash_search(origins, &r->entity, HASH_FIND, NULL);
                        LexOriginEntry    *ab = (LexOriginEntry *) hash_search(origins, &r->anchor, HASH_FIND, NULL);
                        QrfFormEntry      *ef = (QrfFormEntry *) hash_search(forms, &r->entity, HASH_FIND, NULL);
                        AnchorClaimEntry  *bc = (AnchorClaimEntry *) hash_search(bclaims, &r->anchor, HASH_FIND, NULL);
                        ArrayType         *anchors_arr;
                        ArrayType         *ro_arr;
                        OrdList            ro_list = { NULL, 0, 0 };
                        Datum              vals[24];
                        bool               nulls[24];

                        if (r->round == 1)
                        {
                            Datum d1 = Int32GetDatum(r->occ);
                            anchors_arr = construct_array(&d1, 1, INT4OID, sizeof(int32), true, TYPALIGN_INT);
                        }
                        else
                        {
                            LexOriginEntry *fo = (LexOriginEntry *) hash_search(
                                first_occ_by_ent, &r->anchor, HASH_FIND, NULL);
                            anchors_arr = fo ? int32_array_from_ordlist(&fo->ords)
                                             : construct_empty_array(INT4OID);
                        }

                        for (int ri_r = 0; ri_r < n_roots; ri_r++)
                        {
                            if (hash128_eq(&roots[ri_r], &r->anchor))
                                ord_push_sorted_uniq(&ro_list, ri_r + 1);
                        }
                        for (int bi = 0; bi < n_brows; bi++)
                        {
                            if (!hash128_eq(&brows[bi].key, &r->anchor) &&
                                !hash128_eq(&brows[bi].binding, &r->anchor))
                                continue;
                            for (int ri_r = 0; ri_r < n_roots; ri_r++)
                            {
                                if (hash128_eq(&brows[bi].term, &roots[ri_r]))
                                    ord_push_sorted_uniq(&ro_list, ri_r + 1);
                            }
                        }
                        ro_arr = int32_array_from_ordlist(&ro_list);

                        MemSet(nulls, 0, sizeof(nulls));
                        vals[0]  = Int32GetDatum(r->round);
                        vals[1]  = Int32GetDatum(r->occ);
                        vals[2]  = hash128_to_datum(&r->anchor);
                        vals[3]  = (al && !al->content_null && al->content) ? CStringGetTextDatum(al->content) : (Datum) 0;
                        nulls[3] = (vals[3] == (Datum) 0);
                        vals[4]  = hash128_to_datum(&r->entity);
                        if (el && !el->content_null && el->content)
                            vals[5] = CStringGetTextDatum(el->content);
                        else
                        {
                            char *hx = qrf_id_text_alloc(&r->entity);
                            vals[5] = CStringGetTextDatum(hx);
                            pfree(hx);
                        }
                        vals[6]  = Int16GetDatum(r->route);
                        if (r->via_null) { nulls[7] = true; vals[7] = (Datum) 0; }
                        else vals[7] = hash128_to_datum(&r->via);
                        if (r->rel_null)
                        {
                            nulls[8] = true; vals[8] = (Datum) 0;
                            nulls[9] = true; vals[9] = (Datum) 0;
                        }
                        else
                        {
                            vals[8] = hash128_to_datum(&r->rel);
                            vals[9] = (rl && !rl->content_null && rl->content) ? CStringGetTextDatum(rl->content) : (Datum) 0;
                            nulls[9] = (vals[9] == (Datum) 0);
                        }
                        if (r->rating_null) { nulls[10] = true; vals[10] = (Datum) 0; }
                        else vals[10] = Float8GetDatum(r->rating);
                        if (r->rd_null) { nulls[11] = true; vals[11] = (Datum) 0; }
                        else vals[11] = Float8GetDatum(r->rd);
                        if (r->vol_null) { nulls[12] = true; vals[12] = (Datum) 0; }
                        else vals[12] = Float8GetDatum(r->volatility);
                        if (r->matches_null) { nulls[13] = true; vals[13] = (Datum) 0; }
                        else vals[13] = Int64GetDatum(r->matches);
                        nulls[14] = true; vals[14] = (Datum) 0;
                        vals[15] = Int16GetDatum(r->tier);
                        if (r->distance_null) { nulls[16] = true; vals[16] = (Datum) 0; }
                        else vals[16] = Float8GetDatum(r->distance);
                        if (r->outbound_null) { nulls[17] = true; vals[17] = (Datum) 0; }
                        else vals[17] = BoolGetDatum(r->outbound);
                        vals[18] = PointerGetDatum(anchors_arr);
                        vals[19] = PointerGetDatum(tb ? int32_array_from_ordlist(&tb->ords)
                                                      : construct_empty_array(INT4OID));
                        vals[20] = PointerGetDatum(bc ? hash128_array_from_ids(bc->cl.ids, bc->cl.count)
                                                      : construct_empty_array(BYTEAOID));
                        vals[21] = PointerGetDatum(ab ? int32_array_from_ordlist(&ab->ords)
                                                      : construct_empty_array(INT4OID));
                        vals[22] = BoolGetDatum(ef != NULL && ef->kind_slot == -1);
                        vals[23] = PointerGetDatum(ro_arr);

                        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
                        pfree(DatumGetPointer(vals[2]));
                        pfree(DatumGetPointer(vals[4]));
                        if (!nulls[7]) pfree(DatumGetPointer(vals[7]));
                        if (!nulls[8]) pfree(DatumGetPointer(vals[8]));
                    }
                }
            }
        }
    }

    pfree(roots);
    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    return (Datum) 0;
}
