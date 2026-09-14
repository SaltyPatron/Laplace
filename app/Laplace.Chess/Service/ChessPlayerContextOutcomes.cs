using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

/// <summary>
/// Player-conditioned context evidence for A8. The relation vocabulary does not change:
/// overall standing is (player, OUTCOME, Chess_Result); contextual performance is
/// (player, OUTCOME, context-content). A context is a deterministic board phase or a witnessed
/// clock/think class. Keeping the object typed this way makes "Magnus in endgames" and "Magnus
/// while flagging" indexed consensus cells without contaminating the player's overall standing.
///
/// One playing contributes at most one observation per (player, context). A 90-move game must not
/// become 40 independent votes that a player is good in an endgame merely because the same final
/// outcome was visible on 40 endgame plies.
/// </summary>
public static class ChessPlayerContextOutcomes
{
    public const int Version = 1;
    public const string SourceName = "ChessPlayerContextOutcomes";
    public static readonly Hash128 SourceId = SubstrateCanonicalIds.Source(SourceName);
    public static readonly Hash128 TrustClassId = ChessVocabulary.AnalysisTrustClass;
    private const double WitnessWeight = 0.8;

    public static Hash128 MarkerId(Hash128 playingId)
        => Hash128.OfCanonical($"chess/player-context-outcomes/{playingId}/{Version}");

    public static void DeriveGame(SubstrateChangeBuilder b, ChessWitnessedGame game)
    {
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(game);

        var modality = new ChessModality();
        if (ChessAnalyze.InitialState(game.StartFen, modality) is not { } start)
        {
            Stamp(b, game.PlayingId);
            return;
        }

        var state = start.Initial;
        int plies = game.Moves.Count;
        var scratch = new List<ChessMove>(16);
        var seen = new HashSet<(Hash128 Player, string Context)>();

        double[] clocks = game.ClockTokens is not null
            ? game.ClockTokens.Select(static token =>
                token is not null && PgnClocks.TryParseHms(token, out double sec) ? sec : 0d).ToArray()
            : [];
        double medianDrop = PgnClocks.MedianDrop(clocks);
        double medianRemEven = PgnClocks.MedianRemaining(clocks, 0);
        double medianRemOdd = PgnClocks.MedianRemaining(clocks, 1);
        double medianSpent = PgnClocks.MedianSpent(game.SpentSeconds);

        for (int ply = 0; ply < plies; ply++)
        {
            int mover = modality.SideToMove(state);
            Hash128? player = mover == 0 ? game.WhitePlayer : game.BlackPlayer;
            if (player is { } pid)
            {
                PlyOutcome outcome = game.Result.ForMover(mover);
                AppendOnce(b, seen, pid, ChessCanonical.PhaseClass(state.Board), outcome, game.PlayingId);

                string? clockToken = game.ClockTokens is not null && ply < game.ClockTokens.Length
                    ? game.ClockTokens[ply]
                    : null;
                if (clockToken is not null && clocks.Length > ply)
                {
                    double tf = PgnClocks.ThinkFactor(clocks, medianDrop, ply);
                    AppendOnce(b, seen, pid, ChessCanonical.ThinkClass(tf), outcome, game.PlayingId);
                    if (ChessCanonical.ThinkLens(
                            ply, plies, tf, clocks[ply],
                            (ply & 1) == 0 ? medianRemEven : medianRemOdd,
                            medianDrop) is { } lens)
                        AppendOnce(b, seen, pid, lens, outcome, game.PlayingId);
                }
                else if (game.SpentSeconds is { } spent
                         && ply < spent.Length && medianSpent > 0)
                {
                    double tf = PgnClocks.ThinkFactorFromSpent(spent, medianSpent, ply);
                    AppendOnce(b, seen, pid, ChessCanonical.ThinkClass(tf), outcome, game.PlayingId);
                    if (ChessCanonical.ThinkLens(
                            ply, plies, tf,
                            remaining: 0, medianRemaining: 0, medianDrop: 0) is { } lens)
                        AppendOnce(b, seen, pid, lens, outcome, game.PlayingId);
                }
            }

            var move = San.Resolve(state.Board, game.Moves[ply], scratch);
            if (move is null)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "ChessPlayerContextOutcomes: could not replay {0} at ply {1}; contextual prefix retained",
                    game.PlayingId, ply + 1);
                break;
            }
            state = modality.Apply(state, move.Value);
        }

        Stamp(b, game.PlayingId);
    }

    private static void AppendOnce(
        SubstrateChangeBuilder b,
        HashSet<(Hash128 Player, string Context)> seen,
        Hash128 player,
        string contextSurface,
        PlyOutcome outcome,
        Hash128 playingId)
    {
        if (!seen.Add((player, contextSurface))) return;
        if (ContentEmitter.Emit(b, contextSurface, SourceId) is not { } contextId) return;
        b.AddAttestation(NativeAttestation.Aggregated(
            subject: player,
            typeId: ChessVocabulary.OutcomeType,
            obj: contextId,
            sourceId: SourceId,
            contextId: playingId,
            games: 1,
            sumScoreFp1e9: ChessGraph.ScoreFp1e9(outcome),
            witnessWeight: WitnessWeight));
    }

    private static void Stamp(SubstrateChangeBuilder b, Hash128 playingId)
        => b.AddEntity(
            MarkerId(playingId), EntityTier.Document,
            ChessVocabulary.AnalysisMarkerType, SourceId);
}

