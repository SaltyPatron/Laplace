/*
 * trajectory_generate.c — walk_continuations: the S6→S7→S8 emission loop
 * (docs/specs/36 §3), corpus-free.
 *
 * S6 PROPOSE  generation.trajectory_continuations($tail, NULL): the complete k-context successor
 *             read straight off physicalities.trajectory (GIN containment +
 *             ordinal window), with trigram→…→unigram backoff over max_stride.
 *             The trajectory IS the ordered sequence (§9); the per-backend
 *             GenCorpus rebuild of it — 97,111,658 rows streamed into a RAM
 *             suffix array on FIRST CALL of every connection, measured 46
 *             minutes before generate('The king', 40) could emit a token — is
 *             deleted, not optimized. §7 requires cost bounded by the path,
 *             not the corpus, and a whole-corpus build cannot satisfy that at
 *             any size.
 *
 * S7 STEER    generation.steer_candidates($cands, $frontier): re-rank by rated consensus
 *             mass reaching the LIVE frontier — the prompt's routed token/sense
 *             web PLUS every selected identity, so the frontier is where the walk has
 *             ARRIVED and not where it started (docs/specs/36 §3; GH #921 acceptance
 *             "each emitted unit updates the active frontier before the next election").
 *             steer_candidates.c's own header rejects "a weight fixed BEFORE the walk
 *             begins" as a prior rather than steering; holding the frontier at the
 *             prompt made that true of its only caller
 *             ids, re-scored per emitted token. Scored by walk_score.h, the
 *             same kernel walk_branches retrieves with, so proposing and
 *             steering cannot disagree about what an edge is worth.
 *
 *             The combination is signed and multiplicative, matching the
 *             walk's existing semantics (rank × edge_weight precedent, and
 *             walk_branches' "non-positive score must dead-end, not walk"):
 *               edges > 0, steer > 0  → sequence weight × steer
 *               edges = 0             → sequence weight × 1 only when S7 has
 *                                       no positively witnessed proposal;
 *                                       UNATTESTED is fallback, not refutation
 *               edges > 0, steer ≤ 0 → excluded (adjudicated against the
 *                                       frontier: refuted edges dead-end)
 *
 *             This ordering matters once the trajectory estate is large. If a
 *             witnessed-positive candidate exists, allowing an unattested but
 *             very frequent unigram continuation to compete at ×1 makes S6
 *             frequency erase S7 meaning. If no positive S7 signal exists, the
 *             unattested sequence pool remains available exactly as before.
 *
 * S8 SAMPLE   After steering the complete proposal set, a Gumbel draw over the
 *             top-k surviving candidates at the caller's
 *             spread. (Spec S8 names RD-as-temperature; RD already shapes the
 *             steer term through exp(−κ·rd) inside walk_edge_weight, so the
 *             caller's spread composes with it rather than replacing it.
 *             Making RD the SOLE temperature is a candidate follow-up, not
 *             smuggled in here.)
 *
 * All ids stay bytea end to end. The vocab intern table died with the corpus:
 * interning existed to map ids into the suffix array's int32 space, and there
 * is no suffix array.
 */
#include "postgres.h"

#include <math.h>

#include "access/htup_details.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "utils/tuplestore.h"
#include "common/pg_prng.h"

#include "laplace/core/hash128.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/sql_catalog.h"
#include "spi_common.h"
#include "relation_symmetry.h"
#include "steer_candidates.h"
#include "consensus_neighbors.h"
#include "trajectory_continuations.h"

PG_FUNCTION_INFO_V1(pg_laplace_walk_continuations);

typedef struct Cand
{
    Datum  obj;        /* bytea(16), caller-context copy */
    Datum  sep;        /* bytea(16) or (Datum) 0         */
    int64  weight;     /* S6 sequence count; 0 = no sequence testimony */
    int    stride;     /* measured suffix length; semantic-only = 0 */
    double steer;      /* S7 signed consensus mass       */
    int64  edges;      /* S7 edge count; 0 = unattested  */
    double eff;        /* combined sampling weight       */
} Cand;

