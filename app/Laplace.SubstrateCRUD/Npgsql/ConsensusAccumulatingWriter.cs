using System.Collections.Concurrent;
using System.Collections.Immutable;
using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// PRODUCTION write surface: consensus accumulates AT INGEST; evidence
/// recording is DISABLED.
///
/// Decorates the one substrate writer (not a second insert path: entities,
/// physicalities and layer-completion markers still flow through it
/// unchanged). Attestation rows — the per-witness Glicko-2 MATCHES against the
/// neutral baseline — are NOT written to the evidence table; they ACCUMULATE
/// per RELATION IDENTITY (subject, kind, object) and the period MATERIALIZES
/// into <c>consensus</c> in ONE set-based upsert when
/// <see cref="MaterializeConsensusAsync"/> runs at the clean end of the ingest
/// period — the same per-period semantics as <c>incremental_consensus</c>,
/// through the same C aggregate. Evidence is provenance, not knowledge:
/// research instances record the per-witness receipts (LAPLACE_EVIDENCE=record).
///
/// <para><b>Exactness.</b> Within one ingest period a relation's opponent φ is
/// CONSTANT (φ = kind_rank × source_trust × tenant_trust — one kind, one
/// source, one tenant per run), and the Glicko-2 period update depends on the
/// match multiset only through n (game count) and Σs (score sum): every game
/// contributes the same g(φ)²E(1−E) to v, and Δ ∝ Σs − nE. So the accumulator
/// keeps exactly (n, Σs) per relation; materialization replays n games whose
/// scores sum EXACTLY to Σs (n−1 at ⌊Σs/n⌋ + one remainder game) through the
/// SAME C aggregate (<c>laplace_glicko2_accumulate</c>) the per-period SQL
/// path uses. Staged partials merge as Σ of Σ — still exact; φ-uniformity is
/// verified before the merge and fails loud, never averaged.</para>
///
/// <para><b>Bounded memory at ANY volume.</b> The in-memory map holds partial
/// (n, Σs) aggregates; past the staging threshold (default 250M relations ≈
/// 62 GB — sized to the host; env <c>LAPLACE_STAGING_THRESHOLD</c>) it STAGES to a session-scoped
/// TEMP heap on a dedicated connection — append-only, zero indexes, heap
/// speed — and keeps accumulating. A small model at a tight floor and a 400B
/// MoE at a dense one differ only in how many stagings occur.</para>
///
/// <para><b>Idempotency / crash safety.</b> Consensus is untouched until the
/// period completes: the staging lives in a TEMP table on the writer's private
/// connection, so a killed run drops it with the session and materializes
/// NOTHING; the re-run is a lawful continuation (completion-marker guard).
/// Layer-completion marker intents (unit name <c>layer-complete/N</c>) pass
/// through whole — markers are substrate bookkeeping, not evidence.</para>
/// </summary>
public sealed class ConsensusAccumulatingWriter : ISubstrateWriter, IAsyncDisposable
{
    private readonly ISubstrateWriter _inner;
    private readonly NpgsqlDataSource _ds;
    private readonly int _stagingThreshold;

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
    private NpgsqlConnection? _stagingConn;     // owns the TEMP staging for the period
    private readonly SemaphoreSlim _stagingGate = new(1, 1);

    public ConsensusAccumulatingWriter(
        ISubstrateWriter inner, NpgsqlDataSource dataSource, int? stagingThresholdRelations = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        // Default sized to the machine, not to timidity: measured ~250 B per
        // in-memory relation entry ⇒ 250M ≈ 62 GB — right for a 128 GB box
        // with PG capped at 32 GB. A bigger window = more in-memory
        // pre-collapse and fewer staging pauses; RAM is the cheap resource.
        // LAPLACE_STAGING_THRESHOLD tunes it without a rebuild.
        _stagingThreshold = stagingThresholdRelations
            ?? (int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_STAGING_THRESHOLD"), out var t) && t > 0
                ? t : 250_000_000);
    }

    /// <summary>Distinct relation identities currently held IN MEMORY (resets at each staging).</summary>
    public int RelationCount => _accumulation.Count;

    /// <summary>Total matches (games) accumulated so far this period.</summary>
    public long ObservationsAccumulated => Interlocked.Read(ref _observationsAccumulated);

    /// <inheritdoc/>
    public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
        => ApplyManyAsync(new[] { change }, ct);

    /// <inheritdoc/>
    public async Task<ApplyResult> ApplyManyAsync(
        IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var passthrough = new List<SubstrateChange>(changes.Count);
        int observationsAttempted = 0;
        foreach (var c in changes)
        {
            // Layer-completion markers are bookkeeping rows the completion
            // guard + layer gates read — they pass through WHOLE.
            if (c.Metadata.SourceContentUnitName.StartsWith("layer-complete/", StringComparison.Ordinal))
            {
                passthrough.Add(c);
                continue;
            }
            observationsAttempted += c.Attestations.Length;
            foreach (var a in c.Attestations) Accumulate(a);
            passthrough.Add(c.Attestations.IsEmpty
                ? c
                : c with { Attestations = ImmutableArray<AttestationRow>.Empty });
        }

        if (_accumulation.Count >= _stagingThreshold)
            await StagePartialsAsync(ct);

        var r = await _inner.ApplyManyAsync(passthrough, ct);
        // Report the matches as ATTEMPTED (presented and accumulated) with 0
        // INSERTED (no evidence row exists — that is the point).
        return r with { AttestationsAttempted = r.AttestationsAttempted + observationsAttempted };
    }

