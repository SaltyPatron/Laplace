#include "postgres.h"

#include <string.h>

#include "access/genam.h"
#include "access/relscan.h"
#include "access/skey.h"
#include "access/table.h"
#include "access/tableam.h"
#include "catalog/index.h"
#include "catalog/namespace.h"
#include "catalog/pg_am_d.h"
#include "catalog/pg_inherits.h"
#include "catalog/pg_type.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "tcop/dest.h"
#include "utils/acl.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/fmgroids.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/rel.h"
#include "utils/rls.h"
#include "utils/snapmgr.h"

#include "observation_read.h"
#include "public_direct.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "laplace/core/qualifier_law.h"
#include "laplace/core/sql_catalog.h"
#include "laplace_pg/executor.h"

PG_FUNCTION_INFO_V1(pg_laplace_observation_bindings);
PG_FUNCTION_INFO_V1(pg_laplace_native_observation_cells);
PG_FUNCTION_INFO_V1(pg_laplace_native_observation_bindings);

typedef struct OperandEntry
{
    hash128_t id;
    int       first;
} OperandEntry;

typedef struct ObsIdEntry
{
    hash128_t id;
} ObsIdEntry;

typedef struct ObsClaimTriple
{
    hash128_t claim;
    hash128_t subject;
    hash128_t type;
    hash128_t object;
} ObsClaimTriple;

typedef struct ObsDispatch
{
    HTAB                      *operands;
    int                       *next;
    int                        roles;
    LaplaceObservationVisitor  visit;
    void                      *context;
} ObsDispatch;

static void
read_id(Datum value, hash128_t *id)
{
    bytea *bytes = DatumGetByteaPP(value);
    if (VARSIZE_ANY_EXHDR(bytes) != sizeof(*id))
        ereport(ERROR, (errmsg("observation bindings: ids must be 16 bytes")));
    memcpy(id, VARDATA_ANY(bytes), sizeof(*id));
}

/* Digest of one attestation row: row id plus source, context, outcome and
 * occurrence count encoded explicitly, so two rows sharing an id but differing
 * in testimony remain distinct provenance members of the channel's Merkle
 * root. */
hash128_t
laplace_observation_provenance(const LaplaceObservation *row)
{
    unsigned char     bytes[16 * 5 + 12 + 1 + 40 + 32];
    hash128_t         domain;
    hash128_t         zero;
    hash128_t         result;
    const hash128_t  *source;
    const hash128_t  *context;
    Size              at = 0;
    uint16            outcome = (uint16) row->outcome;
    uint64            occurrences = (uint64) row->occurrences;

    hash128_blake3_str(row->native ? "laplace:native-testimony-witness:v1"
                                   : "laplace:query-provenance-witness:v1",
                       &domain);
    hash128_zero(&zero);
    source = row->source_null ? &zero : &row->source;
    context = row->context_null ? &zero : &row->context;

#define PROVENANCE_APPEND(value) \
    do { memcpy(bytes + at, &(value), sizeof(value)); at += sizeof(value); } while (0)
    PROVENANCE_APPEND(domain);
    if (row->native) { PROVENANCE_APPEND(row->claim); } else { PROVENANCE_APPEND(row->id); }
    PROVENANCE_APPEND(*source);
    PROVENANCE_APPEND(*context);
#undef PROVENANCE_APPEND
    bytes[at++] = row->source_null ? 0 : 1;
    bytes[at++] = row->context_null ? 0 : 1;
    bytes[at++] = (unsigned char) (outcome >> 8);
    bytes[at++] = (unsigned char) outcome;
    bytes[at++] = (unsigned char) (occurrences >> 56);
    bytes[at++] = (unsigned char) (occurrences >> 48);
    bytes[at++] = (unsigned char) (occurrences >> 40);
    bytes[at++] = (unsigned char) (occurrences >> 32);
    bytes[at++] = (unsigned char) (occurrences >> 24);
    bytes[at++] = (unsigned char) (occurrences >> 16);
    bytes[at++] = (unsigned char) (occurrences >> 8);
    bytes[at++] = (unsigned char) occurrences;

    if (row->native)
    {
        uint64 score, trust;
        uint64 fields[5];

        bytes[at++] = row->dependence_null ? 0 : 1;
        memcpy(bytes + at, row->dependence_null ? &zero : &row->dependence, sizeof(hash128_t));
        at += sizeof(hash128_t);
        memcpy(&score, &row->score, sizeof(score));
        memcpy(&trust, &row->trust, sizeof(trust));
        fields[0] = (uint64) row->ordinal;
        fields[1] = (uint64) row->tokens;
        fields[2] = score;
        fields[3] = trust;
        fields[4] = (uint64) row->position;
        for (int i = 0; i < (int) lengthof(fields); i++)
            for (int shift = 56; shift >= 0; shift -= 8)
                bytes[at++] = (unsigned char) (fields[i] >> shift);
        memcpy(bytes + at, row->qualifiers, sizeof(row->qualifiers));
        at += sizeof(row->qualifiers);
    }

    hash128_blake3(bytes, at, &result);
    return result;
}

bool
laplace_observation_qualified(const LaplaceObservation *row, const char *family, const char *value)
{
    const int bit = laplace_qualifier_bit(family, value);
    if (bit < 0)
        elog(ERROR, "observation bindings: undeclared qualifier %s/%s", family, value);
    return (row->qualifiers[bit / 8] & (1u << (bit % 8))) != 0;
}

static void
validate_filter(ArrayType *filter)
{
    ArrayIterator iterator;
    Datum         value;
    bool          isnull;

    if (!filter)
        return;
    if (ARR_NDIM(filter) > 1 || ARR_ELEMTYPE(filter) != BYTEAOID)
        ereport(ERROR, (errmsg("observation bindings: ids must be a 1-D bytea array")));
    iterator = array_create_iterator(filter, 0, NULL);
    while (array_iterate(iterator, &value, &isnull))
    {
        hash128_t id;
        if (!isnull)
            read_id(value, &id);
    }
    array_free_iterator(iterator);
}

