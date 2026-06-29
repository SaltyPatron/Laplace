using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using global::Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed class ConsensusAccumulatingWriter : ISubstrateWriter, IAsyncDisposable
{
    public const string PeriodBoundaryUnitPrefix = "period-boundary/";

    private readonly ISubstrateWriter _inner;
    private readonly NpgsqlDataSource _ds;
    private readonly int _stagingThreshold;
    private readonly int _partitions;
    private readonly int _maxFoldBacklog;
    private readonly bool _freshSource;
    private readonly bool _persistEvidence;
    private readonly ILogger _log;

    private sealed class Acc
    {
        public Hash128  Subject;
        public Hash128  Type;
        public Hash128? Object;
        public long     PhiFp1e9;
        public long     Games;
        public long     SumScoreFp1e9;
        public long     MaxTsUnixUs;
    }

    private ConcurrentDictionary<(Hash128 S, Hash128 K, Hash128? O), Acc> _accumulation = new();
    private long _observationsAccumulated;
    private readonly bool _terminalFold;
    private readonly bool _stageAsWalks;
    private int  _periodEpoch;
    private bool _sweptStale;
    private bool _anyEpochCreated;
    private long _foldedRelations;
    private int  _epochsStaged;
    private int  _epochsFolded;
    private Task _foldChain = Task.CompletedTask;
    private readonly SemaphoreSlim _stagingGate = new(1, 1);
    private readonly object _accumLock = new();
    private int _inflightApplies;
    private volatile bool _disposing;

    public bool PersistEvidence => _persistEvidence;

    
    
    
    
    
    
    public ConsensusAccumulatingWriter(
        ISubstrateWriter inner, NpgsqlDataSource dataSource,
        int? stagingThresholdRelations = null, int? foldWorkers = null,
        bool freshSource = false, bool? persistEvidence = null,
        bool stageAsWalks = false,
        ILogger<ConsensusAccumulatingWriter>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _freshSource = freshSource;
        _stageAsWalks = stageAsWalks;
        _persistEvidence = persistEvidence ?? ResolvePersistEvidence();
        _log = logger ?? (ILogger)NullLogger<ConsensusAccumulatingWriter>.Instance;
        if (!_persistEvidence)
            _log.LogInformation(
                "consensus-only deposit: accumulating and folding relations; laplace.attestations writes skipped");
        
        
        
        
        // Peak RAM of the in-memory (subject,type,object)->Acc accumulator is bounded by this
        // threshold (the dict is swapped out to staging when it reaches it). 20M distinct relations
        // is multiple GB for relation-heavy sources (ConceptNet) — the third measured client-RAM
        // source after the unbounded compose channel and the batchSize*32 over-alloc. 4M keeps the
        // working set well under a GB; the native parallel walk fold drains periods concurrently.
        // (Phase 4 replaces this dict entirely with the walk journal.) Overridable via env.
        _stagingThreshold = stagingThresholdRelations
            ?? (int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_STAGING_THRESHOLD"), out var t) && t > 0
                ? t : 4_000_000);
        _partitions = foldWorkers
            ?? CpuTopology.ResolveCpuBoundWorkers(headroom: 1, maxCap: 24);
        
        
        
        _maxFoldBacklog = int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_FOLD_BACKLOG_MAX"), out var bl)
            ? bl : 12;
        
        
        
        
        
        var lane = Environment.GetEnvironmentVariable("LAPLACE_FOLD_LANE");
        _terminalFold = string.Equals(lane, "terminal", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(lane, "bulk", StringComparison.OrdinalIgnoreCase);
        if (_terminalFold)
            _log.LogInformation(
                "terminal fold: epochs stage to disk and fold once at materialize (finish_consensus_fold)");
        if (_partitions is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(foldWorkers), _partitions,
                "fold workers must be in 1..64 (create_period_staging's partition bound)");
    }

    public int RelationCount => _accumulation.Count;

    public long ObservationsAccumulated => Interlocked.Read(ref _observationsAccumulated);

    public int FoldWorkers => _partitions;

    public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
        => ApplyManyAsync(new[] { change }, ct);

    public async Task<ApplyResult> ApplyManyAsync(
        IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (_disposing) throw new ObjectDisposedException(nameof(ConsensusAccumulatingWriter));
        Interlocked.Increment(ref _inflightApplies);
        try
        {
            if (_disposing) throw new ObjectDisposedException(nameof(ConsensusAccumulatingWriter));
            return await ApplyManyCoreAsync(changes, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _inflightApplies);
        }
    }

    private async Task<ApplyResult> ApplyManyCoreAsync(
        IReadOnlyList<SubstrateChange> changes, CancellationToken ct)
    {
        bool boundary = false;
        foreach (var c in changes)
        {
            if (c.Metadata.SourceContentUnitName.StartsWith("layer-complete/", StringComparison.Ordinal))
                continue;
            if (c.Metadata.SourceContentUnitName.StartsWith(PeriodBoundaryUnitPrefix, StringComparison.Ordinal))
            {
                boundary = true;
                continue;
            }
            foreach (var a in c.Attestations) Accumulate(a);
            if (!c.TestimonyWalks.IsDefaultOrEmpty)
                foreach (var w in c.TestimonyWalks) BufferWalk(w);
        }

        if (_walkBuffered >= WalkFlushRows)
            await FlushWalksAsync(ct);

        if ((boundary && _accumulation.Count >= Math.Max(1, _stagingThreshold / 8))
            || _accumulation.Count >= _stagingThreshold)
            await FlushPeriodAsync(ct);

        return await _inner.ApplyManyAsync(ForwardChanges(changes), ct);
    }

    
    
    
    
    public async Task<ApplyResult> AppendAsync(
        IReadOnlyList<SubstrateChange> changes, Hash128 sourceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (_disposing) throw new ObjectDisposedException(nameof(ConsensusAccumulatingWriter));
        Interlocked.Increment(ref _inflightApplies);
        try
        {
            if (_disposing) throw new ObjectDisposedException(nameof(ConsensusAccumulatingWriter));
            return await AppendCoreAsync(changes, sourceId, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _inflightApplies);
        }
    }

    private async Task<ApplyResult> AppendCoreAsync(
        IReadOnlyList<SubstrateChange> changes, Hash128 sourceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);

        bool boundary = false;
        foreach (var c in changes)
        {
            if (c.Metadata.SourceContentUnitName.StartsWith("layer-complete/", StringComparison.Ordinal))
                continue;
            if (c.Metadata.SourceContentUnitName.StartsWith(PeriodBoundaryUnitPrefix, StringComparison.Ordinal))
            {
                boundary = true;
                continue;
            }
            foreach (var a in c.Attestations) Accumulate(a);
            if (!c.TestimonyWalks.IsDefaultOrEmpty)
                foreach (var w in c.TestimonyWalks) BufferWalk(w);
        }

        if (_walkBuffered >= WalkFlushRows)
            await FlushWalksAsync(ct);

        if ((boundary && _accumulation.Count >= Math.Max(1, _stagingThreshold / 8))
            || _accumulation.Count >= _stagingThreshold)
            await FlushPeriodAsync(ct);

        return await _inner.AppendAsync(ForwardChanges(changes), sourceId, ct);
    }

    public Task<(int Entities, int Physicalities, int Attestations)> FinalizeSourceAsync(
        Hash128 sourceId, CancellationToken ct = default)
        => _inner.FinalizeSourceAsync(sourceId, ct);

    public static bool ResolvePersistEvidence()
    {
        string? persist = Environment.GetEnvironmentVariable("LAPLACE_PERSIST_EVIDENCE");
        if (!string.IsNullOrEmpty(persist))
            return !IsEnvDisabled(persist);
        string? evidence = Environment.GetEnvironmentVariable("LAPLACE_EVIDENCE");
        if (!string.IsNullOrEmpty(evidence))
            return !IsEnvDisabled(evidence);
        return true;
    }

    private static bool IsEnvDisabled(string value) =>
        value is "0" or "false" or "False" or "no" or "NO" or "off" or "OFF";

    private IReadOnlyList<SubstrateChange> ForwardChanges(IReadOnlyList<SubstrateChange> changes)
    {
        
        
        bool anyToStrip = false;
        foreach (var c in changes)
        {
            if ((!_persistEvidence && !c.Attestations.IsEmpty) || !c.TestimonyWalks.IsDefaultOrEmpty)
            {
                anyToStrip = true;
                break;
            }
        }
        if (!anyToStrip) return changes;

        var stripped = new SubstrateChange[changes.Count];
        for (int i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            if (!_persistEvidence && !c.Attestations.IsEmpty)
                c = c with { Attestations = ImmutableArray<AttestationRow>.Empty };
            if (!c.TestimonyWalks.IsDefaultOrEmpty)
                c = c with { TestimonyWalks = ImmutableArray<TestimonyWalkRow>.Empty };
            stripped[i] = c;
        }
        return stripped;
    }

    private void Accumulate(AttestationRow a)
    {
        if (_disposing) throw new ObjectDisposedException(nameof(ConsensusAccumulatingWriter));
        lock (_accumLock)
        {
            var acc = _accumulation.GetOrAdd((a.SubjectId, a.TypeId, a.ObjectId), static _ => new Acc());
            lock (acc)
            {
                if (acc.Games == 0)
                {
                    acc.Subject = a.SubjectId; acc.Type = a.TypeId; acc.Object = a.ObjectId;
                    acc.PhiFp1e9 = a.OpponentRdFp1e9;
                }
                else if (acc.PhiFp1e9 != a.OpponentRdFp1e9)
                {
                    throw new InvalidOperationException(
                        $"accumulation invariant violated: relation observed with φ={a.OpponentRdFp1e9} after φ={acc.PhiFp1e9} in the same period");
                }
                acc.Games        += a.ObservationCount;
                acc.SumScoreFp1e9 = checked(acc.SumScoreFp1e9
                    + (a.SumScoreFp1e9 ?? checked(a.ScoreFp1e9 * a.ObservationCount)));
                if (a.LastObservedAtUnixUs > acc.MaxTsUnixUs) acc.MaxTsUnixUs = a.LastObservedAtUnixUs;
            }
        }
        Interlocked.Add(ref _observationsAccumulated, a.ObservationCount);
    }

    
    
    
    
    

    private const int WalkFlushRows = 65_536;
    private List<TestimonyWalkRow>[]? _walkBuffers;
    private int _walkBuffered;
    private bool _walkStagingCreated;
    private NpgsqlConnection? _walkConn;

    private void BufferWalk(TestimonyWalkRow w)
    {
        if (!_stageAsWalks)
            throw new InvalidOperationException(
                "testimony walks arrived but the writer is not in walk-journal mode: "
                + "thread stageAsWalks from the ingest entry point (mixed walk/flat "
                + "staging is refused at the fold)");
        BufferWalkCore(w);
        Interlocked.Add(ref _observationsAccumulated, w.GamesTotal);
    }

    private void BufferWalkCore(TestimonyWalkRow w)
    {
        _walkBuffers ??= Enumerable.Range(0, _partitions)
            .Select(_ => new List<TestimonyWalkRow>()).ToArray();
        _walkBuffers[(int)(w.Subject.Lo % (ulong)_partitions)].Add(w);
        _walkBuffered++;
    }

    private async Task FlushWalksAsync(CancellationToken ct)
    {
        if (_walkBuffers is null || _walkBuffered == 0) return;
        await _stagingGate.WaitAsync(ct);
        try
        {
            await FlushWalksLockedAsync(ct);
        }
        finally
        {
            _stagingGate.Release();
        }
    }

    
    
    
    private async Task FlushWalksLockedAsync(CancellationToken ct)
    {
        if (_walkBuffers is null || _walkBuffered == 0) return;
        _walkConn ??= await _ds.OpenConnectionAsync(ct);
        if (!_walkStagingCreated)
        {
            if (!_sweptStale)
            {
                await using var sweep = _walkConn.CreateCommand();
                sweep.CommandText = "SELECT laplace.drop_period_staging()";
                await sweep.ExecuteNonQueryAsync(ct);
                _sweptStale = true;
            }
            await using var create = _walkConn.CreateCommand();
            create.CommandText = $"SELECT laplace.create_walk_staging({_partitions})";
            await create.ExecuteNonQueryAsync(ct);
            _walkStagingCreated = true;
            _anyEpochCreated = true;
        }
        for (int p = 0; p < _partitions; p++)
        {
            var rows = _walkBuffers[p];
            if (rows.Count == 0) continue;
            await using var stream = await _walkConn.BeginRawBinaryCopyAsync(
                $"COPY laplace.consensus_walk_staging_{p} "
                + "(subject_id, type_id, context_id, phi, n_vertices, games_total, last_ts, walk) "
                + "FROM STDIN (FORMAT BINARY)", ct);
            await WriteWalkRowsAsync(stream, rows, ct);
            rows.Clear();
        }
        _walkBuffered = 0;
    }

    
    
    
    
    private static async Task WriteWalkRowsAsync(
        Stream stream, List<TestimonyWalkRow> rows, CancellationToken ct)
    {
        
        
        
        
        const int fixedRowBytes = 2 + 20 * 3 + 12 + 8 + 12 + 12 + 4;
        var copy = new PgCopyRowBuffer(stream);
        foreach (var w in rows)
        {
            ct.ThrowIfCancellationRequested();
            await copy.EnsureRoomAsync(fixedRowBytes + w.PackedVertices.Length, ct);
            copy.Commit(WriteWalkRow(copy.Array, copy.Filled, w));
        }
        await copy.FinalizeAsync(ct);
    }

    private static int WriteWalkRow(Span<byte> dst, int o, TestimonyWalkRow w)
    {
        BinaryPrimitives.WriteInt16BigEndian(dst[o..], 8); o += 2;
        o = PgBinaryCopy.WriteHash(dst, o, w.Subject);
        o = PgBinaryCopy.WriteHash(dst, o, w.TypeId);
        if (w.ContextId is { } ctx) o = PgBinaryCopy.WriteHash(dst, o, ctx);
        else { BinaryPrimitives.WriteInt32BigEndian(dst[o..], -1); o += 4; }
        o = PgBinaryCopy.WriteInt64Field(dst, o, w.PhiFp1e9);
        BinaryPrimitives.WriteInt32BigEndian(dst[o..], 4); o += 4;
        BinaryPrimitives.WriteInt32BigEndian(dst[o..], w.Count); o += 4;
        o = PgBinaryCopy.WriteInt64Field(dst, o, w.GamesTotal);
        o = PgBinaryCopy.WriteInt64Field(dst, o, w.ObservedAtUnixUs - PgEpochDeltaUs);
        BinaryPrimitives.WriteInt32BigEndian(dst[o..], w.PackedVertices.Length); o += 4;
        w.PackedVertices.CopyTo(dst[o..]); o += w.PackedVertices.Length;
        return o;
    }

    
    
    
    
    private int PartitionOf(Acc acc)
        => (int)((acc.Subject.Lo ^ acc.Type.Lo ^ (acc.Object?.Lo ?? 0UL)) % (ulong)_partitions);

    public async Task FlushPeriodAsync(CancellationToken ct = default)
    {
        await _stagingGate.WaitAsync(ct);
        try
        {
            if (_stageAsWalks)
            {
                
                
                
                
                ConcurrentDictionary<(Hash128, Hash128, Hash128?), Acc> snap;
                lock (_accumLock)
                {
                    snap = _accumulation;
                    _accumulation = new ConcurrentDictionary<(Hash128, Hash128, Hash128?), Acc>();
                }
                if (!snap.IsEmpty)
                {
                    foreach (var acc in snap.Values)
                        BufferWalkCore(ConvertPartialToWalk(acc));
                    _log.LogInformation(
                        "consensus stage (walk journal): {Relations:N0} partial relations journaled as testimony walks",
                        snap.Count);
                }
                await FlushWalksLockedAsync(ct);
                return;
            }

            if (_maxFoldBacklog > 0 && !_terminalFold)
            {
                bool throttled = false;
                while (Volatile.Read(ref _epochsStaged) - Volatile.Read(ref _epochsFolded) >= _maxFoldBacklog)
                {
                    if (!throttled)
                    {
                        throttled = true;
                        _log.LogInformation(
                            "consensus stage throttled: fold backlog {Depth} >= {Max}; holding staging (accumulator keeps merging in RAM) until folds drain",
                            Volatile.Read(ref _epochsStaged) - Volatile.Read(ref _epochsFolded), _maxFoldBacklog);
                    }
                    await Task.WhenAny(_foldChain, Task.Delay(TimeSpan.FromSeconds(5), ct));
                    ct.ThrowIfCancellationRequested();
                }
            }

            ConcurrentDictionary<(Hash128, Hash128, Hash128?), Acc> snapshot;
            lock (_accumLock)
            {
                snapshot = _accumulation;
                _accumulation = new ConcurrentDictionary<(Hash128, Hash128, Hash128?), Acc>();
            }
            if (snapshot.IsEmpty) return;

            int epoch = ++_periodEpoch;
            var stageSw = System.Diagnostics.Stopwatch.StartNew();

            await using (var conn = await _ds.OpenConnectionAsync(ct))
            {
                if (!_sweptStale)
                {
                    await using var sweep = conn.CreateCommand();
                    sweep.CommandText = "SELECT laplace.drop_period_staging()";
                    await sweep.ExecuteNonQueryAsync(ct);
                    _sweptStale = true;
                }
                await using var ddl = conn.CreateCommand();
                ddl.CommandText = "SELECT laplace.create_period_staging($1, $2)";
                ddl.Parameters.AddWithValue(_partitions);
                ddl.Parameters.AddWithValue(epoch);
                await ddl.ExecuteNonQueryAsync(ct);
                _anyEpochCreated = true;
            }

            var buckets = new List<Acc>[_partitions];
            for (int k = 0; k < _partitions; k++) buckets[k] = new List<Acc>();
            foreach (var acc in snapshot.Values) buckets[PartitionOf(acc)].Add(acc);

            await CpuTopology.RunPinnedAsyncParallel(_partitions, async (part, token) =>
            {
                await CopyPartitionAsync(epoch, part, buckets[part], token);
            }, ct);
            stageSw.Stop();
            int staged = Interlocked.Increment(ref _epochsStaged);
            _log.LogInformation(
                "consensus stage e{Epoch}: {Relations:N0} partial relations → {Partitions} partition(s) in {Ms:N0}ms ({Rps:N0} rel/s); fold queue depth {Depth}",
                epoch, snapshot.Count, _partitions, stageSw.ElapsedMilliseconds,
                snapshot.Count / Math.Max(1e-3, stageSw.Elapsed.TotalSeconds),
                staged - Volatile.Read(ref _epochsFolded));

            if (!_terminalFold)
            {
                var prev = _foldChain;
                _foldChain = ChainFoldAsync(prev, epoch);
            }
        }
        finally
        {
            _stagingGate.Release();
        }
    }

    
    
    
    private static TestimonyWalkRow ConvertPartialToWalk(Acc acc)
    {
        long games = acc.Games, sum = acc.SumScoreFp1e9;
        long q = sum / games, rem = sum % games;
        if (rem < 0) { q--; rem += games; }

        var objects = new List<Hash128>(2);
        var scores  = new List<long>(2);
        var runs    = new List<ushort>(2);
        Hash128 obj = acc.Object ?? default;
        AppendRuns(objects, scores, runs, obj, q, games - rem);
        AppendRuns(objects, scores, runs, obj, q + 1, rem);

        byte[] packed = TestimonyWalk.Pack(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(objects),
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(scores),
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(runs));
        return new TestimonyWalkRow(
            acc.Subject, acc.Type, null, acc.PhiFp1e9,
            packed, objects.Count, games, acc.MaxTsUnixUs);
    }

    private static void AppendRuns(
        List<Hash128> objects, List<long> scores, List<ushort> runs,
        Hash128 obj, long score, long count)
    {
        while (count > 0)
        {
            ushort run = count > ushort.MaxValue ? ushort.MaxValue : (ushort)count;
            objects.Add(obj);
            scores.Add(score);
            runs.Add(run);
            count -= run;
        }
    }

    private async Task ChainFoldAsync(Task prev, int epoch)
    {
        await prev.ConfigureAwait(false);
        var foldSw = System.Diagnostics.Stopwatch.StartNew();
        var counts = new long[_partitions];
        await CpuTopology.RunPinnedAsyncParallel(_partitions, async (part, token) =>
        {
            await using var conn = await _ds.OpenConnectionAsync(token).ConfigureAwait(false);
            conn.Notice += (_, e) =>
                _log.LogInformation("consensus fold: {Message}", e.Notice.MessageText);
            await using (var sub = conn.CreateCommand())
            {
                sub.CommandText = "SET client_min_messages = 'log'";
                await sub.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await using var tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false);
            try
            {
                await using (var guc = conn.CreateCommand())
                {
                    guc.Transaction = tx;
                    guc.CommandText = "SET LOCAL session_replication_role = replica";
                    await guc.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                await using var mat = conn.CreateCommand();
                mat.Transaction = tx;
                mat.CommandTimeout = 0;
                mat.CommandText = _freshSource
                    ? "SELECT laplace.materialize_period_partition_fresh(laplace.period_staging_table($1, $2))"
                    : "SELECT laplace.materialize_period_partition(laplace.period_staging_table($1, $2))";
                mat.Parameters.AddWithValue(epoch);
                mat.Parameters.AddWithValue(part);
                counts[part] = (long)(await mat.ExecuteScalarAsync(token).ConfigureAwait(false) ?? 0L);
                await tx.CommitAsync(token).ConfigureAwait(false);
            }
            catch
            {
                try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { }
                throw;
            }
        }, CancellationToken.None).ConfigureAwait(false);
        foldSw.Stop();
        long total = counts.Sum();
        Interlocked.Add(ref _foldedRelations, total);
        int folded = Interlocked.Increment(ref _epochsFolded);
        _log.LogInformation(
            "consensus fold e{Epoch}: {Relations:N0} relations materialized across {Partitions} partition(s) in {Ms:N0}ms ({Rps:N0} rel/s); epochs folded {Folded}/{Staged}",
            epoch, total, _partitions, foldSw.ElapsedMilliseconds,
            total / Math.Max(1e-3, foldSw.Elapsed.TotalSeconds),
            folded, Volatile.Read(ref _epochsStaged));
    }

    private const long PgEpochDeltaUs = 946_684_800_000_000L;
    private const int  MaxRowBytes    = 110;

    private static int WriteRow(Span<byte> dst, int o, Acc acc)
    {
        BinaryPrimitives.WriteInt16BigEndian(dst[o..], 7); o += 2;
        o = PgBinaryCopy.WriteHash(dst, o, acc.Subject);
        o = PgBinaryCopy.WriteHash(dst, o, acc.Type);
        if (acc.Object is Hash128 obj) o = PgBinaryCopy.WriteHash(dst, o, obj);
        else { BinaryPrimitives.WriteInt32BigEndian(dst[o..], -1); o += 4; }
        o = PgBinaryCopy.WriteInt64Field(dst, o, acc.PhiFp1e9);
        o = PgBinaryCopy.WriteInt64Field(dst, o, acc.Games);
        o = PgBinaryCopy.WriteInt64Field(dst, o, acc.SumScoreFp1e9);
        o = PgBinaryCopy.WriteInt64Field(dst, o, acc.MaxTsUnixUs - PgEpochDeltaUs);
        return o;
    }

    private async Task CopyPartitionAsync(int epoch, int partition, List<Acc> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        string table = $"consensus_period_staging_e{epoch:D4}_{partition}";
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var stream = await conn.BeginRawBinaryCopyAsync(
            $"COPY laplace.{table} "
          + "(subject_id, type_id, object_id, phi, games, sum_score, last_ts) "
          + "FROM STDIN (FORMAT BINARY)", ct);

        var copy = new PgCopyRowBuffer(stream);
        foreach (var acc in rows)
        {
            await copy.EnsureRoomAsync(MaxRowBytes, ct);
            copy.Commit(WriteRow(copy.Array, copy.Filled, acc));
        }
        await copy.FinalizeAsync(ct);
    }

    /// <summary>
    /// Online turn-by-turn fold for iterative modalities (e.g. chess self-play). Flushes the
    /// currently-accumulated edges as one rating period and AWAITS the incremental in-place fold —
    /// <c>materialize_period_partition</c>, which reads each edge's existing rating as the Glicko-2
    /// prior and <c>ON CONFLICT DO UPDATE</c>s only the touched edges (then drops its staging table).
    /// Unlike <see cref="MaterializeConsensusAsync"/> this NEVER runs the full-table rebuild/swap
    /// (<c>finish_consensus_fold</c> / <c>consensus_fold_swap</c>) or the walk fold, so the updated
    /// consensus rows are queryable the instant it returns — the immediate, no-drain update. Requires
    /// the flat incremental lane (stageAsWalks:false, not the terminal/bulk lane) and a seeded source
    /// (freshSource:false) so existing ratings are carried forward as priors. Returns the cumulative
    /// folded-relation count.
    /// </summary>
    public async Task<long> FoldIncrementalAsync(CancellationToken ct = default)
    {
        if (_stageAsWalks)
            throw new InvalidOperationException(
                "FoldIncrementalAsync requires the flat incremental lane; this writer is in walk-journal "
                + "mode (walks only fold at MaterializeConsensusAsync via the terminal walk fold)");
        if (_terminalFold)
            throw new InvalidOperationException(
                "FoldIncrementalAsync requires the incremental lane; LAPLACE_FOLD_LANE=terminal/bulk "
                + "defers all folding to the full rebuild at MaterializeConsensusAsync");

        await FlushPeriodAsync(ct).ConfigureAwait(false);
        // FlushPeriodAsync chained this period's per-partition materialize_period_partition fold into
        // _foldChain (and that task awaits all prior chained folds); await it so the touched consensus
        // edges are updated in place before we return.
        await _foldChain.ConfigureAwait(false);
        return Interlocked.Read(ref _foldedRelations);
    }

    public async Task<long> MaterializeConsensusAsync(CancellationToken ct = default)
    {
        await FlushWalksAsync(ct);
        await FlushPeriodAsync(ct);

        
        
        
        
        if (_stageAsWalks
            && _partitions > 1
            && (Environment.GetEnvironmentVariable("LAPLACE_FOLD_IMPL") ?? "engine") != "sql"
            && Environment.GetEnvironmentVariable("LAPLACE_FOLD_PARALLEL") != "0")
        {
            return await ParallelWalkFoldAsync(ct);
        }

        if (_terminalFold || _stageAsWalks)
        {
            
            
            
            
            
            
            
            if (_stageAsWalks && !_terminalFold)
                _log.LogInformation("walk journal staged: materializing through the terminal walk fold");
            string impl = Environment.GetEnvironmentVariable("LAPLACE_FOLD_IMPL") ?? "engine";
            bool resumable = !_stageAsWalks
                && Environment.GetEnvironmentVariable("LAPLACE_FOLD_RESUMABLE") == "1";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await using var conn = await _ds.OpenConnectionAsync(ct);
            conn.Notice += (_, e) =>
                _log.LogInformation("terminal fold: {Message}", e.Notice.MessageText);
            await using (var sub = conn.CreateCommand())
            {
                sub.CommandText = "SET client_min_messages = 'log'";
                await sub.ExecuteNonQueryAsync(ct);
            }
            await using (var laneCmd = conn.CreateCommand())
            {
                laneCmd.CommandText = $"SET laplace.fold_lane = '{(impl == "sql" ? "sql" : "engine")}'";
                await laneCmd.ExecuteNonQueryAsync(ct);
            }
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await using (var guc = conn.CreateCommand())
                {
                    guc.Transaction = tx;
                    guc.CommandText = "SET LOCAL session_replication_role = replica";
                    await guc.ExecuteNonQueryAsync(ct);
                }
                await using var fin = conn.CreateCommand();
                fin.Transaction = tx;
                fin.CommandTimeout = 0;
                fin.CommandText = resumable
                    ? "CALL laplace.finish_consensus_fold_steps(NULL)"
                    : "SELECT laplace.finish_consensus_fold()";
                long n = (long)(await fin.ExecuteScalarAsync(ct) ?? 0L);
                await tx.CommitAsync(ct);
                Interlocked.Add(ref _foldedRelations, n);
                _log.LogInformation(
                    "terminal fold complete: {Relations:N0} consensus relations in {Sec:F0}s ({Rps:N0} rel/s)",
                    n, sw.Elapsed.TotalSeconds, n / Math.Max(1e-3, sw.Elapsed.TotalSeconds));
                return Interlocked.Read(ref _foldedRelations);
            }
            catch
            {
                try { await tx.RollbackAsync(CancellationToken.None); }
                catch { }
                throw;
            }
        }
        await _foldChain;
        return Interlocked.Read(ref _foldedRelations);
    }

    
    
    
    
    
    
    private async Task<long> ParallelWalkFoldAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int nwalk;
        bool fresh;
        await using (var conn = await _ds.OpenConnectionAsync(ct))
        {
            await using var prep = conn.CreateCommand();
            prep.CommandTimeout = 0;
            prep.CommandText = "SELECT nwalk, fresh FROM laplace.walk_fold_prepare()";
            await using var r = await prep.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            nwalk = r.GetInt32(0);
            fresh = r.GetBoolean(1);
        }
        _log.LogInformation(
            "parallel walk fold: {Nwalk} partition(s) over {Mode} consensus",
            nwalk, fresh ? "fresh" : "seeded");

        var folds = new Task<long>[nwalk];
        for (int k = 0; k < nwalk; k++)
        {
            int p = k;
            folds[p] = Task.Run(async () =>
            {
                await using var c = await _ds.OpenConnectionAsync().ConfigureAwait(false);
                c.Notice += (_, e) =>
                    _log.LogInformation("walk fold: {Message}", e.Notice.MessageText);
                await using (var sub = c.CreateCommand())
                {
                    sub.CommandText = "SET client_min_messages = 'log'";
                    await sub.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                await using var cmd = c.CreateCommand();
                cmd.CommandTimeout = 0;
                cmd.CommandText = "SELECT laplace.consensus_fold_walks($1, $2, $3)";
                cmd.Parameters.AddWithValue(p);
                cmd.Parameters.AddWithValue(nwalk);
                cmd.Parameters.AddWithValue(!fresh);
                return (long)(await cmd.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L);
            });
        }
        long total = (await Task.WhenAll(folds).ConfigureAwait(false)).Sum();

        await using (var conn = await _ds.OpenConnectionAsync(ct))
        {
            conn.Notice += (_, e) =>
                _log.LogInformation("walk fold finalize: {Message}", e.Notice.MessageText);
            await using (var sub = conn.CreateCommand())
            {
                sub.CommandText = "SET client_min_messages = 'log'";
                await sub.ExecuteNonQueryAsync(ct);
            }
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await using (var guc = conn.CreateCommand())
                {
                    guc.Transaction = tx;
                    guc.CommandText = "SET LOCAL session_replication_role = replica";
                    await guc.ExecuteNonQueryAsync(ct);
                }
                await using var fin = conn.CreateCommand();
                fin.Transaction = tx;
                fin.CommandTimeout = 0;
                fin.CommandText = "SELECT laplace.walk_fold_finalize()";
                await fin.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                try { await tx.RollbackAsync(CancellationToken.None); }
                catch { }
                throw;
            }
        }
        Interlocked.Add(ref _foldedRelations, total);
        _log.LogInformation(
            "parallel walk fold complete: {Relations:N0} consensus relations in {Sec:F0}s ({Rps:N0} rel/s)",
            total, sw.Elapsed.TotalSeconds, total / Math.Max(1e-3, sw.Elapsed.TotalSeconds));
        return Interlocked.Read(ref _foldedRelations);
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        var spin = new SpinWait();
        while (Interlocked.CompareExchange(ref _inflightApplies, 0, 0) > 0)
            spin.SpinOnce();

        try
        {
            await _foldChain;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "in-flight period fold failed before dispose; staging sweep follows");
        }
        if (_walkConn is not null)
        {
            await _walkConn.DisposeAsync();
            _walkConn = null;
        }
        if (_anyEpochCreated)
        {
            try
            {
                await using var conn = await _ds.OpenConnectionAsync();
                await using var drop = conn.CreateCommand();
                drop.CommandText = "SELECT laplace.drop_period_staging()";
                await drop.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "period staging sweep on dispose failed; next run's first flush sweeps it");
            }
            _anyEpochCreated = false;
        }
        _stagingGate.Dispose();
    }
}
