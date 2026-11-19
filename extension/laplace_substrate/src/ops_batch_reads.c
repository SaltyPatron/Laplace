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
#include "utils/memutils.h"

#include "laplace/core/glicko2.h"
#include "laplace/core/hash128.h"
#include "laplace_pg/executor.h"

#include "consensus_scan.h"
#include "public_direct.h"
#include "relations_internal.h"
#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_laplace_entity_evidence_counts);
PG_FUNCTION_INFO_V1(pg_laplace_native_edges_raw_claims);
PG_FUNCTION_INFO_V1(pg_laplace_native_consensus_cells);
PG_FUNCTION_INFO_V1(pg_laplace_native_classification_values);
PG_FUNCTION_INFO_V1(pg_laplace_native_claim_witness_counts);
PG_FUNCTION_INFO_V1(pg_laplace_generation_metric_vertices);
PG_FUNCTION_INFO_V1(pg_laplace_generation_path_plane_step);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_band_edges_by_types);
PG_FUNCTION_INFO_V1(pg_laplace_couple_topics_native);

static HTAB *
obr_hash_id(MemoryContext ctx, const char *name, Size entry_sz, long init)
{
    HASHCTL ctl;
    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = entry_sz;
    ctl.hcxt = ctx;
    return hash_create(name, Max(init, 16), &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
}

static HTAB *
obr_hash_key(MemoryContext ctx, const char *name, Size key_sz, Size entry_sz, long init)
{
    HASHCTL ctl;
    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = key_sz;
    ctl.entrysize = entry_sz;
    ctl.hcxt = ctx;
    return hash_create(name, Max(init, 16), &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
}

static int
obr_hash128_cmp(const void *a, const void *b)
{
    return memcmp(a, b, sizeof(hash128_t));
}

static int
obr_dedup_ids(const hash128_t *src, int n, hash128_t *dst)
{
    int out = 0;
    if (n <= 0 || src == NULL)
        return 0;
    memcpy(dst, src, sizeof(hash128_t) * (Size) n);
    if (n > 1)
        qsort(dst, (Size) n, sizeof(hash128_t), obr_hash128_cmp);
    dst[out++] = dst[0];
    for (int i = 1; i < n; i++)
    {
        if (!hash128_eq(&dst[out - 1], &dst[i]))
            dst[out++] = dst[i];
    }
    return out;
}

static hash128_t *
obr_bytea_ids_nonnull(ArrayType *arr, int *out_count)
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
    *out_count = valid;
    return ids;
}

static int
obr_expand_path(Datum d_path, lp_id *stack_buf, int stack_cap, lp_id **out_parts)
{
    lp_path path = laplace_pub_geo_path(d_path);
    int     n = (int) lp_path_len(path);
    lp_id  *buf = stack_buf;

    if (n <= 0)
    {
        *out_parts = NULL;
        return 0;
    }
    if (n > stack_cap)
        buf = (lp_id *) palloc(sizeof(lp_id) * (Size) n);
    n = (int) lp_path_expand(path, buf, n);
    if (n <= 0)
    {
        if (buf != stack_buf)
            pfree(buf);
        *out_parts = NULL;
        return 0;
    }
    *out_parts = buf;
    return n;
}

/* ========================================================================
 * 1. ops.entity_evidence_counts(bytea[])
 * ======================================================================== */

typedef struct
{
    int64     ord;
    hash128_t claim;
} EecPair;

typedef struct
{
    hash128_t claim;
    hash128_t trunk;
} EecTrunkKey;

typedef struct
{
    hash128_t claim;
    int64     trunks;
} EecClaimCount;

typedef struct
{
    MemoryContext    caller;
    const hash128_t *inputs;
    const int64     *input_ords;
    int              n_inputs;
    HTAB            *seen_pairs;
    EecPair         *pairs;
    int              n_pairs;
    int              cap_pairs;
    hash128_t       *claims;
    int              n_claims;
    int              cap_claims;
} EecCollectCtx;

static bool
eec_visit_claim(TupleTableSlot *slot, void *opaque)
{
    EecCollectCtx *ctx = (EecCollectCtx *) opaque;
    bool           isnull = false;
    Datum          d_i, d_ent, d_path;
    int64          idx;
    hash128_t      ent;
    lp_path        path;
    lp_id          first_id;
    EecPair        pk;
    bool           found = false;
    MemoryContext  prev;

    d_i = slot_getattr(slot, 1, &isnull);
    if (isnull)
        return true;
    idx = DatumGetInt64(d_i);
    if (idx < 1 || idx > ctx->n_inputs)
        return true;

    d_path = slot_getattr(slot, 3, &isnull);
    if (isnull)
        return true;
    path = laplace_pub_geo_path(d_path);
    if (lp_path_len(path) == 0 || lp_path_expand(path, &first_id, 1) < 1)
        return true;
    if (memcmp(first_id.b, &ctx->inputs[idx - 1], sizeof(hash128_t)) != 0)
        return true;

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;
    ent = laplace_pub_blake3_to_hash128(d_ent);

    prev = MemoryContextSwitchTo(ctx->caller);
    MemSet(&pk, 0, sizeof(pk));
    pk.ord = ctx->input_ords[idx - 1];
    pk.claim = ent;
    (void) hash_search(ctx->seen_pairs, &pk, HASH_ENTER, &found);
    if (!found)
    {
        if (ctx->n_pairs >= ctx->cap_pairs)
        {
            Size sz;
            ctx->cap_pairs = ctx->cap_pairs ? ctx->cap_pairs * 2 : 64;
            sz = sizeof(EecPair) * (Size) ctx->cap_pairs;
            ctx->pairs = ctx->pairs
                ? (EecPair *) repalloc(ctx->pairs, sz)
                : (EecPair *) palloc(sz);
        }
        ctx->pairs[ctx->n_pairs++] = pk;

        if (ctx->n_claims >= ctx->cap_claims)
        {
            Size sz;
            ctx->cap_claims = ctx->cap_claims ? ctx->cap_claims * 2 : 64;
            sz = sizeof(hash128_t) * (Size) ctx->cap_claims;
            ctx->claims = ctx->claims
                ? (hash128_t *) repalloc(ctx->claims, sz)
                : (hash128_t *) palloc(sz);
        }
        ctx->claims[ctx->n_claims++] = ent;
    }
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct
{
    MemoryContext caller;
    HTAB         *seen_trunks;
    HTAB         *counts;
} EecAttestCtx;

static bool
eec_visit_attested(TupleTableSlot *slot, void *opaque)
{
    EecAttestCtx *ctx = (EecAttestCtx *) opaque;
    bool          isnull = false;
    Datum         d_wit, d_claim, d_trunk;
    EecTrunkKey   tk;
    bool          found = false;
    MemoryContext prev;

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
    (void) hash_search(ctx->seen_trunks, &tk, HASH_ENTER, &found);
    if (!found)
    {
        bool           cf = false;
        EecClaimCount *cc = (EecClaimCount *) hash_search(
            ctx->counts, &tk.claim, HASH_ENTER, &cf);
        cc->trunks = cf ? (cc->trunks + 1) : 1;
    }
    MemoryContextSwitchTo(prev);
    return true;
}

Datum
pg_laplace_entity_evidence_counts(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    MemoryContext  caller = CurrentMemoryContext;
    MemoryContext  work, prev;
    Datum         *vals;
    bool          *nulls;
    int            n = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;

    deconstruct_array(PG_GETARG_ARRAYTYPE_P(0), BYTEAOID, -1, false, TYPALIGN_INT,
                      &vals, &nulls, &n);
    if (n == 0)
    {
        pfree(vals);
        pfree(nulls);
        return (Datum) 0;
    }

    work = AllocSetContextCreate(caller, "entity_evidence_counts", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    {
        hash128_t    *req_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n);
        int64        *req_ords = (int64 *) palloc(sizeof(int64) * (Size) n);
        int           n_valid = 0;
        int64        *ord_counts = (int64 *) palloc0(sizeof(int64) * (Size) n);

        for (int i = 0; i < n; i++)
        {
            if (!nulls[i])
            {
                req_ids[n_valid] = datum_to_hash128(vals[i]);
                req_ords[n_valid] = (int64) (i + 1);
                n_valid++;
            }
        }

        if (n_valid > 0)
        {
            Oid           b3_arr_oid = laplace_pub_blake3_array_oid();
            ArrayType    *ids_arr = laplace_pub_blake3_array(req_ids, n_valid);
            ArrayType    *empty_b3 = laplace_pub_blake3_array(NULL, 0);
            Datum         bit_d = Int16GetDatum(laplace_pub_bank_bit("kind", "claim"));
            ArrayType    *bits_arr = construct_array(&bit_d, 1, INT2OID, sizeof(int16), true, TYPALIGN_SHORT);
            Oid           c_types[5] = { b3_arr_oid, INT8OID, INT2ARRAYOID, b3_arr_oid, INT2OID };
            Datum         c_args[5] = {
                PointerGetDatum(ids_arr), (Datum) 0,
                PointerGetDatum(bits_arr), PointerGetDatum(empty_b3), Int16GetDatum(1)
            };
            bool          c_nulls[5] = { false, true, false, false, false };
            EecCollectCtx cctx;

            MemSet(&cctx, 0, sizeof(cctx));
            cctx.caller = work;
            cctx.inputs = req_ids;
            cctx.input_ords = req_ords;
            cctx.n_inputs = n_valid;
            cctx.seen_pairs = obr_hash_key(work, "eec pairs", sizeof(EecPair), sizeof(EecPair), n_valid * 4);

            (void) laplace_pub_call_srf("public", "laplace_claims_each", 5,
                                        c_types, c_args, c_nulls,
                                        eec_visit_claim, &cctx);
            pfree(ids_arr);
            pfree(empty_b3);
            pfree(bits_arr);

            if (cctx.n_claims > 0)
            {
                hash128_t   *uniq_claims = (hash128_t *) palloc(sizeof(hash128_t) * (Size) cctx.n_claims);
                int          n_uniq = obr_dedup_ids(cctx.claims, cctx.n_claims, uniq_claims);
                HTAB        *seen_trunks = obr_hash_key(work, "eec trunks",
                                                        sizeof(EecTrunkKey), sizeof(EecTrunkKey),
                                                        n_uniq * 2);
                HTAB        *claim_cnt = obr_hash_id(work, "eec cnt", sizeof(EecClaimCount), n_uniq);
                ArrayType   *cl_arr = laplace_pub_blake3_array(uniq_claims, n_uniq);
                Oid          a_types[2] = { b3_arr_oid, BOOLOID };
                Datum        a_args[2] = { PointerGetDatum(cl_arr), BoolGetDatum(false) };
                bool         a_nulls[2] = { false, false };
                EecAttestCtx actx = { work, seen_trunks, claim_cnt };

                (void) laplace_pub_call_srf("public", "laplace_attested", 2,
                                            a_types, a_args, a_nulls,
                                            eec_visit_attested, &actx);
                pfree(cl_arr);

                for (int pi = 0; pi < cctx.n_pairs; pi++)
                {
                    EecClaimCount *cc = (EecClaimCount *) hash_search(
                        claim_cnt, &cctx.pairs[pi].claim, HASH_FIND, NULL);
                    if (cc != NULL)
                        ord_counts[cctx.pairs[pi].ord - 1] += cc->trunks;
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            Datum row_v[2] = { Int64GetDatum((int64) (i + 1)), Int64GetDatum(ord_counts[i]) };
            bool  row_n[2] = { false, false };
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, row_v, row_n);
        }
    }

    pfree(vals);
    pfree(nulls);
    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    return (Datum) 0;
}

/* ========================================================================
 * 2. native.edges_raw_claims(bytea[], bytea[], smallint)
 * ======================================================================== */

typedef struct
{
    MemoryContext    caller;
    const hash128_t *keys;
    int              n_keys;
    int16            dir_mode;
    bool             has_type_filter;
    HTAB            *allowed_types;
    ReturnSetInfo   *rsinfo;
} ErcCtx;

static bool
erc_visit_claim(TupleTableSlot *slot, void *opaque)
{
    ErcCtx   *ctx = (ErcCtx *) opaque;
    bool      isnull = false;
    Datum     d_i, d_ent, d_path, d_rat, d_dev, d_vol, d_mat;
    int64     idx, matches = 0;
    double    rating = 0.0, dev = 0.0, vol = 0.0;
    hash128_t subj, entity, type_id, rel_id, neigh;
    lp_id     stack_parts[64];
    lp_id    *parts = NULL;
    int       n_parts;
    bool      starts, ends;
    bool      has_meta = false, m_inv = false;
    hash128_t m_canon = {0, 0};
    bool      is_out;
    Datum     vals[10];
    bool      nulls[10];
    MemoryContext prev;

    d_i = slot_getattr(slot, 1, &isnull);
    if (isnull) return true;
    idx = DatumGetInt64(d_i);
    if (idx < 1 || idx > ctx->n_keys) return true;
    subj = ctx->keys[idx - 1];

    d_path = slot_getattr(slot, 3, &isnull);
    if (isnull) return true;
    n_parts = obr_expand_path(d_path, stack_parts, 64, &parts);
    if (n_parts < 1) return true;

    starts = (memcmp(parts[0].b, &subj, sizeof(hash128_t)) == 0);
    ends = (memcmp(parts[n_parts - 1].b, &subj, sizeof(hash128_t)) == 0);
    if (!starts && !ends)
    {
        if (parts != stack_parts) pfree(parts);
        return true;
    }

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull)
    {
        if (parts != stack_parts) pfree(parts);
        return true;
    }
    entity = laplace_pub_blake3_to_hash128(d_ent);

    if (n_parts == 3)
    {
        memcpy(&rel_id, parts[1].b, sizeof(hash128_t));
        has_meta = laplace_relation_metadata_lookup_c(&rel_id, &m_canon, NULL, &m_inv, NULL);
        type_id = has_meta ? m_canon : rel_id;
    }
    else
    {
        m_inv = false;
        MemSet(&rel_id, 0, sizeof(hash128_t));
        MemSet(&type_id, 0, sizeof(hash128_t));
    }

    if (ctx->dir_mode == 1)
    {
        bool ok = (!m_inv && starts) || (m_inv && n_parts == 3 && ends);
        if (!ok) { if (parts != stack_parts) pfree(parts); return true; }
        is_out = true;
        if (m_inv && n_parts == 3)
            memcpy(&neigh, parts[0].b, sizeof(hash128_t));
        else if (n_parts <= 3)
            memcpy(&neigh, parts[n_parts - 1].b, sizeof(hash128_t));
        else
            neigh = entity;
    }
    else if (ctx->dir_mode == 2)
    {
        bool ok = (!m_inv && ends) || (m_inv && n_parts == 3 && starts);
        if (!ok) { if (parts != stack_parts) pfree(parts); return true; }
        is_out = false;
        if (m_inv && n_parts == 3)
            memcpy(&neigh, parts[2].b, sizeof(hash128_t));
        else
            memcpy(&neigh, parts[0].b, sizeof(hash128_t));
    }
    else
    {
        if (m_inv && n_parts == 3)
        {
            is_out = ends;
            memcpy(&neigh, ends ? parts[0].b : parts[2].b, sizeof(hash128_t));
        }
        else if (starts)
        {
            is_out = true;
            if (n_parts <= 3)
                memcpy(&neigh, parts[n_parts - 1].b, sizeof(hash128_t));
            else
                neigh = entity;
        }
        else
        {
            is_out = false;
            memcpy(&neigh, parts[0].b, sizeof(hash128_t));
        }
        if (!is_out && hash128_eq(&neigh, &subj))
        {
            if (parts != stack_parts) pfree(parts);
            return true;
        }
    }

    if (parts != stack_parts)
        pfree(parts);

    if (ctx->has_type_filter)
    {
        if (n_parts != 3 || hash_search(ctx->allowed_types, &type_id, HASH_FIND, NULL) == NULL)
            return true;
    }

    d_rat = slot_getattr(slot, 4, &isnull);
    if (!isnull) rating = DatumGetFloat8(d_rat);
    d_dev = slot_getattr(slot, 5, &isnull);
    if (!isnull) dev = DatumGetFloat8(d_dev);
    d_vol = slot_getattr(slot, 6, &isnull);
    if (!isnull) vol = DatumGetFloat8(d_vol);
    d_mat = slot_getattr(slot, 7, &isnull);
    if (!isnull) matches = DatumGetInt64(d_mat);

    prev = MemoryContextSwitchTo(ctx->caller);
    MemSet(nulls, 0, sizeof(nulls));
    vals[0] = hash128_to_datum(&subj);
    vals[1] = CStringGetTextDatum(is_out ? "out" : "in");
    if (n_parts == 3)
    {
        vals[2] = hash128_to_datum(&type_id);
        vals[8] = hash128_to_datum(&rel_id);
    }
    else
    {
        nulls[2] = true; vals[2] = (Datum) 0;
        nulls[8] = true; vals[8] = (Datum) 0;
    }
    vals[3] = hash128_to_datum(&neigh);
    vals[4] = Int64GetDatum((int64) llround(rating * 1e9));
    vals[5] = Int64GetDatum((int64) llround(dev * 1e9));
    vals[6] = Int64GetDatum((int64) llround(vol * 1e9));
    vals[7] = Int64GetDatum(matches > 1 ? matches : 1);
    vals[9] = hash128_to_datum(&entity);

    tuplestore_putvalues(ctx->rsinfo->setResult, ctx->rsinfo->setDesc, vals, nulls);
    pfree(DatumGetPointer(vals[0]));
    pfree(DatumGetPointer(vals[1]));
    if (!nulls[2]) pfree(DatumGetPointer(vals[2]));
    pfree(DatumGetPointer(vals[3]));
    if (!nulls[8]) pfree(DatumGetPointer(vals[8]));
    pfree(DatumGetPointer(vals[9]));
    MemoryContextSwitchTo(prev);
    return true;
}

Datum
pg_laplace_native_edges_raw_claims(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    MemoryContext  caller = CurrentMemoryContext;
    MemoryContext  work, prev;
    hash128_t     *raw_subjs, *uniq_subjs;
    int            n_raw = 0, n_keys;
    int16          dir_mode;
    bool           has_types = !PG_ARGISNULL(1);
    hash128_t     *raw_types = NULL;
    int            n_raw_types = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || PG_ARGISNULL(2))
        return (Datum) 0;
    dir_mode = PG_GETARG_INT16(2);

    raw_subjs = obr_bytea_ids_nonnull(PG_GETARG_ARRAYTYPE_P(0), &n_raw);
    if (n_raw == 0)
    {
        if (raw_subjs != NULL) pfree(raw_subjs);
        return (Datum) 0;
    }

    work = AllocSetContextCreate(caller, "native_edges_raw_claims", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    uniq_subjs = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_raw);
    n_keys = obr_dedup_ids(raw_subjs, n_raw, uniq_subjs);

    {
        Oid         b3_arr_oid = laplace_pub_blake3_array_oid();
        ArrayType  *keys_arr = laplace_pub_blake3_array(uniq_subjs, n_keys);
        ArrayType  *empty_b3 = laplace_pub_blake3_array(NULL, 0);
        Datum       bit_d = Int16GetDatum(laplace_pub_bank_bit("kind", "claim"));
        ArrayType  *bits_arr = construct_array(&bit_d, 1, INT2OID, sizeof(int16), true, TYPALIGN_SHORT);
        ArrayType  *b3_types = NULL;
        bool        has_inverse = false;
        HTAB       *allowed = obr_hash_id(work, "erc allowed", sizeof(hash128_t), 32);

        if (has_types)
        {
            raw_types = obr_bytea_ids_nonnull(PG_GETARG_ARRAYTYPE_P(1), &n_raw_types);
            if (n_raw_types == 0)
            {
                b3_types = laplace_pub_blake3_array(NULL, 0);
            }
            else
            {
                int                     n_surf = 0;
                LaplaceRelationSurface *surfs = laplace_relation_surfaces_expand_c(
                    raw_types, n_raw_types, &n_surf);
                hash128_t              *surf_ids = (hash128_t *) palloc(
                    sizeof(hash128_t) * (Size) Max(n_surf, 1));
                hash128_t              *uniq_surfs = (hash128_t *) palloc(
                    sizeof(hash128_t) * (Size) Max(n_surf, 1));
                int                     n_uniq_surfs;

                for (int i = 0; i < n_raw_types; i++)
                    (void) hash_search(allowed, &raw_types[i], HASH_ENTER, NULL);
                for (int i = 0; i < n_surf; i++)
                {
                    surf_ids[i] = surfs[i].surface;
                    if (surfs[i].inverse)
                        has_inverse = true;
                    (void) hash_search(allowed, &surfs[i].canonical, HASH_ENTER, NULL);
                }
                n_uniq_surfs = obr_dedup_ids(surf_ids, n_surf, uniq_surfs);
                b3_types = laplace_pub_blake3_array(uniq_surfs, n_uniq_surfs);
            }
        }

        {
            int16  endpoint = (dir_mode == 3 || has_inverse) ? 3 : (dir_mode == 1 ? 1 : 2);
            Oid    c_types[7] = { b3_arr_oid, INT8OID, INT2ARRAYOID, b3_arr_oid, INT2OID, b3_arr_oid, INT8OID };
            Datum  c_args[7] = {
                PointerGetDatum(keys_arr), (Datum) 0, PointerGetDatum(bits_arr),
                PointerGetDatum(empty_b3), Int16GetDatum(endpoint),
                PointerGetDatum(b3_types), Int64GetDatum(0)
            };
            bool   c_nulls[7] = { false, true, false, false, false, (b3_types == NULL), false };
            ErcCtx ctx = { work, uniq_subjs, n_keys, dir_mode, has_types, allowed, rsinfo };

            (void) laplace_pub_call_srf("public", "laplace_claims_each", 7,
                                        c_types, c_args, c_nulls,
                                        erc_visit_claim, &ctx);
        }
    }

    pfree(raw_subjs);
    if (raw_types != NULL) pfree(raw_types);
    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    return (Datum) 0;
}

/* ========================================================================
 * 3. native.consensus_cells(bytea[], bytea[], bytea[], bool)
 * ======================================================================== */

typedef struct
{
    hash128_t claim;
    hash128_t subj_id;
    hash128_t type_id;
    hash128_t obj_id;
    int64     rating;
    int64     rd;
    int64     volatility;
    int64     witnesses;
    double    standing;
} NccRow;

typedef struct
{
    MemoryContext caller;
    HTAB         *seen_claims;
    HTAB         *subj_set;
    HTAB         *obj_set;
    HTAB         *type_set;
    bool          or_mode;
    NccRow       *rows;
    int           count;
    int           cap;
} NccCtx;

static bool
ncc_visit_claim(TupleTableSlot *slot, void *opaque)
{
    NccCtx   *ctx = (NccCtx *) opaque;
    bool      isnull = false;
    Datum     d_ent, d_path, d_rat, d_dev, d_vol, d_mat;
    hash128_t ent, subj, type_id, obj;
    lp_path   path;
    lp_id     parts[3];
    bool      found = false, has_meta = false, m_inv = false;
    hash128_t m_canon = {0, 0};
    double    raw_r = 0.0, raw_d = 0.0, raw_v = 0.0;
    int64     matches = 0;
    MemoryContext prev;

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    ent = laplace_pub_blake3_to_hash128(d_ent);
    if (hash_search(ctx->seen_claims, &ent, HASH_FIND, NULL) != NULL)
        return true;

    d_path = slot_getattr(slot, 3, &isnull);
    if (isnull) return true;
    path = laplace_pub_geo_path(d_path);
    if (lp_path_len(path) != 3 || lp_path_expand(path, parts, 3) != 3)
        return true;

    {
        hash128_t p0, p1, p2;
        memcpy(&p0, parts[0].b, sizeof(hash128_t));
        memcpy(&p1, parts[1].b, sizeof(hash128_t));
        memcpy(&p2, parts[2].b, sizeof(hash128_t));
        has_meta = laplace_relation_metadata_lookup_c(&p1, &m_canon, NULL, &m_inv, NULL);
        subj = (has_meta && m_inv) ? p2 : p0;
        obj  = (has_meta && m_inv) ? p0 : p2;
        type_id = has_meta ? m_canon : p1;
    }

    if (ctx->or_mode && ctx->subj_set != NULL && ctx->obj_set != NULL)
    {
        if (hash_search(ctx->subj_set, &subj, HASH_FIND, NULL) == NULL &&
            hash_search(ctx->obj_set, &obj, HASH_FIND, NULL) == NULL)
            return true;
    }
    else
    {
        if (ctx->subj_set != NULL && hash_search(ctx->subj_set, &subj, HASH_FIND, NULL) == NULL)
            return true;
        if (ctx->obj_set != NULL && hash_search(ctx->obj_set, &obj, HASH_FIND, NULL) == NULL)
            return true;
    }
    if (ctx->type_set != NULL && hash_search(ctx->type_set, &type_id, HASH_FIND, NULL) == NULL)
        return true;

    d_rat = slot_getattr(slot, 4, &isnull);
    if (!isnull) raw_r = DatumGetFloat8(d_rat);
    d_dev = slot_getattr(slot, 5, &isnull);
    if (!isnull) raw_d = DatumGetFloat8(d_dev);
    d_vol = slot_getattr(slot, 6, &isnull);
    if (!isnull) raw_v = DatumGetFloat8(d_vol);
    d_mat = slot_getattr(slot, 7, &isnull);
    if (!isnull) matches = DatumGetInt64(d_mat);

    prev = MemoryContextSwitchTo(ctx->caller);
    (void) hash_search(ctx->seen_claims, &ent, HASH_ENTER, &found);
    if (!found)
    {
        if (ctx->count >= ctx->cap)
        {
            Size sz;
            ctx->cap = ctx->cap ? ctx->cap * 2 : 64;
            sz = sizeof(NccRow) * (Size) ctx->cap;
            ctx->rows = ctx->rows
                ? (NccRow *) repalloc(ctx->rows, sz)
                : (NccRow *) palloc(sz);
        }
        ctx->rows[ctx->count].claim = ent;
        ctx->rows[ctx->count].subj_id = subj;
        ctx->rows[ctx->count].type_id = type_id;
        ctx->rows[ctx->count].obj_id = obj;
        ctx->rows[ctx->count].rating = (int64) llround(raw_r * 1e9);
        ctx->rows[ctx->count].rd = (int64) llround(raw_d * 1e9);
        ctx->rows[ctx->count].volatility = (int64) llround(raw_v * 1e9);
        ctx->rows[ctx->count].witnesses = matches > 1 ? matches : 1;
        ctx->rows[ctx->count].standing = raw_r - 2.0 * raw_d;
        ctx->count++;
    }
    MemoryContextSwitchTo(prev);
    return true;
}

static int
ncc_cmp(const void *a, const void *b)
{
    const NccRow *x = (const NccRow *) a;
    const NccRow *y = (const NccRow *) b;
    if (x->standing != y->standing)
        return x->standing > y->standing ? -1 : 1;
    return memcmp(&x->claim, &y->claim, sizeof(hash128_t));
}

Datum
pg_laplace_native_consensus_cells(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    MemoryContext  caller = CurrentMemoryContext;
    MemoryContext  work, prev;
    bool           has_s = !PG_ARGISNULL(0);
    bool           has_o = !PG_ARGISNULL(1);
    bool           has_t = !PG_ARGISNULL(2);
    bool           or_mode = PG_ARGISNULL(3) ? false : PG_GETARG_BOOL(3);

    InitMaterializedSRF(fcinfo, 0);
    if (!has_s && !has_o)
        return (Datum) 0;

    work = AllocSetContextCreate(caller, "native_consensus_cells", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    {
        hash128_t *s_raw = NULL, *o_raw = NULL, *t_raw = NULL;
        hash128_t *s_uniq = NULL, *o_uniq = NULL;
        int        ns_raw = 0, no_raw = 0, nt_raw = 0, ns = 0, no = 0;
        HTAB      *subj_set = NULL, *obj_set = NULL, *type_set = NULL;
        bool       has_inverse = !has_t;
        hash128_t *probe_ids, *opp_ids = NULL;
        int        n_probe = 0, n_opp = 0;
        bool       have_opp = false;
        ArrayType *types_b3 = NULL;

        if (has_s)
        {
            s_raw = obr_bytea_ids_nonnull(PG_GETARG_ARRAYTYPE_P(0), &ns_raw);
            if (ns_raw > 0)
            {
                s_uniq = (hash128_t *) palloc(sizeof(hash128_t) * (Size) ns_raw);
                ns = obr_dedup_ids(s_raw, ns_raw, s_uniq);
            }
            subj_set = obr_hash_id(work, "ncc subj", sizeof(hash128_t), Max(ns, 1));
            for (int i = 0; i < ns; i++)
                (void) hash_search(subj_set, &s_uniq[i], HASH_ENTER, NULL);
        }
        if (has_o)
        {
            o_raw = obr_bytea_ids_nonnull(PG_GETARG_ARRAYTYPE_P(1), &no_raw);
            if (no_raw > 0)
            {
                o_uniq = (hash128_t *) palloc(sizeof(hash128_t) * (Size) no_raw);
                no = obr_dedup_ids(o_raw, no_raw, o_uniq);
            }
            obj_set = obr_hash_id(work, "ncc obj", sizeof(hash128_t), Max(no, 1));
            for (int i = 0; i < no; i++)
                (void) hash_search(obj_set, &o_uniq[i], HASH_ENTER, NULL);
        }

        if (has_t)
        {
            t_raw = obr_bytea_ids_nonnull(PG_GETARG_ARRAYTYPE_P(2), &nt_raw);
            type_set = obr_hash_id(work, "ncc type", sizeof(hash128_t), Max(nt_raw, 1));
            if (nt_raw == 0)
            {
                types_b3 = laplace_pub_blake3_array(NULL, 0);
            }
            else
            {
                int                     n_surf = 0;
                LaplaceRelationSurface *surfs = laplace_relation_surfaces_expand_c(t_raw, nt_raw, &n_surf);
                hash128_t              *surf_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) Max(n_surf, 1));
                hash128_t              *uniq_surfs = (hash128_t *) palloc(sizeof(hash128_t) * (Size) Max(n_surf, 1));
                int                     n_uniq;

                for (int i = 0; i < nt_raw; i++)
                    (void) hash_search(type_set, &t_raw[i], HASH_ENTER, NULL);
                for (int i = 0; i < n_surf; i++)
                {
                    surf_ids[i] = surfs[i].surface;
                    if (surfs[i].inverse)
                        has_inverse = true;
                    (void) hash_search(type_set, &surfs[i].canonical, HASH_ENTER, NULL);
                }
                n_uniq = obr_dedup_ids(surf_ids, n_surf, uniq_surfs);
                types_b3 = laplace_pub_blake3_array(uniq_surfs, n_uniq);
            }
        }

        {
            bool so_same = (has_s && has_o && ns == no &&
                            memcmp(s_uniq, o_uniq, sizeof(hash128_t) * (Size) ns) == 0);
            bool pick_o = (has_s && has_o && !or_mode && has_inverse && !so_same && no < ns);
            bool ep_inv;

            if (pick_o)
            {
                probe_ids = o_uniq; n_probe = no;
            }
            else if (has_s)
            {
                probe_ids = s_uniq; n_probe = ns;
            }
            else
            {
                probe_ids = o_uniq; n_probe = no;
            }

            if (!(has_s && has_o) || or_mode || (has_inverse && !so_same))
                have_opp = false;
            else
            {
                have_opp = true;
                opp_ids = pick_o ? s_uniq : o_uniq;
                n_opp = pick_o ? ns : no;
            }

            ep_inv = (or_mode && has_s && has_o) ||
                     (has_inverse && (!(has_s && has_o) || or_mode || !so_same));

            if (n_probe > 0)
            {
                Oid        b3_arr_oid = laplace_pub_blake3_array_oid();
                ArrayType *probe_arr = laplace_pub_blake3_array(probe_ids, n_probe);
                ArrayType *empty_b3 = laplace_pub_blake3_array(NULL, 0);
                ArrayType *opp_arr = have_opp ? laplace_pub_blake3_array(opp_ids, n_opp) : NULL;
                Datum      bit_d = Int16GetDatum(laplace_pub_bank_bit("kind", "claim"));
                ArrayType *bits_arr = construct_array(&bit_d, 1, INT2OID, sizeof(int16), true, TYPALIGN_SHORT);
                int16      endpoint = ep_inv ? 3 : (has_s ? 1 : 2);
                Oid        c_types[8] = {
                    b3_arr_oid, INT8OID, INT2ARRAYOID, b3_arr_oid,
                    INT2OID, b3_arr_oid, INT8OID, b3_arr_oid
                };
                Datum      c_args[8] = {
                    PointerGetDatum(probe_arr), (Datum) 0, PointerGetDatum(bits_arr),
                    PointerGetDatum(empty_b3), Int16GetDatum(endpoint),
                    PointerGetDatum(types_b3), Int64GetDatum(3), PointerGetDatum(opp_arr)
                };
                bool       c_nulls[8] = {
                    false, true, false, false, false,
                    (types_b3 == NULL), false, (opp_arr == NULL)
                };
                NccCtx     nctx;

                MemSet(&nctx, 0, sizeof(nctx));
                nctx.caller = work;
                nctx.seen_claims = obr_hash_id(work, "ncc seen", sizeof(hash128_t), 64);
                nctx.subj_set = subj_set;
                nctx.obj_set = obj_set;
                nctx.type_set = type_set;
                nctx.or_mode = or_mode;

                (void) laplace_pub_call_srf("public", "laplace_claims_each", 8,
                                            c_types, c_args, c_nulls,
                                            ncc_visit_claim, &nctx);
                if (nctx.count > 1)
                    qsort(nctx.rows, (Size) nctx.count, sizeof(NccRow), ncc_cmp);
                for (int i = 0; i < nctx.count; i++)
                {
                    const NccRow *r = &nctx.rows[i];
                    Datum vals[8] = {
                        hash128_to_datum(&r->subj_id),
                        hash128_to_datum(&r->type_id),
                        hash128_to_datum(&r->obj_id),
                        Int64GetDatum(r->rating),
                        Int64GetDatum(r->rd),
                        Int64GetDatum(r->volatility),
                        Int64GetDatum(r->witnesses),
                        hash128_to_datum(&r->claim)
                    };
                    bool nulls[8] = { false, false, false, false, false, false, false, false };
                    tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
                    pfree(DatumGetPointer(vals[0]));
                    pfree(DatumGetPointer(vals[1]));
                    pfree(DatumGetPointer(vals[2]));
                    pfree(DatumGetPointer(vals[7]));
                }
            }
        }
    }

    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    return (Datum) 0;
}