static HTAB *
obs_build_id_set(MemoryContext ctx, const char *name, ArrayType *arr)
{
    Datum   *vals;
    bool    *nulls;
    int      n = 0;
    HASHCTL  ctl;
    HTAB    *set;

    if (arr == NULL)
        return NULL;
    deconstruct_array(arr, BYTEAOID, -1, false, TYPALIGN_INT, &vals, &nulls, &n);
    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(ObsIdEntry);
    ctl.hcxt = ctx;
    set = hash_create(name, Max(n, 16), &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    for (int i = 0; i < n; i++)
    {
        hash128_t id;
        bool      found = false;
        if (nulls[i])
            continue;
        read_id(vals[i], &id);
        (void) hash_search(set, &id, HASH_ENTER, &found);
    }
    pfree(vals);
    pfree(nulls);
    return set;
}

static ArrayType *
obs_bytea_array_to_b3_nonnull(ArrayType *arr)
{
    Datum     *vals;
    bool      *nulls;
    int        n = 0, out = 0;
    hash128_t *ids;
    ArrayType *res;

    if (arr == NULL)
        return NULL;
    deconstruct_array(arr, BYTEAOID, -1, false, TYPALIGN_INT, &vals, &nulls, &n);
    if (n == 0)
    {
        pfree(vals);
        pfree(nulls);
        return laplace_pub_blake3_array(NULL, 0);
    }
    ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n);
    for (int i = 0; i < n; i++)
    {
        if (!nulls[i])
            ids[out++] = datum_to_hash128(vals[i]);
    }
    pfree(vals);
    pfree(nulls);
    res = laplace_pub_blake3_array(ids, out);
    pfree(ids);
    return res;
}

static void
dispatch_observation(const ObsDispatch *d, const LaplaceObservation *row)
{
    OperandEntry *operand = (d->roles & 1)
        ? (OperandEntry *) hash_search(d->operands, &row->subject, HASH_FIND, NULL)
        : NULL;

    if (operand != NULL)
    {
        for (int i = operand->first; i >= 0; i = d->next[i])
            d->visit(i + 1, 1, row, d->context);
    }
    if (!row->object_null && (d->roles & 2))
    {
        OperandEntry *object = (OperandEntry *) hash_search(
            d->operands, &row->object, HASH_FIND, NULL);
        if (!operand && !object)
            elog(ERROR, "observation bindings: witness escaped the operand set");
        if (object != NULL)
        {
            for (int i = object->first; i >= 0; i = d->next[i])
                d->visit(i + 1, 2, row, d->context);
        }
    }
    else if (!operand)
    {
        elog(ERROR, "observation bindings: witness escaped the operand set");
    }
}

/* ---- Native observation reads (laplace_compose / laplace_holders + laplace_testimony) ---- */

typedef struct NativeObsHolderCtx
{
    MemoryContext   caller;
    HTAB           *op_set;
    HTAB           *type_set;
    int             roles;
    HTAB           *claims_map;
    ObsClaimTriple *triples;
    int             count;
    int             cap;
} NativeObsHolderCtx;

static bool
obs_visit_holder(TupleTableSlot *slot, void *opaque)
{
    NativeObsHolderCtx *ctx = (NativeObsHolderCtx *) opaque;
    bool                isnull = false;
    Datum               d_ent, d_path;
    lp_path             path;
    lp_id               parts[3];
    hash128_t           ent, p0, p1, p2;
    bool                match = false;
    ObsClaimTriple     *tr;
    bool                found = false;
    MemoryContext       prev;

    d_path = slot_getattr(slot, 3, &isnull);
    if (isnull)
        return true;
    path = laplace_pub_geo_path(d_path);
    if (lp_path_len(path) != 3 || lp_path_expand(path, parts, 3) != 3)
        return true;

    memcpy(&p0, parts[0].b, sizeof(hash128_t));
    memcpy(&p1, parts[1].b, sizeof(hash128_t));
    memcpy(&p2, parts[2].b, sizeof(hash128_t));

    if ((ctx->roles & 1) && hash_search(ctx->op_set, &p0, HASH_FIND, NULL) != NULL)
        match = true;
    if (!match && (ctx->roles & 2) && hash_search(ctx->op_set, &p2, HASH_FIND, NULL) != NULL)
        match = true;
    if (!match)
        return true;

    if (ctx->type_set != NULL && hash_search(ctx->type_set, &p1, HASH_FIND, NULL) == NULL)
        return true;

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull)
        return true;
    ent = laplace_pub_blake3_to_hash128(d_ent);

    prev = MemoryContextSwitchTo(ctx->caller);
    tr = (ObsClaimTriple *) hash_search(ctx->claims_map, &ent, HASH_ENTER, &found);
    if (!found)
    {
        tr->subject = p0;
        tr->type = p1;
        tr->object = p2;
        if (ctx->count >= ctx->cap)
        {
            Size sz;
            ctx->cap = ctx->cap ? ctx->cap * 2 : 64;
            sz = sizeof(ObsClaimTriple) * (Size) ctx->cap;
            ctx->triples = ctx->triples
                ? (ObsClaimTriple *) repalloc(ctx->triples, sz)
                : (ObsClaimTriple *) palloc(sz);
        }
        ctx->triples[ctx->count++] = *tr;
    }
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct NativeObsTestimonyCtx
{
    MemoryContext     caller;
    HTAB             *claims_map;
    bool              emit_srf;
    ReturnSetInfo    *rsinfo;
    const ObsDispatch *dispatch;
    int               calc_bit;
} NativeObsTestimonyCtx;

