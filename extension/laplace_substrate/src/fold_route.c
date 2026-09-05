/*
 * fold_route.c — native keyed routing for the two fold WRITE sites
 * (attestation_merge, consensus_upsert). Completes GH #565: the probe half
 * landed in descent_probe.c; this is the write half.
 *
 * Both tables are partitioned LIST(type_id) -> HASH(subject_id), and both
 * callers hold the partition keys for every row (the ids were computed FROM
 * them). Routing is therefore the caller's knowledge, not the planner's to
 * rediscover — binding law: routing/math in C/SPI, SQL orchestrates.
 *
 * What this replaces, and why (all measured on live seeds):
 *  - The plpgsql bodies materialized every batch into a fresh temp table
 *    (CREATE TEMP TABLE + CREATE INDEX + ANALYZE per call) purely so a
 *    per-type loop could bind the partition key to a variable/literal.
 *  - attestation_merge went further: EXECUTE format(%L) per type — correct
 *    pruning, but a full re-plan of a partitioned UPDATE for every type of
 *    every chunk of every apply, forever. (An UPDATE's result relations are
 *    locked at PLAN time, so only a literal key prunes; a generic plan opens
 *    all ~1,300 leaves — the disease its comment documents.)
 *
 * The native shape: group the batch by type in C (run detection — the caller
 *  contract already sorts by (type, subject, id), and correctness does not
 *  depend on it: a type split across runs just executes its plan twice on
 *  disjoint rows), then execute a SESSION-CACHED prepared plan per type whose
 *  type_id is a hex LITERAL in the plan text, kept in an HTAB of
 *  type_id -> SPI_keepplan'd SPIPlanPtr in TopMemoryContext. Plan-time LIST
 *  pruning excludes unrelated types. Consensus phase 1 additionally applies
 *  PostgreSQL's HASH partition function in C and gives each exact leaf only the
 *  rows it owns; phase 3 persists matched rows through the primary-key conflict
 *  arbiter and novel rows as target-free inserts. The old MERGE remains only
 *  as a bounded concurrent-insert collision fallback. No temp table, no
 *  per-batch ANALYZE, no volatility trap.
 *
 * Fold math stays one implementation per fact: both fold arms run the same
 * core (glicko2_init + glicko2_fold_grouped_period) the SQL scalar
 * (laplace_glicko2_accumulate_period) wraps — computed natively in one pass
 * per type run, matched cells from their stored prior, novel cells from the
 * neutral prior. The scalar remains only in the collision MERGE fallback for
 * a concurrently-inserted cell (see UPSERT_MERGE_SQL).
 * consensus_id stays one implementation the same way: the SQL definition IS
 * blake3(subject || type || COALESCE(object, 16 zero bytes)) via the core
 * hash128_blake3; this file calls that exact core function over the exact
 * 48-byte layout.
 */
#include "postgres.h"

#include "access/table.h"
#include "access/xact.h"
#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "partitioning/partbounds.h"
#include "partitioning/partdesc.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/partcache.h"
#include "utils/resowner.h"

#include "laplace/core/hash128.h"
#include "laplace/core/glicko2.h"

#include "consensus_fold_math.h"

PG_FUNCTION_INFO_V1(pg_laplace_attestation_merge);
PG_FUNCTION_INFO_V1(pg_laplace_attestation_merge_type);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_upsert);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_upsert_type);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_partition_leaf);

/* ------------------------------------------------------------------ */
/* Session plan cache: one HTAB per statement family, keyed by type id */
/* ------------------------------------------------------------------ */

typedef struct TypePlanEntry
{
    char       type_id[16];
    SPIPlanPtr plan;
} TypePlanEntry;

static HTAB *merge_plans = NULL;          /* attestations matched MERGE       */
static HTAB *upsert_matched_plans = NULL; /* consensus PK-arbitrated updates  */
static HTAB *upsert_novel_plans = NULL;   /* consensus target-free inserts    */
static HTAB *upsert_merge_plans = NULL;   /* collision-only MERGE fallback    */

/* The substrate contract is LIST(type_id) -> HASH(subject_id, 8). A prior
 * lookup must use BOTH pieces of routing information. Literal type pruning
 * alone still left eight leaves beneath the selected LIST partition, and a
 * join whose subject key came from unnest could probe/scan all eight for each
 * input row. Keep one exact-leaf plan per type/remainder instead. */
