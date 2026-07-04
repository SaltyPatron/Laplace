





#include "postgres.h"

#include <math.h>

#include "access/htup_details.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/fmgrprotos.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "utils/timestamp.h"
#include "utils/tuplestore.h"
#include "lib/stringinfo.h"
#include "common/pg_prng.h"
#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_laplace_variant_walk);
PG_FUNCTION_INFO_V1(pg_laplace_respell_variant);

/*
 * Per-node SPI plans are prepared once (static) and re-executed via
 * SPI_execute_plan in the recursion, instead of SPI_execute_with_args
 * re-planning on every visited node. tier + has_trajectory are collapsed
 * into ONE round-trip. Both changes are execution-efficiency only: the
 * queries, arguments, and the values read out are identical to before.
 */
static SPIPlanPtr tier_traj_plan   = NULL;   /* (bytea) -> (tier, has_traj) */
static SPIPlanPtr traj_points_plan = NULL;   /* (bytea) -> trajectory points */
static SPIPlanPtr render_plan      = NULL;   /* (bytea, int) -> render_text  */
static SPIPlanPtr peer_plan        = NULL;   /* (bytea, int) -> consensus_peer */

static void
ensure_variant_plans(void)
{
    if (tier_traj_plan == NULL)
    {
        Oid        argtypes[1] = { BYTEAOID };
        SPIPlanPtr plan = SPI_prepare(
            "SELECT laplace.entity_tier_of($1), laplace.entity_has_trajectory($1)",
            1, argtypes);
        if (plan == NULL)
            elog(ERROR, "variant_walk: SPI_prepare(tier/traj) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "variant_walk: SPI_keepplan(tier/traj) failed");
        tier_traj_plan = plan;
    }
    if (traj_points_plan == NULL)
    {
        Oid        argtypes[1] = { BYTEAOID };
        SPIPlanPtr plan = SPI_prepare(
            "SELECT entity_id, run_length, ctier "
            "FROM laplace.trajectory_unpacked_points($1)",
            1, argtypes);
        if (plan == NULL)
            elog(ERROR, "variant_walk: SPI_prepare(trajectory) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "variant_walk: SPI_keepplan(trajectory) failed");
        traj_points_plan = plan;
    }
    if (render_plan == NULL)
    {
        Oid        argtypes[2] = { BYTEAOID, INT4OID };
        SPIPlanPtr plan = SPI_prepare(
            "SELECT laplace.render_text($1, $2)", 2, argtypes);
        if (plan == NULL)
            elog(ERROR, "variant_walk: SPI_prepare(render_text) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "variant_walk: SPI_keepplan(render_text) failed");
        render_plan = plan;
    }
    if (peer_plan == NULL)
    {
        Oid        argtypes[2] = { BYTEAOID, INT4OID };
        SPIPlanPtr plan = SPI_prepare(
            "SELECT laplace.consensus_peer($1, $2)", 2, argtypes);
        if (plan == NULL)
            elog(ERROR, "variant_walk: SPI_prepare(consensus_peer) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "variant_walk: SPI_keepplan(consensus_peer) failed");
        peer_plan = plan;
    }
}

/* Memo: 16-byte entity id -> its deterministic render_text output.
 * render_text has no randomness, so caching it changes nothing about the
 * walk's output or its PRNG draw order -- it only avoids re-fetching (and
 * re-descending, since render_text re-walks the subtree in SQL) the same
 * entity. Callers pfree the string they receive, so a memo hit returns a
 * fresh pstrdup copy while the memo retains ownership of its own copy. */
typedef struct VariantMemoEntry
{
    char  key[16];
    char *text;
} VariantMemoEntry;

static Datum
consensus_peer_lookup(Datum id, int32 k)
{
    Datum args[2];
    int   rc;
    bool  isnull;

    args[0] = id;
    args[1] = Int32GetDatum(k);

    rc = SPI_execute_plan(peer_plan, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "consensus_peer: query failed: %s", SPI_result_code_string(rc));
    if (SPI_processed == 0)
        return (Datum) 0;

    return copy_bytea_datum(SPI_getbinval(SPI_tuptable->vals[0],
                                          SPI_tuptable->tupdesc, 1, &isnull));
}

static char *
render_entity_text(HTAB *memo, Datum id, int32 max_depth)
{
    char  key[16];
    bool  found = false;
    VariantMemoEntry *e = NULL;
    bytea *idb = DatumGetByteaPP(id);
    Datum args[2];
    int   rc;
    bool  isnull;
    char *rendered;

    if (VARSIZE_ANY_EXHDR(idb) == 16)
    {
        memcpy(key, VARDATA_ANY(idb), 16);
        e = (VariantMemoEntry *) hash_search(memo, key, HASH_ENTER, &found);
        if (found)
            return pstrdup(e->text);
    }

    args[0] = id;
    args[1] = Int32GetDatum(max_depth);

    rc = SPI_execute_plan(render_plan, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "variant_walk: render_text failed: %s", SPI_result_code_string(rc));
    if (SPI_processed == 0)
        rendered = pstrdup("");
    else
    {
        text *t = DatumGetTextPP(SPI_getbinval(SPI_tuptable->vals[0],
                                               SPI_tuptable->tupdesc, 1, &isnull));
        rendered = isnull ? pstrdup("") : text_to_cstring(t);
    }

    if (e != NULL)
    {
        e->text = rendered;         /* memo owns this copy */
        return pstrdup(rendered);   /* caller owns (and may pfree) the return */
    }
    return rendered;
}

typedef struct WalkPoint
{
    Datum  cid;
    int32  run;
    int32  ctier;
} WalkPoint;

static WalkPoint *
fetch_trajectory_points(Datum id, int *out_n)
{
    Datum args[1] = { id };
    int   rc;
    WalkPoint *pts;

    rc = SPI_execute_plan(traj_points_plan, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "variant_walk: trajectory unpack failed: %s",
             SPI_result_code_string(rc));

    *out_n = (int) SPI_processed;
    if (*out_n == 0)
        return NULL;

    pts = (WalkPoint *) palloc(sizeof(WalkPoint) * (*out_n));
    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple tup = SPI_tuptable->vals[r];
        TupleDesc td  = SPI_tuptable->tupdesc;
        bool      isnull;

        pts[r].cid = copy_bytea_datum(SPI_getbinval(tup, td, 1, &isnull));
        pts[r].run = DatumGetInt32(SPI_getbinval(tup, td, 2, &isnull));
        pts[r].ctier = DatumGetInt32(SPI_getbinval(tup, td, 3, &isnull));
        if (isnull)
            pts[r].ctier = 0;
    }
    return pts;
}

/* One round-trip for both tier and has_trajectory. Mirrors the two former
 * scalar lookups exactly: a failed/empty result yields tier=-1 (leaf), and
 * has_traj is only consulted when tier>2 -- so eagerly evaluating it for
 * low-tier entities is harmless and never affects output. */
static void
entity_tier_and_traj(Datum id, int32 *tier_out, bool *traj_out)
{
    Datum args[1] = { id };
    bool  isnull;
    int   rc = SPI_execute_plan(tier_traj_plan, args, NULL, true, 1);

    if (rc != SPI_OK_SELECT || SPI_processed == 0)
    {
        *tier_out = -1;
        *traj_out = false;
        return;
    }
    *tier_out = DatumGetInt32(SPI_getbinval(SPI_tuptable->vals[0],
                                            SPI_tuptable->tupdesc, 1, &isnull));
    *traj_out = DatumGetBool(SPI_getbinval(SPI_tuptable->vals[0],
                                           SPI_tuptable->tupdesc, 2, &isnull));
}

static char *
variant_walk_impl(HTAB *memo, Datum id, float8 swap, int32 k, int32 depth)
{
    int32      tier;
    bool       has_traj;
    StringInfoData out;
    WalkPoint *pts;
    int        n_pts;
    bool       first = true;

    entity_tier_and_traj(id, &tier, &has_traj);

    if (tier < 0 || tier <= 2)
        return render_entity_text(memo, id, 64);
    if (!has_traj)
        return render_entity_text(memo, id, 64);

    pts = fetch_trajectory_points(id, &n_pts);
    if (pts == NULL || n_pts == 0)
        return render_entity_text(memo, id, 64);

    initStringInfo(&out);
    for (int p = 0; p < n_pts; p++)
    {
        for (int i = 0; i < pts[p].run; i++)
        {
            Datum  cur = pts[p].cid;
            char  *piece;
            float8 rnd;

            if (depth > 0 && pts[p].ctier > 2)
            {
                rnd = pg_prng_double(&pg_global_prng_state);
                if (rnd < swap)
                {
                    Datum peer = consensus_peer_lookup(cur, k);
                    if (peer != (Datum) 0)
                        cur = peer;
                }
            }

            piece = variant_walk_impl(memo, cur, swap, k, depth - 1);
            if (piece != NULL && piece[0] != '\0')
            {
                if (!first)
                    appendStringInfoChar(&out, ' ');
                appendStringInfoString(&out, piece);
                first = false;
            }
            if (piece != NULL)
                pfree(piece);
        }
    }
    pfree(pts);
    return out.data;
}

/* Create the per-walk render memo in the current (SPI proc) context, which
 * lives until SPI_finish -- the same lifetime as the strings it caches. */
static HTAB *
variant_memo_create(void)
{
    HASHCTL ctl;

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(VariantMemoEntry);
    return hash_create("variant_walk memo", 1024, &ctl, HASH_ELEM | HASH_BLOBS);
}

Datum
pg_laplace_variant_walk(PG_FUNCTION_ARGS)
{
    Datum  id;
    float8 swap;
    int32  k, depth;
    char  *walk_out;
    MemoryContext caller_cxt = CurrentMemoryContext;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    id    = PG_GETARG_DATUM(0);
    swap  = PG_ARGISNULL(1) ? 0.3 : PG_GETARG_FLOAT8(1);
    k     = PG_ARGISNULL(2) ? 6 : PG_GETARG_INT32(2);
    depth = PG_ARGISNULL(3) ? 4 : PG_GETARG_INT32(3);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "variant_walk: SPI_connect failed");
    ensure_variant_plans();
    walk_out = variant_walk_impl(variant_memo_create(), id, swap, k, depth);
    SPI_finish();

    if (walk_out == NULL || walk_out[0] == '\0')
        PG_RETURN_TEXT_P(cstring_to_text(""));
    {
        MemoryContext old = MemoryContextSwitchTo(caller_cxt);
        text *result = cstring_to_text(walk_out);
        MemoryContextSwitchTo(old);
        pfree(walk_out);
        PG_RETURN_TEXT_P(result);
    }
}

Datum
pg_laplace_respell_variant(PG_FUNCTION_ARGS)
{
    text  *node_type;
    text  *modality;
    float8 swap;
    int32  k, depth;
    Oid    argtypes[2] = { TEXTOID, TEXTOID };
    Datum  args[2];
    char   nulls[3] = "  ";
    int    rc;
    bool   isnull;
    Datum  seed;
    char  *walk_text;
    MemoryContext caller_cxt = CurrentMemoryContext;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    node_type = PG_GETARG_TEXT_PP(0);
    modality  = PG_ARGISNULL(1) ? cstring_to_text("c-sharp") : PG_GETARG_TEXT_PP(1);
    swap      = PG_ARGISNULL(2) ? 0.3 : PG_GETARG_FLOAT8(2);
    k         = PG_ARGISNULL(3) ? 6 : PG_GETARG_INT32(3);
    depth     = PG_ARGISNULL(4) ? 4 : PG_GETARG_INT32(4);

    args[0] = PointerGetDatum(modality);
    args[1] = PointerGetDatum(node_type);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "respell_variant: SPI_connect failed");
    ensure_variant_plans();

    rc = SPI_execute_with_args(
        "SELECT laplace.respell_variant_seed($1, $2)",
        2, argtypes, args, nulls, true, 1);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "respell_variant: seed lookup failed: %s", SPI_result_code_string(rc));
    if (SPI_processed == 0)
    {
        SPI_finish();
        PG_RETURN_NULL();
    }

    seed = copy_bytea_datum(SPI_getbinval(SPI_tuptable->vals[0],
                                          SPI_tuptable->tupdesc, 1, &isnull));
    walk_text = variant_walk_impl(variant_memo_create(), seed, swap, k, depth);
    SPI_finish();

    if (walk_text == NULL)
        PG_RETURN_NULL();
    {
        MemoryContext old = MemoryContextSwitchTo(caller_cxt);
        text *result = cstring_to_text(walk_text);
        MemoryContextSwitchTo(old);
        pfree(walk_text);
        PG_RETURN_TEXT_P(result);
    }
}