/* ========================================================================
 * 4. native.classification_values(bytea[], text)
 * ======================================================================== */

static void
ncv_consume(const LaplaceClassificationRow *row, void *opaque)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) opaque;
    Datum vals[7] = {
        hash128_to_datum(&row->subject),
        hash128_to_datum(&row->value),
        Int64GetDatum(row->rating),
        Int64GetDatum(row->rd),
        Int64GetDatum(row->volatility),
        Int64GetDatum(row->games),
        hash128_to_datum(&row->claim)
    };
    bool nulls[7] = { false, false, false, false, false, false, false };
    tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
    pfree(DatumGetPointer(vals[0]));
    pfree(DatumGetPointer(vals[1]));
    pfree(DatumGetPointer(vals[6]));
}

Datum
pg_laplace_native_classification_values(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    hash128_t     *raw, *uniq;
    int            n_raw = 0, n_uniq;
    ArrayType     *subj_arr;
    char          *bank;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        return (Datum) 0;

    raw = obr_bytea_ids_nonnull(PG_GETARG_ARRAYTYPE_P(0), &n_raw);
    if (n_raw == 0)
    {
        if (raw != NULL) pfree(raw);
        return (Datum) 0;
    }

    uniq = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_raw);
    n_uniq = obr_dedup_ids(raw, n_raw, uniq);
    pfree(raw);
    subj_arr = hash128_array_from_ids(uniq, n_uniq);
    pfree(uniq);

    bank = text_to_cstring(PG_GETARG_TEXT_PP(1));
    laplace_classification_scan(subj_arr, bank, ncv_consume, rsinfo);
    pfree(bank);
    pfree(subj_arr);
    return (Datum) 0;
}

