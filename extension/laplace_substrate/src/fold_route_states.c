/*
 * fold_route_states.c — Period array unpacking, native Glicko-2 batch folding,
 * attestation replay validation, and cell identity composition.
 */
#include "fold_route_internal.h"

PG_FUNCTION_INFO_V1(pg_laplace_attestation_merge);
PG_FUNCTION_INFO_V1(pg_laplace_attestation_merge_type);

void
laplace_fold_read_period_arrays(FunctionCallInfo fcinfo, int cell_count,
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

    laplace_fold_in_array(fcinfo, 8, INT8OID, 8, true, 'd', false, label,
                          &periods->offsets);
    laplace_fold_in_array(fcinfo, 9, INT8OID, 8, true, 'd', false, label,
                          &periods->opponents);
    laplace_fold_in_array(fcinfo, 10, INT8OID, 8, true, 'd', false, label,
                          &periods->phis);
    laplace_fold_in_array(fcinfo, 11, INT8OID, 8, true, 'd', false, label,
                          &periods->games);
    laplace_fold_in_array(fcinfo, 12, INT8OID, 8, true, 'd', false, label,
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

PeriodWindow
laplace_fold_period_window(const PeriodArrays *periods, const InArray *cell_opponents,
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
        window.opponents = laplace_fold_array_window(
            periods->opponents.array, periods->opponents.elems, NULL,
            periods->opponents.n, group_start, group_n,
            INT8OID, 8, true, 'd');
        window.phis = laplace_fold_array_window(
            periods->phis.array, periods->phis.elems, NULL,
            periods->phis.n, group_start, group_n,
            INT8OID, 8, true, 'd');
        window.games = laplace_fold_array_window(
            periods->games.array, periods->games.elems, NULL,
            periods->games.n, group_start, group_n,
            INT8OID, 8, true, 'd');
        window.sums = laplace_fold_array_window(
            periods->sums.array, periods->sums.elems, NULL,
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
            ? laplace_fold_array_window(cell_opponents->array, cell_opponents->elems, NULL,
                                        cell_opponents->n, group_start, group_n,
                                        INT8OID, 8, true, 'd')
            : laplace_fold_neutral_opponent_array(cell_n);
        window.phis = laplace_fold_array_window(
            cell_phis->array, cell_phis->elems, NULL,
            cell_phis->n, group_start, group_n,
            INT8OID, 8, true, 'd');
        window.games = laplace_fold_array_window(
            cell_games->array, cell_games->elems, NULL,
            cell_games->n, group_start, group_n,
            INT8OID, 8, true, 'd');
        window.sums = laplace_fold_array_window(
            cell_sums->array, cell_sums->elems, NULL,
            cell_sums->n, group_start, group_n,
            INT8OID, 8, true, 'd');
    }
    window.starts = construct_array(starts, cell_n, INT4OID, 4, true, 'i');
    window.ends = construct_array(ends, cell_n, INT4OID, 4, true, 'i');
    return window;
}

/* Error-detail only: identify the mapped code the backend actually executes,
 * including the Windows execution module's statically linked core. */
static char *
fold_function_mapping(uintptr_t address)
{
#ifdef _WIN32
    HMODULE module;
    wchar_t path[32768];
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                           GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           (LPCWSTR) address, &module))
        return psprintf("unavailable (GetModuleHandleExW error=%lu)",
                        (unsigned long) GetLastError());
    DWORD length = GetModuleFileNameW(module, path, lengthof(path));
    if (length == 0 || length >= lengthof(path))
        return psprintf("unavailable (GetModuleFileNameW error=%lu length=%lu)",
                        (unsigned long) GetLastError(), (unsigned long) length);
    int bytes = WideCharToMultiByte(CP_UTF8, 0, path, length, NULL, 0, NULL, NULL);
    if (bytes <= 0) return pstrdup("unavailable (module path UTF-8 conversion)");
    char *result = palloc(bytes + 1);
    if (WideCharToMultiByte(CP_UTF8, 0, path, length, result, bytes, NULL, NULL) != bytes)
        return pstrdup("unavailable (module path UTF-8 conversion)");
    result[bytes] = '\0';
    return result;
#else
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
#endif
}

