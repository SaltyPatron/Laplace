/*
 * explore_web.c — SPI beam crawl for the explore consensus-web viz.
 *
 * Unlike foundry_crawl (vocab / tier-2 emit only), this:
 *   - walks undirected consensus (out ∪ in) via explore_web_neighbors
 *   - admits every tier
 *   - beams ≤ fanout NEW nodes per hop (pool-safe: one SPI connection)
 *   - emits typed edges for the retained subgraph
 *
 * C# then labels endpoints with render_text_fast / label_or_hex in one query.
 */

#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"

#include "laplace/core/hash128.h"
#include "laplace/core/glicko2.h"
#include "spi_common.h"
#include "spi_nested.h"

PG_FUNCTION_INFO_V1(pg_laplace_explore_web);

typedef struct {
	char	key[16];
	int		hop;
} SeenNode;

typedef struct {
	hash128_t	nbr;
	hash128_t	type_id;
	hash128_t	from;
	int64		rating;
	int64		rd;
	int64		witnesses;
	bool		outbound;
	double		strength;
} EdgeCand;

typedef struct {
	hash128_t	source;
	hash128_t	type_id;
	hash128_t	object;
	int16		hop;
	int64		rating;
	int64		rd;
	int64		witnesses;
} EdgeOut;

static int
cand_cmp_desc(const void *a, const void *b)
{
	double sa = ((const EdgeCand *) a)->strength;
	double sb = ((const EdgeCand *) b)->strength;

	if (sa < sb)
		return 1;
	if (sa > sb)
		return -1;
	return 0;
}

static double
edge_strength(int64 rating, int64 rd)
{
	double eff = (double) laplace_effective_mu_fp(rating, rd);
	double diff = (eff - (double) LAPLACE_GLICKO2_NEUTRAL_MU_FP) / 1.0e9;
	double s = 0.5 + diff / 800.0;

	if (s < 0.05)
		s = 0.05;
	if (s > 1.0)
		s = 1.0;
	return s;
}

static void
emit_edge(ReturnSetInfo *rsinfo, const EdgeOut *e)
{
	Datum	values[7];
	bool	nulls[7] = {false, false, false, false, false, false, false};

	values[0] = hash128_to_datum(&e->source);
	values[1] = hash128_to_datum(&e->type_id);
	values[2] = hash128_to_datum(&e->object);
	values[3] = Int16GetDatum(e->hop);
	values[4] = Int64GetDatum(e->rating);
	values[5] = Int64GetDatum(e->rd);
	values[6] = Int64GetDatum(e->witnesses);
	tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
}

