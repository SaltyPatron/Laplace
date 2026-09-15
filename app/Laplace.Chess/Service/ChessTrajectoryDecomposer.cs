using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

public sealed class ChessTrajectoryDecomposer
    : ComposeDecomposer<ChessTrajectoryRecord>, IIngestNoOpExplainer
{
    private long _candidatesStreamed;

    public const int TrajectoryVersion = 1;

    public static Hash128 MarkerId(Hash128 lineId)
        => Hash128.OfCanonical($"chess/trajectory/{lineId}/{TrajectoryVersion}");

    public override Hash128 SourceId => ChessVocabulary.TrajectorySourceId;
    public override string SourceName => "ChessTrajectory";
    public override int LayerOrder => 21;
    public override Hash128 TrustClassId => ChessVocabulary.AnalysisTrustClass;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "chess/trajectory";

    public override int EstimatedBytesPerRecord => IngestSourceProfile.ChessAnalyze.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => IngestSourceProfile.ChessAnalyze.EstComposeUnitsPerRecord;
    public override IngestSourceProfile SizingProfile => IngestSourceProfile.ChessAnalyze;

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

        var ws = IngestPipelineDefaults.ResolveWorkingSet(PipelineProfile, options);
        _candidatesStreamed = 0;
        await foreach (var witnessed in ChessWitnessHydrator.StreamUnanalyzedLinesAsync(
                           ds, ContainmentReader!, ws.Batch, MarkerId, ct))
        {
            _candidatesStreamed++;
            yield return ChessTrajectoryRecord.ForGame(witnessed);
        }

        await foreach (var (playerId, name) in
                       ChessWitnessHydrator.StreamPlayersMissingPhysicalityAsync(ds, ws.Batch, ct))
        {
            _candidatesStreamed++;
            yield return ChessTrajectoryRecord.ForPlayer(playerId, name);
        }
    }

    public (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits)
        => _candidatesStreamed == 0
            ? ("already-complete",
               $"ChessTrajectory: all {declaredInputUnits} declared chess line/player "
               + "physicalities are already present.")
            : null;

    protected override void Compose(ChessTrajectoryRecord record, SubstrateChangeBuilder b)
    {
        if (record.Game is { } game)
            Deposit(b, game, SourceId);
        else if (record.PlayerId is { } playerId && record.PlayerName is { } name)
            ChessVocabulary.AppendPlayerPhysicality(b, playerId, name, SourceId);
    }

    public static void Deposit(SubstrateChangeBuilder b, ChessWitnessedGame w, Hash128 sourceId)
    {
        var m = new ChessModality();
        if (ChessAnalyze.InitialState(w.StartFen, m) is not { } start) return;
        var state = start.Initial;

        var line = new List<ChessNode>(w.Moves.Count + 1);
        lock (ChessCompose.Gate)
        {
            line.Add(ChessCompose.Position(state.Board).Position);
            foreach (var san in w.Moves)
            {
                var mv = San.Resolve(state.Board, m.LegalActions(state), san);
                if (mv is null) return;
                state = m.Apply(state, mv.Value);
                line.Add(ChessCompose.Position(state.Board).Position);
            }
        }

        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        ChessGraph.AppendPositionProjection(b, w.LineId, line, sourceId, nowUs);
        b.AddEntity(MarkerId(w.LineId), EntityTier.Document,
                    ChessVocabulary.AnalysisMarkerType, sourceId);
    }

    public override async Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default)
    {
        if (ChessWitnessHydrator.TryResolveDataSource(context.Reader) is not { } ds)
            return null;
        var linesTask = ChessWitnessHydrator.CountRecordedLinesAsync(ds, ct);
        var playersTask = NpgsqlSubstrateReads.CountChessPlayersMissingPhysicalityAsync(
            ds, ChessVocabulary.PlayerType.ToBytes(), (short)PhysicalityType.Projection, ct);
        await Task.WhenAll(linesTask, playersTask).ConfigureAwait(false);
        return (linesTask.Result ?? 0) + playersTask.Result;
    }
}

public sealed record ChessTrajectoryRecord : ITrunkRootRecord
{
    private ChessTrajectoryRecord(ChessWitnessedGame? game, Hash128? playerId, string? playerName)
        => (Game, PlayerId, PlayerName) = (game, playerId, playerName);

    public ChessWitnessedGame? Game { get; }
    public Hash128? PlayerId { get; }
    public string? PlayerName { get; }

    public static ChessTrajectoryRecord ForGame(ChessWitnessedGame game) =>
        new(game, null, null);

    public static ChessTrajectoryRecord ForPlayer(Hash128 playerId, string name) =>
        new(null, playerId, name);

    public Hash128 TrunkRootId => Game is { } game
        ? ChessTrajectoryDecomposer.MarkerId(game.LineId)
        : PhysicalityId.Compute(PlayerId!.Value, PhysicalityType.Projection);
}
