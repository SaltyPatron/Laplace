using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

/// <summary>
/// The bounded deterministic transition fold: one consensus cell per witnessed
/// position --MOVE--> position, with each playing retained as evidence context.
/// It is isolated from ChessAnalysis so an existing corpus can be backfilled without
/// re-emitting and double-counting the standing calculated testimony.
/// </summary>
public static class ChessTransitions
{
    public const int Version = 1;
    public static readonly Hash128 SourceId = SubstrateCanonicalIds.Source("ChessTransitions");
    public static readonly Hash128 TrustClassId = ChessVocabulary.AnalysisTrustClass;

    public static Hash128 MarkerId(Hash128 playingId) =>
        Hash128.OfCanonical($"chess/transitions/{playingId}/{Version}");

    internal static void DepositFromParsed(SubstrateChangeBuilder b, ChessGameRecord parsed)
    {
        ChessGraph.AppendTransitions(
            b, parsed.PositionIds, parsed.Result,
            TC.StructuredCorpus, SourceId, parsed.PlayingId);
        b.AddEntity(
            MarkerId(parsed.PlayingId), EntityTier.Document,
            ChessVocabulary.AnalysisMarkerType, SourceId);
    }

    internal static void Deposit(SubstrateChangeBuilder b, ChessWitnessedGame game)
    {
        var modality = new ChessModality();
        if (ChessAnalyze.InitialState(game.StartFen, modality) is not { } initial) return;
        var state = initial.Initial;
        var positions = new List<Hash128>(game.Moves.Count + 1)
        {
            ChessCompose.PositionId(state.Board),
        };
        var scratch = new List<ChessMove>(32);
        foreach (string san in game.Moves)
        {
            var move = San.Resolve(state.Board, san, scratch);
            if (move is null) return;
            state = modality.Apply(state, move.Value);
            positions.Add(ChessCompose.PositionId(state.Board));
        }
        ChessGraph.AppendTransitions(
            b, positions, game.Result, TC.StructuredCorpus, SourceId, game.PlayingId);
        b.AddEntity(
            MarkerId(game.PlayingId), EntityTier.Document,
            ChessVocabulary.AnalysisMarkerType, SourceId);
    }
}

public sealed class ChessTransitionsDecomposer
    : ComposeDecomposer<ChessTransitionRecord>, IIngestNoOpExplainer
{
    private long _candidatesStreamed;
    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();

    public override Hash128 SourceId => ChessTransitions.SourceId;
    public override string SourceName => "ChessTransitions";
    public override int LayerOrder => 22;
    public override Hash128 TrustClassId => ChessTransitions.TrustClassId;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "chess/transitions";
    public override int EstimatedBytesPerRecord => IngestSourceProfile.ChessAnalyze.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => IngestSourceProfile.ChessAnalyze.EstComposeUnitsPerRecord;
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, SourceId, SourceName, TrustClassId, ct);

    protected override async IAsyncEnumerable<ChessTransitionRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (ContainmentReader is null
            || ChessWitnessHydrator.TryResolveDataSource(ContainmentReader) is not { } ds)
            throw new InvalidOperationException(
                "ChessTransitions requires a live Postgres substrate. Record games first.");
        var ws = IngestPipelineDefaults.ResolveWorkingSet(PipelineProfile, options);
        _candidatesStreamed = 0;
        await foreach (var game in ChessWitnessHydrator.StreamUnanalyzedEventsAsync(
                           ds, ContainmentReader, ws.Batch, ChessTransitions.MarkerId,
                           includeLive: true, ct))
        {
            _candidatesStreamed++;
            yield return new ChessTransitionRecord(game);
        }
    }

    protected override void Compose(ChessTransitionRecord record, SubstrateChangeBuilder b) =>
        ChessTransitions.Deposit(b, record.Game);

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        if (ChessWitnessHydrator.TryResolveDataSource(context.Reader) is not { } ds)
            return Task.FromResult<long?>(null);
        return ChessWitnessHydrator.CountTransitionEventsAsync(ds, ct);
    }

    public (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits) =>
        _candidatesStreamed == 0
            ? ("already-complete",
                $"ChessTransitions: every one of {declaredInputUnits} playing(s) carries the v{ChessTransitions.Version} marker.")
            : null;
}

public sealed record ChessTransitionRecord(ChessWitnessedGame Game) : ITrunkRootRecord
{
    public Hash128 TrunkRootId => ChessTransitions.MarkerId(Game.PlayingId);
}