    private void Accumulate(AttestationRow a)
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
            acc.SumScoreFp1e9 = checked(acc.SumScoreFp1e9 + a.ScoreFp1e9 * a.ObservationCount);
            if (a.LastObservedAtUnixUs > acc.MaxTsUnixUs) acc.MaxTsUnixUs = a.LastObservedAtUnixUs;
        }
        Interlocked.Add(ref _observationsAccumulated, a.ObservationCount);
    }

    /// <summary>The dedicated staging connection + TEMP heap, created once per
    /// period. TEMP = session-scoped: a crash drops the staging with the session.</summary>
    private async Task<NpgsqlConnection> StagingConnAsync(CancellationToken ct)
    {
        if (_stagingConn is not null) return _stagingConn;
        var conn = await _ds.OpenConnectionAsync(ct);
        await using (var ddl = conn.CreateCommand())
        {
            ddl.CommandText = "SELECT laplace.create_period_staging()";
            await ddl.ExecuteNonQueryAsync(ct);
        }
        _stagingConn = conn;
        return conn;
    }

    /// <summary>Stage the in-memory partial aggregates to the TEMP heap and
    /// reset the map. Partials for the same relation across stagings merge
    /// exactly at materialization (Σ of Σ).</summary>
    private async Task StagePartialsAsync(CancellationToken ct)
    {
        await _stagingGate.WaitAsync(ct);
        try
        {
            var snapshot = Interlocked.Exchange(ref _accumulation,
                new ConcurrentDictionary<(Hash128, Hash128, Hash128?), Acc>());
            if (snapshot.IsEmpty) return;
            var conn = await StagingConnAsync(ct);
            await using var imp = await conn.BeginBinaryImportAsync(
                "COPY consensus_period_staging (subject_id, kind_id, object_id, phi, games, sum_score, last_ts) FROM STDIN (FORMAT BINARY)", ct);
            foreach (var acc in snapshot.Values)
            {
                await imp.StartRowAsync(ct);
                await imp.WriteAsync(acc.Subject.ToBytes(), NpgsqlDbType.Bytea, ct);
                await imp.WriteAsync(acc.Kind.ToBytes(),    NpgsqlDbType.Bytea, ct);
                if (acc.Object is Hash128 o) await imp.WriteAsync(o.ToBytes(), NpgsqlDbType.Bytea, ct);
                else                         await imp.WriteNullAsync(ct);
                await imp.WriteAsync(acc.PhiFp1e9,      NpgsqlDbType.Bigint, ct);
                await imp.WriteAsync(acc.Games,         NpgsqlDbType.Bigint, ct);
                await imp.WriteAsync(acc.SumScoreFp1e9, NpgsqlDbType.Bigint, ct);
                await imp.WriteAsync(DateTimeOffset.FromUnixTimeMilliseconds(0).AddTicks(acc.MaxTsUnixUs * 10),
                                     NpgsqlDbType.TimestampTz, ct);
            }
            await imp.CompleteAsync(ct);
        }
        finally
        {
            _stagingGate.Release();
        }
    }

    /// <summary>
    /// Materialize the period's consensus: stage remaining partials, φ-uniformity check
    /// (fail loud), then ONE set-based upsert merging all partials per relation
    /// (Σ of Σ — exact). prior = the relation's CURRENT consensus row (neutral
    /// if absent); the period's games replay through the SAME C aggregate
    /// (<c>laplace_glicko2_accumulate</c>) the per-period SQL path uses. Run
    /// ONLY after the ingest period completed cleanly (the caller owns the
    /// completion-marker semantics). Returns the relation count materialized.
    /// </summary>
    public async Task<long> MaterializeConsensusAsync(CancellationToken ct = default)
    {
        await StagePartialsAsync(ct);
        if (_stagingConn is null) return 0;
        var conn = _stagingConn;

        // The substrate owns the operation (φ-uniformity guard, the §10
        // accumulation against the current row as prior, the upsert, the
        // staging drop, and the function-scoped replica role) — ONE call,
        // zero hand-written SQL, zero duplicated constants.
        long materialized;
        await using (var mat = conn.CreateCommand())
        {
            mat.CommandTimeout = 0;
            mat.CommandText = "SELECT laplace.materialize_period_consensus()";
            materialized = (long)(await mat.ExecuteScalarAsync(ct) ?? 0L);
        }

        await conn.DisposeAsync();
        _stagingConn = null;
        return materialized;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_stagingConn is not null) { await _stagingConn.DisposeAsync(); _stagingConn = null; }
        _stagingGate.Dispose();
    }
}