Datum
pg_laplace_explore_web(PG_FUNCTION_ARGS)
{
	ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
	bytea	   *seed_b;
	hash128_t	seed;
	int32		hops;
	int32		fanout;
	int32		max_nodes;
	HTAB	   *seen;
	HASHCTL		ctl;
	hash128_t  *frontier;
	hash128_t  *next_frontier;
	int			n_front = 0;
	int			n_seen = 0;
	SPIPlanPtr	plan;
	EdgeCand   *cands;
	int			cand_cap;
	bool		spi_top = false;

	if (PG_ARGISNULL(0))
		ereport(ERROR, (errmsg("explore_web: seed must not be NULL")));
	seed_b = PG_GETARG_BYTEA_PP(0);
	seed = datum_to_hash128(PointerGetDatum(seed_b));

	hops = PG_ARGISNULL(1) ? 2 : PG_GETARG_INT32(1);
	fanout = PG_ARGISNULL(2) ? 10 : PG_GETARG_INT32(2);
	max_nodes = PG_ARGISNULL(3) ? 160 : PG_GETARG_INT32(3);
	if (hops < 1)
		hops = 1;
	if (hops > 4)
		hops = 4;
	if (fanout < 2)
		fanout = 2;
	if (fanout > 16)
		fanout = 16;
	if (max_nodes < 8)
		max_nodes = 8;
	if (max_nodes > 400)
		max_nodes = 400;

	InitMaterializedSRF(fcinfo, 0);

	if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
		elog(ERROR, "explore_web: SPI_connect failed");

	{
		Oid			pargs[2] = {BYTEAOID, INT4OID};

		plan = SPI_prepare(
			"SELECT nbr, type_id, rating, rd, witness_count, outbound "
			"FROM laplace.explore_web_neighbors($1, $2)",
			2, pargs);
		if (plan == NULL)
			elog(ERROR, "explore_web: SPI_prepare failed");
	}

	memset(&ctl, 0, sizeof(ctl));
	ctl.keysize = 16;
	ctl.entrysize = sizeof(SeenNode);
	seen = hash_create("explore_web seen", max_nodes, &ctl, HASH_ELEM | HASH_BLOBS);

	frontier = (hash128_t *) palloc(sizeof(hash128_t) * max_nodes);
	next_frontier = (hash128_t *) palloc(sizeof(hash128_t) * max_nodes);
	cand_cap = max_nodes * fanout;
	if (cand_cap > 4096)
		cand_cap = 4096;
	cands = (EdgeCand *) palloc(sizeof(EdgeCand) * cand_cap);

	{
		SeenNode   *e;
		bool		found;

		e = (SeenNode *) hash_search(seen, &seed, HASH_ENTER, &found);
		e->hop = 0;
		n_seen = 1;
		frontier[n_front++] = seed;
	}

	for (int hop = 1; hop <= hops && n_front > 0 && n_seen < max_nodes; hop++)
	{
		int			n_cands = 0;
		int			n_next = 0;
		int			room = max_nodes - n_seen;
		int			admit_n;
		int			probe_limit = fanout * 2;

		if (probe_limit > 32)
			probe_limit = 32;

		for (int fi = 0; fi < n_front; fi++)
		{
			hash128_t	cur = frontier[fi];
			Datum		args[2];
			int			rc;

			args[0] = hash128_to_datum(&cur);
			args[1] = Int32GetDatum(probe_limit);
			rc = SPI_execute_plan(plan, args, NULL, true, 0);
			if (rc != SPI_OK_SELECT)
				elog(ERROR, "explore_web: neighbor probe failed: %s",
					 SPI_result_code_string(rc));

			for (uint64 r = 0; r < SPI_processed; r++)
			{
				HeapTuple	tup = SPI_tuptable->vals[r];
				TupleDesc	td = SPI_tuptable->tupdesc;
				bool		isnull;
				hash128_t	nbr;
				hash128_t	type_id;
				int64		rating;
				int64		rd;
				int64		wit;
				bool		outbound;
				SeenNode   *oe;
				bool		ofound;
				EdgeOut		edge;

				nbr = datum_to_hash128(SPI_getbinval(tup, td, 1, &isnull));
				if (isnull)
					continue;
				type_id = datum_to_hash128(SPI_getbinval(tup, td, 2, &isnull));
				if (isnull)
					continue;
				rating = DatumGetInt64(SPI_getbinval(tup, td, 3, &isnull));
				rd = DatumGetInt64(SPI_getbinval(tup, td, 4, &isnull));
				wit = DatumGetInt64(SPI_getbinval(tup, td, 5, &isnull));
				outbound = DatumGetBool(SPI_getbinval(tup, td, 6, &isnull));

				edge.source = outbound ? cur : nbr;
				edge.type_id = type_id;
				edge.object = outbound ? nbr : cur;
				edge.hop = (int16) hop;
				edge.rating = rating;
				edge.rd = rd;
				edge.witnesses = wit;

				oe = (SeenNode *) hash_search(seen, &nbr, HASH_FIND, &ofound);
				if (ofound)
				{
					/* Weave: both endpoints already retained. */
					emit_edge(rsinfo, &edge);
					continue;
				}

				if (n_cands < cand_cap)
				{
					cands[n_cands].nbr = nbr;
					cands[n_cands].type_id = type_id;
					cands[n_cands].from = cur;
					cands[n_cands].rating = rating;
					cands[n_cands].rd = rd;
					cands[n_cands].witnesses = wit;
					cands[n_cands].outbound = outbound;
					cands[n_cands].strength = edge_strength(rating, rd);
					n_cands++;
				}
			}
			SPI_freetuptable(SPI_tuptable);
		}

		if (n_cands == 0)
			break;

		qsort(cands, n_cands, sizeof(EdgeCand), cand_cmp_desc);

		/* Dedup candidates by nbr (keep strongest edge). */
		{
			HTAB	   *picked;
			HASHCTL		pctl;
			EdgeCand   *uniq;
			int			n_uniq = 0;

			memset(&pctl, 0, sizeof(pctl));
			pctl.keysize = 16;
			pctl.entrysize = sizeof(SeenNode);
			picked = hash_create("explore_web cand", n_cands, &pctl,
								 HASH_ELEM | HASH_BLOBS);
			uniq = (EdgeCand *) palloc(sizeof(EdgeCand) * n_cands);

			for (int i = 0; i < n_cands; i++)
			{
				bool		found;

				hash_search(picked, &cands[i].nbr, HASH_ENTER, &found);
				if (found)
					continue;
				uniq[n_uniq++] = cands[i];
			}
			hash_destroy(picked);

			admit_n = n_uniq;
			if (admit_n > fanout)
				admit_n = fanout;
			if (admit_n > room)
				admit_n = room;

			for (int i = 0; i < admit_n; i++)
			{
				SeenNode   *ne;
				bool		found;
				EdgeOut		edge;

				ne = (SeenNode *) hash_search(seen, &uniq[i].nbr, HASH_ENTER, &found);
				if (found)
					continue;
				ne->hop = hop;
				n_seen++;
				if (n_next < max_nodes)
					next_frontier[n_next++] = uniq[i].nbr;

				edge.source = uniq[i].outbound ? uniq[i].from : uniq[i].nbr;
				edge.type_id = uniq[i].type_id;
				edge.object = uniq[i].outbound ? uniq[i].nbr : uniq[i].from;
				edge.hop = (int16) hop;
				edge.rating = uniq[i].rating;
				edge.rd = uniq[i].rd;
				edge.witnesses = uniq[i].witnesses;
				emit_edge(rsinfo, &edge);
			}
			pfree(uniq);
		}

		/* Swap frontiers. */
		{
			hash128_t  *tmp = frontier;

			frontier = next_frontier;
			next_frontier = tmp;
			n_front = n_next;
		}
	}

	SPI_freeplan(plan);
	laplace_spi_finish(spi_top);
	return (Datum) 0;
}