/* ========================================================================
 * 5. native.claim_witness_counts(bytea[])
 * ======================================================================== */

Datum
pg_laplace_native_claim_witness_counts(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum         *vals;
    bool          *nulls;
    int            n = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;
    deconstruct_array(PG_GETARG_ARRAYTYPE_P(0), BYTEAOID, -1, false, TYPALIGN_INT, &vals, &nulls, &n);
    if (n == 0)
    {
        pfree(vals);
        pfree(nulls);
        return (Datum) 0;
    }

    {
        hash128_t *claims = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n);
        int64     *counts = (int64 *) palloc0(sizeof(int64) * (Size) n);
        int       *map = (int *) palloc(sizeof(int) * (Size) n);
        int        n_valid = 0;

        for (int i = 0; i < n; i++)
        {
            if (nulls[i])
                map[i] = -1;
            else
            {
                map[i] = n_valid;
                claims[n_valid++] = datum_to_hash128(vals[i]);
            }
        }
        if (n_valid > 0)
        {
            int64 *vc = (int64 *) palloc0(sizeof(int64) * (Size) n_valid);
            laplace_consensus_witness_counts(claims, n_valid, vc);
            for (int i = 0; i < n; i++)
            {
                if (map[i] >= 0)
                    counts[i] = vc[map[i]];
            }
            pfree(vc);
        }

        for (int i = 0; i < n; i++)
        {
            Datum rv[2] = { Int64GetDatum((int64) (i + 1)), Int64GetDatum(counts[i]) };
            bool  rn[2] = { false, false };
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, rv, rn);
        }
        pfree(claims);
        pfree(counts);
        pfree(map);
    }

    pfree(vals);
    pfree(nulls);
    return (Datum) 0;
}

