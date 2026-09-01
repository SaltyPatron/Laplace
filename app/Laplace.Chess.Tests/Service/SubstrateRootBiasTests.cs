using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class SubstrateRootBiasTests
{
    [Fact]
    public void ExactWitnessedTransitionSteersTheConventionalRoot()
    {
        var fixture = Start("e2e4");
        var bias = Bias((ids, type) => type == ChessVocabulary.MoveType
            ? Present(ids, fixture.TransitionEdge, +10)
            : Empty());

        var bonus = bias.Bonus(fixture.Board, fixture.Moves);

        Assert.Equal(80, bonus[fixture.Index]);
        Assert.Equal(1, bias.RootsWithExactEvidence);
        Assert.Equal(1, bias.ExactTransitionSignals);
        Assert.Equal(0, bias.MovePhysicalitySignals);
    }

    [Fact]
    public void TypedPieceFromToEvidenceParticipatesInTheSameRootDecision()
    {
        var fixture = Start("g1f3");
        var bias = Bias((ids, type) => type == ChessVocabulary.OutcomeType
            ? Present(ids, fixture.MoveOutcomeEdge, +12)
            : Empty());

        var bonus = bias.Bonus(fixture.Board, fixture.Moves);

        Assert.Equal(96, bonus[fixture.Index]);
        Assert.Equal(0, bias.ExactTransitionSignals);
        Assert.Equal(1, bias.RootsWithMoveEvidence);
        Assert.Equal(1, bias.MovePhysicalitySignals);
    }

    [Fact]
    public void ExactTransitionAndMovePhysicalityAreFusedNotDoubleCounted()
    {
        var fixture = Start("d2d4");
        var bias = Bias((ids, type) => type switch
        {
            var t when t == ChessVocabulary.MoveType =>
                Present(ids, fixture.TransitionEdge, +10),
            var t when t == ChessVocabulary.OutcomeType =>
                Present(ids, fixture.MoveOutcomeEdge, -10),
            _ => Empty(),
        });

        var bonus = bias.Bonus(fixture.Board, fixture.Moves);

        Assert.Equal(0, bonus[fixture.Index]);
        Assert.Equal(1, bias.ExactTransitionSignals);
        Assert.Equal(1, bias.MovePhysicalitySignals);
    }

    [Fact]
    public void ReusesFrontierUntilRelevantOnlineEvidenceChanges()
    {
        var fixture = Start("e2e4");
        int reads = 0;
        long version = 0;
        var bias = new SubstrateRootBias(
            (firstIds, firstType, secondIds, secondType) =>
            {
                reads++;
                return (Present(firstIds, fixture.TransitionEdge, +10), Empty());
            },
            cpPerPoint: 8d, capCp: 150, shrinkK0: 0d,
            version: (_, _) => version);

        _ = bias.Bonus(fixture.Board, fixture.Moves);
        _ = bias.Bonus(fixture.Board, fixture.Moves);
        Assert.Equal(1, reads);
        Assert.Equal(1, bias.BackendReads);

        version++;
        _ = bias.Bonus(fixture.Board, fixture.Moves);
        Assert.Equal(2, reads);
        Assert.Equal(2, bias.BackendReads);
    }

    private static SubstrateRootBias Bias(
        Func<IReadOnlyCollection<Hash128>, Hash128,
            IReadOnlyDictionary<Hash128, NpgsqlConsensusByIds.Row>> read)
        => new((firstIds, firstType, secondIds, secondType) =>
                (read(firstIds, firstType), read(secondIds, secondType)),
            cpPerPoint: 8d, capCp: 150, shrinkK0: 0d);

    private static IReadOnlyDictionary<Hash128, NpgsqlConsensusByIds.Row> Present(
        IReadOnlyCollection<Hash128> requested, Hash128 id, double points)
    {
        Assert.Contains(id, requested);
        return new Dictionary<Hash128, NpgsqlConsensusByIds.Row>
        {
            [id] = new(
                GlickoPriors.NeutralMu + points * 1e9,
                Rd: 0d,
                Witnesses: 100d),
        };
    }

    private static IReadOnlyDictionary<Hash128, NpgsqlConsensusByIds.Row> Empty()
        => new Dictionary<Hash128, NpgsqlConsensusByIds.Row>();

    private static Fixture Start(string uci)
    {
        var modality = new ChessModality();
        var state = modality.Initial();
        var moves = modality.LegalActions(state);
        int index = Enumerable.Range(0, moves.Count)
            .Single(i => moves[i].ToUci() == uci);
        ChessMove selected = moves[index];
        Hash128 rootId, moveId, nextId;
        lock (ChessCompose.Gate)
        {
            rootId = ChessCompose.PositionId(state.Board);
            moveId = ChessCompose.MoveId(state.Board.Squares[selected.From], selected);
            nextId = ChessCompose.PositionId(modality.Apply(state, selected).Board);
        }
        return new Fixture(
            state.Board, moves, index,
            ConsensusKeys.EdgeId(rootId, ChessVocabulary.MoveType, nextId),
            ConsensusKeys.EdgeId(
                moveId, ChessVocabulary.OutcomeType, ChessVocabulary.OutcomeObject));
    }

    private sealed record Fixture(
        Board Board, IReadOnlyList<ChessMove> Moves, int Index,
        Hash128 TransitionEdge, Hash128 MoveOutcomeEdge);
}
