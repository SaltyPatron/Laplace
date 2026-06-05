using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using global::Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// THE write surface: consensus accumulates AT INGEST; evidence persists as
/// PROVENANCE-ONLY rows.
///
/// Decorates the one substrate writer (not a second insert path: entities,
/// physicalities and layer-completion markers still flow through it
/// unchanged). Each attestation row carries the witness's TESTIMONY IN FLIGHT
/// (score, trust→φ): the testimony is CONSUMED here — accumulated per RELATION
/// IDENTITY (subject, kind, object) and materialized into <c>consensus</c>
/// through the substrate's period fold when <see cref="MaterializeConsensusAsync"/>
/// runs at the clean end of the ingest period, through the same C aggregate.
/// The rows then flow to the inner writer WHOLE, whose COPY layout persists
/// exactly the PROVENANCE columns (identity 5-tuple, outcome class, games,
/// time) — the values never reach the wire. EVIDENCE IS PROVENANCE: a stored
/// per-witness score is invertible to the weight — recording raw weights —
/// and is banned.
///
/// <para><b>Exactness.</b> Within one ingest period a relation's opponent φ is
/// CONSTANT (φ = kind_rank × source_trust × tenant_trust — one kind, one
/// source, one tenant per run), and the Glicko-2 period update depends on the
/// match multiset only through n (game count) and Σs (score sum): every game
/// contributes the same g(φ)²E(1−E) to v, and Δ ∝ Σs − nE. So the accumulator
/// keeps exactly (n, Σs) per relation; the fold replays n games whose scores
/// sum EXACTLY to Σs (n−1 at ⌊Σs/n⌋ + one remainder game) through the SAME C
/// aggregate (<c>laplace_glicko2_accumulate</c>) the per-period SQL path uses.
/// Staged partials merge as Σ of Σ — still exact; φ-uniformity is verified in
/// the fold's merge pass and fails loud, never averaged.</para>
///
/// <para><b>The machine, not one core.</b> Partials stage into K UNLOGGED
/// partitions routed by relation identity (BLAKE3 low bits — all partials of
/// one relation land in ONE partition), so staging COPYs run K-wide and the
/// period fold runs as K concurrent <c>materialize_period_consensus(k)</c>
/// sessions over DISJOINT consensus rows. K = <c>LAPLACE_FOLD_WORKERS</c>
/// (default min(4, cores−2)). The single-session TEMP design folded a
/// model-scale period on ONE backend — the 2026-06-05 CI autopsy: 153M staged
/// relations, &gt;4h14m, killed by the job timeout with nothing materialized.
/// Staging rows are framed as PG COPY BINARY in pooled buffers and streamed
/// raw (same discipline as the substrate writer's IntentStage path) — never
/// per-field awaited writes.</para>
///
/// <para><b>Bounded memory at ANY volume.</b> The in-memory map holds partial
/// (n, Σs) aggregates; past the staging threshold (default 250M relations ≈
/// 62 GB — sized to the host; env <c>LAPLACE_STAGING_THRESHOLD</c>) it STAGES
/// to the partitions — append-only heaps, zero indexes — and keeps
/// accumulating. A small model at a tight floor and a 400B MoE at a dense one
/// differ only in how many stagings occur.</para>
///
/// <para><b>Idempotency / crash safety.</b> Consensus is untouched until the
/// period completes: the fold is the ONLY path into consensus and the caller
/// runs it ONLY after a clean period — a killed run materializes NOTHING. A
/// PG crash truncates the UNLOGGED staging; a killed app leaves dead staging
/// that <c>create_period_staging</c> sweeps before the next period (and
/// <see cref="DisposeAsync"/> sweeps on clean abort). Layer-completion marker
/// intents (unit name <c>layer-complete/N</c>) pass through whole — markers
/// are substrate bookkeeping, not evidence.</para>
/// </summary>
public sealed class ConsensusAccumulatingWriter : ISubstrateWriter, IAsyncDisposable
{
    private readonly ISubstrateWriter _inner;
    private readonly NpgsqlDataSource _ds;
    private readonly int _stagingThreshold;
    private readonly int _partitions;
    private readonly ILogger _log;