typedef struct CandIndex
{
    char key[16];
    int  index;
} CandIndex;

static SPIPlanPtr semantic_plan = NULL;
/* PostgreSQL performs one typed content-presence query over the native
 * proposal set. Graph access and reduction are owned by consensus_neighbors. */

static Datum copy_id_datum(Datum d);

typedef struct NeighborhoodEntry
{
    hash128_t id;
    LaplaceNeighbor *edges;
    int count;
} NeighborhoodEntry;

/* Cache only a deterministic projection within this forward call's snapshot.
 * Each newly active identity contributes one native batch probe. Retained
 * prompt neighborhoods are not re-read for every emitted constituent. */
static ArrayType *
proposal_neighborhood(HTAB *cache, Datum *frontier, int n_frontier,
                      ArrayType *types, int limit, MemoryContext owner)
{
    Datum *missing = palloc(sizeof(Datum) * Max(n_frontier, 1));
    int n_missing = 0;
    int64 capacity = n_frontier;
    Datum *proposals;
    int count = 0;
    for (int i = 0; i < n_frontier; ++i)
    {
        const char *id = VARDATA_ANY(DatumGetByteaPP(frontier[i]));
        if (hash_search(cache, id, HASH_FIND, NULL) == NULL)
            missing[n_missing++] = frontier[i];
    }
    if (n_missing > 0)
    {
        MemoryContext temporary = AllocSetContextCreate(CurrentMemoryContext,
            "forward neighborhood batch", ALLOCSET_DEFAULT_SIZES);
        MemoryContext previous = MemoryContextSwitchTo(temporary);
        ArrayType *ids = construct_array(missing, n_missing, BYTEAOID, -1, false, TYPALIGN_INT);
        int n_edges;
        LaplaceNeighbor *edges = laplace_consensus_neighbors(
            ids, types, limit, false, true, &n_edges, NULL);
        MemoryContextSwitchTo(owner);
        for (int i = 0; i < n_missing; ++i)
        {
            const char *id = VARDATA_ANY(DatumGetByteaPP(missing[i]));
            bool found;
            NeighborhoodEntry *entry = hash_search(cache, id, HASH_ENTER, &found);
            int low = 0, high = n_edges, end;
            if (found) continue;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (memcmp(&edges[mid].frontier, id, 16) < 0) low = mid + 1;
                else high = mid;
            }
            end = low;
            while (end < n_edges && memcmp(&edges[end].frontier, id, 16) == 0) ++end;
            entry->count = end - low;
            entry->edges = entry->count ? palloc(sizeof(LaplaceNeighbor) * entry->count) : NULL;
            if (entry->count) memcpy(entry->edges, edges + low, sizeof(LaplaceNeighbor) * entry->count);
        }
        MemoryContextSwitchTo(previous);
        MemoryContextDelete(temporary);
    }
    pfree(missing);
    for (int i = 0; i < n_frontier; ++i)
    {
        NeighborhoodEntry *entry = hash_search(cache,
            VARDATA_ANY(DatumGetByteaPP(frontier[i])), HASH_FIND, NULL);
        capacity += entry->count;
    }
    if (capacity > INT_MAX || (uint64) capacity > MaxAllocSize / sizeof(Datum))
        ereport(ERROR, (errmsg("forward neighborhood exceeds allocation capacity")));
    proposals = palloc(sizeof(Datum) * Max(capacity, 1));
    for (int i = 0; i < n_frontier; ++i)
    {
        NeighborhoodEntry *entry = hash_search(cache,
            VARDATA_ANY(DatumGetByteaPP(frontier[i])), HASH_FIND, NULL);
        proposals[count++] = copy_id_datum(frontier[i]);
        for (int j = 0; j < entry->count; ++j)
        {
            const LaplaceNeighbor *edge = entry->edges + j;
            if (!edge->outbound)
            {
                const laplace_relation_def_t *def = NULL;
                if (laplace_relation_lookup(&edge->type, &def) != 0 || def == NULL ||
                    def->symmetry != LAPLACE_REL_SYMMETRY_SYMMETRIC) continue;
            }
            proposals[count++] = hash128_to_datum(&edge->neighbor);
        }
    }
    ArrayType *result = count ? construct_array(proposals, count, BYTEAOID, -1, false, TYPALIGN_INT)
                              : construct_empty_array(BYTEAOID);
    for (int i = 0; i < count; ++i) pfree(DatumGetPointer(proposals[i]));
    pfree(proposals);
    return result;
}

