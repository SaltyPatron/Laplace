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
/// bulk statements: COPY for new edges + chunked unnest UPDATE for existing,
/// both on the fold advisory-lock transaction (atomic period write).
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
    // Write unnests stay smaller than prior-read chunks: a single ~219k-row
    // bytea[]+bigint[] UPDATE AV'd postgres 18.0.1 (0xc0000005) mid-WordNet fold.
    private const int FoldWriteChunkIds = 32_768;

    private async Task FoldChainClientAsync(Task prev, int epoch, ICollection<Acc> edges)
    {
        await prev.ConfigureAwait(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long folded;
        try
        {
            folded = await FoldSnapshotClientAsync(edges, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Timestamp the failure at the moment it happens. The chain waiters
            // rethrow this later, but "later" can be minutes away — without this
            // line the log shows nothing between "queued" and the eventual crash.
            _log.LogError(ex, "consensus fold e{Epoch} FAILED ({Edges:N0} edges)", epoch, edges.Count);
            throw;
        }
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

        // Refresh consensus id-stats before the prior-read. A multi-epoch fold writes
        // millions of new rows per epoch; between autoanalyze fires (threshold 2% of
        // n_live) a stale reltuples flips ReadPriorsAsync's `WHERE id = ANY($1)` from a
        // PK index scan to a seq scan — the non-monotonic fold-rate decay observed live
        // on the UD run (e4 dropped to 21k rel/s, e5 recovered once autoanalyze caught
        // up). Column-scoped on id => ANALYZE samples ~30k rows (seconds) and refreshes
        // pg_class.reltuples, the estimate the plan choice turns on; it never touches the
        // PostGIS ND-stats on physicalities. Skipped for fresh sources (no priors read).
        if (!_freshSource)
        {
            await using var anConn = await _ds.OpenConnectionAsync(ct);
            await using var anCmd = anConn.CreateCommand();
            anCmd.CommandTimeout = 0;
            anCmd.CommandText = "ANALYZE laplace.consensus (id)";
            await anCmd.ExecuteNonQueryAsync(ct);
        }

        _log.LogInformation(
            "consensus fold: {Edges:N0} edges — acquiring fold lock, reading priors, folding (silence here is work, not a hang; lock waits log the holder every 30s)",
            n);
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await AdvisoryTxLock.BeginWithLockAsync(
            conn, "laplace_consensus_fold",
            "SET LOCAL session_replication_role = replica; "
            + "SET LOCAL synchronous_commit = off; "
            + "SET LOCAL jit = off; ",
            _log, ct);
        try
        {

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
                : await ReadPriorsAsync(cids, accs, ct);

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
            var updTypes = new List<byte[]>();
            var updSubjects = new List<byte[]>();
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
                    updTypes.Add(accs[i].Type.ToBytes());
                    updSubjects.Add(accs[i].Subject.ToBytes());
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
                // Novel COPY must ride the SAME transaction as the prior UPDATE.
                // Parallel side-connection COPYs used to Commit before UPDATE — a
                // postgres AV mid-UPDATE (WordNet 2026-07-11) then left millions of
                // consensus rows durable while the fold failed. One conn/tx: crash
                // rolls back the whole period fold.
                var copySw = System.Diagnostics.Stopwatch.StartNew();
                novel.Sort((a, b) => cids[a].CompareToBytewise(cids[b]));
                await using (var stream = await conn.BeginRawBinaryCopyAsync(
                    "COPY laplace.consensus (id, subject_id, type_id, object_id, "
                    + "rating, rd, volatility, witness_count, last_observed_at) "
                    + "FROM STDIN (FORMAT BINARY)", ct))
                {
                    var copy = new PgCopyRowBuffer(stream);
                    for (int k = 0; k < novel.Count; k++)
                    {
                        int i = novel[k];
                        await copy.EnsureRoomAsync(FoldRowMaxBytes, ct);
                        copy.Commit(WriteConsensusRow(
                            copy.Array, copy.Filled, cids[i], accs[i],
                            foldedStates[i], foldedWitness[i]));
                    }
                    await copy.FinalizeAsync(ct);
                }
                copySw.Stop();
                _log.LogInformation(
                    "consensus fold copy: {Rows:N0} novel relations on fold tx in {Ms:N0}ms ({Rps:N0} rows/s)",
                    novel.Count, copySw.ElapsedMilliseconds,
                    novel.Count / Math.Max(1e-3, copySw.Elapsed.TotalSeconds));
            }

            if (updIds.Count > 0)
            {
                // Chunk the prior-refresh UPDATE. A single unnest of hundreds of
                // thousands of bytea[]+bigint[] arrays AV'd postgres.exe
                // (0xc0000005) mid-WordNet fold 2026-07-11 16:40:38 WER — the
                // client saw "connection forcibly closed" / crash recovery.
                // type_id/subject_id ride along so runtime pruning routes each
                // UPDATE row to its single partition (see ReadPriorsAsync).
                const string updSql =
                    "UPDATE laplace.consensus c SET "
                    + "  rating = d.rating, rd = d.rd, volatility = d.volatility, "
                    + "  witness_count = d.witness_count, last_observed_at = d.ts "
                    + "FROM (SELECT unnest($1::bytea[]) AS id, unnest($2::bigint[]) AS rating, "
                    + "             unnest($3::bigint[]) AS rd, unnest($4::bigint[]) AS volatility, "
                    + "             unnest($5::bigint[]) AS witness_count, unnest($6::timestamptz[]) AS ts, "
                    + "             unnest($7::bytea[]) AS type_id, unnest($8::bytea[]) AS subject_id) d "
                    + "WHERE c.id = d.id AND c.type_id = d.type_id AND c.subject_id = d.subject_id";
                for (int off = 0; off < updIds.Count; off += FoldWriteChunkIds)
                {
                    int m = Math.Min(FoldWriteChunkIds, updIds.Count - off);
                    await using var upd = conn.CreateCommand();
                    upd.Transaction = tx;
                    upd.CommandTimeout = 0;
                    upd.CommandText = updSql;
                    upd.Parameters.Add(new NpgsqlParameter
                    { Value = updIds.GetRange(off, m).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                    upd.Parameters.Add(new NpgsqlParameter
                    { Value = updRating.GetRange(off, m).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                    upd.Parameters.Add(new NpgsqlParameter
                    { Value = updRd.GetRange(off, m).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                    upd.Parameters.Add(new NpgsqlParameter
                    { Value = updVol.GetRange(off, m).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                    upd.Parameters.Add(new NpgsqlParameter
                    { Value = updWitness.GetRange(off, m).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                    upd.Parameters.Add(new NpgsqlParameter
                    { Value = updTs.GetRange(off, m).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
                    upd.Parameters.Add(new NpgsqlParameter
                    { Value = updTypes.GetRange(off, m).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                    upd.Parameters.Add(new NpgsqlParameter
                    { Value = updSubjects.GetRange(off, m).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                    await upd.ExecuteNonQueryAsync(ct);
                }
            }

            // Highway relation-membership masks: DEPOSITED from what this fold already
            // holds — every (entity, relation-type) pair its edges touched — via
            // highway_mask_deposit (OR-accumulate, add-only at ingest, chunk-order-free).
            // No consensus re-reads: the old per-epoch refresh re-derived masks from
            // consensus participation and its object-side join probed every leaf per
            // touched entity (75s of a 118s fold on the partitioned layout). On a
            // greenfield seed this deposit IS the population; the refresh/rebuild
            // repair verbs are legacy for pre-deposit databases only.
            {
                var maskSw = System.Diagnostics.Stopwatch.StartNew();
                var pairs = new HashSet<(Hash128 Ent, Hash128 Typ)>(n * 2);
                for (int i = 0; i < n; i++)
                {
                    pairs.Add((accs[i].Subject, accs[i].Type));
                    if (accs[i].Object is { } obj) pairs.Add((obj, accs[i].Type));
                }
                var pairEnts = new byte[pairs.Count][];
                var pairTypes = new byte[pairs.Count][];
                int ti = 0;
                foreach (var (ent, typ) in pairs)
                {
                    pairEnts[ti] = ent.ToBytes();
                    pairTypes[ti] = typ.ToBytes();
                    ti++;
                }
                long masksWritten = 0;
                for (int off = 0; off < pairEnts.Length; off += FoldWriteChunkIds)
                {
                    int m = Math.Min(FoldWriteChunkIds, pairEnts.Length - off);
                    await using var mask = conn.CreateCommand();
                    mask.Transaction = tx;
                    mask.CommandTimeout = 0;
                    mask.CommandText = "SELECT laplace.highway_mask_deposit($1, $2)";
                    mask.Parameters.Add(new NpgsqlParameter
                    { Value = pairEnts[off..(off + m)], NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                    mask.Parameters.Add(new NpgsqlParameter
                    { Value = pairTypes[off..(off + m)], NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                    masksWritten += (long)(await mask.ExecuteScalarAsync(ct) ?? 0L);
                }
                maskSw.Stop();
                _log.LogInformation(
                    "consensus fold highway masks: {Written:N0} deposited from {Pairs:N0} touched (entity,type) pairs in {Ms:N0}ms",
                    masksWritten, pairEnts.Length, maskSw.ElapsedMilliseconds);
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
        ReadPriorsAsync(Hash128[] cids, Acc[] accs, CancellationToken ct)
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
            var types = new byte[m][];
            var subjects = new byte[m][];
            for (int i = 0; i < m; i++)
            {
                chunk[i] = cids[start + i].ToBytes();
                types[i] = accs[start + i].Type.ToBytes();
                subjects[i] = accs[start + i].Subject.ToBytes();
            }

            await using var conn = await _ds.OpenConnectionAsync(token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;
            // The probe carries the partition keys (type_id, subject_id) alongside
            // the id: consensus is LIST(type_id) + HASH(subject_id)-partitioned, so
            // a bare `id = ANY($1)` would probe every leaf's PK index per chunk.
            // Joining on all three keys lets runtime pruning route each probe to
            // its single leaf partition.
            cmd.CommandText =
                "SELECT c.id, c.rating, c.rd, c.volatility, c.witness_count "
                + "FROM (SELECT unnest($1::bytea[]) AS id, unnest($2::bytea[]) AS type_id, "
                + "             unnest($3::bytea[]) AS subject_id) k "
                + "JOIN laplace.consensus c ON c.id = k.id AND c.type_id = k.type_id "
                + "                        AND c.subject_id = k.subject_id";
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = chunk, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = types, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = subjects, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
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
        AttestationMergeMath.TimestampFromUnixMicros(unixUs, PgEpochDeltaUs);
}