#define CONSENSUS_HASH_LEAVES 8

typedef struct PriorRouteEntry
{
    char       type_id[16];
    Oid        hash_parent_oid;
    Oid        leaf_oids[CONSENSUS_HASH_LEAVES];
    SPIPlanPtr leaf_plans[CONSENSUS_HASH_LEAVES];
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

/* Resolve the concrete HASH leaves that own one relation type. Named relation
 * types resolve to their dedicated LIST child; dynamic types resolve to the
 * DEFAULT child. Both have the same HASH(8) contract. Resolution happens once
 * per backend/type and the prepared plans retain normal PostgreSQL dependency
 * invalidation. */
static PriorRouteEntry *
prior_route(const uint8_t *type16, Datum type_datum, const char *label)
{
    PriorRouteEntry *entry;
    bool             found;

    entry = (PriorRouteEntry *) hash_search(prior_route_htab(), type16,
                                             HASH_ENTER, &found);
    if (!found)
    {
        Oid                namespace_oid;
        Oid                root_oid;
        Oid                hash_parent_oid;
        Relation           root;
        Relation           hash_parent;
        PartitionKey       key;
        PartitionDesc      desc;
        PartitionBoundInfo bounds;
        bool               equal;
        int                datum_index;
        int                part_index;
        int                remainder;

        memset(((char *) entry) + sizeof(entry->type_id), 0,
               sizeof(*entry) - sizeof(entry->type_id));
        namespace_oid = get_namespace_oid("laplace", false);
        root_oid = get_relname_relid("consensus", namespace_oid);
        if (!OidIsValid(root_oid))
            ereport(ERROR,
                    (errcode(ERRCODE_UNDEFINED_TABLE),
                     errmsg("%s: laplace.consensus does not exist", label)));

        root = table_open(root_oid, AccessShareLock);
        key = RelationGetPartitionKey(root);
        desc = RelationGetPartitionDesc(root, false);
        if (key == NULL || key->strategy != PARTITION_STRATEGY_LIST ||
            key->partnatts != 1 || desc == NULL)
            ereport(ERROR,
                    (errcode(ERRCODE_WRONG_OBJECT_TYPE),
                     errmsg("%s: laplace.consensus must be LIST(type_id) partitioned",
                            label)));
        bounds = desc->boundinfo;
        datum_index = partition_list_bsearch(key->partsupfunc,
                                              key->partcollation,
                                              bounds, type_datum, &equal);
        part_index = equal ? bounds->indexes[datum_index]
                           : bounds->default_index;
        if (part_index < 0 || part_index >= desc->nparts)
            ereport(ERROR,
                    (errcode(ERRCODE_CHECK_VIOLATION),
                     errmsg("%s: relation type has no consensus partition", label)));
        hash_parent_oid = desc->oids[part_index];
        table_close(root, AccessShareLock);

        hash_parent = table_open(hash_parent_oid, AccessShareLock);
        key = RelationGetPartitionKey(hash_parent);
        desc = RelationGetPartitionDesc(hash_parent, false);
        if (key == NULL || key->strategy != PARTITION_STRATEGY_HASH ||
            key->partnatts != 1 || desc == NULL ||
            desc->nparts != CONSENSUS_HASH_LEAVES ||
            desc->boundinfo->nindexes != CONSENSUS_HASH_LEAVES)
            ereport(ERROR,
                    (errcode(ERRCODE_WRONG_OBJECT_TYPE),
                     errmsg("%s: consensus relation partition must be HASH(subject_id, %d)",
                            label, CONSENSUS_HASH_LEAVES)));
        for (remainder = 0; remainder < CONSENSUS_HASH_LEAVES; remainder++)
        {
            part_index = desc->boundinfo->indexes[remainder];
            if (part_index < 0 || part_index >= desc->nparts)
                ereport(ERROR,
                        (errcode(ERRCODE_CHECK_VIOLATION),
                         errmsg("%s: consensus HASH partition is missing remainder %d",
                                label, remainder)));
            entry->leaf_oids[remainder] = desc->oids[part_index];
        }
        entry->hash_parent_oid = hash_parent_oid;
        table_close(hash_parent, AccessShareLock);
    }
    return entry;
}

/* Return the one physical consensus leaf that owns (type, subject).  Repair
 * and inference SQL occasionally need to mutate derived state outside the
 * ingest upsert.  Giving those callers the same native partition router keeps
 * them from issuing parent UPDATE/DELETE statements whose plans open every
 * HASH child.  This is routing metadata only; it never reads or changes a
 * consensus row. */
Datum
pg_laplace_consensus_partition_leaf(PG_FUNCTION_ARGS)
{
    const char      *label = "consensus.partition_leaf";
    Datum            type_datum;
    const uint8_t   *type16;
    PriorRouteEntry *route;
    Relation         hash_parent;
    PartitionKey     key;
    Datum            values[1];
    bool             nulls[1] = {false};
    uint64           hash;
    int              remainder;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR,
                (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                 errmsg("%s: type and subject must not be NULL", label)));

    type_datum = PG_GETARG_DATUM(0);
    type16 = bytea16(type_datum, label);
    (void) bytea16(PG_GETARG_DATUM(1), label);
    route = prior_route(type16, type_datum, label);

    hash_parent = table_open(route->hash_parent_oid, AccessShareLock);
    key = RelationGetPartitionKey(hash_parent);
    if (key == NULL || key->strategy != PARTITION_STRATEGY_HASH ||
        key->partnatts != 1)
        ereport(ERROR,
                (errcode(ERRCODE_WRONG_OBJECT_TYPE),
                 errmsg("%s: cached consensus route is no longer HASH partitioned",
                        label)));
    values[0] = PG_GETARG_DATUM(1);
    hash = compute_partition_hash_value(
        1, key->partsupfunc, key->partcollation, values, nulls);
    table_close(hash_parent, AccessShareLock);

    remainder = (int) (hash % CONSENSUS_HASH_LEAVES);
    PG_RETURN_OID(route->leaf_oids[remainder]);
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
            get_rel_namespace(route->leaf_oids[remainder]));
        relation_name = get_rel_name(route->leaf_oids[remainder]);
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
 * ratings. The collision-only MERGE fallback consumes this exact opponent, so
 * a concurrent insert cannot change the fold evidence (GH #1321).
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

/* Route each subject through PostgreSQL's own partition support function,
 * then read its stored state from exactly the one physical leaf that owns it.
 * The batch is partitioned once in native memory. Every exact-leaf statement
 * can choose an index, merge, or hash access path without an Append and without
 * multiplying the input by the number of HASH leaves. */
static FoldPriorStates *
read_run_priors(const uint8_t *type16, Datum type_datum,
                const Datum *cell_ids, const InArray *subjects,
                int run_start, int run_n, const char *label)
{
    PriorRouteEntry *route = prior_route(type16, type_datum, label);
    Relation         hash_parent;
    PartitionKey     key;
    PriorLeafBatch   batches[CONSENSUS_HASH_LEAVES];
    FoldPriorStates *states = fold_prior_states_create(run_n);
    bool              nulls[1] = {false};
    int              *remainder_by_row = (int *) palloc(sizeof(int) * run_n);
    int               i;

    memset(batches, 0, sizeof(batches));
    hash_parent = table_open(route->hash_parent_oid, AccessShareLock);
    key = RelationGetPartitionKey(hash_parent);
    if (key == NULL || key->strategy != PARTITION_STRATEGY_HASH ||
        key->partnatts != 1)
        ereport(ERROR,
                (errcode(ERRCODE_WRONG_OBJECT_TYPE),
                 errmsg("%s: cached consensus route is no longer HASH partitioned",
                        label)));

    for (i = 0; i < run_n; i++)
    {
        Datum  value[1] = {subjects->elems[run_start + i]};
        uint64 hash = compute_partition_hash_value(
            1, key->partsupfunc, key->partcollation, value, nulls);
        int remainder = (int) (hash % CONSENSUS_HASH_LEAVES);

        remainder_by_row[i] = remainder;
        batches[remainder].n++;
    }
    table_close(hash_parent, AccessShareLock);

    for (i = 0; i < CONSENSUS_HASH_LEAVES; i++)
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

    for (i = 0; i < CONSENSUS_HASH_LEAVES; i++)
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

/* Fold is pure in these seven fixed-point inputs and the constant tau. Many
 * novel corpus cells share all seven; calculate each distinct state transition
 * once per batch, not once per edge. Never key only by evidence: matched cells
 * with different stored priors must remain different transitions. */
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

/* Fold one type run natively — matched cells from their stored prior
 * (`priors`: the FOR UPDATE read of this run; ord is 1-based within the run),
 * novel cells from the neutral prior. One tight native pass instead of a
 * record-returning SQL function crossing the executor once per matched row.
 * The novel half was already native (GH #565); this completes the symmetry.
 *
 * Bit parity with the SQL scalars is by construction: their bodies use
 * glicko2_init plus the same uniform/grouped period kernels, and consensus.glicko2_neutral_mu()
 * / consensus.glicko2_tau() are defined as exactly CONSENSUS_FOLD_NEUTRAL_MU /
 * LAPLACE_GLICKO2_DEFAULT_TAU (asserted by tests/sql/consensus_upsert.sql). */
static void
fold_run_states(const InArray *phis, const InArray *opps,
                const InArray *games, const InArray *sums,
                const PeriodArrays *periods,
                int run_start, int run_n, const FoldPriorStates *priors,
                const char *label, FoldStateArrays *out)
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
        glicko2_state_t st;
        int64 input[7];
        FoldMemo *entry = NULL;
        bool found = false;
        int period_start = -1;
        int period_group_n = 1;
        int64 phi = DatumGetInt64(phis->elems[run_start + i]);
        /* The opponent this witness presents. opps->n == 0 means the caller did
         * not supply ratings, which folds against neutral exactly as before
         * (GH #1321). */
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
            else if (consensus_fold_apply_partial(
                         &st, opp, phi, n_games, sum,
                         LAPLACE_GLICKO2_DEFAULT_TAU) != 0)
                ereport(ERROR,
                        (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                         errmsg("%s: aggregate exceeds fixed-point capacity", label),
                         errdetail("games=%ld sum_score=%ld",
                                   (long) n_games, (long) sum)));
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
/* attestation_merge — routed present-row observation merge            */
/* ------------------------------------------------------------------ */

/* sum_score_fp1e9 accumulates additively with observation_count (evidence is
 * the exact record of the fold's inputs); opponent_rd_fp1e9 is deliberately
 * NOT updated — it is the per-deposit phi, and the attestation identity pins
 * (subject, type, object, source, context), so a merge of the same row
 * re-witnesses under the same source/relation: incoming phi == stored phi.
 * See sql/functions/fold/attestation_merge.sql.in. */
static const char *MERGE_SQL =
    "MERGE INTO laplace.attestations a "
    "USING unnest($1::bytea[], $2::bytea[], $3::int8[], $4::int8[], "
    "             $5::timestamptz[], $6::boolean[]) "
    "      AS b(id, s, games, sum, ts, fold_replayable) "
    "ON a.type_id = '\\x%s'::bytea AND a.subject_id = b.s AND a.id = b.id "
    "WHEN MATCHED THEN UPDATE SET "
    "   observation_count = a.observation_count + b.games, "
    "   sum_score_fp1e9   = a.sum_score_fp1e9 + b.sum, "
    "   last_observed_at  = GREATEST(a.last_observed_at, b.ts), "
    "   fold_replayable   = a.fold_replayable AND b.fold_replayable";

Datum
pg_laplace_attestation_merge(PG_FUNCTION_ARGS)
{
    const char *label = "attestation_merge";
    InArray     ids, types, subjects, games, sums, ts, fold_replayable;
    int64       affected = 0;
    int         run_start;

    in_array(fcinfo, 0, BYTEAOID, -1, false, 'i', false, label, &ids);
    in_array(fcinfo, 1, BYTEAOID, -1, false, 'i', false, label, &types);
    in_array(fcinfo, 2, BYTEAOID, -1, false, 'i', false, label, &subjects);
    in_array(fcinfo, 3, INT8OID, 8, true, 'd', false, label, &games);
    in_array(fcinfo, 4, INT8OID, 8, true, 'd', false, label, &sums);
    in_array(fcinfo, 5, TIMESTAMPTZOID, 8, true, 'd', false, label, &ts);
    in_array(fcinfo, 6, BOOLOID, 1, true, 'c', false, label, &fold_replayable);
    if (types.n != ids.n || subjects.n != ids.n || games.n != ids.n ||
        sums.n != ids.n || ts.n != ids.n || fold_replayable.n != ids.n)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: parallel arrays must share length "
                        "(%d/%d/%d/%d/%d/%d/%d)", label,
                        ids.n, types.n, subjects.n, games.n, sums.n, ts.n,
                        fold_replayable.n)));
    if (ids.n == 0)
        PG_RETURN_INT64(0);

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: SPI_connect failed", label)));

    run_start = 0;
    while (run_start < ids.n)
    {
        const uint8_t *type16 = bytea16(types.elems[run_start], label);
        int            run_n = 0;
        int            i = run_start;
        SPIPlanPtr     plan;
        Datum          vals[6];
        static const Oid argtypes[6] =
            {BYTEAARRAYOID, BYTEAARRAYOID, INT8ARRAYOID, INT8ARRAYOID,
             1185 /* timestamptz[] */, BOOLARRAYOID};
        int            rc;

        while (i < ids.n &&
               memcmp(bytea16(types.elems[i], label), type16, 16) == 0)
        {
            run_n++;
            i++;
        }

        plan = typed_plan(&merge_plans, "attestation_merge plans", type16,
                          MERGE_SQL, 6, argtypes);
        vals[0] = PointerGetDatum(array_window(ids.array, ids.elems, NULL,
                                              ids.n, run_start, run_n,
                                              BYTEAOID, -1, false, 'i'));
        vals[1] = PointerGetDatum(array_window(subjects.array, subjects.elems, NULL,
                                              subjects.n, run_start, run_n,
                                              BYTEAOID, -1, false, 'i'));
        vals[2] = PointerGetDatum(array_window(games.array, games.elems, NULL,
                                              games.n, run_start, run_n,
                                              INT8OID, 8, true, 'd'));
        vals[3] = PointerGetDatum(array_window(sums.array, sums.elems, NULL,
                                              sums.n, run_start, run_n,
                                              INT8OID, 8, true, 'd'));
        vals[4] = PointerGetDatum(array_window(ts.array, ts.elems, NULL,
                                              ts.n, run_start, run_n,
                                              TIMESTAMPTZOID, 8, true, 'd'));
        vals[5] = PointerGetDatum(array_window(
            fold_replayable.array, fold_replayable.elems, NULL,
            fold_replayable.n, run_start, run_n, BOOLOID, 1, true, 'c'));

        rc = SPI_execute_plan(plan, vals, NULL, false, 0);
        if (rc != SPI_OK_MERGE)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: MERGE failed: %s",
                            label, SPI_result_code_string(rc))));
        affected += (int64) SPI_processed;

        run_start = i;
    }

    SPI_finish();
    PG_RETURN_INT64(affected);
}