static void
validate_relation_types(ArrayType *types)
{
    Datum *elems;
    bool  *nulls;
    int    count;

    if (ARR_NDIM(types) != 1 || ARR_ELEMTYPE(types) != BYTEAOID)
        ereport(ERROR,
                (errmsg("walk_continuations: relation types must be a 1-D bytea array")));
    deconstruct_array(types, BYTEAOID, -1, false, TYPALIGN_INT,
                      &elems, &nulls, &count);
    for (int i = 0; i < count; ++i)
    {
        if (nulls[i])
            ereport(ERROR,
                    (errmsg("walk_continuations: relation types must not contain NULL")));
        if (VARSIZE_ANY_EXHDR(DatumGetByteaPP(elems[i])) != 16)
            ereport(ERROR,
                    (errmsg("walk_continuations: relation type ids must be 16 bytes")));
    }
    if (count > 0)
    {
        pfree(elems);
        pfree(nulls);
    }
}

/*
 * Prepared once per backend and kept: the un-prepared path re-plans on every
 * emitted token of every walk. Exact sequence proposal passes NULL deliberately:
 * truncating trajectory successors before S7 steering changes the answer. The
 * semantic graph source is separately bounded by the declared beam above.
 */
static void
ensure_plans(void)
{
    if (semantic_plan == NULL)
    {
        Oid argtypes[3] = { BYTEAARRAYOID, BYTEAARRAYOID, BYTEAARRAYOID };
        SPIPlanPtr plan = SPI_prepare_cursor(laplace_sql_query_text("generation.semantic_presence"), 3, argtypes,
                                             CURSOR_OPT_GENERIC_PLAN | CURSOR_OPT_PARALLEL_OK);
        if (plan == NULL || SPI_keepplan(plan) != 0)
            elog(ERROR, "walk_continuations: semantic proposal plan failed");
        semantic_plan = plan;
    }
}

static uint64
splitmix64(uint64 *state)
{
    uint64 z = (*state += UINT64CONST(0x9E3779B97F4A7C15));
    z = (z ^ (z >> 30)) * UINT64CONST(0xBF58476D1CE4E5B9);
    z = (z ^ (z >> 27)) * UINT64CONST(0x94D049BB133111EB);
    return z ^ (z >> 31);
}

static double
rng_uniform(uint64 *state)
{
    return ((double) (splitmix64(state) >> 11) + 0.5) * (1.0 / 9007199254740992.0);
}

/* Copy a 16-byte bytea datum out of SPI_tuptable into the caller's context. */
static Datum
copy_id_datum(Datum d)
{
    bytea *src = DatumGetByteaPP(d);
    bytea *dst = (bytea *) palloc(VARHDRSZ + 16);

    SET_VARSIZE(dst, VARHDRSZ + 16);
    memcpy(VARDATA(dst), VARDATA_ANY(src), 16);
    return PointerGetDatum(dst);
}

