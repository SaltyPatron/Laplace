using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

/// <summary>
/// Reusable board-structure evidence. A witnessed result is expressed once for every
/// constituent of every board in the played trajectory, in White's fixed point of view. The
/// board and its constituents are ordinary Laplace content entities with lossless physicalities;
/// the compose floor only accelerates their deterministic geometry. The evidence subjects are
/// the finite chess atom vocabulary (piece-square, side, castling and en-passant), so repeated
/// games converge on the same cells instead of minting a game-sized read-time web.
/// </summary>
public static class ChessPositionOutcomes
{
    // v2 includes the terminal board. v1 deposited only pre-move boards, leaving every completed
    // line's final trajectory constituent without its content entity/physicality.
    public const int Version = 2;
    public const string SourceName = "ChessPositionOutcomes";
    public static readonly Hash128 SourceId = SubstrateCanonicalIds.Source(SourceName);
    public static readonly Hash128 TrustClassId = ChessVocabulary.AnalysisTrustClass;
    private const double OutcomeWeight = 0.9;

    public static Hash128 MarkerId(Hash128 playingId) =>
        Hash128.OfCanonical($"chess/position-outcomes/{playingId}/{Version}");

    internal static void DepositFromParsed(SubstrateChangeBuilder b, ChessGameRecord game)
    {
        var modality = new ChessModality();
        string? startFen = PgnGames.TagStr(game.GameText, "SetUp") == "1"
            ? PgnGames.TagStr(game.GameText, "FEN") : null;
        if (ChessAnalyze.InitialState(startFen, modality) is not { } start) return;
        var state = start.Initial;
        for (int ply = 0; ply < game.ResolvedMoves.Length; ply++)
        {
            AppendBoard(b, state.Board, game.Result);
            state = modality.Apply(state, game.ResolvedMoves[ply]);
        }
        AppendBoard(b, state.Board, game.Result);
        AddMarker(b, game.PlayingId);
    }

    /// <summary>
    /// Fused PGN path: reuse the N+1 positions already composed for this parsed game. The
    /// PositionOutcomes lane still stages the position/substructure content it owns before
    /// attaching its outcome evidence; it simply does not run ChessCompose.Position again.
    /// </summary>
    internal static void DepositFromParsed(
        SubstrateChangeBuilder b, ChessGameRecord game, ChessParsedReplay replay)
    {
        if (!replay.IsCompleteFor(game))
        {
            DepositFromParsed(b, game);
            return;
        }

        foreach (var position in replay.Positions)
            AppendComposed(b, ChessGraph.EmitComposed(b, position, SourceId), game.Result);
        AddMarker(b, game.PlayingId);
    }

    internal static void Deposit(SubstrateChangeBuilder b, ChessWitnessedGame game)
    {
        var modality = new ChessModality();
        if (ChessAnalyze.InitialState(game.StartFen, modality) is not { } start) return;
        var state = start.Initial;
        var scratch = new List<ChessMove>(32);
        foreach (string san in game.Moves)
        {
            var move = San.Resolve(state.Board, san, scratch);
            if (move is null) return;
            AppendBoard(b, state.Board, game.Result);
            state = modality.Apply(state, move.Value);
        }
        AppendBoard(b, state.Board, game.Result);
        AddMarker(b, game.PlayingId);
    }

    internal static void DepositTrajectory(
        SubstrateChangeBuilder b, IReadOnlyList<string> positionSurfaces,
        GameOutcome result, Hash128 playingId)
    {
        foreach (string surface in positionSurfaces)
            AppendComposed(b, ChessGraph.EmitComposed(b, surface, SourceId), result);
        AddMarker(b, playingId);
    }

    private static void AppendBoard(SubstrateChangeBuilder b, Board board, GameOutcome result)
    {
        var composed = ChessGraph.EmitComposed(b, board, SourceId);
        AppendComposed(b, composed, result);
    }

    private static void AppendComposed(
        SubstrateChangeBuilder b, ChessComposed composed, GameOutcome result)
    {
        long score = ChessGraph.ScoreFp1e9(result.ForMover(0));
        foreach (var atom in composed.Substructures)
            b.AddAttestation(NativeAttestation.Aggregated(
                subject: atom.Id,
                typeId: ChessVocabulary.OutcomeType,
                obj: ChessVocabulary.OutcomeObject,
                sourceId: SourceId,
                contextId: null,
                games: 1,
                sumScoreFp1e9: score,
                witnessWeight: OutcomeWeight));
    }

    private static void AddMarker(SubstrateChangeBuilder b, Hash128 playingId) =>
        b.AddEntity(MarkerId(playingId), EntityTier.Document,
            ChessVocabulary.AnalysisMarkerType, SourceId);
}

public sealed record ChessPositionOutcomeRecord(ChessWitnessedGame Game) : ITrunkRootRecord
{
    public Hash128 TrunkRootId => ChessPositionOutcomes.MarkerId(Game.PlayingId);
}

/// <summary>Marker-gated backfill for games recorded before the fused constituent fold.</summary>
public sealed class ChessPositionOutcomesDecomposer
    : ComposeDecomposer<ChessPositionOutcomeRecord>, IIngestNoOpExplainer
{
    private long _candidatesStreamed;
    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();

    public override Hash128 SourceId => ChessPositionOutcomes.SourceId;
    public override string SourceName => ChessPositionOutcomes.SourceName;
    public override int LayerOrder => 22;
    public override Hash128 TrustClassId => ChessPositionOutcomes.TrustClassId;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "chess/position-outcomes";
    public override int EstimatedBytesPerRecord => IngestSourceProfile.ChessAnalyze.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => IngestSourceProfile.ChessAnalyze.EstComposeUnitsPerRecord;
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, SourceId, SourceName, TrustClassId, ct);

    protected override async IAsyncEnumerable<ChessPositionOutcomeRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (ContainmentReader is null
            || ChessWitnessHydrator.TryResolveDataSource(ContainmentReader) is not { } ds)
            throw new InvalidOperationException(
                "ChessPositionOutcomes requires a live Postgres substrate. Record games first.");
        var ws = IngestPipelineDefaults.ResolveWorkingSet(PipelineProfile, options);
        _candidatesStreamed = 0;
        await foreach (var game in ChessWitnessHydrator.StreamUnanalyzedEventsAsync(
                           ds, ContainmentReader, ws.Batch,
                           ChessPositionOutcomes.MarkerId, includeLive: true, ct))
        {
            _candidatesStreamed++;
            yield return new ChessPositionOutcomeRecord(game);
        }
    }

    protected override void Compose(ChessPositionOutcomeRecord record, SubstrateChangeBuilder b) =>
        ChessPositionOutcomes.Deposit(b, record.Game);

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        if (ChessWitnessHydrator.TryResolveDataSource(context.Reader) is not { } ds)
            return Task.FromResult<long?>(null);
        return ChessWitnessHydrator.CountTransitionEventsAsync(ds, ct);
    }

    public (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits) =>
        _candidatesStreamed == 0
            ? ("already-complete",
                $"ChessPositionOutcomes: every one of {declaredInputUnits} playing(s) carries the v{ChessPositionOutcomes.Version} marker.")
            : null;
}
