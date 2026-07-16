using global::Npgsql;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// The Rule #8 write protocol (.scratchpad/06_Engineering_Ruleset.txt): the
/// client already knows exactly what is novel (descent + hot caches decided
/// that before we got here), so the server's only remaining jobs are (1) a
/// bulk in-transaction verification of the claimed-novel ids — the guard
/// against a concurrent ingest having committed an overlapping subtree
/// between our unlocked descent and this transaction — and (2) pure COPY of
/// what survives, in entities → physicalities → attestations order. No temp
/// tables, no anti-join, no ON CONFLICT.
///
/// The verification probe is flat over the whole claimed-novel set, not
/// frontier-only: a concurrent ingest commits subtrees rooted at ITS roots,
/// which can sit strictly below our novel frontier (we hold novel sentence
/// S ⊃ word w; the other run committed w standalone — probing only S would
/// miss w and hit a PK violation).
///
/// Entity presence is checked with entities_stored_bitmap (perfcache fast
/// path OFF): this probe decides what gets written, and tier-0 codepoint
/// rows only exist because the unicode seed writes them through this lane —
/// answering their presence axiomatically would drop them from the write
/// list forever.
/// </summary>
public sealed partial class NpgsqlSubstrateWriter
{
    private const int ProbeChunkIds = 131_072;

    /// <summary>
    /// Rows below this stay on the fully-atomic single-transaction path;
    /// above it, COPY fans out across connections (per-table barriers keep
    /// entities durable before physicalities before attestations).
    /// </summary>
    private const int ParallelCopyMinRows = 65_536;

    internal static readonly int ApplyParallelism = CpuTopology.ResolveApplyPartitions();

    /// <summary>
    /// Run-scoped index cycle, active between BeginBulkRunAsync and
    /// CompleteBulkRunAsync. While active, qualifying applies drop
    /// secondaries but do NOT rebuild them — the rebuild happens once at
    /// run end. Only the apply lane touches this (applies are serialized
    /// by the runner and by the apply advisory lock), so no locking.
    /// </summary>
    private NpgsqlIndexCycle? _runCycle;

    public async Task BeginBulkRunAsync(CancellationToken ct = default)
    {
        // Recover any journaled drops a crashed prior run left behind
        // BEFORE this run makes its own cycling decisions.
        await NpgsqlIndexCycle.RecoverAsync(_ds, _log, ct);
        _runCycle = new NpgsqlIndexCycle(_ds, _log);
    }

    public async Task CompleteBulkRunAsync(CancellationToken ct = default)
    {
        var cycle = _runCycle;
        _runCycle = null;
        if (cycle is not null)
            await cycle.FinishAsync(ct);
    }

    /// <summary>
    /// Applies one whole working set in a single serialized transaction,
    /// claiming an idempotency token in laplace.ingest_flush_journal keyed
    /// by the change's intent hash. A retry after commit-ambiguity finds the
    /// token and returns a no-op instead of double-applying the additive
    /// attestation merges.
    /// </summary>
    public Task<ApplyResult> ApplyWorkingSetAsync(SubstrateChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        return ApplyManyInternalAsync(new[] { change }, change.Metadata.IntentId, ct);
    }

    public Task<ApplyResult> ApplyWorkingSetAsync(
        IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
            return ApplyManyInternalAsync(changes, workingSetToken: null, ct);
        return ApplyManyInternalAsync(changes, WorkingSetToken(changes), ct);
    }

    private static Hash128 WorkingSetToken(IReadOnlyList<SubstrateChange> changes)
    {
        var buf = new byte[changes.Count * 16];
        for (int i = 0; i < changes.Count; i++)
            changes[i].Metadata.IntentId.WriteBytes(buf.AsSpan(i * 16, 16));
        return Hash128.Blake3(buf);
    }

