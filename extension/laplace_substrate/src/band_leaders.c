#include "postgres.h"

#include <string.h>

#include "catalog/pg_type.h"
#include "funcapi.h"
#include "lib/stringinfo.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/json.h"
#include "utils/jsonb.h"

#include "laplace/laplace.h"
#include "laplace/core/attestation_engine.h"
#include "laplace/core/highway_table.h"
#include "laplace/core/relation_law.h"
#include "laplace/core/sql_catalog.h"

#include "consensus_scan.h"
#include "perfcache_native.h"
#include "public_direct.h"
#include "spi_common.h"

typedef struct {
    hash128_t subject, type, object;
    int32 band;
    int64 rating, rd, witnesses;
} BandLeader;

typedef struct {
    hash128_t id;
    uint32    run;
} ClaimVertex;

typedef struct {
    hash128_t    claim;
    double       confidence;
    ClaimVertex *verts;
    int          n_verts;
    char        *traj_hex;
} ClaimLeaderPage;

typedef struct {
    hash128_t id;
    char     *label;
    bool      has_label;
} LabelMapEntry;

typedef struct {
    hash128_t claim;
    hash128_t trunk;
} AttestTrunkKey;

typedef struct {
    hash128_t claim;
    int64     count;
} AttestCountEntry;

typedef struct {
    MemoryContext    caller;
    HTAB            *seen;
    ClaimLeaderPage *items;
    int              count;
    int              cap;
} ClaimLeaderCollectCtx;

typedef struct {
    MemoryContext caller;
    HTAB         *map;
} LabelCollectCtx;

typedef struct {
    HTAB *seen_trunks;
    HTAB *counts;
} AttestCollectCtx;

static int
cmp_band_hash128(const void *a, const void *b)
{
    return memcmp(a, b, sizeof(hash128_t));
}

static int
cmp_claim_leader_page(const void *pa, const void *pb)
{
    const ClaimLeaderPage *a = (const ClaimLeaderPage *) pa;
    const ClaimLeaderPage *b = (const ClaimLeaderPage *) pb;

    if (a->confidence != b->confidence)
        return (a->confidence > b->confidence) ? -1 : 1;
    return memcmp(&a->claim, &b->claim, sizeof(hash128_t));
}

static bool
visit_claim_leader_raw(TupleTableSlot *slot, void *vctx)
{
    ClaimLeaderCollectCtx *ctx = (ClaimLeaderCollectCtx *) vctx;
    bool                   isnull = false;
    Datum                  d_ent, d_path, d_rat, d_dev;
    hash128_t              entity;
    bool                   found = false;
    double                 rating, dev, conf_neg2, conf;
    lp_rating              lr;
    Datum                  wkb_d;
    bool                   wkb_null = false;
    Oid                    geom_oid;
    lp_path                path;
    ClaimLeaderPage       *item;

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    entity = laplace_pub_blake3_to_hash128(d_ent);

    d_rat = slot_getattr(slot, 4, &isnull);
    if (isnull) return true;
    rating = DatumGetFloat8(d_rat);

    d_dev = slot_getattr(slot, 5, &isnull);
    if (isnull) return true;
    dev = DatumGetFloat8(d_dev);

    lr = (lp_rating){ rating, dev, LP_GLICKO_VOLATILITY };
    conf_neg2 = lp_confidence(&lr, -2.0);
    if (isnan(conf_neg2) || conf_neg2 < 0.5)
        return true;

    (void) hash_search(ctx->seen, &entity, HASH_ENTER, &found);
    if (found)
        return true;

    conf = lp_confidence(&lr, 2.0);

    d_path = slot_getattr(slot, 3, &isnull);
    if (isnull) return true;
    path = laplace_pub_geo_path(d_path);

    geom_oid = laplace_pub_geometry_oid();
    wkb_d = laplace_pub_call("public", "st_asewkb", 1, &geom_oid, &d_path, &isnull, &wkb_null);

    {
        MemoryContext prev = MemoryContextSwitchTo(ctx->caller);

        if (ctx->count == ctx->cap)
        {
            int new_cap = ctx->cap == 0 ? 16 : ctx->cap * 2;
            ctx->items = ctx->items == NULL
                ? (ClaimLeaderPage *) palloc(sizeof(ClaimLeaderPage) * (Size) new_cap)
                : (ClaimLeaderPage *) repalloc(ctx->items, sizeof(ClaimLeaderPage) * (Size) new_cap);
            ctx->cap = new_cap;
        }

        item = &ctx->items[ctx->count++];
        item->claim      = entity;
        item->confidence = conf;
        item->n_verts    = (int) path.n;
        item->verts      = path.n > 0 ? (ClaimVertex *) palloc(sizeof(ClaimVertex) * path.n) : NULL;
        for (size_t i = 0; i < path.n; ++i)
        {
            lp_id vid = lp_path_id(path, i);
            memcpy(&item->verts[i].id, vid.b, sizeof(hash128_t));
            item->verts[i].run = lp_path_run(path, i);
        }

        if (!wkb_null)
        {
            static const char hex_chars[] = "0123456789abcdef";
            bytea         *wb = DatumGetByteaPP(wkb_d);
            const uint8   *src = (const uint8 *) VARDATA_ANY(wb);
            int            slen = VARSIZE_ANY_EXHDR(wb);
            char          *dst = (char *) palloc((Size) slen * 2 + 1);

            for (int i = 0; i < slen; ++i)
            {
                dst[2 * i]     = hex_chars[(src[i] >> 4) & 0x0f];
                dst[2 * i + 1] = hex_chars[src[i] & 0x0f];
            }
            dst[slen * 2] = '\0';
            item->traj_hex = dst;
        }
        else
        {
            item->traj_hex = NULL;
        }

        MemoryContextSwitchTo(prev);
    }

    return true;
}

