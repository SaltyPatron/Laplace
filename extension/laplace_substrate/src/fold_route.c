/*
 * fold_route.c — the consensus fold write: a set of attested evidence deltas
 * folded into their consensus cells in one call (ingest's "fold in sets").
 *
 * laplace.consensus is HASH(subject_id) partitioned. Input arrives as runs of
 * one relation type; each run uses session-cached plans whose type_id is a hex
 * literal in the plan text, and its subjects are routed in C through
 * PostgreSQL's own HASH partition function so each exact leaf receives only
 * the rows it owns. A type split across runs executes its plans twice on
 * disjoint rows.
 *
 * Per run: read and row-lock stored priors from their exact leaves, fold every
 * cell natively (matched cells from their prior, novel cells from the neutral
 * prior) with the glicko2 kernels the SQL scalar
 * laplace_glicko2_accumulate_period also wraps, then persist matched rows by
 * keyed update and novel rows by bulk COPY. A MERGE runs only when a
 * concurrent insert collides with a row classified novel.
 *
 * Cell id is blake3(subject || type || COALESCE(object, 16 zero bytes))
 * through the core hash128_blake3, the byte layout of the SQL consensus_id.
 */
#include "postgres.h"

#include "access/table.h"
#include "access/xact.h"
#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "miscadmin.h"
#include "partitioning/partbounds.h"
#include "hash_leaves.h"
#include "partitioning/partdesc.h"
#include "storage/fd.h"
#include <inttypes.h>
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/partcache.h"
#include "utils/resowner.h"

#include "laplace/core/hash128.h"
#include "laplace/core/glicko2.h"

#include "laplace/core/sql_catalog.h"
#include "consensus_fold_math.h"
#include "consensus_bulk_write.h"

PG_FUNCTION_INFO_V1(pg_laplace_attestation_merge);
PG_FUNCTION_INFO_V1(pg_laplace_attestation_merge_type);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_upsert);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_upsert_type);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_merge_evidence);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_upsert_evidence_type);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_refold_evidence_type);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_partition_leaf);

/* ------------------------------------------------------------------ */
/* Session plan cache: one HTAB per statement family, keyed by type id */
/* ------------------------------------------------------------------ */

typedef struct TypePlanEntry
{
    char       type_id[16];
    SPIPlanPtr plan;
} TypePlanEntry;

static HTAB *upsert_matched_plans = NULL; /* consensus PK-arbitrated updates  */
static HTAB *upsert_novel_plans = NULL;   /* consensus target-free inserts    */
static HTAB *upsert_merge_plans = NULL;   /* concurrent-insert collision MERGE */
static HTAB *evidence_lock_plans = NULL;
static HTAB *evidence_fold_plans = NULL;
static HTAB *evidence_write_plans = NULL;

/* One exact-leaf plan per (type, HASH remainder). A prior read whose subject
 * keys come from unnest would otherwise probe every leaf for each input row. */

typedef struct PriorRouteEntry
{
    char        type_id[16];
    const LaplaceHashLeaves *leaves;
    SPIPlanPtr *leaf_plans;    /* per leaf remainder */
    SPIPlanPtr *update_plans;  /* per leaf remainder */
} PriorRouteEntry;

static HTAB *upsert_prior_routes = NULL;

static const uint8_t *bytea16(Datum d, const char *label);

static HTAB *
plan_htab(HTAB **slot, const char *name)
{
    if (*slot == NULL)
    {
        HASHCTL ctl;

        memset(&ctl, 0, sizeof(ctl));
        ctl.keysize = 16;
        ctl.entrysize = sizeof(TypePlanEntry);
        ctl.hcxt = TopMemoryContext;
        *slot = hash_create(name, 256, &ctl,
                            HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    }
    return *slot;
}

/* Fetch (or build+cache) the per-type plan for one statement family.
 * `template_sql` contains one %s to receive the 32-hex-char type literal
 * (it may appear multiple times via %1$s-style repetition below). */
static SPIPlanPtr
typed_plan(HTAB **slot, const char *name, const uint8_t *type16,
           const char *template_sql, int nargs, const Oid *argtypes)
{
    TypePlanEntry *entry;
    bool           found;

    entry = (TypePlanEntry *) hash_search(plan_htab(slot, name), type16,
                                          HASH_ENTER, &found);
    if (!found)
    {
        char       hex[33];
        StringInfoData sql;
        SPIPlanPtr plan;
        const char *p;
        int         j;

        for (j = 0; j < 16; j++)
            snprintf(hex + j * 2, 3, "%02x", type16[j]);

        /* substitute every %s in the template with the hex literal */
        initStringInfo(&sql);
        for (p = template_sql; *p; p++)
        {
            if (p[0] == '%' && p[1] == 's')
            {
                appendStringInfoString(&sql, hex);
                p++;
            }
            else
                appendStringInfoChar(&sql, *p);
        }

        plan = SPI_prepare(sql.data, nargs, (Oid *) argtypes);
        if (plan == NULL)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: SPI_prepare failed: %s",
                            name, SPI_result_code_string(SPI_result))));
        if (SPI_keepplan(plan) != 0)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: SPI_keepplan failed", name)));
        entry->plan = plan;
        pfree(sql.data);
    }
    return entry->plan;
}

static HTAB *
prior_route_htab(void)
{
    if (upsert_prior_routes == NULL)
    {
        HASHCTL ctl;

        memset(&ctl, 0, sizeof(ctl));
        ctl.keysize = 16;
        ctl.entrysize = sizeof(PriorRouteEntry);
        ctl.hcxt = TopMemoryContext;
        upsert_prior_routes = hash_create("consensus exact prior routes", 256,
                                          &ctl,
                                          HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    }
    return upsert_prior_routes;
}

/* Consensus is HASH(subject_id); a relation type owns no partition of its own.
 * The per-type entry carries that type's prepared leaf plans. */
static PriorRouteEntry *
prior_route(const uint8_t *type16, Datum type_datum, const char *label)
{
    PriorRouteEntry *entry;
    bool             found;

    (void) type_datum;
    entry = (PriorRouteEntry *) hash_search(prior_route_htab(), type16,
                                             HASH_ENTER, &found);
    if (!found)
    {
        entry->leaves = laplace_hash_leaves("consensus", label);
        entry->leaf_plans = MemoryContextAllocZero(
            TopMemoryContext, sizeof(SPIPlanPtr) * entry->leaves->count);
        entry->update_plans = MemoryContextAllocZero(
            TopMemoryContext, sizeof(SPIPlanPtr) * entry->leaves->count);
    }
    else
    {
        /* A swapped leaf keeps its name, so the prepared per-leaf plans stay valid; the
         * leaf oids they were named from are resolved again. */
        entry->leaves = laplace_hash_leaves("consensus", label);
    }
    return entry;
}

/* The physical consensus leaf that owns (type, subject), so SQL that mutates
 * consensus outside this fold can address one leaf instead of a parent
 * statement that opens every HASH child. Reads no consensus row. */
Datum
pg_laplace_consensus_partition_leaf(PG_FUNCTION_ARGS)
{
    const char *label = "consensus.partition_leaf";
    const LaplaceHashLeaves *leaves;
    Datum subject;
    int remainder;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR,
                (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                 errmsg("%s: type and subject must not be NULL", label)));
    (void) bytea16(PG_GETARG_DATUM(0), label);
    (void) bytea16(PG_GETARG_DATUM(1), label);
    leaves = laplace_hash_leaves("consensus", label);
    subject = PG_GETARG_DATUM(1);
    laplace_hash_leaf_route(leaves, &subject, 1, &remainder, label);
    PG_RETURN_OID(leaves->leaf_oids[remainder]);
}

static SPIPlanPtr
prior_leaf_plan(PriorRouteEntry *route, int remainder,
                const uint8_t *type16, const char *label)
{
    SPIPlanPtr plan = route->leaf_plans[remainder];

    if (plan == NULL)
    {
        static const Oid argtypes[2] = {BYTEAARRAYOID, BYTEAARRAYOID};
        char             hex[33];
        char            *namespace_name;
        char            *relation_name;
        char            *qualified_name;
        StringInfoData   sql;
        int              i;

        namespace_name = get_namespace_name(
            get_rel_namespace(route->leaves->leaf_oids[remainder]));
        relation_name = get_rel_name(route->leaves->leaf_oids[remainder]);
        if (namespace_name == NULL || relation_name == NULL)
            ereport(ERROR,
                    (errcode(ERRCODE_UNDEFINED_TABLE),
                     errmsg("%s: consensus HASH leaf disappeared", label)));
        qualified_name = quote_qualified_identifier(namespace_name,
                                                     relation_name);
        for (i = 0; i < 16; i++)
            snprintf(hex + i * 2, 3, "%02x", type16[i]);

        initStringInfo(&sql);
        appendStringInfo(&sql,
            "WITH locked AS MATERIALIZED ("
            "  SELECT c.id, c.subject_id, c.rating, c.rd, c.volatility "
            "  FROM ONLY %s c "
            "  WHERE c.type_id = '\\x%s'::bytea "
            "    AND c.id = ANY($1::bytea[]) "
            "  FOR UPDATE OF c) "
            "SELECT b.ord, locked.rating, locked.rd, locked.volatility "
            "FROM unnest($1::bytea[], $2::bytea[]) WITH ORDINALITY "
            "     AS b(id, s, ord) "
            "JOIN locked ON locked.subject_id = b.s AND locked.id = b.id",
            qualified_name, hex);
        plan = SPI_prepare(sql.data, 2, (Oid *) argtypes);
        if (plan == NULL)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: exact-leaf prior SPI_prepare failed: %s",
                            label, SPI_result_code_string(SPI_result))));
        if (SPI_keepplan(plan) != 0)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: exact-leaf prior SPI_keepplan failed", label)));
        route->leaf_plans[remainder] = plan;
        pfree(sql.data);
    }
    return plan;
}

/* ------------------------------------------------------------------ */
/* Array plumbing                                                      */
/* ------------------------------------------------------------------ */

typedef struct InArray
{
    ArrayType *array;          /* original detoasted argument; reusable whole */
    Datum *elems;
    bool  *nulls;
    int    n;
} InArray;

static void
in_array(FunctionCallInfo fcinfo, int argno, Oid elmtype, int elmlen,
         bool elmbyval, char elmalign, bool allow_nulls, const char *label,
         InArray *out)
{
    ArrayType *arr;

    if (PG_ARGISNULL(argno))
        ereport(ERROR,
                (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                 errmsg("%s: argument %d must not be NULL", label, argno + 1)));
    arr = PG_GETARG_ARRAYTYPE_P(argno);
    out->array = arr;
    if (ARR_NDIM(arr) > 1)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: argument %d must be 1-dimensional", label, argno + 1)));
    deconstruct_array(arr, elmtype, elmlen, elmbyval, elmalign,
                      &out->elems, &out->nulls, &out->n);
    if (!allow_nulls)
    {
        int i;

        for (i = 0; i < out->n; i++)
            if (out->nulls[i])
                ereport(ERROR,
                        (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                         errmsg("%s: argument %d must not contain NULLs",
                                label, argno + 1)));
    }
}

static const uint8_t *
bytea16(Datum d, const char *label)
{
    bytea *b = DatumGetByteaPP(d);

    if (VARSIZE_ANY_EXHDR(b) != 16)
        ereport(ERROR,
                (errcode(ERRCODE_DATA_EXCEPTION),
                 errmsg("%s: expected 16-byte id, got %zu bytes",
                        label, (size_t) VARSIZE_ANY_EXHDR(b))));
    return (const uint8_t *) VARDATA_ANY(b);
}

/*
 * n copies of the neutral opponent, for callers that supply no per-witness
 * ratings. The collision MERGE consumes the same opponents as the native fold,
 * so a concurrent insert cannot change the evidence folded.
 */
static ArrayType *
neutral_opponent_array(int n)
{
    Datum *d = (Datum *) palloc(sizeof(Datum) * (n > 0 ? n : 1));
    int    i;

    for (i = 0; i < n; i++)
        d[i] = Int64GetDatum(CONSENSUS_FOLD_NEUTRAL_MU);
    return construct_array(d, n, INT8OID, 8, true, 'd');
}