/* Direct routed form for first-party ingest. The caller already owns a single
 * type run, so transmitting the same 16-byte type once per row merely to detect
 * that run again is pure allocation/wire/deconstruction work. */
Datum
pg_laplace_attestation_merge_type(PG_FUNCTION_ARGS)
{
    const char    *label = "attestation_merge_type";
    const uint8_t *type16;
    InArray        ids, subjects, games, sums, ts, fold_replayable;
    SPIPlanPtr     plan;
    Datum          vals[6];
    static const Oid argtypes[6] =
        {BYTEAARRAYOID, BYTEAARRAYOID, INT8ARRAYOID, INT8ARRAYOID,
         1185 /* timestamptz[] */, BOOLARRAYOID};
    int rc;

    if (PG_ARGISNULL(0))
        ereport(ERROR,
                (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                 errmsg("%s: type must not be NULL", label)));
    type16 = bytea16(PG_GETARG_DATUM(0), label);
    in_array(fcinfo, 1, BYTEAOID, -1, false, 'i', false, label, &ids);
    in_array(fcinfo, 2, BYTEAOID, -1, false, 'i', false, label, &subjects);
    in_array(fcinfo, 3, INT8OID, 8, true, 'd', false, label, &games);
    in_array(fcinfo, 4, INT8OID, 8, true, 'd', false, label, &sums);
    in_array(fcinfo, 5, TIMESTAMPTZOID, 8, true, 'd', false, label, &ts);
    in_array(fcinfo, 6, BOOLOID, 1, true, 'c', false, label, &fold_replayable);
    if (subjects.n != ids.n || games.n != ids.n || sums.n != ids.n || ts.n != ids.n
        || fold_replayable.n != ids.n)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: parallel arrays must share length", label)));
    if (ids.n == 0)
        PG_RETURN_INT64(0);

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: SPI_connect failed", label)));
    plan = typed_plan(&merge_plans, "attestation_merge plans", type16,
                      MERGE_SQL, 6, argtypes);
    vals[0] = PointerGetDatum(ids.array);
    vals[1] = PointerGetDatum(subjects.array);
    vals[2] = PointerGetDatum(games.array);
    vals[3] = PointerGetDatum(sums.array);
    vals[4] = PointerGetDatum(ts.array);
    vals[5] = PointerGetDatum(fold_replayable.array);
    rc = SPI_execute_plan(plan, vals, NULL, false, 0);
    if (rc != SPI_OK_MERGE)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: MERGE failed: %s", label,
                        SPI_result_code_string(rc))));
    {
        int64 affected = (int64) SPI_processed;
        SPI_finish();
        PG_RETURN_INT64(affected);
    }
}