/* Fold one type run in one native pass: matched cells from their locked
 * prior, novel cells from the neutral prior. A cell with one period group uses
 * the uniform-period kernel; a cell with several uses the grouped-period
 * kernel. Rows flagged in `recomputed` already hold their result and pass
 * through. */
void
laplace_fold_run_states(const InArray *phis, const InArray *opps,
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
                    uintptr_t route_address = (uintptr_t)&laplace_fold_run_states;
                    char *core_mapping = fold_function_mapping(core_address);
                    char *route_mapping = fold_function_mapping(route_address);
                    ereport(ERROR,
                            (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                             errmsg("%s: native rating-period update failed", label),
                             errdetail("result=%d cell_index=%d matched=%s "
                                       "exact_period=%s period_start=%d "
                                       "prior_rating=%" PRId64 " prior_rd=%" PRId64 " prior_volatility=%" PRId64 " "
                                       "opponent_rating=%" PRId64 " opponent_rd=%" PRId64 " "
                                       "games=%" PRId64 " sum_score=%" PRId64 " tau=%" PRId64 " "
                                       "backend_pid=%d core_address=%" PRIxPTR
                                       " core_mapping=[%s] route_address=%" PRIxPTR
                                       " route_mapping=[%s]",
                                       fold_result, run_start + i,
                                       matched[i] ? "true" : "false",
                                       periods->exact ? "true" : "false", period_start,
                                       (int64_t) input[0], (int64_t) input[1], (int64_t) input[2],
                                       (int64_t) input[3], (int64_t) input[4], (int64_t) input[5],
                                       (int64_t) input[6], (int64_t) LAPLACE_GLICKO2_DEFAULT_TAU,
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

/* attestation_merge — replay of already-identified attestations */
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
        (void) laplace_fold_bytea16(PG_GETARG_DATUM(0), label);
    }
    else
        laplace_fold_in_array(fcinfo, 1, BYTEAOID, -1, false, 'i', false, label, &types);
    laplace_fold_in_array(fcinfo, single_type ? 1 : 0, BYTEAOID, -1, false, 'i', false, label, &ids);
    laplace_fold_in_array(fcinfo, 2, BYTEAOID, -1, false, 'i', false, label, &subjects);
    laplace_fold_in_array(fcinfo, 3, INT8OID, 8, true, 'd', false, label, &games);
    laplace_fold_in_array(fcinfo, 4, INT8OID, 8, true, 'd', false, label, &sums);
    laplace_fold_in_array(fcinfo, 5, TIMESTAMPTZOID, 8, true, 'd', false, label, &ts);
    laplace_fold_in_array(fcinfo, 6, BOOLOID, 1, true, 'c', false, label, &fold_replayable);
    if ((!single_type && types.n != ids.n) || subjects.n != ids.n ||
        games.n != ids.n || sums.n != ids.n || ts.n != ids.n ||
        fold_replayable.n != ids.n)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: parallel arrays must share length", label)));
    for (i = 0; i < ids.n; ++i)
    {
        (void) laplace_fold_bytea16(ids.elems[i], label);
        (void) laplace_fold_bytea16(subjects.elems[i], label);
        if (!single_type)
            (void) laplace_fold_bytea16(types.elems[i], label);
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

Datum *
laplace_fold_typed_cell_ids(const uint8_t *type16, const InArray *subjects,
                            const InArray *objects, const char *label)
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
        hash128_t h;
        bytea    *out;
        bool      found;

        h = strand_id(laplace_fold_bytea16(subjects->elems[i], label), type16,
                      objects->nulls[i] ? NULL : laplace_fold_bytea16(objects->elems[i], label));
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

ArrayType *
laplace_fold_retract_evidence_cells(
    const uint8_t *type16, Datum type_datum, const Datum *cell_ids,
    const InArray *subjects, const InArray *objects,
    const FoldPriorStates *priors, const FoldEvidenceStates *evidence,
    const char *label)
{
    Datum *kept_ids = palloc(sizeof(Datum) * subjects->n);
    Datum *kept_subjects = palloc(sizeof(Datum) * subjects->n);
    Datum *kept_objects = palloc(sizeof(Datum) * subjects->n);
    bool *kept_nulls = palloc(sizeof(bool) * subjects->n);
    Datum *removed_ids = palloc(sizeof(Datum) * subjects->n);
    Datum *removed_subjects = palloc(sizeof(Datum) * subjects->n);
    FoldEvidenceStates kept;
    FoldStateArrays folds = {0};
    FoldPriorStates *states = laplace_fold_prior_states_create(subjects->n);
    kept.recomputed = palloc(sizeof(bool) * subjects->n);
    kept.counts = palloc(sizeof(Datum) * subjects->n);
    kept.timestamps = palloc(sizeof(Datum) * subjects->n);
    int live = 0, missing = 0;
    for (int i = 0; i < subjects->n; ++i)
    {
        if (!evidence->recomputed[i])
        {
            removed_ids[missing] = cell_ids[i];
            removed_subjects[missing++] = subjects->elems[i];
            continue;
        }
        kept_ids[live] = cell_ids[i];
        kept_subjects[live] = subjects->elems[i];
        kept_objects[live] = objects->elems[i];
        kept_nulls[live] = objects->nulls[i];
        kept.recomputed[live] = true;
        kept.counts[live] = evidence->counts[i];
        kept.timestamps[live] = evidence->timestamps[i];
        states->ratings[live] = priors->ratings[i];
        states->rds[live] = priors->rds[i];
        states->volatilities[live] = priors->volatilities[i];
        ++live;
    }
    int64 written = 0, deleted = 0;
    if (live > 0)
    {
        InArray ss = {0}, oo = {0};
        ss.n = oo.n = live;
        ss.elems = kept_subjects;
        oo.elems = kept_objects;
        oo.nulls = kept_nulls;
        ss.array = construct_array(kept_subjects, live, BYTEAOID, -1, false, 'i');
        oo.array = construct_md_array(kept_objects, kept_nulls, 1, &live, (int[]){1}, BYTEAOID, -1, false, 'i');
        folds.rating_array = construct_array(states->ratings, live, INT8OID, 8, true, 'd');
        folds.rd_array = construct_array(states->rds, live, INT8OID, 8, true, 'd');
        folds.volatility_array = construct_array(states->volatilities, live, INT8OID, 8, true, 'd');
        written = laplace_fold_write_evidence_states(type16,
            construct_array(kept_ids, live, BYTEAOID, -1, false, 'i'), &ss, &oo, &kept, &folds, label);
    }
    if (missing > 0)
    {
        static SPIPlanPtr delete_plan = NULL;
        static const Oid args[3] = {BYTEAARRAYOID, BYTEAARRAYOID, BYTEAOID};
        if (delete_plan == NULL)
        {
            SPIPlanPtr plan = SPI_prepare(laplace_sql_query_text("consensus.delete_locked_empty_cells"), 3, (Oid *) args);
            if (plan == NULL || SPI_keepplan(plan) != 0)
                elog(ERROR, "%s: empty-cell delete plan preparation failed", label);
            delete_plan = plan;
        }
        Datum values[3] = {
            PointerGetDatum(construct_array(removed_ids, missing, BYTEAOID, -1, false, 'i')),
            PointerGetDatum(construct_array(removed_subjects, missing, BYTEAOID, -1, false, 'i')),
            type_datum};
        int rc = SPI_execute_plan(delete_plan, values, NULL, false, 0);
        if (rc != SPI_OK_DELETE || SPI_processed != (uint64) missing)
            elog(ERROR, "%s: locked empty-cell deletion did not preserve target cardinality", label);
        deleted = (int64) SPI_processed;
    }
    Datum result[2] = {Int64GetDatum(written), Int64GetDatum(deleted)};
    return construct_array(result, 2, INT8OID, 8, true, 'd');
}