static ArrayType *
array_window(ArrayType *original, const Datum *src, const bool *src_nulls,
            int total, int start, int n,
            Oid elmtype, int elmlen, bool elmbyval, char elmalign)
{
    if (start == 0 && n == total)
        return original;

    Datum *d = (Datum *) palloc(sizeof(Datum) * n);
    bool  *nu = (bool *) palloc(sizeof(bool) * n);
    int    dims[1];
    int    lbs[1] = {1};
    int    i;
    bool   any_null = false;

    for (i = 0; i < n; i++)
    {
        bool isnull = src_nulls != NULL && src_nulls[start + i];

        d[i] = isnull ? (Datum) 0 : src[start + i];
        nu[i] = isnull;
        any_null |= isnull;
    }
    dims[0] = n;
    if (any_null)
        return construct_md_array(d, nu, 1, dims, lbs,
                                  elmtype, elmlen, elmbyval, elmalign);
    return construct_array(d, n, elmtype, elmlen, elmbyval, elmalign);
}

static InArray
in_array_window(const InArray *source, int start, int n,
                Oid elmtype, int elmlen, bool elmbyval, char elmalign)
{
    InArray out;

    out.array = array_window(source->array, source->elems, source->nulls,
                             source->n, start, n,
                             elmtype, elmlen, elmbyval, elmalign);
    out.elems = source->elems + start;
    out.nulls = source->nulls != NULL ? source->nulls + start : NULL;
    out.n = n;
    return out;
}

typedef struct FoldStateArrays
{
    ArrayType *seen_array;
    ArrayType *rating_array;
    ArrayType *rd_array;
    ArrayType *volatility_array;
} FoldStateArrays;

typedef struct FoldPriorStates
{
    bool   *matched;
    Datum  *ratings;
    Datum  *rds;
    Datum  *volatilities;
    int     n;
    uint64  matched_n;
    int    *leaves;     /* exact HASH leaf remainder of each row */
} FoldPriorStates;

typedef struct PriorLeafBatch
{
    Datum *ids;
    Datum *subjects;
    int   *positions;
    int    n;
    int    fill;
} PriorLeafBatch;

static FoldPriorStates *
fold_prior_states_create(int n)
{
    FoldPriorStates *states = (FoldPriorStates *) palloc(sizeof(*states));

    states->matched = (bool *) palloc0(sizeof(bool) * n);
    states->leaves = (int *) palloc0(sizeof(int) * n);
    states->ratings = (Datum *) palloc(sizeof(Datum) * n);
    states->rds = (Datum *) palloc(sizeof(Datum) * n);
    states->volatilities = (Datum *) palloc(sizeof(Datum) * n);
    states->n = n;
    states->matched_n = 0;
    return states;
}

static void
fold_prior_states_add(FoldPriorStates *states, SPITupleTable *rows,
                      uint64 nrows, const int *positions, int npositions,
                      const char *label)
{
    uint64 r;

    for (r = 0; r < nrows; r++)
    {
        HeapTuple tup = rows->vals[r];
        TupleDesc desc = rows->tupdesc;
        bool      null_ord, null_rating, null_rd, null_vol;
        int64     leaf_ord = DatumGetInt64(
            SPI_getbinval(tup, desc, 1, &null_ord));
        Datum     prior_rating = SPI_getbinval(tup, desc, 2, &null_rating);
        Datum     prior_rd = SPI_getbinval(tup, desc, 3, &null_rd);
        Datum     prior_vol = SPI_getbinval(tup, desc, 4, &null_vol);
        int       position;

        if (null_ord || null_rating || null_rd || null_vol ||
            leaf_ord < 1 || leaf_ord > (int64) npositions)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: exact-leaf prior read returned an invalid row",
                            label)));
        position = positions[leaf_ord - 1];
        if (position < 0 || position >= states->n || states->matched[position])
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: exact-leaf prior read returned duplicate routing",
                            label)));
        states->matched[position] = true;
        states->ratings[position] = prior_rating;
        states->rds[position] = prior_rd;
        states->volatilities[position] = prior_vol;
        states->matched_n++;
    }
}

/* Existing cells update in the HASH leaf that owns them: one keyed UPDATE per
 * touched leaf, routed as the locked prior read was. Witness count adds and
 * last_observed_at takes the later time. */
static SPIPlanPtr
update_leaf_plan(PriorRouteEntry *route, int remainder, const char *label)
{
    SPIPlanPtr plan = route->update_plans[remainder];

    if (plan == NULL)
    {
        static const Oid argtypes[8] = {BYTEAARRAYOID, BYTEAARRAYOID, INT8ARRAYOID,
            TIMESTAMPTZARRAYOID, INT8ARRAYOID, INT8ARRAYOID, INT8ARRAYOID, BOOLARRAYOID};
        char *namespace_name = get_namespace_name(
            get_rel_namespace(route->leaves->leaf_oids[remainder]));
        char *relation_name = get_rel_name(route->leaves->leaf_oids[remainder]);
        StringInfoData sql;

        if (namespace_name == NULL || relation_name == NULL)
            ereport(ERROR,
                    (errcode(ERRCODE_UNDEFINED_TABLE),
                     errmsg("%s: consensus HASH leaf disappeared", label)));
        initStringInfo(&sql);
        appendStringInfo(&sql,
            "UPDATE ONLY %s AS c SET rating=b.rating, rd=b.rd, volatility=b.volatility,"
            " witness_count=CASE WHEN b.recomputed THEN b.games ELSE c.witness_count+b.games END,"
            " last_observed_at=GREATEST(c.last_observed_at,b.ts) "
            "FROM unnest($1::bytea[],$2::bytea[],$3::int8[],$4::timestamptz[],"
            " $5::int8[],$6::int8[],$7::int8[],$8::bool[])"
            " AS b(id,s,games,ts,rating,rd,volatility,recomputed) "
            "WHERE c.id=b.id AND c.subject_id=b.s",
            quote_qualified_identifier(namespace_name, relation_name));
        plan = SPI_prepare(sql.data, 8, (Oid *) argtypes);
        if (plan == NULL)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: exact-leaf update SPI_prepare failed: %s",
                            label, SPI_result_code_string(SPI_result))));
        if (SPI_keepplan(plan) != 0)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: exact-leaf update SPI_keepplan failed", label)));
        route->update_plans[remainder] = plan;
    }
    return plan;
}

/* Route each subject through PostgreSQL's partition support function, then
 * read and FOR UPDATE lock its stored cell from the one leaf that owns it. The
 * batch is partitioned once in native memory, so each leaf statement plans
 * without an Append and sees only its own rows. */
static FoldPriorStates *
read_run_priors(const uint8_t *type16, Datum type_datum,
                const Datum *cell_ids, const InArray *subjects,
                int run_start, int run_n, const char *label)
{
    PriorRouteEntry *route = prior_route(type16, type_datum, label);
    int               nleaves = route->leaves->count;
    PriorLeafBatch   *batches = palloc0(sizeof(PriorLeafBatch) * nleaves);
    FoldPriorStates *states = fold_prior_states_create(run_n);
    int              *remainder_by_row = (int *) palloc(sizeof(int) * run_n);
    int               i;

    laplace_hash_leaf_route(route->leaves, subjects->elems + run_start, run_n,
                            remainder_by_row, label);
    for (i = 0; i < run_n; i++)
    {
        states->leaves[i] = remainder_by_row[i];
        batches[remainder_by_row[i]].n++;
    }

    for (i = 0; i < nleaves; i++)
    {
        if (batches[i].n == 0)
            continue;
        batches[i].ids = (Datum *) palloc(sizeof(Datum) * batches[i].n);
        batches[i].subjects = (Datum *) palloc(sizeof(Datum) * batches[i].n);
        batches[i].positions = (int *) palloc(sizeof(int) * batches[i].n);
    }
    for (i = 0; i < run_n; i++)
    {
        PriorLeafBatch *batch = &batches[remainder_by_row[i]];
        int             at = batch->fill++;

        batch->ids[at] = cell_ids[run_start + i];
        batch->subjects[at] = subjects->elems[run_start + i];
        batch->positions[at] = i;
    }

    for (i = 0; i < nleaves; i++)
    {
        PriorLeafBatch *batch = &batches[i];
        Datum           vals[2];
        SPIPlanPtr      plan;
        int             rc;

        if (batch->n == 0)
            continue;
        plan = prior_leaf_plan(route, i, type16, label);
        vals[0] = PointerGetDatum(construct_array(
            batch->ids, batch->n, BYTEAOID, -1, false, 'i'));
        vals[1] = PointerGetDatum(construct_array(
            batch->subjects, batch->n, BYTEAOID, -1, false, 'i'));
        rc = SPI_execute_plan(plan, vals, NULL, false, 0);
        if (rc != SPI_OK_SELECT)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: exact-leaf prior SELECT failed: %s",
                            label, SPI_result_code_string(rc))));
        fold_prior_states_add(states, SPI_tuptable, SPI_processed,
                              batch->positions, batch->n, label);
        SPI_freetuptable(SPI_tuptable);
    }
    return states;
}

/* The fold is pure in these seven fixed-point inputs and the constant tau, so
 * each distinct transition is computed once per run. The key includes the
 * prior: matched cells with different stored states are different
 * transitions. */
typedef struct FoldMemo
{
    int64 input[7]; /* rating, rd, volatility, opponent, phi, games, score sum */
    glicko2_state_t result;
} FoldMemo;

typedef struct PeriodArrays
{
    InArray offsets; /* zero-based, one entry per cell plus terminal */
    InArray opponents;
    InArray phis;
    InArray games;
    InArray sums;
    bool exact;
} PeriodArrays;

typedef struct PeriodWindow
{
    ArrayType *starts;
    ArrayType *ends;
    ArrayType *opponents;
    ArrayType *phis;
    ArrayType *games;
    ArrayType *sums;
} PeriodWindow;

static void
read_period_arrays(FunctionCallInfo fcinfo, int cell_count,
                   const char *label, PeriodArrays *periods)
{
    bool any = false;
    bool all = true;

    memset(periods, 0, sizeof(*periods));
    for (int arg = 8; arg <= 12; ++arg)
    {
        bool present = PG_NARGS() > arg && !PG_ARGISNULL(arg);
        any = any || present;
        all = all && present;
    }
    if (!any) return;
    if (!all)
        ereport(ERROR,
                (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                 errmsg("%s: exact rating-period arrays must be supplied together",
                        label)));

    in_array(fcinfo, 8, INT8OID, 8, true, 'd', false, label,
             &periods->offsets);
    in_array(fcinfo, 9, INT8OID, 8, true, 'd', false, label,
             &periods->opponents);
    in_array(fcinfo, 10, INT8OID, 8, true, 'd', false, label,
             &periods->phis);
    in_array(fcinfo, 11, INT8OID, 8, true, 'd', false, label,
             &periods->games);
    in_array(fcinfo, 12, INT8OID, 8, true, 'd', false, label,
             &periods->sums);
    if (periods->offsets.n != cell_count + 1 ||
        periods->opponents.n != periods->phis.n ||
        periods->games.n != periods->phis.n ||
        periods->sums.n != periods->phis.n)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: invalid exact rating-period array lengths", label)));
    if (DatumGetInt64(periods->offsets.elems[0]) != 0 ||
        DatumGetInt64(periods->offsets.elems[cell_count]) != periods->games.n)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: rating-period offsets do not span grouped arrays",
                        label)));
    for (int i = 0; i < cell_count; ++i)
    {
        int64 start = DatumGetInt64(periods->offsets.elems[i]);
        int64 end = DatumGetInt64(periods->offsets.elems[i + 1]);
        if (start < 0 || end <= start || end > periods->games.n)
            ereport(ERROR,
                    (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                     errmsg("%s: cell %d has invalid rating-period range",
                            label, i)));
    }
    periods->exact = true;
}

