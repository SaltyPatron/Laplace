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
    private readonly ReaderWriterLockSlim _swapLock = new();

    public bool PersistEvidence => _persistEvidence;

    /// <param name="stageAsWalks">THE TRAJECTORY JOURNAL mode: every consensus
    /// partial journals as a testimony walk — flat period staging is never
    /// created, so the deposit folds as ONE shape through the terminal walk
    /// fold. Required whenever the decomposer emits TestimonyWalks (mixed
    /// shapes are refused by finish_consensus_fold). Threaded explicitly from
    /// the ingest entry point, never re-derived from the environment.</param>
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
        // The accumulator is a bounded pre-merge of recurrent pairs before they
        // stream to the staging journal — never a place testimony accumulates.
        // The old 250M default held ~most of a model deposit in RAM and OOM'd;
        // 20M keeps the pre-merge win at a bounded ~2-3 GB.
        _stagingThreshold = stagingThresholdRelations
            ?? (int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_STAGING_THRESHOLD"), out var t) && t > 0
                ? t : 20_000_000);
        _partitions = foldWorkers
            ?? (int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_FOLD_WORKERS"), out var w) && w > 0
                ? w : Math.Clamp(Environment.ProcessorCount - 2, 1, 4));
        // Staged-but-unfolded epochs live on disk (~rows×110B each); an unbounded backlog
        // exhausted the drive on the first 1B+-relation behavioral deposit (2026-06-11).
        // 0 or negative disables the bound.
        _maxFoldBacklog = int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_FOLD_BACKLOG_MAX"), out var bl)
            ? bl : 12;
        // LAPLACE_FOLD_LANE=terminal: stage every epoch to disk and fold ONCE at the
        // end through finish_consensus_fold (sequential I/O, zero per-row index probes
        // — the HANDOFF-fold-lane design). The per-epoch merge fold and its backlog
        // bound are bypassed; disk must hold the whole staged journal. ("bulk" is the
        // retired name for the same lane and stays accepted.)
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
        // Walks never travel past the journal (the inner writer has no walk
        // surface); attestation rows forward only when evidence persists.
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
        _swapLock.EnterReadLock();
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
                acc.Games        += a.ObservationCount;
                acc.SumScoreFp1e9 = checked(acc.SumScoreFp1e9
                    + (a.SumScoreFp1e9 ?? checked(a.ScoreFp1e9 * a.ObservationCount)));
                if (a.LastObservedAtUnixUs > acc.MaxTsUnixUs) acc.MaxTsUnixUs = a.LastObservedAtUnixUs;
            }
        }
        finally
        {
            _swapLock.ExitReadLock();
        }
        Interlocked.Add(ref _observationsAccumulated, a.ObservationCount);
    }

    // ── THE TRAJECTORY JOURNAL ───────────────────────────────────────────────
    // Walks bypass the accumulator entirely: they ARE the journal. Buffered per
    // subject-partition (subject.lo % partitions — the fold's gather unit) and
    // COPY'd to consensus_walk_staging_{p}. The terminal fold gathers per
    // subject; recurrence merges there under the period rule.

    private const int WalkFlushRows = 65_536;
    private List<TestimonyWalkRow>[]? _walkBuffers;
    private int _walkBuffered;
    private bool _walkStagingCreated;

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

    /// <summary>Caller holds _stagingGate.</summary>
    private async Task FlushWalksLockedAsync(CancellationToken ct)
    {
        if (_walkBuffers is null || _walkBuffered == 0) return;
        await using var conn = await _ds.OpenConnectionAsync(ct);
        if (!_walkStagingCreated)
        {
            if (!_sweptStale)
            {
                await using var sweep = conn.CreateCommand();
                sweep.CommandText = "SELECT laplace.drop_period_staging()";
                await sweep.ExecuteNonQueryAsync(ct);
                _sweptStale = true;
            }
            await using var create = conn.CreateCommand();
            create.CommandText = $"SELECT laplace.create_walk_staging({_partitions})";
            await create.ExecuteNonQueryAsync(ct);
            _walkStagingCreated = true;
            _anyEpochCreated = true;
        }
        for (int p = 0; p < _partitions; p++)
        {
            var rows = _walkBuffers[p];
            if (rows.Count == 0) continue;
            await using var stream = await conn.BeginRawBinaryCopyAsync(
                $"COPY laplace.consensus_walk_staging_{p} "
                + "(subject_id, type_id, context_id, phi, n_vertices, games_total, last_ts, walk) "
                + "FROM STDIN (FORMAT BINARY)", ct);
            WriteWalkRows(stream, rows, ct);
            rows.Clear();
        }
        _walkBuffered = 0;
    }

    private static void WriteWalkRows(Stream stream, List<TestimonyWalkRow> rows, CancellationToken ct)
    {
        stream.Write(CopyBinaryHeader);

        Span<byte> scratch = stackalloc byte[64];
        foreach (var w in rows)
        {
            ct.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteInt16BigEndian(scratch, 8);
            stream.Write(scratch[..2]);

            WriteHashField(stream, scratch, w.Subject);
            WriteHashField(stream, scratch, w.TypeId);
            if (w.ContextId is { } ctx) WriteHashField(stream, scratch, ctx);
            else { BinaryPrimitives.WriteInt32BigEndian(scratch, -1); stream.Write(scratch[..4]); }

            BinaryPrimitives.WriteInt32BigEndian(scratch, 8);
            BinaryPrimitives.WriteInt64BigEndian(scratch[4..], w.PhiFp1e9);
            stream.Write(scratch[..12]);

            BinaryPrimitives.WriteInt32BigEndian(scratch, 4);
            BinaryPrimitives.WriteInt32BigEndian(scratch[4..], w.Count);
            stream.Write(scratch[..8]);

            BinaryPrimitives.WriteInt32BigEndian(scratch, 8);
            BinaryPrimitives.WriteInt64BigEndian(scratch[4..], w.GamesTotal);
            stream.Write(scratch[..12]);

            // timestamptz: µs since 2000-01-01
            BinaryPrimitives.WriteInt32BigEndian(scratch, 8);
            BinaryPrimitives.WriteInt64BigEndian(scratch[4..],
                w.ObservedAtUnixUs - PgEpochDeltaUs);
            stream.Write(scratch[..12]);

            BinaryPrimitives.WriteInt32BigEndian(scratch, w.PackedVertices.Length);
            stream.Write(scratch[..4]);
            stream.Write(w.PackedVertices);
        }
        BinaryPrimitives.WriteInt16BigEndian(scratch, -1);
        stream.Write(scratch[..2]);
    }

    private static void WriteHashField(Stream stream, Span<byte> scratch, Hash128 h)
    {
        BinaryPrimitives.WriteInt32BigEndian(scratch, 16);
        h.WriteBytes(scratch.Slice(4, 16));
        stream.Write(scratch[..20]);
    }

    // The routing law: identity → staging partition, stable across epochs so the
    // bulk fold can consume one partition at a time (bounded pgsql_tmp). MUST
    // equal laplace.consensus_partition_of (14_period_fold.sql.in) — the fold
    // routes consensus seeds with the SQL twin; drift fails its PK build loudly.
    private int PartitionOf(Acc acc)
        => (int)((acc.Subject.Lo ^ acc.Type.Lo ^ (acc.Object?.Lo ?? 0UL)) % (ulong)_partitions);

    public async Task FlushPeriodAsync(CancellationToken ct = default)
    {
        await _stagingGate.WaitAsync(ct);
        try
        {
            if (_stageAsWalks)
            {
                // THE TRAJECTORY JOURNAL: partials journal as walks too — one
                // shape, one fold, one period. The q/rem split is lossless:
                // (games−rem) observations of q plus rem of (q+1) re-merge in
                // the fold to the exact (games, sum) the flat lane would stage.
                ConcurrentDictionary<(Hash128, Hash128, Hash128?), Acc> snap;
                _swapLock.EnterWriteLock();
                try
                {
                    snap = _accumulation;
                    _accumulation = new ConcurrentDictionary<(Hash128, Hash128, Hash128?), Acc>();
                }
                finally
                {
                    _swapLock.ExitWriteLock();
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
            _swapLock.EnterWriteLock();
            try
            {
                snapshot = _accumulation;
                _accumulation = new ConcurrentDictionary<(Hash128, Hash128, Hash128?), Acc>();
            }
            finally
            {
                _swapLock.ExitWriteLock();
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

            var copies = new Task[_partitions];
            for (int k = 0; k < _partitions; k++)
            {
                int part = k;
                copies[k] = Task.Run(() => CopyPartitionAsync(epoch, part, buckets[part], ct), ct);
            }
            await Task.WhenAll(copies);
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

    // A partial (games, sum) becomes ≤2 score levels — q and q+1 with rem of
    // the latter — chunked to the uint16 run_length bound. A NULL object rides
    // as zero16, the identity-preimage law carried into the vertex.
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
        var folds = new Task<long>[_partitions];
        for (int k = 0; k < _partitions; k++)
        {
            int part = k;
            folds[k] = Task.Run(async () =>
            {
                await using var conn = await _ds.OpenConnectionAsync().ConfigureAwait(false);
                conn.Notice += (_, e) =>
                    _log.LogInformation("consensus fold: {Message}", e.Notice.MessageText);
                await using (var sub = conn.CreateCommand())
                {
                    sub.CommandText = "SET client_min_messages = 'log'";
                    await sub.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                await using var mat = conn.CreateCommand();
                mat.CommandTimeout = 0;
                mat.CommandText = _freshSource
                    ? "SELECT laplace.materialize_period_partition_fresh(laplace.period_staging_table($1, $2))"
                    : "SELECT laplace.materialize_period_partition(laplace.period_staging_table($1, $2))";
                mat.Parameters.AddWithValue(epoch);
                mat.Parameters.AddWithValue(part);
                return (long)(await mat.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L);
            });
        }
        var counts = await Task.WhenAll(folds).ConfigureAwait(false);
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

    private static readonly byte[] CopyBinaryHeader =
    {
        0x50, 0x47, 0x43, 0x4F, 0x50, 0x59, 0x0A, 0xFF, 0x0D, 0x0A, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00
    };

    private const long PgEpochDeltaUs = 946_684_800_000_000L;
    private const int  MaxRowBytes    = 110;

    private static int WriteHash(Span<byte> dst, int o, in Hash128 h)
    {
        BinaryPrimitives.WriteInt32BigEndian(dst[o..], 16);
        h.WriteBytes(dst[(o + 4)..(o + 20)]);
        return o + 20;
    }

    private static int WriteInt64Field(Span<byte> dst, int o, long v)
    {
        BinaryPrimitives.WriteInt32BigEndian(dst[o..], 8);
        BinaryPrimitives.WriteInt64BigEndian(dst[(o + 4)..], v);
        return o + 12;
    }

    private static int WriteRow(Span<byte> dst, int o, Acc acc)
    {
        BinaryPrimitives.WriteInt16BigEndian(dst[o..], 7); o += 2;
        o = WriteHash(dst, o, acc.Subject);
        o = WriteHash(dst, o, acc.Type);
        if (acc.Object is Hash128 obj) o = WriteHash(dst, o, obj);
        else { BinaryPrimitives.WriteInt32BigEndian(dst[o..], -1); o += 4; }
        o = WriteInt64Field(dst, o, acc.PhiFp1e9);
        o = WriteInt64Field(dst, o, acc.Games);
        o = WriteInt64Field(dst, o, acc.SumScoreFp1e9);
        o = WriteInt64Field(dst, o, acc.MaxTsUnixUs - PgEpochDeltaUs);
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

        var buffer = new byte[4 * 1024 * 1024];
        CopyBinaryHeader.CopyTo(buffer, 0);
        int filled = CopyBinaryHeader.Length;
        foreach (var acc in rows)
        {
            if (filled > buffer.Length - MaxRowBytes)
            {
                await stream.WriteAsync(buffer.AsMemory(0, filled), ct);
                filled = 0;
            }
            filled = WriteRow(buffer, filled, acc);
        }
        if (filled > buffer.Length - 2)
        {
            await stream.WriteAsync(buffer.AsMemory(0, filled), ct);
            filled = 0;
        }
        BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(filled), -1); filled += 2;
        await stream.WriteAsync(buffer.AsMemory(0, filled), ct);
    }

    public async Task<long> MaterializeConsensusAsync(CancellationToken ct = default)
    {
        await FlushWalksAsync(ct);
        await FlushPeriodAsync(ct);
        if (_terminalFold || _stageAsWalks)
        {
            // The walk journal has exactly one fold — the terminal walk fold —
            // so walk mode takes this path regardless of LAPLACE_FOLD_LANE.
            // LAPLACE_FOLD_IMPL selects the lane inside the terminal fold:
            // 'engine' (default, consensus_fold_partition in C) or 'sql' (the
            // ordered-aggregate escape hatch and parity reference).
            // LAPLACE_FOLD_RESUMABLE=1 runs the per-partition-COMMIT procedure:
            // staging drops as the fold walks, and a re-run resumes mid-deposit.
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
            await using var fin = conn.CreateCommand();
            fin.CommandTimeout = 0;
            fin.CommandText = resumable
                ? "CALL laplace.finish_consensus_fold_steps(NULL)"
                : "SELECT laplace.finish_consensus_fold()";
            long n = (long)(await fin.ExecuteScalarAsync(ct) ?? 0L);
            Interlocked.Add(ref _foldedRelations, n);
            _log.LogInformation(
                "terminal fold complete: {Relations:N0} consensus relations in {Sec:F0}s ({Rps:N0} rel/s)",
                n, sw.Elapsed.TotalSeconds, n / Math.Max(1e-3, sw.Elapsed.TotalSeconds));
            return Interlocked.Read(ref _foldedRelations);
        }
        await _foldChain;
        return Interlocked.Read(ref _foldedRelations);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _foldChain;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "in-flight period fold failed before dispose; staging sweep follows");
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
        _swapLock.Dispose();
    }
}