    private sealed class Acc
    {
        public Hash128  Subject;
        public Hash128  Kind;
        public Hash128? Object;
        public long     PhiFp1e9;       // invariant: constant per relation per period
        public long     Games;
        public long     SumScoreFp1e9;  // Σ(score × occurrences); ≤ 9.2e18 ⇒ ~9e9 games headroom
        public long     MaxTsUnixUs;
    }

    private ConcurrentDictionary<(Hash128 S, Hash128 K, Hash128? O), Acc> _accumulation = new();
    private long _observationsAccumulated;
    private bool _stagingCreated;                       // period staging partitions exist in the DB
    private readonly SemaphoreSlim _stagingGate = new(1, 1);
    // Accumulate (readers, concurrent) vs snapshot-exchange (writer): with
    // ParallelWorkers > 1 the appliers call ApplyManyAsync concurrently, and an
    // unguarded Interlocked.Exchange could lose partials written into the old
    // map after the swap. Read lock per Accumulate, write lock around the swap.
    private readonly ReaderWriterLockSlim _swapLock = new();

    public ConsensusAccumulatingWriter(
        ISubstrateWriter inner, NpgsqlDataSource dataSource,
        int? stagingThresholdRelations = null, int? foldWorkers = null,
        ILogger<ConsensusAccumulatingWriter>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _log = logger ?? (ILogger)NullLogger<ConsensusAccumulatingWriter>.Instance;
        // Default sized to the machine, not to timidity: measured ~250 B per
        // in-memory relation entry ⇒ 250M ≈ 62 GB — right for a 128 GB box
        // with PG capped at 32 GB. A bigger window = more in-memory
        // pre-collapse and fewer staging pauses; RAM is the cheap resource.
        // LAPLACE_STAGING_THRESHOLD tunes it without a rebuild.
        _stagingThreshold = stagingThresholdRelations
            ?? (int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_STAGING_THRESHOLD"), out var t) && t > 0
                ? t : 250_000_000);
        // K staging partitions = K fold sessions = K cores on the fold. Sized
        // to the machine (cores − 2 leaves room for the PG checkpointer + the
        // app), capped at 4 by default; LAPLACE_FOLD_WORKERS overrides.
        _partitions = foldWorkers
            ?? (int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_FOLD_WORKERS"), out var w) && w > 0
                ? w : Math.Clamp(Environment.ProcessorCount - 2, 1, 4));
        if (_partitions is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(foldWorkers), _partitions,
                "fold workers must be in 1..64 (create_period_staging's partition bound)");
    }

    /// <summary>Distinct relation identities currently held IN MEMORY (resets at each staging).</summary>
    public int RelationCount => _accumulation.Count;

    /// <summary>Total matches (games) accumulated so far this period.</summary>
    public long ObservationsAccumulated => Interlocked.Read(ref _observationsAccumulated);

    /// <summary>Staging/fold partition count for this period (= fold parallelism).</summary>
    public int FoldWorkers => _partitions;

    /// <inheritdoc/>
    public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
        => ApplyManyAsync(new[] { change }, ct);

    /// <inheritdoc/>
    public async Task<ApplyResult> ApplyManyAsync(
        IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);

        foreach (var c in changes)
        {
            // Layer-completion markers are bookkeeping rows the completion
            // guard + layer gates read — no testimony to consume.
            if (c.Metadata.SourceContentUnitName.StartsWith("layer-complete/", StringComparison.Ordinal))
                continue;
            // CONSUME the testimony (score, φ) into the period accumulation.
            foreach (var a in c.Attestations) Accumulate(a);
        }

        if (_accumulation.Count >= _stagingThreshold)
            await StagePartialsAsync(ct);

