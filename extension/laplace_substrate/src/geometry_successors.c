#include "postgres.h"
#include "miscadmin.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "spi_common.h"
#include "spi_nested.h"
#include "spi_stream.h"
#include "laplace/core/sql_catalog.h"
#include "trajectory_wkb.h"

#include "laplace/core/mantissa.h"

/*
 * structural.geometry_successors_batch(points, limit, window, backward, type)
 *   -> TABLE(point_id bytea, successor_id bytea, seen bigint)
 *
 * Sequence projection over the trajectory face: for each requested point, every
 * trajectory of the given physicality type that contains it yields the nearest
 * non-separator constituent after (or before) the point's first occurrence,
 * within `window` positions. Successors are counted per point and the top
 * `limit` per point are returned by count. Separators are the ids
 * generation.separator_ids() declares. The pair is a co-occurrence read off
 * trajectory order; it does not assert a relation between the two entities.
 *
 * One GIN-served containment query covers every requested point; each matching
 * trajectory arrives as one LINESTRING ZM WKB whose vertex order is ordinal
 * order, so vertices are decoded here with mantissa_unpack and run lengths are
 * scanned as compressed ordered runs without materializing repeated occurrences.
 */

static SPIPlanPtr batch_unpack_plan = NULL;
static SPIPlanPtr separator_plan = NULL;

typedef struct IdEntry { char key[16]; } IdEntry;

typedef struct RootEntry
{
    char  key[16];
    int32 index;
    uint64 generation;
} RootEntry;

typedef struct PairEntry
{
    char  key[32];
    int64 count;
} PairEntry;

typedef struct BatchResult
{
    char  key[32];
    int32 root_index;
    int64 count;
} BatchResult;

static void
ensure_plans(void)
{
    if (batch_unpack_plan == NULL)
    {
        Oid        argtypes[2] = { BYTEAARRAYOID, INT2OID };
        /*
         * SPI_prepare plans with parallelism disabled. The containment
         * predicate is on constituents while physicalities is hash-partitioned
         * by id, so nothing prunes and every partition is scanned; a read-only
         * CURSOR_OPT_PARALLEL_OK plan lets that scan run as a Parallel Append.
         */
        SPIPlanPtr plan = SPI_prepare_cursor(laplace_sql_query_text("geometry.successor_carriers"), 2, argtypes,
                                            CURSOR_OPT_PARALLEL_OK);

        if (plan == NULL)
            elog(ERROR, "geometry_successors: SPI_prepare_cursor(batch unpack) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "geometry_successors: SPI_keepplan(batch unpack) failed");
        batch_unpack_plan = plan;
    }

    if (separator_plan == NULL)
    {
        SPIPlanPtr plan = SPI_prepare_cursor(laplace_sql_query_text("geometry.separators"), 0, NULL, CURSOR_OPT_PARALLEL_OK);

        if (plan == NULL)
            elog(ERROR, "geometry_successors: SPI_prepare(separators) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "geometry_successors: SPI_keepplan(separators) failed");
        separator_plan = plan;
    }
}

static HTAB *
load_separators(MemoryContext cxt)
{
    HASHCTL ctl;
    HTAB   *separators;
    int     rc;

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(IdEntry);
    ctl.hcxt = cxt;
    separators = hash_create("geometry successor separators", 128, &ctl,
                             HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    rc = SPI_execute_plan(separator_plan, NULL, NULL, true, 1);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "geometry_successors: separator query failed: %s",
             SPI_result_code_string(rc));
    if (SPI_processed > 0)
    {
        bool  isnull;
        Datum value = SPI_getbinval(SPI_tuptable->vals[0],
                                    SPI_tuptable->tupdesc, 1, &isnull);

        if (!isnull)
        {
            Datum *ids;
            bool  *nulls;
            int    n_ids;

            deconstruct_array(DatumGetArrayTypeP(value), BYTEAOID, -1, false,
                              TYPALIGN_INT, &ids, &nulls, &n_ids);
            for (int i = 0; i < n_ids; i++)
            {
                bytea *id;
                bool   found;

                if (nulls[i])
                    continue;
                id = DatumGetByteaPP(ids[i]);
                if (VARSIZE_ANY_EXHDR(id) != 16)
                    continue;
                (void) hash_search(separators, VARDATA_ANY(id),
                                   HASH_ENTER, &found);
            }
        }
    }
    SPI_freetuptable(SPI_tuptable);
    return separators;
}

static void
record_batch_pair(HTAB *pairs, const char *root, const char *successor)
{
    char       key[32];
    PairEntry *entry;
    bool       found;

    memcpy(key, root, 16);
    memcpy(key + 16, successor, 16);
    entry = (PairEntry *) hash_search(pairs, key, HASH_ENTER, &found);
    if (!found)
        entry->count = 0;
    entry->count++;
}

typedef struct {
    HTAB *roots, *separators, *pairs;
    int window;
    bool backward;
    uint64 generation;
} SuccessorRead;

/* Read compressed occurrences directly. A run containing millions of equal
 * children stays one run; separator counts consume the logical window in one
 * step. The first occurrence of each requested root still owns its successor. */
static void
receive_successor_trajectory(TupleTableSlot *row, void *opaque)
{
    SuccessorRead *read = opaque;
    bool isnull;
    Datum value = slot_getattr(row, 1, &isnull);
    if (isnull) return;
    uint32 count;
    const unsigned char *points = laplace_trajectory_wkb_points(DatumGetByteaPP(value), &count);
    if (++read->generation == 0)
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
            errmsg("geometry_successors_batch: trajectory generation overflow")));
    for (uint32 i = 0; i < count; i++) {
        CHECK_FOR_INTERRUPTS();
        double vertex[4]; mantissa_payload_t token;
        memcpy(vertex, points + (Size)i * 32, sizeof(vertex));
        mantissa_unpack(vertex, &token);
        RootEntry *root = hash_search(read->roots, &token.entity_id, HASH_FIND, NULL);
        if (!root || root->generation == read->generation) continue;
        root->generation = read->generation;
        uint64 remaining = read->window;
        for (int64 j = (int64)i + (read->backward ? -1 : 1);
             j >= 0 && j < count && remaining; j += read->backward ? -1 : 1) {
            mantissa_payload_t candidate;
            memcpy(vertex, points + (Size)j * 32, sizeof(vertex));
            mantissa_unpack(vertex, &candidate);
            if (!read->backward && memcmp(&candidate.entity_id, root->key, 16) == 0)
                continue;
            if (!hash_search(read->separators, &candidate.entity_id, HASH_FIND, NULL)) {
                record_batch_pair(read->pairs, root->key, (const char *)&candidate.entity_id);
                break;
            }
            uint64 run = candidate.run_length ? candidate.run_length : 1;
            if (run >= remaining) break;
            remaining -= run;
        }
    }
}

