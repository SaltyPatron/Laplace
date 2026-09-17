from pathlib import Path


def require_one(text: str, old: str, label: str) -> None:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected one match, found {count}")


# Native sink: delete global lock ownership, add exact P/A presence probes,
# and make every table insertion pure after the probe.
c = Path("extension/laplace_substrate/src/generated_stage_sink.c")
text = c.read_text()
old = '''enum SinkQuery { SQ_LOCK, SQ_EPOCH, SQ_PRESENCE, SQ_ENTITIES, SQ_PHYSICALITIES,
                 SQ_ATTESTATIONS, SQ_FOLD, SQ_MASKS, SQ_INTERPRETATIONS, SQ_COUNT };
static SPIPlanPtr sink_plans[SQ_COUNT];
static const char *const sink_keys[SQ_COUNT] = {
    "ingest.generated_stage_sink.lock", "ingest.generated_stage_sink.epoch",
    "ingest.generated_stage_sink.presence", "ingest.generated_stage_sink.entities",
    "ingest.generated_stage_sink.physicalities", "ingest.generated_stage_sink.attestations",
    "ingest.generated_stage_sink.fold", "ingest.generated_stage_sink.masks",
    "ingest.entity_interpretations"};'''
new = '''enum SinkQuery { SQ_EPOCH, SQ_ENTITY_PRESENCE, SQ_PHYSICALITY_PRESENCE,
                 SQ_ATTESTATION_PRESENCE, SQ_ENTITIES, SQ_PHYSICALITIES,
                 SQ_ATTESTATIONS, SQ_FOLD, SQ_MASKS, SQ_INTERPRETATIONS, SQ_COUNT };
static SPIPlanPtr sink_plans[SQ_COUNT];
static const char *const sink_keys[SQ_COUNT] = {
    "ingest.generated_stage_sink.epoch", "ingest.generated_stage_sink.presence",
    "ingest.generated_stage_sink.physicality_presence",
    "ingest.generated_stage_sink.attestation_presence",
    "ingest.generated_stage_sink.entities", "ingest.generated_stage_sink.physicalities",
    "ingest.generated_stage_sink.attestations", "ingest.generated_stage_sink.fold",
    "ingest.generated_stage_sink.masks", "ingest.entity_interpretations"};'''
require_one(text, old, "sink enum/key block")
text = text.replace(old, new, 1)
text = text.replace("sink_execute(s,SQ_PRESENCE,1,&type,&array,SPI_OK_SELECT);",
                    "sink_execute(s,SQ_ENTITY_PRESENCE,1,&type,&array,SPI_OK_SELECT);")
text = text.replace("if (!all_entity_rows && (row->duplicate || (table == 0 && row->present))) continue;",
                    "if (!all_entity_rows && (row->duplicate || row->present)) continue;")
text = text.replace("if (!t->rows[i].duplicate && !(table == 0 && t->rows[i].present)) ++count;",
                    "if (!t->rows[i].duplicate && !t->rows[i].present) ++count;")
text = text.replace("if (row == NULL || row->accepted || row->duplicate || (table == 0 && row->present))",
                    "if (row == NULL || row->accepted || row->duplicate || row->present)")

marker = '''/* Group exact accepted observations, never average opponent ratings or RD.
 * The canonical consensus owner consumes the entire rating-period group list. */'''