/* ------------------------------------------------------------------ */
/* consensus_upsert — routed inline fold                               */
/* ------------------------------------------------------------------ */

/* The fold is now three phases per type run, all literal-routed and
 * session-plan-cached:
 *
 *  1. read_run_priors partitions the input by PostgreSQL's own HASH support
 *     function, then reads and row-locks each stored cell from its exact owning
 *     leaf. A locked row cannot change between this read and persistence, so
 *     folding from the read state IS folding from the write-time state.
 *  2. fold_run_states() computes every outgoing (rating, rd, volatility) in
 *     one tight native pass — matched cells from their stored prior, novel
 *     cells from the neutral prior. Before this, only the novel half was
 *     native: every already-existing cell crossed into the record-returning
 *     scalar through the executor once per matched row — the last per-row
 *     fold work in the write path.
 *  3. UPSERT_MATCHED_SQL sends rows seen in phase 1 through the primary-key
 *     conflict arbiter, while UPSERT_NOVEL_SQL inserts unseen rows without a
 *     target read. UPSERT_MERGE_SQL is reached only after a concurrent insert
 *     invalidates phase 1's novel classification.
 *
 * b.seen is the router's own matched prediction from phase 1. A MERGE-matched
 * row the router did NOT see can only be a cell inserted by a concurrent
 * writer between phases 1 and 3 (FOR UPDATE excludes the concurrent-update
 * case). Its precomputed state is a neutral fold — wrong to assign — so the
 * MATCHED arm falls back to the same scalar the pre-native plan called, which
 * folds the concurrent state correctly under EvalPlanQual. CASE keeps that
 * fallback lazy: it executes only for rows re-classified after a concurrent-
 * insert collision (see upsert_merge_with_retry), so the (f()).col triple
 * evaluation sits on a path whose executions round to zero. */

