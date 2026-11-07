/*
 * fold_route_evidence.c — Evidence-backed consensus fold, durable testimony
 * batch reads, exact-leaf + COPY persistence, and retraction refolds.
 */
#include "fold_route_internal.h"

PG_FUNCTION_INFO_V1(pg_laplace_consensus_merge_evidence);

static HTAB *evidence_lock_plans = NULL;
static HTAB *evidence_write_plans = NULL;

static const char *EVIDENCE_LOCK_SQL =
    "INSERT INTO laplace.consensus AS c "
    " (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at) "
    "SELECT b.id,b.s,'\\x%s'::bytea,b.o,consensus.glicko2_neutral_mu(),"
    " consensus.glicko2_initial_rd(),consensus.glicko2_initial_volatility(),0,b.ts "
    "FROM unnest($1::bytea[],$2::bytea[],$3::bytea[],$4::timestamptz[]) AS b(id,s,o,ts) "
    "ORDER BY b.id,b.s "
    "ON CONFLICT (id,type_id,subject_id) DO UPDATE SET rating=c.rating WHERE false";

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

typedef struct EvidenceLeafPlan
{
    Oid owner;
    SPIPlanPtr plan;
} EvidenceLeafPlan;

static HTAB *evidence_leaf_plans = NULL;

static bool
evidence_direct_leaf_allowed(const LaplaceHashLeaves *leaves)
{
    if (pg_class_aclcheck(leaves->parent_oid, GetUserId(), ACL_SELECT) != ACLCHECK_OK ||
        check_enable_rls(leaves->parent_oid, InvalidOid, true) == RLS_ENABLED)
        return false;
    for (int i = 0; i < leaves->count; ++i)
        if (pg_class_aclcheck(leaves->leaf_oids[i], GetUserId(), ACL_SELECT) != ACLCHECK_OK ||
            check_enable_rls(leaves->leaf_oids[i], InvalidOid, true) == RLS_ENABLED)
            return false;
    return true;
}

static SPIPlanPtr
evidence_owner_plan(Oid owner, bool direct)
{
    EvidenceLeafPlan *entry;
    bool found;
    if (evidence_leaf_plans == NULL)
    {
        HASHCTL ctl;
        memset(&ctl, 0, sizeof(ctl));
        ctl.keysize = sizeof(Oid);
        ctl.entrysize = sizeof(EvidenceLeafPlan);
        ctl.hcxt = TopMemoryContext;
        evidence_leaf_plans = hash_create("consensus durable evidence owners", 64,
            &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    }
    entry = hash_search(evidence_leaf_plans, &owner, HASH_ENTER, &found);
    if (!found) entry->plan = NULL;
    if (entry->plan == NULL)
    {
        static const Oid args[4] =
            {BYTEAARRAYOID, BYTEAARRAYOID, BYTEAARRAYOID, INT4ARRAYOID};
        char *ns = get_namespace_name(get_rel_namespace(owner));
        char *name = get_rel_name(owner);
        StringInfoData relation, sql;
        if (ns == NULL || name == NULL)
            elog(ERROR, "consensus evidence owner disappeared");
        initStringInfo(&relation);
        appendStringInfo(&relation, "%s%s", direct ? "ONLY " : "",
            quote_qualified_identifier(ns, name));
        initStringInfo(&sql);
        appendStringInfo(&sql, laplace_sql_query_text("consensus.evidence_rows_batch"),
            relation.data, relation.data);
        SPIPlanPtr plan = SPI_prepare(sql.data, 4, (Oid *) args);
        if (plan == NULL || SPI_keepplan(plan) != 0)
            elog(ERROR, "consensus evidence owner plan preparation failed");
        entry->plan = plan;
        pfree(relation.data);
        pfree(sql.data);
    }
    return entry->plan;
}

void
laplace_fold_lock_evidence_targets(const uint8_t *type16, ArrayType *ids,
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
        Datum *empty_times=palloc0(sizeof(Datum)*subjects->n);
        vals[3]=PointerGetDatum(construct_array(
            empty_times,subjects->n,TIMESTAMPTZOID,8,true,'d'));
        pfree(empty_times);
    }
    SPIPlanPtr plan = laplace_fold_typed_plan(&evidence_lock_plans,
        "consensus evidence target locks",type16,EVIDENCE_LOCK_SQL,4,args);
    int rc = SPI_execute_plan(plan,vals,NULL,false,0);
    if (rc != SPI_OK_INSERT)
        ereport(ERROR,(errcode(ERRCODE_INTERNAL_ERROR),
                       errmsg("%s: evidence target locking failed: %s",
                              label,SPI_result_code_string(rc))));
}

