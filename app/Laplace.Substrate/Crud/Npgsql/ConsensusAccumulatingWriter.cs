using System.Collections.Immutable;
using global::Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Inline consensus fold: every apply batch writes its evidence AND folds its
/// consensus delta in the same flow — consensus is the fourth table of the
/// batched apply, not a deferred phase. Per batch: client-dedup the cell deltas
/// (already merged in RAM), forward evidence to the inner writer, then one
/// ordered transaction runs consensus_upsert (server-side native Glicko fold
/// inside each row's lock window, MERGE ordered by partition keys) plus
/// highway_mask_deposit (bits OR'd in from the pairs this batch touched).
/// Ingest completion IS fold completion — no accumulator epochs, no staging
/// tables, no walk journal, no terminal fold, no advisory-lock wall.
/// The Glicko rating period is the batch (ratified 2026-07-15).
/// </summary>
public sealed class ConsensusAccumulatingWriter : ISubstrateWriter, IAsyncDisposable
{
    public const string PeriodBoundaryUnitPrefix = IngestBatchPipeline.PeriodBoundaryUnitPrefix;

    private readonly ISubstrateWriter _inner;
    private readonly NpgsqlDataSource _ds;
    private readonly bool _persistEvidence;
    private readonly ILogger _log;

    // One ordered upsert at a time per process: parallel batches overlap their
    // COPY phases and queue only here. Combined with the (type, subject, id)
    // sort inside consensus_upsert, lock acquisition has one global order —
    // no deadlocks, no advisory locks.
    private readonly SemaphoreSlim _upsertGate = new(1, 1);

    private long _observations;
    private long _cellsFolded;
    private int _inflightApplies;
    private volatile bool _disposing;

    private const int UpsertChunkCells = 65_536;

    // Fold fan-out width: per-type segments are row-disjoint under the
    // LIST(type_id) partitioning, so they ride parallel connections exactly
    // like the 12-way COPY apply above them. One connection per segment.
    private static readonly int FoldConnections = Math.Clamp(Environment.ProcessorCount, 1, 12);

    // Run-scoped mask-pair dedup: masks only ACCRETE, so a pair deposited once
    // this run never needs resending — without this, every flush re-verifies
    // every earlier flush's pairs server-side (~6 leaf probes per pair) for
    // zero writes. Mutated only under _upsertGate. Cleared at the cap as a
    // memory valve: clearing costs re-verification, never correctness.
    private readonly HashSet<(Hash128 Ent, Hash128 Typ)> _depositedMaskPairs = new();
    private const int DepositedMaskPairsCap = 8_388_608;

    public ConsensusAccumulatingWriter(
        ISubstrateWriter inner, NpgsqlDataSource dataSource,
        bool? persistEvidence = null,
        ILogger<ConsensusAccumulatingWriter>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _persistEvidence = persistEvidence ?? true;
        _log = logger ?? (ILogger)NullLogger<ConsensusAccumulatingWriter>.Instance;
        if (!_persistEvidence)
            _log.LogInformation(
                "consensus-only deposit: folding relations inline; laplace.attestations writes skipped");
    }

    public bool PersistEvidence => _persistEvidence;

    public long ObservationsAccumulated => Interlocked.Read(ref _observations);

    /// <summary>Total consensus cells folded (inserted or updated) this run.</summary>
    public long CellsFolded => Interlocked.Read(ref _cellsFolded);

    public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
        => ApplyManyAsync(new[] { change }, ct);