static bool
obs_visit_testimony(TupleTableSlot *slot, void *opaque)
{
    NativeObsTestimonyCtx *ctx = (NativeObsTestimonyCtx *) opaque;
    bool                   isnull = false;
    Datum                  d_claim, d_src, d_ctx, d_ord, d_out, d_occ, d_tok, d_scr, d_pos, d_tru, d_dep, d_calc;
    bool                   src_null, ctx_null, ord_null, out_null, occ_null, tok_null, scr_null, pos_null, tru_null, dep_null, calc_null;
    hash128_t              claim;
    ObsClaimTriple        *tr;

    d_claim = slot_getattr(slot, 1, &isnull);
    if (isnull)
        return true;
    claim = laplace_pub_blake3_to_hash128(d_claim);
    tr = (ObsClaimTriple *) hash_search(ctx->claims_map, &claim, HASH_FIND, NULL);
    if (tr == NULL)
        return true;

    d_src = slot_getattr(slot, 2, &src_null);
    d_ctx = slot_getattr(slot, 3, &ctx_null);
    d_ord = slot_getattr(slot, 4, &ord_null);
    d_out = slot_getattr(slot, 5, &out_null);
    d_occ = slot_getattr(slot, 6, &occ_null);
    d_tok = slot_getattr(slot, 7, &tok_null);
    d_scr = slot_getattr(slot, 8, &scr_null);
    d_pos = slot_getattr(slot, 9, &pos_null);
    d_tru = slot_getattr(slot, 10, &tru_null);
    d_dep = slot_getattr(slot, 11, &dep_null);
    d_calc = slot_getattr(slot, 12, &calc_null);

    if (ctx->emit_srf)
    {
        Datum values[16];
        bool  nulls[16];
        hash128_t src_h, ctx_h, dep_h;
        MemoryContext prev;

        MemSet(nulls, 0, sizeof(nulls));
        prev = MemoryContextSwitchTo(ctx->caller);

        values[0] = hash128_to_datum(&claim);
        values[1] = hash128_to_datum(&tr->subject);
        values[2] = hash128_to_datum(&tr->type);
        values[3] = hash128_to_datum(&tr->object);

        if (src_null) { nulls[4] = true; values[4] = (Datum) 0; }
        else { src_h = laplace_pub_blake3_to_hash128(d_src); values[4] = hash128_to_datum(&src_h); }

        if (ctx_null) { nulls[5] = true; values[5] = (Datum) 0; }
        else { ctx_h = laplace_pub_blake3_to_hash128(d_ctx); values[5] = hash128_to_datum(&ctx_h); }

        nulls[6] = out_null;  values[6] = out_null ? (Datum) 0 : d_out;
        nulls[7] = occ_null;  values[7] = occ_null ? (Datum) 0 : d_occ;
        nulls[8] = true;      values[8] = (Datum) 0; /* qualifier_mask = NULL::bytea */
        nulls[9] = scr_null;  values[9] = scr_null ? (Datum) 0 : d_scr;
        nulls[10] = tok_null; values[10] = tok_null ? (Datum) 0 : d_tok;
        nulls[11] = ord_null; values[11] = ord_null ? (Datum) 0 : d_ord;
        nulls[12] = tru_null; values[12] = tru_null ? (Datum) 0 : d_tru;

        if (dep_null) { nulls[13] = true; values[13] = (Datum) 0; }
        else { dep_h = laplace_pub_blake3_to_hash128(d_dep); values[13] = hash128_to_datum(&dep_h); }

        nulls[14] = calc_null; values[14] = calc_null ? (Datum) 0 : d_calc;
        nulls[15] = pos_null;  values[15] = pos_null ? (Datum) 0 : d_pos;

        tuplestore_putvalues(ctx->rsinfo->setResult, ctx->rsinfo->setDesc, values, nulls);
        for (int k = 0; k < 6; k++)
            if (!nulls[k]) pfree(DatumGetPointer(values[k]));
        if (!nulls[13])
            pfree(DatumGetPointer(values[13]));
        MemoryContextSwitchTo(prev);
    }
    else
    {
        LaplaceObservation row;
        MemoryContext      prev;

        if (occ_null || scr_null || tok_null || ord_null || tru_null || pos_null)
            elog(ERROR, "Native observation has a missing series field");

        MemSet(&row, 0, sizeof(row));
        row.native = true;
        row.claim = claim;
        row.subject = tr->subject;
        row.type = tr->type;
        row.object = tr->object;
        row.object_null = false;
        row.source_null = src_null;
        if (!src_null) row.source = laplace_pub_blake3_to_hash128(d_src);
        row.context_null = ctx_null;
        if (!ctx_null) row.context = laplace_pub_blake3_to_hash128(d_ctx);
        row.outcome = out_null ? LAPLACE_OBSERVATION_SCORED : DatumGetInt16(d_out);
        row.occurrences = DatumGetInt64(d_occ);
        row.score = DatumGetFloat8(d_scr);
        row.tokens = DatumGetInt64(d_tok);
        row.ordinal = DatumGetInt64(d_ord);
        row.trust = DatumGetFloat8(d_tru);
        row.position = DatumGetInt32(d_pos);
        row.dependence_null = dep_null;
        if (!dep_null) row.dependence = laplace_pub_blake3_to_hash128(d_dep);
        if (!calc_null && DatumGetBool(d_calc))
            row.qualifiers[ctx->calc_bit / 8] |= 1u << (ctx->calc_bit % 8);
        row.id = laplace_observation_provenance(&row);

        prev = MemoryContextSwitchTo(ctx->caller);
        dispatch_observation(ctx->dispatch, &row);
        MemoryContextSwitchTo(prev);
    }

    CHECK_FOR_INTERRUPTS();
    return true;
}

static void
native_obs_run_testimony(MemoryContext work, HTAB *claims_map,
                         const hash128_t *claim_ids, int n_claims,
                         ArrayType *sources_bytea,
                         bool emit_srf, ReturnSetInfo *rsinfo,
                         const ObsDispatch *dispatch)
{
    Oid                    b3_arr_oid = laplace_pub_blake3_array_oid();
    ArrayType             *claims_arr;
    ArrayType             *sources_arr = NULL;
    Oid                    t_types[2] = { b3_arr_oid, b3_arr_oid };
    Datum                  t_args[2];
    bool                   t_nulls[2] = { false, true };
    NativeObsTestimonyCtx  tctx;
    int                    calc_bit = laplace_qualifier_bit("derivation", "calculation");

    if (n_claims <= 0)
        return;
    if (calc_bit < 0 || calc_bit >= 256)
        elog(ERROR, "Native calculation qualifier is undeclared");

    claims_arr = laplace_pub_blake3_array(claim_ids, n_claims);
    t_args[0] = PointerGetDatum(claims_arr);
    if (sources_bytea != NULL)
    {
        sources_arr = obs_bytea_array_to_b3_nonnull(sources_bytea);
        t_args[1] = PointerGetDatum(sources_arr);
        t_nulls[1] = false;
    }
    else
    {
        t_args[1] = (Datum) 0;
        t_nulls[1] = true;
    }

    tctx.caller = work;
    tctx.claims_map = claims_map;
    tctx.emit_srf = emit_srf;
    tctx.rsinfo = rsinfo;
    tctx.dispatch = dispatch;
    tctx.calc_bit = calc_bit;

    (void) laplace_pub_call_srf("public", "laplace_testimony", 2,
                                t_types, t_args, t_nulls,
                                obs_visit_testimony, &tctx);
    pfree(claims_arr);
    if (sources_arr != NULL)
        pfree(sources_arr);
}

