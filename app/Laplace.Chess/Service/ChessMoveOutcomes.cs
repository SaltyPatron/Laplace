using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

/// <summary>
/// BACKFILL arm of the move-outcome law: fold each witnessed line's result onto its MOVE
/// objects for games recorded BEFORE the fused ingest-time deposit existed,
/// ONCE, through the ingest law — markers, resume, eviction, consensus fold — so the
/// learned table is a consensus LOOKUP forever after, never a read-time fold.
///
/// WHY THIS GRAIN. A piece-square cell is a dot product (every arrival path summed at
/// query time); a move is a lookup — pe2e4 is a stored tier-2 object. The corpus move
/// vocabulary is 7,797 entities against 1.6M games, so this deposits a BOUNDED reusable
/// statistic exactly where the substrate keeps those: aggregated OUTCOME testimony,
/// Glicko-folded like the player cells. The read-time fold it replaces
/// (chess.learned_moves inside LearnedPst.ReadWhite) recomputed ~2.4M plies per process
/// start and after EVERY recorded live game — 6.4s measured on the deployed API for a
/// 384-cell answer that should be an index read.
///
/// Ply parity is the mover: constituent ordinals are 1-based, odd = White. The record
/// already fixes who moved; no board, no replay, no legal-move generation.
/// </summary>
public static class ChessMoveOutcomes
{
    public const int Version = 1;

    public static readonly Hash128 SourceId = SubstrateCanonicalIds.Source("ChessMoveOutcomes");
    public const string SourceName = "ChessMoveOutcomes";
    public static readonly Hash128 TrustClassId = ChessVocabulary.AnalysisTrustClass;

    // Calculated testimony, below the stockfish census's 0.95: the census measures the
    // move against an engine, this measures it against how its games ended.
    private const double OutcomeWeight = 0.9;

    public static Hash128 MarkerId(Hash128 lineId, int version)
        => Hash128.OfCanonical($"chess/move-outcomes/{lineId}/{version}");

    /// <summary>
    /// The RECORD-time deposit, shared by every path that witnesses a finished game:
    /// PGN ingest (fused, GH #600 pattern), the live hosts, and the backfill lane. One
    /// aggregated OUTCOME observation per ply onto the MOVE object, ctx = null so
    /// testimony MERGES per (move, source) -- consensus stays bounded by the 7,797-move
    /// vocabulary, never by games (#838's ballooning was the position-keyed web; this is
    /// the move-keyed statistic). The per-line marker lets the backfill true-skip lines
    /// any inline path already folded.
    /// </summary>
    public static void AppendGame(
        SubstrateChangeBuilder b, Hash128 lineId, IReadOnlyList<Hash128> moveIds,
        GameOutcome result, Hash128 src, double witnessWeight)
    {
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(moveIds);
        if (moveIds.Count == 0) return;

        for (int i = 0; i < moveIds.Count; i++)
            b.AddAttestation(NativeAttestation.Aggregated(
                subject: moveIds[i],
                typeId: ChessVocabulary.OutcomeType,
                obj: ChessVocabulary.OutcomeObject,
                sourceId: src,
                contextId: null,
                games: 1,
                sumScoreFp1e9: ChessGraph.ScoreFp1e9(result.ForMover(i % 2)),
                witnessWeight: witnessWeight));

        b.AddEntity(MarkerId(lineId, Version), EntityTier.Document,
            ChessVocabulary.AnalysisMarkerType, src);
    }

    /// <summary>Backfill arm: same deposit, under this lane's own source.</summary>
    public static void DeriveGame(SubstrateChangeBuilder b, ChessWitnessedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        AppendGame(b, game.LineId, game.MoveIds, game.Result, SourceId, OutcomeWeight);
    }
}

/// <summary>
/// Line-grain record; trunk root is the versioned per-LINE marker so re-runs dedup
/// against the marker, never against the line. Same law as the stockfish census.
/// </summary>
public sealed record ChessMoveOutcomeRecord(ChessWitnessedGame Game) : ITrunkRootRecord
{
    public Hash128 TrunkRootId => ChessMoveOutcomes.MarkerId(Game.LineId, ChessMoveOutcomes.Version);
}

// Run: `laplace ingest chess-move-outcomes`  (no path — the substrate is the source)
public sealed class ChessMoveOutcomesDecomposer
    : ComposeDecomposer<ChessMoveOutcomeRecord>, IIngestNoOpExplainer
{
    private long _candidatesStreamed;

    public override Hash128 SourceId => ChessMoveOutcomes.SourceId;
    public override string SourceName => ChessMoveOutcomes.SourceName;
    public override int LayerOrder => 22;
    public override Hash128 TrustClassId => ChessMoveOutcomes.TrustClassId;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "chess/move-outcomes";

    public override int EstimatedBytesPerRecord => IngestSourceProfile.ChessAnalyze.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => IngestSourceProfile.ChessAnalyze.EstComposeUnitsPerRecord;

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessMoveOutcomes.SourceId, SourceName, ChessMoveOutcomes.TrustClassId, ct);

    protected override async IAsyncEnumerable<ChessMoveOutcomeRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (ContainmentReader is null
            || ChessWitnessHydrator.TryResolveDataSource(ContainmentReader) is not { } ds)
            throw new InvalidOperationException(
                "ChessMoveOutcomes requires a live Postgres substrate (NpgsqlSubstrateReader). "
                + "Record games first: laplace ingest chess <pgn>");

        var ws = IngestPipelineDefaults.ResolveWorkingSet(PipelineProfile, options);
        _candidatesStreamed = 0;
        await foreach (var witnessed in ChessWitnessHydrator.StreamUnanalyzedLinesAsync(
                           ds, ContainmentReader!, ws.Batch,
                           lineId => ChessMoveOutcomes.MarkerId(lineId, ChessMoveOutcomes.Version), ct))
        {
            if (witnessed.MoveIds.Count == 0) continue;
            _candidatesStreamed++;
            yield return new ChessMoveOutcomeRecord(witnessed);
        }
    }

    protected override void Compose(ChessMoveOutcomeRecord record, SubstrateChangeBuilder b)
        => ChessMoveOutcomes.DeriveGame(b, record.Game);

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        if (ChessWitnessHydrator.TryResolveDataSource(context.Reader) is not { } ds)
            return Task.FromResult<long?>(null);
        return ChessWitnessHydrator.CountRecordedLinesAsync(ds, ct);
    }

    /// <summary>Nothing streamed means every line already carries this version's marker.</summary>
    public (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits)
        => _candidatesStreamed == 0
            ? ("already-complete",
               $"ChessMoveOutcomes: every one of {declaredInputUnits} recorded line(s) already "
               + $"carries the v{ChessMoveOutcomes.Version} move-outcome marker — nothing left to fold.")
            : null;
}