/* ========================================================================
 * 6. generation.metric_vertices(bytea[], bool)
 * ======================================================================== */

typedef struct
{
    hash128_t id;
    double    xyzm[4];
} GmvEntity;

typedef struct
{
    hash128_t entity;
    Datum     curve_wkb;
} GmvCurve;

typedef struct
{
    MemoryContext caller;
    GmvEntity    *items;
    int           count;
    int           cap;
} GmvEntCtx;

typedef struct
{
    MemoryContext caller;
    HTAB         *curves;
} GmvCurveCtx;

static bool
gmv_visit_entity(TupleTableSlot *slot, void *opaque)
{
    GmvEntCtx *ctx = (GmvEntCtx *) opaque;
    bool       isnull = false;
    Datum      d_id, d_coord;
    double     xyzm[4];
    MemoryContext prev;

    d_coord = slot_getattr(slot, 4, &isnull);
    if (isnull || !laplace_pub_geo_point4d(d_coord, xyzm))
        return true;
    d_id = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count >= ctx->cap)
    {
        Size sz;
        ctx->cap = ctx->cap ? ctx->cap * 2 : 64;
        sz = sizeof(GmvEntity) * (Size) ctx->cap;
        ctx->items = ctx->items ? (GmvEntity *) repalloc(ctx->items, sz) : (GmvEntity *) palloc(sz);
    }
    ctx->items[ctx->count].id = laplace_pub_blake3_to_hash128(d_id);
    memcpy(ctx->items[ctx->count].xyzm, xyzm, sizeof(xyzm));
    ctx->count++;
    MemoryContextSwitchTo(prev);
    return true;
}