presence = r'''static void sink_physicality_presence(SinkState *s)
{
    SinkTable *t=&s->tables[1];
    size_t count=0;
    for(size_t i=0;i<t->count;++i) if(!t->rows[i].duplicate) ++count;
    if(count==0) return;
    Datum *ids=sink_alloc(s,sink_multiply(count,sizeof(Datum)));
    size_t at=0;
    for(size_t i=0;i<t->count;++i) if(!t->rows[i].duplicate)
        ids[at++]=sink_bytea(s,&t->rows[i].id,16);
    Datum array=sink_array(s,ids,NULL,count,BYTEAOID); Oid type=BYTEAARRAYOID;
    sink_charge(s,sink_multiply(count,sizeof(HeapTupleData)+128));
    sink_execute(s,SQ_PHYSICALITY_PRESENCE,1,&type,&array,SPI_OK_SELECT);
    if(SPI_processed>count || (SPI_processed && SPI_tuptable==NULL))
        sink_invalid("invalid physicality presence cardinality");
    for(uint64 i=0;i<SPI_processed;++i) {
        bool is_null; Datum value=SPI_getbinval(SPI_tuptable->vals[i],SPI_tuptable->tupdesc,1,&is_null);
        if(is_null) sink_invalid("null present physicality");
        bytea *bytes=DatumGetByteaPP(value); hash128_t id;
        if(VARSIZE_ANY_EXHDR(bytes)!=16) sink_invalid("invalid present physicality identity");
        memcpy(&id,VARDATA_ANY(bytes),16);
        SinkRow *row=sink_find(t,&id);
        if(row==NULL || row->duplicate) sink_invalid("unrequested physicality presence");
        row->present=true;
    }
    sink_clear_result();
}

static void sink_attestation_presence(SinkState *s)
{
    SinkTable *t=&s->tables[2];
    size_t count=0;
    for(size_t i=0;i<t->count;++i) if(!t->rows[i].duplicate) ++count;
    if(count==0) return;
    Datum *ids=sink_alloc(s,sink_multiply(count,sizeof(Datum)));
    Datum *types=sink_alloc(s,sink_multiply(count,sizeof(Datum)));
    Datum *subjects=sink_alloc(s,sink_multiply(count,sizeof(Datum)));
    size_t at=0;
    for(size_t i=0;i<t->count;++i) {
        SinkRow *row=&t->rows[i]; if(row->duplicate) continue;
        ids[at]=sink_bytea(s,&row->id,16);
        subjects[at]=sink_field_value(s,&row->fields[1],BYTEAOID);
        types[at]=sink_field_value(s,&row->fields[2],BYTEAOID); ++at;
    }
    Datum values[3]={sink_array(s,ids,NULL,count,BYTEAOID),
                     sink_array(s,types,NULL,count,BYTEAOID),
                     sink_array(s,subjects,NULL,count,BYTEAOID)};
    Oid argtypes[3]={BYTEAARRAYOID,BYTEAARRAYOID,BYTEAARRAYOID};
    sink_charge(s,sink_multiply(count,sizeof(HeapTupleData)+128));
    sink_execute(s,SQ_ATTESTATION_PRESENCE,3,argtypes,values,SPI_OK_SELECT);
    if(SPI_processed>count || (SPI_processed && SPI_tuptable==NULL))
        sink_invalid("invalid attestation presence cardinality");
    for(uint64 i=0;i<SPI_processed;++i) {
        bool is_null; Datum value=SPI_getbinval(SPI_tuptable->vals[i],SPI_tuptable->tupdesc,1,&is_null);
        if(is_null) sink_invalid("null present attestation");
        bytea *bytes=DatumGetByteaPP(value); hash128_t id;
        if(VARSIZE_ANY_EXHDR(bytes)!=16) sink_invalid("invalid present attestation identity");
        memcpy(&id,VARDATA_ANY(bytes),16);
        SinkRow *row=sink_find(t,&id);
        if(row==NULL || row->duplicate) sink_invalid("unrequested attestation presence");
        row->present=true;
    }
    sink_clear_result();
}

''' + marker
require_one(text, marker, "presence insertion marker")
text = text.replace(marker, presence, 1)

