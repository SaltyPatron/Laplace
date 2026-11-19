#include "postgres.h"

#include <math.h>
#include <string.h>

#include "catalog/pg_collation.h"
#include "catalog/pg_type.h"
#include "funcapi.h"
#include "lib/stringinfo.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/fmgroids.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"

#include "laplace/core/hash128.h"
#include "laplace/core/highway_table.h"
#include "laplace/core/relation_law.h"
#include "converse_bindings.h"
#include "edges_raw_internal.h"
#include "perfcache_native.h"
#include "public_direct.h"
#include "relations_internal.h"
#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_laplace_converse_facts);

typedef struct CfPullRow
{
    int64      ordinal;
    hash128_t  id;
    double     rating;
    double     deviation;
    double     cost;
    int32      hops;
    hash128_t *chain;
    int        n_chain;
    hash128_t *claims;
    int        n_claims;
} CfPullRow;

typedef struct CfPullCtx
{
    MemoryContext caller;
    CfPullRow    *rows;
    int           count;
    int           cap;
} CfPullCtx;

typedef struct CfClaimTypeEntry
{
    hash128_t key;
    hash128_t type_id;
} CfClaimTypeEntry;

typedef struct CfClaimStandEntry
{
    hash128_t key;
    double    eff_mu;
} CfClaimStandEntry;

typedef struct CfLabelEntry
{
    hash128_t key;
    char     *content;
    bool      is_hex;
} CfLabelEntry;

typedef struct CfPathCtx
{
    MemoryContext caller;
    HTAB         *claim_types;
} CfPathCtx;

typedef struct CfStandCtx
{
    MemoryContext caller;
    HTAB         *claim_stand;
} CfStandCtx;

typedef struct CfLabelCtx
{
    MemoryContext caller;
    HTAB         *labels;
} CfLabelCtx;

static hash128_t
cf_resolve_canon_type(const hash128_t *t)
{
    const laplace_relation_def_t *def = NULL;
    hash128_t                     canon;

    if (laplace_relation_lookup(t, &def) == 0 && def != NULL &&
        def->canonical != NULL &&
        laplace_relation_type_id(def->canonical, &canon) >= 0)
        return canon;
    if (laplace_relation_metadata_lookup_c(t, &canon, NULL, NULL, NULL))
        return canon;
    return *t;
}

static bool
cf_in_family_any(const hash128_t *t, const char **families, int n_families)
{
    for (int i = 0; i < n_families; i++)
    {
        int in_fam = 0;
        if (laplace_relation_in_family(t, families[i], &in_fam) == 0 && in_fam != 0)
            return true;
    }
    return false;
}

static int
cf_edge_mu_cmp(const void *a, const void *b)
{
    const LaplaceEdgeRawRow *ea = (const LaplaceEdgeRawRow *) a;
    const LaplaceEdgeRawRow *eb = (const LaplaceEdgeRawRow *) b;
    int64                    ma = laplace_effective_mu_fp(ea->rating, ea->rd);
    int64                    mb = laplace_effective_mu_fp(eb->rating, eb->rd);

    if (ma != mb)
        return ma > mb ? -1 : 1;
    if (ea->neighbour_null != eb->neighbour_null)
        return ea->neighbour_null ? 1 : -1;
    if (!ea->neighbour_null)
    {
        int cmp = memcmp(&ea->neighbour_id, &eb->neighbour_id, sizeof(hash128_t));
        if (cmp != 0)
            return cmp;
    }
    return 0;
}

static bool
cf_visit_pull(TupleTableSlot *slot, void *opaque)
{
    CfPullCtx    *ctx = (CfPullCtx *) opaque;
    bool          isnull = false;
    Datum         d_ord, d_id, d_rat, d_dev, d_cost, d_hops, d_chain, d_claims;
    int32         hops;
    int64         ord = 0;
    hash128_t     id;
    double        rat = 0.0, dev = 0.0, cost = 0.0;
    bool          ch_null = false, cl_null = false;
    Oid           b3_oid = laplace_pub_blake3_oid();
    MemoryContext prev;

    d_hops = slot_getattr(slot, 10, &isnull);
    if (isnull)
        return true;
    hops = DatumGetInt32(d_hops);
    if (hops < 1)
        return true;

    d_ord = slot_getattr(slot, 1, &isnull);
    if (!isnull)
        ord = DatumGetInt64(d_ord);
    d_id = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;
    id = laplace_pub_blake3_to_hash128(d_id);

    d_rat = slot_getattr(slot, 7, &isnull);
    if (!isnull)
        rat = DatumGetFloat8(d_rat);
    d_dev = slot_getattr(slot, 8, &isnull);
    if (!isnull)
        dev = DatumGetFloat8(d_dev);
    d_cost = slot_getattr(slot, 9, &isnull);
    if (!isnull)
        cost = DatumGetFloat8(d_cost);

    d_chain = slot_getattr(slot, 13, &ch_null);
    d_claims = slot_getattr(slot, 14, &cl_null);

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count >= ctx->cap)
    {
        Size sz;
        ctx->cap = ctx->cap ? ctx->cap * 2 : 32;
        sz = sizeof(CfPullRow) * (Size) ctx->cap;
        ctx->rows = ctx->rows
            ? (CfPullRow *) repalloc(ctx->rows, sz)
            : (CfPullRow *) palloc(sz);
    }
    {
        CfPullRow *r = &ctx->rows[ctx->count++];
        MemSet(r, 0, sizeof(*r));
        r->ordinal = ord;
        r->id = id;
        r->rating = rat;
        r->deviation = dev;
        r->cost = cost;
        r->hops = hops;

        if (!ch_null)
        {
            Datum     *cd = NULL;
            bool      *cn = NULL;
            int        nc = 0;
            ArrayType *arr = DatumGetArrayTypeP(d_chain);

            deconstruct_array(arr, b3_oid, 16, false, TYPALIGN_CHAR, &cd, &cn, &nc);
            if (nc > 0)
            {
                r->chain = (hash128_t *) palloc(sizeof(hash128_t) * (Size) nc);
                for (int i = 0; i < nc; i++)
                {
                    if (!cn[i])
                        r->chain[r->n_chain++] = laplace_pub_blake3_to_hash128(cd[i]);
                }
            }
            pfree(cd);
            pfree(cn);
        }

        if (!cl_null)
        {
            Datum     *ld = NULL;
            bool      *ln = NULL;
            int        nl = 0;
            ArrayType *arr = DatumGetArrayTypeP(d_claims);

            deconstruct_array(arr, b3_oid, 16, false, TYPALIGN_CHAR, &ld, &ln, &nl);
            if (nl > 0)
            {
                r->claims = (hash128_t *) palloc(sizeof(hash128_t) * (Size) nl);
                for (int i = 0; i < nl; i++)
                {
                    if (!ln[i])
                        r->claims[r->n_claims++] = laplace_pub_blake3_to_hash128(ld[i]);
                }
            }
            pfree(ld);
            pfree(ln);
        }
    }
    MemoryContextSwitchTo(prev);
    return true;
}