static PeriodWindow
period_window(const PeriodArrays *periods, const InArray *cell_opponents,
              const InArray *cell_phis, const InArray *cell_games,
              const InArray *cell_sums, int cell_start, int cell_n)
{
    PeriodWindow window;
    Datum *starts = (Datum *)palloc(sizeof(Datum) * cell_n);
    Datum *ends = (Datum *)palloc(sizeof(Datum) * cell_n);
    int group_start;
    int group_n;

    if (periods->exact)
    {
        int64 first = DatumGetInt64(periods->offsets.elems[cell_start]);
        int64 terminal = DatumGetInt64(
            periods->offsets.elems[cell_start + cell_n]);
        group_start = (int)first;
        group_n = (int)(terminal - first);
        for (int i = 0; i < cell_n; ++i)
        {
            int64 begin = DatumGetInt64(
                periods->offsets.elems[cell_start + i]) - first;
            int64 end = DatumGetInt64(
                periods->offsets.elems[cell_start + i + 1]) - first;
            starts[i] = Int32GetDatum((int32)begin + 1);
            ends[i] = Int32GetDatum((int32)end);
        }
        window.opponents = array_window(
            periods->opponents.array, periods->opponents.elems, NULL,
            periods->opponents.n, group_start, group_n,
            INT8OID, 8, true, 'd');
        window.phis = array_window(periods->phis.array, periods->phis.elems, NULL,
                                   periods->phis.n, group_start, group_n,
                                   INT8OID, 8, true, 'd');
        window.games = array_window(periods->games.array, periods->games.elems, NULL,
                                    periods->games.n, group_start, group_n,
                                    INT8OID, 8, true, 'd');
        window.sums = array_window(periods->sums.array, periods->sums.elems, NULL,
                                   periods->sums.n, group_start, group_n,
                                   INT8OID, 8, true, 'd');
    }
    else
    {
        group_start = cell_start;
        group_n = cell_n;
        for (int i = 0; i < cell_n; ++i)
            starts[i] = ends[i] = Int32GetDatum(i + 1);
        window.opponents = cell_opponents->n > 0
            ? array_window(cell_opponents->array, cell_opponents->elems, NULL,
                           cell_opponents->n, group_start, group_n,
                           INT8OID, 8, true, 'd')
            : neutral_opponent_array(cell_n);
        window.phis = array_window(cell_phis->array, cell_phis->elems, NULL,
                                   cell_phis->n, group_start, group_n,
                                   INT8OID, 8, true, 'd');
        window.games = array_window(cell_games->array, cell_games->elems, NULL,
                                    cell_games->n, group_start, group_n,
                                    INT8OID, 8, true, 'd');
        window.sums = array_window(cell_sums->array, cell_sums->elems, NULL,
                                   cell_sums->n, group_start, group_n,
                                   INT8OID, 8, true, 'd');
    }
    window.starts = construct_array(starts, cell_n, INT4OID, 4, true, 'i');
    window.ends = construct_array(ends, cell_n, INT4OID, 4, true, 'i');
    return window;
}

/* Error-detail only: the /proc/self/maps line (device, inode, path, deleted
 * marker) of the library that holds `address`, which identifies the mapped
 * code a running backend actually executes. */
static char *
fold_function_mapping(uintptr_t address)
{
    FILE *maps = AllocateFile("/proc/self/maps", "r");
    char line[2048];
    bool found = false;
    bool truncated = false;
    if (maps == NULL)
        return pstrdup("unavailable");
    while (fgets(line, sizeof(line), maps) != NULL)
    {
        uintptr_t begin, end;
        if (sscanf(line, "%" SCNxPTR "-%" SCNxPTR, &begin, &end) == 2 &&
            address >= begin && address < end)
        {
            truncated = strchr(line, '\n') == NULL;
            line[strcspn(line, "\r\n")] = '\0';
            found = true;
            break;
        }
    }
    FreeFile(maps);
    if (!found) return pstrdup("not-found");
    return truncated ? psprintf("%s [truncated]", line) : pstrdup(line);
}

/* Fold one type run in one native pass: matched cells from their locked
 * prior, novel cells from the neutral prior. A cell with one period group uses
 * the uniform-period kernel; a cell with several uses the grouped-period
 * kernel. Rows flagged in `recomputed` already hold their result and pass
 * through.
 *
 * The SQL scalars use glicko2_init and the same period kernels, and
 * consensus.glicko2_neutral_mu() / consensus.glicko2_tau() equal
 * CONSENSUS_FOLD_NEUTRAL_MU / LAPLACE_GLICKO2_DEFAULT_TAU, so both paths
 * produce identical bits. */
static void
fold_run_states(const InArray *phis, const InArray *opps,
                const InArray *games, const InArray *sums,
                const PeriodArrays *periods,
                int run_start, int run_n, const FoldPriorStates *priors,
                const char *label, FoldStateArrays *out,
                const bool *recomputed)
{
    bool   *matched = priors->matched;
    Datum  *seen = (Datum *) palloc(sizeof(Datum) * run_n);
    Datum  *ratings = priors->ratings;
    Datum  *rds = priors->rds;
    Datum  *volatilities = priors->volatilities;
    int     i;
    HASHCTL memo_ctl;
    HTAB *memo;

    memset(&memo_ctl, 0, sizeof(memo_ctl));
    memo_ctl.keysize = sizeof(((FoldMemo *) 0)->input);
    memo_ctl.entrysize = sizeof(FoldMemo);
    memo = hash_create("batch consensus fold transitions", Min(run_n, 128),
                       &memo_ctl, HASH_ELEM | HASH_BLOBS);

    for (i = 0; i < run_n; i++)
    {
        /* An evidence-recomputed cell already holds the fold of its retained
         * testimony; its incoming delta is not another rating period. */
        if (recomputed != NULL && recomputed[i])
        {
            seen[i] = BoolGetDatum(matched[i]);
            continue;
        }
        glicko2_state_t st;
        int64 input[7];
        FoldMemo *entry = NULL;
        bool found = false;
        int period_start = -1;
        int period_group_n = 1;
        int64 phi = DatumGetInt64(phis->elems[run_start + i]);
        /* The opponent this witness presents; without supplied ratings (or a
         * zero rating) it is the neutral opponent. */
        int64 opp = (opps != NULL && opps->n > 0)
                    ? DatumGetInt64(opps->elems[run_start + i])
                    : CONSENSUS_FOLD_NEUTRAL_MU;
        if (opp == 0)
            opp = CONSENSUS_FOLD_NEUTRAL_MU;
        int64 n_games = DatumGetInt64(games->elems[run_start + i]);
        int64 sum = DatumGetInt64(sums->elems[run_start + i]);

        if (periods->exact)
        {
            int cell = run_start + i;
            int64 start64 = DatumGetInt64(periods->offsets.elems[cell]);
            int64 end64 = DatumGetInt64(periods->offsets.elems[cell + 1]);
            period_start = (int)start64;
            period_group_n = (int)(end64 - start64);
            if (period_group_n == 1)
            {
                opp = DatumGetInt64(periods->opponents.elems[period_start]);
                phi = DatumGetInt64(periods->phis.elems[period_start]);
                if (DatumGetInt64(periods->games.elems[period_start]) != n_games ||
                    DatumGetInt64(periods->sums.elems[period_start]) != sum)
                    ereport(ERROR,
                            (errcode(ERRCODE_DATA_EXCEPTION),
                             errmsg("%s: grouped period totals do not match cell totals",
                                    label)));
            }
        }

        if (n_games <= 0)
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                     errmsg("%s: games must be > 0 (got %ld)",
                            label, (long) n_games)));
        if (matched[i])
            glicko2_init(&st, DatumGetInt64(ratings[i]),
                         DatumGetInt64(rds[i]),
                         DatumGetInt64(volatilities[i]));
        else
            glicko2_init(&st, CONSENSUS_FOLD_NEUTRAL_MU,
                         CONSENSUS_FOLD_INITIAL_RD,
                         CONSENSUS_FOLD_INITIAL_VOLATILITY);
        if (period_group_n == 1)
        {
            input[0] = st.rating;
            input[1] = st.rd;
            input[2] = st.volatility;
            input[3] = opp;
            input[4] = phi;
            input[5] = n_games;
            input[6] = sum;
            entry = hash_search(memo, input, HASH_ENTER, &found);
            if (found)
                st = entry->result;
            else
            {
                int fold_result = consensus_fold_apply_partial(
                    &st, opp, phi, n_games, sum, LAPLACE_GLICKO2_DEFAULT_TAU);
                if (fold_result != 0)
                {
                    uintptr_t core_address = (uintptr_t)&glicko2_fold_uniform_period;
                    uintptr_t route_address = (uintptr_t)&fold_run_states;
                    char *core_mapping = fold_function_mapping(core_address);
                    char *route_mapping = fold_function_mapping(route_address);
                    ereport(ERROR,
                            (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                             errmsg("%s: native rating-period update failed", label),
                             errdetail("result=%d cell_index=%d matched=%s "
                                       "exact_period=%s period_start=%d "
                                       "prior_rating=%ld prior_rd=%ld prior_volatility=%ld "
                                       "opponent_rating=%ld opponent_rd=%ld "
                                       "games=%ld sum_score=%ld tau=%ld "
                                       "backend_pid=%d core_address=%" PRIxPTR
                                       " core_mapping=[%s] route_address=%" PRIxPTR
                                       " route_mapping=[%s]",
                                       fold_result, run_start + i,
                                       matched[i] ? "true" : "false",
                                       periods->exact ? "true" : "false", period_start,
                                       (long) input[0], (long) input[1], (long) input[2],
                                       (long) input[3], (long) input[4], (long) input[5],
                                       (long) input[6], (long) LAPLACE_GLICKO2_DEFAULT_TAU,
                                       MyProcPid, core_address, core_mapping,
                                       route_address, route_mapping)));
                }
            }
            if (!found) entry->result = st;
        }
        else
        {
            int start = period_start;
            int group_n = period_group_n;
            int64 *group_opps = (int64 *)palloc(sizeof(int64) * group_n);
            int64 *group_phis = (int64 *)palloc(sizeof(int64) * group_n);
            int64 *group_games = (int64 *)palloc(sizeof(int64) * group_n);
            int64 *group_sums = (int64 *)palloc(sizeof(int64) * group_n);
            int64 exact_games = 0;
            int64 exact_sum = 0;

            for (int g = 0; g < group_n; ++g)
            {
                group_opps[g] = DatumGetInt64(periods->opponents.elems[start + g]);
                if (group_opps[g] == 0)
                    group_opps[g] = CONSENSUS_FOLD_NEUTRAL_MU;
                group_phis[g] = DatumGetInt64(periods->phis.elems[start + g]);
                group_games[g] = DatumGetInt64(periods->games.elems[start + g]);
                group_sums[g] = DatumGetInt64(periods->sums.elems[start + g]);
                if (group_games[g] <= 0 ||
                    exact_games > INT64_MAX - group_games[g])
                    ereport(ERROR,
                            (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                             errmsg("%s: invalid grouped game count", label)));
                exact_games += group_games[g];
                if ((group_sums[g] > 0 && exact_sum > INT64_MAX - group_sums[g]) ||
                    (group_sums[g] < 0 && exact_sum < INT64_MIN - group_sums[g]))
                    ereport(ERROR,
                            (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                             errmsg("%s: grouped score sum exceeds capacity", label)));
                exact_sum += group_sums[g];
            }
            if (exact_games != n_games || exact_sum != sum)
                ereport(ERROR,
                        (errcode(ERRCODE_DATA_EXCEPTION),
                         errmsg("%s: grouped period totals do not match cell totals",
                                label)));
            int fold_rc = glicko2_fold_grouped_period(
                &st, group_opps, group_phis, group_games, group_sums,
                (size_t)group_n, LAPLACE_GLICKO2_DEFAULT_TAU, 0);
            pfree(group_opps);
            pfree(group_phis);
            pfree(group_games);
            pfree(group_sums);
            if (fold_rc != 0)
                ereport(ERROR,
                        (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                         errmsg("%s: exact rating period exceeds fixed-point capacity",
                                label)));
        }
        seen[i] = BoolGetDatum(matched[i]);
        ratings[i] = Int64GetDatum(st.rating);
        rds[i] = Int64GetDatum(st.rd);
        volatilities[i] = Int64GetDatum(st.volatility);
    }
    hash_destroy(memo);

    out->seen_array = construct_array(seen, run_n, BOOLOID, 1, true, 'c');
    out->rating_array = construct_array(ratings, run_n, INT8OID, 8, true, 'd');
    out->rd_array = construct_array(rds, run_n, INT8OID, 8, true, 'd');
    out->volatility_array = construct_array(volatilities, run_n,
                                            INT8OID, 8, true, 'd');
}