static bool
gmv_visit_curve(TupleTableSlot *slot, void *opaque)
{
    GmvCurveCtx *ctx = (GmvCurveCtx *) opaque;
    bool         isnull = false;
    Datum        d_ent, d_wkb, wkb_cp;
    hash128_t    ent;
    GmvCurve    *ce;
    bool         found = false;
    MemoryContext prev;

    d_wkb = slot_getattr(slot, 3, &isnull);
    if (isnull)
        return true;
    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;
    ent = laplace_pub_blake3_to_hash128(d_ent);

    prev = MemoryContextSwitchTo(ctx->caller);
    wkb_cp = PointerGetDatum(PG_DETOAST_DATUM_COPY(d_wkb));
    ce = (GmvCurve *) hash_search(ctx->curves, &ent, HASH_ENTER, &found);
    if (!found)
        ce->curve_wkb = wkb_cp;
    else
        pfree(DatumGetPointer(wkb_cp));
    MemoryContextSwitchTo(prev);
    return true;
}

Datum
pg_laplace_generation_metric_vertices(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    MemoryContext  caller = CurrentMemoryContext;
    MemoryContext  work, prev;
    hash128_t     *raw, *uniq;
    int            n_raw = 0, n_uniq;
    bool           inc_curves = PG_ARGISNULL(1) ? false : PG_GETARG_BOOL(1);

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;
    raw = obr_bytea_ids_nonnull(PG_GETARG_ARRAYTYPE_P(0), &n_raw);
    if (n_raw == 0)
    {
        if (raw != NULL) pfree(raw);
        return (Datum) 0;
    }

    work = AllocSetContextCreate(caller, "generation_metric_vertices", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    uniq = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_raw);
    n_uniq = obr_dedup_ids(raw, n_raw, uniq);

    {
        Oid        b3_arr_oid = laplace_pub_blake3_array_oid();
        ArrayType *ids_arr = laplace_pub_blake3_array(uniq, n_uniq);
        Datum      arg = PointerGetDatum(ids_arr);
        bool       argnull = false;
        GmvEntCtx  ectx;
        HTAB      *curves = NULL;

        MemSet(&ectx, 0, sizeof(ectx));
        ectx.caller = work;
        (void) laplace_pub_call_srf("public", "laplace_entities", 1,
                                    &b3_arr_oid, &arg, &argnull,
                                    gmv_visit_entity, &ectx);

        if (inc_curves && ectx.count > 0)
        {
            GmvCurveCtx cctx;
            curves = obr_hash_id(work, "gmv curves", sizeof(GmvCurve), n_uniq);
            cctx.caller = work;
            cctx.curves = curves;
            (void) laplace_pub_call_srf("public", "laplace_curves", 1,
                                        &b3_arr_oid, &arg, &argnull,
                                        gmv_visit_curve, &cctx);
        }
        pfree(ids_arr);

        for (int i = 0; i < ectx.count; i++)
        {
            const GmvEntity *e = &ectx.items[i];
            GmvCurve        *ce = curves
                ? (GmvCurve *) hash_search(curves, &e->id, HASH_FIND, NULL) : NULL;
            Datum vals[6] = {
                hash128_to_datum(&e->id),
                Float8GetDatum(e->xyzm[0]),
                Float8GetDatum(e->xyzm[1]),
                Float8GetDatum(e->xyzm[2]),
                Float8GetDatum(e->xyzm[3]),
                ce ? ce->curve_wkb : (Datum) 0
            };
            bool nulls[6] = { false, false, false, false, false, (ce == NULL) };
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
            pfree(DatumGetPointer(vals[0]));
        }
    }

    pfree(raw);
    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    return (Datum) 0;
}

/* ========================================================================
 * 7. generation.path_plane_step(bytea[], text[], bool, bytea[])
 * ======================================================================== */

typedef struct
{
    int64     i;
    hash128_t claim;
    hash128_t start_id;
    hash128_t end_id;
    int       n_parts;
    double    rating;
    double    deviation;
} PpsClaim;

typedef struct
{
    MemoryContext    caller;
    const hash128_t *frontier;
    int              n_frontier;
    bool             reverse;
    PpsClaim        *items;
    int              count;
    int              cap;
    hash128_t       *claims;
    int              n_claims;
} PpsClaimCtx;

static bool
pps_visit_claim(TupleTableSlot *slot, void *opaque)
{
    PpsClaimCtx *ctx = (PpsClaimCtx *) opaque;
    bool         isnull = false;
    Datum        d_i, d_ent, d_path, d_rat, d_dev;
    int64        idx;
    lp_path      path;
    lp_id        parts[3];
    hash128_t    p0, p2;
    MemoryContext prev;

    d_i = slot_getattr(slot, 1, &isnull);
    if (isnull) return true;
    idx = DatumGetInt64(d_i);
    if (idx < 1 || idx > ctx->n_frontier) return true;

    d_path = slot_getattr(slot, 3, &isnull);
    if (isnull) return true;
    path = laplace_pub_geo_path(d_path);
    if (lp_path_len(path) != 3 || lp_path_expand(path, parts, 3) != 3)
        return true;

    memcpy(&p0, parts[0].b, sizeof(hash128_t));
    memcpy(&p2, parts[2].b, sizeof(hash128_t));
    if (ctx->reverse)
    {
        if (!hash128_eq(&p2, &ctx->frontier[idx - 1]))
            return true;
    }
    else
    {
        if (!hash128_eq(&p0, &ctx->frontier[idx - 1]))
            return true;
    }

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;

    d_rat = slot_getattr(slot, 4, &isnull);
    double rval = isnull ? 0.0 : DatumGetFloat8(d_rat);
    d_dev = slot_getattr(slot, 5, &isnull);
    double dval = isnull ? 0.0 : DatumGetFloat8(d_dev);

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count >= ctx->cap)
    {
        Size sz;
        ctx->cap = ctx->cap ? ctx->cap * 2 : 64;
        sz = sizeof(PpsClaim) * (Size) ctx->cap;
        ctx->items = ctx->items ? (PpsClaim *) repalloc(ctx->items, sz) : (PpsClaim *) palloc(sz);
        ctx->claims = ctx->claims
            ? (hash128_t *) repalloc(ctx->claims, sizeof(hash128_t) * (Size) ctx->cap)
            : (hash128_t *) palloc(sizeof(hash128_t) * (Size) ctx->cap);
    }
    ctx->items[ctx->count].i = idx;
    ctx->items[ctx->count].claim = laplace_pub_blake3_to_hash128(d_ent);
    ctx->items[ctx->count].start_id = p0;
    ctx->items[ctx->count].end_id = p2;
    ctx->items[ctx->count].n_parts = 3;
    ctx->items[ctx->count].rating = rval;
    ctx->items[ctx->count].deviation = dval;
    ctx->claims[ctx->n_claims++] = ctx->items[ctx->count].claim;
    ctx->count++;
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct
{
    hash128_t claim;
    double    rating;
    double    deviation;
} PpsScopedEntry;