/// <summary>
/// Marker-gated backfill/future-refresh lane over already witnessed games. This deliberately does
/// not bump ChessAnalyze.Version or replay its other output, so admitting player context cannot
/// double-count openings, motifs, trajectories, evaluations or global think statistics.
/// </summary>
public sealed class ChessPlayerContextOutcomesDecomposer
    : ComposeDecomposer<ChessPlayerContextOutcomeRecord>, IIngestNoOpExplainer
{
    private long _candidatesStreamed;

    public override Hash128 SourceId => ChessPlayerContextOutcomes.SourceId;
    public override string SourceName => ChessPlayerContextOutcomes.SourceName;
    public override int LayerOrder => 25;
    public override Hash128 TrustClassId => ChessPlayerContextOutcomes.TrustClassId;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "chess/player-context-outcomes";

    public override int EstimatedBytesPerRecord => IngestSourceProfile.ChessAnalyze.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => IngestSourceProfile.ChessAnalyze.EstComposeUnitsPerRecord;

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, SourceId, SourceName, TrustClassId, ct);

    protected override async IAsyncEnumerable<ChessPlayerContextOutcomeRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (ContainmentReader is null
            || ChessWitnessHydrator.TryResolveDataSource(ContainmentReader) is not { } ds)
            throw new InvalidOperationException(
                "ChessPlayerContextOutcomes requires a live Postgres substrate. Record games first.");

        var ws = IngestPipelineDefaults.ResolveWorkingSet(PipelineProfile, options);
        _candidatesStreamed = 0;
        await foreach (var witnessed in ChessWitnessHydrator.StreamUnanalyzedEventsAsync(
                           ds, ContainmentReader!, ws.Batch,
                           ChessPlayerContextOutcomes.MarkerId,
                           includeLive: true, ct))
        {
            _candidatesStreamed++;
            yield return new ChessPlayerContextOutcomeRecord(witnessed);
        }
    }

    protected override void Compose(
        ChessPlayerContextOutcomeRecord record, SubstrateChangeBuilder b)
        => ChessPlayerContextOutcomes.DeriveGame(b, record.Game);

    public override Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default)
    {
        if (ChessWitnessHydrator.TryResolveDataSource(context.Reader) is not { } ds)
            return Task.FromResult<long?>(null);
        return ChessWitnessHydrator.CountTransitionEventsAsync(ds, ct);
    }

    public (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits)
        => _candidatesStreamed == 0
            ? ("already-complete",
               $"ChessPlayerContextOutcomes: every one of {declaredInputUnits} recorded playing(s) "
               + $"already carries the v{ChessPlayerContextOutcomes.Version} context marker.")
            : null;
}

public sealed record ChessPlayerContextOutcomeRecord(ChessWitnessedGame Game) : ITrunkRootRecord
{
    public Hash128 TrunkRootId => ChessPlayerContextOutcomes.MarkerId(Game.PlayingId);
}