/* ------------------------------------------------------------------ */
/* attestation_merge — replay of already-identified attestations       */
/* ------------------------------------------------------------------ */

/* An attestation id already identifies the witnessed event, so receiving it
 * again adds no observation, timestamp or calibration. These entry points
 * validate the parallel arrays and write nothing (return 0); novel testimony
 * is admitted by the shared attestation writer, and only that accepted set is
 * folded into consensus. */
static Datum
attestation_replay(PG_FUNCTION_ARGS, bool single_type)
{
    const char *label = "attestation_merge";
    InArray ids, types, subjects, games, sums, ts, fold_replayable;
    int i;

    if (single_type)
    {
        if (PG_ARGISNULL(0))
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("%s: type must not be NULL", label)));
        (void) bytea16(PG_GETARG_DATUM(0), label);
    }
    else
        in_array(fcinfo, 1, BYTEAOID, -1, false, 'i', false, label, &types);
    in_array(fcinfo, single_type ? 1 : 0, BYTEAOID, -1, false, 'i', false, label, &ids);
    in_array(fcinfo, 2, BYTEAOID, -1, false, 'i', false, label, &subjects);
    in_array(fcinfo, 3, INT8OID, 8, true, 'd', false, label, &games);
    in_array(fcinfo, 4, INT8OID, 8, true, 'd', false, label, &sums);
    in_array(fcinfo, 5, TIMESTAMPTZOID, 8, true, 'd', false, label, &ts);
    in_array(fcinfo, 6, BOOLOID, 1, true, 'c', false, label, &fold_replayable);
    if ((!single_type && types.n != ids.n) || subjects.n != ids.n ||
        games.n != ids.n || sums.n != ids.n || ts.n != ids.n ||
        fold_replayable.n != ids.n)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: parallel arrays must share length", label)));
    for (i = 0; i < ids.n; ++i)
    {
        (void) bytea16(ids.elems[i], label);
        (void) bytea16(subjects.elems[i], label);
        if (!single_type)
            (void) bytea16(types.elems[i], label);
    }
    PG_RETURN_INT64(0);
}

Datum
pg_laplace_attestation_merge(PG_FUNCTION_ARGS)
{
    return attestation_replay(fcinfo, false);
}

Datum
pg_laplace_attestation_merge_type(PG_FUNCTION_ARGS)
{
    return attestation_replay(fcinfo, true);
}

/* ------------------------------------------------------------------ */
/* consensus_upsert — routed inline fold                               */
/* ------------------------------------------------------------------ */

/* Three phases per type run:
 *
 *  1. read_run_priors routes the run by HASH leaf and reads and row-locks each
 *     stored cell there. A locked row cannot change before persistence, so the
 *     fold starts from the write-time state.
 *  2. fold_run_states computes every outgoing (rating, rd, volatility).
 *  3. Matched rows persist through a keyed update (UPSERT_MATCHED_SQL, the
 *     primary-key conflict arbiter, when the native update declines); novel
 *     rows stream through PostgreSQL's COPY bulk insertion (UPSERT_NOVEL_SQL
 *     when a relation's rules or row policies require SQL INSERT). No phase-3
 *     statement joins the batch back to the partitioned target.
 *
 * A cell a concurrent writer inserts after phase 1 makes the novel insert
 * raise unique_violation; the phase-3 subtransaction rolls back and
 * UPSERT_MERGE_SQL runs instead. In it b.seen is phase 1's matched
 * classification: a MATCHED row with b.seen false is that concurrent cell, and
 * its precomputed neutral fold would be wrong, so the CASE arm folds the
 * committed state through laplace_glicko2_accumulate_period under
 * EvalPlanQual. The CASE keeps that scalar off every other row. */
static const char *UPSERT_MATCHED_SQL =
    "INSERT INTO laplace.consensus AS c "
    "  (id, subject_id, type_id, object_id, rating, rd, volatility, "
    "   witness_count, last_observed_at) "
    "SELECT b.id, b.s, '\\x%s'::bytea, b.o, b.new_rating, b.new_rd, "
    "       b.new_volatility, b.games, b.ts "
    "FROM unnest($1::bytea[], $2::bytea[], $3::bytea[], $4::int8[], "
    "             $5::timestamptz[], $6::bool[], $7::int8[], $8::int8[], "
    "             $9::int8[]) "
    "     AS b(id, s, o, games, ts, seen, new_rating, new_rd, new_volatility) "
    "WHERE b.seen "
    "ON CONFLICT (id, type_id, subject_id) DO UPDATE SET "
    "  rating = EXCLUDED.rating, "
    "  rd = EXCLUDED.rd, "
    "  volatility = EXCLUDED.volatility, "
    "  witness_count = c.witness_count + EXCLUDED.witness_count, "
    "  last_observed_at = GREATEST(c.last_observed_at, EXCLUDED.last_observed_at)";

static const char *UPSERT_NOVEL_SQL =
    "INSERT INTO laplace.consensus "
    "  (id, subject_id, type_id, object_id, rating, rd, volatility, "
    "   witness_count, last_observed_at) "
    "SELECT b.id, b.s, '\\x%s'::bytea, b.o, b.new_rating, b.new_rd, "
    "       b.new_volatility, b.games, b.ts "
    "FROM unnest($1::bytea[], $2::bytea[], $3::bytea[], $4::int8[], "
    "             $5::timestamptz[], $6::bool[], $7::int8[], $8::int8[], "
    "             $9::int8[]) "
    "     AS b(id, s, o, games, ts, seen, new_rating, new_rd, new_volatility) "
    "WHERE NOT b.seen";

static const char *UPSERT_MERGE_SQL =
    "MERGE INTO laplace.consensus c "
    "USING unnest($1::bytea[], $2::bytea[], $3::bytea[], $4::int8[], "
    "             $5::int8[], $6::int8[], $7::timestamptz[], "
    "             $8::bool[], $9::int8[], $10::int8[], $11::int8[], "
    "             $12::int8[], $13::int4[], $14::int4[]) "
    "      AS b(id, s, o, phi, games, score_sum, ts, seen, new_rating, "
    "           new_rd, new_volatility, opp_rating, group_start, group_end) "
    "ON c.type_id = '\\x%s'::bytea AND c.subject_id = b.s AND c.id = b.id "
    "WHEN MATCHED THEN UPDATE SET "
    "  rating = CASE WHEN b.seen THEN b.new_rating ELSE "
    "      (laplace.laplace_glicko2_accumulate_period("
    "           c.rating, c.rd, c.volatility, $15[b.group_start:b.group_end], "
    "           $16[b.group_start:b.group_end], $17[b.group_start:b.group_end], "
    "           $18[b.group_start:b.group_end], consensus.glicko2_tau())).rating END, "
    "  rd = CASE WHEN b.seen THEN b.new_rd ELSE "
    "      (laplace.laplace_glicko2_accumulate_period("
    "           c.rating, c.rd, c.volatility, $15[b.group_start:b.group_end], "
    "           $16[b.group_start:b.group_end], $17[b.group_start:b.group_end], "
    "           $18[b.group_start:b.group_end], consensus.glicko2_tau())).rd END, "
    "  volatility = CASE WHEN b.seen THEN b.new_volatility ELSE "
    "      (laplace.laplace_glicko2_accumulate_period("
    "           c.rating, c.rd, c.volatility, $15[b.group_start:b.group_end], "
    "           $16[b.group_start:b.group_end], $17[b.group_start:b.group_end], "
    "           $18[b.group_start:b.group_end], consensus.glicko2_tau())).volatility END, "
    "  witness_count = c.witness_count + b.games, "
    "  last_observed_at = GREATEST(c.last_observed_at, b.ts) "
    "WHEN NOT MATCHED THEN INSERT "
    "  (id, subject_id, type_id, object_id, rating, rd, volatility, "
    "   witness_count, last_observed_at) "
    "VALUES (b.id, b.s, '\\x%s'::bytea, b.o, b.new_rating, b.new_rd, "
    "        b.new_volatility, b.games, b.ts)";

/* This write queues no highway-mask work: the caller deposits highway bits for
 * the same delta through highway_mask_deposit. */

/* Duplicate-cell guard: one call may name each cell once. */
typedef struct CellSeen
{
    char id[16];
} CellSeen;

/* Execute one consensus MERGE, absorbing the concurrent-insert race: if another
 * writer commits a cell of this run after the prior read, the NOT MATCHED arm
 * collides (MERGE has no ON CONFLICT) and raises unique_violation. The
 * colliding row is committed, so re-executing under a fresh snapshot
 * reclassifies it as MATCHED with b.seen false, and the CASE arm folds its
 * committed state. A failed attempt rolls back with its subtransaction; the
 * phase-1 FOR UPDATE locks belong to the parent transaction and survive. Each
 * retry needs a fresh committed collision, so the attempts are bounded and the
 * last error is rethrown. */
#define UPSERT_MERGE_MAX_ATTEMPTS 4

static uint64
upsert_merge_with_retry(SPIPlanPtr plan, Datum *vals, const char *label)
{
    int attempt = 0;

    for (;;)
    {
        MemoryContext   oldcontext = CurrentMemoryContext;
        ResourceOwner   oldowner = CurrentResourceOwner;
        volatile uint64 processed = 0;
        volatile bool   retry = false;

        BeginInternalSubTransaction(NULL);
        MemoryContextSwitchTo(oldcontext);
        PG_TRY();
        {
            int rc = SPI_execute_plan(plan, vals, NULL, false, 0);

            if (rc != SPI_OK_MERGE)
                ereport(ERROR,
                        (errcode(ERRCODE_INTERNAL_ERROR),
                         errmsg("%s: MERGE failed: %s",
                                label, SPI_result_code_string(rc))));
            processed = SPI_processed;
            ReleaseCurrentSubTransaction();
            MemoryContextSwitchTo(oldcontext);
            CurrentResourceOwner = oldowner;
        }
        PG_CATCH();
        {
            ErrorData *edata;

            MemoryContextSwitchTo(oldcontext);
            edata = CopyErrorData();
            FlushErrorState();
            RollbackAndReleaseCurrentSubTransaction();
            MemoryContextSwitchTo(oldcontext);
            CurrentResourceOwner = oldowner;

            if (edata->sqlerrcode != ERRCODE_UNIQUE_VIOLATION ||
                ++attempt >= UPSERT_MERGE_MAX_ATTEMPTS)
                ReThrowError(edata);
            FreeErrorData(edata);
            retry = true;
        }
        PG_END_TRY();

        if (!retry)
            return processed;
    }
}

/* Persist a phase-1 classification without joining the batch back to the
 * partitioned target. Matched rows update natively on their known HASH leaves
 * when the relation's policy semantics permit, otherwise through the parent's
 * primary-key-arbitrated INSERT ... ON CONFLICT. Novel rows insert by native
 * COPY, otherwise by SQL INSERT. Both writes share a subtransaction, so a
 * unique_violation from a concurrent insert rolls back the matched updates
 * too before the MERGE reclassifies the run under a fresh snapshot. */
