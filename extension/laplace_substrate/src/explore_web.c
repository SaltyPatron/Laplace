/*
 * explore_web.c — SPI beam crawl for the explore consensus-web viz.
 *
 * Unlike generation.foundry_crawl(vocab / tier-2 emit only), this:
 *   - walks undirected consensus (out ∪ in) via one batched frontier probe/hop
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
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"

#include "laplace/core/hash128.h"
#include "laplace/core/glicko2.h"
#include "laplace/core/highway_table.h"
#include "laplace/core/relation_law.h"
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
	const EdgeCand *ea = (const EdgeCand *) a;
	const EdgeCand *eb = (const EdgeCand *) b;
	double sa = ea->strength;
	double sb = eb->strength;
	int cmp;

	if (sa < sb)
		return 1;
	if (sa > sb)
		return -1;
	cmp = memcmp(&ea->nbr, &eb->nbr, sizeof(hash128_t));
	if (cmp != 0)
		return cmp;
	cmp = memcmp(&ea->type_id, &eb->type_id, sizeof(hash128_t));
	if (cmp != 0)
		return cmp;
	cmp = memcmp(&ea->from, &eb->from, sizeof(hash128_t));
	if (cmp != 0)
		return cmp;
	if (ea->outbound != eb->outbound)
		return ea->outbound ? -1 : 1;
	return 0;
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

/* Resolve the union of the frontier's deposited relation bits entirely in
 * memory after one indexed entity-array lookup.  A missing mask means the
 * projection cannot prove a complete relation set, so the caller uses the
 * unmasked correctness path. */
