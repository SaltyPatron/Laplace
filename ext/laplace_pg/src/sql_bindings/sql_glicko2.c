/*
 * sql_glicko2.c — SQL function bindings for Glicko2Service.
 *
 * Phase 2 / Track C / C3.
 *
 * Exposes laplace_glicko2_apply as a PostgreSQL function. Significance
 * updates can run as set-based SQL aggregates (one rating period per
 * substrate-source × rated entity batch) without managed-side round-trips.
 */

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "catalog/pg_type.h"

#include "laplace_pg/glicko2.h"

/*
 * laplace_glicko2_apply(
 *   in_mu     double precision,
 *   in_phi    double precision,
 *   in_sigma  double precision,
 *   in_games  integer,
 *   opp_mu    double precision[],
 *   opp_phi   double precision[],
 *   scores    double precision[],
 *   weights   double precision[],
 *   tau       double precision DEFAULT 0.5
 * ) RETURNS TABLE(out_mu double precision, out_phi double precision,
 *                 out_sigma double precision, out_games integer)
 */
PG_FUNCTION_INFO_V1(pg_laplace_glicko2_apply);
Datum pg_laplace_glicko2_apply(PG_FUNCTION_ARGS)
{
    laplace_glicko2_state_t in;
    in.mu     = PG_GETARG_FLOAT8(0);
    in.phi    = PG_GETARG_FLOAT8(1);
    in.sigma  = PG_GETARG_FLOAT8(2);
    in.games  = PG_GETARG_INT32(3);

    ArrayType *opp_mu  = PG_GETARG_ARRAYTYPE_P(4);
    ArrayType *opp_phi = PG_GETARG_ARRAYTYPE_P(5);
    ArrayType *scores  = PG_GETARG_ARRAYTYPE_P(6);
    ArrayType *weights = PG_GETARG_ARRAYTYPE_P(7);
    double     tau     = PG_GETARG_FLOAT8(8);

    Datum *mu_d, *phi_d, *score_d, *weight_d;
    bool  *mu_n, *phi_n, *score_n, *weight_n;
    int    n_mu, n_phi, n_score, n_weight;

    deconstruct_array(opp_mu,  FLOAT8OID, 8, true, 'd', &mu_d,     &mu_n,     &n_mu);
    deconstruct_array(opp_phi, FLOAT8OID, 8, true, 'd', &phi_d,    &phi_n,    &n_phi);
    deconstruct_array(scores,  FLOAT8OID, 8, true, 'd', &score_d,  &score_n,  &n_score);
    deconstruct_array(weights, FLOAT8OID, 8, true, 'd', &weight_d, &weight_n, &n_weight);

    if (n_mu != n_phi || n_mu != n_score || n_mu != n_weight)
    {
        ereport(ERROR, (errmsg("opp_mu, opp_phi, scores, weights arrays must all have the same length")));
    }

    laplace_glicko2_observation_t *obs =
        (laplace_glicko2_observation_t *) palloc((Size) n_mu * sizeof(laplace_glicko2_observation_t));
    for (int i = 0; i < n_mu; ++i)
    {
        obs[i].opponent_mu  = mu_n[i]     ? 0.0 : DatumGetFloat8(mu_d[i]);
        obs[i].opponent_phi = phi_n[i]    ? 0.0 : DatumGetFloat8(phi_d[i]);
        obs[i].score        = score_n[i]  ? 0.5 : DatumGetFloat8(score_d[i]);
        obs[i].weight       = weight_n[i] ? 1.0 : DatumGetFloat8(weight_d[i]);
    }

    laplace_glicko2_state_t out;
    laplace_glicko2_apply(&in, obs, (size_t) n_mu, tau, &out);
    pfree(obs);

    /* Build the row (composite type) with the four output columns. */
    TupleDesc tupdesc;
    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE)
    {
        ereport(ERROR, (errmsg("laplace_glicko2_apply: function must return a composite type")));
    }
    BlessTupleDesc(tupdesc);

    Datum values[4];
    bool  nulls[4] = {false, false, false, false};
    values[0] = Float8GetDatum(out.mu);
    values[1] = Float8GetDatum(out.phi);
    values[2] = Float8GetDatum(out.sigma);
    values[3] = Int32GetDatum(out.games);
    HeapTuple tup = heap_form_tuple(tupdesc, values, nulls);
    PG_RETURN_DATUM(HeapTupleGetDatum(tup));
}

#endif /* LAPLACE_BUILD_PG_EXTENSION */