static uint64
upsert_persist_keyed_or_fallback(SPIPlanPtr matched_plan,
                                 SPIPlanPtr novel_plan,
                                 SPIPlanPtr merge_plan,
                                 Datum *write_vals, Datum *merge_vals,
                                 Datum type,
                                 uint64 matched_n, uint64 total_n,
                                 const char *label)
{
    MemoryContext   oldcontext = CurrentMemoryContext;
    ResourceOwner   oldowner = CurrentResourceOwner;
    volatile uint64 processed = 0;
    volatile bool   fallback = false;

    BeginInternalSubTransaction(NULL);
    MemoryContextSwitchTo(oldcontext);
    PG_TRY();
    {
        int rc;

        if (matched_n > 0)
        {
            uint64 updated = 0;

            if (!laplace_consensus_update_matched(
                    type, write_vals, (int) total_n, matched_n, &updated))
            {
                rc = SPI_execute_plan(matched_plan, write_vals, NULL, false, 0);
                if (rc != SPI_OK_INSERT)
                    ereport(ERROR,
                            (errcode(ERRCODE_INTERNAL_ERROR),
                             errmsg("%s: keyed matched upsert failed: %s",
                                    label, SPI_result_code_string(rc))));
                updated = SPI_processed;
            }
            if (updated != matched_n)
                ereport(ERROR,
                        (errcode(ERRCODE_INTERNAL_ERROR),
                         errmsg("%s: keyed matched persistence affected %lu of %lu rows",
                                label, (unsigned long) updated,
                                (unsigned long) matched_n)));
            processed += updated;
        }
        if (matched_n < total_n)
        {
            uint64 novel_n = total_n - matched_n;

            uint64 inserted;
            if (!laplace_consensus_copy_novel(type, write_vals, (int) total_n, &inserted))
            {
                rc = SPI_execute_plan(novel_plan, write_vals, NULL, false, 0);
                if (rc != SPI_OK_INSERT)
                    elog(ERROR, "%s: novel INSERT failed: %s", label, SPI_result_code_string(rc));
                inserted = SPI_processed;
            }
            if (inserted != novel_n)
                ereport(ERROR,
                        (errcode(ERRCODE_INTERNAL_ERROR),
                         errmsg("%s: keyed novel insert affected %lu of %lu rows",
                                label, (unsigned long) inserted,
                                (unsigned long) novel_n)));
            processed += inserted;
        }
        ReleaseCurrentSubTransaction();
        MemoryContextSwitchTo(oldcontext);
        CurrentResourceOwner = oldowner;
    }
    PG_CATCH();
    {
        ErrorData *edata;

        MemoryContextSwitchTo(oldcontext);
        edata = CopyErrorData();
        FlushErrorState();
        RollbackAndReleaseCurrentSubTransaction();
        MemoryContextSwitchTo(oldcontext);
        CurrentResourceOwner = oldowner;

        if (edata->sqlerrcode != ERRCODE_UNIQUE_VIOLATION)
            ReThrowError(edata);
        FreeErrorData(edata);
        fallback = true;
    }
    PG_END_TRY();

    if (!fallback)
        return processed;

    processed = upsert_merge_with_retry(merge_plan, merge_vals, label);
    if (processed != total_n)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: collision fallback affected %lu of %lu rows",
                        label, (unsigned long) processed,
                        (unsigned long) total_n)));
    return processed;
}

Datum
pg_laplace_consensus_upsert(PG_FUNCTION_ARGS)
{
    const char *label = "consensus_upsert";
    InArray     subjects, types, objects, phis, games, sums, ts;
    InArray     opps;
    PeriodArrays periods;
    Datum      *cell_ids;
    ArrayType  *cell_id_array;
    int64       affected = 0;
    HTAB       *seen;
    HASHCTL     ctl;
    int         run_start;
    int         i;

    in_array(fcinfo, 0, BYTEAOID, -1, false, 'i', false, label, &subjects);
    in_array(fcinfo, 1, BYTEAOID, -1, false, 'i', false, label, &types);
    in_array(fcinfo, 2, BYTEAOID, -1, false, 'i', true, label, &objects);
    in_array(fcinfo, 3, INT8OID, 8, true, 'd', false, label, &phis);
    in_array(fcinfo, 4, INT8OID, 8, true, 'd', false, label, &games);
    in_array(fcinfo, 5, INT8OID, 8, true, 'd', false, label, &sums);
    in_array(fcinfo, 6, TIMESTAMPTZOID, 8, true, 'd', false, label, &ts);
    memset(&opps, 0, sizeof(opps));
    if (PG_NARGS() > 7 && !PG_ARGISNULL(7))
        in_array(fcinfo, 7, INT8OID, 8, true, 'd', false, label, &opps);
    read_period_arrays(fcinfo, subjects.n, label, &periods);
    if (opps.n > 0 && opps.n != subjects.n)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: opponent-rating array must match subject length", label)));
    if (types.n != subjects.n || objects.n != subjects.n || phis.n != subjects.n ||
        games.n != subjects.n || sums.n != subjects.n || ts.n != subjects.n)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: parallel arrays must share length", label)));
    if (subjects.n == 0)
        PG_RETURN_INT64(0);

    /* Cell ids: blake3(subject || type || COALESCE(object, zeros)), the SQL
     * consensus_id byte layout through the same core hash. The same pass
     * rejects a cell named twice in one call. */
    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(CellSeen);
    seen = hash_create("consensus_upsert cell guard", subjects.n, &ctl,
                       HASH_ELEM | HASH_BLOBS);
    cell_ids = (Datum *) palloc(sizeof(Datum) * subjects.n);
    for (i = 0; i < subjects.n; i++)
    {
        uint8_t    buf[48];
        hash128_t  h;
        bytea     *out;
        bool       found;

        memcpy(buf, bytea16(subjects.elems[i], label), 16);
        memcpy(buf + 16, bytea16(types.elems[i], label), 16);
        if (objects.nulls[i])
            memset(buf + 32, 0, 16);
        else
            memcpy(buf + 32, bytea16(objects.elems[i], label), 16);
        hash128_blake3(buf, sizeof(buf), &h);

        hash_search(seen, &h, HASH_ENTER, &found);
        if (found)
            ereport(ERROR,
                    (errcode(ERRCODE_CARDINALITY_VIOLATION),
                     errmsg("consensus_upsert: duplicate cell in one call "
                            "(client-dedup contract violated)")));

        out = (bytea *) palloc(VARHDRSZ + 16);
        SET_VARSIZE(out, VARHDRSZ + 16);
        memcpy(VARDATA(out), &h, 16);
        cell_ids[i] = PointerGetDatum(out);
    }
    hash_destroy(seen);
    cell_id_array = construct_array(cell_ids, subjects.n,
                                    BYTEAOID, -1, false, 'i');

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: SPI_connect failed", label)));

    run_start = 0;
    while (run_start < subjects.n)
    {
        const uint8_t *type16 = bytea16(types.elems[run_start], label);
        int            run_n = 0;
        int            j = run_start;
        SPIPlanPtr     matched_plan;
        SPIPlanPtr     novel_plan;
        SPIPlanPtr     merge_plan;
        ArrayType     *run_ids;
        ArrayType     *run_subjects;
        FoldPriorStates *priors;
        FoldStateArrays folds;
        PeriodWindow    period_run;
        Datum          write_vals[9];
        Datum          vals[18];
        uint64         matched_n;
        static const Oid write_args[9] =
            {BYTEAARRAYOID, BYTEAARRAYOID, BYTEAARRAYOID, INT8ARRAYOID,
             1185, BOOLARRAYOID, INT8ARRAYOID, INT8ARRAYOID, INT8ARRAYOID};
        static const Oid args[18] =
            {BYTEAARRAYOID, BYTEAARRAYOID, BYTEAARRAYOID, INT8ARRAYOID,
             INT8ARRAYOID, INT8ARRAYOID, 1185,
             BOOLARRAYOID, INT8ARRAYOID, INT8ARRAYOID, INT8ARRAYOID,
             INT8ARRAYOID, INT4ARRAYOID, INT4ARRAYOID, INT8ARRAYOID,
             INT8ARRAYOID, INT8ARRAYOID, INT8ARRAYOID};

        while (j < subjects.n &&
               memcmp(bytea16(types.elems[j], label), type16, 16) == 0)
        {
            run_n++;
            j++;
        }

        run_ids = array_window(cell_id_array, cell_ids, NULL,
                               subjects.n, run_start, run_n,
                               BYTEAOID, -1, false, 'i');
        run_subjects = array_window(subjects.array, subjects.elems, NULL,
                                    subjects.n, run_start, run_n,
                                    BYTEAOID, -1, false, 'i');

        priors = read_run_priors(type16, types.elems[run_start], cell_ids,
                                 &subjects, run_start, run_n, label);
        matched_n = priors->matched_n;
        fold_run_states(&phis, &opps, &games, &sums, &periods, run_start, run_n,
                        priors, label, &folds, NULL);

        matched_plan = typed_plan(&upsert_matched_plans,
                                  "consensus_upsert matched plans", type16,
                                  UPSERT_MATCHED_SQL, 9, write_args);
        novel_plan = typed_plan(&upsert_novel_plans,
                                "consensus_upsert novel plans", type16,
                                UPSERT_NOVEL_SQL, 9, write_args);
        merge_plan = typed_plan(&upsert_merge_plans, "consensus_upsert merge plans",
                                type16, UPSERT_MERGE_SQL, 18, args);
        vals[0] = PointerGetDatum(run_ids);
        vals[1] = PointerGetDatum(run_subjects);
        vals[2] = PointerGetDatum(array_window(objects.array, objects.elems,
                                              objects.nulls, objects.n,
                                              run_start, run_n,
                                              BYTEAOID, -1, false, 'i'));
        vals[3] = PointerGetDatum(array_window(phis.array, phis.elems, NULL,
                                              phis.n, run_start, run_n,
                                              INT8OID, 8, true, 'd'));
        vals[4] = PointerGetDatum(array_window(games.array, games.elems, NULL,
                                              games.n, run_start, run_n,
                                              INT8OID, 8, true, 'd'));
        vals[5] = PointerGetDatum(array_window(sums.array, sums.elems, NULL,
                                              sums.n, run_start, run_n,
                                              INT8OID, 8, true, 'd'));
        vals[6] = PointerGetDatum(array_window(ts.array, ts.elems, NULL,
                                              ts.n, run_start, run_n,
                                              TIMESTAMPTZOID, 8, true, 'd'));
        vals[7] = PointerGetDatum(folds.seen_array);
        vals[8] = PointerGetDatum(folds.rating_array);
        vals[9] = PointerGetDatum(folds.rd_array);
        vals[10] = PointerGetDatum(folds.volatility_array);
        vals[11] = PointerGetDatum(
            opps.n > 0
            ? array_window(opps.array, opps.elems, NULL, opps.n,
                           run_start, run_n, INT8OID, 8, true, 'd')
            : neutral_opponent_array(run_n));
        period_run = period_window(&periods, &opps, &phis, &games, &sums,
                                   run_start, run_n);
        vals[12] = PointerGetDatum(period_run.starts);
        vals[13] = PointerGetDatum(period_run.ends);
        vals[14] = PointerGetDatum(period_run.opponents);
        vals[15] = PointerGetDatum(period_run.phis);
        vals[16] = PointerGetDatum(period_run.games);
        vals[17] = PointerGetDatum(period_run.sums);
        write_vals[0] = vals[0];
        write_vals[1] = vals[1];
        write_vals[2] = vals[2];
        write_vals[3] = vals[4];
        write_vals[4] = vals[6];
        write_vals[5] = vals[7];
        write_vals[6] = vals[8];
        write_vals[7] = vals[9];
        write_vals[8] = vals[10];
        affected += (int64) upsert_persist_keyed_or_fallback(
            matched_plan, novel_plan, merge_plan, write_vals, vals,
            types.elems[run_start],
            matched_n, (uint64) run_n, label);

        run_start = j;
    }

    SPI_finish();
    PG_RETURN_INT64(affected);
}

