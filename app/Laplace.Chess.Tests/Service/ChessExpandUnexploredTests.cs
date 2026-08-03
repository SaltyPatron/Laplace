using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessExpandUnexploredTests
{
    [Fact]
    public void AppendUnexploredOnePly_FromStart_EmitsLinesForLegalMoves()
    {
        var m = new ChessModality();
        var b = new SubstrateChangeBuilder(ChessExpandUnexplored.SourceId, "test/expand");
        int n = ChessExpandUnexplored.AppendUnexploredOnePly(b, m.Initial(), m);
        var change = b.SetInputUnitsConsumed(1).Build();

        Assert.Equal(20, n); // standard start: 20 legal moves
        Assert.Equal(20, change.Entities.Count(e => e.TypeId == ChessVocabulary.GameType));
        Assert.Equal(20, change.Physicalities.Count(p => p.NConstituents == 2));
        Assert.All(
            change.Attestations.Where(a => a.TypeId == ChessVocabulary.MoveType),
            a => Assert.Equal(ChessExpandUnexplored.SourceId, a.SourceId));
    }

    [Fact]
    public void AppendUnexploredOnePly_SkipsAlreadyExploredTargets()
    {
        var m = new ChessModality();
        var from = m.Initial();
        var first = m.LegalActions(from)[0];
        var next = m.Apply(from, first);
        var explored = new HashSet<Engine.Core.Hash128>();
        lock (ChessCompose.Gate)
            explored.Add(ChessCompose.PositionId(m.StateKey(next)));

        var b = new SubstrateChangeBuilder(ChessExpandUnexplored.SourceId, "test/expand");
        int n = ChessExpandUnexplored.AppendUnexploredOnePly(b, from, m, explored);
        Assert.Equal(19, n);
    }
}
