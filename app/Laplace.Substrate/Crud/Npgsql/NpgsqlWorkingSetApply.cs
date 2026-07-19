using global::Npgsql;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// The Rule #8 write protocol (docs/specs/06_Engineering_Ruleset.txt, step 6
/// as amended 2026-07-18): the client owns dedup — one distinct row per
/// entity/physicality id, one collapsed group per attestation id — and the
/// SERVER adjudicates presence at insert time. Each table's client-deduped
/// rows raw-binary COPY into a session-local TEMP staging table, then ONE
/// set-based INSERT .. SELECT lands them: ON CONFLICT DO NOTHING for content
/// (entities/physicalities — present content is immutable, a re-seen id is a
/// no-op), ON CONFLICT DO UPDATE for attestations (a present row MERGES its
/// observation count and max timestamp — dropping counts drops testimony).
///
/// This replaced the apply-side existence probes (three bitmap probes over
/// every staged id before a filtered COPY): at Wiktionary scale the probe
/// re-verified ~12M mostly-novel ids per apply at cache-cold random I/O,
/// measured 37-53 MINUTES per apply. The ON CONFLICT arbiter is the PK,
/// which never cycles (PK/unique/exclusion are exempt from the index cycle),
/// so adjudication is the same keyed descent the probe paid — but paid once,
/// fused with the write, only where a row actually lands.
/// </summary>
public sealed partial class NpgsqlSubstrateWriter
{
    /// <summary>
    /// Rows below this stay on the fully-atomic single-transaction path;
    /// above it, staging + adjudication fans out across connections
    /// (per-table barriers keep entities durable before physicalities
    /// before attestations).
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

        // Client dedup — the one guarantee the ON CONFLICT offload rests on:
        // DO UPDATE faults if a single statement touches the same row twice,
        // so exactly one staged row per id may reach the insert.
        //
        // Entities/physicalities: first occurrence of each id, first-seen
        // order. Physicalities dedup by their OWN content-addressed id,
        // never inferred from their entity (projections and building blocks
        // legitimately arrive for an already-stored entity). KeptRow.SortKey
        // partitions parallel groups into disjoint index keyspaces: row id
        // for btree tables, hilbert index for the physicality coord GiST.
        var keptEnts = new List<KeptRow>(ents.Ids.Count);
        var seenEnt = new HashSet<Hash128>(ents.Ids.Count);
        for (int i = 0; i < ents.Ids.Count; i++)
            if (seenEnt.Add(ents.Ids[i]))
                keptEnts.Add(new KeptRow(ents.Ids[i], ents.Rows[i], -1, 0));

        var keptPhys = new List<KeptRow>(phys.Ids.Count);
        var seenPhys = new HashSet<Hash128>(phys.Ids.Count);
        for (int i = 0; i < phys.Ids.Count; i++)
            if (seenPhys.Add(phys.Ids[i]))
                keptPhys.Add(new KeptRow(phys.HilbertKeys[i], phys.Rows[i], -1, 0));

