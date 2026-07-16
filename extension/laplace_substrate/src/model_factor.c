#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/builtins.h"

#include "spi_common.h"
#include "spi_nested.h"

#include "laplace/core/mantissa.h"

/*
 * Model-factor readers (campaign doc 26 item B, v1 varlena path): the factor
 * trajectories deposited by the model lane ARE the model's testimony — these
 * functions make them SQL-queryable. SQL is the doorway; the loop lives here
 * (never WITH RECURSIVE). Trajectory layout = the FactorTrajectory law
 * (Laplace.Core/FactorTrajectory.cs — the single shared convention):
 *   vertex 0                : arena as one raw f32 in a FACTOR vertex
 *   per token t             : 1 testimony header (token entity id, salience
 *                             score fp1e9, games = dim) then ceil(dim/6)
 *                             FACTOR vertices of raw f32 factors
 * v1 reads the PostGIS varlena via ST_AsBinary and scans headers linearly;
 * the factor perfcache blob (while-hot law) replaces both when it lands.
 */

typedef struct
{
    double *xyzm;        /* palloc'd copy of the vertex doubles            */
    int     n_vertices;
    int     dim;         /* factor dimensionality (source_dim column)      */
    int     tokens;      /* token count (n_constituents column)            */
    int     stride;      /* vertices per token = 1 + ceil(dim/6)           */
} factor_traj_t;

static const char *TRAJ_QUERY =
    "SELECT ST_AsBinary(trajectory), source_dim, n_constituents "
    "FROM laplace.physicalities WHERE entity_id = $1 AND type = 3";

/* Fetch + WKB-parse one factor trajectory by carrier entity id. WKB
 * LineStringZM LE: byte order 1, uint32 type, uint32 npoints, npoints x 4
 * float8. Returns false if no row. */
static bool
fetch_factor_traj(Datum slice_id, factor_traj_t *out, const char *who)
{
    Oid    argtypes[1] = { BYTEAOID };
    Datum  args[1] = { slice_id };
    bool   isnull;
    bytea *wkb;
    const uint8 *raw;
    uint32 npoints;
    Datum  d;

    if (SPI_execute_with_args(TRAJ_QUERY, 1, argtypes, args, NULL, true, 1)
            != SPI_OK_SELECT)
        elog(ERROR, "%s: trajectory fetch failed", who);
    if (SPI_processed == 0)
        return false;

    d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
    if (isnull)
        return false;
    wkb = DatumGetByteaPP(d);
    raw = (const uint8 *) VARDATA_ANY(wkb);
    if (VARSIZE_ANY_EXHDR(wkb) < 9 || raw[0] != 1)
        ereport(ERROR, (errmsg("%s: unexpected WKB shape", who)));
    memcpy(&npoints, raw + 5, 4);
    if ((Size) VARSIZE_ANY_EXHDR(wkb) < 9 + (Size) npoints * 32)
        ereport(ERROR, (errmsg("%s: WKB truncated", who)));

    out->n_vertices = (int) npoints;
    out->xyzm = (double *) palloc((Size) npoints * 4 * sizeof(double));
    memcpy(out->xyzm, raw + 9, (Size) npoints * 4 * sizeof(double));

    d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 2, &isnull);
    out->dim = isnull ? 0 : DatumGetInt32(d);
    d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 3, &isnull);
    out->tokens = isnull ? 0 : DatumGetInt32(d);

    if (out->dim <= 0 || out->tokens <= 0)
        ereport(ERROR, (errmsg("%s: physicality lacks dim/token metadata", who)));
    out->stride = 1 + (out->dim + (int) LAPLACE_FACTOR_VALUES_PER_VERTEX - 1)
                      / (int) LAPLACE_FACTOR_VALUES_PER_VERTEX;
    if (out->n_vertices != 1 + out->tokens * out->stride)
        ereport(ERROR, (errmsg("%s: trajectory of %d vertices does not fit "
                               "factor layout (tokens=%d dim=%d)",
                               who, out->n_vertices, out->tokens, out->dim)));
    return true;
}

/* Linear header scan: token entity id -> ordinal, or -1. The perfcache blob
 * replaces this with a hash probe. */
static int
find_token_ordinal(const factor_traj_t *t, const hash128_t *token)
{
    for (int i = 0; i < t->tokens; i++)
    {
        hash128_t obj;
        int64     score;
        uint16    games, ord;
        const double *v = t->xyzm + ((Size) (1 + i * t->stride)) * 4;

        if (laplace_testimony_unpack_vertex(v, &obj, &score, &games, &ord) != 0)
            ereport(ERROR, (errmsg("factor trajectory: bad header at %d", i)));
        if (obj.lo == token->lo && obj.hi == token->hi)
            return i;
    }
    return -1;
}

/* Unpack one token's factor run into caller-provided float buffer[dim]. */
static void
unpack_token_factors(const factor_traj_t *t, int ord, float *out)
{
    int fvpt = t->stride - 1;
    int have = 0;

    for (int v = 0; v < fvpt; v++)
    {
        float   vals[LAPLACE_FACTOR_VALUES_PER_VERTEX];
        uint8_t cnt = 0;
        const double *vert = t->xyzm + ((Size) (1 + ord * t->stride + 1 + v)) * 4;

        if (laplace_factor_unpack_vertex(vert, vals, &cnt) != 0)
            ereport(ERROR, (errmsg("factor trajectory: bad factor vertex")));
        for (int j = 0; j < cnt && have < t->dim; j++)
            out[have++] = vals[j];
    }
    if (have != t->dim)
        ereport(ERROR, (errmsg("factor run holds %d values, expected %d",
                               have, t->dim)));
}

