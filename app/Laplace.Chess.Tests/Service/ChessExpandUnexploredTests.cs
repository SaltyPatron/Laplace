using Laplace.Engine.Core;
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
        var lines = change.Physicalities.Where(p => p.NConstituents == 2).ToList();
        Assert.Equal(20, lines.Count);
        Assert.DoesNotContain(change.Attestations,
            a => a.TypeId == ChessVocabulary.MoveType);

        Hash128 root = ChessCompose.PositionId(m.StateKey(m.Initial()));
        var successors = new HashSet<Hash128>();
        foreach (var line in lines)
        {
            var ids = Trajectory.Constituents(line.TrajectoryXyzm!);
            Assert.Equal(root, ids[0]);
            successors.Add(ids[1]);
        }
        Assert.Equal(20, successors.Count);
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