static int
batch_result_cmp(const void *a, const void *b)
{
    const BatchResult *x = (const BatchResult *) a;
    const BatchResult *y = (const BatchResult *) b;

    if (x->root_index < y->root_index) return -1;
    if (x->root_index > y->root_index) return 1;
    if (x->count > y->count) return -1;
    if (x->count < y->count) return 1;
    return memcmp(x->key + 16, y->key + 16, 16);
}

PG_FUNCTION_INFO_V1(pg_laplace_geometry_successors_batch);

Datum
pg_laplace_geometry_successors_batch(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    ArrayType     *points_array;
    Datum         *point_datums;
    bool          *point_nulls;
    int            n_input;
    int32          limit_rows;
    int32          window;
    bool           backward;
    int16          physicality_type;
    bool           spi_top = false;
    HASHCTL        ctl;
    HTAB          *roots;
    HTAB          *separators;
    HTAB          *pairs;
    char          *root_ids;
    int            n_roots = 0;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("geometry_successors_batch: points must not be NULL")));

    points_array = PG_GETARG_ARRAYTYPE_P(0);
    limit_rows = PG_ARGISNULL(1) ? 20 : PG_GETARG_INT32(1);
    window = PG_ARGISNULL(2) ? 8 : PG_GETARG_INT32(2);
    backward = (PG_NARGS() > 3 && !PG_ARGISNULL(3)) ? PG_GETARG_BOOL(3) : false;
    physicality_type = (PG_NARGS() > 4 && !PG_ARGISNULL(4)) ? PG_GETARG_INT16(4) : 1;
    if (limit_rows < 0)
        ereport(ERROR, (errmsg("geometry_successors_batch: limit must not be negative")));
    if (window < 0)
        ereport(ERROR, (errmsg("geometry_successors_batch: window must not be negative")));

    deconstruct_array(points_array, BYTEAOID, -1, false, TYPALIGN_INT,
                      &point_datums, &point_nulls, &n_input);
    InitMaterializedSRF(fcinfo, 0);
    if (n_input < 1 || limit_rows == 0 || window == 0)
        return (Datum) 0;

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(RootEntry);
    ctl.hcxt = CurrentMemoryContext;
    roots = hash_create("geometry successor batch roots", n_input, &ctl,
                        HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    root_ids = (char *) palloc((Size) n_input * 16);

    for (int i = 0; i < n_input; i++)
    {
        bytea    *point;
        RootEntry *root;
        bool       found;

        if (point_nulls[i])
            ereport(ERROR, (errmsg("geometry_successors_batch: points contain NULL")));
        point = DatumGetByteaPP(point_datums[i]);
        if (VARSIZE_ANY_EXHDR(point) != 16)
            ereport(ERROR, (errmsg("geometry_successors_batch: points must be 16-byte ids")));

        root = (RootEntry *) hash_search(roots, VARDATA_ANY(point),
                                         HASH_ENTER, &found);
        if (!found)
        {
            root->index = n_roots;
            root->generation = 0;
            memcpy(root_ids + (Size) n_roots * 16, VARDATA_ANY(point), 16);
            n_roots++;
        }
    }

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "geometry_successors_batch: SPI_connect failed");
    ensure_plans();
    separators = load_separators(CurrentMemoryContext);

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 32;
    ctl.entrysize = sizeof(PairEntry);
    ctl.hcxt = CurrentMemoryContext;
    pairs = hash_create("geometry successor batch pairs", n_roots * 64,
                        &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    {
        Datum     *normalized_datums = (Datum *) palloc(sizeof(Datum) * (Size) n_roots);
        ArrayType *normalized;
        Datum      args[2];
        char       nulls[2] = { ' ', ' ' };

        for (int i = 0; i < n_roots; i++)
        {
            bytea *point = (bytea *) palloc(VARHDRSZ + 16);

            SET_VARSIZE(point, VARHDRSZ + 16);
            memcpy(VARDATA(point), root_ids + (Size) i * 16, 16);
            normalized_datums[i] = PointerGetDatum(point);
        }
        normalized = construct_array(normalized_datums, n_roots, BYTEAOID,
                                     -1, false, TYPALIGN_INT);
        args[0] = PointerGetDatum(normalized);
        args[1] = Int16GetDatum(physicality_type);
        Oid types[] = {BYTEAARRAYOID, INT2OID};
        SuccessorRead read = {roots, separators, pairs, window, backward, 0};
        laplace_spi_stream(batch_unpack_plan, 2, types, args, nulls,
            receive_successor_trajectory, &read);
    }

    {
        int             n_pairs = (int) hash_get_num_entries(pairs);
        BatchResult    *ordered = (BatchResult *) palloc(
            sizeof(BatchResult) * (n_pairs > 0 ? (Size) n_pairs : 1));
        HASH_SEQ_STATUS seq;
        PairEntry      *pair;
        int             n = 0;
        int             current_root = -1;
        int             emitted = 0;

        hash_seq_init(&seq, pairs);
        while ((pair = (PairEntry *) hash_seq_search(&seq)) != NULL)
        {
            RootEntry *root = (RootEntry *) hash_search(
                roots, pair->key, HASH_FIND, NULL);

            memcpy(ordered[n].key, pair->key, 32);
            ordered[n].root_index = root->index;
            ordered[n].count = pair->count;
            n++;
        }
        qsort(ordered, (size_t) n, sizeof(BatchResult), batch_result_cmp);

        for (int i = 0; i < n; i++)
        {
            bytea *root;
            bytea *successor;
            Datum  values[3];
            bool   nulls[3] = { false, false, false };

            if (ordered[i].root_index != current_root)
            {
                current_root = ordered[i].root_index;
                emitted = 0;
            }
            if (emitted >= limit_rows)
                continue;
            emitted++;

            root = (bytea *) palloc(VARHDRSZ + 16);
            successor = (bytea *) palloc(VARHDRSZ + 16);
            SET_VARSIZE(root, VARHDRSZ + 16);
            SET_VARSIZE(successor, VARHDRSZ + 16);
            memcpy(VARDATA(root), ordered[i].key, 16);
            memcpy(VARDATA(successor), ordered[i].key + 16, 16);
            values[0] = PointerGetDatum(root);
            values[1] = PointerGetDatum(successor);
            values[2] = Int64GetDatum(ordered[i].count);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc,
                                 values, nulls);
        }
    }

    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
