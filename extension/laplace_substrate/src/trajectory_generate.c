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
#include "spi_common.h"
#include "relation_symmetry.h"
#include "steer_candidates.h"
#include "consensus_neighbors.h"
#include "trajectory_continuations.h"
#include "walk_score.h"
#include "prompt_input.h"
#include "explore_web.h"

PG_FUNCTION_INFO_V1(pg_laplace_walk_continuations);
PG_FUNCTION_INFO_V1(pg_laplace_forward_prompt);

typedef struct Cand
{
    Datum  obj;        /* bytea(16), caller-context copy */
    Datum  sep;        /* bytea(16) or (Datum) 0         */
    int64  weight;     /* S6 sequence count; 0 = no sequence testimony */
    int    stride;     /* measured suffix length; semantic-only = 0 */
    double steer;      /* S7 signed consensus mass       */
    int64  edges;      /* S7 edge count; 0 = unattested  */
    double projection; /* positive support for the declared output operation */
    double eff;        /* combined sampling weight       */
} Cand;

static Datum copy_id_datum(Datum d);

typedef struct NeighborhoodEntry
{
    hash128_t id;
    LaplaceNeighbor *edges;
    int count;
} NeighborhoodEntry;

typedef struct ProjectionSupport
{
    hash128_t id;
    double score;
} ProjectionSupport;

/* Cache only a deterministic projection within this forward call's snapshot.
 * Each newly active identity contributes one native batch probe. Retained
 * prompt neighborhoods are not re-read for every emitted constituent. */
static void
proposal_neighborhood(HTAB *cache, Datum *frontier, int n_frontier,
                      ArrayType *types, int limit, MemoryContext owner,
                      HTAB *projection)
{
    Datum *missing = palloc(sizeof(Datum) * Max(n_frontier, 1));
    int n_missing = 0;
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
            ids, types, limit, false, true, true, &n_edges, NULL);
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
        /* A routing identity is state, not an output proposal. Only the
         * endpoints of the requested output relations enter this projection. */
        for (int j = 0; j < entry->count; ++j)
        {
            const LaplaceNeighbor *edge = entry->edges + j;
            if (!edge->outbound)
            {
                const laplace_relation_def_t *def = NULL;
                if (laplace_relation_lookup(&edge->type, &def) != 0 || def == NULL ||
                    def->symmetry != LAPLACE_REL_SYMMETRY_SYMMETRIC) continue;
            }
            bool found;
            ProjectionSupport *support = hash_search(projection, &edge->neighbor,
                                                     HASH_ENTER, &found);
            if (!found) support->score = 0.0;
            support->score += walk_edge_score(edge->type, edge->rating, edge->rd);
        }
    }
}