PG_FUNCTION_INFO_V1(pg_laplace_model_pair_score);

/* (q_slice bytea, k_slice bytea, tok_a bytea, tok_b bytea) -> float8:
 * q_h(A) . k_h(B) reconstructed from the deposited factor trajectories —
 * the model's own coupling, by lookup. NULL when either token is absent. */
Datum
pg_laplace_model_pair_score(PG_FUNCTION_ARGS)
{
    hash128_t tok_a = datum_to_hash128(PG_GETARG_DATUM(2));
    hash128_t tok_b = datum_to_hash128(PG_GETARG_DATUM(3));
    factor_traj_t q, k;
    bool   spi_top = false;
    int    oa, ob;
    float *fa, *fb;
    double dot = 0.0;

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "model_pair_score: SPI_connect failed");
    if (!fetch_factor_traj(PG_GETARG_DATUM(0), &q, "model_pair_score") ||
        !fetch_factor_traj(PG_GETARG_DATUM(1), &k, "model_pair_score"))
    {
        laplace_spi_finish(spi_top);
        PG_RETURN_NULL();
    }
    if (q.dim != k.dim)
        ereport(ERROR, (errmsg("model_pair_score: q dim %d != k dim %d",
                               q.dim, k.dim)));

    oa = find_token_ordinal(&q, &tok_a);
    ob = find_token_ordinal(&k, &tok_b);
    if (oa < 0 || ob < 0)
    {
        laplace_spi_finish(spi_top);
        PG_RETURN_NULL();
    }

    fa = (float *) palloc(sizeof(float) * q.dim);
    fb = (float *) palloc(sizeof(float) * k.dim);
    unpack_token_factors(&q, oa, fa);
    unpack_token_factors(&k, ob, fb);
    for (int j = 0; j < q.dim; j++)
        dot += (double) fa[j] * (double) fb[j];

    laplace_spi_finish(spi_top);
    PG_RETURN_FLOAT8(dot);
}

PG_FUNCTION_INFO_V1(pg_laplace_model_row_topk);

/* (q_slice bytea, k_slice bytea, tok bytea, k int4)
 *   -> SETOF (token bytea, score float8):
 * the k strongest partners of tok in this circuit, computed by one native
 * scan over the k-side factor runs — the transformer's attention row as an
 * indexed lookup, strongest first. */
Datum
pg_laplace_model_row_topk(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    hash128_t tok = datum_to_hash128(PG_GETARG_DATUM(2));
    int32     want = PG_ARGISNULL(3) ? 20 : PG_GETARG_INT32(3);
    factor_traj_t q, k;
    bool      spi_top = false;
    int       oa;
    float    *fq, *fk;
    hash128_t *top_tok;
    double   *top_score;
    int       filled = 0;

    if (want < 1 || want > 10000)
        ereport(ERROR, (errmsg("model_row_topk: k must be in [1,10000]")));

    InitMaterializedSRF(fcinfo, 0);

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "model_row_topk: SPI_connect failed");
    if (!fetch_factor_traj(PG_GETARG_DATUM(0), &q, "model_row_topk") ||
        !fetch_factor_traj(PG_GETARG_DATUM(1), &k, "model_row_topk"))
    {
        laplace_spi_finish(spi_top);
        return (Datum) 0;
    }
    if (q.dim != k.dim)
        ereport(ERROR, (errmsg("model_row_topk: q dim %d != k dim %d", q.dim, k.dim)));

    oa = find_token_ordinal(&q, &tok);
    if (oa < 0)
    {
        laplace_spi_finish(spi_top);
        return (Datum) 0;
    }

    fq = (float *) palloc(sizeof(float) * q.dim);
    fk = (float *) palloc(sizeof(float) * k.dim);
    unpack_token_factors(&q, oa, fq);

    /* Bounded insertion top-k (want is small); min at slot filled-1. */
    top_tok = (hash128_t *) palloc(sizeof(hash128_t) * want);
    top_score = (double *) palloc(sizeof(double) * want);

    for (int t = 0; t < k.tokens; t++)
    {
        hash128_t obj;
        int64     sfp;
        uint16    games, ord;
        double    dot = 0.0;
        const double *hv = k.xyzm + ((Size) (1 + t * k.stride)) * 4;

        if (laplace_testimony_unpack_vertex(hv, &obj, &sfp, &games, &ord) != 0)
            ereport(ERROR, (errmsg("model_row_topk: bad header at %d", t)));
        unpack_token_factors(&k, t, fk);
        for (int j = 0; j < k.dim; j++)
            dot += (double) fq[j] * (double) fk[j];

        if (filled < want)
        {
            int i = filled++;
            while (i > 0 && top_score[i - 1] < dot)
            {
                top_score[i] = top_score[i - 1];
                top_tok[i] = top_tok[i - 1];
                i--;
            }
            top_score[i] = dot;
            top_tok[i] = obj;
        }
        else if (dot > top_score[want - 1])
        {
            int i = want - 1;
            while (i > 0 && top_score[i - 1] < dot)
            {
                top_score[i] = top_score[i - 1];
                top_tok[i] = top_tok[i - 1];
                i--;
            }
            top_score[i] = dot;
            top_tok[i] = obj;
        }
    }

    for (int i = 0; i < filled; i++)
    {
        Datum values[2];
        bool  nulls[2] = { false, false };

        values[0] = hash128_to_datum(&top_tok[i]);
        values[1] = Float8GetDatum(top_score[i]);
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
    }

    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