static bool
visit_leader_label(TupleTableSlot *slot, void *vctx)
{
    LabelCollectCtx *ctx = (LabelCollectCtx *) vctx;
    bool             isnull = false;
    Datum            d_id, d_lbl;
    hash128_t        id;
    LabelMapEntry   *le;
    bool             found = false;
    MemoryContext    prev;

    d_id = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    id = laplace_pub_blake3_to_hash128(d_id);

    d_lbl = slot_getattr(slot, 3, &isnull);
    prev = MemoryContextSwitchTo(ctx->caller);
    le = (LabelMapEntry *) hash_search(ctx->map, &id, HASH_ENTER, &found);
    if (!found)
    {
        le->id = id;
        le->has_label = !isnull;
        le->label = isnull ? NULL : TextDatumGetCString(d_lbl);
    }
    MemoryContextSwitchTo(prev);
    return true;
}

static bool
visit_leader_attested(TupleTableSlot *slot, void *vctx)
{
    AttestCollectCtx *ctx = (AttestCollectCtx *) vctx;
    bool              isnull = false;
    Datum             d_claim, d_trunk, d_wit;
    AttestTrunkKey    tk;
    bool              found = false;

    d_wit = slot_getattr(slot, 8, &isnull);
    if (isnull || !DatumGetBool(d_wit))
        return true;

    d_claim = slot_getattr(slot, 1, &isnull);
    if (isnull) return true;
    d_trunk = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;

    tk.claim = laplace_pub_blake3_to_hash128(d_claim);
    tk.trunk = laplace_pub_blake3_to_hash128(d_trunk);

    (void) hash_search(ctx->seen_trunks, &tk, HASH_ENTER, &found);
    if (!found)
    {
        bool              c_found = false;
        AttestCountEntry *ce = (AttestCountEntry *) hash_search(ctx->counts, &tk.claim, HASH_ENTER, &c_found);
        if (!c_found)
            ce->count = 1;
        else
            ce->count++;
    }
    return true;
}

/* The same declared relation selectors over Native claim trajectories. Aliases
 * select an arena; they never replace a claim ID or rewrite its constituents. */
PG_FUNCTION_INFO_V1(pg_laplace_claim_leaders);
PG_FUNCTION_INFO_V1(pg_laplace_leaders_native_page);

typedef struct {
    int32      band;
    hash128_t *s_ids;
    int        n_s;
} BandSelectorGroup;

