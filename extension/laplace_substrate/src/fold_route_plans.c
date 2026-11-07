/*
 * fold_route_plans.c — Session plan cache, HASH leaf routing, array plumbing,
 * and exact-leaf prior state reads for consensus folding.
 */
#include "fold_route_internal.h"

PG_FUNCTION_INFO_V1(pg_laplace_consensus_partition_leaf);

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
SPIPlanPtr
laplace_fold_typed_plan(HTAB **slot, const char *name, const uint8_t *type16,
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

static PriorRouteEntry shared_prior_route = {{0}, NULL, NULL, NULL};

/* Consensus is HASH(subject_id); a relation type owns no partition of its own,
 * and cell id = blake3(subject || type || object) already binds the relation
 * type, so all relation types share one set of 64 per-leaf prior and update plans. */
PriorRouteEntry *
laplace_fold_prior_route(const uint8_t *type16, Datum type_datum, const char *label)
{
    (void) type16;
    (void) type_datum;
    if (shared_prior_route.leaf_plans == NULL)
    {
        shared_prior_route.leaves = laplace_hash_leaves("consensus", label);
        shared_prior_route.leaf_plans = MemoryContextAllocZero(
            TopMemoryContext, sizeof(SPIPlanPtr) * shared_prior_route.leaves->count);
        shared_prior_route.update_plans = MemoryContextAllocZero(
            TopMemoryContext, sizeof(SPIPlanPtr) * shared_prior_route.leaves->count);
    }
    else
    {
        /* A swapped leaf keeps its name, so the prepared per-leaf plans stay valid; the
         * leaf oids they were named from are resolved again. */
        shared_prior_route.leaves = laplace_hash_leaves("consensus", label);
    }
    return &shared_prior_route;
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
    (void) laplace_fold_bytea16(PG_GETARG_DATUM(0), label);
    (void) laplace_fold_bytea16(PG_GETARG_DATUM(1), label);
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

    (void) type16;
    if (plan == NULL)
    {
        static const Oid argtypes[2] = {BYTEAARRAYOID, BYTEAARRAYOID};
        char            *namespace_name;
        char            *relation_name;
        char            *qualified_name;
        StringInfoData   sql;

        namespace_name = get_namespace_name(
            get_rel_namespace(route->leaves->leaf_oids[remainder]));
        relation_name = get_rel_name(route->leaves->leaf_oids[remainder]);
        if (namespace_name == NULL || relation_name == NULL)
            ereport(ERROR,
                    (errcode(ERRCODE_UNDEFINED_TABLE),
                     errmsg("%s: consensus HASH leaf disappeared", label)));
        qualified_name = quote_qualified_identifier(namespace_name,
                                                     relation_name);

        initStringInfo(&sql);
        appendStringInfo(&sql,
            "WITH locked AS MATERIALIZED ("
            "  SELECT c.id, c.subject_id, c.rating, c.rd, c.volatility "
            "  FROM ONLY %s c "
            "  WHERE c.id = ANY($1::bytea[]) "
            "  FOR UPDATE OF c) "
            "SELECT b.ord, locked.rating, locked.rd, locked.volatility "
            "FROM unnest($1::bytea[], $2::bytea[]) WITH ORDINALITY "
            "     AS b(id, s, ord) "
            "JOIN locked ON locked.subject_id = b.s AND locked.id = b.id",
            qualified_name);
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

void
laplace_fold_in_array(FunctionCallInfo fcinfo, int argno, Oid elmtype, int elmlen,
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

const uint8_t *
laplace_fold_bytea16(Datum d, const char *label)
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
ArrayType *
laplace_fold_neutral_opponent_array(int n)
{
    Datum *d = (Datum *) palloc(sizeof(Datum) * (n > 0 ? n : 1));
    int    i;

    for (i = 0; i < n; i++)
        d[i] = Int64GetDatum(CONSENSUS_FOLD_NEUTRAL_MU);
    return construct_array(d, n, INT8OID, 8, true, 'd');
}

ArrayType *
laplace_fold_array_window(ArrayType *original, const Datum *src, const bool *src_nulls,
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

InArray
laplace_fold_in_array_window(const InArray *source, int start, int n,
                             Oid elmtype, int elmlen, bool elmbyval, char elmalign)
{
    InArray out;

    out.array = laplace_fold_array_window(source->array, source->elems, source->nulls,
                                          source->n, start, n,
                                          elmtype, elmlen, elmbyval, elmalign);
    out.elems = source->elems + start;
    out.nulls = source->nulls != NULL ? source->nulls + start : NULL;
    out.n = n;
    return out;
}

FoldPriorStates *
laplace_fold_prior_states_create(int n)
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
SPIPlanPtr
laplace_fold_update_leaf_plan(PriorRouteEntry *route, int remainder, const char *label)
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
FoldPriorStates *
laplace_fold_read_run_priors(const uint8_t *type16, Datum type_datum,
                             const Datum *cell_ids, const InArray *subjects,
                             int run_start, int run_n, const char *label)
{
    PriorRouteEntry *route = laplace_fold_prior_route(type16, type_datum, label);
    if (pg_class_aclcheck(route->leaves->parent_oid, GetUserId(), ACL_SELECT) != ACLCHECK_OK ||
        pg_class_aclcheck(route->leaves->parent_oid, GetUserId(), ACL_UPDATE) != ACLCHECK_OK)
        aclcheck_error(ACLCHECK_NO_PRIV, OBJECT_TABLE, "consensus");
    if (check_enable_rls(route->leaves->parent_oid, InvalidOid, true) == RLS_ENABLED)
        ereport(ERROR, (errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
            errmsg("%s: native standing mutation requires unfiltered consensus access", label)));
    int               nleaves = route->leaves->count;
    PriorLeafBatch   *batches = palloc0(sizeof(PriorLeafBatch) * nleaves);
    FoldPriorStates *states = laplace_fold_prior_states_create(run_n);
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
