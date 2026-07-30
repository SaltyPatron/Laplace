using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

/// <summary>
/// Backfills the GAME TRAJECTORY onto games recorded before it existed, and nothing else.
///
/// The obvious way to reach those games would be to bump ChessAnalyze.Version and re-run the
/// analyzer. That is exactly wrong here: rows are idempotent but TESTIMONY IS NOT. Attestation
/// merge ACCUMULATES observation_count (attestation_merge regress pins 3+5=8), so re-deriving
/// ~29M ChessAnalysis attestations over the standing corpus would double every witness count in
/// the calculated layer — the runner refuses source re-ingest for this exact reason ("a re-ingest
/// would double-count testimony into consensus"). Inflating consensus to add geometry would be a
/// far worse trade than not having the geometry.
///
/// A trajectory is not testimony. It is a PHYSICALITY, keyed by id and written as an upsert —
/// no counter, nothing to double. So this pass deposits ONLY the game's linestring plus its own
/// completion marker, and touches no attestation at all. Re-running it is a no-op on rows already
/// present, and safe on rows that are.
///
/// The marker is versioned independently of ChessAnalyze.Version so this backfill and a future
/// analysis re-derive can never be mistaken for one another.
///
/// COMMIT CADENCE — read this before running it over a large corpus. In working-set mode the
/// runner's only flush trigger is accumulated apply bytes reaching the flush envelope (RAM/64,
/// ceiling 512 MiB). These records are unusually small — two rows plus one linestring, ~2.9 KB
/// for an 80-ply game — so the envelope is not reached until roughly 176,000 games. A full run
/// over a 200k corpus therefore commits ONCE, near the end: it is idempotent across completed
/// runs, but a run killed before that first flush loses everything it composed. Marker-based
/// skipping resumes across runs, not within one.
///
/// Set LAPLACE_WS_FLUSH_MB to commit sooner — e.g. 32 flushes about every 11,000 games, which
/// makes an interrupted run resumable at that granularity. Nothing about correctness changes
/// either way; only how much work an interruption costs.
///
/// Run: `laplace ingest chess-trajectory` (no path — the substrate is the source of truth)
///      `LAPLACE_WS_FLUSH_MB=32 laplace ingest chess-trajectory`  (frequent commits)
/// </summary>
public sealed class ChessTrajectoryDecomposer : ComposeDecomposer<ChessTrajectoryRecord>
{
    /// <summary>
    /// Marker generation for THIS pass. Bump only when the trajectory encoding itself changes;
    /// deliberately distinct from ChessAnalyze.Version, which governs the testimony re-derive.
    /// </summary>
    public const int TrajectoryVersion = 1;

    // GH #736: the linestring is a pure function of the LINE, so the marker is per line —
    // a line shared by many playings deposits ONE trajectory, and the duplicate-linestring
    // disease #736 names is structurally gone.
    public static Hash128 MarkerId(Hash128 lineId)
        => Hash128.OfCanonical($"chess/trajectory/{lineId}/{TrajectoryVersion}");

    // GH #736 source split (#508): the trajectory lane writes under its OWN source so
    // source-grain eviction never conflates it with ChessAnalysis testimony.
    public override Hash128 SourceId => ChessVocabulary.TrajectorySourceId;
    public override string SourceName => "ChessTrajectory";
    public override int LayerOrder => 21;
    public override Hash128 TrustClassId => ChessVocabulary.AnalysisTrustClass;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "chess/trajectory";
    protected override int DefaultBatchSize => BatchConfigDefaults.Chess;

    public override int EstimatedBytesPerRecord => IngestSourceProfile.ChessAnalyze.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => IngestSourceProfile.ChessAnalyze.EstComposeUnitsPerRecord;

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessVocabulary.TrajectorySourceId, SourceName, ChessVocabulary.AnalysisTrustClass, ct);

    protected override async IAsyncEnumerable<ChessTrajectoryRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (ContainmentReader is null
            || ChessWitnessHydrator.TryResolveDataSource(ContainmentReader) is not { } ds)
            throw new InvalidOperationException(
                "ChessAnalysis requires a live Postgres substrate (NpgsqlSubstrateReader). "
                + "Record games first: laplace ingest chess <pgn>");

        var ws = IngestPipelineDefaults.ResolveWorkingSet(PipelineProfile, options, DefaultBatchSize);
        // LINE-grain stream (GH #736), gated on THIS pass's per-line marker: a line whose
        // trajectory has already been COMMITTED is skipped before compose, so a second run
        // costs a bitmap probe per line rather than a replay. Composed-but-uncommitted work is
        // not skipped — see the commit-cadence note above.
        await foreach (var witnessed in ChessWitnessHydrator.StreamUnanalyzedLinesAsync(
                           ds, ContainmentReader!, ws.Batch, MarkerId, ct))
            yield return new ChessTrajectoryRecord(witnessed);
    }

    protected override void Compose(ChessTrajectoryRecord record, SubstrateChangeBuilder b)
        => Deposit(b, record.Game, SourceId);

    /// <summary>
    /// The pass itself, as a pure function of a hydrated game: replay the line, deposit its
    /// trajectory and this pass's marker. Public so the geometry can be pinned without a live
    /// substrate — and static because nothing about it depends on decomposer instance state.
    /// </summary>
    public static void Deposit(SubstrateChangeBuilder b, ChessWitnessedGame w, Hash128 sourceId)
    {
        var m = new ChessModality();
        var state = w.StartFen is { Length: > 0 } fen ? m.FromFen(fen) : m.Initial();

        var line = new List<ChessNode>(w.Moves.Count + 1);
        lock (ChessCompose.Gate)
        {
            line.Add(ChessCompose.Position(m.StateKey(state)).Position);
            foreach (var san in w.Moves)
            {
                var mv = San.Resolve(state.Board, m.LegalActions(state), san);
                // A line that will not replay is a line this game never walked. Deposit
                // nothing — not the trajectory, not the marker — so a later run can retry.
                if (mv is null) return;
                state = m.Apply(state, mv.Value);
                line.Add(ChessCompose.Position(m.StateKey(state)).Position);
            }
        }

        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        ChessGraph.AppendGameTrajectory(b, w.GameId, line, sourceId, nowUs);
        b.AddEntity(MarkerId(w.GameId), EntityTier.Document,
                    ChessVocabulary.AnalysisMarkerType, sourceId);
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        if (ChessWitnessHydrator.TryResolveDataSource(context.Reader) is not { } ds)
            return Task.FromResult<long?>(null);
        return ChessWitnessHydrator.CountRecordedGamesAsync(ds, ct);
    }
}

/// <summary>Trunk root is this pass's marker, so its batches never collide with the analyzer's.</summary>
public sealed record ChessTrajectoryRecord(ChessWitnessedGame Game) : ITrunkRootRecord
{
    public Hash128 TrunkRootId => ChessTrajectoryDecomposer.MarkerId(Game.GameId);
}