typedef struct
{
    MemoryContext caller;
    HTAB         *scoped;
} PpsScopedCtx;

static bool
pps_visit_scoped(TupleTableSlot *slot, void *opaque)
{
    PpsScopedCtx   *ctx = (PpsScopedCtx *) opaque;
    bool            isnull = false;
    Datum           d_cl, d_rat, d_dev;
    hash128_t       cl;
    PpsScopedEntry *se;
    bool            found = false;
    MemoryContext   prev;

    d_cl = slot_getattr(slot, 1, &isnull);
    if (isnull) return true;
    cl = laplace_pub_blake3_to_hash128(d_cl);
    d_rat = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    d_dev = slot_getattr(slot, 3, &isnull);
    if (isnull) return true;

    prev = MemoryContextSwitchTo(ctx->caller);
    se = (PpsScopedEntry *) hash_search(ctx->scoped, &cl, HASH_ENTER, &found);
    if (!found)
    {
        se->rating = DatumGetFloat8(d_rat);
        se->deviation = DatumGetFloat8(d_dev);
    }
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct
{
    hash128_t claim;
    int       first;
} PpsPrevHead;

typedef struct
{
    hash128_t predicate;
    hash128_t object;
    char     *type_label;
    int       next;
} PpsPrevRow;

typedef struct
{
    MemoryContext caller;
    HTAB         *heads;
    PpsPrevRow   *rows;
    int           count;
    int           cap;
} PpsPrevCtx;

static bool
pps_visit_preview(TupleTableSlot *slot, void *opaque)
{
    PpsPrevCtx   *ctx = (PpsPrevCtx *) opaque;
    bool          isnull = false;
    Datum         d_ent, d_pred, d_obj, d_lbl;
    hash128_t     ent;
    char         *lbl = NULL;
    PpsPrevHead  *hd;
    bool          found = false;
    MemoryContext prev;

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    ent = laplace_pub_blake3_to_hash128(d_ent);

    d_pred = slot_getattr(slot, 3, &isnull);
    if (isnull) return true;
    d_obj = slot_getattr(slot, 4, &isnull);
    if (isnull) return true;
    d_lbl = slot_getattr(slot, 5, &isnull);
    if (!isnull)
        lbl = TextDatumGetCString(d_lbl);

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count >= ctx->cap)
    {
        Size sz;
        ctx->cap = ctx->cap ? ctx->cap * 2 : 64;
        sz = sizeof(PpsPrevRow) * (Size) ctx->cap;
        ctx->rows = ctx->rows ? (PpsPrevRow *) repalloc(ctx->rows, sz) : (PpsPrevRow *) palloc(sz);
    }
    ctx->rows[ctx->count].predicate = laplace_pub_blake3_to_hash128(d_pred);
    ctx->rows[ctx->count].object = laplace_pub_blake3_to_hash128(d_obj);
    ctx->rows[ctx->count].type_label = lbl ? pstrdup(lbl) : NULL;
    hd = (PpsPrevHead *) hash_search(ctx->heads, &ent, HASH_ENTER, &found);
    ctx->rows[ctx->count].next = found ? hd->first : -1;
    hd->first = ctx->count++;
    MemoryContextSwitchTo(prev);

    if (lbl != NULL) pfree(lbl);
    return true;
}

Datum
pg_laplace_generation_path_plane_step(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    MemoryContext  caller = CurrentMemoryContext;
    MemoryContext  work, prev;
    hash128_t     *front_ids;
    int            n_front = 0;
    bool           rev;
    bool           has_sources = !PG_ARGISNULL(3);

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1) || PG_ARGISNULL(2))
        return (Datum) 0;
    rev = PG_GETARG_BOOL(2);

    front_ids = obr_bytea_ids_nonnull(PG_GETARG_ARRAYTYPE_P(0), &n_front);
    if (n_front == 0)
    {
        if (front_ids != NULL) pfree(front_ids);
        return (Datum) 0;
    }

    work = AllocSetContextCreate(caller, "generation_path_plane_step", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    {
        HASHCTL ctl;
        HTAB   *rel_id_set = obr_hash_id(work, "pps tids", sizeof(hash128_t), 32);
        HTAB   *rel_name_set;
        Datum  *rvals;
        bool   *rnulls;
        int     nr = 0;

        MemSet(&ctl, 0, sizeof(ctl));
        ctl.keysize = 64;
        ctl.entrysize = 64;
        ctl.hcxt = work;
        rel_name_set = hash_create("pps rnames", 32, &ctl, HASH_ELEM | HASH_STRINGS | HASH_CONTEXT);

        deconstruct_array(PG_GETARG_ARRAYTYPE_P(1), TEXTOID, -1, false, TYPALIGN_INT, &rvals, &rnulls, &nr);
        for (int i = 0; i < nr; i++)
        {
            char     *s;
            hash128_t tid;
            if (rnulls[i])
                continue;
            s = TextDatumGetCString(rvals[i]);
            if (laplace_pub_text_id_hash(s, strlen(s), &tid))
                (void) hash_search(rel_id_set, &tid, HASH_ENTER, NULL);
            if (strlen(s) < 64)
                (void) hash_search(rel_name_set, s, HASH_ENTER, NULL);
            pfree(s);
        }
        pfree(rvals);
        pfree(rnulls);

        {
            Oid         b3_arr_oid = laplace_pub_blake3_array_oid();
            ArrayType  *front_arr = laplace_pub_blake3_array(front_ids, n_front);
            ArrayType  *empty_b3 = laplace_pub_blake3_array(NULL, 0);
            ArrayType  *empty_i2 = construct_empty_array(INT2OID);
            Oid         c_types[5] = { b3_arr_oid, INT8OID, INT2ARRAYOID, b3_arr_oid, INT2OID };
            Datum       c_args[5] = {
                PointerGetDatum(front_arr), (Datum) 0, PointerGetDatum(empty_i2),
                PointerGetDatum(empty_b3), Int16GetDatum(rev ? 2 : 1)
            };
            bool        c_nulls[5] = { false, true, false, false, false };
            PpsClaimCtx cctx;

            MemSet(&cctx, 0, sizeof(cctx));
            cctx.caller = work;
            cctx.frontier = front_ids;
            cctx.n_frontier = n_front;
            cctx.reverse = rev;

            (void) laplace_pub_call_srf("public", "laplace_claims_each", 5,
                                        c_types, c_args, c_nulls,
                                        pps_visit_claim, &cctx);
            pfree(front_arr);
            pfree(empty_b3);
            pfree(empty_i2);

            if (cctx.count > 0)
            {
                hash128_t  *uniq_cl = (hash128_t *) palloc(sizeof(hash128_t) * (Size) cctx.n_claims);
                int         n_uniq_cl = obr_dedup_ids(cctx.claims, cctx.n_claims, uniq_cl);
                ArrayType  *cl_arr = laplace_pub_blake3_array(uniq_cl, n_uniq_cl);
                HTAB       *scoped_map = NULL;
                PpsPrevCtx  pctx;

                if (has_sources)
                {
                    int        n_src = 0;
                    hash128_t *src_ids = obr_bytea_ids_nonnull(PG_GETARG_ARRAYTYPE_P(3), &n_src);
                    ArrayType *src_arr = laplace_pub_blake3_array(src_ids, n_src);
                    Oid        s_types[2] = { b3_arr_oid, b3_arr_oid };
                    Datum      s_args[2] = { PointerGetDatum(cl_arr), PointerGetDatum(src_arr) };
                    bool       s_nulls[2] = { false, false };
                    PpsScopedCtx sctx;

                    scoped_map = obr_hash_id(work, "pps scoped", sizeof(PpsScopedEntry), n_uniq_cl);
                    sctx.caller = work;
                    sctx.scoped = scoped_map;
                    (void) laplace_pub_call_srf("public", "laplace_source_standings", 2,
                                                s_types, s_args, s_nulls,
                                                pps_visit_scoped, &sctx);
                    pfree(src_arr);
                    if (src_ids) pfree(src_ids);
                }

                MemSet(&pctx, 0, sizeof(pctx));
                pctx.caller = work;
                pctx.heads = obr_hash_id(work, "pps pheads", sizeof(PpsPrevHead), n_uniq_cl);
                {
                    Datum p_arg = PointerGetDatum(cl_arr);
                    bool  p_null = false;
                    (void) laplace_pub_call_srf("public", "laplace_claim_preview", 1,
                                                &b3_arr_oid, &p_arg, &p_null,
                                                pps_visit_preview, &pctx);
                }
                pfree(cl_arr);

                for (int ci = 0; ci < cctx.count; ci++)
                {
                    const PpsClaim *c = &cctx.items[ci];
                    double          eff_r = c->rating, eff_d = c->deviation;
                    PpsPrevHead    *hd;

                    if (has_sources)
                    {
                        PpsScopedEntry *se = (PpsScopedEntry *) hash_search(
                            scoped_map, &c->claim, HASH_FIND, NULL);
                        if (se == NULL)
                            continue;
                        eff_r = se->rating;
                        eff_d = se->deviation;
                    }

                    hd = (PpsPrevHead *) hash_search(pctx.heads, &c->claim, HASH_FIND, NULL);
                    if (hd == NULL)
                        continue;

                    for (int pi = hd->first; pi >= 0; pi = pctx.rows[pi].next)
                    {
                        const PpsPrevRow *pr = &pctx.rows[pi];
                        bool              rel_ok = false;

                        if (hash_search(rel_id_set, &pr->predicate, HASH_FIND, NULL) != NULL)
                            rel_ok = true;
                        else if (pr->type_label != NULL && strlen(pr->type_label) < 64 &&
                                 hash_search(rel_name_set, pr->type_label, HASH_FIND, NULL) != NULL)
                            rel_ok = true;
                        else
                        {
                            const char *m_canon = NULL;
                            bool        m_inv = false;
                            int16       m_qual = -1;
                            if (laplace_relation_metadata_lookup_c(&pr->predicate, NULL, &m_canon,
                                                                   &m_inv, &m_qual) &&
                                !m_inv && m_qual < 0 && m_canon != NULL && strlen(m_canon) < 64 &&
                                hash_search(rel_name_set, m_canon, HASH_FIND, NULL) != NULL)
                                rel_ok = true;
                        }
                        if (!rel_ok)
                            continue;

                        {
                            hash128_t next_id = rev ? c->start_id : pr->object;
                            double    w = laplace_walk_edge_weight((int64) llround(eff_r * 1e9),
                                                                   (int64) llround(eff_d * 1e9));
                            Datum vals[9] = {
                                hash128_to_datum(&front_ids[c->i - 1]),
                                hash128_to_datum(&next_id),
                                Float8GetDatum(w),
                                hash128_to_datum(&c->claim),
                                hash128_to_datum(&c->start_id),
                                hash128_to_datum(&pr->predicate),
                                hash128_to_datum(&pr->object),
                                Float8GetDatum(eff_r),
                                Float8GetDatum(eff_d)
                            };
                            bool nulls[9] = { false, false, false, false, false, false, false, false, false };

                            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
                            pfree(DatumGetPointer(vals[0]));
                            pfree(DatumGetPointer(vals[1]));
                            pfree(DatumGetPointer(vals[3]));
                            pfree(DatumGetPointer(vals[4]));
                            pfree(DatumGetPointer(vals[5]));
                            pfree(DatumGetPointer(vals[6]));
                        }
                    }
                }
            }
        }
    }

    pfree(front_ids);
    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
    return (Datum) 0;
}

