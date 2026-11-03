using System.Runtime.InteropServices;
using global::Npgsql;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed partial class NpgsqlSubstrateWriter
{
    /// <summary>Commit one canonical structural window with the writer's durability policy.</summary>
    public async Task<Laplace.Ingestion.StagedWriteResult> ApplyCanonicalWorkingSetAsync(
        Laplace.Ingestion.StagedSourceWriter window, Hash128[] roots, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(roots);
        await using var connection = await _ds.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        string settings = Durability == PostgresWriteDurability.Synchronous
            ? "write.settings_sync" : "write.settings_async";
        await using (var command = NpgsqlCatalog.Command(connection, transaction, settings))
            await command.ExecuteNonQueryAsync(ct);
        var result = await ApplyCanonicalWorkingSetAsync(window, connection, transaction, roots, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// Apply a Native canonical structural window through the installed extension.
    /// This method does not commit a caller-owned transaction or register a source witness.
    /// </summary>
    public Task<Laplace.Ingestion.StagedWriteResult> ApplyCanonicalWorkingSetAsync(
        Laplace.Ingestion.StagedSourceWriter window, NpgsqlConnection connection,
        NpgsqlTransaction transaction, Hash128[] roots, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(roots);
        if (transaction.Connection != connection)
            throw new ArgumentException("The canonical source transaction belongs to a different connection.", nameof(transaction));
        return window.WriteAsync(connection, transaction, roots, ct);
    }

    internal static readonly int ApplyParallelism = CpuTopology.ResolveApplyPartitions();
    private static readonly IngestSizing.ApplyIoPlan ApplySizing =
        IngestSizing.ResolveApplyIo(ApplyParallelism);

    /// <summary>
    /// Process-wide bound on connections held by COPY groups at once.
    ///
    /// <see cref="PostgresResourcePlan"/> sizes the pool as
    /// <c>1 control + 2p (COPY fan + fold fan) + observability</c>, budgeting the COPY fan
    /// at p. The physicality and attestation phases overlap and each fans out to
    /// <see cref="ApplyParallelism"/> groups, so without this bound live COPY connections
    /// could reach 2p and take the fold fan's and observability's share of the pool.
    ///
    /// Group count is not changed (id-range disjointness and payload sizing decide it);
    /// only the number holding a connection at once is bounded, so extra groups wait for
    /// a slot. Derived from the same plan that sizes MaxPoolSize.
    /// </summary>
    private static readonly SemaphoreSlim CopyConnectionBudget =
        new(ResolveCopyConnectionBudget(), ResolveCopyConnectionBudget());

    internal static int ResolveCopyConnectionBudget()
    {
        var plan = PostgresResourcePlan.Current;
        // Minus the control owner, the observability owners and the fold's reserved
        // headroom: the headroom is not fan capacity, and counting it would give a COPY
        // budget wider than one phase's fan-out.
        int fans = plan.IngestConnectionOwners - 1
                 - plan.ObservabilityConnectionOwners
                 - plan.FoldPoolHeadroomOwners;
        return Math.Max(1, fans / 2);
    }

    /// <summary>
    /// Write-epoch accounting, logged only; no probe or skip decision reads it. Every
    /// write transaction advances laplace.apply_write_epoch before writing; the epoch is
    /// a plain sequence, so advances survive rollback and crash (an aborted write still
    /// counts as a foreign write).
    ///
    /// <see cref="_epochAfterLastCommit"/> is last_value read on the control connection
    /// after the whole apply committed (control transaction, COPY sub-transactions and
    /// merge sub-transactions). At the next apply the control advance returns v, and
    /// v − 1 − baseline − ownBumpsSinceBaseline counts nextval calls by other
    /// transactions in between. Both approximations overcount foreign writes, never
    /// undercount: advances by this process's consensus fold and mask lanes count as
    /// foreign, and an advance landing between the merge commit and the last_value read
    /// is absorbed into the baseline. <see cref="_epochOwnBumpsSinceBaseline"/> covers
    /// the path that advances without re-reading the baseline: a journal-replay apply.
    /// </summary>
    private int _applyWriteEpochRoute = -1;

    /// <summary>The substrate tables' one HASH modulus, read once per writer.</summary>
    private int _leafModulus;
    private long _epochAfterLastCommit = -1;
    private long _epochOwnBumpsSinceBaseline;

    /// <summary>
    /// Once-per-writer to_regclass probe: an older
    /// installed extension has no epoch sequence and every bump site must
    /// degrade to the exact pre-epoch behavior instead of failing the apply.
    /// </summary>
    private async ValueTask<bool> SupportsApplyWriteEpochAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        int known = Volatile.Read(ref _applyWriteEpochRoute);
        if (known >= 0) return known == 1;

        await using var probe = NpgsqlCatalog.Command(connection, null, "write.supports_epoch");
        bool supported = (bool)(await probe.ExecuteScalarAsync(ct) ?? false);
        Interlocked.CompareExchange(ref _applyWriteEpochRoute, supported ? 1 : 0, -1);
        return Volatile.Read(ref _applyWriteEpochRoute) == 1;
    }

    /// <summary>
    /// Tier-0 completeness gate, resolved ONCE per bulk run: true iff the
    /// the Unicode source has completed layer 0 in the target DB.
    /// While true, every tier-0 entity id is present by definition (the t0
    /// space is closed and fully seeded — UCD single-origin law) and skips
    /// the presence probe client-side. Conservative by construction: absent
    /// completion (fresh DB, mid-unicode-seed) leaves the gate off and every t0
    /// row probes as before. Entities only — t0 physicalities are NOT
    /// guaranteed 1:1 (projections land after identity content).
    /// </summary>
    private bool _tier0LayerComplete;

    public async Task BeginBulkRunAsync(CancellationToken ct = default)
    {
        ApplySizing.Log();
        _tier0LayerComplete = await QueryTier0LayerCompleteAsync(ct);
        if (_tier0LayerComplete)
            _log.LogInformation(
                "WS_APPLY tier-0 gate ON: unicode L0 layer completion present — "
                + "tier-0 entity ids answer presence client-side, zero probes");
    }

    public async Task CompleteBulkRunAsync(CancellationToken ct = default)
    {
        _tier0LayerComplete = false;
        // Pending GIN entries are already queryable. Owned bulk loaders can merge
        // them now; workload roles leave index maintenance to autovacuum.
        var drain = System.Diagnostics.Stopwatch.StartNew();
        await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        var indexes = await NpgsqlIngestOps.CleanGinPendingListsAsync(conn, ct).ConfigureAwait(false);
        _log.LogInformation("WS_APPLY completion gin_drain_ms={GinMs} indexes_cleaned={Cleaned} indexes_deferred_to_autovacuum={Deferred}",
            drain.ElapsedMilliseconds, indexes.Count(index => index.PagesCleaned.HasValue),
            indexes.Count(index => !index.PagesCleaned.HasValue));
    }

    public Task AbortBulkRunAsync(CancellationToken ct = default)
    {
        _tier0LayerComplete = false;
        return Task.CompletedTask;
    }

    private async Task<bool> QueryTier0LayerCompleteAsync(CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = NpgsqlCatalog.Command(conn, null, "write.unicode_layer_completed");
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>
    /// Persists one whole working set in a single serialized transaction, claiming
    /// an idempotency receipt in laplace.ingest_flush_journal keyed by the working
    /// set's token over its intent ids. A retry after an ambiguous commit finds the
    /// receipt and returns a no-op instead of applying the additive attestation
    /// merges twice.
    /// </summary>
    public Task<ApplyResult> ApplyWorkingSetAsync(SubstrateChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        return ApplyWorkingSetAsync((IReadOnlyList<SubstrateChange>)new[] { change }, ct);
    }

    public Task<ApplyResult> ApplyWorkingSetAsync(
        IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
            return ApplyManyInternalAsync(
                changes, workingSetKey: null, transactionParticipant: null, ct);
        changes = CanonicalWorkingSetOrder(changes);
        return ApplyManyInternalAsync(
            changes, WorkingSetToken(changes), transactionParticipant: null, ct);
    }

    private static IReadOnlyList<SubstrateChange> CanonicalWorkingSetOrder(
        IReadOnlyList<SubstrateChange> changes)
    {
        if (changes.Count < 2) return changes;
        Hash128 firstSource = changes[0].Metadata.SourceId;
        bool mixed = false;
        for (int i = 1; i < changes.Count; i++)
            if (changes[i].Metadata.SourceId != firstSource) { mixed = true; break; }
        if (!mixed) return changes;

        return changes
            .OrderBy(change => change.Metadata.SourceId, Hash128BytewiseOrder)
            .ThenBy(change => change.Metadata.IntentId, Hash128BytewiseOrder)
            .ToArray();
    }

    private static readonly IComparer<Hash128> Hash128BytewiseOrder =
        Comparer<Hash128>.Create(static (left, right) => left.CompareToBytewise(right));

    private static Hash128 WorkingSetToken(IReadOnlyList<SubstrateChange> changes)
    {
        int ephemeralCount = 0;
        for (int i = 0; i < changes.Count; i++)
            if (!changes[i].EphemeralFoldInputs.IsDefaultOrEmpty)
                ephemeralCount = checked(ephemeralCount + changes[i].EphemeralFoldInputs.Length);

        // Without transient inputs the token is BLAKE3 over the ordered intent ids.
        // Transient inputs additionally bind their calculation receipt ids, so a
        // change cannot replay-hit the receipt of a different calculation. The
        // score is not bound: the receipt identifies the calculation, not its value.
        if (ephemeralCount == 0)
        {
            var ordinary = new byte[changes.Count * 16];
            for (int i = 0; i < changes.Count; i++)
                changes[i].Metadata.IntentId.WriteBytes(ordinary.AsSpan(i * 16, 16));
            return Hash128.Blake3(ordinary);
        }

        var inputs = new List<EphemeralFoldInput>(ephemeralCount);
        var buf = new byte[checked(changes.Count * 16 + 4 + ephemeralCount * 32)];
        for (int i = 0; i < changes.Count; i++)
        {
            changes[i].Metadata.IntentId.WriteBytes(buf.AsSpan(i * 16, 16));
            if (!changes[i].EphemeralFoldInputs.IsDefaultOrEmpty)
                inputs.AddRange(changes[i].EphemeralFoldInputs);
        }
        inputs.Sort(static (x, y) =>
        {
            int c = x.AttestationId.CompareToBytewise(y.AttestationId);
            return c != 0 ? c : x.CalculationReceiptId.CompareToBytewise(y.CalculationReceiptId);
        });
        int offset = changes.Count * 16;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            buf.AsSpan(offset, 4), ephemeralCount);
        offset += 4;
        foreach (var input in inputs)
        {
            input.AttestationId.WriteBytes(buf.AsSpan(offset, 16));
            input.CalculationReceiptId.WriteBytes(buf.AsSpan(offset + 16, 16));
            offset += 32;
        }
        return Hash128.Blake3(buf);
    }

    private static async Task InsertJournalReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Hash128 token,
        Hash128? source,
        IReadOnlyList<Hash128> sources,
        string receiptKind,
        CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(connection, transaction, SqlCatalog.Get("write.journal_receipt"), parameters =>
        {
        parameters.AddWithValue(NpgsqlDbType.Bytea, token.ToBytes());
        parameters.Add(new NpgsqlParameter
        {
            Value = source is { } sourceId ? sourceId.ToBytes() : DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Bytea,
        });
        parameters.AddWithValue(NpgsqlDbType.Text, receiptKind);
        parameters.Add(new NpgsqlParameter
        {
            Value = sources.Select(sourceId => sourceId.ToBytes()).ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
        });
        });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private sealed class CopyTransactionCounts
    {
        internal int Started;
        internal int Committed;
        private bool _controlContainsCopy;
        internal void StartControl()
        {
            if (_controlContainsCopy) return;
            _controlContainsCopy = true;
            Interlocked.Increment(ref Started);
        }
        internal void CommitControl()
        {
            if (_controlContainsCopy) Interlocked.Increment(ref Committed);
        }
    }

    internal static string TransactionSettingsQuery(PostgresWriteDurability durability) => durability switch
    {
        PostgresWriteDurability.Asynchronous => "write.settings_async",
        PostgresWriteDurability.Synchronous => "write.settings_sync",
        _ => throw new ArgumentOutOfRangeException(nameof(durability)),
    };

    internal static async Task<PostgresCommitReceipt> ReadCommitSettingsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, PostgresWriteDurability durability,
        CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(connection, transaction, "write.commit_settings");
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("PostgreSQL commit settings are unavailable.");
        var receipt = new PostgresCommitReceipt(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), false);
        if (receipt.SynchronousCommit != (durability == PostgresWriteDurability.Synchronous ? "on" : "off"))
            throw new InvalidOperationException("PostgreSQL did not apply the selected commit acknowledgement mode.");
        if (durability == PostgresWriteDurability.Synchronous && (!receipt.Fsync || !receipt.FullPageWrites))
            throw new InvalidOperationException("Synchronous recording requires PostgreSQL fsync and full_page_writes enabled.");
        return receipt;
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

    /// <summary>
    /// Returns one deterministic row index per staged entity id, in bytewise id
    /// order: the representative is the lowest tier, then the bytewise-smallest
    /// type_id, independent of arrival order. With the tier-0 gate on, an id with
    /// any tier-0 row goes to <paramref name="tier0Present"/> instead and skips
    /// the probe.
    /// </summary>
    internal static List<int> DistinctEntityRowIndices(
        CopyTupleParser.EntityRows ents, bool tier0Gate, out List<Hash128>? tier0Present)
    {
        var ids = CollectionsMarshal.AsSpan(ents.Ids);
        var tiers = CollectionsMarshal.AsSpan(ents.Tiers);
        var types = CollectionsMarshal.AsSpan(ents.TypeIds);
        var best = new Dictionary<Hash128, int>(ids.Length);
        HashSet<Hash128>? tier0Ids = tier0Gate ? new HashSet<Hash128>() : null;

        for (int i = 0; i < ids.Length; i++)
        {
            Hash128 id = ids[i];
            if (tier0Gate && tiers[i] == 0) tier0Ids!.Add(id);
            ref int prior = ref CollectionsMarshal.GetValueRefOrAddDefault(best, id, out bool seen);
            if (!seen
                || tiers[i] < tiers[prior]
                || (tiers[i] == tiers[prior]
                    && types[i].CompareToBytewise(types[prior]) < 0))
                prior = i;
        }

        // Survivors keep first-seen order: presence routes each id to its hash leaf
        // and every COPY lane sorts its own rows, so no global id order is needed.
        int n = best.Count;
        var selected = new List<int>(n);
        tier0Present = tier0Gate ? new List<Hash128>() : null;
        foreach (int row in best.Values)
        {
            if (tier0Gate && tier0Ids!.Contains(ids[row]))
            {
                tier0Present!.Add(ids[row]);
                continue;
            }
            selected.Add(row);
        }
        return selected;
    }
}