static void
ensure_candidate_capacity(Cand **cand, int *capacity, uint64 needed,
                          MemoryContext owner)
{
    MemoryContext old;

    if (needed <= (uint64) *capacity)
        return;
    if (needed > (uint64) INT_MAX ||
        needed > (uint64) (MaxAllocSize / sizeof(Cand)))
        ereport(ERROR,
                (errmsg("walk_continuations: candidate set exceeds PostgreSQL allocation capacity"),
                 errdetail("Requested %llu candidates.",
                           (unsigned long long) needed)));

    old = MemoryContextSwitchTo(owner);
    *cand = *cand == NULL
        ? (Cand *) palloc(sizeof(Cand) * (Size) needed)
        : (Cand *) repalloc(*cand, sizeof(Cand) * (Size) needed);
    MemoryContextSwitchTo(old);
    *capacity = (int) needed;
}

static void
free_candidate_ids(Cand *cand, int count)
{
    for (int i = 0; i < count; i++)
    {
        pfree(DatumGetPointer(cand[i].obj));
        if (cand[i].sep != (Datum) 0)
            pfree(DatumGetPointer(cand[i].sep));
    }
}

static int
candidate_cmp(const void *a, const void *b)
{
    const Cand *x = (const Cand *) a;
    const Cand *y = (const Cand *) b;
    bytea      *xo;
    bytea      *yo;

    if (x->eff > y->eff) return -1;
    if (x->eff < y->eff) return 1;
    if (x->weight > y->weight) return -1;
    if (x->weight < y->weight) return 1;
    xo = DatumGetByteaPP(x->obj);
    yo = DatumGetByteaPP(y->obj);
    return memcmp(VARDATA_ANY(xo), VARDATA_ANY(yo), 16);
}