old_lock = '''void laplace_generated_stage_sink_lock(void)
{
    sink_require_isolation();
    SinkState state;
    memset(&state,0,sizeof(state));
    state.limits.maximum_operations=2;
    if (SPI_connect()!=SPI_OK_CONNECT) elog(ERROR,"generated stage sink: SPI_connect failed");
    sink_execute(&state,SQ_LOCK,0,NULL,NULL,SPI_OK_SELECT);
    sink_clear_result();
    if (SPI_finish()!=SPI_OK_FINISH) elog(ERROR,"generated stage sink: SPI_finish failed");
}

/* Ordinary entity INSERT/COPY uses the same bounded transaction lock as
 * generated native writes. Keep the SQL key and isolation rule in one owner. */
PG_FUNCTION_INFO_V1(pg_laplace_entity_write_lock);
Datum pg_laplace_entity_write_lock(PG_FUNCTION_ARGS)
{
    laplace_generated_stage_sink_lock();
    PG_RETURN_VOID();
}
'''
new_lock = '''void laplace_generated_stage_sink_lock(void)
{
    /* Compatibility entry for the session caller: validate isolation only.
     * Exact-row conflicts abort and retry the whole transaction. */
    sink_require_isolation();
}
'''
require_one(text, old_lock, "global lock function block")
text = text.replace(old_lock, new_lock, 1)
old_call = '''        sink_parse(s,stages,stage_count);
        if (SPI_connect()!=SPI_OK_CONNECT) elog(ERROR,"generated stage sink: SPI_connect failed");
        sink_execute(s,SQ_LOCK,0,NULL,NULL,SPI_OK_SELECT);
        sink_clear_result();
        /* SPI-owned temporary allocations remain under owner until SPI_finish;
'''
new_call = '''        sink_parse(s,stages,stage_count);
        if (SPI_connect()!=SPI_OK_CONNECT) elog(ERROR,"generated stage sink: SPI_connect failed");
        /* SPI-owned temporary allocations remain under owner until SPI_finish;
'''
require_one(text, old_call, "main sink lock call")
text = text.replace(old_call, new_call, 1)
old_write = '''        sink_validate_bodies(s,stages,stage_count);
        if (total != 0) {
            sink_execute(s,SQ_EPOCH,0,NULL,NULL,SPI_OK_SELECT);
            (void)sink_scalar();
            sink_insert(s,0);
            sink_interpretations(s);
            for (unsigned t=1;t<3;++t) sink_insert(s,t);
            sink_fold(s);
        }
'''
new_write = '''        sink_validate_bodies(s,stages,stage_count);
        if (total != 0) {
            /* Same probe-then-write law as canonical working-set apply. A
             * post-probe race raises 23505; the caller retries this transaction
             * and re-probes rather than arbitrating identity inside INSERT. */
            sink_physicality_presence(s);
            sink_attestation_presence(s);
            sink_execute(s,SQ_EPOCH,0,NULL,NULL,SPI_OK_SELECT);
            (void)sink_scalar();
            sink_insert(s,0);
            sink_interpretations(s);
            for (unsigned t=1;t<3;++t) sink_insert(s,t);
            sink_fold(s);
        }
'''
require_one(text, old_write, "main sink write block")
text = text.replace(old_write, new_write, 1)
if "SQ_LOCK" in text or "pg_laplace_entity_write_lock" in text:
    raise SystemExit("global generated-sink lock authority remains")
c.write_text(text)

# Header claims the actual isolation-only behavior.
h = Path("extension/laplace_substrate/src/generated_stage_sink.h")
ht = h.read_text()
ht = ht.replace("""    /* Actual sink SPI prepares + executions. Existing consensus/mask owners'\n     * internal SPI operations are outside this counter; their input cardinality\n     * is bounded above by inserted attestations. This includes the sink's\n     * reentrant writer lock. A caller's earlier explicit lock remains separate. */""",
"""    /* Actual sink SPI prepares + executions. Existing consensus/mask owners'\n     * internal SPI operations are outside this counter; their input cardinality\n     * is bounded above by inserted attestations. */""")
ht = ht.replace("""/* Call before locking a session row. The existing shared apply lock is\n * transaction scoped and reentrant. This operation owns a nested SPI frame.\n * READ COMMITTED is required; callers pin provider snapshots AFTER this lock. */""",
"""/* Compatibility isolation check used before session-row admission. It does\n * not acquire a database lock. READ COMMITTED is required so transaction retry\n * can re-probe after a concurrent canonical writer wins. */""")
h.write_text(ht)