static void
native_obs_cells_exec(const LaplaceObservationCell *cells, int cell_count,
                      ArrayType *sources_bytea,
                      bool emit_srf, ReturnSetInfo *rsinfo,
                      const ObsDispatch *dispatch)
{
    MemoryContext  caller = CurrentMemoryContext;
    MemoryContext  work, prev;
    HASHCTL        ctl;
    HTAB          *seen_cells;
    HTAB          *claims_map;
    hash128_t     *claim_ids;
    int            n_claims = 0;
    Oid            b3_arr_oid = laplace_pub_blake3_array_oid();

    if (cell_count <= 0)
        return;

    work = AllocSetContextCreate(caller, "native_obs_cells", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(LaplaceObservationCell);
    ctl.entrysize = sizeof(LaplaceObservationCell);
    ctl.hcxt = work;
    seen_cells = hash_create("noc seen", Max(cell_count, 16), &ctl,
                             HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(ObsClaimTriple);
    ctl.hcxt = work;
    claims_map = hash_create("noc claims", Max(cell_count, 16), &ctl,
                             HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    claim_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) cell_count);

    for (int i = 0; i < cell_count; i++)
    {
        bool       found = false;
        hash128_t  parts[3] = { cells[i].subject, cells[i].type, cells[i].object };
        ArrayType *p_arr;
        Datum      arg;
        bool       isnull = false;
        Datum      res;
        hash128_t  claim;
        bool       c_found = false;
        ObsClaimTriple *tr;

        hash_search(seen_cells, &cells[i], HASH_ENTER, &found);
        if (found)
            continue;

        p_arr = laplace_pub_blake3_array(parts, 3);
        arg = PointerGetDatum(p_arr);
        res = laplace_pub_call("public", "laplace_compose", 1, &b3_arr_oid, &arg, &isnull, &isnull);
        pfree(p_arr);
        if (isnull)
            continue;
        claim = laplace_pub_blake3_to_hash128(res);
        pfree(DatumGetPointer(res));

        tr = (ObsClaimTriple *) hash_search(claims_map, &claim, HASH_ENTER, &c_found);
        if (!c_found)
        {
            tr->subject = cells[i].subject;
            tr->type = cells[i].type;
            tr->object = cells[i].object;
            claim_ids[n_claims++] = claim;
        }
    }

    native_obs_run_testimony(work, claims_map, claim_ids, n_claims,
                             sources_bytea, emit_srf, rsinfo, dispatch);

    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
}

static void
native_obs_bindings_exec(const hash128_t *unique_ops, int unique_count,
                         ArrayType *sources_bytea, ArrayType *types_bytea,
                         int roles, bool emit_srf, ReturnSetInfo *rsinfo,
                         const ObsDispatch *dispatch)
{
    MemoryContext      caller = CurrentMemoryContext;
    MemoryContext      work, prev;
    HASHCTL            ctl;
    HTAB              *op_set;
    HTAB              *type_set = NULL;
    HTAB              *claims_map;
    Oid                b3_arr_oid = laplace_pub_blake3_array_oid();
    ArrayType         *ops_arr, *empty_arr;
    Oid                h_types[6] = { b3_arr_oid, b3_arr_oid, BOOLOID, INT2OID, INT8OID, INT2OID };
    Datum              h_args[6];
    bool               h_nulls[6] = { false, false, false, false, false, false };
    NativeObsHolderCtx hctx;

    if (unique_count <= 0 || roles == 0)
        return;

    work = AllocSetContextCreate(caller, "native_obs_bindings", ALLOCSET_DEFAULT_SIZES);
    prev = MemoryContextSwitchTo(work);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(ObsIdEntry);
    ctl.hcxt = work;
    op_set = hash_create("nob ops", Max(unique_count, 16), &ctl,
                         HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    for (int i = 0; i < unique_count; i++)
    {
        bool f = false;
        (void) hash_search(op_set, &unique_ops[i], HASH_ENTER, &f);
    }

    if (types_bytea != NULL)
    {
        ArrayType *t_clean = obs_bytea_array_to_b3_nonnull(types_bytea);
        (void) t_clean;
        type_set = obs_build_id_set(work, "nob types", types_bytea);
    }

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(ObsClaimTriple);
    ctl.hcxt = work;
    claims_map = hash_create("nob claims", 64, &ctl,
                             HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    MemSet(&hctx, 0, sizeof(hctx));
    hctx.caller = work;
    hctx.op_set = op_set;
    hctx.type_set = type_set;
    hctx.roles = roles;
    hctx.claims_map = claims_map;

    ops_arr = laplace_pub_blake3_array(unique_ops, unique_count);
    empty_arr = laplace_pub_blake3_array(NULL, 0);
    h_args[0] = PointerGetDatum(ops_arr);
    h_args[1] = PointerGetDatum(empty_arr);
    h_args[2] = BoolGetDatum(false);
    h_args[3] = Int16GetDatum(-1);
    h_args[4] = Int64GetDatum(-1);
    h_args[5] = Int16GetDatum(1);

    (void) laplace_pub_call_srf("public", "laplace_holders", 6,
                                h_types, h_args, h_nulls,
                                obs_visit_holder, &hctx);
    pfree(ops_arr);
    pfree(empty_arr);

    if (hctx.count > 0)
    {
        hash128_t *claim_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) hctx.count);
        for (int i = 0; i < hctx.count; i++)
            claim_ids[i] = hctx.triples[i].claim;
        native_obs_run_testimony(work, claims_map, claim_ids, hctx.count,
                                 sources_bytea, emit_srf, rsinfo, dispatch);
    }

    MemoryContextSwitchTo(prev);
    MemoryContextDelete(work);
}

/* ---- Legacy laplace.attestations direct scan (zero SPI) ---- */

static AttrNumber
obs_find_attnum(TupleDesc desc, const char *name)
{
    for (int i = 0; i < desc->natts; i++)
    {
        Form_pg_attribute a = TupleDescAttr(desc, i);
        if (!a->attisdropped && strcmp(NameStr(a->attname), name) == 0)
            return a->attnum;
    }
    return InvalidAttrNumber;
}

static bool
obs_rel_has_live_rows(Oid relid)
{
    char     relkind = get_rel_relkind(relid);
    bool     has = false;
    Snapshot snap = GetActiveSnapshot();

    if (relkind == RELKIND_PARTITIONED_TABLE)
    {
        List     *children = find_inheritance_children(relid, AccessShareLock);
        ListCell *lc;
        foreach(lc, children)
        {
            has = obs_rel_has_live_rows(lfirst_oid(lc));
            if (has)
                break;
        }
        list_free(children);
        return has;
    }

    {
        Relation        rel = table_open(relid, AccessShareLock);
        TableScanDesc   scan = table_beginscan(rel, snap, 0, NULL);
        TupleTableSlot *slot = table_slot_create(rel, NULL);
        has = table_scan_getnextslot(scan, ForwardScanDirection, slot);
        ExecDropSingleTupleTableSlot(slot);
        table_endscan(scan);
        table_close(rel, AccessShareLock);
    }
    return has;
}

static void
obs_scan_legacy_attestations(Oid relid,
                             HTAB *op_set, HTAB *src_set, HTAB *type_set,
                             HTAB *cell_set, int roles,
                             const ObsDispatch *dispatch)
{
    char     relkind = get_rel_relkind(relid);
    Snapshot snap = GetActiveSnapshot();

    if (relkind == RELKIND_PARTITIONED_TABLE)
    {
        List     *children = find_inheritance_children(relid, AccessShareLock);
        ListCell *lc;
        foreach(lc, children)
            obs_scan_legacy_attestations(lfirst_oid(lc), op_set, src_set, type_set,
                                         cell_set, roles, dispatch);
        list_free(children);
        return;
    }

    {
        Relation        rel = table_open(relid, AccessShareLock);
        TupleDesc       desc = RelationGetDescr(rel);
        AttrNumber      a_id = obs_find_attnum(desc, "id");
        AttrNumber      a_sub = obs_find_attnum(desc, "subject_id");
        AttrNumber      a_typ = obs_find_attnum(desc, "type_id");
        AttrNumber      a_obj = obs_find_attnum(desc, "object_id");
        AttrNumber      a_src = obs_find_attnum(desc, "source_id");
        AttrNumber      a_ctx = obs_find_attnum(desc, "context_id");
        AttrNumber      a_out = obs_find_attnum(desc, "outcome");
        AttrNumber      a_occ = obs_find_attnum(desc, "observation_count");
        AttrNumber      a_qmk = obs_find_attnum(desc, "qualifier_mask");
        TableScanDesc   scan = table_beginscan(rel, snap, 0, NULL);
        TupleTableSlot *slot = table_slot_create(rel, NULL);

        while (table_scan_getnextslot(scan, ForwardScanDirection, slot))
        {
            bool               isnull = false;
            Datum              d;
            LaplaceObservation row;

            MemSet(&row, 0, sizeof(row));
            row.dependence_null = true;

            d = slot_getattr(slot, a_id, &isnull);
            if (isnull) { ExecClearTuple(slot); continue; }
            read_id(d, &row.id);

            d = slot_getattr(slot, a_sub, &isnull);
            if (isnull) { ExecClearTuple(slot); continue; }
            read_id(d, &row.subject);

            d = slot_getattr(slot, a_typ, &isnull);
            if (isnull) { ExecClearTuple(slot); continue; }
            read_id(d, &row.type);

            d = slot_getattr(slot, a_obj, &isnull);
            row.object_null = isnull;
            if (!isnull) read_id(d, &row.object);

            d = slot_getattr(slot, a_src, &isnull);
            row.source_null = isnull;
            if (!isnull) read_id(d, &row.source);

            if (src_set != NULL && (row.source_null ||
                hash_search(src_set, &row.source, HASH_FIND, NULL) == NULL))
            {
                ExecClearTuple(slot);
                continue;
            }

            if (cell_set != NULL)
            {
                LaplaceObservationCell ck;
                if (row.object_null)
                {
                    ExecClearTuple(slot);
                    continue;
                }
                ck.subject = row.subject;
                ck.type = row.type;
                ck.object = row.object;
                if (hash_search(cell_set, &ck, HASH_FIND, NULL) == NULL)
                {
                    ExecClearTuple(slot);
                    continue;
                }
            }
            else
            {
                bool match = false;
                if ((roles & 1) && hash_search(op_set, &row.subject, HASH_FIND, NULL) != NULL)
                    match = true;
                if (!match && (roles & 2) && !row.object_null &&
                    hash_search(op_set, &row.object, HASH_FIND, NULL) != NULL)
                    match = true;
                if (!match || (type_set != NULL &&
                    hash_search(type_set, &row.type, HASH_FIND, NULL) == NULL))
                {
                    ExecClearTuple(slot);
                    continue;
                }
            }

            d = slot_getattr(slot, a_ctx, &isnull);
            row.context_null = isnull;
            if (!isnull) read_id(d, &row.context);

            d = slot_getattr(slot, a_out, &isnull);
            if (isnull) { ExecClearTuple(slot); continue; }
            row.outcome = DatumGetInt16(d);

            d = slot_getattr(slot, a_occ, &isnull);
            if (isnull) { ExecClearTuple(slot); continue; }
            row.occurrences = DatumGetInt64(d);
            row.score = (double) row.outcome / 2.0;
            row.tokens = row.occurrences;

            if (a_qmk != InvalidAttrNumber)
            {
                d = slot_getattr(slot, a_qmk, &isnull);
                if (!isnull)
                {
                    bytea *mask = DatumGetByteaPP(d);
                    if (VARSIZE_ANY_EXHDR(mask) != sizeof(row.qualifiers))
                        elog(ERROR, "observation bindings: qualifier masks must be 32 bytes");
                    memcpy(row.qualifiers, VARDATA_ANY(mask), sizeof(row.qualifiers));
                }
            }

            dispatch_observation(dispatch, &row);
            ExecClearTuple(slot);
            CHECK_FOR_INTERRUPTS();
        }
        ExecDropSingleTupleTableSlot(slot);
        table_endscan(scan);
        table_close(rel, AccessShareLock);
    }
}

typedef struct LegacyObsReceiver
{
    DestReceiver       receiver;
    MemoryContext      work_ctx;
    const ObsDispatch *dispatch;
} LegacyObsReceiver;

static SPIPlanPtr legacy_obs_plan = NULL;
static SPIPlanPtr legacy_obs_cell_plan = NULL;

static void
legacy_observation_startup(DestReceiver *destination, int operation, TupleDesc descriptor)
{
    static const Oid types[9] = {
        BYTEAOID, BYTEAOID, BYTEAOID, BYTEAOID,
        BYTEAOID, BYTEAOID, INT2OID, INT8OID, BYTEAOID
    };

    (void) destination;
    if (operation != CMD_SELECT || descriptor->natts != 9)
        elog(ERROR, "observation bindings: invalid result shape");
    for (int i = 0; i < 9; ++i)
        if (TupleDescAttr(descriptor, i)->atttypid != types[i])
            elog(ERROR, "observation bindings: invalid result column type");
}

static void
legacy_observation_shutdown(DestReceiver *destination)
{
    (void) destination;
}

static bool
receive_legacy_observation(TupleTableSlot *slot, DestReceiver *destination)
{
    LegacyObsReceiver *receiver = (LegacyObsReceiver *) destination;
    LaplaceObservation row;
    hash128_t         *ids[6];
    bool               nulls[9];
    Datum              values[9];
    MemoryContext      prev;

    MemSet(&row, 0, sizeof(row));
    ids[0] = &row.id;
    ids[1] = &row.subject;
    ids[2] = &row.type;
    ids[3] = &row.object;
    ids[4] = &row.source;
    ids[5] = &row.context;

    for (int i = 0; i < 9; ++i)
        values[i] = slot_getattr(slot, i + 1, &nulls[i]);
    if (nulls[0] || nulls[1] || nulls[2] || nulls[6] || nulls[7])
        elog(ERROR, "observation bindings: missing witness identity or outcome");
    for (int i = 0; i < 6; ++i)
        if (!nulls[i])
            read_id(values[i], ids[i]);

    row.object_null = nulls[3];
    row.source_null = nulls[4];
    row.context_null = nulls[5];
    row.outcome = DatumGetInt16(values[6]);
    row.occurrences = DatumGetInt64(values[7]);
    row.native = false;
    row.dependence_null = true;
    row.score = (double) row.outcome / 2.0;
    row.tokens = row.occurrences;

    if (!nulls[8])
    {
        bytea *mask = DatumGetByteaPP(values[8]);
        if (VARSIZE_ANY_EXHDR(mask) != sizeof(row.qualifiers))
            elog(ERROR, "observation bindings: qualifier masks must be 32 bytes");
        memcpy(row.qualifiers, VARDATA_ANY(mask), sizeof(row.qualifiers));
    }

    prev = MemoryContextSwitchTo(receiver->work_ctx);
    dispatch_observation(receiver->dispatch, &row);
    MemoryContextSwitchTo(prev);
    CHECK_FOR_INTERRUPTS();
    return true;
}

static void
obs_scan_legacy_attestations_spi(MemoryContext work,
                                 const hash128_t *unique, int unique_count,
                                 ArrayType *sources, ArrayType *types, int roles,
                                 const LaplaceObservationCell *cells, int cell_count,
                                 const ObsDispatch *dispatch)
{
    ParamListInfo    params;
    Oid              param_types[4];
    SPIPlanPtr      *plan;
    const char      *query_name;
    MemoryContext    before_spi;
    bool             spi_top = false;
    LegacyObsReceiver receiver = {
        .receiver = {
            receive_legacy_observation,
            legacy_observation_startup,
            legacy_observation_shutdown,
            legacy_observation_shutdown,
            DestNone
        },
        .work_ctx = work,
        .dispatch = dispatch
    };
    SPIExecuteOptions options = {
        .params = NULL,
        .read_only = true,
        .must_return_tuples = true,
        .dest = &receiver.receiver
    };
    int              result;

    params = makeParamList(4);
    for (int i = 0; i < 4; ++i)
        params->params[i].pflags = PARAM_FLAG_CONST;

    if (cells != NULL)
    {
        HASHCTL          cell_ctl;
        HTAB            *seen;
        ArrayBuildState *subjects = NULL, *relations = NULL, *objects = NULL;

        MemSet(&cell_ctl, 0, sizeof(cell_ctl));
        cell_ctl.keysize = cell_ctl.entrysize = sizeof(LaplaceObservationCell);
        cell_ctl.hcxt = work;
        seen = hash_create("obs spi exact cells", Max(cell_count, 1),
                           &cell_ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
        for (int i = 0; i < cell_count; ++i)
        {
            bool found = false;
            hash_search(seen, &cells[i], HASH_ENTER, &found);
            if (found)
                continue;
            subjects = accumArrayResult(subjects, hash128_to_datum(&cells[i].subject),
                                        false, BYTEAOID, work);
            relations = accumArrayResult(relations, hash128_to_datum(&cells[i].type),
                                         false, BYTEAOID, work);
            objects = accumArrayResult(objects, hash128_to_datum(&cells[i].object),
                                       false, BYTEAOID, work);
        }
        if (subjects == NULL)
            return;

        params->params[0].value = makeArrayResult(subjects, work);
        params->params[1].value = makeArrayResult(relations, work);
        params->params[2].value = makeArrayResult(objects, work);
        params->params[3].value = sources ? PointerGetDatum(sources) : (Datum) 0;
        for (int i = 0; i < 3; ++i)
        {
            params->params[i].isnull = false;
            params->params[i].ptype = BYTEAARRAYOID;
        }
        params->params[3].isnull = (sources == NULL);
        params->params[3].ptype = BYTEAARRAYOID;

        param_types[0] = BYTEAARRAYOID;
        param_types[1] = BYTEAARRAYOID;
        param_types[2] = BYTEAARRAYOID;
        param_types[3] = BYTEAARRAYOID;
        plan = &legacy_obs_cell_plan;
        query_name = "evidence.observation_cells";
    }
    else
    {
        Datum     *ud = (Datum *) palloc(sizeof(Datum) * (Size) unique_count);
        ArrayType *u_arr;

        for (int i = 0; i < unique_count; ++i)
            ud[i] = hash128_to_datum(&unique[i]);
        u_arr = construct_array(ud, unique_count, BYTEAOID, -1, false, TYPALIGN_INT);

        params->params[0].value = PointerGetDatum(u_arr);
        params->params[0].isnull = false;
        params->params[0].ptype = BYTEAARRAYOID;
        params->params[1].value = sources ? PointerGetDatum(sources) : (Datum) 0;
        params->params[1].isnull = (sources == NULL);
        params->params[1].ptype = BYTEAARRAYOID;
        params->params[2].value = types ? PointerGetDatum(types) : (Datum) 0;
        params->params[2].isnull = (types == NULL);
        params->params[2].ptype = BYTEAARRAYOID;
        params->params[3].value = Int32GetDatum(roles);
        params->params[3].isnull = false;
        params->params[3].ptype = INT4OID;

        param_types[0] = BYTEAARRAYOID;
        param_types[1] = BYTEAARRAYOID;
        param_types[2] = BYTEAARRAYOID;
        param_types[3] = INT4OID;
        plan = &legacy_obs_plan;
        query_name = "evidence.observation_bindings";
    }

    options.params = params;
    before_spi = CurrentMemoryContext;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "observation bindings: SPI_connect failed");

    if (!*plan)
    {
        const char *query = laplace_sql_query_text(query_name);
        if (!query)
            elog(ERROR, "observation bindings: required static query is unavailable");
        *plan = SPI_prepare_cursor(query, 4, param_types, CURSOR_OPT_PARALLEL_OK);
        if (!*plan || SPI_keepplan(*plan) != 0)
            elog(ERROR, "observation bindings: preparing static read failed");
    }

    result = SPI_execute_plan_extended(*plan, &options);
    if (result != SPI_OK_SELECT && result != SPI_OK_UTILITY)
        elog(ERROR, "observation bindings: read failed: %s", SPI_result_code_string(result));

    laplace_spi_finish(spi_top);
    MemoryContextSwitchTo(before_spi);
}

static void
observation_read(ArrayType *operands, ArrayType *sources,
                 ArrayType *types, int roles,
                 const LaplaceObservationCell *cells, int cell_count,
                 LaplaceObservationVisitor visitor, void *context)
{
    MemoryContext  work, previous;
    HASHCTL        ctl = { 0 };
    Datum         *values;
    bool          *nulls;
    int            count;
    hash128_t     *unique;
    int            unique_count = 0;
    ObsDispatch    dispatch = { NULL, NULL, roles, visitor, context };

    validate_filter(operands);
    validate_filter(sources);
    validate_filter(types);
    if (!operands || !visitor)
        elog(ERROR, "observation bindings: operands and visitor are required");
    if (roles < 0 || roles > 3)
        elog(ERROR, "observation bindings: roles must be 0 (none), 1 (subject), 2 (object), or 3 (both)");
    if (roles == 0)
        return;
    if (ArrayGetNItems(ARR_NDIM(operands), ARR_DIMS(operands)) == 0 ||
        (sources && ArrayGetNItems(ARR_NDIM(sources), ARR_DIMS(sources)) == 0) ||
        (types && ArrayGetNItems(ARR_NDIM(types), ARR_DIMS(types)) == 0))
        return;

    work = AllocSetContextCreate(CurrentMemoryContext, "observation bindings",
                                 ALLOCSET_DEFAULT_SIZES);
    previous = MemoryContextSwitchTo(work);

    deconstruct_array(operands, BYTEAOID, -1, false, TYPALIGN_INT, &values, &nulls, &count);
    unique = (hash128_t *) palloc(sizeof(hash128_t) * (Size) count);
    dispatch.next = (int *) palloc(sizeof(int) * (Size) count);
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(OperandEntry);
    ctl.hcxt = work;
    dispatch.operands = hash_create("observation operands", Max(count, 16), &ctl,
                                    HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    for (int i = count - 1; i >= 0; --i)
    {
        hash128_t     id;
        OperandEntry *entry;
        bool          found;

        if (nulls[i])
            continue;
        read_id(values[i], &id);
        entry = (OperandEntry *) hash_search(dispatch.operands, &id, HASH_ENTER, &found);
        dispatch.next[i] = found ? entry->first : -1;
        entry->first = i;
        if (!found)
            unique[unique_count++] = id;
    }

    if (unique_count > 0)
    {
        Oid ns = get_namespace_oid("laplace", true);
        Oid att_rel = OidIsValid(ns) ? get_relname_relid("attestations", ns) : InvalidOid;

        if (OidIsValid(att_rel))
        {
            AclResult acl = pg_class_aclcheck(att_rel, GetUserId(), ACL_SELECT);
            if (acl != ACLCHECK_OK)
                aclcheck_error(acl, OBJECT_TABLE, "laplace.attestations");

            if (obs_rel_has_live_rows(att_rel))
            {
                Relation rel = table_open(att_rel, AccessShareLock);
                bool     use_spi = (rel->rd_rules != NULL ||
                                    check_enable_rls(att_rel, InvalidOid, true) == RLS_ENABLED);
                table_close(rel, AccessShareLock);

                if (use_spi)
                {
                    obs_scan_legacy_attestations_spi(work, unique, unique_count,
                                                     sources, types, roles,
                                                     cells, cell_count, &dispatch);
                }
                else
                {
                    HTAB *op_set = hash_create("obs op set", Max(unique_count, 16), &ctl,
                                               HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
                    HTAB *src_set = sources ? obs_build_id_set(work, "obs src set", sources) : NULL;
                    HTAB *type_set = types ? obs_build_id_set(work, "obs type set", types) : NULL;
                    HTAB *cell_set = NULL;

                    for (int i = 0; i < unique_count; i++)
                    {
                        bool f = false;
                        (void) hash_search(op_set, &unique[i], HASH_ENTER, &f);
                    }
                    if (cells != NULL)
                    {
                        HASHCTL cctl = { 0 };
                        cctl.keysize = cctl.entrysize = sizeof(LaplaceObservationCell);
                        cctl.hcxt = work;
                        cell_set = hash_create("obs exact cells", Max(cell_count, 16), &cctl,
                                               HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
                        for (int i = 0; i < cell_count; i++)
                        {
                            bool f = false;
                            (void) hash_search(cell_set, &cells[i], HASH_ENTER, &f);
                        }
                    }
                    obs_scan_legacy_attestations(att_rel, op_set, src_set, type_set,
                                                 cell_set, roles, &dispatch);
                }
            }
        }

        if (cells != NULL)
            native_obs_cells_exec(cells, cell_count, sources, false, NULL, &dispatch);
        else
            native_obs_bindings_exec(unique, unique_count, sources, types, roles,
                                     false, NULL, &dispatch);
    }

    MemoryContextSwitchTo(previous);
    MemoryContextDelete(work);
}

void
laplace_observation_read(ArrayType *operands, ArrayType *sources,
                         ArrayType *types, int roles,
                         LaplaceObservationVisitor visitor, void *context)
{
    observation_read(operands, sources, types, roles, NULL, 0, visitor, context);
}

void
laplace_observation_read_cells(ArrayType *operands, ArrayType *sources,
                               const LaplaceObservationCell *cells, int cell_count,
                               LaplaceObservationVisitor visitor, void *context)
{
    if (cell_count < 0 || (cell_count > 0 && !cells))
        elog(ERROR, "observation bindings: invalid exact cell set");
    if (cell_count == 0)
        return;
    observation_read(operands, sources, NULL, 3, cells, cell_count, visitor, context);
}

static void
emit_observation(int ordinal, int16 role, const LaplaceObservation *row, void *context)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) context;
    Datum values[] = {
        Int32GetDatum(ordinal), Int16GetDatum(role), hash128_to_datum(&row->id),
        hash128_to_datum(&row->subject), hash128_to_datum(&row->type),
        hash128_to_datum(&row->object), hash128_to_datum(&row->source),
        hash128_to_datum(&row->context), Int16GetDatum(row->outcome),
        Int64GetDatum(row->occurrences)
    };
    bool nulls[] = {
        false, false, false, false, false, row->object_null,
        row->source_null, row->context_null, row->outcome == LAPLACE_OBSERVATION_SCORED, false
    };
    tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
    for (int i = 2; i <= 7; ++i)
        pfree(DatumGetPointer(values[i]));
}

Datum
pg_laplace_observation_bindings(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;
    laplace_observation_read(PG_GETARG_ARRAYTYPE_P(0),
                             PG_ARGISNULL(1) ? NULL : PG_GETARG_ARRAYTYPE_P(1),
                             PG_ARGISNULL(2) ? NULL : PG_GETARG_ARRAYTYPE_P(2),
                             PG_ARGISNULL(3) ? 3 : PG_GETARG_INT32(3),
                             emit_observation, fcinfo->resultinfo);
    return (Datum) 0;
}

Datum
pg_laplace_native_observation_cells(PG_FUNCTION_ARGS)
{
    ReturnSetInfo          *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum                  *sv, *rv, *ov;
    bool                   *sn, *rn, *on;
    int                     ns = 0, nr = 0, no = 0, n;
    LaplaceObservationCell *cells;
    int                     valid = 0;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1) || PG_ARGISNULL(2))
        return (Datum) 0;

    deconstruct_array(PG_GETARG_ARRAYTYPE_P(0), BYTEAOID, -1, false, TYPALIGN_INT, &sv, &sn, &ns);
    deconstruct_array(PG_GETARG_ARRAYTYPE_P(1), BYTEAOID, -1, false, TYPALIGN_INT, &rv, &rn, &nr);
    deconstruct_array(PG_GETARG_ARRAYTYPE_P(2), BYTEAOID, -1, false, TYPALIGN_INT, &ov, &on, &no);
    n = Min(ns, Min(nr, no));
    if (n > 0)
    {
        cells = (LaplaceObservationCell *) palloc(sizeof(LaplaceObservationCell) * (Size) n);
        for (int i = 0; i < n; i++)
        {
            if (sn[i] || rn[i] || on[i])
                continue;
            read_id(sv[i], &cells[valid].subject);
            read_id(rv[i], &cells[valid].type);
            read_id(ov[i], &cells[valid].object);
            valid++;
        }
        if (valid > 0)
            native_obs_cells_exec(cells, valid,
                                  PG_ARGISNULL(3) ? NULL : PG_GETARG_ARRAYTYPE_P(3),
                                  true, rsinfo, NULL);
        pfree(cells);
    }
    pfree(sv); pfree(sn);
    pfree(rv); pfree(rn);
    pfree(ov); pfree(on);
    return (Datum) 0;
}