    private async Task<(int e, int p, int a, long fold, long eSkip, long pSkip, int rt, bool journalHit)>
        ApplyStagesCoreAsync(IReadOnlyList<IntentStage> stages, Hash128? workingSetToken, CancellationToken ct)
    {
        var entBlobs = CollectBlobs(stages, IntentStageTable.Entities, 4, "entities");
        var physBlobs = CollectBlobs(stages, IntentStageTable.Physicalities, 10, "physicalities");
        var attBlobs = CollectBlobs(stages, IntentStageTable.Attestations, 10, "attestations");

        var ents = CopyTupleParser.ParseEntities(entBlobs);
        var phys = CopyTupleParser.ParsePhysicalities(physBlobs);
        var atts = CopyTupleParser.ParseAttestations(attBlobs);

        // Distinct ids in first-seen order. Physicalities are verified by
        // their OWN content-addressed id, never inferred from their entity:
        // a physicality legitimately arrives for an already-stored entity
        // (projections and building blocks land after identity content).
        var entityIdSet = new HashSet<Hash128>(ents.Ids.Count);
        var probeEntityIds = new List<Hash128>(ents.Ids.Count);
        foreach (var id in ents.Ids)
            if (entityIdSet.Add(id)) probeEntityIds.Add(id);
        int distinctStagedEntities = probeEntityIds.Count;

        var physIdSet = new HashSet<Hash128>(phys.Ids.Count);
        var probePhysIds = new List<Hash128>(phys.Ids.Count);
        foreach (var id in phys.Ids)
            if (physIdSet.Add(id)) probePhysIds.Add(id);

        // Attestation duplicate collapse, exactly apply_batch's semantics:
        // representative = latest-ts staged row, observation counts sum.
        var attGroups = new Dictionary<Hash128, (int RepIdx, long MaxTs, long Games)>(atts.Ids.Count);
        // The keyed attestation probe needs the partition keys parallel to
        // the probed ids: id alone cannot prune LIST(type_id)->HASH(subject).
        var probeAttIds = new List<Hash128>(atts.Ids.Count);
        var probeAttTypes = new List<Hash128>(atts.Ids.Count);
        var probeAttSubjects = new List<Hash128>(atts.Ids.Count);
        for (int i = 0; i < atts.Ids.Count; i++)
        {
            if (attGroups.TryGetValue(atts.Ids[i], out var g))
            {
                long games = AttestationMergeMath.SafeAddGames(g.Games, atts.Counts[i]);
                attGroups[atts.Ids[i]] = atts.TimestampsPgUs[i] > g.MaxTs
                    ? (i, atts.TimestampsPgUs[i], games)
                    : (g.RepIdx, g.MaxTs, games);
            }
            else
            {
                attGroups[atts.Ids[i]] = (i, atts.TimestampsPgUs[i], atts.Counts[i]);
                probeAttIds.Add(atts.Ids[i]);
                probeAttTypes.Add(atts.TypeIds[i]);
                probeAttSubjects.Add(atts.SubjectIds[i]);
            }
        }

        // Per-phase round-trip counters — summed into the returned total AND logged as a
        // breakdown, so the operator sees WHERE the round-trips go (lock / journal / probe /
        // copy / merge) instead of one opaque number. Probe fans across connections → atomic.
        int rtLock = 0, rtJournal = 0, rtProbe = 0, rtCopy = 0, rtMerge = 0;
        int eIns = 0, pIns = 0, aIns = 0;
        long aFold = 0, eSkip = 0, pSkip = 0;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        // Bulk-apply session SEMANTICS only (FK-trigger bypass, relaxed durability for
        // this bulk tx, no JIT for COPY). Magnitude tuning — work_mem,
        // maintenance_work_mem, parallel workers — is owned by tune-pg.cmd (derived from
        // Cpu/MemoryTopology) and INHERITED here, never re-set with a hardcoded literal.
        await using var tx = await AdvisoryTxLock.BeginWithLockAsync(
            conn, "laplace_apply_batch",
            "SET LOCAL session_replication_role = replica; "
            + "SET LOCAL synchronous_commit = off; "
            + "SET LOCAL jit = off; ",
            _log, ct);
        try
        {
            rtLock++;

            if (workingSetToken is Hash128 token)
            {
                await using var journal = conn.CreateCommand();
                journal.Transaction = tx;
                journal.CommandText =
                    "INSERT INTO laplace.ingest_flush_journal (working_set_id) "
                    + "VALUES ($1) ON CONFLICT (working_set_id) DO NOTHING";
                journal.Parameters.Add(new NpgsqlParameter
                { Value = token.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
                int claimed = await journal.ExecuteNonQueryAsync(ct);
                rtJournal++;
                if (claimed == 0)
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    _log.LogInformation(
                        "WORKING_SET_REPLAY token={Token} already journaled — skipping apply",
                        token);
                    return (0, 0, 0, 0, 0, 0, rtLock + rtJournal, true);
                }
            }

            // Probes fan out across pooled connections. Correct under the
            // held advisory lock: every snapshot starts after the lock was
            // acquired, so anything a prior applier committed is visible.
            var phaseSw = System.Diagnostics.Stopwatch.StartNew();
            var probeTasks = new[]
            {
                ProbePresentParallelAsync("laplace.entities_stored_bitmap", probeEntityIds, r => Interlocked.Add(ref rtProbe, r), ct),
                ProbePresentParallelAsync("laplace.physicalities_exist_bitmap", probePhysIds, r => Interlocked.Add(ref rtProbe, r), ct),
                ProbePresentKeyedParallelAsync("laplace.attestations_exist_bitmap", probeAttIds, probeAttTypes, probeAttSubjects, r => Interlocked.Add(ref rtProbe, r), ct),
            };
            await Task.WhenAll(probeTasks);
            var presentEntities = probeTasks[0].Result;
            var presentPhys = probeTasks[1].Result;
            var presentAtts = probeTasks[2].Result;
            _log.LogInformation(
                "WS_APPLY verify: {Entities:N0}e+{Phys:N0}p+{Atts:N0}a distinct ids probed in {Ms:N0}ms "
                + "(present: {PresentE:N0}e/{PresentP:N0}p/{PresentA:N0}a)",
                probeEntityIds.Count, probePhysIds.Count, probeAttIds.Count, phaseSw.ElapsedMilliseconds,
                presentEntities.Count, presentPhys.Count, presentAtts.Count);

            // Entities: first occurrence of each id, minus stored rows.
            // Kept rows carry their id so parallel COPY groups can own
            // DISJOINT btree key ranges — content-addressed ids are
            // uniformly random, and un-partitioned parallel inserts
            // measured as LWLock:BufferContent pile-ups on shared index
            // pages. Range-partitioned sorted groups fill leaves like a
            // parallel bulk index build instead.
            var keptEnts = new List<KeptRow>(ents.Rows.Count);
            var seenEnt = new HashSet<Hash128>(distinctStagedEntities);
            for (int i = 0; i < ents.Ids.Count; i++)
            {
                if (!seenEnt.Add(ents.Ids[i])) continue;
                if (presentEntities.Contains(ents.Ids[i])) { eSkip++; continue; }
                keptEnts.Add(new KeptRow(ents.Ids[i], ents.Rows[i], -1, 0));
            }

            // Physicalities: first occurrence of each id, minus stored rows.
            // Sort key = HILBERT INDEX, not id: the contended index here is
            // the coord GiST, and hilbert order is its spatial locality —
            // range-partitioned groups land in disjoint GiST subtrees the
            // way id-sorted groups land in disjoint btree leaf ranges.
            var keptPhys = new List<KeptRow>(phys.Rows.Count);
            var seenPhys = new HashSet<Hash128>(phys.Ids.Count);
            for (int i = 0; i < phys.Ids.Count; i++)
            {
                if (!seenPhys.Add(phys.Ids[i])) continue;
                if (presentPhys.Contains(phys.Ids[i])) { pSkip++; continue; }
                keptPhys.Add(new KeptRow(phys.HilbertKeys[i], phys.Rows[i], -1, 0));
            }

            // Attestations: novel groups COPY their representative (count
            // patched to the group sum when duplicates collapsed); present
            // groups merge via one UPDATE.
            var novelRepIdx = new List<int>(attGroups.Count);
            var mergeIds = new List<byte[]>();
            var mergeGames = new List<long>();
            var mergeTs = new List<DateTime>();
            foreach (var (id, g) in attGroups)
            {
                if (presentAtts.Contains(id))
                {
                    mergeIds.Add(id.ToBytes());
                    mergeGames.Add(g.Games);
                    mergeTs.Add(AttestationMergeMath.TimestampFromPgMicros(g.MaxTs));
                }
                else
                {
                    novelRepIdx.Add(g.RepIdx);
                }
            }
            novelRepIdx.Sort();
            var keptAtts = new List<KeptRow>(novelRepIdx.Count);
            for (int k = 0; k < novelRepIdx.Count; k++)
            {
                int i = novelRepIdx[k];
                long games = attGroups[atts.Ids[i]].Games;
                keptAtts.Add(new KeptRow(
                    atts.Ids[i], atts.Rows[i],
                    games == atts.Counts[i] ? -1 : games,
                    atts.CountValueOffsets[i]));
            }

            bool parallelCopy = ApplyParallelism > 1
                && keptEnts.Count + keptPhys.Count + keptAtts.Count >= ParallelCopyMinRows;

            if (!parallelCopy)
            {
                // Small applies stay fully atomic inside the control tx.
                if (keptEnts.Count > 0)
                {
                    await CopyKeptAsync(conn, "entities", IntentStageTable.Entities,
                        entBlobs, keptEnts, 0, keptEnts.Count, ct);
                    eIns = keptEnts.Count;
                    rtCopy++;
                }
                if (keptPhys.Count > 0)
                {
                    await CopyKeptAsync(conn, "physicalities", IntentStageTable.Physicalities,
                        physBlobs, keptPhys, 0, keptPhys.Count, ct);
                    pIns = keptPhys.Count;
                    rtCopy++;
                }
                if (keptAtts.Count > 0)
                {
                    await CopyKeptAsync(conn, "attestations", IntentStageTable.Attestations,
                        attBlobs, keptAtts, 0, keptAtts.Count, ct);
                    aIns = keptAtts.Count;
                    rtCopy++;
                }
            }
            else
            {
                // Bulk COPY fans out across connections owning DISJOINT
                // index keyspaces (sorted + range-partitioned: id for btree
                // tables, hilbert for the coord GiST). Fresh-seed-shaped
                // volumes additionally cycle secondary indexes: drop → COPY
                // clean heaps → parallel sort-based rebuilds (journal-backed
                // for crash recovery). Per-table barriers keep referenced
                // rows durable before their referencers (entities →
                // physicalities → attestations). The control tx holds the
                // advisory lock across the whole window, so no other applier
                // interleaves; a crash mid-phase leaves no flush-journal
                // token and the replay's verification subtracts whatever
                // landed.
                //
                // Cycle scope: inside a bulk run the run-scoped cycle owns
                // the indexes — each qualifying apply drops whatever is
                // still standing (idempotent: dropped indexes no longer
                // appear in pg_index) and the ONE rebuild happens at
                // CompleteBulkRunAsync. Outside a bulk run (no bracket),
                // the apply cycles locally as before. Correct with the
                // indexes down between applies: every write-lane presence
                // probe (*_stored_bitmap / *_present_ordinals) is a PK
                // lookup, and PK/unique/exclusion never cycle.
                var cycle = _runCycle;
                if (cycle is null)
                {
                    cycle = new NpgsqlIndexCycle(_ds, _log);
                    await NpgsqlIndexCycle.RecoverAsync(_ds, _log, ct);
                }
                await cycle.BeginAsync(new[]
                {
                    ("entities", (long)keptEnts.Count),
                    ("physicalities", (long)keptPhys.Count),
                    ("attestations", (long)keptAtts.Count),
                    // Consensus is written by the client fold, which has no cycle
                    // of its own and paid 6 live secondary-index inserts per novel
                    // row (fold collapsed to ~5K rel/s on the big sources). Drop
                    // them in the same run-scoped bracket, rebuilt once at run end;
                    // the fold's prior-read is a PK lookup, unaffected by dropping
                    // the secondaries. Staged proxied by the attestation count.
                    ("consensus", (long)keptAtts.Count),
                }, ct);

                rtCopy += await CopyPhaseParallelAsync("entities", IntentStageTable.Entities,
                    entBlobs, keptEnts, ct);
                eIns = keptEnts.Count;
                rtCopy += await CopyPhaseParallelAsync("physicalities", IntentStageTable.Physicalities,
                    physBlobs, keptPhys, ct);
                pIns = keptPhys.Count;
                rtCopy += await CopyPhaseParallelAsync("attestations", IntentStageTable.Attestations,
                    attBlobs, keptAtts, ct);
                aIns = keptAtts.Count;

                if (!ReferenceEquals(cycle, _runCycle))
                    await cycle.FinishAsync(ct);
            }

            if (mergeIds.Count > 0)
            {
                // Same class as consensus fold: unbounded unnest UPDATE AVs
                // postgres 18 on large bytea[] arrays — chunk writes.
                const int mergeChunk = 32_768;
                const string mergeSql =
                    "UPDATE laplace.attestations a SET "
                    + "  observation_count = a.observation_count + d.games, "
                    + "  last_observed_at  = GREATEST(a.last_observed_at, d.ts) "
                    + "FROM (SELECT unnest($1::bytea[]) AS id, unnest($2::bigint[]) AS games, "
                    + "             unnest($3::timestamptz[]) AS ts) d "
                    + "WHERE a.id = d.id";
                for (int off = 0; off < mergeIds.Count; off += mergeChunk)
                {
                    int m = Math.Min(mergeChunk, mergeIds.Count - off);
                    await using var merge = conn.CreateCommand();
                    merge.Transaction = tx;
                    merge.CommandTimeout = 0;
                    merge.CommandText = mergeSql;
                    merge.Parameters.Add(new NpgsqlParameter
                    { Value = mergeIds.GetRange(off, m).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                    merge.Parameters.Add(new NpgsqlParameter
                    { Value = mergeGames.GetRange(off, m).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                    merge.Parameters.Add(new NpgsqlParameter
                    { Value = mergeTs.GetRange(off, m).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
                    aFold += await merge.ExecuteNonQueryAsync(ct);
                    rtMerge++;
                }
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch { }
            throw;
        }

        int rt = rtLock + rtJournal + rtProbe + rtCopy + rtMerge;
        _log.LogInformation(
            "WS_APPLY round-trips: {Total} = {Lock} lock + {Journal} journal + {Probe} probe + {Copy} copy + {Merge} merge "
            + "({E:N0}e/{P:N0}p/{A:N0}a novel, {Fold:N0} merged)",
            rt, rtLock, rtJournal, rtProbe, rtCopy, rtMerge, eIns, pIns, aIns, aFold);
        return (eIns, pIns, aIns, aFold, eSkip, pSkip, rt, false);
    }

    private static List<(IntPtr Ptr, long Len)> CollectBlobs(
        IReadOnlyList<IntentStage> stages, IntentStageTable table, int expectedFields, string tableName)
    {
        var blobs = new List<(IntPtr, long)>(stages.Count);
        foreach (var s in stages)
        {
            int rowCount = table switch
            {
                IntentStageTable.Entities => s.EntityCount,
                IntentStageTable.Physicalities => s.PhysicalityCount,
                _ => s.AttestationCount,
            };
            if (rowCount == 0) continue;
            (IntPtr ptr, long len) = s.TupleBuffer(table);
            if (ptr == IntPtr.Zero || len <= 0) continue;
            if (CopyBlobValidator.Enabled)
                CopyBlobValidator.Validate(ptr, len, expectedFields, tableName, rowCount);
            blobs.Add((ptr, len));
        }
        return blobs;
    }

    private async Task<HashSet<Hash128>> ProbePresentParallelAsync(
        string function, IReadOnlyList<Hash128> ids, Action<int> addRoundTrips, CancellationToken ct)
    {
        var present = new HashSet<Hash128>();
        if (ids.Count == 0) return present;

        int chunkCount = (ids.Count + ProbeChunkIds - 1) / ProbeChunkIds;
        var perChunk = new List<Hash128>[chunkCount];

        async Task ProbeChunkAsync(int c, CancellationToken token)
        {
            int start = c * ProbeChunkIds;
            int n = Math.Min(ProbeChunkIds, ids.Count - start);
            var chunk = new byte[n][];
            for (int i = 0; i < n; i++) chunk[i] = ids[start + i].ToBytes();

            await using var conn = await _ds.OpenConnectionAsync(token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = $"SELECT {function}($1)";
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = chunk, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            var bm = await cmd.ExecuteScalarAsync(token) as byte[] ?? Array.Empty<byte>();

            var hits = new List<Hash128>();
            long bits = (long)bm.Length * 8;
            for (int i = 0; i < n; i++)
                if (i < bits && (bm[i >> 3] & (1 << (i & 7))) != 0)
                    hits.Add(ids[start + i]);
            perChunk[c] = hits;
        }

        if (chunkCount == 1)
        {
            await ProbeChunkAsync(0, ct);
        }
        else
        {
            int workers = Math.Min(chunkCount, Math.Min(ApplyParallelism, 8));
            int next = -1;
            await CpuTopology.RunPinnedAsyncParallel(workers, async (_, token) =>
            {
                for (int c = Interlocked.Increment(ref next); c < chunkCount;
                     c = Interlocked.Increment(ref next))
                    await ProbeChunkAsync(c, token);
            }, ct);
        }

        foreach (var hits in perChunk)
            if (hits is not null)
                foreach (var id in hits) present.Add(id);
        addRoundTrips(chunkCount);
        return present;
    }

    /// <summary>Keyed variant of the presence probe: passes the target
    /// table's partition keys parallel to the ids so the server-side probe
    /// can prune (attestations: LIST(type_id) -> HASH(subject_id); an
    /// id-only probe pays one index descent per leaf — ~145x).</summary>
    private async Task<HashSet<Hash128>> ProbePresentKeyedParallelAsync(
        string function, IReadOnlyList<Hash128> ids, IReadOnlyList<Hash128> typeIds,
        IReadOnlyList<Hash128> subjectIds, Action<int> addRoundTrips, CancellationToken ct)
    {
        var present = new HashSet<Hash128>();
        if (ids.Count == 0) return present;
        if (typeIds.Count != ids.Count || subjectIds.Count != ids.Count)
            throw new InvalidOperationException(
                $"keyed probe arrays misaligned: {ids.Count} ids / {typeIds.Count} types / {subjectIds.Count} subjects");

        int chunkCount = (ids.Count + ProbeChunkIds - 1) / ProbeChunkIds;
        var perChunk = new List<Hash128>[chunkCount];

        async Task ProbeChunkAsync(int c, CancellationToken token)
        {
            int start = c * ProbeChunkIds;
            int n = Math.Min(ProbeChunkIds, ids.Count - start);
            var chunkIds = new byte[n][];
            var chunkTypes = new byte[n][];
            var chunkSubjects = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                chunkIds[i] = ids[start + i].ToBytes();
                chunkTypes[i] = typeIds[start + i].ToBytes();
                chunkSubjects[i] = subjectIds[start + i].ToBytes();
            }

            await using var conn = await _ds.OpenConnectionAsync(token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = $"SELECT {function}($1, $2, $3)";
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = chunkIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = chunkTypes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = chunkSubjects, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            var bm = await cmd.ExecuteScalarAsync(token) as byte[] ?? Array.Empty<byte>();

            var hits = new List<Hash128>();
            long bits = (long)bm.Length * 8;
            for (int i = 0; i < n; i++)
                if (i < bits && (bm[i >> 3] & (1 << (i & 7))) != 0)
                    hits.Add(ids[start + i]);
            perChunk[c] = hits;
        }

        if (chunkCount == 1)
        {
            await ProbeChunkAsync(0, ct);
        }
        else
        {
            int workers = Math.Min(chunkCount, Math.Min(ApplyParallelism, 8));
            int next = -1;
            await CpuTopology.RunPinnedAsyncParallel(workers, async (_, token) =>
            {
                for (int c = Interlocked.Increment(ref next); c < chunkCount;
                     c = Interlocked.Increment(ref next))
                    await ProbeChunkAsync(c, token);
            }, ct);
        }

        foreach (var hits in perChunk)
            if (hits is not null)
                foreach (var id in hits) present.Add(id);
        addRoundTrips(chunkCount);
        return present;
    }

    /// <summary>SortKey partitions parallel COPY groups into disjoint index
    /// keyspaces: the row id for btree-indexed tables, the hilbert index for
    /// physicalities (coord GiST locality).</summary>
    private readonly record struct KeptRow(Hash128 SortKey, StagedRowRef Row, long Patch, int CountOff);

    private static async Task CopyKeptAsync(
        NpgsqlConnection conn, string tableName, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, List<KeptRow> kept,
        int start, int count, CancellationToken ct)
    {
        var rows = new List<StagedRowRef>(count);
        long[]? patches = null;
        int[]? countOffs = null;
        bool anyPatch = false;
        for (int i = start; i < start + count; i++)
            if (kept[i].Patch >= 0) { anyPatch = true; break; }
        if (anyPatch)
        {
            patches = new long[count];
            countOffs = new int[count];
        }
        for (int i = 0; i < count; i++)
        {
            var k = kept[start + i];
            rows.Add(k.Row);
            if (patches is not null)
            {
                patches[i] = k.Patch;
                countOffs![i] = k.CountOff;
            }
        }
        await CopyFilteredAsync(conn, tableName, table, blobs, rows, patches, countOffs, ct);
    }

    private async Task<int> CopyPhaseParallelAsync(
        string tableName, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, List<KeptRow> kept, CancellationToken ct)
    {
        if (kept.Count == 0) return 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int groups = (int)Math.Min(ApplyParallelism, Math.Max(1L, kept.Count / 16_384));

        // Sort by id so range-partitioned groups own disjoint btree
        // keyspaces — measured LWLock:BufferContent contention disappears
        // when concurrent COPY backends never share index pages.
        kept.Sort(static (a, b) => a.SortKey.CompareToBytewise(b.SortKey));
        int per = (kept.Count + groups - 1) / groups;

        await CpuTopology.RunPinnedAsyncParallel(groups, async (g, token) =>
        {
            int start = g * per;
            if (start >= kept.Count) return;
            int n = Math.Min(per, kept.Count - start);

            await using var conn = await _ds.OpenConnectionAsync(token);
            await using var tx = await conn.BeginTransactionAsync(token);
            await using (var guc = conn.CreateCommand())
            {
                guc.Transaction = tx;
                guc.CommandText =
                    "SET LOCAL session_replication_role = replica; "
                    + "SET LOCAL synchronous_commit = off; "
                    + "SET LOCAL jit = off";
                await guc.ExecuteNonQueryAsync(token);
            }
            await CopyKeptAsync(conn, tableName, table, blobs, kept, start, n, token);
            await tx.CommitAsync(token);
        }, ct);

        sw.Stop();
        _log.LogInformation(
            "WS_APPLY copy {Table}: {Rows:N0} rows across {Groups} id-range connection(s) in {Ms:N0}ms ({Rps:N0} rows/s)",
            tableName, kept.Count, groups, sw.ElapsedMilliseconds,
            kept.Count / Math.Max(1e-3, sw.Elapsed.TotalSeconds));
        return groups;
    }

    private static async Task CopyFilteredAsync(
        NpgsqlConnection conn, string tableName, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, IReadOnlyList<StagedRowRef> rows,
        long[]? patchedCounts, IReadOnlyList<int>? countValueOffsets, CancellationToken ct)
    {
        string cols = IntentStage.CopyColumnList(table);
        await using var stream = await conn.BeginRawBinaryCopyAsync(
            $"COPY laplace.{tableName} ({cols}) FROM STDIN (FORMAT BINARY)", ct);
        await CopyTupleParser.WriteFilteredAsync(
            stream, blobs, rows, patchedCounts, countValueOffsets, ct);
    }
}