# Catalog: one facet owner, explicit presence probes, pure inserts.
q = Path("engine/core/src/sql_catalog.def")
qt = q.read_text()
start = qt.index('SQL_QUERY("ingest.entity_interpretations"')
end = qt.index('SQL_QUERY("ingest.generated_stage_sink.epoch"', start)
qt = qt[:start] + '''SQL_QUERY("ingest.entity_interpretations", "bytea[],int2[],bytea[],bytea[],bool[]",\n    "SELECT laplace.entity_interpretations_publish($1,$2,$3,$4,$5)")\n''' + qt[end:]
qt = qt.replace('''SQL_QUERY("ingest.generated_stage_sink.lock", "",\n    "SELECT pg_advisory_xact_lock(hashtextextended('laplace_apply_batch',0))")\n''', '')
presence_query = '''SQL_QUERY("ingest.generated_stage_sink.presence", "bytea[]",\n    "SELECT DISTINCT id FROM laplace.entities WHERE id=ANY($1)")\n'''
new_presence_queries = presence_query + '''SQL_QUERY("ingest.generated_stage_sink.physicality_presence", "bytea[]",\n    "SELECT id FROM laplace.physicalities WHERE id=ANY($1)")\nSQL_QUERY("ingest.generated_stage_sink.attestation_presence", "bytea[],bytea[],bytea[]",\n    "SELECT a.id FROM laplace.attestations a JOIN unnest($1,$2,$3) r(id,type_id,subject_id) "\n    "ON a.id=r.id AND a.type_id=r.type_id AND a.subject_id=r.subject_id")\n'''
require_one(qt, presence_query, "catalog presence block")
qt = qt.replace(presence_query, new_presence_queries, 1)
entity_insert = '''    "INSERT INTO laplace.entities(id,tier,type_id,first_observed_by) SELECT r.* FROM unnest($1,$2,$3,$4) r(id,tier,type_id,source) WHERE NOT EXISTS(SELECT 1 FROM laplace.entities e WHERE e.id=r.id) ORDER BY r.id ON CONFLICT(id) DO NOTHING RETURNING id")'''
require_one(qt, entity_insert, "entity conflict insert")
qt = qt.replace(entity_insert, '''    "INSERT INTO laplace.entities(id,tier,type_id,first_observed_by) SELECT r.* FROM unnest($1,$2,$3,$4) r(id,tier,type_id,source) ORDER BY r.id RETURNING id")''', 1)
physicality_insert = '''    "INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,n_constituents,alignment_residual,source_dim,observed_at) SELECT r.id,r.entity,r.type,public.ST_GeomFromEWKB(r.coord),r.hilbert,public.ST_GeomFromEWKB(r.trajectory),r.n,r.residual,r.dim,r.observed FROM unnest($1,$2,$3,$4,$5,$6,$7,$8,$9,$10) r(id,entity,type,coord,hilbert,trajectory,n,residual,dim,observed) ORDER BY r.id ON CONFLICT(id) DO NOTHING RETURNING id")'''
require_one(qt, physicality_insert, "physicality conflict insert")
qt = qt.replace(physicality_insert, '''    "INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,n_constituents,alignment_residual,source_dim,observed_at) SELECT r.id,r.entity,r.type,public.ST_GeomFromEWKB(r.coord),r.hilbert,public.ST_GeomFromEWKB(r.trajectory),r.n,r.residual,r.dim,r.observed FROM unnest($1,$2,$3,$4,$5,$6,$7,$8,$9,$10) r(id,entity,type,coord,hilbert,trajectory,n,residual,dim,observed) ORDER BY r.id RETURNING id")''', 1)
attestation_insert = '''    "INSERT INTO laplace.attestations(id,subject_id,type_id,object_id,source_id,context_id,outcome,last_observed_at,observation_count,sum_score_fp1e9,opponent_rd_fp1e9,opponent_rating_fp1e9,fold_replayable,highway_mask) SELECT r.* FROM unnest($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14) r(id,subject,type,object,source,context,outcome,observed,games,score,rd,rating,replayable,mask) ORDER BY r.type,r.subject,r.id ON CONFLICT(id,type_id,subject_id) DO NOTHING RETURNING id")'''
require_one(qt, attestation_insert, "attestation conflict insert")
qt = qt.replace(attestation_insert, '''    "INSERT INTO laplace.attestations(id,subject_id,type_id,object_id,source_id,context_id,outcome,last_observed_at,observation_count,sum_score_fp1e9,opponent_rd_fp1e9,opponent_rating_fp1e9,fold_replayable,highway_mask) SELECT r.* FROM unnest($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14) r(id,subject,type,object,source,context,outcome,observed,games,score,rd,rating,replayable,mask) ORDER BY r.type,r.subject,r.id RETURNING id")''', 1)
block_start = qt.index("/* Native generated-stage deposit")
block_end = qt.index("/* Direct indexed containment", block_start)
block = qt[block_start:block_end]
if "ON CONFLICT" in block or "advisory" in block:
    raise SystemExit("generated-stage catalog still arbitrates conflicts")
q.write_text(qt)