/* Phase 3 has no target join on the ordinary path. Phase 1 already classified
 * and row-locked every existing cell. Persist those rows through the declared
 * primary-key arbiter; PostgreSQL routes the proposed row by type+subject and
 * probes the owning HASH leaf's unique index. Novel rows are plain inserts and
 * therefore perform no target read at all. This removes the empty-leaf MERGE
 * plan that produced O(rows x leaves) sequential scans in GH #1370.
 *
 * A cell inserted by a concurrent writer after phase 1 makes the novel INSERT
 * raise unique_violation. upsert_persist_keyed_or_fallback rolls back this
 * phase-3 subtransaction and executes the old MERGE race fallback once; its
 * b.seen=false arm folds the newly committed prior correctly. Thus the bad
 * planner shape is no longer the production path without weakening the
 * concurrent-insert correctness contract. */
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

/* NO mask queue here — parity with the plpgsql body this replaces
 * (2026-07-21): the caller deposits highway bits INLINE for this same delta
 * via highway_mask_deposit; highway_mask_dirty is populated only by the
 * repair verbs, which need to CLEAR bits (per-source evict). */

/* Duplicate-cell guard: parity with the plpgsql contract check. */
typedef struct CellSeen
{
    char id[16];
} CellSeen;

/* Execute one consensus MERGE, absorbing the concurrent-insert race without
 * leaning on the global apply mutex: if another writer commits a cell of this
 * run between the prior-state read and this MERGE, the NOT MATCHED arm
 * collides (MERGE has no ON CONFLICT) and raises unique_violation. The
 * colliding row is committed, so re-executing the same plan under a fresh
 * snapshot re-classifies it as MATCHED with b.seen = false — the lazy scalar
 * fallback arm then folds the concurrent state correctly. Everything the
 * failed attempt wrote rolls back with its subtransaction; the phase-1 FOR
 * UPDATE locks belong to the parent transaction and survive. Bounded: every
 * retry requires a fresh committed collision on this run's cells, so
 * exhausting the attempts signals something structurally wrong, and the last
 * error is rethrown as-is. */
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
 * partitioned target. Existing rows use the PK conflict arbiter; novel rows
 * insert directly. Both writes share a subtransaction so a concurrent insert
 * of a phase-1-novel cell can roll back any preceding matched updates before
 * the established MERGE race fallback reclassifies it under a fresh snapshot. */