static Datum *
typed_cell_ids(const uint8_t *type16,const InArray *subjects,
               const InArray *objects,const char *label)
{
    HTAB *seen;
    HASHCTL ctl;
    Datum *cell_ids;
    int i;
    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(CellSeen);
    seen = hash_create("consensus_upsert_type cell guard", subjects->n, &ctl,
                       HASH_ELEM | HASH_BLOBS);
    cell_ids = (Datum *) palloc(sizeof(Datum) * subjects->n);
    for (i = 0; i < subjects->n; i++)
    {
        uint8_t   buf[48];
        hash128_t h;
        bytea    *out;
        bool      found;

        memcpy(buf, bytea16(subjects->elems[i], label), 16);
        memcpy(buf + 16, type16, 16);
        if (objects->nulls[i])
            memset(buf + 32, 0, 16);
        else
            memcpy(buf + 32, bytea16(objects->elems[i], label), 16);
        hash128_blake3(buf, sizeof(buf), &h);
        hash_search(seen, &h, HASH_ENTER, &found);
        if (found)
            ereport(ERROR,
                    (errcode(ERRCODE_CARDINALITY_VIOLATION),
                     errmsg("%s: duplicate cell in one call "
                            "(client-dedup contract violated)", label)));
        out = (bytea *) palloc(VARHDRSZ + 16);
        SET_VARSIZE(out, VARHDRSZ + 16);
        memcpy(VARDATA(out), &h, 16);
        cell_ids[i] = PointerGetDatum(out);
    }
    hash_destroy(seen);
    return cell_ids;
}

/* Evidence-backed writes lock their complete target cell set before reading
 * testimony. Missing cells are inserted at the neutral state; ON CONFLICT ...
 * WHERE false takes the existing row's lock without rewriting it. Sorted input
 * gives concurrent writers one lock order. Inserted neutral rows stay
 * transaction-local until every result is written. */
static const char *EVIDENCE_LOCK_SQL =
    "INSERT INTO laplace.consensus AS c "
    " (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at) "
    "SELECT b.id,b.s,'\\x%s'::bytea,b.o,consensus.glicko2_neutral_mu(),"
    " consensus.glicko2_initial_rd(),consensus.glicko2_initial_volatility(),0,b.ts "
    "FROM unnest($1::bytea[],$2::bytea[],$3::bytea[],$4::timestamptz[]) AS b(id,s,o,ts) "
    "ORDER BY b.id,b.s "
    "ON CONFLICT (id,type_id,subject_id) DO UPDATE SET rating=c.rating WHERE false";

/* Result write for evidence-backed cells. Recomputed cells take their folded
 * state and retained witness count/time outright; the others add their delta
 * to the stored witness count and keep the later observation time. */
static const char *EVIDENCE_WRITE_SQL =
    "WITH input AS MATERIALIZED ("
    " SELECT * FROM unnest($1::bytea[],$2::bytea[],$3::int8[],$4::timestamptz[],"
    " $5::bool[],$6::int8[],$7::int8[],$8::int8[],$9::bytea[]) "
    " AS b(id,s,games,ts,recomputed,rating,rd,volatility,o)), "
    "replayed AS ("
    " INSERT INTO laplace.consensus AS c "
    " (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at) "
    " SELECT id,s,'\\x%s'::bytea,o,rating,rd,volatility,games,ts "
    " FROM input WHERE recomputed ORDER BY id,s "
    " ON CONFLICT (id,type_id,subject_id) DO UPDATE SET "
    " rating=EXCLUDED.rating,rd=EXCLUDED.rd,volatility=EXCLUDED.volatility,"
    " witness_count=EXCLUDED.witness_count,last_observed_at=EXCLUDED.last_observed_at "
    " RETURNING 1), "
    "incremental AS ("
    " INSERT INTO laplace.consensus AS c "
    " (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at) "
    " SELECT id,s,'\\x%s'::bytea,o,rating,rd,volatility,games,ts "
    " FROM input WHERE NOT recomputed ORDER BY id,s "
    " ON CONFLICT (id,type_id,subject_id) DO UPDATE SET "
    " rating=EXCLUDED.rating,rd=EXCLUDED.rd,volatility=EXCLUDED.volatility,"
    " witness_count=c.witness_count+EXCLUDED.witness_count,"
    " last_observed_at=GREATEST(c.last_observed_at,EXCLUDED.last_observed_at) "
    " RETURNING 1) "
    "SELECT (SELECT count(*) FROM replayed)+(SELECT count(*) FROM incremental)";

typedef struct FoldEvidenceStates
{
    bool *recomputed;
    Datum *counts;
    Datum *timestamps;
} FoldEvidenceStates;

static void
lock_evidence_targets(const uint8_t *type16, ArrayType *ids,
                      const InArray *subjects, const InArray *objects,
                      const InArray *ts, const char *label)
{
    static const Oid args[4] =
        {BYTEAARRAYOID,BYTEAARRAYOID,BYTEAARRAYOID,TIMESTAMPTZARRAYOID};
    Datum vals[4] = {PointerGetDatum(ids),PointerGetDatum(subjects->array),
                     PointerGetDatum(objects->array),(Datum)0};
    if (ts != NULL)
        vals[3]=PointerGetDatum(ts->array);
    else
    {
        /* A refold has no incoming observation time. This transaction-local
         * placeholder is overwritten by the actual retained maximum before
         * commit; it never becomes an evidence timestamp. */
        Datum *empty_times=palloc0(sizeof(Datum)*subjects->n);
        vals[3]=PointerGetDatum(construct_array(
            empty_times,subjects->n,TIMESTAMPTZOID,8,true,'d'));
        pfree(empty_times);
    }
    SPIPlanPtr plan = typed_plan(&evidence_lock_plans,
        "consensus evidence target locks",type16,EVIDENCE_LOCK_SQL,4,args);
    int rc = SPI_execute_plan(plan,vals,NULL,false,0);
    if (rc != SPI_OK_INSERT)
        ereport(ERROR,(errcode(ERRCODE_INTERNAL_ERROR),
                       errmsg("%s: evidence target locking failed: %s",
                              label,SPI_result_code_string(rc))));
}

/* Read the retained testimony of every target cell (rows grouped by cell
 * ordinal) and refold each replayable cell from the neutral prior over all of
 * it with the grouped-period kernel, recording its witness count and latest
 * observation time. A cell without testimony is an error. */
static FoldEvidenceStates *
read_evidence_states(const uint8_t *type16, const InArray *subjects,
                     const InArray *objects, const InArray *games,
                     const InArray *ts, FoldPriorStates *priors,
                     const char *label, bool replayable_only, bool require_all)
{
    static const Oid args[2] = {BYTEAARRAYOID,BYTEAARRAYOID};
    Datum vals[2] = {PointerGetDatum(subjects->array),PointerGetDatum(objects->array)};
    SPIPlanPtr plan = typed_plan(&evidence_fold_plans,
        "consensus exact durable evidence",type16,laplace_sql_query_text("consensus.evidence_rows_typed"),2,args);
    FoldEvidenceStates *out = palloc(sizeof(*out));
    bool *visited = palloc0(sizeof(bool)*subjects->n);
    out->recomputed = palloc0(sizeof(bool)*subjects->n);
    out->counts = palloc(sizeof(Datum)*subjects->n);
    out->timestamps = palloc(sizeof(Datum)*subjects->n);
    if (games != NULL) memcpy(out->counts,games->elems,sizeof(Datum)*subjects->n);
    if (ts != NULL) memcpy(out->timestamps,ts->elems,sizeof(Datum)*subjects->n);

    /* Non-readonly SPI takes a fresh READ COMMITTED snapshot after the target
     * locks, so a writer that waited on another sees its committed testimony. */
    int rc = SPI_execute_plan(plan,vals,NULL,false,0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "%s: durable evidence read failed", label);
    uint64 rows = SPI_processed;
    SPITupleTable *tuples = SPI_tuptable;
    int64_t *opponents = NULL, *phis = NULL, *counts = NULL, *sums = NULL;
    Size capacity = 0;
    uint64 row = 0;
    while (row < rows)
    {
        CHECK_FOR_INTERRUPTS();
        bool isnull;
        int64 ord = DatumGetInt64(SPI_getbinval(tuples->vals[row], tuples->tupdesc, 1, &isnull));
        if (isnull || ord < 1 || ord > subjects->n || visited[ord - 1])
            elog(ERROR, "%s: invalid durable evidence target ordinal", label);
        int i = (int) ord - 1;
        visited[i] = true;
        Size groups = 0;
        int64 witnesses = 0;
        Datum latest = (Datum) 0;
        bool replayable = true;
        do
        {
            Datum values[8];
            bool nulls[8];
            for (int c = 0; c < 8; ++c)
                values[c] = SPI_getbinval(tuples->vals[row], tuples->tupdesc, c + 1, &nulls[c]);
            for (int c = 0; c < 8; ++c)
                if (nulls[c]) elog(ERROR, "%s: incomplete durable evidence row", label);
            replayable &= DatumGetBool(values[7]);
            if (groups == capacity)
            {
                Size next = capacity == 0 ? 8 : capacity * 2;
                if (next > MaxAllocSize / sizeof(int64_t))
                    ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                        errmsg("%s: evidence cell exceeds allocation capacity", label)));
                opponents = opponents ? repalloc(opponents, next * sizeof(int64_t)) : palloc(next * sizeof(int64_t));
                phis = phis ? repalloc(phis, next * sizeof(int64_t)) : palloc(next * sizeof(int64_t));
                counts = counts ? repalloc(counts, next * sizeof(int64_t)) : palloc(next * sizeof(int64_t));
                sums = sums ? repalloc(sums, next * sizeof(int64_t)) : palloc(next * sizeof(int64_t));
                capacity = next;
            }
            opponents[groups] = DatumGetInt64(values[5]);
            if (opponents[groups] == 0) opponents[groups] = CONSENSUS_FOLD_NEUTRAL_MU;
            phis[groups] = DatumGetInt64(values[6]);
            counts[groups] = Max(DatumGetInt64(values[3]), INT64CONST(1));
            sums[groups] = DatumGetInt64(values[4]);
            ++groups;
            latest = values[2];
            ++row;
            if (row == rows) break;
            int64 next = DatumGetInt64(SPI_getbinval(tuples->vals[row], tuples->tupdesc, 1, &isnull));
            if (isnull || next != ord) break;
        } while (true);
        if (!replayable)
        {
            if (replayable_only)
                ereport(ERROR, (errcode(ERRCODE_DATA_EXCEPTION),
                    errmsg("%s: non-replayable testimony has no retained continuous score", label),
                    errdetail("cell_index=%d", i)));
            continue;
        }
        for (Size g = 0; g < groups; ++g)
        {
            if (witnesses > PG_INT64_MAX - counts[g])
                ereport(ERROR, (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                    errmsg("%s: witness count exceeds bigint capacity", label)));
            witnesses += counts[g];
        }
        glicko2_state_t state;
        glicko2_init(&state, CONSENSUS_FOLD_NEUTRAL_MU,
            CONSENSUS_FOLD_INITIAL_RD, CONSENSUS_FOLD_INITIAL_VOLATILITY);
        if (glicko2_fold_grouped_period(&state, opponents, phis, counts, sums,
                groups, LAPLACE_GLICKO2_DEFAULT_TAU, 0) != 0)
            ereport(ERROR, (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                errmsg("%s: grouped rating-period update failed", label)));
        out->recomputed[i] = true;
        priors->ratings[i] = Int64GetDatum(state.rating);
        priors->rds[i] = Int64GetDatum(state.rd);
        priors->volatilities[i] = Int64GetDatum(state.volatility);
        out->counts[i] = Int64GetDatum(witnesses);
        out->timestamps[i] = latest;
    }
    for (int i = 0; require_all && i < subjects->n; ++i)
        if (!visited[i])
            ereport(ERROR, (errcode(ERRCODE_DATA_EXCEPTION),
                errmsg("%s: target has no durable accepted testimony", label),
                errdetail("cell_index=%d", i)));
    if (opponents) pfree(opponents);
    if (phis) pfree(phis);
    if (counts) pfree(counts);
    if (sums) pfree(sums);
    SPI_freetuptable(SPI_tuptable);
    pfree(visited);
    return out;
}