    public async Task<ApplyResult> ApplyManyAsync(
        IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
        => await ApplyCoreAsync(changes, workingSet: false, append: false, default, ct);

    public Task<ApplyResult> ApplyWorkingSetAsync(SubstrateChange change, CancellationToken ct = default)
        => ApplyWorkingSetAsync(new[] { change }, ct);

    public async Task<ApplyResult> ApplyWorkingSetAsync(
        IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
        => await ApplyCoreAsync(changes, workingSet: true, append: false, default, ct);

    public async Task<ApplyResult> AppendAsync(
        IReadOnlyList<SubstrateChange> changes, Hash128 sourceId, CancellationToken ct = default)
        => await ApplyCoreAsync(changes, workingSet: false, append: true, sourceId, ct);

    private sealed class Delta
    {
        public long PhiFp1e9;
        public long Games;
        public long SumScoreFp1e9;
        public long MaxTsUnixUs;
    }

    private async Task<ApplyResult> ApplyCoreAsync(
        IReadOnlyList<SubstrateChange> changes, bool workingSet, bool append,
        Hash128 sourceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (_disposing) throw new ObjectDisposedException(nameof(ConsensusAccumulatingWriter));
        Interlocked.Increment(ref _inflightApplies);
        try
        {
            if (_disposing) throw new ObjectDisposedException(nameof(ConsensusAccumulatingWriter));

            var delta = BuildDelta(changes);

            // Evidence lands FIRST; the fold runs only after it succeeds, so a
            // retried batch folds exactly once (a throw below leaves consensus
            // untouched for this batch).
            var forwarded = ForwardChanges(changes);
            var result = append
                ? await _inner.AppendAsync(forwarded, sourceId, ct)
                : workingSet
                    ? await _inner.ApplyWorkingSetAsync(forwarded, ct)
                    : await _inner.ApplyManyAsync(forwarded, ct);

            // INVARIANT: one fold per claimed flush-journal token. A journal
            // hit means a prior apply of this exact working set committed —
            // and that apply's own flow folded this same delta right after its
            // evidence landed. The fold is additive, not idempotent, so
            // folding a replay would double-count the batch's testimony in
            // consensus; a journal hit must no-op evidence AND fold.
            if (delta is { Count: > 0 } && !result.JournalReplayHit)
                await UpsertDeltaAsync(delta, ct);

            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _inflightApplies);
        }
    }

    private Dictionary<(Hash128 S, Hash128 T, Hash128? O), Delta>? BuildDelta(
        IReadOnlyList<SubstrateChange> changes)
    {
        Dictionary<(Hash128, Hash128, Hash128?), Delta>? delta = null;
        foreach (var c in changes)
        {
            if (!c.TestimonyWalks.IsDefaultOrEmpty)
                throw new InvalidOperationException(
                    "testimony walks are no longer journaled — the consensus fold is inline; "
                    + "emit aggregated attestations (observation_count/sum_score) instead");
            if (c.Metadata.SourceContentUnitName.StartsWith("layer-complete/", StringComparison.Ordinal)
                || c.Metadata.SourceContentUnitName.StartsWith(PeriodBoundaryUnitPrefix, StringComparison.Ordinal))
                continue;

            var atts = c.Attestations;
            for (int i = 0; i < atts.Length; i++)
            {
                var a = atts[i];
                delta ??= new Dictionary<(Hash128, Hash128, Hash128?), Delta>();
                var key = (a.SubjectId, a.TypeId, a.ObjectId);
                if (!delta.TryGetValue(key, out var d))
                {
                    delta[key] = new Delta
                    {
                        PhiFp1e9 = a.OpponentRdFp1e9,
                        Games = a.ObservationCount,
                        SumScoreFp1e9 = AttestationMergeMath.RowScoreTotal(a),
                        MaxTsUnixUs = a.LastObservedAtUnixUs,
                    };
                }
                else
                {
                    if (d.PhiFp1e9 != a.OpponentRdFp1e9)
                        throw new InvalidOperationException(
                            $"fold invariant violated: cell observed with φ={a.OpponentRdFp1e9} "
                            + $"after φ={d.PhiFp1e9} in the same batch");
                    d.Games = AttestationMergeMath.SafeAddGames(d.Games, a.ObservationCount);
                    d.SumScoreFp1e9 = AttestationMergeMath.SafeAddScores(
                        d.SumScoreFp1e9, AttestationMergeMath.RowScoreTotal(a));
                    if (a.LastObservedAtUnixUs > d.MaxTsUnixUs) d.MaxTsUnixUs = a.LastObservedAtUnixUs;
                }
                Interlocked.Add(ref _observations, a.ObservationCount);
            }
        }
        return delta;
    }

    private IReadOnlyList<SubstrateChange> ForwardChanges(IReadOnlyList<SubstrateChange> changes)
    {
        if (_persistEvidence) return changes;

        bool anyToStrip = false;
        foreach (var c in changes)
            if (!c.Attestations.IsEmpty) { anyToStrip = true; break; }
        if (!anyToStrip) return changes;

        var stripped = new SubstrateChange[changes.Count];
        for (int i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            if (!c.Attestations.IsEmpty)
                c = c with { Attestations = ImmutableArray<AttestationRow>.Empty };
            stripped[i] = c;
        }
        return stripped;
    }

    private async Task UpsertDeltaAsync(
        Dictionary<(Hash128 S, Hash128 T, Hash128? O), Delta> delta, CancellationToken ct)
    {
        // Sort by the partition keys (type, subject) then edge id so every
        // writer locks rows in one global order.
        var cells = new ((Hash128 S, Hash128 T, Hash128? O) Key, Hash128 Cid, Delta D)[delta.Count];
        int n = 0;
        foreach (var (key, d) in delta)
            cells[n++] = (key, ConsensusKeys.EdgeId(key.S, key.T, key.O ?? default), d);
        Array.Sort(cells, static (x, y) =>
        {
            int c = x.Key.T.CompareToBytewise(y.Key.T);
            if (c != 0) return c;
            c = x.Key.S.CompareToBytewise(y.Key.S);
            return c != 0 ? c : x.Cid.CompareToBytewise(y.Cid);
        });

        // Mask pairs from the same delta: (subject, type) + (object, type).
        var maskPairs = new HashSet<(Hash128 Ent, Hash128 Typ)>(n * 2);
        foreach (var cell in cells)
        {
            maskPairs.Add((cell.Key.S, cell.Key.T));
            if (cell.Key.O is { } obj) maskPairs.Add((obj, cell.Key.T));
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long folded = 0, masks = 0;
        await _upsertGate.WaitAsync(ct);
        try
        {
            // Never resend a pair this run already deposited: the server-side
            // no-op still costs ~6 tier-leaf probes per pair for zero writes.
            maskPairs.ExceptWith(_depositedMaskPairs);

            // Fixed-size chunks over the sorted cells, folded on PARALLEL
            // connections — the same width the COPY apply uses — instead of
            // serializing 80% of ingest wall time onto one core. Chunking (not
            // per-type segmentation) is the correct fan-out axis: a skewed
            // source (unicode: 1.6M cells, essentially ONE type) degenerates a
            // type split back to a single serial stream. Safety does not come
            // from partition boundaries at all: cells are CLIENT-DEDUPED, so
            // no two chunks can touch the same consensus row — row locks are
            // disjoint by construction, inserts are unique by construction,
            // and consensus_upsert's internal per-type loop still gives every
            // call runtime-pruned, type-major-ordered writes. Each chunk
            // commits its own transaction: a mid-delta failure aborts the run
            // either way (same retry class as the prior whole-delta tx).
            var segments = new List<(int Off, int Len)>();
            for (int s = 0; s < cells.Length; s += UpsertChunkCells)
                segments.Add((s, Math.Min(UpsertChunkCells, cells.Length - s)));

            await Parallel.ForEachAsync(segments,
                new ParallelOptions { MaxDegreeOfParallelism = FoldConnections, CancellationToken = ct },
                async (seg, token) =>
            {
                await using var conn = await _ds.OpenConnectionAsync(token);
                await using var tx = await conn.BeginTransactionAsync(token);
                long segFolded = 0;
                for (int off = seg.Off; off < seg.Off + seg.Len; off += UpsertChunkCells)
                {
                    int m = Math.Min(UpsertChunkCells, seg.Off + seg.Len - off);
                    var subjects = new byte[m][];
                    var types = new byte[m][];
                    var objects = new byte[m][];
                    var phis = new long[m];
                    var games = new long[m];
                    var sums = new long[m];
                    var ts = new DateTime[m];
                    for (int i = 0; i < m; i++)
                    {
                        var cell = cells[off + i];
                        subjects[i] = cell.Key.S.ToBytes();
                        types[i] = cell.Key.T.ToBytes();
                        objects[i] = cell.Key.O?.ToBytes()!;
                        phis[i] = cell.D.PhiFp1e9;
                        games[i] = cell.D.Games;
                        sums[i] = cell.D.SumScoreFp1e9;
                        ts[i] = TsFromUnixUs(cell.D.MaxTsUnixUs);
                    }
                    await using var up = conn.CreateCommand();
                    up.Transaction = tx;
                    up.CommandTimeout = 0;
                    up.CommandText = "SELECT laplace.consensus_upsert($1, $2, $3, $4, $5, $6, $7)";
                    up.Parameters.Add(new NpgsqlParameter { Value = subjects, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                    up.Parameters.Add(new NpgsqlParameter { Value = types, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                    up.Parameters.Add(new NpgsqlParameter { Value = objects, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                    up.Parameters.Add(new NpgsqlParameter { Value = phis, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                    up.Parameters.Add(new NpgsqlParameter { Value = games, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                    up.Parameters.Add(new NpgsqlParameter { Value = sums, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                    up.Parameters.Add(new NpgsqlParameter { Value = ts, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
                    segFolded += (long)(await up.ExecuteScalarAsync(token) ?? 0L);
                }
                await tx.CommitAsync(token);
                Interlocked.Add(ref folded, segFolded);
            });

            // Mask deposits fan out BUCKETED BY ENTITY: one entity accretes
            // bits from many types, so the split axis must keep all of an
            // entity's pairs in ONE bucket — buckets then touch disjoint
            // entities rows and parallel deposit transactions cannot contend
            // or deadlock on a shared row. (A count-based split would put the
            // same entity row under two transactions.)
            if (maskPairs.Count > 0)
            {
                int buckets = Math.Min(FoldConnections, 1 + maskPairs.Count / UpsertChunkCells);
                var bucketed = new List<(Hash128 Ent, Hash128 Typ)>[buckets];
                for (int b = 0; b < buckets; b++)
                    bucketed[b] = new List<(Hash128 Ent, Hash128 Typ)>(maskPairs.Count / buckets + 16);
                foreach (var p in maskPairs)
                    bucketed[(int)((uint)p.Ent.GetHashCode() % (uint)buckets)].Add(p);

                long maskTotal = 0;
                await Parallel.ForEachAsync(bucketed,
                    new ParallelOptions { MaxDegreeOfParallelism = FoldConnections, CancellationToken = ct },
                    async (bucket, token) =>
                {
                    if (bucket.Count == 0) return;
                    await using var conn = await _ds.OpenConnectionAsync(token);
                    await using var tx = await conn.BeginTransactionAsync(token);
                    long dep = 0;
                    for (int off = 0; off < bucket.Count; off += UpsertChunkCells)
                    {
                        int m = Math.Min(UpsertChunkCells, bucket.Count - off);
                        var pairEnts = new byte[m][];
                        var pairTypes = new byte[m][];
                        for (int i = 0; i < m; i++)
                        {
                            pairEnts[i] = bucket[off + i].Ent.ToBytes();
                            pairTypes[i] = bucket[off + i].Typ.ToBytes();
                        }
                        await using var mask = conn.CreateCommand();
                        mask.Transaction = tx;
                        mask.CommandTimeout = 0;
                        mask.CommandText = "SELECT laplace.highway_mask_deposit($1, $2)";
                        mask.Parameters.Add(new NpgsqlParameter { Value = pairEnts, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                        mask.Parameters.Add(new NpgsqlParameter { Value = pairTypes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                        dep += (long)(await mask.ExecuteScalarAsync(token) ?? 0L);
                    }
                    await tx.CommitAsync(token);
                    Interlocked.Add(ref maskTotal, dep);
                });
                masks += maskTotal;

                // Mark AFTER all buckets commit — pairs from any failed run
                // stay resendable; resends are no-ops server-side.
                if (_depositedMaskPairs.Count + maskPairs.Count > DepositedMaskPairsCap)
                    _depositedMaskPairs.Clear();
                _depositedMaskPairs.UnionWith(maskPairs);
            }
        }
        finally
        {
            _upsertGate.Release();
        }
        Interlocked.Add(ref _cellsFolded, folded);
        _log.LogInformation(
            "consensus fold (inline): {Cells:N0} cells folded, {Masks:N0} masks deposited in {Ms:N0}ms ({Rate:N0} cells/s)",
            folded, masks, sw.ElapsedMilliseconds,
            folded / Math.Max(1e-3, sw.Elapsed.TotalSeconds));
    }

    private static DateTime TsFromUnixUs(long unixUs)
        => DateTime.UnixEpoch.AddTicks(unixUs * 10);

    public Task<(int Entities, int Physicalities, int Attestations)> FinalizeSourceAsync(
        Hash128 sourceId, CancellationToken ct = default)
        => _inner.FinalizeSourceAsync(sourceId, ct);

    public Task BeginBulkRunAsync(CancellationToken ct = default)
        => _inner.BeginBulkRunAsync(ct);

    public Task CompleteBulkRunAsync(CancellationToken ct = default)
        => _inner.CompleteBulkRunAsync(ct);

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        var waitSw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _inflightApplies) > 0)
        {
            await Task.Delay(25);
            if (waitSw.Elapsed >= TimeSpan.FromSeconds(30))
            {
                _log.LogWarning(
                    "dispose: still waiting on {N} in-flight apply call(s)",
                    Volatile.Read(ref _inflightApplies));
                waitSw.Restart();
            }
        }
        _upsertGate.Dispose();
    }
}