static void
emit_claim_leader_pages(ReturnSetInfo *result,
                          const int32 *req_bands, int n_req,
                          const int32 *uniq_bands,
                          hash128_t * const *band_sids, const int *band_nsids,
                          int n_uniq, int64 per)
{
    ClaimLeaderPage **band_pages;
    int              *band_counts;
    hash128_t        *all_vids = NULL;
    int               n_vids = 0, cap_vids = 0;
    hash128_t        *all_claims = NULL;
    int               n_claims = 0, cap_claims = 0;
    HTAB             *label_map;
    HTAB             *attest_counts;
    HASHCTL           hctl;
    Datum             claim_bit_datum;
    ArrayType        *bits_arr;
    ArrayType        *empty_b3_arr;
    Oid               b3_arr_oid;
    Oid               claims_types[7];

    band_pages  = (ClaimLeaderPage **) palloc0(sizeof(ClaimLeaderPage *) * (Size) n_uniq);
    band_counts = (int *) palloc0(sizeof(int) * (Size) n_uniq);

    b3_arr_oid   = laplace_pub_blake3_array_oid();
    claims_types[0] = b3_arr_oid;
    claims_types[1] = INT8OID;
    claims_types[2] = INT2ARRAYOID;
    claims_types[3] = b3_arr_oid;
    claims_types[4] = INT2OID;
    claims_types[5] = b3_arr_oid;
    claims_types[6] = INT8OID;

    claim_bit_datum = Int16GetDatum(laplace_pub_bank_bit("kind", "claim"));
    bits_arr        = construct_array(&claim_bit_datum, 1, INT2OID, sizeof(int16), true, TYPALIGN_SHORT);
    empty_b3_arr    = laplace_pub_blake3_array(NULL, 0);

    for (int ui = 0; ui < n_uniq; ++ui)
    {
        if (band_nsids[ui] > 0 && per > 0)
        {
            hash128_t *s_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) band_nsids[ui]);
            int        n_s = band_nsids[ui];
            int        w = 1;
            ArrayType *s_arr;
            Datum      c_args[7];
            bool       c_nulls[7] = { false, false, false, false, false, false, false };
            ClaimLeaderCollectCtx cctx;
            int64      keep;

            memcpy(s_ids, band_sids[ui], sizeof(hash128_t) * (Size) n_s);
            qsort(s_ids, (Size) n_s, sizeof(hash128_t), cmp_band_hash128);
            for (int i = 1; i < n_s; ++i)
                if (memcmp(&s_ids[w - 1], &s_ids[i], sizeof(hash128_t)) != 0)
                    s_ids[w++] = s_ids[i];
            n_s = w;

            s_arr = laplace_pub_blake3_array(s_ids, n_s);
            c_args[0] = PointerGetDatum(s_arr);
            c_args[1] = Int64GetDatum(Max(per * 8, (int64) 64));
            c_args[2] = PointerGetDatum(bits_arr);
            c_args[3] = PointerGetDatum(empty_b3_arr);
            c_args[4] = Int16GetDatum(0);
            c_args[5] = PointerGetDatum(s_arr);
            c_args[6] = Int64GetDatum(0);

            memset(&hctl, 0, sizeof(hctl));
            hctl.keysize   = sizeof(hash128_t);
            hctl.entrysize = sizeof(hash128_t);
            hctl.hcxt      = CurrentMemoryContext;

            memset(&cctx, 0, sizeof(cctx));
            cctx.caller = CurrentMemoryContext;
            cctx.seen = hash_create("claim leaders page", 64, &hctl,
                                    HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

            (void) laplace_pub_call_srf("public", "laplace_claims_each", 7,
                                        claims_types, c_args, c_nulls,
                                        visit_claim_leader_raw, &cctx);
            hash_destroy(cctx.seen);
            pfree(s_arr);
            pfree(s_ids);

            if (cctx.count > 1)
                qsort(cctx.items, (Size) cctx.count, sizeof(ClaimLeaderPage), cmp_claim_leader_page);
            keep = (int64) cctx.count < per ? (int64) cctx.count : per;
            band_pages[ui]  = cctx.items;
            band_counts[ui] = (int) keep;

            for (int k = 0; k < (int) keep; ++k)
            {
                const ClaimLeaderPage *it = &cctx.items[k];
                if (n_claims == cap_claims)
                {
                    cap_claims = cap_claims == 0 ? 32 : cap_claims * 2;
                    all_claims = all_claims == NULL
                        ? (hash128_t *) palloc(sizeof(hash128_t) * (Size) cap_claims)
                        : (hash128_t *) repalloc(all_claims, sizeof(hash128_t) * (Size) cap_claims);
                }
                all_claims[n_claims++] = it->claim;

                for (int vi = 0; vi < it->n_verts; ++vi)
                {
                    if (n_vids == cap_vids)
                    {
                        cap_vids = cap_vids == 0 ? 64 : cap_vids * 2;
                        all_vids = all_vids == NULL
                            ? (hash128_t *) palloc(sizeof(hash128_t) * (Size) cap_vids)
                            : (hash128_t *) repalloc(all_vids, sizeof(hash128_t) * (Size) cap_vids);
                    }
                    all_vids[n_vids++] = it->verts[vi].id;
                }
            }
        }
    }

    pfree(bits_arr);
    pfree(empty_b3_arr);

    memset(&hctl, 0, sizeof(hctl));
    hctl.keysize   = sizeof(hash128_t);
    hctl.entrysize = sizeof(LabelMapEntry);
    hctl.hcxt      = CurrentMemoryContext;
    label_map = hash_create("claim leaders labels", Max(n_vids, 16), &hctl,
                            HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    if (n_vids > 0)
    {
        int             w = 1;
        ArrayType      *vid_arr;
        Datum           l_args[1];
        bool            l_nulls[1] = { false };
        LabelCollectCtx lctx = { CurrentMemoryContext, label_map };

        qsort(all_vids, (Size) n_vids, sizeof(hash128_t), cmp_band_hash128);
        for (int i = 1; i < n_vids; ++i)
            if (memcmp(&all_vids[w - 1], &all_vids[i], sizeof(hash128_t)) != 0)
                all_vids[w++] = all_vids[i];
        n_vids = w;

        vid_arr = laplace_pub_blake3_array(all_vids, n_vids);
        l_args[0] = PointerGetDatum(vid_arr);
        (void) laplace_pub_call_srf("public", "laplace_labels", 1, &b3_arr_oid,
                                    l_args, l_nulls, visit_leader_label, &lctx);
        pfree(vid_arr);
    }

    memset(&hctl, 0, sizeof(hctl));
    hctl.keysize   = sizeof(hash128_t);
    hctl.entrysize = sizeof(AttestCountEntry);
    hctl.hcxt      = CurrentMemoryContext;
    attest_counts = hash_create("claim leaders attestations", Max(n_claims, 16), &hctl,
                                HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    if (n_claims > 0)
    {
        int              w = 1;
        ArrayType       *cl_arr;
        Oid              att_types[2] = { b3_arr_oid, BOOLOID };
        Datum            att_args[2];
        bool             att_nulls[2] = { false, false };
        HASHCTL          tctl;
        HTAB            *seen_trunks;
        AttestCollectCtx actx;

        qsort(all_claims, (Size) n_claims, sizeof(hash128_t), cmp_band_hash128);
        for (int i = 1; i < n_claims; ++i)
            if (memcmp(&all_claims[w - 1], &all_claims[i], sizeof(hash128_t)) != 0)
                all_claims[w++] = all_claims[i];
        n_claims = w;

        memset(&tctl, 0, sizeof(tctl));
        tctl.keysize   = sizeof(AttestTrunkKey);
        tctl.entrysize = sizeof(AttestTrunkKey);
        tctl.hcxt      = CurrentMemoryContext;
        seen_trunks = hash_create("claim leaders trunks", Max(n_claims * 2, 16), &tctl,
                                  HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

        cl_arr = laplace_pub_blake3_array(all_claims, n_claims);
        att_args[0] = PointerGetDatum(cl_arr);
        att_args[1] = BoolGetDatum(false);
        actx.seen_trunks = seen_trunks;
        actx.counts      = attest_counts;

        (void) laplace_pub_call_srf("public", "laplace_attested", 2, att_types,
                                    att_args, att_nulls, visit_leader_attested, &actx);
        hash_destroy(seen_trunks);
        pfree(cl_arr);
    }

    {
        StringInfoData jbuf;
        initStringInfo(&jbuf);

        for (int ri = 0; ri < n_req; ++ri)
        {
            int32 b = req_bands[ri];
            int   ui = -1;

            for (int j = 0; j < n_uniq; ++j)
            {
                if (uniq_bands[j] == b) { ui = j; break; }
            }
            if (ui < 0 || band_counts[ui] == 0)
            {
                Datum vals[6] = { Int32GetDatum(b), 0, 0, 0, 0, 0 };
                bool  nulls[6] = { false, true, true, true, true, true };
                tuplestore_putvalues(result->setResult, result->setDesc, vals, nulls);
                continue;
            }

            for (int k = 0; k < band_counts[ui]; ++k)
            {
                const ClaimLeaderPage *it = &band_pages[ui][k];
                AttestCountEntry      *ac = (AttestCountEntry *) hash_search(
                    attest_counts, &it->claim, HASH_FIND, NULL);
                int                    emitted_verts = 0;
                Datum                  vals[6];
                bool                   nulls[6] = { false, false, false, false, false, false };

                resetStringInfo(&jbuf);
                appendStringInfoChar(&jbuf, '[');
                for (int vi = 0; vi < it->n_verts; ++vi)
                {
                    LabelMapEntry *le = (LabelMapEntry *) hash_search(
                        label_map, &it->verts[vi].id, HASH_FIND, NULL);
                    char           hex[33];

                    if (le == NULL)
                        continue;
                    if (emitted_verts > 0)
                        appendStringInfoChar(&jbuf, ',');
                    emitted_verts++;

                    lp_id_hex((const lp_id *) &it->verts[vi].id, hex);
                    appendStringInfoString(&jbuf, "{\"id\":");
                    escape_json(&jbuf, hex);
                    appendStringInfoString(&jbuf, ",\"label\":");
                    if (le->has_label && le->label != NULL)
                        escape_json(&jbuf, le->label);
                    else
                        appendStringInfoString(&jbuf, "null");
                    appendStringInfo(&jbuf, ",\"run\":%u}", it->verts[vi].run);
                }
                appendStringInfoChar(&jbuf, ']');

                vals[0] = Int32GetDatum(b);
                vals[1] = hash128_to_datum(&it->claim);
                if (emitted_verts > 0)
                    vals[2] = DirectFunctionCall1(jsonb_in, CStringGetDatum(jbuf.data));
                else
                {
                    vals[2] = (Datum) 0;
                    nulls[2] = true;
                }
                vals[3] = Float8GetDatum(it->confidence);
                vals[4] = Int64GetDatum(ac ? ac->count : 0);
                if (it->traj_hex != NULL)
                    vals[5] = CStringGetTextDatum(it->traj_hex);
                else
                {
                    vals[5] = (Datum) 0;
                    nulls[5] = true;
                }

                tuplestore_putvalues(result->setResult, result->setDesc, vals, nulls);
            }
        }
        pfree(jbuf.data);
    }

    hash_destroy(label_map);
    hash_destroy(attest_counts);
    if (all_vids) pfree(all_vids);
    if (all_claims) pfree(all_claims);
}

Datum
pg_laplace_claim_leaders(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *result;
    int32          per;
    Datum         *bands;
    bool          *missing;
    int            count;
    size_t         capacity;
    int32         *req_bands;
    int            n_req = 0;
    int32         *uniq_bands;
    int            n_uniq = 0;
    hash128_t    **band_sids;
    int           *band_nsids;

    InitMaterializedSRF(fcinfo, 0);
    result = (ReturnSetInfo *) fcinfo->resultinfo;
    if (PG_ARGISNULL(0))
        return (Datum) 0;
    per = PG_ARGISNULL(1) ? 5 : PG_GETARG_INT32(1);
    if (per < 1)
        ereport(ERROR, (errmsg("claim leaders: page size must be positive")));

    deconstruct_array(PG_GETARG_ARRAYTYPE_P(0), INT4OID, 4, true, TYPALIGN_INT, &bands, &missing, &count);
    if (!count)
        return (Datum) 0;
    if (!laplace_highway_ready())
        elog(ERROR, "claim leaders: declared relation selectors are not configured");

    capacity = laplace_relation_table_count + laplace_relation_alias_table_count;
    if (capacity > MaxAllocSize / sizeof(hash128_t))
        elog(ERROR, "claim leaders: relation selectors exceed allocation capacity");

    req_bands  = (int32 *) palloc(sizeof(int32) * (Size) count);
    uniq_bands = (int32 *) palloc(sizeof(int32) * (Size) count);
    for (int i = 0; i < count; ++i)
    {
        int32 b;
        bool  seen_b = false;

        CHECK_FOR_INTERRUPTS();
        if (missing[i]) continue;
        b = DatumGetInt32(bands[i]);
        req_bands[n_req++] = b;
        for (int j = 0; j < n_uniq; ++j)
        {
            if (uniq_bands[j] == b) { seen_b = true; break; }
        }
        if (!seen_b)
            uniq_bands[n_uniq++] = b;
    }
    pfree(bands);
    pfree(missing);
    if (n_req == 0)
    {
        pfree(req_bands);
        pfree(uniq_bands);
        return (Datum) 0;
    }

    band_sids  = (hash128_t **) palloc0(sizeof(hash128_t *) * (Size) n_uniq);
    band_nsids = (int *) palloc0(sizeof(int) * (Size) n_uniq);
    for (int ui = 0; ui < n_uniq; ++ui)
    {
        int32      selected = uniq_bands[ui];
        hash128_t *s_ids = (hash128_t *) palloc(sizeof(hash128_t) * Max(capacity, 1));
        int        n_s = 0;

        for (size_t i = 0; i < capacity; ++i)
        {
            const char *surface = i < laplace_relation_table_count
                ? laplace_relation_table[i].canonical
                : laplace_relation_alias_table[i - laplace_relation_table_count].surface;
            hash128_t type;
            uint8_t   actual;
            Oid       text_oid = TEXTOID;
            Datum     txt_d;
            bool      txt_null = false, id_null = false;
            Datum     id_d;

            if (laplace_relation_resolve(surface, &type) != 0 ||
                highway_table_relation_by_hash(&type, NULL, NULL, &actual) != 0 ||
                actual != selected)
                continue;

            txt_d = CStringGetTextDatum(surface);
            id_d = laplace_pub_call("public", "laplace_id", 1, &text_oid, &txt_d, &txt_null, &id_null);
            pfree(DatumGetPointer(txt_d));
            if (!id_null)
                s_ids[n_s++] = laplace_pub_blake3_to_hash128(id_d);
        }
        band_sids[ui] = s_ids;
        band_nsids[ui] = n_s;
    }

    emit_claim_leader_pages(result, req_bands, n_req, uniq_bands,
                            band_sids, band_nsids, n_uniq, (int64) per);

    for (int ui = 0; ui < n_uniq; ++ui)
        if (band_sids[ui] != NULL) pfree(band_sids[ui]);
    pfree(band_sids);
    pfree(band_nsids);
    pfree(req_bands);
    pfree(uniq_bands);
    return (Datum) 0;
}

Datum
pg_laplace_leaders_native_page(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *result;
    Datum         *b_vals, *sb_vals, *sf_vals;
    bool          *b_null, *sb_null, *sf_null;
    int            n_b = 0, n_sb = 0, n_sf = 0, n_sel;
    int64          per;
    int32         *req_bands;
    int            n_req = 0;
    int32         *uniq_bands;
    int            n_uniq = 0;
    hash128_t    **band_sids;
    int           *band_nsids;
    int           *band_caps;

    InitMaterializedSRF(fcinfo, 0);
    result = (ReturnSetInfo *) fcinfo->resultinfo;
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1) || PG_ARGISNULL(2) || PG_ARGISNULL(3))
        return (Datum) 0;

    per = PG_GETARG_INT64(3);
    if (per < 0)
        ereport(ERROR, (errcode(ERRCODE_INVALID_ROW_COUNT_IN_LIMIT_CLAUSE),
                        errmsg("LIMIT must not be negative")));

    deconstruct_array(PG_GETARG_ARRAYTYPE_P(0), INT4OID, 4, true, TYPALIGN_INT, &b_vals, &b_null, &n_b);
    if (n_b == 0)
    {
        pfree(b_vals);
        pfree(b_null);
        return (Datum) 0;
    }

    req_bands  = (int32 *) palloc(sizeof(int32) * (Size) n_b);
    uniq_bands = (int32 *) palloc(sizeof(int32) * (Size) n_b);
    for (int i = 0; i < n_b; ++i)
    {
        int32 b;
        bool  seen_b = false;
        if (b_null[i]) continue;
        b = DatumGetInt32(b_vals[i]);
        req_bands[n_req++] = b;
        for (int j = 0; j < n_uniq; ++j)
        {
            if (uniq_bands[j] == b) { seen_b = true; break; }
        }
        if (!seen_b)
            uniq_bands[n_uniq++] = b;
    }
    pfree(b_vals);
    pfree(b_null);
    if (n_req == 0)
    {
        pfree(req_bands);
        pfree(uniq_bands);
        return (Datum) 0;
    }

    band_sids  = (hash128_t **) palloc0(sizeof(hash128_t *) * (Size) n_uniq);
    band_nsids = (int *) palloc0(sizeof(int) * (Size) n_uniq);
    band_caps  = (int *) palloc0(sizeof(int) * (Size) n_uniq);

    deconstruct_array(PG_GETARG_ARRAYTYPE_P(1), INT4OID, 4, true, TYPALIGN_INT, &sb_vals, &sb_null, &n_sb);
    deconstruct_array(PG_GETARG_ARRAYTYPE_P(2), TEXTOID, -1, false, TYPALIGN_INT, &sf_vals, &sf_null, &n_sf);
    n_sel = Min(n_sb, n_sf);

    for (int i = 0; i < n_sel; ++i)
    {
        int32 sb;
        int   ui = -1;
        Oid   text_oid = TEXTOID;
        bool  id_null = false;
        Datum id_d;

        if (sb_null[i] || sf_null[i])
            continue;
        sb = DatumGetInt32(sb_vals[i]);
        for (int j = 0; j < n_uniq; ++j)
        {
            if (uniq_bands[j] == sb) { ui = j; break; }
        }
        if (ui < 0)
            continue;

        id_d = laplace_pub_call("public", "laplace_id", 1, &text_oid, &sf_vals[i], &sf_null[i], &id_null);
        if (!id_null)
        {
            if (band_nsids[ui] >= band_caps[ui])
            {
                band_caps[ui] = band_caps[ui] == 0 ? 16 : band_caps[ui] * 2;
                band_sids[ui] = band_sids[ui] == NULL
                    ? (hash128_t *) palloc(sizeof(hash128_t) * (Size) band_caps[ui])
                    : (hash128_t *) repalloc(band_sids[ui], sizeof(hash128_t) * (Size) band_caps[ui]);
            }
            band_sids[ui][band_nsids[ui]++] = laplace_pub_blake3_to_hash128(id_d);
        }
    }
    pfree(sb_vals);
    pfree(sb_null);
    pfree(sf_vals);
    pfree(sf_null);

    emit_claim_leader_pages(result, req_bands, n_req, uniq_bands,
                            band_sids, band_nsids, n_uniq, per);

    for (int ui = 0; ui < n_uniq; ++ui)
        if (band_sids[ui] != NULL) pfree(band_sids[ui]);
    pfree(band_sids);
    pfree(band_nsids);
    pfree(band_caps);
    pfree(req_bands);
    pfree(uniq_bands);
    return (Datum) 0;
}