static int64
write_evidence_states(const uint8_t *type16, ArrayType *ids,
                      const InArray *subjects, const InArray *objects,
                      const FoldEvidenceStates *evidence,
                      const FoldStateArrays *folds, const char *label)
{
    static const Oid args[9] =
        {BYTEAARRAYOID,BYTEAARRAYOID,INT8ARRAYOID,TIMESTAMPTZARRAYOID,
         BOOLARRAYOID,INT8ARRAYOID,INT8ARRAYOID,INT8ARRAYOID,BYTEAARRAYOID};
    Datum *flags=palloc(sizeof(Datum)*subjects->n);
    for (int i=0;i<subjects->n;i++) flags[i]=BoolGetDatum(evidence->recomputed[i]);
    Datum vals[9] = {
        PointerGetDatum(ids),PointerGetDatum(subjects->array),
        PointerGetDatum(construct_array(evidence->counts,subjects->n,INT8OID,8,true,'d')),
        PointerGetDatum(construct_array(evidence->timestamps,subjects->n,TIMESTAMPTZOID,8,true,'d')),
        PointerGetDatum(construct_array(flags,subjects->n,BOOLOID,1,true,'c')),
        PointerGetDatum(folds->rating_array),PointerGetDatum(folds->rd_array),
        PointerGetDatum(folds->volatility_array),PointerGetDatum(objects->array)};
    SPIPlanPtr plan=typed_plan(&evidence_write_plans,
        "consensus evidence result writes",type16,EVIDENCE_WRITE_SQL,9,args);
    int rc=SPI_execute_plan(plan,vals,NULL,false,0);
    bool isnull = true;
    int64 affected = 0;
    if (rc == SPI_OK_SELECT && SPI_processed == 1)
        affected = DatumGetInt64(SPI_getbinval(
            SPI_tuptable->vals[0],SPI_tuptable->tupdesc,1,&isnull));
    if (isnull || affected != subjects->n)
        ereport(ERROR,(errcode(ERRCODE_INTERNAL_ERROR),
                       errmsg("%s: locked evidence targets changed before result write",label)));
    pfree(flags);
    SPI_freetuptable(SPI_tuptable);
    return affected;
}

/* One set write per run: existing cells by keyed UPDATE per HASH leaf, novel
 * cells by native COPY. */
static void
write_run(const uint8_t *type16, Datum type, ArrayType *ids,
               const InArray *subjects, const InArray *objects,
               const FoldEvidenceStates *evidence, const FoldStateArrays *folds,
               const FoldPriorStates *priors, int64 *affected, const char *label)
{
    int n = subjects->n;
    uint64 matched_n = priors->matched_n;
    ArrayType *counts = construct_array(evidence->counts, n, INT8OID, 8, true, 'd');
    ArrayType *times = construct_array(evidence->timestamps, n, TIMESTAMPTZOID, 8, true, 'd');
    uint64 done = 0;
    {
        if (matched_n > 0)
        {
            PriorRouteEntry *route = prior_route(type16, type, label);
            Datum *all_ids, *rat, *rds, *vol;
            bool *nul;
            int cnt;
            deconstruct_array(ids, BYTEAOID, -1, false, 'i', &all_ids, &nul, &cnt);
            deconstruct_array(folds->rating_array, INT8OID, 8, true, 'd', &rat, &nul, &cnt);
            deconstruct_array(folds->rd_array, INT8OID, 8, true, 'd', &rds, &nul, &cnt);
            deconstruct_array(folds->volatility_array, INT8OID, 8, true, 'd', &vol, &nul, &cnt);
            for (int leaf = 0; leaf < route->leaves->count; ++leaf)
            {
                int m = 0;
                for (int i = 0; i < n; ++i)
                    if (priors->matched[i] && priors->leaves[i] == leaf) ++m;
                if (m == 0) continue;
                Datum *mid = palloc(sizeof(Datum) * m), *ms = palloc(sizeof(Datum) * m);
                Datum *mg = palloc(sizeof(Datum) * m), *mt = palloc(sizeof(Datum) * m);
                Datum *mr = palloc(sizeof(Datum) * m), *md = palloc(sizeof(Datum) * m);
                Datum *mv = palloc(sizeof(Datum) * m), *mc = palloc(sizeof(Datum) * m);
                int k = 0;
                for (int i = 0; i < n; ++i)
                    if (priors->matched[i] && priors->leaves[i] == leaf)
                    {
                        mid[k] = all_ids[i]; ms[k] = subjects->elems[i];
                        mg[k] = evidence->counts[i]; mt[k] = evidence->timestamps[i];
                        mr[k] = rat[i]; md[k] = rds[i]; mv[k] = vol[i];
                        mc[k] = BoolGetDatum(evidence->recomputed[i]);
                        ++k;
                    }
                Datum vals[8] = {
                    PointerGetDatum(construct_array(mid, m, BYTEAOID, -1, false, 'i')),
                    PointerGetDatum(construct_array(ms, m, BYTEAOID, -1, false, 'i')),
                    PointerGetDatum(construct_array(mg, m, INT8OID, 8, true, 'd')),
                    PointerGetDatum(construct_array(mt, m, TIMESTAMPTZOID, 8, true, 'd')),
                    PointerGetDatum(construct_array(mr, m, INT8OID, 8, true, 'd')),
                    PointerGetDatum(construct_array(md, m, INT8OID, 8, true, 'd')),
                    PointerGetDatum(construct_array(mv, m, INT8OID, 8, true, 'd')),
                    PointerGetDatum(construct_array(mc, m, BOOLOID, 1, true, 'c'))};
                int rc = SPI_execute_plan(update_leaf_plan(route, leaf, label), vals, NULL, false, 0);
                if (rc != SPI_OK_UPDATE || SPI_processed != (uint64) m)
                    ereport(ERROR, (errcode(ERRCODE_INTERNAL_ERROR),
                        errmsg("%s: exact-leaf update affected %lu of %d rows",
                               label, (unsigned long) SPI_processed, m)));
                done += SPI_processed;
            }
        }
        if (matched_n < (uint64) n)
        {
            /* write_vals layout shared with consensus_upsert's native COPY. */
            Datum write_vals[9] = {
                PointerGetDatum(ids), PointerGetDatum(subjects->array),
                PointerGetDatum(objects->array), PointerGetDatum(counts),
                PointerGetDatum(times), PointerGetDatum(folds->seen_array),
                PointerGetDatum(folds->rating_array), PointerGetDatum(folds->rd_array),
                PointerGetDatum(folds->volatility_array)};
            uint64 inserted = 0;
            if (!laplace_consensus_copy_novel(type, write_vals, n, &inserted))
                ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                    errmsg("%s: consensus relation refuses native bulk insert", label)));
            if (inserted != (uint64) n - matched_n)
                ereport(ERROR, (errcode(ERRCODE_INTERNAL_ERROR),
                    errmsg("%s: novel COPY affected %lu of %lu rows", label,
                           (unsigned long) inserted, (unsigned long) (n - matched_n))));
            done += inserted;
        }
    }
    *affected += (int64) done;
}

/* Validate the incoming delta (positive games, 0 <= score sum <= games * 1e9,
 * non-negative phi, period groups summing to the cell totals) even where
 * retained testimony supplies the resulting state, so a malformed delta is
 * never hidden behind a populated cell. */
static void
validate_evidence_delta(const InArray *phis, const InArray *games,
                         const InArray *sums, const PeriodArrays *periods,
                         const char *label)
{
    for (int i=0;i<games->n;i++)
    {
        int64 n=DatumGetInt64(games->elems[i]);
        int64 sum=DatumGetInt64(sums->elems[i]);
        if (n<=0 || sum<0 || (__int128)sum>(__int128)n*1000000000LL ||
            DatumGetInt64(phis->elems[i])<0)
            ereport(ERROR,(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                           errmsg("%s: invalid incoming evidence delta",label),
                           errdetail("cell_index=%d",i)));
        if (periods->exact)
        {
            int64 begin=DatumGetInt64(periods->offsets.elems[i]);
            int64 end=DatumGetInt64(periods->offsets.elems[i+1]);
            __int128 total_games=0,total_sum=0;
            for (int64 j=begin;j<end;j++)
            {
                int64 group_games=DatumGetInt64(periods->games.elems[j]);
                int64 group_sum=DatumGetInt64(periods->sums.elems[j]);
                if (group_games<=0 || group_sum<0 ||
                    (__int128)group_sum>(__int128)group_games*1000000000LL ||
                    DatumGetInt64(periods->phis.elems[j])<0)
                    ereport(ERROR,(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                                   errmsg("%s: invalid incoming evidence period group",label)));
                total_games+=group_games;
                total_sum+=group_sum;
            }
            if (total_games!=n || total_sum!=sum)
                ereport(ERROR,(errcode(ERRCODE_DATA_EXCEPTION),
                               errmsg("%s: grouped period totals do not match cell totals",label)));
        }
    }
}

/* Fold a complete mixed-type working set of evidence deltas in one call. Type
 * runs must be bytewise sorted; each run is folded onto its locked priors and
 * written with write_run. */
Datum
pg_laplace_consensus_merge_evidence(PG_FUNCTION_ARGS)
{
    const char *label = "consensus_merge_evidence";
    InArray subjects, types, objects, phis, games, sums, ts, opps;
    PeriodArrays periods;
    int64 affected = 0;
    int run_start = 0;

    in_array(fcinfo, 0, BYTEAOID, -1, false, 'i', false, label, &subjects);
    in_array(fcinfo, 1, BYTEAOID, -1, false, 'i', false, label, &types);
    in_array(fcinfo, 2, BYTEAOID, -1, false, 'i', true, label, &objects);
    in_array(fcinfo, 3, INT8OID, 8, true, 'd', false, label, &phis);
    in_array(fcinfo, 4, INT8OID, 8, true, 'd', false, label, &games);
    in_array(fcinfo, 5, INT8OID, 8, true, 'd', false, label, &sums);
    in_array(fcinfo, 6, TIMESTAMPTZOID, 8, true, 'd', false, label, &ts);
    memset(&opps, 0, sizeof(opps));
    if (PG_NARGS() > 7 && !PG_ARGISNULL(7))
        in_array(fcinfo, 7, INT8OID, 8, true, 'd', false, label, &opps);
    read_period_arrays(fcinfo, subjects.n, label, &periods);

    if (types.n != subjects.n || objects.n != subjects.n ||
        phis.n != subjects.n || games.n != subjects.n ||
        sums.n != subjects.n || ts.n != subjects.n ||
        (opps.n > 0 && opps.n != subjects.n))
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: parallel arrays must share length", label)));
    if (subjects.n == 0)
        PG_RETURN_INT64(0);

    validate_evidence_delta(&phis, &games, &sums, &periods, label);
    if (IsolationUsesXactSnapshot())
        ereport(ERROR,
                (errcode(ERRCODE_T_R_SERIALIZATION_FAILURE),
                 errmsg("%s: retry the evidence transaction at READ COMMITTED", label)));
    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: SPI_connect failed", label)));

    while (run_start < subjects.n)
    {
        const uint8_t *type16 = bytea16(types.elems[run_start], label);
        int run_end = run_start + 1;
        InArray run_subjects, run_objects, run_games, run_ts;
        Datum *cell_ids;
        ArrayType *ids;
        FoldPriorStates *priors;
        FoldEvidenceStates *evidence;
        FoldStateArrays folds;

        while (run_end < subjects.n &&
               memcmp(bytea16(types.elems[run_end], label), type16, 16) == 0)
            ++run_end;
        if (run_end < subjects.n &&
            memcmp(type16, bytea16(types.elems[run_end], label), 16) > 0)
            ereport(ERROR,
                    (errcode(ERRCODE_DATA_EXCEPTION),
                     errmsg("%s: type ids must be bytewise sorted", label)));

        run_subjects = in_array_window(&subjects, run_start,
                                       run_end - run_start,
                                       BYTEAOID, -1, false, 'i');
        run_objects = in_array_window(&objects, run_start,
                                      run_end - run_start,
                                      BYTEAOID, -1, false, 'i');
        run_games = in_array_window(&games, run_start,
                                    run_end - run_start,
                                    INT8OID, 8, true, 'd');
        run_ts = in_array_window(&ts, run_start,
                                 run_end - run_start,
                                 TIMESTAMPTZOID, 8, true, 'd');

        cell_ids = typed_cell_ids(type16, &run_subjects, &run_objects, label);
        ids = construct_array(cell_ids, run_subjects.n,
                              BYTEAOID, -1, false, 'i');

        /* Writers of one relation type serialize here for the rest of their
         * transaction. Runs arrive in ascending type order, so the locks are
         * taken in one global order, and the prior read below sees every
         * committed cell of this type: a cell read as novel is novel. */
        {
            uint64 key;
            memcpy(&key, type16, sizeof(key));
            DirectFunctionCall1(pg_advisory_xact_lock_int8, Int64GetDatum((int64) key));
        }

        /* A cell's standing is one rating period over all of its durable
         * evidence from the neutral prior (laplace.consensus_fold). This
         * transaction's testimony is already durable here, so every cell with
         * retained evidence is recomputed from it; a cell without retained
         * evidence (consensus-only admission) folds the delta onto its prior. */
        priors = read_run_priors(type16, types.elems[run_start], cell_ids,
                                 &run_subjects, 0, run_subjects.n, label);
        evidence = read_evidence_states(type16, &run_subjects, &run_objects,
                                        &run_games, &run_ts, priors, label,
                                        false, false);
        fold_run_states(&phis, &opps, &games, &sums, &periods,
                        run_start, run_subjects.n, priors, label,
                        &folds, evidence->recomputed);
        write_run(type16, types.elems[run_start], ids, &run_subjects,
                  &run_objects, evidence, &folds, priors, &affected, label);
        run_start = run_end;
        CHECK_FOR_INTERRUPTS();
    }

    SPI_finish();
    PG_RETURN_INT64(affected);
}