FoldEvidenceStates *
laplace_fold_read_evidence_states_batch(const InArray *types, const InArray *subjects,
                                        const InArray *objects, const InArray *games,
                                        const InArray *ts, FoldPriorStates *priors,
                                        const char *label, bool replayable_only,
                                        bool require_all, bool matched_only)
{
    const LaplaceHashLeaves *leaves = laplace_hash_leaves("attestations", label);
    if (check_enable_rls(leaves->parent_oid, InvalidOid, true) == RLS_ENABLED)
        ereport(ERROR, (errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
            errmsg("%s: standing recomputation requires complete visibility of retained testimony", label)));
    bool direct = evidence_direct_leaf_allowed(leaves);
    int owners = direct ? leaves->count : 1;
    int *routes = palloc0(sizeof(int) * subjects->n);
    int *sizes = palloc0(sizeof(int) * owners);
    int *starts = palloc0(sizeof(int) * (owners + 1));
    int *fill = palloc0(sizeof(int) * owners);
    int *positions = palloc(sizeof(int) * subjects->n);
    FoldEvidenceStates *out = palloc(sizeof(*out));
    bool *visited = palloc0(sizeof(bool)*subjects->n);
    out->recomputed = palloc0(sizeof(bool)*subjects->n);
    out->counts = palloc0(sizeof(Datum)*subjects->n);
    out->timestamps = palloc0(sizeof(Datum)*subjects->n);
    if (games != NULL) memcpy(out->counts,games->elems,sizeof(Datum)*subjects->n);
    if (ts != NULL) memcpy(out->timestamps,ts->elems,sizeof(Datum)*subjects->n);
    if (direct) laplace_hash_leaf_route(leaves, subjects->elems, subjects->n, routes, label);
    for (int i = 0; i < subjects->n; ++i)
        if (!matched_only || priors->matched[i]) ++sizes[routes[i]];
    for (int i = 0; i < owners; ++i) starts[i + 1] = starts[i] + sizes[i];
    for (int i = 0; i < subjects->n; ++i)
        if (!matched_only || priors->matched[i])
            positions[starts[routes[i]] + fill[routes[i]]++] = i;

    MemoryContext scratch = AllocSetContextCreate(CurrentMemoryContext,
        "consensus evidence owner batch", ALLOCSET_DEFAULT_SIZES);
    for (int owner = 0; owner < owners; ++owner)
    {
        int n = sizes[owner];
        if (n == 0) continue;
        CHECK_FOR_INTERRUPTS();
        MemoryContext previous = MemoryContextSwitchTo(scratch);
        Datum *ss = palloc(sizeof(Datum) * n), *tt = palloc(sizeof(Datum) * n);
        Datum *oo = palloc(sizeof(Datum) * n), *ord = palloc(sizeof(Datum) * n);
        bool *nulls = palloc(sizeof(bool) * n);
        for (int k = 0; k < n; ++k)
        {
            int i = positions[starts[owner] + k];
            ss[k] = subjects->elems[i]; tt[k] = types->elems[i];
            oo[k] = objects->elems[i]; nulls[k] = objects->nulls[i];
            ord[k] = Int32GetDatum(i + 1);
        }
        Datum vals[4] = {
            PointerGetDatum(construct_array(ss, n, BYTEAOID, -1, false, 'i')),
            PointerGetDatum(construct_array(tt, n, BYTEAOID, -1, false, 'i')),
            PointerGetDatum(construct_md_array(oo, nulls, 1, &n, (int[]){1}, BYTEAOID, -1, false, 'i')),
            PointerGetDatum(construct_array(ord, n, INT4OID, 4, true, 'i'))};
        Oid oid = direct ? leaves->leaf_oids[owner] : leaves->parent_oid;
        int rc = SPI_execute_plan(evidence_owner_plan(oid, direct),vals,NULL,false,0);
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
                counts[groups] = DatumGetInt64(values[3]);
                if (counts[groups] <= 0)
                    ereport(ERROR, (errcode(ERRCODE_DATA_EXCEPTION),
                        errmsg("%s: non-positive durable observation count cannot be folded", label),
                        errdetail("cell_index=%d observation_count=" INT64_FORMAT, i, counts[groups])));
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
        if (opponents) pfree(opponents);
        if (phis) pfree(phis);
        if (counts) pfree(counts);
        if (sums) pfree(sums);
        SPI_freetuptable(SPI_tuptable);
        MemoryContextSwitchTo(previous);
        MemoryContextReset(scratch);
    }
    MemoryContextDelete(scratch);
    for (int i = 0; require_all && i < subjects->n; ++i)
        if (!visited[i])
            ereport(ERROR, (errcode(ERRCODE_DATA_EXCEPTION),
                errmsg("%s: target has no durable accepted testimony", label),
                errdetail("cell_index=%d", i)));
    pfree(visited);
    pfree(routes); pfree(sizes); pfree(starts); pfree(fill); pfree(positions);
    return out;
}

