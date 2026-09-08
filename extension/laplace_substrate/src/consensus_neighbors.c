#include "postgres.h"
#include "catalog/pg_type.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "consensus_neighbors.h"
#include "relation_symmetry.h"
#include "walk_score.h"

PG_FUNCTION_INFO_V1(pg_laplace_explore_web_neighbors);

typedef struct NeighborEntry
{
    char key[32];
    int heap_index;
} NeighborEntry;

typedef struct NeighborBucket
{
    hash128_t frontier;
    LaplaceNeighbor *heap;
    int count;
    int capacity;
} NeighborBucket;

typedef struct NeighborState
{
    HTAB *pairs;
    HTAB *frontiers;
    int limit;
    bool reverse;
    bool respect_direction;
    bool require_positive;
} NeighborState;

static int
neighbor_rank(const LaplaceNeighbor *a, const LaplaceNeighbor *b)
{
    /* Wide integer subtraction preserves the exact stored ordering without
     * overflowing or collapsing adjacent fixed-point standings to float8. */
    __int128 ar = (__int128) a->rating - 2 * (__int128) a->rd;
    __int128 br = (__int128) b->rating - 2 * (__int128) b->rd;
    int cmp;
    if (ar != br) return ar > br ? -1 : 1;
    cmp = memcmp(&a->neighbor, &b->neighbor, 16);
    if (cmp != 0) return cmp;
    cmp = memcmp(&a->type, &b->type, 16);
    if (cmp != 0) return cmp;
    return a->outbound == b->outbound ? 0 : a->outbound ? -1 : 1;
}

static int
neighbor_order(const void *left, const void *right)
{
    const LaplaceNeighbor *a = left, *b = right;
    int cmp = memcmp(&a->frontier, &b->frontier, 16);
    return cmp != 0 ? cmp : neighbor_rank(a, b);
}

static void
neighbor_key(const LaplaceNeighbor *edge, char key[32])
{
    memcpy(key, &edge->frontier, 16);
    memcpy(key + 16, &edge->neighbor, 16);
}

static void
heap_store(NeighborState *state, NeighborBucket *bucket, int at,
           const LaplaceNeighbor *edge)
{
    char key[32];
    neighbor_key(edge, key);
    NeighborEntry *entry = hash_search(state->pairs, key, HASH_ENTER, NULL);
    entry->heap_index = at;
    bucket->heap[at] = *edge;
}

/* Worst retained neighbor at the root. Improving an existing pair sifts down;
 * adding a new pair sifts up. The bound is applied after exact pair election,
 * without storing the unbounded set of losing pairs. */
static void
heap_down(NeighborState *state, NeighborBucket *bucket, int at,
          const LaplaceNeighbor *edge)
{
    while (at < bucket->count / 2)
    {
        int child = at * 2 + 1;
        if (child + 1 < bucket->count &&
            neighbor_rank(&bucket->heap[child + 1], &bucket->heap[child]) > 0)
            ++child;
        if (neighbor_rank(&bucket->heap[child], edge) <= 0) break;
        heap_store(state, bucket, at, &bucket->heap[child]);
        at = child;
    }
    heap_store(state, bucket, at, edge);
}