static bool
cf_visit_path(TupleTableSlot *slot, void *opaque)
{
    CfPathCtx        *ctx = (CfPathCtx *) opaque;
    bool              isnull = false;
    Datum             d_ent, d_path;
    lp_path           path;
    lp_id             parts[2];
    hash128_t         ent, rel;
    CfClaimTypeEntry *te;
    bool              found = false;
    MemoryContext     prev;

    d_path = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;
    path = laplace_pub_geo_path(d_path);
    if (lp_path_len(path) < 2 || lp_path_expand(path, parts, 2) < 2)
        return true;

    d_ent = slot_getattr(slot, 1, &isnull);
    if (isnull)
        return true;
    ent = laplace_pub_blake3_to_hash128(d_ent);
    memcpy(&rel, parts[1].b, sizeof(hash128_t));

    prev = MemoryContextSwitchTo(ctx->caller);
    te = (CfClaimTypeEntry *) hash_search(ctx->claim_types, &ent, HASH_ENTER, &found);
    if (!found)
        te->type_id = rel;
    MemoryContextSwitchTo(prev);
    return true;
}

static bool
cf_visit_standing(TupleTableSlot *slot, void *opaque)
{
    CfStandCtx        *ctx = (CfStandCtx *) opaque;
    bool               isnull = false;
    Datum              d_cl, d_rat, d_dev;
    hash128_t          cl;
    double             rat, dev;
    CfClaimStandEntry *se;
    bool               found = false;
    MemoryContext      prev;

    d_cl = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;
    cl = laplace_pub_blake3_to_hash128(d_cl);
    d_rat = slot_getattr(slot, 3, &isnull);
    if (isnull)
        return true;
    rat = DatumGetFloat8(d_rat);
    d_dev = slot_getattr(slot, 4, &isnull);
    if (isnull)
        return true;
    dev = DatumGetFloat8(d_dev);

    prev = MemoryContextSwitchTo(ctx->caller);
    se = (CfClaimStandEntry *) hash_search(ctx->claim_stand, &cl, HASH_ENTER, &found);
    if (!found)
        se->eff_mu = rat - 2.0 * dev;
    MemoryContextSwitchTo(prev);
    return true;
}

static bool
cf_matches_hex32(const hash128_t *id, const char *s)
{
    char hex[33];

    if (s == NULL)
        return false;
    lp_id_hex((const lp_id *) id, hex);
    return strcmp(s, hex) == 0;
}

static bool
cf_is_cili_code(const char *s)
{
    int len = 0;

    if (s == NULL || (s[0] != 'i' && s[0] != 'I'))
        return false;
    for (int p = 1; s[p] != '\0'; p++)
    {
        if (s[p] < '0' || s[p] > '9')
            return false;
        len++;
        if (len > 11)
            return false;
    }
    return len >= 1;
}

