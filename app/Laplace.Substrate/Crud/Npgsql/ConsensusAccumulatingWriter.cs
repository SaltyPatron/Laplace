using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using global::Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed partial class ConsensusAccumulatingWriter : ISubstrateWriter, IAsyncDisposable
{
    public const string PeriodBoundaryUnitPrefix = IngestBatchPipeline.PeriodBoundaryUnitPrefix;

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
        public Hash128 Subject;
        public Hash128 Type;
        public Hash128? Object;
        public long PhiFp1e9;
        public long Games;
        public long SumScoreFp1e9;
        public long MaxTsUnixUs;
    }

    private ConcurrentDictionary<(Hash128 S, Hash128 K, Hash128? O), Acc> _accumulation = new();
    private long _observationsAccumulated;
    private readonly bool _terminalFold;
    private readonly bool _stageAsWalks;
    private int _periodEpoch;
    private bool _sweptStale;
    private bool _anyEpochCreated;
    private long _foldedRelations;
    private int _epochsStaged;
    private int _epochsFolded;
    private Task _foldChain = Task.CompletedTask;
    private readonly SemaphoreSlim _stagingGate = new(1, 1);
    // Read side = concurrent Accumulate (per-edge Acc locks handle merge);
    // write side = the period snapshot swap. The previous plain lock
    // serialized every attestation of every compose worker in the process.
    private readonly ReaderWriterLockSlim _accumLock = new();
    // Attestations of a working set are accumulated ACROSS CORES, not serially on
    // the single consumer thread. Accumulate() is thread-safe (read-lock +
    // ConcurrentDictionary + per-Acc lock), so this removes the Amdahl serial
    // section where a multi-million-row working set merged one edge at a time.
    private readonly int _accumulateWorkers =
        CpuTopology.ResolveCpuBoundWorkers(headroom: 1);
    private const int ParallelAccumulateMin = 50_000;
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










        _stagingThreshold = stagingThresholdRelations ?? MemoryTopology.ConsensusFoldMaxRelations;
        _partitions = foldWorkers
            ?? CpuTopology.ResolveCpuBoundWorkers(headroom: 1, maxCap: 24);

        _maxFoldBacklog = 12;
        _terminalFold = false;
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
            return await ApplyManyCoreAsync(changes, ct, workingSet: false);
        }
        finally
        {
            Interlocked.Decrement(ref _inflightApplies);
        }
    }

    public Task<ApplyResult> ApplyWorkingSetAsync(SubstrateChange change, CancellationToken ct = default)
        => ApplyWorkingSetAsync(new[] { change }, ct);

    public async Task<ApplyResult> ApplyWorkingSetAsync(
        IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (_disposing) throw new ObjectDisposedException(nameof(ConsensusAccumulatingWriter));
        Interlocked.Increment(ref _inflightApplies);
        try
        {
            if (_disposing) throw new ObjectDisposedException(nameof(ConsensusAccumulatingWriter));
            return await ApplyManyCoreAsync(changes, ct, workingSet: true);
        }
        finally
        {
            Interlocked.Decrement(ref _inflightApplies);
        }
    }

    private async Task<ApplyResult> ApplyManyCoreAsync(
        IReadOnlyList<SubstrateChange> changes, CancellationToken ct, bool workingSet = false)
    {
        Dictionary<(Hash128 S, Hash128 K, Hash128? O), Acc>? commitBatchAcc = null;

        bool boundary = false;
        List<SubstrateChange>? accChanges = null;
        List<TestimonyWalkRow>? pendingWalks = null;
        foreach (var c in changes)
        {
            if (c.Metadata.SourceContentUnitName.StartsWith("layer-complete/", StringComparison.Ordinal))
                continue;
            if (c.Metadata.SourceContentUnitName.StartsWith(PeriodBoundaryUnitPrefix, StringComparison.Ordinal))
            {
                boundary = true;
                continue;
            }
            if (_stageAsWalks && !_persistEvidence)
            {
                foreach (var a in c.Attestations)
                    MergeAttestationInto(ref commitBatchAcc, a);
            }
            else if (!c.Attestations.IsEmpty)
            {
                (accChanges ??= new List<SubstrateChange>(changes.Count)).Add(c);
            }
            if (!c.TestimonyWalks.IsDefaultOrEmpty)
                foreach (var w in c.TestimonyWalks) (pendingWalks ??= new List<TestimonyWalkRow>()).Add(w);
        }

        // Evidence apply FIRST; fold state only after it succeeds. The old order
        // accumulated (and could period-flush) before the inner apply, so a
        // transient apply failure + IngestRunner retry re-accumulated the whole
        // batch — silently doubling games/sum_score in the fold (the phi
        // invariant can't catch it: same phi) — and an abandoned batch left
        // staged matchups whose evidence never landed. A throw below leaves the
        // accumulator untouched, so a retried batch folds exactly once.
        var result = workingSet
            ? await _inner.ApplyWorkingSetAsync(ForwardChanges(changes), ct)
            : await _inner.ApplyManyAsync(ForwardChanges(changes), ct);

        if (accChanges is not null)
            AccumulateChangesParallel(accChanges, ct);

        if (commitBatchAcc is not null)
        {
            foreach (var acc in commitBatchAcc.Values)
                BufferWalkCore(ConvertPartialToWalk(acc));
        }
        if (pendingWalks is not null)
            foreach (var w in pendingWalks) BufferWalk(w);

        if (_walkBuffered >= WalkFlushRows)
            await FlushWalksAsync(ct);

        if ((boundary && _accumulation.Count >= Math.Max(1, _stagingThreshold / 8))
            || _accumulation.Count >= _stagingThreshold)
            await FlushPeriodAsync(ct);

        if (_stageAsWalks && _walkBuffered > 0)
            await FlushWalksAsync(ct);

        return result;
    }

    private void MergeAttestationInto(
        ref Dictionary<(Hash128 S, Hash128 K, Hash128? O), Acc>? batch, AttestationRow a)
    {
        batch ??= new Dictionary<(Hash128, Hash128, Hash128?), Acc>();
        var key = (a.SubjectId, a.TypeId, a.ObjectId);
        if (!batch.TryGetValue(key, out var acc))
        {
            acc = new Acc
            {
                Subject = a.SubjectId,
                Type = a.TypeId,
                Object = a.ObjectId,
                PhiFp1e9 = a.OpponentRdFp1e9,
                Games = a.ObservationCount,
                SumScoreFp1e9 = a.SumScoreFp1e9
                    ?? AttestationMergeMath.SafeScoreTimesCount(a.ScoreFp1e9, a.ObservationCount),
                MaxTsUnixUs = a.LastObservedAtUnixUs,
            };
            batch[key] = acc;
            Interlocked.Add(ref _observationsAccumulated, a.ObservationCount);
            return;
        }
        if (acc.PhiFp1e9 != a.OpponentRdFp1e9)
            throw new InvalidOperationException(
                $"accumulation invariant violated: relation observed with φ={a.OpponentRdFp1e9} after φ={acc.PhiFp1e9} in the same commit");
        acc.Games = AttestationMergeMath.SafeAddGames(acc.Games, a.ObservationCount);
        acc.SumScoreFp1e9 = AttestationMergeMath.SafeAddScores(
            acc.SumScoreFp1e9,
            a.SumScoreFp1e9 ?? AttestationMergeMath.SafeScoreTimesCount(a.ScoreFp1e9, a.ObservationCount));
        if (a.LastObservedAtUnixUs > acc.MaxTsUnixUs) acc.MaxTsUnixUs = a.LastObservedAtUnixUs;
        Interlocked.Add(ref _observationsAccumulated, a.ObservationCount);
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
        List<SubstrateChange>? accChanges = null;
        List<TestimonyWalkRow>? pendingWalks = null;
        foreach (var c in changes)
        {
            if (c.Metadata.SourceContentUnitName.StartsWith("layer-complete/", StringComparison.Ordinal))
                continue;
            if (c.Metadata.SourceContentUnitName.StartsWith(PeriodBoundaryUnitPrefix, StringComparison.Ordinal))
            {
                boundary = true;
                continue;
            }
            if (!c.Attestations.IsEmpty)
                (accChanges ??= new List<SubstrateChange>(changes.Count)).Add(c);
            if (!c.TestimonyWalks.IsDefaultOrEmpty)
                foreach (var w in c.TestimonyWalks) (pendingWalks ??= new List<TestimonyWalkRow>()).Add(w);
        }

        // Same retry law as ApplyManyCoreAsync: evidence lands first, fold
        // state mutates only after success, so retries fold exactly once.
        var result = await _inner.AppendAsync(ForwardChanges(changes), sourceId, ct);

        if (accChanges is not null)
            AccumulateChangesParallel(accChanges, ct);
        if (pendingWalks is not null)
            foreach (var w in pendingWalks) BufferWalk(w);

        if (_walkBuffered >= WalkFlushRows)
            await FlushWalksAsync(ct);

        if ((boundary && _accumulation.Count >= Math.Max(1, _stagingThreshold / 8))
            || _accumulation.Count >= _stagingThreshold)
            await FlushPeriodAsync(ct);

        return result;
    }

    public Task<(int Entities, int Physicalities, int Attestations)> FinalizeSourceAsync(
        Hash128 sourceId, CancellationToken ct = default)
        => _inner.FinalizeSourceAsync(sourceId, ct);

    public Task BeginBulkRunAsync(CancellationToken ct = default)
        => _inner.BeginBulkRunAsync(ct);

    public Task CompleteBulkRunAsync(CancellationToken ct = default)
        => _inner.CompleteBulkRunAsync(ct);

    public static bool ResolvePersistEvidence() => true;

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
        _accumLock.EnterReadLock();
        try
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
                acc.Games = AttestationMergeMath.SafeAddGames(acc.Games, a.ObservationCount);
                acc.SumScoreFp1e9 = AttestationMergeMath.SafeAddScores(
                    acc.SumScoreFp1e9,
                    AttestationMergeMath.RowScoreTotal(a));
                if (a.LastObservedAtUnixUs > acc.MaxTsUnixUs) acc.MaxTsUnixUs = a.LastObservedAtUnixUs;
            }
        }
        finally
        {
            _accumLock.ExitReadLock();
        }
        Interlocked.Add(ref _observationsAccumulated, a.ObservationCount);
    }

    // Accumulate every attestation of a working set across cores. Each change's
    // attestation array is split into ~4x-worker chunks so even a SINGLE giant
    // change (one working-set intent with millions of attestations, e.g.
    // conceptnet/opensubtitles) fans out to all workers instead of one thread.
    // Accumulate() is thread-safe, so this is a pure parallelization of the merge.
    private void AccumulateChangesParallel(List<SubstrateChange> accChanges, CancellationToken ct)
    {
        long total = 0;
        foreach (var c in accChanges) total += c.Attestations.Length;

        if (total < ParallelAccumulateMin || _accumulateWorkers <= 1)
        {
            foreach (var c in accChanges)
            {
                var atts = c.Attestations;
                for (int i = 0; i < atts.Length; i++) Accumulate(atts[i]);
            }
            return;
        }

        int targetChunks = _accumulateWorkers * 4;
        int chunkSize = (int)Math.Max(4096, (total + targetChunks - 1) / targetChunks);
        var work = new List<(ImmutableArray<AttestationRow> Arr, int Start, int End)>();
        foreach (var c in accChanges)
        {
            var atts = c.Attestations;
            for (int s = 0; s < atts.Length; s += chunkSize)
                work.Add((atts, s, Math.Min(s + chunkSize, atts.Length)));
        }

        Parallel.ForEach(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = _accumulateWorkers, CancellationToken = ct },
            wk =>
            {
                var atts = wk.Arr;
                for (int i = wk.Start; i < wk.End; i++) Accumulate(atts[i]);
            });
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





    public async Task FlushPeriodAsync(CancellationToken ct = default)
    {
        await _stagingGate.WaitAsync(ct);
        try
        {
            if (_stageAsWalks)
            {




                ConcurrentDictionary<(Hash128, Hash128, Hash128?), Acc> snap;
                _accumLock.EnterWriteLock();
                try
                {
                    snap = _accumulation;
                    _accumulation = new ConcurrentDictionary<(Hash128, Hash128, Hash128?), Acc>();
                }
                finally { _accumLock.ExitWriteLock(); }
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

            if (_maxFoldBacklog > 0)
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
                    // Task.WhenAny never throws. A faulted fold chain stops
                    // _epochsFolded from ever advancing, which used to turn this
                    // loop into a silent forever-spin — the ingest "hung" while
                    // the real exception sat unobserved. Surface it instead.
                    if (_foldChain.IsFaulted)
                        await _foldChain;
                }
            }

            ConcurrentDictionary<(Hash128, Hash128, Hash128?), Acc> snapshot;
            _accumLock.EnterWriteLock();
            try
            {
                snapshot = _accumulation;
                _accumulation = new ConcurrentDictionary<(Hash128, Hash128, Hash128?), Acc>();
            }
            finally { _accumLock.ExitWriteLock(); }
            if (snapshot.IsEmpty) return;

            int epoch = ++_periodEpoch;
            int staged = Interlocked.Increment(ref _epochsStaged);
            _log.LogInformation(
                "consensus period e{Epoch}: {Relations:N0} pre-merged relations queued for client fold; fold queue depth {Depth}",
                epoch, snapshot.Count, staged - Volatile.Read(ref _epochsFolded));

            var prev = _foldChain;
            _foldChain = FoldChainClientAsync(prev, epoch, snapshot.Values);
        }
        finally
        {
            _stagingGate.Release();
        }
    }




    // Run counts are almost always <= 2 (one run per score value; ushort splits
    // only past 65,535 games), so the buffers live on the stack and this method
    // allocates nothing but the packed payload.
    private const int RunStackMax = 8;

    private static TestimonyWalkRow ConvertPartialToWalk(Acc acc)
    {
        long games = acc.Games, sum = acc.SumScoreFp1e9;
        long q = sum / games, rem = sum % games;
        if (rem < 0) { q--; rem += games; }

        long loCount = games - rem, hiCount = rem;
        int n = RunCount(loCount) + RunCount(hiCount);
        Span<Hash128> objects = n <= RunStackMax ? stackalloc Hash128[RunStackMax] : new Hash128[n];
        Span<long> scores = n <= RunStackMax ? stackalloc long[RunStackMax] : new long[n];
        Span<ushort> runs = n <= RunStackMax ? stackalloc ushort[RunStackMax] : new ushort[n];

        Hash128 obj = acc.Object ?? default;
        int w = FillRuns(objects, scores, runs, 0, obj, q, loCount);
        w = FillRuns(objects, scores, runs, w, obj, q + 1, hiCount);

        byte[] packed = TestimonyWalk.Pack(objects[..w], scores[..w], runs[..w]);
        return new TestimonyWalkRow(
            acc.Subject, acc.Type, null, acc.PhiFp1e9,
            packed, w, games, acc.MaxTsUnixUs);
    }

    private static int RunCount(long count)
        => count <= 0 ? 0 : (int)((count + ushort.MaxValue - 1) / ushort.MaxValue);

    private static int FillRuns(
        Span<Hash128> objects, Span<long> scores, Span<ushort> runs,
        int w, Hash128 obj, long score, long count)
    {
        while (count > 0)
        {
            ushort run = count > ushort.MaxValue ? ushort.MaxValue : (ushort)count;
            objects[w] = obj;
            scores[w] = score;
            runs[w] = run;
            w++;
            count -= run;
        }
        return w;
    }

    private const long PgEpochDeltaUs = 946_684_800_000_000L;

    public async Task<long> FoldIncrementalAsync(CancellationToken ct = default)
    {
        if (_stageAsWalks)
            throw new InvalidOperationException(
                "FoldIncrementalAsync requires the flat incremental lane; this writer is in walk-journal "
                + "mode (walks only fold at MaterializeConsensusAsync via the terminal walk fold)");

        await FlushPeriodAsync(ct).ConfigureAwait(false);



        await _foldChain.ConfigureAwait(false);
        return Interlocked.Read(ref _foldedRelations);
    }

    public async Task<long> MaterializeConsensusAsync(CancellationToken ct = default)
    {
        await FlushWalksAsync(ct);
        await FlushPeriodAsync(ct);





        if (_stageAsWalks
            && _partitions > 1)
        {
            return await ParallelWalkFoldAsync(ct);
        }

        if (_stageAsWalks)
        {







            if (!_terminalFold)
                _log.LogInformation("walk journal staged: materializing through the terminal walk fold");
            const string impl = "engine";
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
                fin.CommandText = "SELECT laplace.finish_consensus_fold()";
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
        // A raw SpinWait here was undiagnosable: an apply blocked inside PG
        // (e.g. on an advisory lock) made dispose spin forever with zero
        // output. Wait cooperatively and say what is being waited on.
        var waitSw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _inflightApplies) > 0)
        {
            await Task.Delay(25);
            if (waitSw.Elapsed >= TimeSpan.FromSeconds(30))
            {
                _log.LogWarning(
                    "dispose: still waiting on {N} in-flight apply call(s) — if this repeats, a backend is blocked in PG (see advisory-lock holder warnings above)",
                    Volatile.Read(ref _inflightApplies));
                waitSw.Restart();
            }
        }

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
