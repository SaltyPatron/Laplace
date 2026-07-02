using System.Buffers.Binary;
using global::Npgsql;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Rule #8 step 4 — glicko-2 matchups accumulate AND FOLD client-side. The
/// flat period fold: no staging tables, no per-row PL/pgSQL
/// laplace_glicko2_accumulate_games() calls. The client already holds the
/// whole period's pre-merged matchups (one Acc per edge, φ-invariant
/// enforced at accumulation); the fold is the same native glicko-2 the
/// server called — glicko2_fold_uniform_period against the neutral opponent
/// — run in-process at microseconds per edge, then written with exactly two
/// bulk statements: COPY for new edges, one unnest UPDATE for existing.
///
/// Parity with the retired materialize_period_partition is by construction:
/// neutral seeds and tau are read in-transaction from the same SQL functions
/// the server fold used (no C# literals to drift), the edge id is the same
/// blake3(subject‖type‖object-or-zero) (ConsensusKeysParityTests), and the
/// fold math is bit-equal (Glicko2FoldParityTests: FoldUniformPeriod ==
/// AccumulateGames == the SQL binding). witness_count = prior + games;
/// last_observed_at = the period's max ts (the server overwrote, not
/// GREATEST — replicated exactly).
/// </summary>
public sealed partial class ConsensusAccumulatingWriter
{
    private const int FoldProbeChunkIds = 131_072;
    private static readonly long PgEpochTicks =
        new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    private async Task FoldChainClientAsync(Task prev, int epoch, ICollection<Acc> edges)
    {
        await prev.ConfigureAwait(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long folded = await FoldSnapshotClientAsync(edges, CancellationToken.None).ConfigureAwait(false);
        sw.Stop();
        Interlocked.Add(ref _foldedRelations, folded);
        int done = Interlocked.Increment(ref _epochsFolded);
        _log.LogInformation(
            "consensus fold e{Epoch} (client): {Relations:N0} relations folded in {Ms:N0}ms ({Rps:N0} rel/s); epochs folded {Folded}/{Staged}",
            epoch, folded, sw.ElapsedMilliseconds,
            folded / Math.Max(1e-3, sw.Elapsed.TotalSeconds),
            done, Volatile.Read(ref _epochsStaged));
    }

    private async Task<long> FoldSnapshotClientAsync(ICollection<Acc> edges, CancellationToken ct)
    {
        if (edges.Count == 0) return 0;

        int n = edges.Count;
        var accs = new Acc[n];
        edges.CopyTo(accs, 0);
        var cids = new Hash128[n];
        for (int i = 0; i < n; i++)
            cids[i] = ConsensusKeys.EdgeId(accs[i].Subject, accs[i].Type, accs[i].Object ?? default);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var guc = conn.CreateCommand())
            {
                guc.Transaction = tx;
                guc.CommandText =
                    "SET LOCAL session_replication_role = replica; "
                    + "SET LOCAL synchronous_commit = off; "
                    + "SET LOCAL jit = off; "
                    + "SELECT pg_advisory_xact_lock(hashtextextended('laplace_consensus_fold', 0))";
                await guc.ExecuteNonQueryAsync(ct);
            }

            long neutralMu, initialRd, initialVol, tau;
            await using (var consts = conn.CreateCommand())
            {
                consts.Transaction = tx;
                consts.CommandText =
                    "SELECT laplace.glicko2_neutral_mu(), laplace.glicko2_initial_rd(), "
                    + "laplace.glicko2_initial_volatility(), laplace.glicko2_tau()";
                await using var rd = await consts.ExecuteReaderAsync(ct);
                await rd.ReadAsync(ct);
                neutralMu = rd.GetInt64(0);
                initialRd = rd.GetInt64(1);
                initialVol = rd.GetInt64(2);
                tau = rd.GetInt64(3);
            }

            var priors = _freshSource
                ? new Dictionary<Hash128, (long Rating, long Rd, long Vol, long Witnesses)>()
                : await ReadPriorsAsync(cids, ct);

            // Fold math is pure native glicko-2 — stripe it across P-cores.
            var foldedStates = new Glicko2State[n];
            var foldedWitness = new long[n];
            var hasPriorFlags = new bool[n];
            int mathWorkers = Math.Min(Math.Max(1, n / 65_536) + 1, NpgsqlSubstrateWriter.ApplyParallelism);
            if (mathWorkers <= 1)
            {
                FoldStripe(0, 1);
            }
            else
            {
                await CpuTopology.RunPinnedAsyncParallel(mathWorkers, (w, _) =>
                {
                    FoldStripe(w, mathWorkers);
                    return Task.CompletedTask;
                }, ct);
            }

            void FoldStripe(int offset, int stride)
            {
                for (int i = offset; i < n; i += stride)
                {
                    var acc = accs[i];
                    bool hasPrior = priors.TryGetValue(cids[i], out var p);
                    var st = hasPrior
                        ? Glicko2.Init(p.Rating, p.Rd, p.Vol)
                        : Glicko2.Init(neutralMu, initialRd, initialVol);
                    Glicko2.FoldUniformPeriod(
                        ref st, neutralMu, acc.PhiFp1e9, acc.Games, acc.SumScoreFp1e9, tau, 0);
                    foldedStates[i] = st;
                    foldedWitness[i] = (hasPrior ? p.Witnesses : 0) + acc.Games;
                    hasPriorFlags[i] = hasPrior;
                }
            }

            var novel = new List<int>(n);
            var updIds = new List<byte[]>();
            var updRating = new List<long>();
            var updRd = new List<long>();
            var updVol = new List<long>();
            var updWitness = new List<long>();
            var updTs = new List<DateTime>();
            for (int i = 0; i < n; i++)
            {
                if (hasPriorFlags[i])
                {
                    updIds.Add(cids[i].ToBytes());
                    updRating.Add(foldedStates[i].RatingFp1e9);
                    updRd.Add(foldedStates[i].RdFp1e9);
                    updVol.Add(foldedStates[i].VolatilityFp1e9);
                    updWitness.Add(foldedWitness[i]);
                    updTs.Add(TsFromUnixUs(accs[i].MaxTsUnixUs));
                }
                else
                {
                    novel.Add(i);
                }
            }

            if (novel.Count > 0)
            {
                var copySw = System.Diagnostics.Stopwatch.StartNew();
                int groups = (int)Math.Min(NpgsqlSubstrateWriter.ApplyParallelism,
                    Math.Max(1L, novel.Count / 16_384));
                // Disjoint id ranges per connection — same
                // LWLock:BufferContent avoidance as the apply lane.
                novel.Sort((a, b) => cids[a].CompareToBytewise(cids[b]));
                int per = (novel.Count + groups - 1) / groups;
                await CpuTopology.RunPinnedAsyncParallel(groups, async (g, token) =>
                {
                    int start = g * per;
                    if (start >= novel.Count) return;
                    int count = Math.Min(per, novel.Count - start);

                    await using var groupConn = await _ds.OpenConnectionAsync(token);
                    await using var groupTx = await groupConn.BeginTransactionAsync(token);
                    await using (var guc = groupConn.CreateCommand())
                    {
                        guc.Transaction = groupTx;
                        guc.CommandText =
                            "SET LOCAL session_replication_role = replica; "
                            + "SET LOCAL synchronous_commit = off; SET LOCAL jit = off";
                        await guc.ExecuteNonQueryAsync(token);
                    }
                    await using (var stream = await groupConn.BeginRawBinaryCopyAsync(
                        "COPY laplace.consensus (id, subject_id, type_id, object_id, "
                        + "rating, rd, volatility, witness_count, last_observed_at) "
                        + "FROM STDIN (FORMAT BINARY)", token))
                    {
                        var copy = new PgCopyRowBuffer(stream);
                        for (int k = start; k < start + count; k++)
                        {
                            int i = novel[k];
                            await copy.EnsureRoomAsync(FoldRowMaxBytes, token);
                            copy.Commit(WriteConsensusRow(
                                copy.Array, copy.Filled, cids[i], accs[i],
                                foldedStates[i], foldedWitness[i]));
                        }
                        await copy.FinalizeAsync(token);
                    }
                    await groupTx.CommitAsync(token);
                }, ct);
                copySw.Stop();
                _log.LogInformation(
                    "consensus fold copy: {Rows:N0} novel relations across {Groups} connection(s) in {Ms:N0}ms ({Rps:N0} rows/s)",
                    novel.Count, groups, copySw.ElapsedMilliseconds,
                    novel.Count / Math.Max(1e-3, copySw.Elapsed.TotalSeconds));
            }

            if (updIds.Count > 0)
            {
                await using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandTimeout = 0;
                upd.CommandText =
                    "UPDATE laplace.consensus c SET "
                    + "  rating = d.rating, rd = d.rd, volatility = d.volatility, "
                    + "  witness_count = d.witness_count, last_observed_at = d.ts "
                    + "FROM (SELECT unnest($1::bytea[]) AS id, unnest($2::bigint[]) AS rating, "
                    + "             unnest($3::bigint[]) AS rd, unnest($4::bigint[]) AS volatility, "
                    + "             unnest($5::bigint[]) AS witness_count, unnest($6::timestamptz[]) AS ts) d "
                    + "WHERE c.id = d.id";
                upd.Parameters.Add(new NpgsqlParameter
                { Value = updIds.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                upd.Parameters.Add(new NpgsqlParameter
                { Value = updRating.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                upd.Parameters.Add(new NpgsqlParameter
                { Value = updRd.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                upd.Parameters.Add(new NpgsqlParameter
                { Value = updVol.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                upd.Parameters.Add(new NpgsqlParameter
                { Value = updWitness.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                upd.Parameters.Add(new NpgsqlParameter
                { Value = updTs.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
                await upd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch { }
            throw;
        }
        return n;
    }

    private async Task<Dictionary<Hash128, (long Rating, long Rd, long Vol, long Witnesses)>>
        ReadPriorsAsync(Hash128[] cids, CancellationToken ct)
    {
        var priors = new Dictionary<Hash128, (long, long, long, long)>();
        if (cids.Length == 0) return priors;

        int chunkCount = (cids.Length + FoldProbeChunkIds - 1) / FoldProbeChunkIds;
        var perChunk = new List<(Hash128, (long, long, long, long))>[chunkCount];

        async Task ReadChunkAsync(int c, CancellationToken token)
        {
            int start = c * FoldProbeChunkIds;
            int m = Math.Min(FoldProbeChunkIds, cids.Length - start);
            var chunk = new byte[m][];
            for (int i = 0; i < m; i++) chunk[i] = cids[start + i].ToBytes();

            await using var conn = await _ds.OpenConnectionAsync(token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText =
                "SELECT id, rating, rd, volatility, witness_count "
                + "FROM laplace.consensus WHERE id = ANY($1)";
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = chunk, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            var rows = new List<(Hash128, (long, long, long, long))>();
            await using var rd = await cmd.ExecuteReaderAsync(token);
            while (await rd.ReadAsync(token))
                rows.Add((Hash128.FromBytes((byte[])rd[0]),
                          (rd.GetInt64(1), rd.GetInt64(2), rd.GetInt64(3), rd.GetInt64(4))));
            perChunk[c] = rows;
        }

        if (chunkCount == 1)
        {
            await ReadChunkAsync(0, ct);
        }
        else
        {
            int workers = Math.Min(chunkCount, Math.Min(NpgsqlSubstrateWriter.ApplyParallelism, 8));
            int next = -1;
            await CpuTopology.RunPinnedAsyncParallel(workers, async (_, token) =>
            {
                for (int c = Interlocked.Increment(ref next); c < chunkCount;
                     c = Interlocked.Increment(ref next))
                    await ReadChunkAsync(c, token);
            }, ct);
        }

        foreach (var rows in perChunk)
            if (rows is not null)
                foreach (var (id, v) in rows) priors[id] = v;
        return priors;
    }

    // 2 (field count) + 4×20 (id/subject/type/object) + 4×12 (int8s) + 12 (ts)
    private const int FoldRowMaxBytes = 2 + 80 + 48 + 12;

    private static int WriteConsensusRow(
        Span<byte> dst, int o, in Hash128 cid, Acc acc, in Glicko2State st, long witnessCount)
    {
        BinaryPrimitives.WriteInt16BigEndian(dst[o..], 9); o += 2;
        o = PgBinaryCopy.WriteHash(dst, o, cid);
        o = PgBinaryCopy.WriteHash(dst, o, acc.Subject);
        o = PgBinaryCopy.WriteHash(dst, o, acc.Type);
        if (acc.Object is Hash128 obj) o = PgBinaryCopy.WriteHash(dst, o, obj);
        else { BinaryPrimitives.WriteInt32BigEndian(dst[o..], -1); o += 4; }
        o = PgBinaryCopy.WriteInt64Field(dst, o, st.RatingFp1e9);
        o = PgBinaryCopy.WriteInt64Field(dst, o, st.RdFp1e9);
        o = PgBinaryCopy.WriteInt64Field(dst, o, st.VolatilityFp1e9);
        o = PgBinaryCopy.WriteInt64Field(dst, o, witnessCount);
        o = PgBinaryCopy.WriteInt64Field(dst, o, acc.MaxTsUnixUs - PgEpochDeltaUs);
        return o;
    }

    private static DateTime TsFromUnixUs(long unixUs) =>
        new(checked((unixUs - PgEpochDeltaUs) * 10 + PgEpochTicks), DateTimeKind.Utc);
}