        // Attestation duplicate collapse, exactly apply_batch's semantics:
        // representative = latest-ts staged row, observation counts sum. The
        // collapsed group stages ONE row carrying the summed count (patched
        // on the way out) and the max timestamp; a group whose id is already
        // present then merges via the insert's DO UPDATE.
        var attGroups = new Dictionary<Hash128, (int RepIdx, long MaxTs, long Games)>(atts.Ids.Count);
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
            }
        }
        var repIdx = new List<int>(attGroups.Count);
        foreach (var (_, g) in attGroups)
            repIdx.Add(g.RepIdx);
        repIdx.Sort(); // staged order — contiguous rows coalesce in the COPY writer
        var keptAtts = new List<KeptRow>(repIdx.Count);
        for (int k = 0; k < repIdx.Count; k++)
        {
            int i = repIdx[k];
            long games = attGroups[atts.Ids[i]].Games;
            keptAtts.Add(new KeptRow(
                atts.Ids[i], atts.Rows[i],
                games == atts.Counts[i] ? -1 : games,
                atts.CountValueOffsets[i]));
        }

        // Per-phase round-trip counters — summed into the returned total AND logged as a
        // breakdown, so the operator sees WHERE the round-trips go (lock / journal /
        // stage+adjudicate) instead of one opaque number. Phases fan across connections
        // in the parallel path → atomic adds there.
        int rtLock = 0, rtJournal = 0, rtApply = 0;
        long eIns = 0, pIns = 0, aIns = 0;
        long aFold = 0;

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

            bool parallelApply = ApplyParallelism > 1
                && keptEnts.Count + keptPhys.Count + keptAtts.Count >= ParallelCopyMinRows;

            if (!parallelApply)
            {
                // Small applies stay fully atomic inside the control tx.
                if (keptEnts.Count > 0)
                {
                    (eIns, _) = await StageAdjudicateAsync(conn, "entities", IntentStageTable.Entities,
                        entBlobs, keptEnts, 0, keptEnts.Count, ct);
                    rtApply += StageAdjudicateRoundTrips;
                }
                if (keptPhys.Count > 0)
                {
                    (pIns, _) = await StageAdjudicateAsync(conn, "physicalities", IntentStageTable.Physicalities,
                        physBlobs, keptPhys, 0, keptPhys.Count, ct);
                    rtApply += StageAdjudicateRoundTrips;
                }
                if (keptAtts.Count > 0)
                {
                    (aIns, aFold) = await StageAdjudicateAsync(conn, "attestations", IntentStageTable.Attestations,
                        attBlobs, keptAtts, 0, keptAtts.Count, ct);
                    rtApply += StageAdjudicateRoundTrips;
                }
            }
            else
            {
                // Bulk staging fans out across connections owning DISJOINT
                // index keyspaces (sorted + range-partitioned: id for btree
                // tables, hilbert for the coord GiST). Fresh-seed-shaped
                // volumes additionally cycle secondary indexes: drop → land
                // clean heaps → parallel sort-based rebuilds (journal-backed
                // for crash recovery). Per-table barriers keep referenced
                // rows durable before their referencers (entities →
                // physicalities → attestations). The control tx holds the
                // advisory lock across the whole window, so no other applier
                // interleaves; a crash mid-phase leaves no flush-journal
                // token, and the replay's ON CONFLICT adjudication absorbs
                // whatever content already landed.
                //
                // Cycle scope: inside a bulk run the run-scoped cycle owns
                // the indexes — each qualifying apply drops whatever is
                // still standing (idempotent: dropped indexes no longer
                // appear in pg_index) and the ONE rebuild happens at
                // CompleteBulkRunAsync. Outside a bulk run (no bracket),
                // the apply cycles locally as before. Correct with the
                // indexes down between applies: the ON CONFLICT arbiter is
                // the PK, and PK/unique/exclusion never cycle.
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

                (int rt, long ins, long _) = await AdjudicatePhaseParallelAsync(
                    "entities", IntentStageTable.Entities, entBlobs, keptEnts, ct);
                rtApply += rt; eIns = ins;
                (rt, ins, _) = await AdjudicatePhaseParallelAsync(
                    "physicalities", IntentStageTable.Physicalities, physBlobs, keptPhys, ct);
                rtApply += rt; pIns = ins;
                long merged;
                (rt, ins, merged) = await AdjudicatePhaseParallelAsync(
                    "attestations", IntentStageTable.Attestations, attBlobs, keptAtts, ct);
                rtApply += rt; aIns = ins; aFold = merged;

                if (!ReferenceEquals(cycle, _runCycle))
                    await cycle.FinishAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch { }
            throw;
        }

        // Novelty telemetry derives from the server's own insert row counts —
        // a staged distinct row the insert did NOT land was already present.
        long eSkip = keptEnts.Count - eIns;
        long pSkip = keptPhys.Count - pIns;

        int rt2 = rtLock + rtJournal + rtApply;
        _log.LogInformation(
            "WS_APPLY round-trips: {Total} = {Lock} lock + {Journal} journal + {Apply} stage/adjudicate "
            + "({E:N0}e/{P:N0}p/{A:N0}a inserted, {Fold:N0} merged, {ESkip:N0}e/{PSkip:N0}p present)",
            rt2, rtLock, rtJournal, rtApply, eIns, pIns, aIns, aFold, eSkip, pSkip);
        return (checked((int)eIns), checked((int)pIns), checked((int)aIns), aFold, eSkip, pSkip, rt2, false);
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

    /// <summary>Round trips per <see cref="StageAdjudicateAsync"/> call. The
    /// unit in this lane is one serialized server VISIT per connection, not
    /// one wire command: the retired COPY groups also ran
    /// BEGIN + GUCs + COPY + COMMIT and counted 1. Staging DDL + COPY +
    /// adjudicating insert pipeline on one connection the same way.</summary>
    private const int StageAdjudicateRoundTrips = 1;

    /// <summary>
    /// Stages one kept-row range into a session-local TEMP table (raw binary
    /// COPY, dropped at the surrounding transaction's commit) and lands it
    /// with one set-based INSERT .. SELECT whose ON CONFLICT clause is the
    /// presence adjudication. Returns (rows inserted, rows merged); for the
    /// DO NOTHING tables merged is always 0 and inserted &lt; staged means
    /// already-present content was skipped server-side.
    /// </summary>
    private static async Task<(long Inserted, long Merged)> StageAdjudicateAsync(
        NpgsqlConnection conn, string tableName, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, List<KeptRow> kept,
        int start, int count, CancellationToken ct)
    {
        string cols = IntentStage.CopyColumnList(table);
        string stage = "_laplace_stage_" + tableName;

        // LIKE .. INCLUDING DEFAULTS mirrors column names/types/typmods (no
        // geometry re-coercion on the way back out) and keeps NOT NULL
        // columns outside the COPY list satisfiable; CHECKs, indexes and the
        // generated radius_origin are deliberately NOT copied — staging is a
        // pipe, the real table enforces.
        await using (var ddl = conn.CreateCommand())
        {
            ddl.CommandText =
                $"CREATE TEMP TABLE {stage} (LIKE laplace.{tableName} INCLUDING DEFAULTS) ON COMMIT DROP";
            await ddl.ExecuteNonQueryAsync(ct);
        }

        await CopyKeptAsync(conn, $"pg_temp.{stage}", table, blobs, kept, start, count, ct);

        if (table == IntentStageTable.Attestations)
        {
            // DO UPDATE, never DO NOTHING: a re-seen attestation id carries new
            // observations — its count MERGES (additive) and its timestamp
            // advances, byte-for-byte the semantics of the retired present-
            // attestation merge UPDATE. RETURNING old (PG 18) distinguishes a
            // fresh insert (old is the null row) from a conflict-merge, so
            // novelty telemetry stays honest; the classic xmax = 0 trick is
            // rejected on partitioned tables ("cannot retrieve a system
            // column in this context").
            await using var ins = conn.CreateCommand();
            ins.CommandTimeout = 0;
            ins.CommandText =
                $"WITH adjudicated AS ("
                + $" INSERT INTO laplace.{tableName} ({cols})"
                + $" SELECT {cols} FROM pg_temp.{stage}"
                + " ON CONFLICT (id, type_id, subject_id) DO UPDATE SET"
                + "   observation_count = attestations.observation_count + EXCLUDED.observation_count,"
                + "   last_observed_at  = GREATEST(attestations.last_observed_at, EXCLUDED.last_observed_at)"
                + " RETURNING (old.id IS NULL) AS inserted)"
                + " SELECT count(*) FILTER (WHERE inserted),"
                + "        count(*) FILTER (WHERE NOT inserted) FROM adjudicated";
            await using var rd = await ins.ExecuteReaderAsync(ct);
            await rd.ReadAsync(ct);
            return (rd.GetInt64(0), rd.GetInt64(1));
        }

        string arbiter = table == IntentStageTable.Entities ? "(id, tier)" : "(hilbert_index, id)";
        await using var insert = conn.CreateCommand();
        insert.CommandTimeout = 0;
        insert.CommandText =
            $"INSERT INTO laplace.{tableName} ({cols})"
            + $" SELECT {cols} FROM pg_temp.{stage}"
            + $" ON CONFLICT {arbiter} DO NOTHING";
        long inserted = await insert.ExecuteNonQueryAsync(ct);
        return (inserted, 0);
    }

    /// <summary>SortKey partitions parallel apply groups into disjoint index
    /// keyspaces: the row id for btree-indexed tables, the hilbert index for
    /// physicalities (coord GiST locality).</summary>
    private readonly record struct KeptRow(Hash128 SortKey, StagedRowRef Row, long Patch, int CountOff);

    private static async Task CopyKeptAsync(
        NpgsqlConnection conn, string target, IntentStageTable table,
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
        await CopyFilteredAsync(conn, target, table, blobs, rows, patches, countOffs, ct);
    }

    private async Task<(int RoundTrips, long Inserted, long Merged)> AdjudicatePhaseParallelAsync(
        string tableName, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, List<KeptRow> kept, CancellationToken ct)
    {
        if (kept.Count == 0) return (0, 0, 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int groups = (int)Math.Min(ApplyParallelism, Math.Max(1L, kept.Count / 16_384));

        // Sort by the key whose index the insert descends, so range-
        // partitioned groups own disjoint keyspaces — measured
        // LWLock:BufferContent contention disappears when concurrent
        // backends never share index pages — and each group's INSERT..SELECT
        // walks its staging heap (COPYed in this order) sequentially.
        kept.Sort(static (a, b) => a.SortKey.CompareToBytewise(b.SortKey));
        int per = (kept.Count + groups - 1) / groups;

        long inserted = 0, merged = 0;
        int roundTrips = 0;
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
            var r = await StageAdjudicateAsync(conn, tableName, table, blobs, kept, start, n, token);
            await tx.CommitAsync(token);
            Interlocked.Add(ref inserted, r.Inserted);
            Interlocked.Add(ref merged, r.Merged);
            Interlocked.Add(ref roundTrips, StageAdjudicateRoundTrips);
        }, ct);

        sw.Stop();
        _log.LogInformation(
            "WS_APPLY {Table}: {Rows:N0} staged rows across {Groups} key-range connection(s) in {Ms:N0}ms "
            + "({Rps:N0} rows/s; {Inserted:N0} inserted, {Merged:N0} merged)",
            tableName, kept.Count, groups, sw.ElapsedMilliseconds,
            kept.Count / Math.Max(1e-3, sw.Elapsed.TotalSeconds),
            inserted, merged);
        return (roundTrips, inserted, merged);
    }

    private static async Task CopyFilteredAsync(
        NpgsqlConnection conn, string target, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, IReadOnlyList<StagedRowRef> rows,
        long[]? patchedCounts, IReadOnlyList<int>? countValueOffsets, CancellationToken ct)
    {
        string cols = IntentStage.CopyColumnList(table);
        await using var stream = await conn.BeginRawBinaryCopyAsync(
            $"COPY {target} ({cols}) FROM STDIN (FORMAT BINARY)", ct);
        await CopyTupleParser.WriteFilteredAsync(
            stream, blobs, rows, patchedCounts, countValueOffsets, ct);
    }
}