FoldEvidenceStates *
laplace_fold_read_evidence_states(const uint8_t *type16, const InArray *subjects,
                                  const InArray *objects, const InArray *games,
                                  const InArray *ts, FoldPriorStates *priors,
                                  const char *label, bool replayable_only, bool require_all)
{
    bytea *type = palloc(VARHDRSZ + 16);
    SET_VARSIZE(type, VARHDRSZ + 16);
    memcpy(VARDATA(type), type16, 16);
    InArray types;
    memset(&types, 0, sizeof(types));
    types.n = subjects->n;
    types.elems = palloc(sizeof(Datum) * types.n);
    for (int i = 0; i < types.n; ++i) types.elems[i] = PointerGetDatum(type);
    FoldEvidenceStates *out = laplace_fold_read_evidence_states_batch(&types, subjects, objects,
        games, ts, priors, label, replayable_only, require_all, false);
    pfree(types.elems); pfree(type);
    return out;
}

int64
laplace_fold_write_evidence_states(const uint8_t *type16, ArrayType *ids,
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
    SPIPlanPtr plan=laplace_fold_typed_plan(&evidence_write_plans,
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

void
laplace_fold_write_run(const uint8_t *type16, Datum type, const Datum *types, ArrayType *ids,
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
            PriorRouteEntry *route = laplace_fold_prior_route(type16, type, label);
            Datum *all_ids, *rat, *rds, *vol;
            bool *nul;
            int cnt;
            deconstruct_array(ids, BYTEAOID, -1, false, 'i', &all_ids, &nul, &cnt);
            deconstruct_array(folds->rating_array, INT8OID, 8, true, 'd', &rat, &nul, &cnt);
            deconstruct_array(folds->rd_array, INT8OID, 8, true, 'd', &rds, &nul, &cnt);
            deconstruct_array(folds->volatility_array, INT8OID, 8, true, 'd', &vol, &nul, &cnt);
            int owners = route->leaves->count;
            int *sizes = palloc0(sizeof(int) * owners);
            int *starts = palloc0(sizeof(int) * (owners + 1));
            int *fill = palloc0(sizeof(int) * owners);
            int *positions = palloc(sizeof(int) * matched_n);
            for (int i = 0; i < n; ++i)
                if (priors->matched[i]) ++sizes[priors->leaves[i]];
            for (int leaf = 0; leaf < owners; ++leaf)
                starts[leaf + 1] = starts[leaf] + sizes[leaf];
            for (int i = 0; i < n; ++i)
                if (priors->matched[i])
                {
                    int leaf = priors->leaves[i];
                    positions[starts[leaf] + fill[leaf]++] = i;
                }
            MemoryContext scratch = AllocSetContextCreate(CurrentMemoryContext,
                "consensus matched owner batch", ALLOCSET_DEFAULT_SIZES);
            for (int leaf = 0; leaf < owners; ++leaf)
            {
                int m = sizes[leaf];
                if (m == 0) continue;
                CHECK_FOR_INTERRUPTS();
                MemoryContext previous = MemoryContextSwitchTo(scratch);
                Datum *mid = palloc(sizeof(Datum) * m), *ms = palloc(sizeof(Datum) * m);
                Datum *mg = palloc(sizeof(Datum) * m), *mt = palloc(sizeof(Datum) * m);
                Datum *mr = palloc(sizeof(Datum) * m), *md = palloc(sizeof(Datum) * m);
                Datum *mv = palloc(sizeof(Datum) * m), *mc = palloc(sizeof(Datum) * m);
                for (int k = 0; k < m; ++k)
                {
                    int i = positions[starts[leaf] + k];
                    mid[k] = all_ids[i]; ms[k] = subjects->elems[i];
                    mg[k] = evidence->counts[i]; mt[k] = evidence->timestamps[i];
                    mr[k] = rat[i]; md[k] = rds[i]; mv[k] = vol[i];
                    mc[k] = BoolGetDatum(evidence->recomputed[i]);
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
                int rc = SPI_execute_plan(laplace_fold_update_leaf_plan(route, leaf, label), vals, NULL, false, 0);
                if (rc != SPI_OK_UPDATE || SPI_processed != (uint64) m)
                    ereport(ERROR, (errcode(ERRCODE_INTERNAL_ERROR),
                        errmsg("%s: exact-leaf update affected %lu of %d rows",
                               label, (unsigned long) SPI_processed, m)));
                done += SPI_processed;
                MemoryContextSwitchTo(previous);
                MemoryContextReset(scratch);
            }
            MemoryContextDelete(scratch);
            pfree(sizes); pfree(starts); pfree(fill); pfree(positions);
        }
        if (matched_n < (uint64) n)
        {
            Datum write_vals[9] = {
                PointerGetDatum(ids), PointerGetDatum(subjects->array),
                PointerGetDatum(objects->array), PointerGetDatum(counts),
                PointerGetDatum(times), PointerGetDatum(folds->seen_array),
                PointerGetDatum(folds->rating_array), PointerGetDatum(folds->rd_array),
                PointerGetDatum(folds->volatility_array)};
            uint64 inserted = 0;
            if (!laplace_consensus_copy_novel_batch(types, type, priors->leaves,
                                                    write_vals, n, &inserted))
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

void
laplace_fold_validate_evidence_delta(const InArray *phis, const InArray *games,
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

Datum
pg_laplace_consensus_merge_evidence(PG_FUNCTION_ARGS)
{
    const char *label = "consensus_merge_evidence";
    InArray subjects, types, objects, phis, games, sums, ts, opps;
    PeriodArrays periods;
    Datum *cell_ids;
    ArrayType *ids;
    FoldPriorStates *priors;
    FoldEvidenceStates *evidence;
    FoldStateArrays folds;
    HTAB *seen;
    HASHCTL ctl;
    int64 affected = 0;
    int run_start = 0;

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

    if (types.n != subjects.n || objects.n != subjects.n ||
        phis.n != subjects.n || games.n != subjects.n ||
        sums.n != subjects.n || ts.n != subjects.n ||
        (opps.n > 0 && opps.n != subjects.n))
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: parallel arrays must share length", label)));
    if (subjects.n == 0)
        PG_RETURN_INT64(0);

    laplace_fold_validate_evidence_delta(&phis, &games, &sums, &periods, label);
    if (IsolationUsesXactSnapshot())
        ereport(ERROR,
                (errcode(ERRCODE_T_R_SERIALIZATION_FAILURE),
                 errmsg("%s: retry the evidence transaction at READ COMMITTED", label)));

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(CellSeen);
    seen = hash_create("consensus_merge_evidence cell guard", subjects.n, &ctl,
                       HASH_ELEM | HASH_BLOBS);
    cell_ids = (Datum *) palloc(sizeof(Datum) * subjects.n);

    while (run_start < subjects.n)
    {
        const uint8_t *type16 = laplace_fold_bytea16(types.elems[run_start], label);
        int run_end = run_start;

        while (run_end < subjects.n &&
               memcmp(laplace_fold_bytea16(types.elems[run_end], label), type16, 16) == 0)
        {
            hash128_t h;
            bytea    *out;
            bool      found;

            h = strand_id(laplace_fold_bytea16(subjects.elems[run_end], label), type16,
                          objects.nulls[run_end] ? NULL : laplace_fold_bytea16(objects.elems[run_end], label));
            hash_search(seen, &h, HASH_ENTER, &found);
            if (found)
                ereport(ERROR,
                        (errcode(ERRCODE_CARDINALITY_VIOLATION),
                         errmsg("%s: duplicate cell in one call "
                                "(client-dedup contract violated)", label)));
            out = (bytea *) palloc(VARHDRSZ + 16);
            SET_VARSIZE(out, VARHDRSZ + 16);
            memcpy(VARDATA(out), &h, 16);
            cell_ids[run_end] = PointerGetDatum(out);
            ++run_end;
        }
        if (run_end < subjects.n &&
            memcmp(type16, laplace_fold_bytea16(types.elems[run_end], label), 16) > 0)
            ereport(ERROR,
                    (errcode(ERRCODE_DATA_EXCEPTION),
                     errmsg("%s: type ids must be bytewise sorted", label)));

        run_start = run_end;
    }
    hash_destroy(seen);
    ids = construct_array(cell_ids, subjects.n, BYTEAOID, -1, false, 'i');

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: SPI_connect failed", label)));

    priors = laplace_fold_read_run_priors(
        laplace_fold_bytea16(types.elems[0], label), types.elems[0],
        cell_ids, &subjects, 0, subjects.n, label);

    evidence = laplace_fold_read_evidence_states_batch(&types, &subjects, &objects,
        &games, &ts, priors, label, false, false, periods.exact);

    laplace_fold_run_states(&phis, &opps, &games, &sums, &periods,
                            0, subjects.n, priors, label,
                            &folds, evidence->recomputed);
    laplace_fold_write_run(laplace_fold_bytea16(types.elems[0], label),
                           types.elems[0], types.elems,
                           ids, &subjects, &objects, evidence, &folds, priors,
                           &affected, label);

    SPI_finish();
    PG_RETURN_INT64(affected);
}