static void
validate_relation_types(ArrayType *types)
{
    Datum *elems;
    bool  *nulls;
    int    count;

    if (ARR_NDIM(types) > 1 || ARR_ELEMTYPE(types) != BYTEAOID)
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

static Datum
walk_continuations(FunctionCallInfo fcinfo, const LaplacePromptInput *input)
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
    /* Routing/steering and output projection are independent typed operands.
     * Compatibility adapters bind their declared continuation set before this
     * native operation. NULL here establishes no graph output purpose. */
    ArrayType *output_relations = PG_NARGS() > 11 ?
        (PG_ARGISNULL(11) ? NULL : PG_GETARG_ARRAYTYPE_P(11)) : NULL;
    MemoryContext walk_cxt, step_cxt, old;
    HTAB *neighborhoods;
    HTAB *frontier_ids;
    LaplaceTrajectoryScope *trajectory_scope = NULL;

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
    if (ARR_NDIM(ctx_arr) > 1 || ARR_ELEMTYPE(ctx_arr) != BYTEAOID)
        ereport(ERROR, (errmsg("walk_continuations: context must be a 1-D bytea array")));
    if (relation_types != NULL)
        validate_relation_types(relation_types);
    if (output_relations != NULL && output_relations != relation_types)
        validate_relation_types(output_relations);

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
    if (ARR_NDIM(front_arr) > 1 || ARR_ELEMTYPE(front_arr) != BYTEAOID)
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
    /* The product forward pass supplies its resolved observation operand.
     * Legacy explicit corpus-continuation callers retain their unscoped form.
     * An explicitly empty observation scope is empty, never corpus-wide. */
    if (PG_NARGS() > 9 && !PG_ARGISNULL(9) && max_order > 0)
    {
        old = MemoryContextSwitchTo(walk_cxt);
        trajectory_scope = laplace_trajectory_scope_create();
        laplace_trajectory_scope_extend(trajectory_scope, PG_GETARG_ARRAYTYPE_P(9));
        MemoryContextSwitchTo(old);
    }
    if (PG_NARGS() > 10 && !PG_ARGISNULL(10) && max_order > 0)
    {
        old = MemoryContextSwitchTo(walk_cxt);
        if (!trajectory_scope) trajectory_scope = laplace_trajectory_scope_create();
        laplace_trajectory_scope_extend_containing(trajectory_scope, PG_GETARG_ARRAYTYPE_P(10));
        MemoryContextSwitchTo(old);
    }
    if (input && max_order > 0)
    {
        old = MemoryContextSwitchTo(walk_cxt);
        if (!trajectory_scope) trajectory_scope = laplace_trajectory_scope_create();
        laplace_trajectory_scope_bind_input(trajectory_scope, input);
        MemoryContextSwitchTo(old);
    }
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
        int depth = input && step == 1 && max_order > 0
            ? ctx_len : Min(ctx_len, max_order);
        if (depth > 0)
        {
            ArrayType *tail = construct_array(ctx + ctx_len - depth, depth,
                                              BYTEAOID, -1, false, TYPALIGN_INT);
            int count;
            LaplaceContinuation *successors = laplace_trajectory_continuations_scoped(
                tail, true, trajectory_scope, &count);
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
                cand[n_cand].projection = 0.0;
                ++n_cand;
            }
            MemoryContextSwitchTo(old);
            pfree(successors);
            pfree(tail);
        }
        /* ---- S6 typed output projection from the live routing frontier ----
         * Renderability cannot establish output purpose. Without an elected
         * relation projection, graph nodes remain internal steering state.
         * No label/type blacklist: even a frame name is legal when it is an
         * observed successor or the endpoint of the requested operation. */
        if (output_relations != NULL && ArrayGetNItems(ARR_NDIM(output_relations),
                                                     ARR_DIMS(output_relations)) > 0)
        {
            HASHCTL ctl = {0};
            ctl.keysize = sizeof(hash128_t);
            ctl.entrysize = sizeof(ProjectionSupport);
            HTAB *projection = hash_create("forward output support", 128, &ctl,
                                           HASH_ELEM | HASH_BLOBS);
            /* A routed constituent does not inherit the whole request's
             * output purpose. Direct completion testimony must bind the input
             * root; subsequent selections become additional output operands. */
            Datum *output_frontier = frontier;
            int n_output_frontier = n_frontier;
            if (input)
            {
                n_output_frontier = 1 + ctx_len - n_in;
                output_frontier = palloc(sizeof(Datum) * n_output_frontier);
                output_frontier[0] = hash128_to_datum(&input->root);
                for (int i = 1; i < n_output_frontier; ++i)
                    output_frontier[i] = ctx[n_in + i - 1];
            }
            proposal_neighborhood(neighborhoods, output_frontier, n_output_frontier,
                                  output_relations, fanout, walk_cxt, projection);
            if (input)
            {
                pfree(DatumGetPointer(output_frontier[0]));
                pfree(output_frontier);
            }
            ctl.entrysize = sizeof(hash128_t);
            HTAB *seen = hash_create("forward output identities", 128,
                                     &ctl, HASH_ELEM | HASH_BLOBS);
            /* Typed endpoints need no content/type/label query. Output purpose
             * established their eligibility; REALIZE owns the requested surface.
             * Retain existing ordinal support when both paths nominate one ID. */
            for (int i = 0; i < n_cand; ++i)
            {
                const char *id = VARDATA_ANY(DatumGetByteaPP(cand[i].obj));
                ProjectionSupport *support = hash_search(projection, id, HASH_FIND, NULL);
                if (support) cand[i].projection = support->score;
                hash_search(seen, id, HASH_ENTER, NULL);
            }
            /* Graph-only emission is a simple path. Ordered observations can
             * independently license repetition of a prompt/emitted identity. */
            for (int i = 0; i < ctx_len; ++i)
                hash_search(seen, VARDATA_ANY(DatumGetByteaPP(ctx[i])), HASH_ENTER, NULL);
            HASH_SEQ_STATUS seq;
            ProjectionSupport *support;
            hash_seq_init(&seq, projection);
            while ((support = hash_seq_search(&seq)) != NULL)
            {
                bool found;
                CHECK_FOR_INTERRUPTS();
                hash_search(seen, &support->id, HASH_ENTER, &found);
                if (found) continue;
                ensure_candidate_capacity(&cand, &cand_capacity, (uint64)n_cand + 1, walk_cxt);
                old = MemoryContextSwitchTo(walk_cxt);
                cand[n_cand] = (Cand){0};
                cand[n_cand].obj = hash128_to_datum(&support->id);
                cand[n_cand++].projection = support->score;
                MemoryContextSwitchTo(old);
            }
            hash_destroy(seen);
            hash_destroy(projection);
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
         * The observation-scoped path has independent witnessed ordinal support:
         * missing graph evidence does not disqualify those continuations.
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
                    /* A continuation in the retained observation scope has
                     * witnessed ordinal support even without a direct semantic
                     * edge. A positive graph proposal cannot erase that evidence.
                     * Keep the corpus-wide compatibility fallback separate. */
                    || (has_positive_steer && cand[i].edges == 0 && !trajectory_scope
                        && !(cand[i].projection > 0.0))
                    || (cand[i].weight == 0 && !(cand[i].projection > 0.0)))
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
                              (cand[m].edges > 0 ? cand[m].steer :
                               cand[m].projection > 0.0 ? cand[m].projection : 1.0);
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

        if (trajectory_scope)
        {
            bool ordered = cand[pick].stride > 0;
            laplace_trajectory_scope_select(trajectory_scope, cand[pick].obj, ordered);
            /* A typed graph result can establish a new observation operand.
             * An emitted sequence constituent advances retained occurrences;
             * its other incident relations do not reopen unrelated contexts. */
            if (!ordered)
            {
                ArrayType *selected = construct_array(ctx + ctx_len - 1, 1,
                    BYTEAOID, -1, false, TYPALIGN_INT);
                laplace_trajectory_scope_extend(trajectory_scope, selected);
                pfree(selected);
            }
        }

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