# Conversation is the production owner of this native sink. Retry the entire
# acceptance transaction on the same concurrency states as canonical COPY.
w = Path("app/Laplace.Substrate/Crud/Npgsql/ConsensusAccumulatingWriter.cs")
wt = w.read_text()
old_method = '''    public Task<ApplyResult> ApplyConversationTurnAsync(
        SubstrateChange change, Hash128 sessionId, IReadOnlyList<Hash128> turnIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(turnIds);
        if (sessionId == Hash128.Zero || turnIds.Count == 0)
            throw new ArgumentException("A conversation append requires a session and ordered turn ids.");
        var ids = turnIds.Select(id => id.ToBytes()).ToArray();
        return ApplyCoreAsync(
            [change], workingSet: true, append: false, default,
            reconciliation: null, precommitVerifier: null, ct,
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand(
                    SqlCatalog.Get("conversation.append_turns").Text,
                    connection, transaction);
                command.Parameters.AddWithValue(NpgsqlDbType.Bytea, sessionId.ToBytes());
                command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, ids);
                command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz,
                    change.Metadata.BuiltAt.ToUniversalTime());
                command.Parameters.AddWithValue(NpgsqlDbType.Bytea, change.Metadata.IntentId.ToBytes());
                long physicalityBudget = IngestSizing.ResolveWorkingSetBudgetBytes();
                command.Parameters.AddWithValue(NpgsqlDbType.Bigint, physicalityBudget);
                command.Parameters.AddWithValue(NpgsqlDbType.Integer, 512);
                command.Parameters.AddWithValue(NpgsqlDbType.Bigint, physicalityBudget / MemoryTopology.Hash128Bytes);
                void OnSessionNotice(object sender, NpgsqlNoticeEventArgs notice)
                {
                    const string prefix = "session descriptor view unavailable; transaction pending: ";
                    if (notice.Notice.MessageText.StartsWith(prefix, StringComparison.Ordinal))
                        _log.LogInformation("SESSION_PHYSICALITY_VIEW transaction_pending=true receipt={Receipt}",
                            notice.Notice.MessageText[prefix.Length..]);
                }
                // The native appender keeps its scalar turn-count ABI. Expose its
                // bounded missing-view receipt for this operation and detach on
                // failure as well as success before the connection is reused.
                connection.Notice += OnSessionNotice;
                try { await command.ExecuteScalarAsync(token).ConfigureAwait(false); }
                finally { connection.Notice -= OnSessionNotice; }
            });
    }
'''
require_one(wt, old_method, "conversation retry owner")
new_method = '''    public async Task<ApplyResult> ApplyConversationTurnAsync(
        SubstrateChange change, Hash128 sessionId, IReadOnlyList<Hash128> turnIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(turnIds);
        if (sessionId == Hash128.Zero || turnIds.Count == 0)
            throw new ArgumentException("A conversation append requires a session and ordered turn ids.");
        var ids = turnIds.Select(id => id.ToBytes()).ToArray();
        var retry = Laplace.Ingestion.TransientErrorRetryPolicy.ConcurrencyRetry;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await ApplyCoreAsync(
                    [change], workingSet: true, append: false, default,
                    reconciliation: null, precommitVerifier: null, ct,
                    async (connection, transaction, token) =>
                    {
                        await using var command = new NpgsqlCommand(
                            SqlCatalog.Get("conversation.append_turns").Text,
                            connection, transaction);
                        command.Parameters.AddWithValue(NpgsqlDbType.Bytea, sessionId.ToBytes());
                        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, ids);
                        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz,
                            change.Metadata.BuiltAt.ToUniversalTime());
                        command.Parameters.AddWithValue(NpgsqlDbType.Bytea, change.Metadata.IntentId.ToBytes());
                        long physicalityBudget = IngestSizing.ResolveWorkingSetBudgetBytes();
                        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, physicalityBudget);
                        command.Parameters.AddWithValue(NpgsqlDbType.Integer, 512);
                        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, physicalityBudget / MemoryTopology.Hash128Bytes);
                        void OnSessionNotice(object sender, NpgsqlNoticeEventArgs notice)
                        {
                            const string prefix = "session descriptor view unavailable; transaction pending: ";
                            if (notice.Notice.MessageText.StartsWith(prefix, StringComparison.Ordinal))
                                _log.LogInformation("SESSION_PHYSICALITY_VIEW transaction_pending=true receipt={Receipt}",
                                    notice.Notice.MessageText[prefix.Length..]);
                        }
                        connection.Notice += OnSessionNotice;
                        try { await command.ExecuteScalarAsync(token).ConfigureAwait(false); }
                        finally { connection.Notice -= OnSessionNotice; }
                    }).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt + 1 < retry.MaxAttempts && retry.IsTransient(ex))
            {
                TimeSpan delay = retry.DelayBeforeAttempt(attempt, Random.Shared);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }
'''
w.write_text(wt.replace(old_method, new_method, 1))

