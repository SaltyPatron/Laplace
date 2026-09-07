#include "postgres.h"
#include "catalog/pg_type.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "consensus_neighbors.h"

PG_FUNCTION_INFO_V1(pg_laplace_explore_web_neighbors);

typedef struct NeighborEntry
{
    char key[32];
    LaplaceNeighbor edge;
} NeighborEntry;

typedef struct NeighborState { HTAB *pairs; bool reverse; } NeighborState;

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
neighbor_cell(const LaplaceConsensusRow *row, void *opaque)
{
    NeighborState *state = opaque;
    LaplaceNeighbor edge;
    NeighborEntry *entry;
    char key[32];
    bool found;
    if (row->object_is_null || memcmp(&row->subject, &row->object, 16) == 0) return;
    edge.frontier = state->reverse ? row->object : row->subject;
    edge.neighbor = state->reverse ? row->subject : row->object;
    edge.type = row->type;
    edge.rating = row->rating;
    edge.rd = row->rd;
    edge.witnesses = row->witnesses;
    edge.outbound = !state->reverse;
    memcpy(key, &edge.frontier, 16);
    memcpy(key + 16, &edge.neighbor, 16);
    entry = hash_search(state->pairs, key, HASH_ENTER, &found);
    if (!found || neighbor_rank(&edge, &entry->edge) < 0) entry->edge = edge;
}

LaplaceNeighbor *
laplace_consensus_neighbors(ArrayType *frontier, ArrayType *types, int limit,
                            bool include_default, int *count,
                            LaplaceConsensusScanStats *stats)
{
    HASHCTL ctl = {0};
    NeighborState state = {0};
    HASH_SEQ_STATUS seq;
    NeighborEntry *entry;
    LaplaceNeighbor *rows;
    long entries;
    int n = 0, retained = 0, per_frontier = 0;
    hash128_t previous;
    *count = 0;
    if (limit <= 0) return NULL;
    ctl.keysize = 32;
    ctl.entrysize = sizeof(NeighborEntry);
    ctl.hcxt = CurrentMemoryContext;
    state.pairs = hash_create("consensus neighbor pairs", 256, &ctl,
                             HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    laplace_consensus_scan(frontier, NULL, types, neighbor_cell, &state, stats);
    if (types != NULL && include_default)
        laplace_consensus_scan_default(frontier, NULL, neighbor_cell, &state, stats);
    state.reverse = true;
    laplace_consensus_scan(NULL, frontier, types, neighbor_cell, &state, stats);
    if (types != NULL && include_default)
        laplace_consensus_scan_default(NULL, frontier, neighbor_cell, &state, stats);
    entries = hash_get_num_entries(state.pairs);
    if (entries > INT_MAX || (Size) entries > MaxAllocSize / sizeof(*rows))
        ereport(ERROR, (errmsg("consensus neighbors exceed PostgreSQL allocation capacity")));
    rows = palloc(sizeof(*rows) * Max(entries, 1));
    hash_seq_init(&seq, state.pairs);
    while ((entry = hash_seq_search(&seq)) != NULL) rows[n++] = entry->edge;
    qsort(rows, n, sizeof(*rows), neighbor_order);
    for (int i = 0; i < n; ++i)
    {
        if (i == 0 || memcmp(&previous, &rows[i].frontier, 16) != 0)
        {
            previous = rows[i].frontier;
            per_frontier = 0;
        }
        if (per_frontier++ < limit) rows[retained++] = rows[i];
    }
    *count = retained;
    hash_destroy(state.pairs);
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
    bool typed = PG_NARGS() == 3;
    if (PG_ARGISNULL(0)) PG_RETURN_NULL();
    frontier = PG_GETARG_ARRAYTYPE_P(0);
    types = typed && !PG_ARGISNULL(1) ? PG_GETARG_ARRAYTYPE_P(1) : NULL;
    limit = PG_ARGISNULL(typed ? 2 : 1) ? 0 : PG_GETARG_INT32(typed ? 2 : 1);
    InitMaterializedSRF(fcinfo, 0);
    rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    /* The historical typed NULL means the DEFAULT partition only. */
    if (typed && types == NULL) types = construct_empty_array(BYTEAOID);
    rows = laplace_consensus_neighbors(frontier, types, limit, typed, &count, NULL);
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
