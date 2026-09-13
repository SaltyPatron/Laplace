using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

/// <summary>
/// Bounded tactical-pattern learning lane. Exact chess positions are mostly singleton evidence;
/// color-normalized fork/pin/skewer patterns recur across the entire corpus and therefore form
/// real statistical subjects. Each recorded game contributes at most one outcome observation per
/// (pattern, owning side), preventing a long-lived pin from counting once per ply.
/// </summary>
public static class ChessTacticOutcomes
{
    public const int Version = 1;
    public const string SourceName = "ChessTacticOutcomes";
    public static readonly Hash128 SourceId = SubstrateCanonicalIds.Source(SourceName);
    public static readonly Hash128 TrustClassId = ChessVocabulary.AnalysisTrustClass;
    private const double OutcomeWeight = 0.9;

    public static Hash128 MarkerId(Hash128 playingId)
        => Hash128.OfCanonical($"chess/tactic-outcomes/{playingId}/{Version}");

    public static string Surface(ChessTacticPattern pattern)
        => $"chess/tactic/{pattern}";

    public static Hash128? PatternId(ChessTacticPattern pattern)
        => ContentEmitter.RootId(Surface(pattern));

    /// <summary>Fused path for a replay whose board sequence is already available.</summary>
    public static void AppendGame(
        SubstrateChangeBuilder b,
        IReadOnlyList<Board> boards,
        GameOutcome result,
        Hash128 playingId,
        Hash128 sourceId,
        double witnessWeight)
    {
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(boards);
        if (boards.Count == 0) return;

        // One observation per side/pattern per game. The stored subject is color-normalized;
        // outcome is from the pattern owner's POV, so white/black examples corroborate.
        var seen = new HashSet<(long Key, bool OwnerWhite)>();
        var aggregate = new Dictionary<long, (ChessTacticPattern Pattern, int Games, long ScoreSum)>();

        foreach (var board in boards)
        {
            Collect(board, ownerWhite: true, result, seen, aggregate);
            Collect(board, ownerWhite: false, result, seen, aggregate);
        }

        foreach (var (_, entry) in aggregate)
        {
            if (ContentEmitter.Emit(b, Surface(entry.Pattern), sourceId) is not { } patternId)
                continue;
            b.AddAttestation(NativeAttestation.Aggregated(
                subject: patternId,
                typeId: ChessVocabulary.OutcomeType,
                obj: ChessVocabulary.OutcomeObject,
                sourceId: sourceId,
                contextId: playingId,
                games: entry.Games,
                sumScoreFp1e9: entry.ScoreSum,
                witnessWeight: witnessWeight));
        }

        b.AddEntity(MarkerId(playingId), EntityTier.Document,
            ChessVocabulary.AnalysisMarkerType, sourceId);
    }

    private static void Collect(
        Board board,
        bool ownerWhite,
        GameOutcome result,
        HashSet<(long Key, bool OwnerWhite)> seen,
        Dictionary<long, (ChessTacticPattern Pattern, int Games, long ScoreSum)> aggregate)
    {
        foreach (var pattern in ChessTacticGeometry.Forks(board, ownerWhite)
                     .Concat(ChessTacticGeometry.PinsAndSkewers(board, ownerWhite)))
        {
            if (!seen.Add((pattern.Key, ownerWhite))) continue;
            long score = ChessGraph.ScoreFp1e9(result.ForMover(ownerWhite ? 0 : 1));
            if (aggregate.TryGetValue(pattern.Key, out var prior))
                aggregate[pattern.Key] = (prior.Pattern, prior.Games + 1, prior.ScoreSum + score);
            else
                aggregate[pattern.Key] = (pattern, 1, score);
        }
    }

    /// <summary>Backfill path: replay witnessed SAN and deposit only this lane.</summary>
    public static void DeriveGame(SubstrateChangeBuilder b, ChessWitnessedGame game)
    {
        if (!TryReplay(game, out var boards)) return;
        AppendGame(b, boards, game.Result, game.PlayingId, SourceId, OutcomeWeight);
    }

    internal static bool TryReplay(ChessWitnessedGame game, out IReadOnlyList<Board> boards)
    {
        var modality = new ChessModality();
        if (ChessAnalyze.InitialState(game.StartFen, modality) is not { } start)
        {
            boards = Array.Empty<Board>();
            return false;
        }

        var state = start.Initial;
        var result = new List<Board>(game.Moves.Count + 1) { state.Board.Clone() };
        var scratch = new List<ChessMove>(32);
        foreach (string san in game.Moves)
        {
            if (San.Resolve(state.Board, san, scratch) is not { } move)
            {
                boards = Array.Empty<Board>();
                return false;
            }
            state = modality.Apply(state, move);
            result.Add(state.Board.Clone());
        }
        boards = result;
        return true;
    }
}

public sealed record ChessTacticOutcomeRecord(ChessWitnessedGame Game) : ITrunkRootRecord
{
    public Hash128 TrunkRootId => ChessTacticOutcomes.MarkerId(Game.PlayingId);
}

/// <summary>
/// Historical backfill without bumping ChessAnalyze.Version. Re-running the whole analyzer would
/// double accumulated testimony; this lane writes only the new bounded tactic statistics and its
/// own marker.
/// </summary>
public sealed class ChessTacticOutcomesDecomposer
    : ComposeDecomposer<ChessTacticOutcomeRecord>, IIngestNoOpExplainer
{
    private long _candidatesStreamed;

    public override Hash128 SourceId => ChessTacticOutcomes.SourceId;
    public override string SourceName => ChessTacticOutcomes.SourceName;
    public override int LayerOrder => 24;
    public override Hash128 TrustClassId => ChessTacticOutcomes.TrustClassId;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "chess/tactic-outcomes";

    public override int EstimatedBytesPerRecord => IngestSourceProfile.ChessAnalyze.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => IngestSourceProfile.ChessAnalyze.EstComposeUnitsPerRecord;

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, SourceId, SourceName, TrustClassId, ct);

    protected override async IAsyncEnumerable<ChessTacticOutcomeRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (ContainmentReader is null
            || ChessWitnessHydrator.TryResolveDataSource(ContainmentReader) is not { } ds)
            throw new InvalidOperationException(
                "ChessTacticOutcomes requires a live Postgres substrate. Record games first.");

        var ws = IngestPipelineDefaults.ResolveWorkingSet(PipelineProfile, options);
        _candidatesStreamed = 0;
        await foreach (var witnessed in ChessWitnessHydrator.StreamUnanalyzedEventsAsync(
                           ds, ContainmentReader!, ws.Batch,
                           playingId => ChessTacticOutcomes.MarkerId(playingId),
                           includeLive: true, ct))
        {
            _candidatesStreamed++;
            yield return new ChessTacticOutcomeRecord(witnessed);
        }
    }

    protected override void Compose(ChessTacticOutcomeRecord record, SubstrateChangeBuilder b)
        => ChessTacticOutcomes.DeriveGame(b, record.Game);

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        if (ChessWitnessHydrator.TryResolveDataSource(context.Reader) is not { } ds)
            return Task.FromResult<long?>(null);
        return ChessWitnessHydrator.CountRecordedPlayingsAsync(ds, ct);
    }

    public (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits)
        => _candidatesStreamed == 0
            ? ("already-complete",
               $"ChessTacticOutcomes: every one of {declaredInputUnits} recorded playing(s) " +
               $"already carries the v{ChessTacticOutcomes.Version} tactic-outcome marker.")
            : null;
}
