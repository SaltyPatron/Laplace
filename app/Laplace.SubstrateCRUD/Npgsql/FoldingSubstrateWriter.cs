using System.Collections.Concurrent;
using System.Collections.Immutable;
using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// PRODUCTION (fold-only) write surface: evidence recording DISABLED.
///
/// Decorates the real writer. Entities + physicalities pass through unchanged
/// (they are substrate). Attestation rows — the per-witness Glicko-2
/// OBSERVATIONS — are NOT written to the evidence table; they are accumulated
/// in memory per RELATION IDENTITY (subject, kind, object) and folded into the
/// <c>consensus</c> table in ONE set-based upsert when
/// <see cref="FoldToConsensusAsync"/> runs at the clean end of the ingest
/// period. Evidence is provenance, not knowledge: research instances run the
/// inner writer directly (LAPLACE_EVIDENCE=record) when per-witness receipts
/// are wanted for interpretability/audit.
///
/// <para><b>Exactness.</b> Within one ingest period a relation's opponent φ is
/// CONSTANT (φ = kind_rank × source_trust × tenant_trust → one kind, one
/// source, one tenant per run), and the Glicko-2 period update depends on the
/// observation multiset only through n (game count) and Σs (score sum): every
/// game contributes the same g(φ)²E(1−E) to v, and Δ ∝ Σ(s_j − E) = Σs − nE.
/// So the accumulator keeps exactly (n, Σs) per relation, and the fold replays
/// n games whose scores sum EXACTLY to Σs (n−1 games at ⌊Σs/n⌋ + one remainder
/// game) through the SAME C aggregate (<c>laplace_glicko2_accumulate</c>) the
/// per-period SQL fold uses — identical math, no managed-scalar reimplementation.
/// A mixed-φ relation within one period violates the invariant and fails loud.</para>
///
/// <para><b>Idempotency / crash safety.</b> Nothing touches consensus until the
/// period completes: a killed run folds NOTHING (the in-memory accumulator
/// dies with it) and the re-run is a lawful continuation (completion-marker
/// guard). The layer-completion marker intent (unit name
/// <c>layer-complete/N</c>) passes through whole — markers are substrate
/// bookkeeping rows, not per-witness evidence.</para>
/// </summary>
public sealed class FoldingSubstrateWriter : ISubstrateWriter
{
    private readonly ISubstrateWriter _inner;

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

    private readonly ConcurrentDictionary<(Hash128 S, Hash128 K, Hash128? O), Acc> _fold = new();
    private long _observationsFolded;

    public FoldingSubstrateWriter(ISubstrateWriter inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>Distinct relations accumulated so far this period.</summary>
    public int RelationCount => _fold.Count;

    /// <summary>Total observations (games) folded so far this period.</summary>
    public long ObservationsFolded => Interlocked.Read(ref _observationsFolded);

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
            // guard + ADR 0037 gates read — they pass through WHOLE.
            if (c.Metadata.SourceContentUnitName.StartsWith("layer-complete/", StringComparison.Ordinal))
            {
                passthrough.Add(c);
                continue;
            }
            observationsAttempted += c.Attestations.Length;
            foreach (var a in c.Attestations) Fold(a);
            passthrough.Add(c.Attestations.IsEmpty
                ? c
                : c with { Attestations = ImmutableArray<AttestationRow>.Empty });
        }