# Controlled native SPI now models presence/replay instead of partial conflict acceptance.
t = Path("extension/laplace_substrate/tests/generated_stage_sink_native_probe.c")
tt = t.read_text()
tt = tt.replace("static bool omit_reference,all_present,accept_none;\nstatic size_t accept_limit=SIZE_MAX;",
                "static bool omit_reference,all_present;")
tt = tt.replace("if(query==SQ_PRESENCE) {", "if(query==SQ_ENTITY_PRESENCE) {")
insert_point = "    if(query>=SQ_ENTITIES && query<=SQ_ATTESTATIONS) {\n"
require_one(tt, insert_point, "native SPI insert point")
tt = tt.replace(insert_point, '''    if(query==SQ_PHYSICALITY_PRESENCE || query==SQ_ATTESTATION_PRESENCE) {
        size_t n=array_count(values[0]); Datum *ids=array_values(values[0]);
        if(query==SQ_ATTESTATION_PRESENCE)
            CHECK(array_count(values[1])==n && array_count(values[2])==n);
        if(all_present) for(size_t i=0;i<n;++i) result_add(ids[i]);
        return SPI_OK_SELECT;
    }
''' + insert_point, 1)
old_partial = '''        if(!(query==SQ_ATTESTATIONS && accept_none))
            for(size_t i=0;i<n && (query!=SQ_ATTESTATIONS || i<accept_limit);++i) {
                result_add(ids[i]);
                if(query==SQ_ATTESTATIONS && i==0)
                    memcpy(&first_accepted,VARDATA_ANY(DatumGetByteaPP(ids[i])),16);
            }'''
require_one(tt, old_partial, "partial conflict acceptance")
tt = tt.replace(old_partial, '''        for(size_t i=0;i<n;++i) {
            result_add(ids[i]);
            if(query==SQ_ATTESTATIONS && i==0)
                memcpy(&first_accepted,VARDATA_ANY(DatumGetByteaPP(ids[i])),16);
        }''', 1)
tt = tt.replace("CHECK(query==SQ_EPOCH || query==SQ_LOCK);", "CHECK(query==SQ_EPOCH);")
tt = tt.replace("accept_none=all_present=omit_reference=interpretation_missing=false;accept_limit=SIZE_MAX;",
                "all_present=omit_reference=interpretation_missing=false;")
tt = tt.replace('''    reset_spi();sink_validate_bodies(s,stages,1);
    CHECK(s->receipt.logical_work==4 && s->receipt.stored_vertices==4);
    sink_insert(s,0);sink_interpretations(s);''', '''    reset_spi();sink_validate_bodies(s,stages,1);
    CHECK(s->receipt.logical_work==4 && s->receipt.stored_vertices==4);
    sink_physicality_presence(s);sink_attestation_presence(s);
    sink_insert(s,0);sink_interpretations(s);''')
tt = tt.replace('''    CHECK(query_calls[SQ_PRESENCE]==1 && query_calls[SQ_ENTITIES]==1 && query_calls[SQ_PHYSICALITIES]==1 &&
          query_calls[SQ_ATTESTATIONS]==1 && query_calls[SQ_FOLD]==1 && query_calls[SQ_MASKS]==1);''', '''    CHECK(query_calls[SQ_ENTITY_PRESENCE]==1 && query_calls[SQ_PHYSICALITY_PRESENCE]==1 &&
          query_calls[SQ_ATTESTATION_PRESENCE]==1 && query_calls[SQ_ENTITIES]==1 &&
          query_calls[SQ_PHYSICALITIES]==1 && query_calls[SQ_ATTESTATIONS]==1 &&
          query_calls[SQ_FOLD]==1 && query_calls[SQ_MASKS]==1);''')
tt = tt.replace("CHECK(s->receipt.operations==14 && prepares==7);", "CHECK(s->receipt.operations==18 && prepares==9);")
tt = tt.replace('''    reset_spi();all_present=accept_none=true;
    s=probe_state(stages,1);sink_parse(s,stages,1);sink_validate_bodies(s,stages,1);
    sink_insert(s,0);sink_interpretations(s);
    for(unsigned i=1;i<3;++i) { sink_insert(s,i); }''', '''    reset_spi();all_present=true;
    s=probe_state(stages,1);sink_parse(s,stages,1);sink_validate_bodies(s,stages,1);
    sink_physicality_presence(s);sink_attestation_presence(s);
    sink_insert(s,0);sink_interpretations(s);
    for(unsigned i=1;i<3;++i) { sink_insert(s,i); }''')