/* ========================================================================
 * 8. consensus.band_edges(bytea[], int8, int8)
 * ======================================================================== */

Datum
pg_laplace_consensus_band_edges_by_types(PG_FUNCTION_ARGS)
{
    ReturnSetInfo      *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    hash128_t          *types;
    int                 n_types = 0;
    bool                has_min;
    int64               min_mu = 0;
    int64               limit_rows;
    LaplaceBandEdgeRow *rows;
    int                 n_rows = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;
    types = obr_bytea_ids_nonnull(PG_GETARG_ARRAYTYPE_P(0), &n_types);
    if (n_types == 0)
    {
        if (types != NULL) pfree(types);
        return (Datum) 0;
    }

    has_min = !PG_ARGISNULL(1);
    if (has_min) min_mu = PG_GETARG_INT64(1);
    limit_rows = PG_ARGISNULL(2) ? 0 : PG_GETARG_INT64(2);
    if (limit_rows <= 0)
    {
        pfree(types);
        return (Datum) 0;
    }

    rows = laplace_consensus_band_edges_scan_c(types, n_types, has_min, min_mu, limit_rows, &n_rows);
    pfree(types);
    for (int i = 0; i < n_rows; i++)
    {
        const LaplaceBandEdgeRow *r = &rows[i];
        Datum vals[7] = {
            hash128_to_datum(&r->subject_id),
            hash128_to_datum(&r->type_id),
            hash128_to_datum(&r->object_id),
            Int64GetDatum(r->rating),
            Int64GetDatum(r->rd),
            Int64GetDatum(r->witness_count),
            Int64GetDatum(r->eff_mu)
        };
        bool nulls[7] = { false, false, false, false, false, false, false };
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
        pfree(DatumGetPointer(vals[0]));
        pfree(DatumGetPointer(vals[1]));
        pfree(DatumGetPointer(vals[2]));
    }
    if (rows != NULL)
        pfree(rows);
    return (Datum) 0;
}

/* ========================================================================
 * 9. conversation.couple_topics(bytea, bytea)
 * ======================================================================== */

typedef struct
{
    MemoryContext caller;
    hash128_t    *ids;
    int           count;
    int           cap;
} CtContainerCtx;

static bool
ct_visit_container(TupleTableSlot *slot, void *opaque)
{
    CtContainerCtx *ctx = (CtContainerCtx *) opaque;
    bool            isnull = false;
    Datum           d = slot_getattr(slot, 1, &isnull);
    MemoryContext   prev;

    if (isnull) return true;
    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count >= ctx->cap)
    {
        Size sz;
        ctx->cap = ctx->cap ? ctx->cap * 2 : 32;
        sz = sizeof(hash128_t) * (Size) ctx->cap;
        ctx->ids = ctx->ids ? (hash128_t *) repalloc(ctx->ids, sz) : (hash128_t *) palloc(sz);
    }
    ctx->ids[ctx->count++] = laplace_pub_blake3_to_hash128(d);
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct { hash128_t x; hash128_t y; } CtPair;
typedef struct { hash128_t x; hash128_t y; hash128_t entity; } CtRoleHit;

typedef struct
{
    MemoryContext caller;
    const CtPair *pairs;
    int           n_pairs;
    CtRoleHit    *hits;
    int           count;
    int           cap;
} CtRoleCtx;

static bool
ct_visit_role(TupleTableSlot *slot, void *opaque)
{
    CtRoleCtx    *ctx = (CtRoleCtx *) opaque;
    bool          isnull = false;
    Datum         d_ord, d_ent, d_path;
    int64         ord;
    const CtPair *cp;
    lp_path       path;
    lp_id         parts[3];
    hash128_t     p0, p2;
    MemoryContext prev;

    d_ord = slot_getattr(slot, 1, &isnull);
    if (isnull) return true;
    ord = DatumGetInt64(d_ord);
    if (ord < 1 || ord > ctx->n_pairs) return true;
    cp = &ctx->pairs[ord - 1];

    d_path = slot_getattr(slot, 3, &isnull);
    if (isnull) return true;
    path = laplace_pub_geo_path(d_path);
    if (lp_path_len(path) != 3 || lp_path_expand(path, parts, 3) != 3)
        return true;

    memcpy(&p0, parts[0].b, sizeof(hash128_t));
    memcpy(&p2, parts[2].b, sizeof(hash128_t));
    if (!((hash128_eq(&p0, &cp->x) && hash128_eq(&p2, &cp->y)) ||
          (hash128_eq(&p2, &cp->x) && hash128_eq(&p0, &cp->y))))
        return true;

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count >= ctx->cap)
    {
        Size sz;
        ctx->cap = ctx->cap ? ctx->cap * 2 : 32;
        sz = sizeof(CtRoleHit) * (Size) ctx->cap;
        ctx->hits = ctx->hits ? (CtRoleHit *) repalloc(ctx->hits, sz) : (CtRoleHit *) palloc(sz);
    }
    ctx->hits[ctx->count].x = cp->x;
    ctx->hits[ctx->count].y = cp->y;
    ctx->hits[ctx->count].entity = laplace_pub_blake3_to_hash128(d_ent);
    ctx->count++;
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct { hash128_t claim; double standing; } CtStanding;

typedef struct
{
    MemoryContext caller;
    HTAB         *standings;
} CtStandingCtx;