static bool
cf_visit_label(TupleTableSlot *slot, void *opaque)
{
    CfLabelCtx   *ctx = (CfLabelCtx *) opaque;
    bool          isnull = false;
    Datum         d_ent, d_txt;
    hash128_t     ent;
    CfLabelEntry *le;
    bool          found = false;
    MemoryContext prev;

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;
    ent = laplace_pub_blake3_to_hash128(d_ent);
    d_txt = slot_getattr(slot, 3, &isnull);
    if (isnull)
        return true;

    prev = MemoryContextSwitchTo(ctx->caller);
    le = (CfLabelEntry *) hash_search(ctx->labels, &ent, HASH_ENTER, &found);
    if (!found)
    {
        le->content = TextDatumGetCString(d_txt);
        le->is_hex = cf_matches_hex32(&ent, le->content);
    }
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct CfTermPick
{
    hash128_t key;
    hash128_t term;
    bool      in_lang;
    int64     games;
    double    eff_mu;
} CfTermPick;

static void
cf_pick_bindings(HTAB *picks,
                 const hash128_t *cili_ids,
                 int n_cili,
                 const hash128_t *lang_id)
{
    int                 n_brows = 0;
    ConverseBindingRow *brows;

    if (cili_ids == NULL || n_cili <= 0)
        return;

    brows = converse_key_binding_evidence_c(cili_ids, n_cili, lang_id, false, &n_brows);
    for (int i = 0; i < n_brows; i++)
    {
        const ConverseBindingRow *r = &brows[i];
        bool                      found = false;
        CfTermPick               *pk = (CfTermPick *) hash_search(
            picks, &r->key, HASH_ENTER, &found);
        bool                      better = !found;

        if (found)
        {
            if (r->in_lang != pk->in_lang)
                better = r->in_lang;
            else if (r->games != pk->games)
                better = r->games > pk->games;
            else if (r->eff_mu != pk->eff_mu)
                better = r->eff_mu > pk->eff_mu;
            else if (memcmp(&r->term, &pk->term, sizeof(hash128_t)) < 0)
                better = true;
        }
        if (better)
        {
            pk->term = r->term;
            pk->in_lang = r->in_lang;
            pk->games = r->games;
            pk->eff_mu = r->eff_mu;
        }
    }
    if (brows != NULL)
        pfree(brows);
}

static HTAB *
cf_realize_entities(MemoryContext work,
                    const hash128_t *ids,
                    int n_ids,
                    const hash128_t *lang_id)
{
    MemoryContext old = MemoryContextSwitchTo(work);
    HASHCTL       ctl;
    HTAB         *lbl_map;
    Oid           b3_arr_oid = laplace_pub_blake3_array_oid();
    hash128_t    *uniq;
    int           n_uniq = 0;
    hash128_t    *cili_ids;
    int           n_cili = 0;

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(CfLabelEntry);
    ctl.hcxt = work;
    lbl_map = hash_create("cf realized labels", Max(n_ids, 16), &ctl,
                          HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    if (ids == NULL || n_ids <= 0)
    {
        MemoryContextSwitchTo(old);
        return lbl_map;
    }

    uniq = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_ids);
    for (int i = 0; i < n_ids; i++)
    {
        bool dup = false;
        for (int j = 0; j < n_uniq; j++)
        {
            if (hash128_eq(&uniq[j], &ids[i]))
            {
                dup = true;
                break;
            }
        }
        if (!dup)
            uniq[n_uniq++] = ids[i];
    }

    {
        ArrayType *lbl_b3 = laplace_pub_blake3_array(uniq, n_uniq);
        Datum      la = PointerGetDatum(lbl_b3);
        bool       ln = false;
        CfLabelCtx lctx = { work, lbl_map };

        (void) laplace_pub_call_srf("public", "laplace_labels", 1, &b3_arr_oid,
                                    &la, &ln, cf_visit_label, &lctx);
        pfree(lbl_b3);
    }

    cili_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_uniq);
    for (int i = 0; i < n_uniq; i++)
    {
        CfLabelEntry *le = (CfLabelEntry *) hash_search(lbl_map, &uniq[i], HASH_FIND, NULL);
        if (le == NULL || le->content == NULL || le->is_hex || cf_is_cili_code(le->content))
            cili_ids[n_cili++] = uniq[i];
    }

    if (n_cili > 0)
    {
        HTAB *picks;

        MemSet(&ctl, 0, sizeof(ctl));
        ctl.keysize = sizeof(hash128_t);
        ctl.entrysize = sizeof(CfTermPick);
        ctl.hcxt = work;
        picks = hash_create("cf cili picks", Max(n_cili, 16), &ctl,
                            HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

        cf_pick_bindings(picks, cili_ids, n_cili, lang_id);

        if (lang_id != NULL)
        {
            hash128_t *rem_cili = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_cili);
            int        n_rem = 0;

            for (int i = 0; i < n_cili; i++)
            {
                if (hash_search(picks, &cili_ids[i], HASH_FIND, NULL) == NULL)
                    rem_cili[n_rem++] = cili_ids[i];
            }
            if (n_rem > 0)
                cf_pick_bindings(picks, rem_cili, n_rem, NULL);
            pfree(rem_cili);
        }

        if (hash_get_num_entries(picks) > 0)
        {
            long             n_picks = hash_get_num_entries(picks);
            hash128_t       *win_terms = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_picks);
            int              n_terms = 0;
            HTAB            *term_lbls;
            HASH_SEQ_STATUS  seq;
            CfTermPick      *pk;

            hash_seq_init(&seq, picks);
            while ((pk = (CfTermPick *) hash_seq_search(&seq)) != NULL)
            {
                bool dup = false;
                for (int j = 0; j < n_terms; j++)
                {
                    if (hash128_eq(&win_terms[j], &pk->term))
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup)
                    win_terms[n_terms++] = pk->term;
            }

            MemSet(&ctl, 0, sizeof(ctl));
            ctl.keysize = sizeof(hash128_t);
            ctl.entrysize = sizeof(CfLabelEntry);
            ctl.hcxt = work;
            term_lbls = hash_create("cf term labels", Max(n_terms, 16), &ctl,
                                    HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

            if (n_terms > 0)
            {
                ArrayType *tb3 = laplace_pub_blake3_array(win_terms, n_terms);
                Datum      ta = PointerGetDatum(tb3);
                bool       tn = false;
                CfLabelCtx tctx = { work, term_lbls };

                (void) laplace_pub_call_srf("public", "laplace_labels", 1, &b3_arr_oid,
                                            &ta, &tn, cf_visit_label, &tctx);
                pfree(tb3);
            }

            hash_seq_init(&seq, picks);
            while ((pk = (CfTermPick *) hash_seq_search(&seq)) != NULL)
            {
                CfLabelEntry *tle = (CfLabelEntry *) hash_search(term_lbls, &pk->term, HASH_FIND, NULL);
                if (tle != NULL && tle->content != NULL && !tle->is_hex && tle->content[0] != '\0')
                {
                    bool          lf = false;
                    CfLabelEntry *dst = (CfLabelEntry *) hash_search(lbl_map, &pk->key, HASH_ENTER, &lf);
                    dst->content = tle->content;
                    dst->is_hex = false;
                }
            }
            pfree(win_terms);
        }
    }

    pfree(cili_ids);
    pfree(uniq);
    MemoryContextSwitchTo(old);
    return lbl_map;
}