Datum
pg_laplace_walk_continuations(PG_FUNCTION_ARGS)
{
    return walk_continuations(fcinfo, NULL);
}

typedef struct PromptFrontier
{
    HTAB *seen;
    ArrayBuildState *ids;
    MemoryContext owner;
} PromptFrontier;

static void
prompt_frontier_add(PromptFrontier *frontier, const hash128_t *id)
{
    bool found;
    hash_search(frontier->seen, id, HASH_ENTER, &found);
    if (!found)
        frontier->ids = accumArrayResult(frontier->ids, hash128_to_datum(id),
                                         false, BYTEAOID, frontier->owner);
}

static void
prompt_frontier_edge(const LaplaceWebEdge *edge, void *context)
{
    PromptFrontier *frontier = context;
    MemoryContext previous = MemoryContextSwitchTo(frontier->owner);
    prompt_frontier_add(frontier, &edge->source);
    prompt_frontier_add(frontier, &edge->object);
    MemoryContextSwitchTo(previous);
}

Datum
pg_laplace_forward_prompt(PG_FUNCTION_ARGS)
{
    if (PG_ARGISNULL(0) || VARSIZE_ANY_EXHDR(PG_GETARG_TEXT_PP(0)) == 0)
    {
        InitMaterializedSRF(fcinfo, 0);
        return (Datum) 0;
    }
    /* One retained native tree is shared by preparation, indexed admission,
     * ordered binding and the dynamic walk. SQL binds options and realizes
     * selected identities; it does not flatten/reconstruct the query program. */
    LaplacePromptInput *input = laplace_prompt_input(PG_GETARG_TEXT_PP(0));
    HASHCTL ctl = {0};
    ctl.keysize = ctl.entrysize = sizeof(hash128_t);
    PromptFrontier frontier = {.owner = CurrentMemoryContext,
        .seen = hash_create("prompt routed identities", 128, &ctl, HASH_ELEM | HASH_BLOBS)};
    ArrayIterator iterator = array_create_iterator(input->seeds, 0, NULL);
    Datum value;
    bool isnull;
    while (array_iterate(iterator, &value, &isnull))
    {
        hash128_t id = datum_to_hash128(value);
        prompt_frontier_add(&frontier, &id);
    }
    array_free_iterator(iterator);
    laplace_explore_web(input->seeds,
        PG_ARGISNULL(6) ? 2 : PG_GETARG_INT32(6),
        PG_ARGISNULL(7) ? 8 : PG_GETARG_INT32(7), -1, true,
        prompt_frontier_edge, &frontier);
    if (!PG_ARGISNULL(8))
    {
        ArrayType *prior = PG_GETARG_ARRAYTYPE_P(8);
        if (ARR_NDIM(prior) > 1 || ARR_ELEMTYPE(prior) != BYTEAOID)
            elog(ERROR, "forward prompt: history must be a 1-D bytea array");
        iterator = array_create_iterator(prior, 0, NULL);
        while (array_iterate(iterator, &value, &isnull))
        {
            if (isnull) continue;
            if (VARSIZE_ANY_EXHDR(DatumGetByteaPP(value)) != sizeof(hash128_t))
                elog(ERROR, "forward prompt: history identities must be 16 bytes");
            hash128_t id = datum_to_hash128(value);
            prompt_frontier_add(&frontier, &id);
        }
        array_free_iterator(iterator);
    }
    LOCAL_FCINFO(walk_call, 12);
    InitFunctionCallInfoData(*walk_call, fcinfo->flinfo, 12, fcinfo->fncollation,
                            fcinfo->context, fcinfo->resultinfo);
    for (int i = 0; i < 12; ++i) walk_call->args[i].isnull = true;
    walk_call->args[0] = (NullableDatum){PointerGetDatum(input->context), false};
    for (int i = 1; i <= 5; ++i) walk_call->args[i] = fcinfo->args[i];
    walk_call->args[6] = (NullableDatum){makeArrayResult(frontier.ids, frontier.owner), false};
    walk_call->args[8] = fcinfo->args[7];
    walk_call->args[11] = fcinfo->args[9];
    return walk_continuations(walk_call, input);
}