static void
neighbor_cell(const LaplaceConsensusRow *row, void *opaque)
{
    NeighborState *state = opaque;
    LaplaceNeighbor edge;
    NeighborEntry *entry;
    NeighborBucket *bucket;
    char key[32];
    bool found;
    if (row->object_is_null || memcmp(&row->subject, &row->object, 16) == 0) return;
    /* Output admission cannot spend its bound on an edge whose signed
     * standing does not support the requested operation. Browse retains all
     * standings; this predicate is an explicit execution operand. */
    if (state->require_positive &&
        !(walk_edge_score(row->type, row->rating, row->rd) > 0.0)) return;
    /* Direction is checked before pair election and the bounded heap. */
    if (state->reverse && state->respect_direction)
    {
        const laplace_relation_def_t *def = NULL;
        if (laplace_relation_lookup(&row->type, &def) != 0 || def == NULL ||
            def->symmetry != LAPLACE_REL_SYMMETRY_SYMMETRIC) return;
    }
    edge.frontier = state->reverse ? row->object : row->subject;
    edge.neighbor = state->reverse ? row->subject : row->object;
    edge.type = row->type;
    edge.rating = row->rating;
    edge.rd = row->rd;
    edge.witnesses = row->witnesses;
    edge.outbound = !state->reverse;
    bucket = hash_search(state->frontiers, &edge.frontier, HASH_ENTER, &found);
    if (!found)
    {
        bucket->heap = NULL;
        bucket->count = bucket->capacity = 0;
    }
    neighbor_key(&edge, key);
    entry = hash_search(state->pairs, key, HASH_FIND, NULL);
    if (entry != NULL)
    {
        if (neighbor_rank(&edge, &bucket->heap[entry->heap_index]) < 0)
            heap_down(state, bucket, entry->heap_index, &edge);
        return;
    }
    if (bucket->count == state->limit)
    {
        if (neighbor_rank(&edge, &bucket->heap[0]) >= 0) return;
        char removed[32];
        neighbor_key(&bucket->heap[0], removed);
        hash_search(state->pairs, removed, HASH_REMOVE, NULL);
        heap_down(state, bucket, 0, &edge);
        return;
    }
    if (bucket->count == bucket->capacity)
    {
        int64 capacity = Min((int64) state->limit,
                             bucket->capacity ? (int64) bucket->capacity * 2 : 16);
        if ((uint64) capacity > MaxAllocSize / sizeof(LaplaceNeighbor))
            ereport(ERROR, (errmsg("consensus neighbor bound exceeds allocation capacity")));
        bucket->capacity = (int) capacity;
        bucket->heap = bucket->heap ? repalloc(bucket->heap, capacity * sizeof(LaplaceNeighbor))
                                    : palloc(capacity * sizeof(LaplaceNeighbor));
    }
    int at = bucket->count++;
    while (at > 0)
    {
        int parent = (at - 1) / 2;
        if (neighbor_rank(&bucket->heap[parent], &edge) >= 0) break;
        heap_store(state, bucket, at, &bucket->heap[parent]);
        at = parent;
    }
    heap_store(state, bucket, at, &edge);
}

static bool
neighbor_cutoff(const LaplaceConsensusRow *row, void *opaque)
{
    NeighborState *state = opaque;
    const hash128_t *id = state->reverse ? &row->object : &row->subject;
    NeighborBucket *bucket;
    if (state->reverse && row->object_is_null) return false;
    bucket = hash_search(state->frontiers, id, HASH_FIND, NULL);
    if (bucket == NULL || bucket->count < state->limit) return false;
    /* Strict inequality retains every tied score for identity/type/direction
     * election. The heap bound improves monotonically across partitions. */
    return (__int128) row->rating - 2 * (__int128) row->rd <
        (__int128) bucket->heap[0].rating - 2 * (__int128) bucket->heap[0].rd;
}

