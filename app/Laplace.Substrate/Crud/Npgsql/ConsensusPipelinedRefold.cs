using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;
using Microsoft.Extensions.Logging;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed partial class ConsensusAccumulatingWriter
{
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