static uint64
upsert_persist_keyed_or_fallback(SPIPlanPtr matched_plan,
                                 SPIPlanPtr novel_plan,
                                 SPIPlanPtr merge_plan,
                                 Datum *write_vals, Datum *merge_vals,
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
            rc = SPI_execute_plan(matched_plan, write_vals, NULL, false, 0);
            if (rc != SPI_OK_INSERT || SPI_processed != matched_n)
                ereport(ERROR,
                        (errcode(ERRCODE_INTERNAL_ERROR),
                         errmsg("%s: keyed matched upsert affected %lu of %lu rows (%s)",
                                label, (unsigned long) SPI_processed,
                                (unsigned long) matched_n,
                                SPI_result_code_string(rc))));
            processed += SPI_processed;
        }
        if (matched_n < total_n)
        {
            uint64 novel_n = total_n - matched_n;

            rc = SPI_execute_plan(novel_plan, write_vals, NULL, false, 0);
            if (rc != SPI_OK_INSERT || SPI_processed != novel_n)
                ereport(ERROR,
                        (errcode(ERRCODE_INTERNAL_ERROR),
                         errmsg("%s: keyed novel insert affected %lu of %lu rows (%s)",
                                label, (unsigned long) SPI_processed,
                                (unsigned long) novel_n,
                                SPI_result_code_string(rc))));
            processed += SPI_processed;
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

    /* Cell ids natively: blake3(subject || type || COALESCE(object, zeros)) —
     * the exact byte layout of the SQL consensus_id definition, through the
     * same core hash. Doubles as the duplicate-cell contract check. */
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
                        priors, label, &folds);

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
            matched_n, (uint64) run_n, label);

        run_start = j;
    }

    SPI_finish();
    PG_RETURN_INT64(affected);
}