/* Single-type run. Caller arrays pass straight through to the cached plans.
 * With from_evidence, target cells are locked first and each replayable cell is
 * refolded from all of its retained testimony; otherwise the delta folds onto
 * the locked prior. */
static Datum
consensus_upsert_type(FunctionCallInfo fcinfo, bool from_evidence)
{
    const char    *label = from_evidence
        ? "consensus_upsert_evidence_type" : "consensus_upsert_type";
    const uint8_t *type16;
    Datum          type_datum;
    InArray        subjects, objects, phis, games, sums, ts;
    InArray        opps;
    PeriodArrays   periods;
    Datum         *cell_ids;
    ArrayType     *cell_id_array;
    FoldPriorStates *priors;
    FoldStateArrays folds;
    PeriodWindow    period_run;
    SPIPlanPtr     matched_plan;
    SPIPlanPtr     novel_plan;
    SPIPlanPtr     merge_plan;
    Datum          write_vals[9];
    Datum          vals[18];
    uint64         matched_n;
    static const Oid write_args[9] =
        {BYTEAARRAYOID, BYTEAARRAYOID, BYTEAARRAYOID, INT8ARRAYOID,
         1185, BOOLARRAYOID, INT8ARRAYOID, INT8ARRAYOID, INT8ARRAYOID};
    static const Oid args[18] =
        {BYTEAARRAYOID, BYTEAARRAYOID, BYTEAARRAYOID, INT8ARRAYOID,
         INT8ARRAYOID, INT8ARRAYOID, 1185,
         BOOLARRAYOID, INT8ARRAYOID, INT8ARRAYOID, INT8ARRAYOID,
         INT8ARRAYOID, INT4ARRAYOID, INT4ARRAYOID, INT8ARRAYOID,
         INT8ARRAYOID, INT8ARRAYOID, INT8ARRAYOID};
    if (PG_ARGISNULL(0))
        ereport(ERROR,
                (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                 errmsg("%s: type must not be NULL", label)));
    type_datum = PG_GETARG_DATUM(0);
    type16 = bytea16(type_datum, label);
    in_array(fcinfo, 1, BYTEAOID, -1, false, 'i', false, label, &subjects);
    in_array(fcinfo, 2, BYTEAOID, -1, false, 'i', true, label, &objects);
    in_array(fcinfo, 3, INT8OID, 8, true, 'd', false, label, &phis);
    in_array(fcinfo, 4, INT8OID, 8, true, 'd', false, label, &games);
    in_array(fcinfo, 5, INT8OID, 8, true, 'd', false, label, &sums);
    in_array(fcinfo, 6, TIMESTAMPTZOID, 8, true, 'd', false, label, &ts);
    memset(&opps, 0, sizeof(opps));
    if (PG_NARGS() > 7 && !PG_ARGISNULL(7))
        in_array(fcinfo, 7, INT8OID, 8, true, 'd', false, label, &opps);
    read_period_arrays(fcinfo, subjects.n, label, &periods);
    if (opps.n > 0 && opps.n != subjects.n)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: opponent-rating array must match subject length", label)));
    if (objects.n != subjects.n || phis.n != subjects.n || games.n != subjects.n ||
        sums.n != subjects.n || ts.n != subjects.n)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: parallel arrays must share length", label)));
    if (subjects.n == 0)
        PG_RETURN_INT64(0);
    if (from_evidence)
        validate_evidence_delta(&phis,&games,&sums,&periods,label);
    if (from_evidence && IsolationUsesXactSnapshot())
        ereport(ERROR,(errcode(ERRCODE_T_R_SERIALIZATION_FAILURE),
                       errmsg("%s: retry the evidence transaction at READ COMMITTED",label)));

    cell_ids=typed_cell_ids(type16,&subjects,&objects,label);
    cell_id_array = construct_array(cell_ids, subjects.n,
                                    BYTEAOID, -1, false, 'i');

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: SPI_connect failed", label)));
    if (from_evidence)
        lock_evidence_targets(type16,cell_id_array,&subjects,&objects,&ts,label);
    priors = read_run_priors(type16, type_datum, cell_ids, &subjects,
                             0, subjects.n, label);
    matched_n = priors->matched_n;
    if (from_evidence)
    {
        if (matched_n!=(uint64)subjects.n)
            ereport(ERROR,(errcode(ERRCODE_INTERNAL_ERROR),
                           errmsg("%s: locked target is absent from its physical owner",label)));
        FoldEvidenceStates *evidence=read_evidence_states(
            type16,&subjects,&objects,&games,&ts,priors,label,false,true);
        fold_run_states(&phis,&opps,&games,&sums,&periods,0,subjects.n,
                        priors,label,&folds,evidence->recomputed);
        int64 affected=write_evidence_states(
            type16,cell_id_array,&subjects,&objects,evidence,&folds,label);
        SPI_finish();
        PG_RETURN_INT64(affected);
    }
    fold_run_states(&phis, &opps, &games, &sums, &periods, 0, subjects.n,
                    priors, label, &folds, NULL);

    matched_plan = typed_plan(&upsert_matched_plans,
                              "consensus_upsert matched plans", type16,
                              UPSERT_MATCHED_SQL, 9, write_args);
    novel_plan = typed_plan(&upsert_novel_plans,
                            "consensus_upsert novel plans", type16,
                            UPSERT_NOVEL_SQL, 9, write_args);
    merge_plan = typed_plan(&upsert_merge_plans, "consensus_upsert merge plans",
                            type16, UPSERT_MERGE_SQL, 18, args);
    vals[0] = PointerGetDatum(cell_id_array);
    vals[1] = PointerGetDatum(subjects.array);
    vals[2] = PointerGetDatum(objects.array);
    vals[3] = PointerGetDatum(phis.array);
    vals[4] = PointerGetDatum(games.array);
    vals[5] = PointerGetDatum(sums.array);
    vals[6] = PointerGetDatum(ts.array);
    vals[7] = PointerGetDatum(folds.seen_array);
    vals[8] = PointerGetDatum(folds.rating_array);
    vals[9] = PointerGetDatum(folds.rd_array);
    vals[10] = PointerGetDatum(folds.volatility_array);
    vals[11] = PointerGetDatum(
        opps.n > 0 ? opps.array : neutral_opponent_array((int) subjects.n));
    period_run = period_window(&periods, &opps, &phis, &games, &sums,
                               0, subjects.n);
    vals[12] = PointerGetDatum(period_run.starts);
    vals[13] = PointerGetDatum(period_run.ends);
    vals[14] = PointerGetDatum(period_run.opponents);
    vals[15] = PointerGetDatum(period_run.phis);
    vals[16] = PointerGetDatum(period_run.games);
    vals[17] = PointerGetDatum(period_run.sums);
    write_vals[0] = vals[0];
    write_vals[1] = vals[1];
    write_vals[2] = vals[2];
    write_vals[3] = vals[4];
    write_vals[4] = vals[6];
    write_vals[5] = vals[7];
    write_vals[6] = vals[8];
    write_vals[7] = vals[9];
    write_vals[8] = vals[10];
    {
        int64 affected = (int64) upsert_persist_keyed_or_fallback(
            matched_plan, novel_plan, merge_plan, write_vals, vals,
            PG_GETARG_DATUM(0),
            matched_n, (uint64) subjects.n, label);
        SPI_finish();
        PG_RETURN_INT64(affected);
    }
}

Datum
pg_laplace_consensus_upsert_type(PG_FUNCTION_ARGS)
{
    return consensus_upsert_type(fcinfo,false);
}

Datum
pg_laplace_consensus_upsert_evidence_type(PG_FUNCTION_ARGS)
{
    return consensus_upsert_type(fcinfo,true);
}

/* Refold existing cells from their retained testimony alone, through the same
 * lock, snapshot, native fold and result write as the evidence path; there is
 * no incoming delta. Every target must hold replayable testimony. */
Datum
pg_laplace_consensus_refold_evidence_type(PG_FUNCTION_ARGS)
{
    const char *label="consensus_refold_evidence_type";
    InArray subjects,objects;
    Datum type_datum;
    const uint8_t *type16;
    Datum *cell_ids;
    ArrayType *ids;
    FoldPriorStates *priors;
    FoldEvidenceStates *evidence;
    FoldStateArrays folds;
    int64 affected;

    if (PG_ARGISNULL(0))
        ereport(ERROR,(errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                       errmsg("%s: type must not be NULL",label)));
    type_datum=PG_GETARG_DATUM(0);
    type16=bytea16(type_datum,label);
    in_array(fcinfo,1,BYTEAOID,-1,false,'i',false,label,&subjects);
    in_array(fcinfo,2,BYTEAOID,-1,false,'i',true,label,&objects);
    if (subjects.n!=objects.n)
        ereport(ERROR,(errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                       errmsg("%s: parallel arrays must share length",label)));
    if (subjects.n==0) PG_RETURN_INT64(0);
    if (IsolationUsesXactSnapshot())
        ereport(ERROR,(errcode(ERRCODE_T_R_SERIALIZATION_FAILURE),
                       errmsg("%s: retry the evidence transaction at READ COMMITTED",label)));
    cell_ids=typed_cell_ids(type16,&subjects,&objects,label);
    ids=construct_array(cell_ids,subjects.n,BYTEAOID,-1,false,'i');
    if (SPI_connect()!=SPI_OK_CONNECT)
        ereport(ERROR,(errcode(ERRCODE_INTERNAL_ERROR),
                       errmsg("%s: SPI_connect failed",label)));
    lock_evidence_targets(type16,ids,&subjects,&objects,NULL,label);
    priors=read_run_priors(type16,type_datum,cell_ids,&subjects,0,subjects.n,label);
    if (priors->matched_n!=(uint64)subjects.n)
        ereport(ERROR,(errcode(ERRCODE_INTERNAL_ERROR),
                       errmsg("%s: locked target is absent from its physical owner",label)));
    evidence=read_evidence_states(type16,&subjects,&objects,NULL,NULL,priors,label,true,true);
    folds.seen_array=NULL;
    folds.rating_array=construct_array(priors->ratings,subjects.n,INT8OID,8,true,'d');
    folds.rd_array=construct_array(priors->rds,subjects.n,INT8OID,8,true,'d');
    folds.volatility_array=construct_array(priors->volatilities,subjects.n,INT8OID,8,true,'d');
    affected=write_evidence_states(type16,ids,&subjects,&objects,evidence,&folds,label);
    SPI_finish();
    PG_RETURN_INT64(affected);
}