static bool
ct_visit_standing(TupleTableSlot *slot, void *opaque)
{
    CtStandingCtx *ctx = (CtStandingCtx *) opaque;
    bool           isnull = false;
    Datum          d_cl, d_rat, d_dev;
    hash128_t      cl;
    double         r = 0.0, d = 0.0;
    CtStanding    *st;
    bool           f = false;
    MemoryContext  prev;

    d_cl = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    cl = laplace_pub_blake3_to_hash128(d_cl);
    d_rat = slot_getattr(slot, 3, &isnull);
    if (!isnull) r = DatumGetFloat8(d_rat);
    d_dev = slot_getattr(slot, 4, &isnull);
    if (!isnull) d = DatumGetFloat8(d_dev);

    prev = MemoryContextSwitchTo(ctx->caller);
    st = (CtStanding *) hash_search(ctx->standings, &cl, HASH_ENTER, &f);
    if (!f)
        st->standing = r - 2.0 * d;
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct
{
    MemoryContext caller;
    char         *x_lbl;
    char         *y_lbl;
    hash128_t     best_x;
    hash128_t     best_y;
} CtLabelCtx;

static bool
ct_visit_label(TupleTableSlot *slot, void *opaque)
{
    CtLabelCtx   *ctx = (CtLabelCtx *) opaque;
    bool          isnull = false;
    Datum         d_ent, d_lbl;
    hash128_t     ent;
    char         *str;
    MemoryContext prev;

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    ent = laplace_pub_blake3_to_hash128(d_ent);
    d_lbl = slot_getattr(slot, 3, &isnull);
    if (isnull) return true;
    str = TextDatumGetCString(d_lbl);

    prev = MemoryContextSwitchTo(ctx->caller);
    if (hash128_eq(&ent, &ctx->best_x) && ctx->x_lbl == NULL)
        ctx->x_lbl = pstrdup(str);
    if (hash128_eq(&ent, &ctx->best_y) && ctx->y_lbl == NULL)
        ctx->y_lbl = pstrdup(str);
    MemoryContextSwitchTo(prev);
    pfree(str);
    return true;
}

Datum
pg_laplace_couple_topics_native(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    MemoryContext  caller = CurrentMemoryContext;
    MemoryContext  work, prev;
    hash128_t      in_x, in_y;
    hash128_t      out_x, out_y;
    char          *x_lbl = NULL, *y_lbl = NULL;
    bool           has_winner = false;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        return (Datum) 0;
    in_x = datum_to_hash128(PG_GETARG_DATUM(0));
    in_y = datum_to_hash128(PG_GETARG_DATUM(1));
    out_x = in_x;
    out_y = in_y;

    work = AllocSetContextCreate(caller, "couple_topics_native", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    {
        Oid         b3_arr_oid = laplace_pub_blake3_array_oid();
        Datum       tup_d = Int16GetDatum(laplace_pub_bank_bit("kind", "tuple"));
        ArrayType  *tup_arr = construct_array(&tup_d, 1, INT2OID, sizeof(int16), true, TYPALIGN_SHORT);
        Oid         c_types[2] = { b3_arr_oid, INT2ARRAYOID };
        bool        c_nulls[2] = { false, false };
        CtContainerCtx xctx = { work, NULL, 0, 0 };
        CtContainerCtx yctx = { work, NULL, 0, 0 };
        hash128_t  *xs, *ys;
        int         nx = 1, ny = 1;

        xctx.ids = (hash128_t *) palloc(sizeof(hash128_t) * 16);
        xctx.cap = 16;
        xctx.ids[xctx.count++] = in_x;

        yctx.ids = (hash128_t *) palloc(sizeof(hash128_t) * 16);
        yctx.cap = 16;
        yctx.ids[yctx.count++] = in_y;

        {
            ArrayType *xa = laplace_pub_blake3_array(&in_x, 1);
            ArrayType *ya = laplace_pub_blake3_array(&in_y, 1);
            Datum da[2] = { PointerGetDatum(xa), PointerGetDatum(tup_arr) };
            Datum db[2] = { PointerGetDatum(ya), PointerGetDatum(tup_arr) };
            (void) laplace_pub_call_srf("public", "laplace_containers", 2, c_types, da, c_nulls, ct_visit_container, &xctx);
            (void) laplace_pub_call_srf("public", "laplace_containers", 2, c_types, db, c_nulls, ct_visit_container, &yctx);
            pfree(xa);
            pfree(ya);
            pfree(tup_arr);
        }

        xs = (hash128_t *) palloc(sizeof(hash128_t) * (Size) xctx.count);
        ys = (hash128_t *) palloc(sizeof(hash128_t) * (Size) yctx.count);
        nx = obr_dedup_ids(xctx.ids, xctx.count, xs);
        ny = obr_dedup_ids(yctx.ids, yctx.count, ys);

        {
            CtPair    *pairs = (CtPair *) palloc(sizeof(CtPair) * (Size) (nx * ny));
            hash128_t *px = (hash128_t *) palloc(sizeof(hash128_t) * (Size) (nx * ny));
            hash128_t *py = (hash128_t *) palloc(sizeof(hash128_t) * (Size) (nx * ny));
            int        np = 0;

            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    if (!hash128_eq(&xs[i], &ys[j]))
                    {
                        pairs[np].x = xs[i];
                        pairs[np].y = ys[j];
                        px[np] = xs[i];
                        py[np] = ys[j];
                        np++;
                    }
                }
            }

            if (np > 0)
            {
                ArrayType *xarr = laplace_pub_blake3_array(px, np);
                ArrayType *yarr = laplace_pub_blake3_array(py, np);
                Oid        r_types[4] = { b3_arr_oid, b3_arr_oid, INT8OID, INT2OID };
                Datum      r_args[4] = { PointerGetDatum(xarr), PointerGetDatum(yarr), (Datum) 0, Int16GetDatum(3) };
                bool       r_nulls[4] = { false, false, true, false };
                CtRoleCtx  rctx = { work, pairs, np, NULL, 0, 0 };

                (void) laplace_pub_call_srf("public", "laplace_roles_of", 4,
                                            r_types, r_args, r_nulls,
                                            ct_visit_role, &rctx);
                pfree(xarr);
                pfree(yarr);

                if (rctx.count > 0)
                {
                    hash128_t *raw_e = (hash128_t *) palloc(sizeof(hash128_t) * (Size) rctx.count);
                    hash128_t *uniq_e = (hash128_t *) palloc(sizeof(hash128_t) * (Size) rctx.count);
                    int        n_ue;
                    ArrayType *earr;
                    Datum      ea;
                    bool       en = false;
                    HTAB      *st_map = obr_hash_id(work, "ct stand", sizeof(CtStanding), rctx.count);
                    CtStandingCtx sctx = { work, st_map };
                    double     best_stand = 0.0;
                    hash128_t  best_ent = {0, 0};

                    for (int i = 0; i < rctx.count; i++)
                        raw_e[i] = rctx.hits[i].entity;
                    n_ue = obr_dedup_ids(raw_e, rctx.count, uniq_e);
                    earr = laplace_pub_blake3_array(uniq_e, n_ue);
                    ea = PointerGetDatum(earr);
                    (void) laplace_pub_call_srf("public", "laplace_standings", 1,
                                                &b3_arr_oid, &ea, &en,
                                                ct_visit_standing, &sctx);
                    pfree(earr);

                    for (int i = 0; i < rctx.count; i++)
                    {
                        const CtRoleHit *rh = &rctx.hits[i];
                        CtStanding *st = (CtStanding *) hash_search(st_map, &rh->entity, HASH_FIND, NULL);
                        double      sc;
                        int         c;
                        bool        better = false;

                        if (st == NULL)
                            continue;
                        sc = st->standing;
                        if (!has_winner)
                            better = true;
                        else if (sc != best_stand)
                            better = (sc > best_stand);
                        else if ((c = memcmp(&rh->entity, &best_ent, sizeof(hash128_t))) != 0)
                            better = (c < 0);
                        else if ((c = memcmp(&rh->x, &out_x, sizeof(hash128_t))) != 0)
                            better = (c < 0);
                        else
                            better = (memcmp(&rh->y, &out_y, sizeof(hash128_t)) < 0);

                        if (better)
                        {
                            has_winner = true;
                            best_stand = sc;
                            best_ent = rh->entity;
                            out_x = rh->x;
                            out_y = rh->y;
                        }
                    }

                    if (has_winner)
                    {
                        hash128_t  lbl_ids[2] = { out_x, out_y };
                        int        n_lid = hash128_eq(&out_x, &out_y) ? 1 : 2;
                        ArrayType *larr = laplace_pub_blake3_array(lbl_ids, n_lid);
                        Datum      la = PointerGetDatum(larr);
                        bool       ln = false;
                        CtLabelCtx lctx = { work, NULL, NULL, out_x, out_y };
                        (void) laplace_pub_call_srf("public", "laplace_labels", 1,
                                                    &b3_arr_oid, &la, &ln,
                                                    ct_visit_label, &lctx);
                        pfree(larr);
                        MemoryContextSwitchTo(caller);
                        if (lctx.x_lbl) x_lbl = pstrdup(lctx.x_lbl);
                        if (lctx.y_lbl) y_lbl = pstrdup(lctx.y_lbl);
                    }
                }
            }
        }
    }

    MemoryContextSwitchTo(caller);
    {
        Datum vals[4] = {
            hash128_to_datum(&out_x),
            hash128_to_datum(&out_y),
            x_lbl ? CStringGetTextDatum(x_lbl) : (Datum) 0,
            y_lbl ? CStringGetTextDatum(y_lbl) : (Datum) 0
        };
        bool nulls[4] = { false, false, (x_lbl == NULL), (y_lbl == NULL) };
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
        pfree(DatumGetPointer(vals[0]));
        pfree(DatumGetPointer(vals[1]));
        if (!nulls[2]) pfree(DatumGetPointer(vals[2]));
        if (!nulls[3]) pfree(DatumGetPointer(vals[3]));
    }

    MemoryContextDelete(work);
    return (Datum) 0;
}
