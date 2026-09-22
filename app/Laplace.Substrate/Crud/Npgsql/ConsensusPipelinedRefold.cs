using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;
using Microsoft.Extensions.Logging;

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