static ArrayType *
frontier_relation_types(SPIPlanPtr mask_plan, ArrayType *frontier_array,
						int expected_rows, bool *complete)
{
	Datum		args[1];
	laplace_mask256_t union_mask;
	Datum		type_datums[256];
	int			n_types = 0;
	int			rc;

	*complete = false;
	memset(&union_mask, 0, sizeof(union_mask));
	args[0] = PointerGetDatum(frontier_array);
	rc = SPI_execute_plan(mask_plan, args, NULL, true, 0);
	if (rc != SPI_OK_SELECT)
		elog(ERROR, "explore_web: frontier mask probe failed: %s",
			 SPI_result_code_string(rc));

	if (SPI_processed != (uint64) expected_rows)
	{
		SPI_freetuptable(SPI_tuptable);
		return NULL;
	}

	for (uint64 r = 0; r < SPI_processed; r++)
	{
		HeapTuple	tup = SPI_tuptable->vals[r];
		TupleDesc	td = SPI_tuptable->tupdesc;
		bool		isnull;
		Datum		d = SPI_getbinval(tup, td, 1, &isnull);
		bytea	   *mask;
		laplace_mask256_t one;

		if (isnull)
		{
			SPI_freetuptable(SPI_tuptable);
			return NULL;
		}
		mask = DatumGetByteaPP(d);
		if (VARSIZE_ANY_EXHDR(mask) != sizeof(one))
			elog(ERROR, "explore_web: entity highway mask is not 32 bytes");
		memcpy(&one, VARDATA_ANY(mask), sizeof(one));
		union_mask = highway_table_mask_or(union_mask, one);
	}
	SPI_freetuptable(SPI_tuptable);

	if (!highway_table_is_loaded())
		return NULL;
	for (int bit = 0; bit < 256; bit++)
	{
		const char *canonical = NULL;
		float		rank;
		uint8_t		band;
		hash128_t	type_id;

		if (!highway_table_mask_test(&union_mask, (uint8_t) bit))
			continue;
		if (highway_table_relation_by_bit((uint8_t) bit, &canonical,
									   &rank, &band) != 0)
			continue;
		if (laplace_relation_type_id(canonical, &type_id) != 0)
			continue;
		type_datums[n_types++] = hash128_to_datum(&type_id);
	}
	if (n_types == 0)
		return NULL;

	*complete = true;
	return construct_array(type_datums, n_types, BYTEAOID, -1, false, 'i');
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
	SPIPlanPtr	full_plan;
	SPIPlanPtr	masked_plan;
	SPIPlanPtr	mask_plan;
	EdgeCand   *cands;
	Datum	   *frontier_datums;
	int			cand_cap;
	bool		spi_top = false;

	if (PG_ARGISNULL(0))
		ereport(ERROR, (errmsg("explore_web: seed must not be NULL")));
	seed_b = PG_GETARG_BYTEA_PP(0);
	seed = datum_to_hash128(PointerGetDatum(seed_b));

	hops = PG_ARGISNULL(1) ? 2 : PG_GETARG_INT32(1);
	fanout = PG_ARGISNULL(2) ? 10 : PG_GETARG_INT32(2);
	if (hops < 0 || fanout < 0)
		ereport(ERROR, (errmsg("explore_web: hops and fanout must be >= 0")));
	if (hops > PG_INT16_MAX)
		ereport(ERROR, (errmsg("explore_web: hops exceeds the smallint result coordinate")));
	if (PG_ARGISNULL(3))
	{
		int64 derived = 1 + (int64) hops * (int64) fanout;
		if (derived > PG_INT32_MAX)
			ereport(ERROR, (errmsg("explore_web: derived node budget exceeds int32")));
		max_nodes = (int32) derived;
	}
	else
		max_nodes = PG_GETARG_INT32(3);
	if (max_nodes < 0)
		ereport(ERROR, (errmsg("explore_web: max_nodes must be >= 0")));

	InitMaterializedSRF(fcinfo, 0);
	if (hops == 0 || fanout == 0 || max_nodes == 0)
		return (Datum) 0;
	if ((Size) max_nodes > MaxAllocSize / sizeof(hash128_t)
		|| (Size) max_nodes > MaxAllocSize / sizeof(Datum)
		|| (Size) max_nodes > MaxAllocSize / sizeof(SeenNode))
		ereport(ERROR, (errmsg("explore_web: requested node budget exceeds PostgreSQL allocation capacity")));

	if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
		elog(ERROR, "explore_web: SPI_connect failed");

	{
		Oid			pargs[2] = {BYTEAARRAYOID, INT4OID};

		full_plan = SPI_prepare(
			"SELECT frontier_id, nbr, type_id, rating, rd, witness_count, outbound "
			"FROM consensus.explore_web_neighbors($1, $2)",
			2, pargs);
		if (full_plan == NULL)
			elog(ERROR, "explore_web: full neighbor SPI_prepare failed");
	}
	{
		Oid			pargs[3] = {BYTEAARRAYOID, BYTEAARRAYOID, INT4OID};

		masked_plan = SPI_prepare(
			"SELECT frontier_id, nbr, type_id, rating, rd, witness_count, outbound "
			"FROM consensus.explore_web_neighbors($1, $2, $3)",
			3, pargs);
		if (masked_plan == NULL)
			elog(ERROR, "explore_web: masked neighbor SPI_prepare failed");
	}
	{
		Oid			pargs[1] = {BYTEAARRAYOID};

		mask_plan = SPI_prepare(
			"SELECT e.highway_mask FROM laplace.entities e "
			"WHERE e.id = ANY($1) AND e.highway_mask IS NOT NULL",
			1, pargs);
		if (mask_plan == NULL)
			elog(ERROR, "explore_web: frontier mask SPI_prepare failed");
	}

	memset(&ctl, 0, sizeof(ctl));
	ctl.keysize = 16;
	ctl.entrysize = sizeof(SeenNode);
	seen = hash_create("explore_web seen", max_nodes, &ctl, HASH_ELEM | HASH_BLOBS);

	frontier = (hash128_t *) palloc(sizeof(hash128_t) * max_nodes);
	next_frontier = (hash128_t *) palloc(sizeof(hash128_t) * max_nodes);
	frontier_datums = (Datum *) palloc(sizeof(Datum) * max_nodes);
	cand_cap = 0;
	cands = NULL;

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
		int			admit_target = fanout < room ? fanout : room;
		int			probe_limit = n_seen + admit_target;
		int64		required_cands = (int64) n_front * (int64) probe_limit;

		/* SQL returns one ranked, neighbour-distinct head per frontier member. At
		 * most n_seen entries in any head can already be retained, so
		 * n_seen+admit_target proves enough room for the requested number of new
		 * nodes. The exact union is n_front*probe_limit; C ranks and deduplicates
		 * that union globally. This replaces the former fixed multiplier and
		 * ceiling probability guess. */
		if ((uint64) required_cands > (uint64) (MaxAllocSize / sizeof(EdgeCand))
			|| required_cands > PG_INT32_MAX)
			ereport(ERROR, (errmsg("explore_web: candidate budget exceeds PostgreSQL allocation capacity")));
		if ((int) required_cands > cand_cap)
		{
			cand_cap = (int) required_cands;
			cands = cands == NULL
				? (EdgeCand *) palloc(sizeof(EdgeCand) * cand_cap)
				: (EdgeCand *) repalloc(cands, sizeof(EdgeCand) * cand_cap);
		}

		Datum		args[3];
		ArrayType  *frontier_array;
		ArrayType  *type_array;
		bool		masked;
		int			rc;

		for (int fi = 0; fi < n_front; fi++)
			frontier_datums[fi] = hash128_to_datum(&frontier[fi]);
		frontier_array = construct_array(frontier_datums, n_front,
									 BYTEAOID, -1, false, 'i');
		type_array = frontier_relation_types(mask_plan, frontier_array,
									 n_front, &masked);
		args[0] = PointerGetDatum(frontier_array);
		if (masked)
		{
			args[1] = PointerGetDatum(type_array);
			args[2] = Int32GetDatum(probe_limit);
			rc = SPI_execute_plan(masked_plan, args, NULL, true, 0);
		}
		else
		{
			args[1] = Int32GetDatum(probe_limit);
			rc = SPI_execute_plan(full_plan, args, NULL, true, 0);
		}
		if (rc != SPI_OK_SELECT)
			elog(ERROR, "explore_web: frontier probe failed: %s",
				 SPI_result_code_string(rc));

		for (uint64 r = 0; r < SPI_processed; r++)
		{
			HeapTuple	tup = SPI_tuptable->vals[r];
			TupleDesc	td = SPI_tuptable->tupdesc;
			bool		isnull;
			hash128_t	cur;
			hash128_t	nbr;
			hash128_t	type_id;
			int64		rating;
			int64		rd;
			int64		wit;
			bool		outbound;
			SeenNode   *oe;
			bool		ofound;
			EdgeOut		edge;

			cur = datum_to_hash128(SPI_getbinval(tup, td, 1, &isnull));
			if (isnull)
				continue;
			nbr = datum_to_hash128(SPI_getbinval(tup, td, 2, &isnull));
			if (isnull)
				continue;
			type_id = datum_to_hash128(SPI_getbinval(tup, td, 3, &isnull));
			if (isnull)
				continue;
			rating = DatumGetInt64(SPI_getbinval(tup, td, 4, &isnull));
			rd = DatumGetInt64(SPI_getbinval(tup, td, 5, &isnull));
			wit = DatumGetInt64(SPI_getbinval(tup, td, 6, &isnull));
			outbound = DatumGetBool(SPI_getbinval(tup, td, 7, &isnull));

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
				cands[n_cands].strength = laplace_edge_strength(rating, rd);
				n_cands++;
			}
		}
		SPI_freetuptable(SPI_tuptable);

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

	SPI_freeplan(mask_plan);
	SPI_freeplan(masked_plan);
	SPI_freeplan(full_plan);
	laplace_spi_finish(spi_top);
	return (Datum) 0;
}
