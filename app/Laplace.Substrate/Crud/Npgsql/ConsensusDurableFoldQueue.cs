using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed partial class ConsensusAccumulatingWriter
{
    private readonly object _durableFoldScheduleLock = new();
    private readonly HashSet<Hash128> _durableFoldScheduled = [];

    /// <summary>
    /// Persist the exact explicit-delta payload that the old atomic participant would
    /// have folded inline. Storage flushes remain non-semantic: the payload is grouped
    /// by the same rating periods and split only by the byte-derived fold transit cap.
    /// </summary>
    private async Task<bool> PersistDurableFoldWorkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Hash128 workingSetToken,
        Dictionary<(Hash128 S, Hash128 T, Hash128? O), Delta> delta,
        CancellationToken ct)
    {
        if (delta.Count == 0) return false;

        var cells = new ((Hash128 S, Hash128 T, Hash128? O) Key, Hash128 Cid, Delta D)[delta.Count];
        int at = 0;
        foreach (var (key, value) in delta)
            cells[at++] = (key, ConsensusKeys.EdgeId(key.S, key.T, key.O ?? default), value);
        Array.Sort(cells, static (left, right) =>
        {
            int c = left.Key.T.CompareToBytewise(right.Key.T);
            if (c != 0) return c;
            c = left.Cid.CompareToBytewise(right.Cid);
            return c != 0 ? c : left.Key.S.CompareToBytewise(right.Key.S);
        });

        var chunks = new List<DurableFoldChunk>();
        int ordinal = 0;
        for (int runStart = 0; runStart < cells.Length;)
        {
            Hash128 type = cells[runStart].Key.T;
            int runEnd = runStart + 1;
            while (runEnd < cells.Length && cells[runEnd].Key.T == type) runEnd++;

            for (int off = runStart; off < runEnd; off += FoldSizing.ChunkCells)
            {
                int count = Math.Min(FoldSizing.ChunkCells, runEnd - off);
                var subjects = new byte[count][];
                var objects = new byte[count][];
                var objectNulls = new bool[count];
                var phis = new long[count];
                var games = new long[count];
                var sums = new long[count];
                var observed = new DateTime[count];
                var opponents = new long[count];

                int periodCount = 0;
                for (int i = 0; i < count; i++)
                    periodCount = checked(periodCount + PeriodCount(in cells[off + i].D));
                var periodOffsets = new long[count + 1];
                var periodOpponents = new long[periodCount];
                var periodPhis = new long[periodCount];
                var periodGames = new long[periodCount];
                var periodSums = new long[periodCount];
                int periodAt = 0;

                for (int i = 0; i < count; i++)
                {
                    var cell = cells[off + i];
                    subjects[i] = cell.Key.S.ToBytes();
                    objectNulls[i] = cell.Key.O is null;
                    objects[i] = (cell.Key.O ?? default).ToBytes();
                    phis[i] = cell.D.FirstPeriod.PhiFp1e9;
                    games[i] = cell.D.Games;
                    sums[i] = cell.D.SumScoreFp1e9;
                    observed[i] = TsFromUnixUs(cell.D.MaxTsUnixUs);
                    opponents[i] = cell.D.FirstPeriod.OpponentRatingFp1e9;
                    periodOffsets[i] = periodAt;
                    WritePeriods(in cell.D, periodOpponents, periodPhis,
                        periodGames, periodSums, ref periodAt);
                }
                periodOffsets[count] = periodAt;
                chunks.Add(new DurableFoldChunk(
                    ordinal++, type, subjects, objects, objectNulls,
                    phis, games, sums, observed, opponents,
                    periodOffsets, periodOpponents, periodPhis, periodGames, periodSums));
            }
            runStart = runEnd;
        }

        await using var batch = new NpgsqlBatch(connection, transaction);
        var work = new NpgsqlBatchCommand(
            "INSERT INTO laplace.ingest_fold_work(working_set_id) VALUES($1) "
            + "ON CONFLICT (working_set_id) DO NOTHING");
        work.Parameters.Add(new NpgsqlParameter
        { Value = workingSetToken.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
        batch.BatchCommands.Add(work);

        foreach (var chunk in chunks)
        {
            var cmd = new NpgsqlBatchCommand(
                "INSERT INTO laplace.ingest_fold_chunk("
                + "working_set_id,ordinal,type_id,subjects,objects,object_nulls,"
                + "phis,games,sums,observed_at,opponents,period_offsets,"
                + "period_opponents,period_phis,period_games,period_sums) "
                + "VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16) "
                + "ON CONFLICT (working_set_id,ordinal) DO NOTHING");
            cmd.Parameters.Add(new NpgsqlParameter { Value = workingSetToken.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
            cmd.Parameters.Add(new NpgsqlParameter { Value = chunk.Ordinal, NpgsqlDbType = NpgsqlDbType.Integer });
            cmd.Parameters.Add(new NpgsqlParameter { Value = chunk.Type.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
            cmd.Parameters.Add(new NpgsqlParameter { Value = chunk.Subjects, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.Add(new NpgsqlParameter { Value = chunk.Objects, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.Add(new NpgsqlParameter { Value = chunk.ObjectNulls, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Boolean });
            cmd.Parameters.Add(new NpgsqlParameter { Value = chunk.Phis, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
            cmd.Parameters.Add(new NpgsqlParameter { Value = chunk.Games, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
            cmd.Parameters.Add(new NpgsqlParameter { Value = chunk.Sums, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
            cmd.Parameters.Add(new NpgsqlParameter { Value = chunk.ObservedAt, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
            cmd.Parameters.Add(new NpgsqlParameter { Value = chunk.Opponents, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
            cmd.Parameters.Add(new NpgsqlParameter { Value = chunk.PeriodOffsets, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
            cmd.Parameters.Add(new NpgsqlParameter { Value = chunk.PeriodOpponents, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
            cmd.Parameters.Add(new NpgsqlParameter { Value = chunk.PeriodPhis, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
            cmd.Parameters.Add(new NpgsqlParameter { Value = chunk.PeriodGames, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
            cmd.Parameters.Add(new NpgsqlParameter { Value = chunk.PeriodSums, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
            batch.BatchCommands.Add(cmd);
        }

        await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task RecoverDurableFoldWorkAsync(CancellationToken ct)
    {
        await using var command = _ds.CreateCommand(
            "SELECT working_set_id FROM laplace.ingest_fold_work ORDER BY work_sequence");
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var tokens = new List<Hash128>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                tokens.Add(Hash128.FromBytes(reader.GetFieldValue<byte[]>(0)));
            foreach (var token in tokens)
                await EnqueueDurableFoldWorkAsync(token, ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // Older installed extension: deployment will create the queue. Until then,
            // retain the historical inline fold path by surfacing no recoverable work.
        }
    }

    private async Task EnqueueDurableFoldWorkAsync(Hash128 token, CancellationToken ct)
    {
        lock (_durableFoldScheduleLock)
            if (_durableFoldScheduled.Contains(token))
                return;

        await _foldDepth.WaitAsync(ct).ConfigureAwait(false);
        bool ownsDepth = true;
        bool scheduled = false;
        try
        {
            lock (_durableFoldScheduleLock)
            {
                if (!_durableFoldScheduled.Add(token))
                {
                    _foldDepth.Release();
                    ownsDepth = false;
                    return;
                }
                scheduled = true;
            }

            var chunks = new List<(int Ordinal, Hash128 Type)>();
            await using (var command = _ds.CreateCommand(
                "SELECT ordinal,type_id FROM laplace.ingest_fold_chunk "
                + "WHERE working_set_id=$1 ORDER BY ordinal"))
            {
                command.Parameters.Add(new NpgsqlParameter
                { Value = token.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    chunks.Add((reader.GetInt32(0), Hash128.FromBytes(reader.GetFieldValue<byte[]>(1))));
            }

            if (chunks.Count == 0)
            {
                await CleanupDurableFoldWorkAsync(token, ct).ConfigureAwait(false);
                lock (_durableFoldScheduleLock) _durableFoldScheduled.Remove(token);
                scheduled = false;
                _foldDepth.Release();
                ownsDepth = false;
                return;
            }

            Interlocked.CompareExchange(
                ref _foldSpanStarted,
                System.Diagnostics.Stopwatch.GetTimestamp(),
                comparand: 0);

            var completions = new List<Task>();
            foreach (var typeGroup in chunks
                         .GroupBy(static chunk => chunk.Type)
                         .OrderBy(static group => group.Key, Hash128BytewiseComparer.Instance))
            {
                Hash128 type = typeGroup.Key;
                int[] ordinals = typeGroup.Select(static chunk => chunk.Ordinal)
                    .OrderBy(static ordinal => ordinal)
                    .ToArray();

                Task next;
                lock (_laneLock)
                {
                    Task prior = _typeLanes.TryGetValue(type, out var existing)
                        ? existing : Task.CompletedTask;
                    next = Task.Run(async () =>
                    {
                        await prior.ConfigureAwait(false);
                        await Parallel.ForEachAsync(
                            ordinals,
                            new ParallelOptions
                            {
                                MaxDegreeOfParallelism = Math.Min(FoldConnections, ordinals.Length),
                                CancellationToken = CancellationToken.None,
                            },
                            async (ordinal, tokenCt) =>
                                await ProcessDurableFoldChunkAsync(token, ordinal, tokenCt)
                                    .ConfigureAwait(false)).ConfigureAwait(false);
                    }, CancellationToken.None);
                    _typeLanes[type] = next;
                }
                completions.Add(next);
            }

            Task tracked = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(completions).ConfigureAwait(false);
                    await CleanupDurableFoldWorkAsync(token, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    lock (_durableFoldScheduleLock)
                        _durableFoldScheduled.Remove(token);
                    _foldDepth.Release();
                }
            }, CancellationToken.None);

            lock (_foldChainLock)
                _outstanding.Add(tracked);
            ownsDepth = false;
            scheduled = false; // tracked task owns both cleanup responsibilities now.
        }
        catch
        {
            if (scheduled)
                lock (_durableFoldScheduleLock) _durableFoldScheduled.Remove(token);
            if (ownsDepth) _foldDepth.Release();
            throw;
        }
    }

    private async Task CleanupDurableFoldWorkAsync(Hash128 token, CancellationToken ct)
    {
        await using var cleanup = _ds.CreateCommand(
            "DELETE FROM laplace.ingest_fold_work w WHERE w.working_set_id=$1 "
            + "AND NOT EXISTS (SELECT 1 FROM laplace.ingest_fold_chunk c "
            + "WHERE c.working_set_id=w.working_set_id)");
        cleanup.Parameters.Add(new NpgsqlParameter
        { Value = token.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
        await cleanup.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task ProcessDurableFoldChunkAsync(
        Hash128 token, int ordinal, CancellationToken ct)
    {
        var retry = Laplace.Ingestion.TransientErrorRetryPolicy.ConcurrencyRetry;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await ProcessDurableFoldChunkAttemptAsync(token, ordinal, ct)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException
                && attempt + 1 < retry.MaxAttempts
                && retry.IsTransient(ex))
            {
                TimeSpan delay = retry.DelayBeforeAttempt(attempt, Random.Shared);
                _log.LogWarning(
                    ex,
                    "durable fold token={Token} chunk={Ordinal} transient failure attempt={Attempt}; retrying",
                    token, ordinal, attempt + 1);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessDurableFoldChunkAttemptAsync(
        Hash128 token, int ordinal, CancellationToken ct)
    {
        await _foldConnections.WaitAsync(ct).ConfigureAwait(false);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await using var connection = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            Hash128 type;
            byte[][] subjects;
            byte[][] objectsStored;
            bool[] objectNulls;
            long[] phis;
            long[] games;
            long[] sums;
            DateTime[] observed;
            long[] opponents;
            long[] periodOffsets;
            long[] periodOpponents;
            long[] periodPhis;
            long[] periodGames;
            long[] periodSums;

            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandTimeout = 0;
                read.CommandText =
                    "SELECT type_id,subjects,objects,object_nulls,phis,games,sums,"
                    + "observed_at,opponents,period_offsets,period_opponents,period_phis,"
                    + "period_games,period_sums FROM laplace.ingest_fold_chunk "
                    + "WHERE working_set_id=$1 AND ordinal=$2 FOR UPDATE";
                read.Parameters.Add(new NpgsqlParameter { Value = token.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
                read.Parameters.Add(new NpgsqlParameter { Value = ordinal, NpgsqlDbType = NpgsqlDbType.Integer });
                await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    await transaction.RollbackAsync(ct).ConfigureAwait(false);
                    return;
                }
                type = Hash128.FromBytes(reader.GetFieldValue<byte[]>(0));
                subjects = reader.GetFieldValue<byte[][]>(1);
                objectsStored = reader.GetFieldValue<byte[][]>(2);
                objectNulls = reader.GetFieldValue<bool[]>(3);
                phis = reader.GetFieldValue<long[]>(4);
                games = reader.GetFieldValue<long[]>(5);
                sums = reader.GetFieldValue<long[]>(6);
                observed = reader.GetFieldValue<DateTime[]>(7);
                opponents = reader.GetFieldValue<long[]>(8);
                periodOffsets = reader.GetFieldValue<long[]>(9);
                periodOpponents = reader.GetFieldValue<long[]>(10);
                periodPhis = reader.GetFieldValue<long[]>(11);
                periodGames = reader.GetFieldValue<long[]>(12);
                periodSums = reader.GetFieldValue<long[]>(13);
            }

            var objects = new byte[objectsStored.Length][];
            for (int i = 0; i < objects.Length; i++)
                objects[i] = objectNulls[i] ? null! : objectsStored[i];

            bool epoch = await SupportsApplyWriteEpochAsync(connection, ct).ConfigureAwait(false);
            await using (var fold = connection.CreateCommand())
            {
                fold.Transaction = transaction;
                fold.CommandTimeout = 0;
                string sql =
                    "SELECT consensus.upsert_type($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)";
                fold.CommandText = epoch
                    ? "WITH epoch AS MATERIALIZED (SELECT nextval('laplace.apply_write_epoch')) "
                        + sql + " FROM epoch"
                    : sql;
                fold.Parameters.Add(new NpgsqlParameter { Value = type.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
                fold.Parameters.Add(new NpgsqlParameter { Value = subjects, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                fold.Parameters.Add(new NpgsqlParameter { Value = objects, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                fold.Parameters.Add(new NpgsqlParameter { Value = phis, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                fold.Parameters.Add(new NpgsqlParameter { Value = games, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                fold.Parameters.Add(new NpgsqlParameter { Value = sums, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                fold.Parameters.Add(new NpgsqlParameter { Value = observed, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
                fold.Parameters.Add(new NpgsqlParameter { Value = opponents, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                fold.Parameters.Add(new NpgsqlParameter { Value = periodOffsets, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                fold.Parameters.Add(new NpgsqlParameter { Value = periodOpponents, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                fold.Parameters.Add(new NpgsqlParameter { Value = periodPhis, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                fold.Parameters.Add(new NpgsqlParameter { Value = periodGames, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                fold.Parameters.Add(new NpgsqlParameter { Value = periodSums, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                long folded = (long)(await fold.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
                Interlocked.Add(ref _cellsFolded, folded);
                Interlocked.Increment(ref _consensusUpsertCalls);
            }

            var pairs = new HashSet<Hash128>();
            for (int i = 0; i < subjects.Length; i++)
            {
                pairs.Add(Hash128.FromBytes(subjects[i]));
                if (!objectNulls[i]) pairs.Add(Hash128.FromBytes(objectsStored[i]));
            }
            var ordered = pairs.OrderBy(static id => id, Hash128BytewiseComparer.Instance).ToArray();
            long maskUpdated = 0;
            for (int off = 0; off < ordered.Length; off += FoldSizing.ChunkCells)
            {
                int count = Math.Min(FoldSizing.ChunkCells, ordered.Length - off);
                var entityIds = new byte[count][];
                var typeIds = new byte[count][];
                byte[] typeBytes = type.ToBytes();
                for (int i = 0; i < count; i++)
                {
                    entityIds[i] = ordered[off + i].ToBytes();
                    typeIds[i] = typeBytes;
                }
                await using var mask = connection.CreateCommand();
                mask.Transaction = transaction;
                mask.CommandTimeout = 0;
                mask.CommandText = "SELECT consensus.highway_mask_deposit($1,$2)";
                mask.Parameters.Add(new NpgsqlParameter { Value = entityIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                mask.Parameters.Add(new NpgsqlParameter { Value = typeIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                maskUpdated += (long)(await mask.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
                Interlocked.Increment(ref _highwayMaskCalls);
                Interlocked.Add(ref _highwayMaskPairs, count);
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText =
                    "DELETE FROM laplace.ingest_fold_chunk WHERE working_set_id=$1 AND ordinal=$2";
                delete.Parameters.Add(new NpgsqlParameter { Value = token.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
                delete.Parameters.Add(new NpgsqlParameter { Value = ordinal, NpgsqlDbType = NpgsqlDbType.Integer });
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            _ = maskUpdated;
        }
        finally
        {
            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref _consensusBackendTicks, elapsed);
            _foldConnections.Release();
        }
    }

    private sealed record DurableFoldChunk(
        int Ordinal,
        Hash128 Type,
        byte[][] Subjects,
        byte[][] Objects,
        bool[] ObjectNulls,
        long[] Phis,
        long[] Games,
        long[] Sums,
        DateTime[] ObservedAt,
        long[] Opponents,
        long[] PeriodOffsets,
        long[] PeriodOpponents,
        long[] PeriodPhis,
        long[] PeriodGames,
        long[] PeriodSums);

    private sealed class Hash128BytewiseComparer : IComparer<Hash128>
    {
        internal static readonly Hash128BytewiseComparer Instance = new();
        public int Compare(Hash128 x, Hash128 y) => x.CompareToBytewise(y);
    }
}