Datum
pg_laplace_walk_continuations(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    ArrayType *ctx_arr, *front_arr;
    int32      steps, max_order, topk, fanout;
    float8     temp;
    uint64     rng;
    Datum     *elems, *front_elems;
    bool      *nulls, *front_nulls;
    int        n_in, n_front_in;
    Datum     *ctx;
    int        ctx_len = 0, ctx_cap;
    Datum     *frontier;
    int        n_frontier = 0;
    Cand      *cand = NULL;
    int        cand_capacity = 0;
    ArrayType *relation_types = PG_NARGS() > 7 && !PG_ARGISNULL(7) ?
        PG_GETARG_ARRAYTYPE_P(7) : NULL;
    MemoryContext walk_cxt, step_cxt, old;
    HTAB *neighborhoods;
    HTAB *frontier_ids;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("walk_continuations: context must not be NULL")));
    ctx_arr   = PG_GETARG_ARRAYTYPE_P(0);
    steps     = PG_ARGISNULL(1) ? 24  : PG_GETARG_INT32(1);
    max_order = PG_ARGISNULL(2) ? 5   : PG_GETARG_INT32(2);
    temp      = PG_ARGISNULL(3) ? 0.7 : PG_GETARG_FLOAT8(3);
    topk      = PG_ARGISNULL(4) ? 10  : PG_GETARG_INT32(4);
    fanout    = PG_NARGS() > 8 && !PG_ARGISNULL(8) ? PG_GETARG_INT32(8) : 8;
    rng       = PG_ARGISNULL(5) ? UINT64CONST(0x5851F42D4C957F2D)
                                : (uint64) PG_GETARG_INT64(5);

    if (steps < 0)
        ereport(ERROR, (errmsg("walk_continuations: steps must not be negative")));
    if (max_order < 0)
        ereport(ERROR, (errmsg("walk_continuations: max_order must not be negative")));
    if (topk < 0)
        ereport(ERROR, (errmsg("walk_continuations: topk must not be negative")));
    if (fanout < 0)
        ereport(ERROR, (errmsg("walk_continuations: fanout must not be negative")));
    if (!isfinite(temp) || temp < 0.0)
        ereport(ERROR, (errmsg("walk_continuations: spread must be finite and not negative")));
    if (ARR_NDIM(ctx_arr) != 1 || ARR_ELEMTYPE(ctx_arr) != BYTEAOID)
        ereport(ERROR, (errmsg("walk_continuations: context must be a 1-D bytea array")));
    if (relation_types != NULL)
        validate_relation_types(relation_types);

    InitMaterializedSRF(fcinfo, 0);

    walk_cxt = CurrentMemoryContext;
    {
        HASHCTL cache_ctl = {0};
        cache_ctl.keysize = sizeof(hash128_t);
        cache_ctl.entrysize = sizeof(NeighborhoodEntry);
        cache_ctl.hcxt = walk_cxt;
        neighborhoods = hash_create("forward neighborhood cache", 128, &cache_ctl,
                                    HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
        cache_ctl.entrysize = sizeof(hash128_t);
        frontier_ids = hash_create("forward active identities", 128, &cache_ctl,
                                  HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    }

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "walk_continuations: SPI_connect failed");
    ensure_plans();

    deconstruct_array(ctx_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &elems, &nulls, &n_in);

    /* The ordered proposal context and the semantic frontier are different
     * operands. Appending routed neighbours to ctx would make them a fake
     * trajectory suffix. A caller that omits p_frontier retains the historical
     * prompt-only behaviour. */
    if (PG_NARGS() > 6 && !PG_ARGISNULL(6))
        front_arr = PG_GETARG_ARRAYTYPE_P(6);
    else
        front_arr = ctx_arr;
    if (ARR_NDIM(front_arr) != 1 || ARR_ELEMTYPE(front_arr) != BYTEAOID)
        ereport(ERROR, (errmsg("walk_continuations: frontier must be a 1-D bytea array")));
    deconstruct_array(front_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &front_elems, &front_nulls, &n_front_in);

    if ((uint64) n_in + (uint64) steps > (uint64) INT_MAX ||
        (uint64) n_in + (uint64) steps >
            (uint64) (MaxAllocSize / sizeof(Datum)))
        ereport(ERROR,
                (errmsg("walk_continuations: requested walk exceeds PostgreSQL allocation capacity")));
    /* An empty/all-NULL routed frontier falls back to the entire prompt. */
    int frontier_base_capacity = n_front_in > n_in ? n_front_in : n_in;
    if ((uint64) frontier_base_capacity + (uint64) steps > INT_MAX ||
        (uint64) frontier_base_capacity + (uint64) steps >
            (uint64) (MaxAllocSize / sizeof(Datum)))
        ereport(ERROR,
                (errmsg("walk_continuations: requested frontier exceeds PostgreSQL allocation capacity")));
    ctx_cap = n_in + steps;
    old = MemoryContextSwitchTo(walk_cxt);
    ctx      = (Datum *) palloc(sizeof(Datum) * (ctx_cap > 0 ? ctx_cap : 1));
    /* Ordinal matching depth does not bound semantic memory. Retain every
     * selected identity for this pass; repeated occurrences remain in ctx,
     * while the evidence frontier contains each identity exactly once. */
    frontier = (Datum *) palloc(sizeof(Datum) *
                                (frontier_base_capacity + steps > 0 ?
                                 frontier_base_capacity + steps : 1));
    for (int i = 0; i < n_in; i++)
    {
        bytea *b;

        if (nulls[i])
            continue;
        b = DatumGetByteaPP(elems[i]);
        if (VARSIZE_ANY_EXHDR(b) != 16)
            ereport(ERROR, (errmsg("walk_continuations: context ids must be 16 bytes")));
        ctx[ctx_len++]           = copy_id_datum(elems[i]);
    }
    for (int i = 0; i < n_front_in; i++)
    {
        bytea *b;

        if (front_nulls[i])
            continue;
        b = DatumGetByteaPP(front_elems[i]);
        if (VARSIZE_ANY_EXHDR(b) != 16)
            ereport(ERROR, (errmsg("walk_continuations: frontier ids must be 16 bytes")));
        bool found;
        hash_search(frontier_ids, VARDATA_ANY(b), HASH_ENTER, &found);
        if (!found) frontier[n_frontier++] = copy_id_datum(front_elems[i]);
    }
    /* An explicitly empty route is an abstention, not permission to erase the
     * request. Preserve the resolved prompt as the minimum live frontier. */
    if (n_frontier == 0)
    {
        for (int i = 0; i < ctx_len; i++)
        {
            bool found;
            hash_search(frontier_ids, VARDATA_ANY(DatumGetByteaPP(ctx[i])), HASH_ENTER, &found);
            if (!found) frontier[n_frontier++] = ctx[i];
        }
    }
    MemoryContextSwitchTo(old);

    if (ctx_len == 0 || steps == 0 || topk == 0)
    {
        SPI_finish();
        return (Datum) 0;
    }

    step_cxt = AllocSetContextCreate(walk_cxt, "forward step operands",
                                     ALLOCSET_DEFAULT_SIZES);
    MemoryContextSwitchTo(step_cxt);
    for (int64 step = 1; step <= steps; step++)
    {
        int  n_cand = 0;
        int  pick = -1;

        /* Evidence state and the ordered trajectory live in walk_cxt. Query
         * operands, pair reductions and candidate arrays for the previous
         * election do not become additional retained conversation memory. */
        MemoryContextReset(step_cxt);
        CHECK_FOR_INTERRUPTS();

        /* Resolve every suffix in one indexed native trajectory operation.
         * Selection receives the complete successor set at the greatest exact
         * stride; no repeated SQL calls or pre-steering top-K loss. */
        int depth = Min(ctx_len, max_order);
        if (depth > 0)
        {
            ArrayType *tail = construct_array(ctx + ctx_len - depth, depth,
                                              BYTEAOID, -1, false, TYPALIGN_INT);
            int count;
            LaplaceContinuation *successors = laplace_trajectory_continuations(tail, true, &count);
            ensure_candidate_capacity(&cand, &cand_capacity, count, walk_cxt);
            old = MemoryContextSwitchTo(walk_cxt);
            for (int i = 0; i < count; ++i)
            {
                cand[n_cand].obj = hash128_to_datum(&successors[i].id);
                cand[n_cand].sep = (Datum) 0;
                cand[n_cand].weight = successors[i].occurrences;
                cand[n_cand].stride = successors[i].stride;
                cand[n_cand].steer = 0.0;
                cand[n_cand].edges = 0;
                ++n_cand;
            }
            MemoryContextSwitchTo(old);
            pfree(successors);
            pfree(tail);
        }
        /* ---- S6 semantic proposals from the same live frontier ---- */
        {
            Datum type_ids[5];
            for (uint8 tier = 0; tier < 5; ++tier)
            {
                hash128_t type = laplace_content_tier_type_id(tier);
                bytea *id = (bytea *) palloc(VARHDRSZ + 16);
                SET_VARSIZE(id, VARHDRSZ + 16);
                memcpy(VARDATA(id), &type, 16);
                type_ids[tier] = PointerGetDatum(id);
            }
            ArrayType *types = construct_array(type_ids, 5, BYTEAOID, -1, false, TYPALIGN_INT);
            ArrayType *front = proposal_neighborhood(neighborhoods, frontier, n_frontier,
                                                       relation_types, fanout, walk_cxt);
            /* The semantic graph walk is a simple path through content. Prompt
             * seeds and already-emitted nodes do not re-enter solely through the
             * graph. Witnessed sequence proposals can still repeat them. Routed
             * frontier members are eligible; they are not all prompt seeds. */
            ArrayType *visited = construct_array(ctx, ctx_len, BYTEAOID, -1, false, TYPALIGN_INT);
            Datum args[3] = { PointerGetDatum(front), PointerGetDatum(types), PointerGetDatum(visited) };
            HASHCTL ctl = {0};
            ctl.keysize = 16;
            ctl.entrysize = sizeof(CandIndex);
            ctl.hcxt = walk_cxt;
            HTAB *seen = hash_create("semantic proposal union", n_cand + 32, &ctl,
                                    HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
            for (int i = 0; i < n_cand; ++i)
                hash_search(seen, VARDATA_ANY(DatumGetByteaPP(cand[i].obj)), HASH_ENTER, NULL);
            Portal portal = SPI_cursor_open(NULL,
                                            semantic_plan,
                                            args, NULL, true);
            if (portal == NULL)
                elog(ERROR, "walk_continuations: semantic proposal cursor open failed: %s",
                     SPI_result_code_string(SPI_result));
            for (;;)
            {
                SPI_cursor_fetch(portal, true, 50000);
                if (SPI_processed == 0)
                    break;
                ensure_candidate_capacity(&cand, &cand_capacity,
                                          (uint64)n_cand + SPI_processed, walk_cxt);
                for (uint64 r = 0; r < SPI_processed; ++r)
                {
                    bool isnull, found;
                    Datum id = SPI_getbinval(SPI_tuptable->vals[r],
                                             SPI_tuptable->tupdesc, 1, &isnull);
                    if (isnull) continue;
                    bytea *bytes = DatumGetByteaPP(id);
                    if (VARSIZE_ANY_EXHDR(bytes) != 16)
                        elog(ERROR, "walk_continuations: semantic candidate id must be 16 bytes");
                    hash_search(seen, VARDATA_ANY(bytes), HASH_ENTER, &found);
                    if (found) continue;
                    old = MemoryContextSwitchTo(walk_cxt);
                    cand[n_cand].obj = copy_id_datum(id);
                    MemoryContextSwitchTo(old);
                    cand[n_cand].sep = (Datum)0;
                    cand[n_cand].weight = 0;
                    cand[n_cand].stride = 0;
                    cand[n_cand].steer = 0.0;
                    cand[n_cand].edges = 0;
                    ++n_cand;
                }
                SPI_freetuptable(SPI_tuptable);
                SPI_tuptable = NULL;
                CHECK_FOR_INTERRUPTS();
            }
            if (SPI_tuptable != NULL)
            {
                SPI_freetuptable(SPI_tuptable);
                SPI_tuptable = NULL;
            }
            SPI_cursor_close(portal);
            hash_destroy(seen);
            pfree(types); pfree(front); pfree(visited);
            for (int i = 0; i < 5; ++i) pfree(DatumGetPointer(type_ids[i]));
        }
        if (n_cand == 0)
            break;

        /* ---- S7 STEER: direct native batch, shared with the SQL wrapper ---- */
        {
            Datum *objs = palloc(sizeof(Datum) * n_cand);
            ArrayType *cand_a, *front_a;
            LaplaceSteeredCandidate *scores;
            int n_scores;
            for (int i = 0; i < n_cand; ++i) objs[i] = cand[i].obj;
            cand_a = construct_array(objs, n_cand, BYTEAOID, -1, false, TYPALIGN_INT);
            front_a = construct_array(frontier, n_frontier, BYTEAOID, -1, false, TYPALIGN_INT);
            scores = laplace_steer_candidates(cand_a, front_a, relation_types, &n_scores, NULL);
            /* The result is ordered by exact identity. Every separator variant
             * of one candidate receives that identity's same semantic score. */
            for (int i = 0; i < n_cand; ++i)
            {
                const char *id = VARDATA_ANY(DatumGetByteaPP(cand[i].obj));
                int low = 0, high = n_scores;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    int cmp = memcmp(&scores[middle].id, id, 16);
                    if (cmp < 0) low = middle + 1;
                    else high = middle;
                }
                if (low < n_scores && memcmp(&scores[low].id, id, 16) == 0)
                {
                    cand[i].steer = scores[low].steer;
                    cand[i].edges = scores[low].edges;
                }
            }
            pfree(scores);
            pfree(objs);
            pfree(cand_a);
            pfree(front_a);
        }

        /*
         * Combine, signed, in two semantic pools. Refuted-toward-frontier
         * candidates are always excluded. If S7 positively witnesses at least
         * one proposal, unattested sequence-only proposals wait behind that
         * witnessed pool instead of competing with raw ×1 frequency. If S7 has
         * no positive signal at all, unattested proposals remain the fallback.
         *
         * This keeps "no opinion" distinct from refutation without letting a
         * high-frequency unigram erase meaning when meaning is actually present.
         */
        {
            int  m = 0;
            bool has_positive_steer = false;

            for (int i = 0; i < n_cand; i++)
            {
                if (cand[i].edges > 0
                    && cand[i].steer > 0.0 && isfinite(cand[i].steer))
                {
                    has_positive_steer = true;
                    break;
                }
            }

            for (int i = 0; i < n_cand; i++)
            {
                if ((cand[i].edges > 0
                     && (cand[i].steer <= 0.0 || !isfinite(cand[i].steer)))
                    || (has_positive_steer && cand[i].edges == 0)
                    || (cand[i].weight == 0 && cand[i].edges == 0))
                {
                    pfree(DatumGetPointer(cand[i].obj));
                    if (cand[i].sep != (Datum) 0)
                        pfree(DatumGetPointer(cand[i].sep));
                    continue;
                }
                cand[m] = cand[i];
                /* Neutral sequence prior for a semantic-only proposal; its
                 * recorded sequence count remains zero. */
                cand[m].eff = (cand[m].weight > 0 ? (double)cand[m].weight : 1.0) *
                              (cand[m].edges > 0 ? cand[m].steer : 1.0);
                if (cand[m].eff <= 0.0 || !isfinite(cand[m].eff))
                {
                    pfree(DatumGetPointer(cand[m].obj));
                    if (cand[m].sep != (Datum) 0)
                        pfree(DatumGetPointer(cand[m].sep));
                    continue;
                }
                m++;
            }
            n_cand = m;
        }
        if (n_cand == 0)
            break;

        qsort(cand, (size_t) n_cand, sizeof(Cand), candidate_cmp);

        /* ---- S8 SAMPLE: Gumbel over the top-k at the caller's spread ---- */
        {
            int    limit = (n_cand < topk) ? n_cand : topk;
            double best_key = 0;

            if (temp == 0.0)
                pick = 0;
            for (int i = 0; temp > 0.0 && i < limit; i++)
            {
                double u   = rng_uniform(&rng);
                double key = log(cand[i].eff) / temp - log(-log(u));

                if (i == 0 || key > best_key)
                    { best_key = key; pick = i; }
            }
        }

        {
            Datum  values[4];
            bool   rnulls[4] = { false, false, false, false };

            values[0] = Int32GetDatum((int32) step);
            values[1] = cand[pick].obj;
            values[2] = Int32GetDatum(cand[pick].stride);
            if (cand[pick].sep != (Datum) 0)
                values[3] = cand[pick].sep;
            else
                rnulls[3] = true;
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, rnulls);
        }

        old = MemoryContextSwitchTo(walk_cxt);
        ctx[ctx_len++] = copy_id_datum(cand[pick].obj);
        MemoryContextSwitchTo(old);

        /* A selected constituent changes the next graph proposal and steering
         * state even when ordinal backoff is disabled. Sequence depth must not
         * silently expire an earlier contribution to the active evidence. */
        bool already_active;
        hash_search(frontier_ids, VARDATA_ANY(DatumGetByteaPP(ctx[ctx_len - 1])),
                    HASH_ENTER, &already_active);
        if (!already_active) frontier[n_frontier++] = ctx[ctx_len - 1];
        free_candidate_ids(cand, n_cand);
    }

    MemoryContextSwitchTo(walk_cxt);
    MemoryContextDelete(step_cxt);
    SPI_finish();
    return (Datum) 0;
}