LaplaceNeighbor *
laplace_consensus_neighbors(ArrayType *frontier, ArrayType *types, int limit,
                            bool include_default, bool respect_direction,
                            bool require_positive, int *count,
                            LaplaceConsensusScanStats *stats)
{
    HASHCTL ctl = {0};
    NeighborState state = {0};
    HASH_SEQ_STATUS seq;
    NeighborBucket *bucket;
    LaplaceNeighbor *rows;
    long entries;
    int n = 0;
    *count = 0;
    if (limit <= 0) return NULL;
    ctl.keysize = 32;
    ctl.entrysize = sizeof(NeighborEntry);
    ctl.hcxt = CurrentMemoryContext;
    state.pairs = hash_create("consensus neighbor pairs", 256, &ctl,
                             HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    state.respect_direction = respect_direction;
    state.require_positive = require_positive;
    state.limit = limit;
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(NeighborBucket);
    state.frontiers = hash_create("consensus neighbor frontiers", 64, &ctl,
                                 HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    laplace_consensus_scan_ranked(frontier, NULL, types, false,
        neighbor_cell, neighbor_cutoff, &state, stats);
    if (types != NULL && include_default)
        laplace_consensus_scan_ranked(frontier, NULL, NULL, true,
            neighbor_cell, neighbor_cutoff, &state, stats);
    state.reverse = true;
    ArrayType *reverse_types = respect_direction
        ? laplace_symmetric_relation_types_in(types) : types;
    laplace_consensus_scan_ranked(NULL, frontier, reverse_types, false,
        neighbor_cell, neighbor_cutoff, &state, stats);
    if (respect_direction && types != NULL) pfree(reverse_types);
    if (types != NULL && include_default)
        laplace_consensus_scan_ranked(NULL, frontier, NULL, true,
            neighbor_cell, neighbor_cutoff, &state, stats);
    entries = hash_get_num_entries(state.pairs);
    if (entries > INT_MAX || (Size) entries > MaxAllocSize / sizeof(*rows))
        ereport(ERROR, (errmsg("consensus neighbors exceed PostgreSQL allocation capacity")));
    rows = palloc(sizeof(*rows) * Max(entries, 1));
    hash_seq_init(&seq, state.frontiers);
    while ((bucket = hash_seq_search(&seq)) != NULL)
    {
        memcpy(rows + n, bucket->heap, bucket->count * sizeof(*rows));
        n += bucket->count;
        pfree(bucket->heap);
    }
    qsort(rows, n, sizeof(*rows), neighbor_order);
    *count = n;
    hash_destroy(state.pairs);
    hash_destroy(state.frontiers);
    return rows;
}

Datum
pg_laplace_explore_web_neighbors(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo;
    ArrayType *frontier, *types;
    Datum *input;
    bool *input_nulls;
    int n_input, count, limit;
    LaplaceNeighbor *rows;
    bool include_default = !PG_ARGISNULL(3) && PG_GETARG_BOOL(3);
    if (PG_ARGISNULL(0)) PG_RETURN_NULL();
    frontier = PG_GETARG_ARRAYTYPE_P(0);
    types = PG_ARGISNULL(1) ? NULL : PG_GETARG_ARRAYTYPE_P(1);
    limit = PG_ARGISNULL(2) ? 0 : PG_GETARG_INT32(2);
    InitMaterializedSRF(fcinfo, 0);
    rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    rows = laplace_consensus_neighbors(frontier, types, limit, include_default, false, false, &count, NULL);
    deconstruct_array(frontier, BYTEAOID, -1, false, TYPALIGN_INT,
                      &input, &input_nulls, &n_input);
    /* Preserve input ordinals and duplicate frontier entries at the SQL edge;
     * storage access and reduction operate over the distinct set only once. */
    for (int i = 0; i < n_input; ++i)
    {
        bytea *id;
        int low = 0, high = count;
        if (input_nulls[i]) continue;
        id = DatumGetByteaPP(input[i]);
        if (VARSIZE_ANY_EXHDR(id) != 16)
            ereport(ERROR, (errmsg("consensus neighbors require 16-byte frontier identities")));
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (memcmp(&rows[mid].frontier, VARDATA_ANY(id), 16) < 0) low = mid + 1;
            else high = mid;
        }
        for (int r = low; r < count &&
             memcmp(&rows[r].frontier, VARDATA_ANY(id), 16) == 0; ++r)
        {
            Datum values[7];
            bool nulls[7] = {false, false, false, false, false, false, false};
            bytea *neighbor = palloc(VARHDRSZ + 16), *type = palloc(VARHDRSZ + 16);
            SET_VARSIZE(neighbor, VARHDRSZ + 16);
            SET_VARSIZE(type, VARHDRSZ + 16);
            memcpy(VARDATA(neighbor), &rows[r].neighbor, 16);
            memcpy(VARDATA(type), &rows[r].type, 16);
            values[0] = input[i];
            values[1] = PointerGetDatum(neighbor);
            values[2] = PointerGetDatum(type);
            values[3] = Int64GetDatum(rows[r].rating);
            values[4] = Int64GetDatum(rows[r].rd);
            values[5] = Int64GetDatum(rows[r].witnesses);
            values[6] = BoolGetDatum(rows[r].outbound);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
            pfree(neighbor);
            pfree(type);
        }
    }
    pfree(input);
    pfree(input_nulls);
    if (rows != NULL) pfree(rows);
    return (Datum) 0;
}
