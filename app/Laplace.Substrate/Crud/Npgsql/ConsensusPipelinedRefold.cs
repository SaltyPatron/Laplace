using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed partial class ConsensusAccumulatingWriter
{
    private readonly record struct EvidenceCell(
        Hash128 Subject, Hash128 Type, Hash128? Object);

    private sealed record EvidenceRefoldPlan(
        EvidenceCell[] Cells,
        long ObservationCount);

    private static bool IsFileCompletionChange(SubstrateChange change) =>
        change.Metadata.SourceContentUnitName.StartsWith(
            PeriodBoundaryUnitPrefix, StringComparison.Ordinal);

    private static SubstrateChange RetainCompletionChange(SubstrateChange change)
    {
        // BuildFileCompletion is deliberately a managed marker change. Keeping a
        // native/apply envelope alive after ProcessBatchAsync returns would violate
        // the transfer contract, so fail the architecture instead of smuggling one
        // into background work.
        if (!change.IntentStages.IsDefaultOrEmpty || change.ApplyEnvelope is not null)
            throw new InvalidOperationException(
                "file completion marker owns transient apply state; it cannot trail consensus");
        return change;
    }

    private static bool TryBuildEvidenceRefoldPlan(
        IReadOnlyList<SubstrateChange> changes,
        out EvidenceRefoldPlan? plan)
    {
        var cells = new HashSet<EvidenceCell>();
        long observations = 0;

        foreach (var change in changes)
        {
            if (change.Metadata.SourceContentUnitName.StartsWith(
                    "layer-complete/", StringComparison.Ordinal)
                || IsFileCompletionChange(change))
                continue;

            foreach (AttestationRow attestation in change.Attestations)
            {
                if (OpsMarkerTypeIds.Contains(attestation.TypeId))
                    continue;
                if (!attestation.FoldReplayable)
                {
                    plan = null;
                    return false;
                }
                cells.Add(new EvidenceCell(
                    attestation.SubjectId,
                    attestation.TypeId,
                    attestation.ObjectId));
                observations = AttestationMergeMath.SafeAddGames(
                    observations, attestation.ObservationCount);
            }
        }

        if (cells.Count == 0)
        {
            plan = null;
            return false;
        }

        var ordered = cells.ToArray();
        Array.Sort(ordered, static (left, right) =>
        {
            int c = left.Type.CompareToBytewise(right.Type);
            if (c != 0) return c;
            Hash128 leftId = ConsensusKeys.EdgeId(
                left.Subject, left.Type, left.Object ?? default);
            Hash128 rightId = ConsensusKeys.EdgeId(
                right.Subject, right.Type, right.Object ?? default);
            c = leftId.CompareToBytewise(rightId);
            return c != 0 ? c : left.Subject.CompareToBytewise(right.Subject);
        });
        plan = new EvidenceRefoldPlan(ordered, observations);
        return true;
    }

    /// <summary>
    /// One admitted working set immediately schedules its own evidence-backed consensus
    /// refold. This is a continuously running ETL stage: there is no payload queue, no
    /// source-end recomputation, and no alternate bulk drain. The only end-of-run action
    /// is awaiting tasks that were already dispatched while ingestion was progressing.
    /// </summary>
    private async Task EnqueueEvidenceRefoldAsync(
        EvidenceRefoldPlan plan,
        IReadOnlyList<SubstrateChange> completionChanges,
        CancellationToken ct)
    {
        await _foldDepth.WaitAsync(ct).ConfigureAwait(false);
        Task dispatched;
        try
        {
            Interlocked.CompareExchange(
                ref _foldSpanStarted,
                System.Diagnostics.Stopwatch.GetTimestamp(),
                comparand: 0);
            dispatched = DispatchEvidenceRefoldAsync(
                plan.Cells, completionChanges, CancellationToken.None);
        }
        catch
        {
            _foldDepth.Release();
            throw;
        }

        async Task ReleaseAsync()
        {
            try { await dispatched.ConfigureAwait(false); }
            finally { _foldDepth.Release(); }
        }

        Task tracked = ReleaseAsync();
        lock (_foldChainLock)
        {
            _outstanding.Add(tracked);
            // A file boundary is not durable until this continuation publishes its
            // completion marker after consensus + masks. Register the SAME batch task
            // under every represented file so the runner can observe durability
            // asynchronously without turning file boundaries back into apply barriers.
            foreach (string owner in completionChanges
                         .Select(static change => change.Metadata.FileLabel)
                         .Where(static label => !string.IsNullOrWhiteSpace(label))
                         .Select(static label => label!)
                         .Distinct(StringComparer.Ordinal))
            {
                if (!_fileFolds.TryGetValue(owner, out var owned))
                    _fileFolds.Add(owner, owned = []);
                owned.Add(tracked);
            }
        }
    }

    private Task DispatchEvidenceRefoldAsync(
        EvidenceCell[] cells,
        IReadOnlyList<SubstrateChange> completionChanges,
        CancellationToken ct)
    {
        var runs = new List<(Hash128 Type, int Off, int Len)>();
        for (int i = 0; i < cells.Length;)
        {
            int j = i + 1;
            while (j < cells.Length && cells[j].Type == cells[i].Type) j++;
            runs.Add((cells[i].Type, i, j - i));
            i = j;
        }
        int[] widths = IngestSizing.AllocateFoldRunWidths(
            runs.Select(static run => run.Len).ToArray(), FoldConnections);

        var completions = new List<Task>(runs.Count + MaskShards);
        long folded = 0;
        long masks = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        lock (_laneLock)
        {
            for (int i = 0; i < runs.Count; i++)
            {
                var run = runs[i];
                Task prior = _typeLanes.TryGetValue(run.Type, out Task? existing)
                    ? existing
                    : Task.CompletedTask;
                int width = widths[i];
                var owned = run;
                Task next = Task.Run(
                    () => RefoldRunAfterAsync(prior, owned, width),
                    CancellationToken.None);
                _typeLanes[run.Type] = next;
                completions.Add(next);
            }

            var buckets = new List<(Hash128 Ent, Hash128 Typ)>?[MaskShards];
            foreach (EvidenceCell cell in cells)
            {
                AddPair(cell.Subject, cell.Type);
                if (cell.Object is { } objectId) AddPair(objectId, cell.Type);
            }

            void AddPair(Hash128 entity, Hash128 type)
            {
                int shard = MaskShard(entity);
                (buckets[shard] ??= []).Add((entity, type));
            }

            for (int shard = 0; shard < buckets.Length; shard++)
            {
                if (buckets[shard] is not { Count: > 0 } bucket) continue;
                Task prior = _maskLanes[shard];
                int ownedShard = shard;
                List<(Hash128 Ent, Hash128 Typ)> ownedBucket = bucket;
                Task next = Task.Run(async () =>
                {
                    await prior.ConfigureAwait(false);
                    long updated = await DepositEvidencePairsAsync(
                        ownedBucket, ownedShard, ct).ConfigureAwait(false);
                    Interlocked.Add(ref masks, updated);
                }, CancellationToken.None);
                _maskLanes[shard] = next;
                completions.Add(next);
            }
        }

        return CompleteAsync();

        async Task CompleteAsync()
        {
            try
            {
                await Task.WhenAll(completions).ConfigureAwait(false);

                // File resume becomes durable only after this file/batch's semantic work
                // is current. Markers are batched together, so 7,567 tiny XML files do
                // not recreate 7,567 apply transactions.
                if (completionChanges.Count > 0)
                    await _inner.ApplyWorkingSetAsync(
                        completionChanges, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                PruneCompletedLanes();
            }

            _log.LogInformation(
                "consensus evidence-refold: {Cells:N0} cells refreshed across {Lanes} type lanes, "
                + "{Masks:N0} masks deposited in {Ms:N0}ms ({Rate:N0} cells/s)",
                folded, runs.Count, masks, sw.ElapsedMilliseconds,
                folded / Math.Max(1e-3, sw.Elapsed.TotalSeconds));
        }

        async Task RefoldRunAfterAsync(
            Task prior, (Hash128 Type, int Off, int Len) run, int connectionWidth)
        {
            await prior.ConfigureAwait(false);

            int segLen = Math.Min(
                FoldSizing.ChunkCells,
                (run.Len + connectionWidth - 1) / connectionWidth);
            var segments = new List<(int Off, int Len)>();
            for (int start = run.Off; start < run.Off + run.Len; start += segLen)
                segments.Add((start, Math.Min(segLen, run.Off + run.Len - start)));

            await Parallel.ForEachAsync(
                segments,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = connectionWidth,
                    CancellationToken = ct,
                },
                async (segment, token) =>
                {
                    await _foldConnections.WaitAsync(token).ConfigureAwait(false);
                    long backendStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        int count = segment.Len;
                        var subjects = new byte[count][];
                        var objects = new byte[count][];
                        for (int i = 0; i < count; i++)
                        {
                            EvidenceCell cell = cells[segment.Off + i];
                            subjects[i] = cell.Subject.ToBytes();
                            objects[i] = cell.Object?.ToBytes()!;
                        }

                        await using var connection =
                            await _ds.OpenConnectionAsync(token).ConfigureAwait(false);
                        bool epoch = await SupportsApplyWriteEpochAsync(
                            connection, token).ConfigureAwait(false);
                        await using var transaction =
                            await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                        await using var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandTimeout = 0;
                        const string refoldSql =
                            "SELECT consensus.refold_evidence_type($1,$2,$3)";
                        command.CommandText = epoch
                            ? "WITH epoch AS MATERIALIZED "
                                + "(SELECT nextval('laplace.apply_write_epoch')) "
                                + refoldSql + " FROM epoch"
                            : refoldSql;
                        command.Parameters.Add(new NpgsqlParameter
                        { Value = run.Type.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
                        command.Parameters.Add(new NpgsqlParameter
                        { Value = subjects, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                        command.Parameters.Add(new NpgsqlParameter
                        { Value = objects, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });

                        long changed = (long)(
                            await command.ExecuteScalarAsync(token).ConfigureAwait(false)
                            ?? 0L);
                        await transaction.CommitAsync(token).ConfigureAwait(false);
                        Interlocked.Add(ref folded, changed);
                    }
                    finally
                    {
                        Interlocked.Add(
                            ref _consensusBackendTicks,
                            System.Diagnostics.Stopwatch.GetTimestamp() - backendStarted);
                        Interlocked.Increment(ref _consensusUpsertCalls);
                        _foldConnections.Release();
                    }
                }).ConfigureAwait(false);
        }
    }

    private async Task<long> DepositEvidencePairsAsync(
        List<(Hash128 Ent, Hash128 Typ)> pairs,
        int shard,
        CancellationToken ct)
    {
        var deposited = _depositedMaskPairs[shard];
        pairs.Sort(static (left, right) =>
        {
            int c = left.Ent.CompareToBytewise(right.Ent);
            return c != 0 ? c : left.Typ.CompareToBytewise(right.Typ);
        });

        var todo = new List<(Hash128 Ent, Hash128 Typ)>(pairs.Count);
        (Hash128 Ent, Hash128 Typ)? prior = null;
        foreach (var pair in pairs)
        {
            if (prior is { } previous && previous == pair) continue;
            prior = pair;
            if (!deposited.Contains(pair)) todo.Add(pair);
        }
        if (todo.Count == 0) return 0;

        long updated = 0;
        for (int off = 0; off < todo.Count; off += FoldSizing.ChunkCells)
        {
            int count = Math.Min(FoldSizing.ChunkCells, todo.Count - off);
            var entities = new byte[count][];
            var types = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                entities[i] = todo[off + i].Ent.ToBytes();
                types[i] = todo[off + i].Typ.ToBytes();
            }

            await _foldConnections.WaitAsync(ct).ConfigureAwait(false);
            long backendStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                await using var connection =
                    await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
                bool epoch = await SupportsApplyWriteEpochAsync(
                    connection, ct).ConfigureAwait(false);
                await using var transaction =
                    await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandTimeout = 0;
                const string maskSql =
                    "SELECT consensus.highway_mask_deposit($1,$2)";
                command.CommandText = epoch
                    ? "WITH epoch AS MATERIALIZED "
                        + "(SELECT nextval('laplace.apply_write_epoch')) "
                        + maskSql + " FROM epoch"
                    : maskSql;
                command.Parameters.Add(new NpgsqlParameter
                { Value = entities, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                command.Parameters.Add(new NpgsqlParameter
                { Value = types, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                updated += (long)(
                    await command.ExecuteScalarAsync(ct).ConfigureAwait(false)
                    ?? 0L);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Add(
                    ref _highwayMaskBackendTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp() - backendStarted);
                Interlocked.Increment(ref _highwayMaskCalls);
                Interlocked.Add(ref _highwayMaskPairs, count);
                _foldConnections.Release();
            }
        }

        if (deposited.Count + todo.Count > DepositedMaskPairsCap / MaskShards)
            deposited.Clear();
        deposited.UnionWith(todo);
        return updated;
    }
}
