/*
 * fold_route.c — Consensus upsert SQL templates, subtransaction persistence,
 * collision MERGE fallback, and pg_laplace_consensus_upsert* entry points.
 */
#include "fold_route_internal.h"

PG_FUNCTION_INFO_V1(pg_laplace_consensus_upsert);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_upsert_type);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_upsert_evidence_type);

static HTAB *upsert_matched_plans = NULL; /* consensus PK-arbitrated updates  */
static HTAB *upsert_novel_plans = NULL;   /* consensus target-free inserts    */
static HTAB *upsert_merge_plans = NULL;   /* concurrent-insert collision MERGE */

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

    laplace_fold_in_array(fcinfo, 0, BYTEAOID, -1, false, 'i', false, label, &subjects);
    laplace_fold_in_array(fcinfo, 1, BYTEAOID, -1, false, 'i', false, label, &types);
    laplace_fold_in_array(fcinfo, 2, BYTEAOID, -1, false, 'i', true, label, &objects);
    laplace_fold_in_array(fcinfo, 3, INT8OID, 8, true, 'd', false, label, &phis);
    laplace_fold_in_array(fcinfo, 4, INT8OID, 8, true, 'd', false, label, &games);
    laplace_fold_in_array(fcinfo, 5, INT8OID, 8, true, 'd', false, label, &sums);
    laplace_fold_in_array(fcinfo, 6, TIMESTAMPTZOID, 8, true, 'd', false, label, &ts);
    memset(&opps, 0, sizeof(opps));
    if (PG_NARGS() > 7 && !PG_ARGISNULL(7))
        laplace_fold_in_array(fcinfo, 7, INT8OID, 8, true, 'd', false, label, &opps);
    laplace_fold_read_period_arrays(fcinfo, subjects.n, label, &periods);
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

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(CellSeen);
    seen = hash_create("consensus_upsert cell guard", subjects.n, &ctl,
                       HASH_ELEM | HASH_BLOBS);
    cell_ids = (Datum *) palloc(sizeof(Datum) * subjects.n);
    for (i = 0; i < subjects.n; i++)
    {
        hash128_t  h;
        bytea     *out;
        bool       found;

        h = strand_id(laplace_fold_bytea16(subjects.elems[i], label),
                      laplace_fold_bytea16(types.elems[i], label),
                      objects.nulls[i] ? NULL : laplace_fold_bytea16(objects.elems[i], label));

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
        const uint8_t *type16 = laplace_fold_bytea16(types.elems[run_start], label);
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
               memcmp(laplace_fold_bytea16(types.elems[j], label), type16, 16) == 0)
        {
            run_n++;
            j++;
        }

        run_ids = laplace_fold_array_window(cell_id_array, cell_ids, NULL,
                                            subjects.n, run_start, run_n,
                                            BYTEAOID, -1, false, 'i');
        run_subjects = laplace_fold_array_window(subjects.array, subjects.elems, NULL,
                                                 subjects.n, run_start, run_n,
                                                 BYTEAOID, -1, false, 'i');

        priors = laplace_fold_read_run_priors(type16, types.elems[run_start], cell_ids,
                                              &subjects, run_start, run_n, label);
        matched_n = priors->matched_n;
        laplace_fold_run_states(&phis, &opps, &games, &sums, &periods, run_start, run_n,
                                priors, label, &folds, NULL);

        matched_plan = laplace_fold_typed_plan(&upsert_matched_plans,
                                               "consensus_upsert matched plans", type16,
                                               UPSERT_MATCHED_SQL, 9, write_args);
        novel_plan = laplace_fold_typed_plan(&upsert_novel_plans,
                                             "consensus_upsert novel plans", type16,
                                             UPSERT_NOVEL_SQL, 9, write_args);
        merge_plan = laplace_fold_typed_plan(&upsert_merge_plans, "consensus_upsert merge plans",
                                             type16, UPSERT_MERGE_SQL, 18, args);
        vals[0] = PointerGetDatum(run_ids);
        vals[1] = PointerGetDatum(run_subjects);
        vals[2] = PointerGetDatum(laplace_fold_array_window(
            objects.array, objects.elems, objects.nulls, objects.n,
            run_start, run_n, BYTEAOID, -1, false, 'i'));
        vals[3] = PointerGetDatum(laplace_fold_array_window(
            phis.array, phis.elems, NULL, phis.n, run_start, run_n,
            INT8OID, 8, true, 'd'));
        vals[4] = PointerGetDatum(laplace_fold_array_window(
            games.array, games.elems, NULL, games.n, run_start, run_n,
            INT8OID, 8, true, 'd'));
        vals[5] = PointerGetDatum(laplace_fold_array_window(
            sums.array, sums.elems, NULL, sums.n, run_start, run_n,
            INT8OID, 8, true, 'd'));
        vals[6] = PointerGetDatum(laplace_fold_array_window(
            ts.array, ts.elems, NULL, ts.n, run_start, run_n,
            TIMESTAMPTZOID, 8, true, 'd'));
        vals[7] = PointerGetDatum(folds.seen_array);
        vals[8] = PointerGetDatum(folds.rating_array);
        vals[9] = PointerGetDatum(folds.rd_array);
        vals[10] = PointerGetDatum(folds.volatility_array);
        vals[11] = PointerGetDatum(
            opps.n > 0
            ? laplace_fold_array_window(opps.array, opps.elems, NULL, opps.n,
                                        run_start, run_n, INT8OID, 8, true, 'd')
            : laplace_fold_neutral_opponent_array(run_n));
        period_run = laplace_fold_period_window(&periods, &opps, &phis, &games, &sums,
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
    type16 = laplace_fold_bytea16(type_datum, label);
    laplace_fold_in_array(fcinfo, 1, BYTEAOID, -1, false, 'i', false, label, &subjects);
    laplace_fold_in_array(fcinfo, 2, BYTEAOID, -1, false, 'i', true, label, &objects);
    laplace_fold_in_array(fcinfo, 3, INT8OID, 8, true, 'd', false, label, &phis);
    laplace_fold_in_array(fcinfo, 4, INT8OID, 8, true, 'd', false, label, &games);
    laplace_fold_in_array(fcinfo, 5, INT8OID, 8, true, 'd', false, label, &sums);
    laplace_fold_in_array(fcinfo, 6, TIMESTAMPTZOID, 8, true, 'd', false, label, &ts);
    memset(&opps, 0, sizeof(opps));
    if (PG_NARGS() > 7 && !PG_ARGISNULL(7))
        laplace_fold_in_array(fcinfo, 7, INT8OID, 8, true, 'd', false, label, &opps);
    laplace_fold_read_period_arrays(fcinfo, subjects.n, label, &periods);
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
        laplace_fold_validate_evidence_delta(&phis,&games,&sums,&periods,label);
    if (from_evidence && IsolationUsesXactSnapshot())
        ereport(ERROR,(errcode(ERRCODE_T_R_SERIALIZATION_FAILURE),
                       errmsg("%s: retry the evidence transaction at READ COMMITTED",label)));

    cell_ids=laplace_fold_typed_cell_ids(type16,&subjects,&objects,label);
    cell_id_array = construct_array(cell_ids, subjects.n,
                                    BYTEAOID, -1, false, 'i');

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: SPI_connect failed", label)));
    if (from_evidence)
        laplace_fold_lock_evidence_targets(type16,cell_id_array,&subjects,&objects,&ts,label);
    priors = laplace_fold_read_run_priors(type16, type_datum, cell_ids, &subjects,
                                          0, subjects.n, label);
    matched_n = priors->matched_n;
    if (from_evidence)
    {
        if (matched_n!=(uint64)subjects.n)
            ereport(ERROR,(errcode(ERRCODE_INTERNAL_ERROR),
                           errmsg("%s: locked target is absent from its physical owner",label)));
        FoldEvidenceStates *evidence=laplace_fold_read_evidence_states(
            type16,&subjects,&objects,&games,&ts,priors,label,false,true);
        laplace_fold_run_states(&phis,&opps,&games,&sums,&periods,0,subjects.n,
                                priors,label,&folds,evidence->recomputed);
        int64 affected=laplace_fold_write_evidence_states(
            type16,cell_id_array,&subjects,&objects,evidence,&folds,label);
        SPI_finish();
        PG_RETURN_INT64(affected);
    }
    laplace_fold_run_states(&phis, &opps, &games, &sums, &periods, 0, subjects.n,
                            priors, label, &folds, NULL);

    matched_plan = laplace_fold_typed_plan(&upsert_matched_plans,
                                           "consensus_upsert matched plans", type16,
                                           UPSERT_MATCHED_SQL, 9, write_args);
    novel_plan = laplace_fold_typed_plan(&upsert_novel_plans,
                                         "consensus_upsert novel plans", type16,
                                         UPSERT_NOVEL_SQL, 9, write_args);
    merge_plan = laplace_fold_typed_plan(&upsert_merge_plans, "consensus_upsert merge plans",
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
        opps.n > 0 ? opps.array : laplace_fold_neutral_opponent_array((int) subjects.n));
    period_run = laplace_fold_period_window(&periods, &opps, &phis, &games, &sums,
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

PG_FUNCTION_INFO_V1(pg_laplace_consensus_refold_evidence_type);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_refold_after_retraction_type);

static Datum
consensus_refold_evidence_type(FunctionCallInfo fcinfo, bool retract)
{
    const char *label=retract ? "consensus_refold_after_retraction_type"
                              : "consensus_refold_evidence_type";
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
    type16=laplace_fold_bytea16(type_datum,label);
    laplace_fold_in_array(fcinfo,1,BYTEAOID,-1,false,'i',false,label,&subjects);
    laplace_fold_in_array(fcinfo,2,BYTEAOID,-1,false,'i',true,label,&objects);
    if (subjects.n!=objects.n)
        ereport(ERROR,(errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                       errmsg("%s: parallel arrays must share length",label)));
    if (subjects.n==0)
    {
        if (retract)
        {
            Datum empty[2]={Int64GetDatum(0),Int64GetDatum(0)};
            PG_RETURN_ARRAYTYPE_P(construct_array(empty,2,INT8OID,8,true,'d'));
        }
        PG_RETURN_INT64(0);
    }
    if (IsolationUsesXactSnapshot())
        ereport(ERROR,(errcode(ERRCODE_T_R_SERIALIZATION_FAILURE),
                       errmsg("%s: retry the evidence transaction at READ COMMITTED",label)));
    if (retract && check_enable_rls(get_relname_relid("attestations",
            get_namespace_oid("laplace",false)),InvalidOid,true)==RLS_ENABLED)
        ereport(ERROR,(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
            errmsg("%s: retraction requires complete visibility of retained testimony",label)));
    cell_ids=laplace_fold_typed_cell_ids(type16,&subjects,&objects,label);
    ids=construct_array(cell_ids,subjects.n,BYTEAOID,-1,false,'i');
    if (SPI_connect()!=SPI_OK_CONNECT)
        ereport(ERROR,(errcode(ERRCODE_INTERNAL_ERROR),
                       errmsg("%s: SPI_connect failed",label)));
    laplace_fold_lock_evidence_targets(type16,ids,&subjects,&objects,NULL,label);
    priors=laplace_fold_read_run_priors(type16,type_datum,cell_ids,&subjects,0,subjects.n,label);
    if (priors->matched_n!=(uint64)subjects.n)
        ereport(ERROR,(errcode(ERRCODE_INTERNAL_ERROR),
                       errmsg("%s: locked target is absent from its physical owner",label)));
    evidence=laplace_fold_read_evidence_states(type16,&subjects,&objects,NULL,NULL,priors,label,true,!retract);
    if (retract)
    {
        Datum *kept_ids=palloc(sizeof(Datum)*subjects.n);
        Datum *kept_subjects=palloc(sizeof(Datum)*subjects.n);
        Datum *kept_objects=palloc(sizeof(Datum)*subjects.n);
        bool *kept_nulls=palloc(sizeof(bool)*subjects.n);
        Datum *removed_ids=palloc(sizeof(Datum)*subjects.n);
        Datum *removed_subjects=palloc(sizeof(Datum)*subjects.n);
        FoldEvidenceStates kept;
        FoldPriorStates *states=laplace_fold_prior_states_create(subjects.n);
        kept.recomputed=palloc(sizeof(bool)*subjects.n);
        kept.counts=palloc(sizeof(Datum)*subjects.n);
        kept.timestamps=palloc(sizeof(Datum)*subjects.n);
        int live=0,missing=0;
        for (int i=0;i<subjects.n;++i)
        {
            if (!evidence->recomputed[i])
            {
                removed_ids[missing]=cell_ids[i];
                removed_subjects[missing++]=subjects.elems[i];
                continue;
            }
            kept_ids[live]=cell_ids[i];
            kept_subjects[live]=subjects.elems[i];
            kept_objects[live]=objects.elems[i];
            kept_nulls[live]=objects.nulls[i];
            kept.recomputed[live]=true;
            kept.counts[live]=evidence->counts[i];
            kept.timestamps[live]=evidence->timestamps[i];
            states->ratings[live]=priors->ratings[i];
            states->rds[live]=priors->rds[i];
            states->volatilities[live]=priors->volatilities[i];
            ++live;
        }
        int64 written=0,deleted=0;
        if (live>0)
        {
            InArray ss={0},oo={0};
            ss.n=oo.n=live;
            ss.elems=kept_subjects; oo.elems=kept_objects; oo.nulls=kept_nulls;
            ss.array=construct_array(kept_subjects,live,BYTEAOID,-1,false,'i');
            oo.array=construct_md_array(kept_objects,kept_nulls,1,&live,(int[]){1},BYTEAOID,-1,false,'i');
            folds.rating_array=construct_array(states->ratings,live,INT8OID,8,true,'d');
            folds.rd_array=construct_array(states->rds,live,INT8OID,8,true,'d');
            folds.volatility_array=construct_array(states->volatilities,live,INT8OID,8,true,'d');
            written=laplace_fold_write_evidence_states(type16,
                construct_array(kept_ids,live,BYTEAOID,-1,false,'i'),&ss,&oo,&kept,&folds,label);
        }
        if (missing>0)
        {
            static SPIPlanPtr delete_plan=NULL;
            static const Oid args[3]={BYTEAARRAYOID,BYTEAARRAYOID,BYTEAOID};
            if (delete_plan==NULL)
            {
                SPIPlanPtr plan=SPI_prepare(laplace_sql_query_text("consensus.delete_locked_empty_cells"),3,(Oid *)args);
                if (plan==NULL || SPI_keepplan(plan)!=0)
                    elog(ERROR,"%s: empty-cell delete plan preparation failed",label);
                delete_plan=plan;
            }
            Datum values[3]={
                PointerGetDatum(construct_array(removed_ids,missing,BYTEAOID,-1,false,'i')),
                PointerGetDatum(construct_array(removed_subjects,missing,BYTEAOID,-1,false,'i')),
                type_datum};
            int rc=SPI_execute_plan(delete_plan,values,NULL,false,0);
            if (rc!=SPI_OK_DELETE || SPI_processed!=(uint64)missing)
                elog(ERROR,"%s: locked empty-cell deletion did not preserve target cardinality",label);
            deleted=(int64)SPI_processed;
        }
        Datum result[2]={Int64GetDatum(written),Int64GetDatum(deleted)};
        SPI_finish();
        PG_RETURN_ARRAYTYPE_P(construct_array(result,2,INT8OID,8,true,'d'));
    }
    folds.seen_array=NULL;
    folds.rating_array=construct_array(priors->ratings,subjects.n,INT8OID,8,true,'d');
    folds.rd_array=construct_array(priors->rds,subjects.n,INT8OID,8,true,'d');
    folds.volatility_array=construct_array(priors->volatilities,subjects.n,INT8OID,8,true,'d');
    affected=laplace_fold_write_evidence_states(type16,ids,&subjects,&objects,evidence,&folds,label);
    SPI_finish();
    PG_RETURN_INT64(affected);
}

Datum
pg_laplace_consensus_refold_evidence_type(PG_FUNCTION_ARGS)
{
    return consensus_refold_evidence_type(fcinfo,false);
}

Datum
pg_laplace_consensus_refold_after_retraction_type(PG_FUNCTION_ARGS)
{
    return consensus_refold_evidence_type(fcinfo,true);
}