        // The rows flow to the inner writer WHOLE: its COPY layout persists
        // only the PROVENANCE columns (identity, outcome class, games, time) —
        // the consumed values never reach the wire.
        return await _inner.ApplyManyAsync(changes, ct);
    }

    private void Accumulate(AttestationRow a)
    {
        _swapLock.EnterReadLock();
        try
        {
            var acc = _accumulation.GetOrAdd((a.SubjectId, a.KindId, a.ObjectId), static _ => new Acc());
            lock (acc)
            {
                if (acc.Games == 0)
                {
                    acc.Subject = a.SubjectId; acc.Kind = a.KindId; acc.Object = a.ObjectId;
                    acc.PhiFp1e9 = a.OpponentRdFp1e9;
                }
                else if (acc.PhiFp1e9 != a.OpponentRdFp1e9)
                {
                    // One kind × one source × one tenant per period ⇒ φ constant.
                    // Mixed φ is a decomposer bug — fail loud, never average.
                    throw new InvalidOperationException(
                        $"accumulation invariant violated: relation observed with φ={a.OpponentRdFp1e9} after φ={acc.PhiFp1e9} in the same period");
                }
                acc.Games        += a.ObservationCount;
                // Pre-aggregated rows carry their EXACT score sum (positions folded
                // onto one row); uniform rows contribute score × occurrences.
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

    /// <summary>Relation identity → staging partition. BLAKE3 low bits are
    /// uniform; identity-determined routing keeps every partial of a relation
    /// in ONE partition (exact Σ of Σ, disjoint parallel folds).</summary>
    private int PartitionOf(Acc acc)
        => (int)((acc.Subject.Lo ^ acc.Kind.Lo ^ (acc.Object?.Lo ?? 0UL)) % (ulong)_partitions);

    /// <summary>Create the period's staging partitions once (substrate-owned DDL).</summary>
    private async Task EnsureStagingAsync(CancellationToken ct)
    {
        if (_stagingCreated) return;
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var ddl = conn.CreateCommand();
        ddl.CommandText = "SELECT laplace.create_period_staging($1)";
        ddl.Parameters.AddWithValue(_partitions);
        await ddl.ExecuteNonQueryAsync(ct);
        _stagingCreated = true;
    }

    /// <summary>Stage the in-memory partial aggregates to the K staging
    /// partitions (parallel raw-binary COPY) and reset the map. Partials for
    /// the same relation across stagings merge exactly at the fold (Σ of Σ).</summary>
    private async Task StagePartialsAsync(CancellationToken ct)
    {
        await _stagingGate.WaitAsync(ct);
        try
        {
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

            await EnsureStagingAsync(ct);

            var buckets = new List<Acc>[_partitions];
            for (int k = 0; k < _partitions; k++) buckets[k] = new List<Acc>();
            foreach (var acc in snapshot.Values) buckets[PartitionOf(acc)].Add(acc);

            var copies = new Task[_partitions];
            for (int k = 0; k < _partitions; k++)
            {
                int part = k;
                copies[k] = Task.Run(() => CopyPartitionAsync(part, buckets[part], ct), ct);
            }
            await Task.WhenAll(copies);
        }
        finally
        {
            _stagingGate.Release();
        }
    }

    // ── PG COPY BINARY framing — buffer-materialized, streamed raw ─────────
    // (Per-field awaited writes cost ~7 awaits × rows; a 153M-relation period
    // is ~1.1e9 awaited calls. Frame rows into pooled chunks instead and hand
    // the stream whole buffers — the IntentStage discipline.)

    private static readonly byte[] CopyBinaryHeader =
    {
        0x50, 0x47, 0x43, 0x4F, 0x50, 0x59, 0x0A, 0xFF, 0x0D, 0x0A, 0x00, // "PGCOPY\n\xFF\r\n\0"
        0x00, 0x00, 0x00, 0x00,                                           // flags
        0x00, 0x00, 0x00, 0x00                                            // header extension length
    };

    private const long PgEpochDeltaUs = 946_684_800_000_000L;  // 2000-01-01 − 1970-01-01 in µs
    private const int  MaxRowBytes    = 110;                   // 2 + 3×(4+16) + 3×(4+8) + (4+8); NULL object is smaller

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
        o = WriteHash(dst, o, acc.Kind);
        if (acc.Object is Hash128 obj) o = WriteHash(dst, o, obj);
        else { BinaryPrimitives.WriteInt32BigEndian(dst[o..], -1); o += 4; }
        o = WriteInt64Field(dst, o, acc.PhiFp1e9);
        o = WriteInt64Field(dst, o, acc.Games);
        o = WriteInt64Field(dst, o, acc.SumScoreFp1e9);
        o = WriteInt64Field(dst, o, acc.MaxTsUnixUs - PgEpochDeltaUs);   // timestamptz = µs since 2000-01-01
        return o;
    }

    private async Task CopyPartitionAsync(int partition, List<Acc> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var stream = await conn.BeginRawBinaryCopyAsync(
            $"COPY laplace.consensus_period_staging_{partition} "
          + "(subject_id, kind_id, object_id, phi, games, sum_score, last_ts) "
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
        // trailer: int16 -1
        if (filled > buffer.Length - 2)
        {
            await stream.WriteAsync(buffer.AsMemory(0, filled), ct);
            filled = 0;
        }
        BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(filled), -1); filled += 2;
        await stream.WriteAsync(buffer.AsMemory(0, filled), ct);
    }

    /// <summary>
    /// Materialize the period's consensus: stage remaining partials, then fold
    /// the K partitions CONCURRENTLY — one <c>materialize_period_consensus(k)</c>
    /// session per partition (disjoint by relation identity; φ-uniformity
    /// guarded inside the fold's merge pass, fail loud). prior = the
    /// relation's CURRENT consensus row (neutral if absent); the period's
    /// games replay through the SAME C aggregate the per-period SQL path uses.
    /// Run ONLY after the ingest period completed cleanly (the caller owns the
    /// completion-marker semantics). Returns the relation count materialized.
    /// </summary>
    public async Task<long> MaterializeConsensusAsync(CancellationToken ct = default)
    {
        await StagePartialsAsync(ct);
        if (!_stagingCreated) return 0;

        var folds = new Task<long>[_partitions];
        for (int k = 0; k < _partitions; k++)
        {
            int part = k;
            folds[k] = Task.Run(async () =>
            {
                await using var conn = await _ds.OpenConnectionAsync(ct);
                conn.Notice += (_, e) =>
                    _log.LogInformation("consensus fold: {Message}", e.Notice.MessageText);
                // The fold reports per-partition progress at LOG severity
                // (regress-deterministic); opt this session in to receive it.
                await using (var sub = conn.CreateCommand())
                {
                    sub.CommandText = "SET client_min_messages = 'log'";
                    await sub.ExecuteNonQueryAsync(ct);
                }
                await using var mat = conn.CreateCommand();
                mat.CommandTimeout = 0;
                mat.CommandText = "SELECT laplace.materialize_period_consensus($1)";
                mat.Parameters.AddWithValue(part);
                return (long)(await mat.ExecuteScalarAsync(ct) ?? 0L);
            }, ct);
        }
        var counts = await Task.WhenAll(folds);

        _stagingCreated = false;
        return counts.Sum();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_stagingCreated)
        {
            // Clean-abort sweep (best effort): the period did not complete, so
            // nothing materialized; drop the staging now rather than leaving it
            // for the next period's create-sweep.
            try
            {
                await using var conn = await _ds.OpenConnectionAsync();
                await using var drop = conn.CreateCommand();
                drop.CommandText = "SELECT laplace.drop_period_staging()";
                await drop.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "period staging sweep on dispose failed; next create_period_staging sweeps it");
            }
            _stagingCreated = false;
        }
        _stagingGate.Dispose();
        _swapLock.Dispose();
    }
}