static HTAB *
cf_id_set_create(MemoryContext owner, const char *name, int est)
{
    HASHCTL ctl;

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(hash128_t);
    ctl.hcxt = owner;
    return hash_create(name, Max(est, 16), &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
}

static ArrayType *
cf_bytea_array_from_ids(const hash128_t *ids, int count)
{
    Datum *elems;

    if (count <= 0)
        return construct_empty_array(BYTEAOID);
    elems = (Datum *) palloc(sizeof(Datum) * (Size) count);
    for (int i = 0; i < count; i++)
        elems[i] = hash128_to_datum(&ids[i]);
    return construct_array(elems, count, BYTEAOID, -1, false, TYPALIGN_INT);
}

static void
cf_emit_row(ReturnSetInfo *rsinfo,
            const char *fact_kind,
            Datum sentence, bool sentence_null,
            Datum eff_mu, bool eff_mu_null,
            int64 witnesses, bool witnesses_null,
            ArrayType *path_arr, bool path_null,
            ArrayType *types_arr, bool types_null)
{
    Datum values[6];
    bool  nulls[6] = { false, false, false, false, false, false };

    values[0] = CStringGetTextDatum(fact_kind);
    if (sentence_null)
        nulls[1] = true;
    else
        values[1] = sentence;

    if (eff_mu_null)
        nulls[2] = true;
    else
        values[2] = eff_mu;

    if (witnesses_null)
        nulls[3] = true;
    else
        values[3] = Int64GetDatum(witnesses);

    if (path_null || path_arr == NULL)
        nulls[4] = true;
    else
        values[4] = PointerGetDatum(path_arr);

    if (types_null || types_arr == NULL)
        nulls[5] = true;
    else
        values[5] = PointerGetDatum(types_arr);

    tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
}

static void
cf_emit_listing_fact(MemoryContext work,
                     const hash128_t *lang_id,
                     ReturnSetInfo *rsinfo,
                     const char *fact_kind,
                     const char *prefix_fmt,
                     const char *sep,
                     const char *initcap_cstr,
                     const LaplaceEdgeRawRow *rows,
                     int n_rows,
                     bool has_limit,
                     int32 limit)
{
    int        cap = has_limit ? Min(n_rows, limit) : n_rows;
    hash128_t *nbr_ids;
    int        n_nbr = 0;
    HTAB      *lbl_map;
    int        n_kept = 0;
    int64      min_mu_fp = 0;
    int64      sum_wc = 0;
    StringInfoData buf;

    if (cap <= 0)
        return;

    nbr_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) cap);
    for (int i = 0; i < cap; i++)
    {
        if (!rows[i].neighbour_null)
            nbr_ids[n_nbr++] = rows[i].neighbour_id;
    }
    if (n_nbr == 0)
    {
        pfree(nbr_ids);
        return;
    }

    lbl_map = cf_realize_entities(work, nbr_ids, n_nbr, lang_id);
    pfree(nbr_ids);

    initStringInfo(&buf);
    for (int i = 0; i < cap; i++)
    {
        CfLabelEntry *le;
        int64         mu_fp;

        if (rows[i].neighbour_null)
            continue;
        le = (CfLabelEntry *) hash_search(lbl_map, &rows[i].neighbour_id, HASH_FIND, NULL);
        if (le == NULL || le->content == NULL || le->content[0] == '\0')
            continue;

        mu_fp = eff_mu_display_fp(rows[i].rating, rows[i].rd);
        if (n_kept == 0)
            min_mu_fp = mu_fp;
        else
            min_mu_fp = Min(min_mu_fp, mu_fp);
        sum_wc += rows[i].witness_count;

        if (n_kept > 0)
            appendStringInfoString(&buf, sep);
        appendStringInfoString(&buf, le->content);
        n_kept++;
    }

    if (n_kept > 0)
    {
        Datum sentence_d = (Datum) 0;
        bool  sentence_null = (initcap_cstr == NULL);

        if (!sentence_null)
        {
            char *full = psprintf(prefix_fmt, initcap_cstr, buf.data);
            sentence_d = CStringGetTextDatum(full);
        }
        cf_emit_row(rsinfo, fact_kind,
                    sentence_d, sentence_null,
                    fp_display_numeric(min_mu_fp), false,
                    sum_wc, false,
                    NULL, true,
                    NULL, true);
    }
}

