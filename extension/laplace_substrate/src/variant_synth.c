





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
#include "utils/memutils.h"
#include "utils/timestamp.h"
#include "utils/tuplestore.h"
#include "lib/stringinfo.h"
#include "common/pg_prng.h"
#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_laplace_variant_walk);
PG_FUNCTION_INFO_V1(pg_laplace_respell_variant);

static Datum
consensus_peer_lookup(Datum id, int32 k)
{
    Oid   argtypes[2] = { BYTEAOID, INT4OID };
    Datum args[2];
    char  nulls[3] = "  ";
    int   rc;
    bool  isnull;

    args[0] = id;
    args[1] = Int32GetDatum(k);

    rc = SPI_execute_with_args(
        "SELECT laplace.consensus_peer($1, $2)",
        2, argtypes, args, nulls, true, 1);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "consensus_peer: query failed: %s", SPI_result_code_string(rc));
    if (SPI_processed == 0)
        return (Datum) 0;

    return copy_bytea_datum(SPI_getbinval(SPI_tuptable->vals[0],
                                          SPI_tuptable->tupdesc, 1, &isnull));
}

static char *
render_entity_text(Datum id, int32 max_depth)
{
    Oid   argtypes[2] = { BYTEAOID, INT4OID };
    Datum args[2];
    char  nulls[3] = "  ";
    int   rc;
    bool  isnull;
    text *t;

    args[0] = id;
    args[1] = Int32GetDatum(max_depth);
    nulls[1] = ' ';

    rc = SPI_execute_with_args(
        "SELECT laplace.render_text($1, $2)", 2, argtypes, args, nulls, true, 1);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "variant_walk: render_text failed: %s", SPI_result_code_string(rc));
    if (SPI_processed == 0)
        return pstrdup("");

    t = DatumGetTextPP(SPI_getbinval(SPI_tuptable->vals[0],
                                      SPI_tuptable->tupdesc, 1, &isnull));
    if (isnull)
        return pstrdup("");
    return text_to_cstring(t);
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
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1] = { id };
    int   rc;
    WalkPoint *pts;

    rc = SPI_execute_with_args(
        "SELECT entity_id, run_length, ctier "
        "FROM laplace.trajectory_unpacked_points($1)",
        1, argtypes, args, NULL, true, 0);
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

static int32
entity_tier(Datum id)
{
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1] = { id };
    int   rc;
    bool  isnull;

    rc = SPI_execute_with_args(
        "SELECT laplace.entity_tier_of($1)",
        1, argtypes, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return -1;
    return DatumGetInt32(SPI_getbinval(SPI_tuptable->vals[0],
                                       SPI_tuptable->tupdesc, 1, &isnull));
}

static bool
has_trajectory(Datum id)
{
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1] = { id };
    int   rc;
    bool  isnull;

    rc = SPI_execute_with_args(
        "SELECT laplace.entity_has_trajectory($1)",
        1, argtypes, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return false;
    return DatumGetBool(SPI_getbinval(SPI_tuptable->vals[0],
                                      SPI_tuptable->tupdesc, 1, &isnull));
}

static char *
variant_walk_impl(Datum id, float8 swap, int32 k, int32 depth)
{
    int32      tier = entity_tier(id);
    StringInfoData out;
    WalkPoint *pts;
    int        n_pts;
    bool       first = true;

    if (tier < 0 || tier <= 2)
        return render_entity_text(id, 64);
    if (!has_trajectory(id))
        return render_entity_text(id, 64);

    pts = fetch_trajectory_points(id, &n_pts);
    if (pts == NULL || n_pts == 0)
        return render_entity_text(id, 64);

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

            piece = variant_walk_impl(cur, swap, k, depth - 1);
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
    walk_out = variant_walk_impl(id, swap, k, depth);
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
    walk_text = variant_walk_impl(seed, swap, k, depth);
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