/* Direct routed form for a caller-owned single type run. Besides eliminating
 * the redundant type array, this constructs the derived cell-id array exactly
 * once and passes every caller array straight through to the cached MERGE plan. */
Datum
pg_laplace_consensus_upsert_type(PG_FUNCTION_ARGS)
{
    const char    *label = "consensus_upsert_type";
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
    HTAB          *seen;
    HASHCTL        ctl;
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
    int i;

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

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(CellSeen);
    seen = hash_create("consensus_upsert_type cell guard", subjects.n, &ctl,
                       HASH_ELEM | HASH_BLOBS);
    cell_ids = (Datum *) palloc(sizeof(Datum) * subjects.n);
    for (i = 0; i < subjects.n; i++)
    {
        uint8_t   buf[48];
        hash128_t h;
        bytea    *out;
        bool      found;

        memcpy(buf, bytea16(subjects.elems[i], label), 16);
        memcpy(buf + 16, type16, 16);
        if (objects.nulls[i])
            memset(buf + 32, 0, 16);
        else
            memcpy(buf + 32, bytea16(objects.elems[i], label), 16);
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
    cell_id_array = construct_array(cell_ids, subjects.n,
                                    BYTEAOID, -1, false, 'i');

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: SPI_connect failed", label)));
    priors = read_run_priors(type16, type_datum, cell_ids, &subjects,
                             0, subjects.n, label);
    matched_n = priors->matched_n;
    fold_run_states(&phis, &opps, &games, &sums, &periods, 0, subjects.n,
                    priors, label, &folds);

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
            matched_n, (uint64) subjects.n, label);
        SPI_finish();
        PG_RETURN_INT64(affected);
    }
}