Datum
pg_laplace_converse_facts(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    MemoryContext  caller = CurrentMemoryContext;
    MemoryContext  work, prev;
    Datum          syn_d;
    hash128_t      syn_id;
    hash128_t      lang_id;
    bool           has_lang;
    bool           has_part_limit, has_kin_limit;
    int32          part_limit = 0, kin_limit = 0;

    InitMaterializedSRF(fcinfo, 0);

    if (!laplace_highway_ready())
        ereport(ERROR,
                (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                 errmsg("converse.facts requires the highway perfcache")));

    if (PG_ARGISNULL(0))
        return (Datum) 0;

    has_part_limit = !PG_ARGISNULL(2);
    part_limit = has_part_limit ? PG_GETARG_INT32(2) : 0;
    has_kin_limit = !PG_ARGISNULL(3);
    kin_limit = has_kin_limit ? PG_GETARG_INT32(3) : 0;
    if ((has_part_limit && part_limit < 0) || (has_kin_limit && kin_limit < 0))
        ereport(ERROR,
                (errcode(ERRCODE_RAISE_EXCEPTION),
                 errmsg("converse.facts: listing limits must be >= 0 or NULL for all rows")));

    syn_d = PG_GETARG_DATUM(0);
    syn_id = datum_to_hash128(syn_d);
    has_lang = !PG_ARGISNULL(1);
    if (has_lang)
        lang_id = datum_to_hash128(PG_GETARG_DATUM(1));
    else
        has_lang = laplace_pub_text_id_hash("eng", 3, &lang_id);

    work = AllocSetContextCreate(caller, "converse_facts", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    {
        Oid                b3_arr_oid = laplace_pub_blake3_array_oid();
        const hash128_t   *lang_ptr = has_lang ? &lang_id : NULL;
        HTAB              *ht_def = cf_id_set_create(work, "cf def band", 32);
        HTAB              *ht_part = cf_id_set_create(work, "cf part band", 32);
        HTAB              *ht_assoc = cf_id_set_create(work, "cf assoc band", 64);
        char              *initcap_cstr = NULL;
        ArrayType         *syn_b3;
        LaplaceEdgeRawRow *edges;
        int                n_edges = 0;
        bool               has_tax_type = false;
        const char        *non_kin_fams[4] = {
            "HAS_ETYMOLOGY",
            "HAS_EXAMPLE",
            "HAS_USAGE_REGISTER",
            "HAS_LEX_CATEGORY"
        };

        for (int bit = 0; bit < 256; bit++)
        {
            const char                   *canonical = NULL;
            float                         rank = 0.0f;
            uint8_t                       band = 0;
            hash128_t                     tid;
            const laplace_relation_def_t *def = NULL;
            bool                          found = false;

            if (highway_table_relation_by_bit((uint8_t) bit, &canonical, &rank, &band) != 0)
                continue;
            if (laplace_relation_type_id(canonical, &tid) < 0 ||
                laplace_relation_lookup(&tid, &def) != 0 || def == NULL)
                continue;

            if (band == 1)
                (void) hash_search(ht_def, &tid, HASH_ENTER, &found);
            else if (band == 4)
                (void) hash_search(ht_part, &tid, HASH_ENTER, &found);
            else if (band == 7)
                (void) hash_search(ht_assoc, &tid, HASH_ENTER, &found);
        }

        {
            HTAB         *topic_lbls = cf_realize_entities(work, &syn_id, 1, lang_ptr);
            CfLabelEntry *tle = (CfLabelEntry *) hash_search(topic_lbls, &syn_id, HASH_FIND, NULL);
            if (tle != NULL && tle->content != NULL)
            {
                Datum ic_d = OidFunctionCall1Coll(F_INITCAP, DEFAULT_COLLATION_OID,
                                                  CStringGetTextDatum(tle->content));
                initcap_cstr = TextDatumGetCString(ic_d);
            }
        }

        syn_b3 = hash128_array_from_ids(&syn_id, 1);
        edges = laplace_edges_raw_batch_c(syn_b3, "out", NULL, false, 0, false, "eff_mu", &n_edges);
        pfree(syn_b3);

        /* Pre-classify canonical types across all non-refuted outbound claims */
        if (edges != NULL && n_edges > 0)
        {
            hash128_t         *canon_types = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_edges);
            LaplaceEdgeRawRow *part_edges = (LaplaceEdgeRawRow *) palloc(sizeof(LaplaceEdgeRawRow) * (Size) n_edges);
            int                n_part = 0;
            LaplaceEdgeRawRow *kin_edges = (LaplaceEdgeRawRow *) palloc(sizeof(LaplaceEdgeRawRow) * (Size) n_edges);
            int                n_kin = 0;
            int                best_def = -1;

            for (int i = 0; i < n_edges; i++)
            {
                int in_isa = 0;
                canon_types[i] = cf_resolve_canon_type(&edges[i].type_id);

                if (hash_search(ht_def, &canon_types[i], HASH_FIND, NULL) != NULL)
                {
                    if (best_def < 0 || cf_edge_mu_cmp(&edges[i], &edges[best_def]) < 0)
                        best_def = i;
                }
                if (!has_tax_type &&
                    laplace_relation_in_family(&canon_types[i], "IS_A", &in_isa) == 0 &&
                    in_isa != 0)
                    has_tax_type = true;
                if (hash_search(ht_part, &canon_types[i], HASH_FIND, NULL) != NULL)
                    part_edges[n_part++] = edges[i];
                if (hash_search(ht_assoc, &canon_types[i], HASH_FIND, NULL) != NULL &&
                    !cf_in_family_any(&canon_types[i], non_kin_fams, 4))
                    kin_edges[n_kin++] = edges[i];
            }

            /* ---- 1. definition ---- */
            if (best_def >= 0)
            {
                const LaplaceEdgeRawRow *bd = &edges[best_def];
                const char *obj_cstr = NULL;
                Datum      sent_d = (Datum) 0;
                bool       sent_null = true;
                Datum      p_elems[2];
                bool       p_nulls[2] = { false, bd->neighbour_null };
                int        dims[1] = { 2 };
                int        lbs[1] = { 1 };
                ArrayType *p_arr;
                Datum      t_elem = hash128_to_datum(&canon_types[best_def]);
                ArrayType *t_arr;

                if (!bd->neighbour_null)
                {
                    HTAB         *def_lbls = cf_realize_entities(work, &bd->neighbour_id, 1, lang_ptr);
                    CfLabelEntry *dle = (CfLabelEntry *) hash_search(def_lbls, &bd->neighbour_id, HASH_FIND, NULL);
                    if (dle != NULL && !dle->is_hex && dle->content != NULL && dle->content[0] != '\0')
                        obj_cstr = dle->content;
                }
                if (initcap_cstr != NULL && obj_cstr != NULL)
                {
                    sent_d = CStringGetTextDatum(psprintf("%s is %s.", initcap_cstr, obj_cstr));
                    sent_null = false;
                }

                p_elems[0] = syn_d;
                p_elems[1] = bd->neighbour_null ? (Datum) 0 : hash128_to_datum(&bd->neighbour_id);
                p_arr = construct_md_array(p_elems, p_nulls, 1, dims, lbs,
                                           BYTEAOID, -1, false, TYPALIGN_INT);
                t_arr = construct_array(&t_elem, 1, BYTEAOID, -1, false, TYPALIGN_INT);

                cf_emit_row(rsinfo, "definition",
                            sent_d, sent_null,
                            eff_mu_display_numeric(bd->rating, bd->rd), false,
                            bd->witness_count, false,
                            p_arr, false,
                            t_arr, false);
            }

            /* ---- 2. taxonomy ---- */
            if (has_tax_type)
            {
                Oid        text_oid = TEXTOID;
                Datum      hyp_arg = CStringGetTextDatum("hypernym");
                bool       hyp_null = false, hyp_out_null = false;
                Datum      d_hyp = laplace_pub_call("public", "laplace_text_id",
                                                    1, &text_oid, &hyp_arg, &hyp_null, &hyp_out_null);
                hash128_t  hyp_id;
                ArrayType *seeds_b3 = laplace_pub_blake3_array(&syn_id, 1);
                ArrayType *empty_b3 = laplace_pub_blake3_array(NULL, 0);
                ArrayType *mid_b3;
                CfPullCtx  tctx;

                if (hyp_out_null)
                    elog(ERROR, "converse.facts: hypernym relation id unavailable");
                hyp_id = laplace_pub_blake3_to_hash128(d_hyp);
                mid_b3 = laplace_pub_blake3_array(&hyp_id, 1);

                {
                    Oid p_types[9] = {
                        b3_arr_oid, b3_arr_oid, INT4OID, INT8OID, INT8OID,
                        b3_arr_oid, b3_arr_oid, FLOAT8OID, FLOAT8OID
                    };
                    Datum p_args[9] = {
                        PointerGetDatum(seeds_b3), (Datum) 0, Int32GetDatum(4),
                        Int64GetDatum(3), Int64GetDatum(4096),
                        PointerGetDatum(mid_b3), PointerGetDatum(empty_b3),
                        Float8GetDatum(2.0), Float8GetDatum(0.05)
                    };
                    bool p_nulls[9] = {
                        false, true, false, false, false, false, false, false, false
                    };

                    MemSet(&tctx, 0, sizeof(tctx));
                    tctx.caller = work;
                    (void) laplace_pub_call_srf("public", "laplace_pull", 9,
                                                p_types, p_args, p_nulls,
                                                cf_visit_pull, &tctx);
                }
                pfree(seeds_b3);
                pfree(mid_b3);
                pfree(empty_b3);

                if (tctx.count > 0)
                {
                    int       w_idx = 0;
                    double    w_avg = exp(-tctx.rows[0].cost) / (double) Max(tctx.rows[0].hops, 1);
                    const CfPullRow *win;
                    int       n_steps;
                    hash128_t step_ids[2];
                    int       match_cnt = 0;
                    double    min_emu = 0.0;

                    for (int i = 1; i < tctx.count; i++)
                    {
                        double avg = exp(-tctx.rows[i].cost) / (double) Max(tctx.rows[i].hops, 1);
                        if (tctx.rows[i].hops > tctx.rows[w_idx].hops ||
                            (tctx.rows[i].hops == tctx.rows[w_idx].hops && avg > w_avg))
                        {
                            w_idx = i;
                            w_avg = avg;
                        }
                    }
                    win = &tctx.rows[w_idx];
                    n_steps = Min(win->hops, 2);

                    if (n_steps >= 1 && win->n_chain > n_steps)
                    {
                        for (int s = 0; s < n_steps; s++)
                        {
                            int step_depth = s + 1;
                            step_ids[s] = win->chain[step_depth];
                            for (int bi = 0; bi < tctx.count; bi++)
                            {
                                if (tctx.rows[bi].hops == step_depth &&
                                    hash128_eq(&tctx.rows[bi].id, &step_ids[s]))
                                {
                                    double b_emu = tctx.rows[bi].rating - 2.0 * tctx.rows[bi].deviation;
                                    if (match_cnt == 0 || b_emu < min_emu)
                                        min_emu = b_emu;
                                    match_cnt++;
                                }
                            }
                        }
                    }

                    if (match_cnt > 0)
                    {
                        HTAB          *tax_lbls = cf_realize_entities(work, step_ids, n_steps, lang_ptr);
                        StringInfoData sbuf;
                        Datum          sent_d = (Datum) 0;
                        bool           sent_null = (initcap_cstr == NULL);
                        ArrayType     *win_path_arr = cf_bytea_array_from_ids(win->chain, win->n_chain);
                        ArrayType     *win_types_arr;

                        initStringInfo(&sbuf);
                        if (!sent_null)
                            appendStringInfoString(&sbuf, initcap_cstr);
                        for (int s = 0; s < n_steps; s++)
                        {
                            CfLabelEntry *sle = (CfLabelEntry *)
                                hash_search(tax_lbls, &step_ids[s], HASH_FIND, NULL);
                            if (sle == NULL || sle->content == NULL || sle->content[0] == '\0')
                            {
                                sent_null = true;
                                break;
                            }
                            appendStringInfoString(&sbuf,
                                s == 0 ? " is a kind of " : ", which is a kind of ");
                            appendStringInfoString(&sbuf, sle->content);
                        }
                        if (!sent_null)
                        {
                            appendStringInfoChar(&sbuf, '.');
                            sent_d = CStringGetTextDatum(sbuf.data);
                        }

                        if (win->n_claims > 0)
                        {
                            HASHCTL    pctl;
                            HTAB      *ct_map;
                            CfPathCtx  pctx;
                            ArrayType *cl_b3 = laplace_pub_blake3_array(win->claims, win->n_claims);
                            Datum      cla = PointerGetDatum(cl_b3);
                            bool       cln = false;
                            hash128_t *t_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) win->n_claims);
                            int        n_tids = 0;

                            MemSet(&pctl, 0, sizeof(pctl));
                            pctl.keysize = sizeof(hash128_t);
                            pctl.entrysize = sizeof(CfClaimTypeEntry);
                            pctl.hcxt = work;
                            ct_map = hash_create("cf tax claim types", Max(win->n_claims, 16), &pctl,
                                                 HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

                            pctx.caller = work;
                            pctx.claim_types = ct_map;
                            (void) laplace_pub_call_srf("public", "laplace_paths", 1, &b3_arr_oid,
                                                        &cla, &cln, cf_visit_path, &pctx);
                            pfree(cl_b3);

                            for (int ci = 0; ci < win->n_claims; ci++)
                            {
                                CfClaimTypeEntry *te = (CfClaimTypeEntry *)
                                    hash_search(ct_map, &win->claims[ci], HASH_FIND, NULL);
                                if (te != NULL)
                                    t_ids[n_tids++] = te->type_id;
                            }
                            win_types_arr = cf_bytea_array_from_ids(t_ids, n_tids);
                        }
                        else
                        {
                            win_types_arr = construct_empty_array(BYTEAOID);
                        }

                        cf_emit_row(rsinfo, "taxonomy",
                                    sent_d, sent_null,
                                    DirectFunctionCall1(float8_numeric, Float8GetDatum(min_emu)), false,
                                    0, false,
                                    win_path_arr, false,
                                    win_types_arr, false);
                    }
                }
            }

            /* ---- 3. parts ---- */
            if (n_part > 0)
            {
                if (n_part > 1)
                    qsort(part_edges, (Size) n_part, sizeof(LaplaceEdgeRawRow), cf_edge_mu_cmp);
                cf_emit_listing_fact(work, lang_ptr, rsinfo, "parts", "%s has parts such as %s.", ", ",
                                     initcap_cstr, part_edges, n_part, has_part_limit, part_limit);
            }

            /* ---- 4. kin ---- */
            if (n_kin > 0)
            {
                if (n_kin > 1)
                    qsort(kin_edges, (Size) n_kin, sizeof(LaplaceEdgeRawRow), cf_edge_mu_cmp);
                cf_emit_listing_fact(work, lang_ptr, rsinfo, "kin", "%s is related to %s.", " and ",
                                     initcap_cstr, kin_edges, n_kin, has_kin_limit, kin_limit);
            }
        }

        /* ---- 5. web ---- */
        {
            ArrayType *seeds_b3 = laplace_pub_blake3_array(&syn_id, 1);
            hash128_t  refuse_ids[2];
            int        n_refuse = 0;
            hash128_t  eng_id;
            ArrayType *refuse_b3;
            CfPullCtx  wctx;

            if (laplace_pub_text_id_hash("eng", 3, &eng_id))
                refuse_ids[n_refuse++] = eng_id;
            if (lang_ptr != NULL && (n_refuse == 0 || !hash128_eq(&refuse_ids[0], lang_ptr)))
                refuse_ids[n_refuse++] = *lang_ptr;
            refuse_b3 = laplace_pub_blake3_array(refuse_ids, n_refuse);

            {
                Oid p_types[9] = {
                    b3_arr_oid, b3_arr_oid, INT4OID, INT8OID, INT8OID,
                    b3_arr_oid, b3_arr_oid, FLOAT8OID, FLOAT8OID
                };
                Datum p_args[9] = {
                    PointerGetDatum(seeds_b3), (Datum) 0, Int32GetDatum(3),
                    Int64GetDatum(16), Int64GetDatum(128),
                    (Datum) 0, PointerGetDatum(refuse_b3),
                    Float8GetDatum(2.0), Float8GetDatum(0.05)
                };
                bool p_nulls[9] = {
                    false, true, false, false, false, true, false, false, false
                };

                MemSet(&wctx, 0, sizeof(wctx));
                wctx.caller = work;
                (void) laplace_pub_call_srf("public", "laplace_pull", 9,
                                            p_types, p_args, p_nulls,
                                            cf_visit_pull, &wctx);
            }
            pfree(seeds_b3);
            pfree(refuse_b3);

            if (wctx.count > 0)
            {
                int              w_idx = 0;
                const CfPullRow *win;

                for (int i = 1; i < wctx.count; i++)
                {
                    if (wctx.rows[i].hops > wctx.rows[w_idx].hops ||
                        (wctx.rows[i].hops == wctx.rows[w_idx].hops &&
                         (wctx.rows[i].cost < wctx.rows[w_idx].cost ||
                          (wctx.rows[i].cost == wctx.rows[w_idx].cost &&
                           wctx.rows[i].ordinal < wctx.rows[w_idx].ordinal))))
                        w_idx = i;
                }
                win = &wctx.rows[w_idx];

                if (win->n_chain > 1)
                {
                    int            n_steps = win->n_chain - 1;
                    HASHCTL        ctl;
                    HTAB          *ct_map = NULL;
                    HTAB          *st_map = NULL;
                    HTAB          *lbl_map;
                    HTAB          *rel_lbl_map = NULL;
                    hash128_t     *ent_req;
                    int            n_ent_req = 0;
                    hash128_t     *rel_req;
                    int            n_rel_req = 0;
                    StringInfoData sbuf;
                    bool           have_emu = false;
                    double         min_emu = 0.0;
                    CfLabelEntry  *root_le;

                    if (win->n_claims > 0)
                    {
                        ArrayType  *cl_b3 = laplace_pub_blake3_array(win->claims, win->n_claims);
                        Datum       ca = PointerGetDatum(cl_b3);
                        bool        cn = false;
                        CfPathCtx   pctx;
                        CfStandCtx  sctx;

                        MemSet(&ctl, 0, sizeof(ctl));
                        ctl.keysize = sizeof(hash128_t);
                        ctl.entrysize = sizeof(CfClaimTypeEntry);
                        ctl.hcxt = work;
                        ct_map = hash_create("cf web paths", Max(win->n_claims, 16), &ctl,
                                             HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
                        pctx.caller = work;
                        pctx.claim_types = ct_map;
                        (void) laplace_pub_call_srf("public", "laplace_paths", 1, &b3_arr_oid,
                                                    &ca, &cn, cf_visit_path, &pctx);

                        MemSet(&ctl, 0, sizeof(ctl));
                        ctl.keysize = sizeof(hash128_t);
                        ctl.entrysize = sizeof(CfClaimStandEntry);
                        ctl.hcxt = work;
                        st_map = hash_create("cf web standings", Max(win->n_claims, 16), &ctl,
                                             HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
                        sctx.caller = work;
                        sctx.claim_stand = st_map;
                        (void) laplace_pub_call_srf("public", "laplace_standings", 1, &b3_arr_oid,
                                                    &ca, &cn, cf_visit_standing, &sctx);
                        pfree(cl_b3);
                    }

                    ent_req = (hash128_t *) palloc(sizeof(hash128_t) * (Size) (n_steps + 1));
                    ent_req[n_ent_req++] = syn_id;
                    for (int s = 0; s < n_steps; s++)
                        ent_req[n_ent_req++] = win->chain[s + 1];
                    lbl_map = cf_realize_entities(work, ent_req, n_ent_req, lang_ptr);
                    pfree(ent_req);

                    rel_req = (hash128_t *) palloc(sizeof(hash128_t) * (Size) Max(n_steps, 1));
                    if (ct_map != NULL)
                    {
                        HTAB *rseen = cf_id_set_create(work, "cf web rel req", n_steps + 1);
                        for (int s = 0; s < n_steps && s < win->n_claims; s++)
                        {
                            CfClaimTypeEntry *te = (CfClaimTypeEntry *)
                                hash_search(ct_map, &win->claims[s], HASH_FIND, NULL);
                            if (te != NULL)
                            {
                                bool f = false;
                                (void) hash_search(rseen, &te->type_id, HASH_ENTER, &f);
                                if (!f)
                                    rel_req[n_rel_req++] = te->type_id;
                            }
                        }
                    }

                    MemSet(&ctl, 0, sizeof(ctl));
                    ctl.keysize = sizeof(hash128_t);
                    ctl.entrysize = sizeof(CfLabelEntry);
                    ctl.hcxt = work;
                    rel_lbl_map = hash_create("cf web rel labels", Max(n_rel_req, 16), &ctl,
                                              HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
                    if (n_rel_req > 0)
                    {
                        ArrayType *rl_b3 = laplace_pub_blake3_array(rel_req, n_rel_req);
                        Datum      rla = PointerGetDatum(rl_b3);
                        bool       rln = false;
                        CfLabelCtx rlctx = { work, rel_lbl_map };

                        (void) laplace_pub_call_srf("public", "laplace_labels", 1, &b3_arr_oid,
                                                    &rla, &rln, cf_visit_label, &rlctx);
                        pfree(rl_b3);
                    }
                    pfree(rel_req);

                    initStringInfo(&sbuf);
                    root_le = (CfLabelEntry *) hash_search(lbl_map, &syn_id, HASH_FIND, NULL);
                    appendStringInfoString(&sbuf,
                        (root_le != NULL && root_le->content != NULL) ? root_le->content : "?");

                    for (int s = 0; s < n_steps; s++)
                    {
                        CfLabelEntry *te_lbl = NULL;
                        const char   *rel_str = "?";
                        CfLabelEntry *ee_lbl = (CfLabelEntry *)
                            hash_search(lbl_map, &win->chain[s + 1], HASH_FIND, NULL);

                        if (ct_map != NULL && s < win->n_claims)
                        {
                            CfClaimTypeEntry *te = (CfClaimTypeEntry *)
                                hash_search(ct_map, &win->claims[s], HASH_FIND, NULL);
                            if (te != NULL)
                            {
                                te_lbl = (CfLabelEntry *)
                                    hash_search(rel_lbl_map, &te->type_id, HASH_FIND, NULL);
                                if (te_lbl != NULL && te_lbl->content != NULL && !te_lbl->is_hex)
                                    rel_str = te_lbl->content;
                                else
                                {
                                    const laplace_relation_def_t *rdef = NULL;
                                    if (laplace_relation_lookup(&te->type_id, &rdef) == 0 &&
                                        rdef != NULL && rdef->canonical != NULL)
                                        rel_str = rdef->canonical;
                                }
                            }
                        }
                        if (st_map != NULL && s < win->n_claims)
                        {
                            CfClaimStandEntry *se = (CfClaimStandEntry *)
                                hash_search(st_map, &win->claims[s], HASH_FIND, NULL);
                            if (se != NULL)
                            {
                                if (!have_emu || se->eff_mu < min_emu)
                                {
                                    min_emu = se->eff_mu;
                                    have_emu = true;
                                }
                            }
                        }

                        appendStringInfo(&sbuf, " \xE2\x80\x94%s\xE2\x86\x92 %s",
                            rel_str,
                            (ee_lbl != NULL && ee_lbl->content != NULL) ? ee_lbl->content : "?");
                    }

                    cf_emit_row(rsinfo, "web",
                                CStringGetTextDatum(sbuf.data), false,
                                have_emu ? DirectFunctionCall1(float8_numeric, Float8GetDatum(min_emu)) : (Datum) 0,
                                !have_emu,
                                0, true,
                                NULL, true,
                                NULL, true);
                }
            }
        }
    }

    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    return (Datum) 0;
}