partial_block = '''    /* Only the exact subset returned by INSERT participates in the fold. */
    reset_spi();accept_limit=1;s=probe_state(stages,1);sink_parse(s,stages,1);
    sink_validate_bodies(s,stages,1);sink_insert(s,2);sink_fold(s);
    SinkRow *accepted=sink_find(&s->tables[2],&first_accepted);CHECK(accepted!=NULL);
    CHECK(actual_games==sink_integer(&accepted->fields[8]) && actual_score==sink_integer(&accepted->fields[9]));
    CHECK(actual_groups==1 && s->receipt.inserted_rows[2]==1);sink_cleanup(s);

'''
require_one(tt, partial_block, "partial-acceptance regression")
tt = tt.replace(partial_block, "", 1)
tt = tt.replace("CHECK(query_calls[SQ_PRESENCE]==0);", "CHECK(query_calls[SQ_ENTITY_PRESENCE]==0);")
tt = tt.replace("actual\n     * reentrant lock, prepare and execution receipt", "explicit\n     * presence probes, prepare and execution receipt")
tt = tt.replace("CHECK(query_calls[SQ_LOCK]==1 && query_calls[SQ_EPOCH]==1);",
                "CHECK(query_calls[SQ_EPOCH]==1 && query_calls[SQ_PHYSICALITY_PRESENCE]==1 && query_calls[SQ_ATTESTATION_PRESENCE]==1);")
tt = tt.replace('''    XactIsoLevel=XACT_REPEATABLE_READ;REFUSES(laplace_generated_stage_sink_lock(),"READ COMMITTED");
    XactIsoLevel=XACT_READ_COMMITTED;laplace_generated_stage_sink_lock();laplace_generated_stage_sink_lock();
    CHECK(query_calls[SQ_LOCK]==2);''', '''    XactIsoLevel=XACT_REPEATABLE_READ;REFUSES(laplace_generated_stage_sink_lock(),"READ COMMITTED");
    XactIsoLevel=XACT_READ_COMMITTED;laplace_generated_stage_sink_lock();laplace_generated_stage_sink_lock();''')
tt = tt.replace("CHECK(query_calls[SQ_LOCK]==0 && memcmp(&receipt,&four_stage_receipt,sizeof(receipt))==0);",
                "CHECK(memcmp(&receipt,&four_stage_receipt,sizeof(receipt))==0);")
if "SQ_LOCK" in tt or "accept_limit" in tt or "accept_none" in tt:
    raise SystemExit("native probe retained conflict/lock simulation")
t.write_text(tt)

# Architecture ratchet.
gate = Path("app/Laplace.Substrate.Tests/Abstractions/GeneratedStageSinkArchitectureTests.cs")
gate.write_text(r'''using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class GeneratedStageSinkArchitectureTests
{
    [Fact]
    public void GeneratedSinkUsesProbeThenPureWriteAndConversationRetry()
    {
        Assert.True(Laplace.Engine.Core.LaplaceInstall.TryRepoRoot(out string root));
        string sink = File.ReadAllText(Path.Combine(root,"extension","laplace_substrate","src","generated_stage_sink.c"));
        string catalog = File.ReadAllText(Path.Combine(root,"engine","core","src","sql_catalog.def"));
        string writer = File.ReadAllText(Path.Combine(root,"app","Laplace.Substrate","Crud","Npgsql","ConsensusAccumulatingWriter.cs"));
        Assert.DoesNotContain("SQ_LOCK", sink, StringComparison.Ordinal);
        Assert.DoesNotContain("pg_laplace_entity_write_lock", sink, StringComparison.Ordinal);
        int start = catalog.IndexOf("/* Native generated-stage deposit", StringComparison.Ordinal);
        int end = catalog.IndexOf("/* Direct indexed containment", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string block = catalog[start..end];
        Assert.DoesNotContain("ON CONFLICT", block, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("advisory", block, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("physicality_presence", block, StringComparison.Ordinal);
        Assert.Contains("attestation_presence", block, StringComparison.Ordinal);
        Assert.Contains("TransientErrorRetryPolicy.ConcurrencyRetry", writer, StringComparison.Ordinal);
    }
}
''')