Datum
pg_laplace_native_observation_bindings(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum         *vals;
    bool          *nulls;
    int            n = 0;
    hash128_t     *ops;
    int            valid = 0;
    int32          roles;
    ArrayType     *types = PG_ARGISNULL(2) ? NULL : PG_GETARG_ARRAYTYPE_P(2);
    ArrayType     *sources = PG_ARGISNULL(1) ? NULL : PG_GETARG_ARRAYTYPE_P(1);

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0) || PG_ARGISNULL(3))
        return (Datum) 0;
    roles = PG_GETARG_INT32(3);
    if (roles == 0)
        return (Datum) 0;

    if (types != NULL && ArrayGetNItems(ARR_NDIM(types), ARR_DIMS(types)) == 0)
        return (Datum) 0;

    deconstruct_array(PG_GETARG_ARRAYTYPE_P(0), BYTEAOID, -1, false, TYPALIGN_INT, &vals, &nulls, &n);
    if (n > 0)
    {
        ops = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n);
        for (int i = 0; i < n; i++)
        {
            if (!nulls[i])
                read_id(vals[i], &ops[valid++]);
        }
        if (valid > 0)
        {
            HTAB *seen_ops;
            HASHCTL octl = { 0 };
            int uniq_cnt = 0;

            octl.keysize = octl.entrysize = sizeof(hash128_t);
            octl.hcxt = CurrentMemoryContext;
            seen_ops = hash_create("nob dedup ops", Max(valid, 16), &octl,
                                   HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
            for (int i = 0; i < valid; i++)
            {
                bool f = false;
                (void) hash_search(seen_ops, &ops[i], HASH_ENTER, &f);
                if (!f)
                    ops[uniq_cnt++] = ops[i];
            }
            hash_destroy(seen_ops);
            native_obs_bindings_exec(ops, uniq_cnt, sources, types, roles,
                                     true, rsinfo, NULL);
        }
        pfree(ops);
    }
    pfree(vals);
    pfree(nulls);
    return (Datum) 0;
}