/* For each requested band, the indexed arena read returns its top `per` consensus
 * cells (subject, type, object, rating, RD, witnesses). Every subject and object
 * across all bands is then labelled in one display batch, and each cell is
 * emitted with its effective mu. */
PG_FUNCTION_INFO_V1(pg_laplace_band_leaders);
Datum
pg_laplace_band_leaders(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo, 0);
    ReturnSetInfo *r = (ReturnSetInfo *) fcinfo->resultinfo;
    if (PG_ARGISNULL(0))
        return (Datum) 0;
    int32 per = PG_ARGISNULL(1) ? 5 : PG_GETARG_INT32(1);
    if (per < 1)
        ereport(ERROR, (errmsg("band leaders: page size must be positive")));
    Datum *bands;
    bool  *band_nulls;
    int    n_bands;
    deconstruct_array(PG_GETARG_ARRAYTYPE_P(0), INT4OID, 4, true, TYPALIGN_INT, &bands, &band_nulls, &n_bands);
    if (n_bands == 0)
        return (Datum) 0;

    if (!laplace_highway_ready())
        elog(ERROR, "band leaders: declared relation selectors are not configured");

    BandLeader *rows = NULL;
    Size        count = 0;

    for (int band = 0; band < n_bands; ++band)
    {
        int32               selected;
        hash128_t           type_ids[256];
        int                 n_types = 0;
        LaplaceBandEdgeRow *edges;
        int                 n_edges = 0;

        CHECK_FOR_INTERRUPTS();
        if (band_nulls[band])
            continue;
        selected = DatumGetInt32(bands[band]);

        for (int bit = 0; bit < 256; ++bit)
        {
            const char *canonical = NULL;
            float       rank;
            uint8_t     rec_band;
            hash128_t   type_id;

            if (highway_table_relation_by_bit((uint8_t) bit, &canonical, &rank, &rec_band) != 0)
                continue;
            if ((int32) rec_band != selected)
                continue;
            if (laplace_relation_type_id(canonical, &type_id) < 0)
                continue;
            type_ids[n_types++] = type_id;
        }
        if (n_types == 0)
            continue;

        edges = laplace_consensus_band_edges_scan_c(type_ids, n_types, false, 0, per, &n_edges);
        if (n_edges > 0)
        {
            Size total = count + (Size) n_edges;
            if (total > MaxAllocSize / sizeof(BandLeader) || total > MaxAllocSize / (2 * sizeof(hash128_t)))
                elog(ERROR, "band leaders: selected pages exceed allocation capacity");
            rows = rows ? repalloc(rows, total * sizeof(BandLeader)) : palloc(total * sizeof(BandLeader));
            for (int i = 0; i < n_edges; ++i)
            {
                rows[count++] = (BandLeader){
                    .subject   = edges[i].subject_id,
                    .type      = edges[i].type_id,
                    .object    = edges[i].object_id,
                    .band      = selected,
                    .rating    = edges[i].rating,
                    .rd        = edges[i].rd,
                    .witnesses = edges[i].witness_count
                };
            }
        }
        if (edges != NULL)
            pfree(edges);
    }

    if (count > 0)
    {
        hash128_t      *id_list = (hash128_t *) palloc(2 * count * sizeof(hash128_t));
        ArrayType      *b3_arr;
        Oid             b3_arr_oid = laplace_pub_blake3_array_oid();
        Datum           l_args[1];
        bool            l_nulls[1] = { false };
        HASHCTL         hctl;
        HTAB           *label_map;
        LabelCollectCtx lctx;
        Datum           entity_fallback = CStringGetTextDatum("Entity");

        for (Size i = 0; i < count; ++i)
        {
            id_list[2 * i]     = rows[i].subject;
            id_list[2 * i + 1] = rows[i].object;
        }

        memset(&hctl, 0, sizeof(hctl));
        hctl.keysize   = sizeof(hash128_t);
        hctl.entrysize = sizeof(LabelMapEntry);
        hctl.hcxt      = CurrentMemoryContext;
        label_map = hash_create("band leaders labels", Max((int) count * 2, 16), &hctl,
                                HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
        lctx.map = label_map;

        b3_arr = laplace_pub_blake3_array(id_list, (int) (2 * count));
        l_args[0] = PointerGetDatum(b3_arr);
        (void) laplace_pub_call_srf("public", "laplace_labels", 1, &b3_arr_oid,
                                    l_args, l_nulls, visit_leader_label, &lctx);
        pfree(b3_arr);
        pfree(id_list);

        for (Size i = 0; i < count; ++i)
        {
            LabelMapEntry *se = (LabelMapEntry *) hash_search(label_map, &rows[i].subject, HASH_FIND, NULL);
            LabelMapEntry *oe = (LabelMapEntry *) hash_search(label_map, &rows[i].object, HASH_FIND, NULL);
            Datum          subject_d = (se && se->has_label && se->label)
                ? CStringGetTextDatum(se->label) : entity_fallback;
            Datum          object_d = (oe && oe->has_label && oe->label)
                ? CStringGetTextDatum(oe->label) : entity_fallback;
            const char    *relation = laplace_relation_canonical_for_type_id(&rows[i].type);
            Datum          v[8] = {
                Int32GetDatum(rows[i].band),
                hash128_to_datum(&rows[i].subject),
                subject_d,
                CStringGetTextDatum(relation ? relation : "Relation"),
                hash128_to_datum(&rows[i].object),
                object_d,
                eff_mu_display_numeric(rows[i].rating, rows[i].rd),
                Int64GetDatum(rows[i].witnesses)
            };
            bool           nulls[8] = { false };

            tuplestore_putvalues(r->setResult, r->setDesc, v, nulls);
        }
        hash_destroy(label_map);
    }

    return (Datum) 0;
}