        var r = await _inner.ApplyManyAsync(passthrough, ct);
        // Report the observations as ATTEMPTED (they were presented and folded)
        // with 0 INSERTED (no evidence row exists — that is the point).
        return r with { AttestationsAttempted = r.AttestationsAttempted + observationsAttempted };
    }

    private void Fold(AttestationRow a)
    {
        var acc = _fold.GetOrAdd((a.SubjectId, a.KindId, a.ObjectId), static _ => new Acc());
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
                // A mixed φ here is a decomposer bug — fail loud, never average.
                throw new InvalidOperationException(
                    $"fold invariant violated: relation observed with φ={a.OpponentRdFp1e9} after φ={acc.PhiFp1e9} in the same period");
            }
            acc.Games        += a.ObservationCount;
            acc.SumScoreFp1e9 = checked(acc.SumScoreFp1e9 + a.ScoreFp1e9 * a.ObservationCount);
            if (a.LastObservedAtUnixUs > acc.MaxTsUnixUs) acc.MaxTsUnixUs = a.LastObservedAtUnixUs;
        }
        Interlocked.Add(ref _observationsFolded, a.ObservationCount);
    }

    /// <summary>
    /// The period fold: one set-based consensus upsert for every accumulated
    /// relation. prior = the relation's CURRENT consensus row (neutral if
    /// absent); the period's games replay through the SAME C aggregate the SQL
    /// fold uses. Run ONLY after the ingest period completed cleanly (the
    /// caller writes/owns the completion marker semantics). Returns the number
    /// of relations folded.
    /// </summary>
    public async Task<long> FoldToConsensusAsync(NpgsqlDataSource ds, CancellationToken ct = default)
    {
        if (_fold.IsEmpty) return 0;

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var ddl = conn.CreateCommand())
        {
            ddl.CommandText = @"CREATE TEMP TABLE fold_staging (
                subject_id bytea NOT NULL,
                kind_id    bytea NOT NULL,
                object_id  bytea,
                phi        bigint NOT NULL,
                games      bigint NOT NULL,
                sum_score  bigint NOT NULL,
                last_ts    timestamptz NOT NULL
            ) ON COMMIT DROP";
            await ddl.ExecuteNonQueryAsync(ct);
        }

        // ~one row per RELATION (not per observation) — the 74×+ collapse is
        // exactly why this importer loop is cheap.
        await using (var imp = await conn.BeginBinaryImportAsync(
            "COPY fold_staging (subject_id, kind_id, object_id, phi, games, sum_score, last_ts) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var acc in _fold.Values)
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

        long folded;
        await using (var up = conn.CreateCommand())
        {
            up.CommandTimeout = 0;
            // n−1 games at ⌊Σs/n⌋ + one game carrying the remainder ⇒ the game
            // multiset's Σs is EXACT; v is score-independent, Δ ∝ Σs ⇒ the
            // period update is exactly the true multiset's update (constant φ).
            up.CommandText = @"
INSERT INTO laplace.consensus (id, subject_id, kind_id, object_id, context_id,
                               rating, rd, volatility, witness_count, last_observed_at)
SELECT laplace.consensus_id(f.subject_id, f.kind_id, f.object_id),
       f.subject_id, f.kind_id, f.object_id, NULL::bytea,
       (f.acc).rating, (f.acc).rd, (f.acc).volatility,
       COALESCE(c0.witness_count, 0) + f.games,
       f.last_ts
FROM (
    SELECT s.subject_id, s.kind_id, s.object_id, s.games, s.last_ts,
           laplace_glicko2_accumulate(
               COALESCE(c.rating,     1500000000000::bigint),   -- prior = current consensus,
               COALESCE(c.rd,          350000000000::bigint),   -- neutral when the relation is new
               COALESCE(c.volatility,     60000000::bigint),
               1500000000000::bigint,                            -- NEUTRAL opponent μ
               s.phi,                                            -- opponent φ from witness trust
               CASE WHEN g.i < s.games THEN s.sum_score / s.games
                    ELSE s.sum_score - (s.sum_score / s.games) * (s.games - 1) END,
               500000000::bigint                                 -- τ = 0.5
           ) AS acc
    FROM fold_staging s
    LEFT JOIN laplace.consensus c
           ON c.id = laplace.consensus_id(s.subject_id, s.kind_id, s.object_id)
    CROSS JOIN LATERAL generate_series(1, s.games) AS g(i)
    GROUP BY s.subject_id, s.kind_id, s.object_id, s.games, s.last_ts,
             c.rating, c.rd, c.volatility
) f
LEFT JOIN laplace.consensus c0
       ON c0.id = laplace.consensus_id(f.subject_id, f.kind_id, f.object_id)
ON CONFLICT (id) DO UPDATE SET
    rating           = EXCLUDED.rating,
    rd               = EXCLUDED.rd,
    volatility       = EXCLUDED.volatility,
    witness_count    = EXCLUDED.witness_count,
    last_observed_at = EXCLUDED.last_observed_at";
            folded = await up.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _fold.Clear();
        return folded;
    }
}
