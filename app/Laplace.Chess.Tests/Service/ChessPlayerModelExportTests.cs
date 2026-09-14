using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessPlayerModelExportTests
{
    [Fact]
    public void MemberComposition_IsOrderInsensitive_AndBoundaryVersioned()
    {
        var carlsen = ChessVocabulary.PlayerId("Magnus Carlsen");
        var karpov = ChessVocabulary.PlayerId("Anatoly Karpov");

        var a = ChessPlayerModelExport.Create([carlsen, karpov], "epoch-100");
        var b = ChessPlayerModelExport.Create([karpov, carlsen, carlsen], "epoch-100");
        var later = ChessPlayerModelExport.Create([karpov, carlsen], "epoch-101");

        Assert.Equal(a.MemberSetId, b.MemberSetId);
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.MemberSetId, later.MemberSetId);
        Assert.NotEqual(a.Id, later.Id);
        Assert.Equal(2, a.Members.Count);
    }

    [Fact]
    public void JsonRoundTrip_RecomputesAndValidatesIdentity()
    {
        var export = ChessPlayerModelExport.ForNames(
            ["Magnus Carlsen", "Anatoly Karpov"],
            "substrate-generation-abc");

        var restored = ChessPlayerModelExport.FromJson(export.ToJson());

        Assert.Equal(export.Version, restored.Version);
        Assert.Equal(export.Id, restored.Id);
        Assert.Equal(export.MemberSetId, restored.MemberSetId);
        Assert.Equal(export.EvidenceBoundary, restored.EvidenceBoundary);
        Assert.Equal(export.Members.ToArray(), restored.Members.ToArray());
    }

    [Fact]
    public void CompositeBias_PreservesConflict_AndRewardsCorroboration()
    {
        var carlsen = ChessVocabulary.PlayerId("Magnus Carlsen");
        var karpov = ChessVocabulary.PlayerId("Anatoly Karpov");
        var export = ChessPlayerModelExport.Create([carlsen, karpov], "test-boundary");
        var board = Board.FromFen(ChessModality.StartFen);
        var legal = MoveGen.Legal(board);
        var e4 = Assert.Single(legal, static m => m.ToUci() == "e2e4");
        var d4 = Assert.Single(legal, static m => m.ToUci() == "d2d4");
        Hash128 e4Next = NextId(board, e4);
        Hash128 d4Next = NextId(board, d4);

        var bias = new ChessCompositePlayerBias(
            export,
            (position, player, white, limit) =>
            {
                Assert.Equal(ChessCompose.PositionId(board), position);
                Assert.True(white);
                Assert.Equal(legal.Count, limit);
                if (player == carlsen)
                    return
                    [
                        new ChessPlayerMoveEvidence(e4Next, 100, 1.0),
                        new ChessPlayerMoveEvidence(d4Next, 100, 0.75),
                    ];
                if (player == karpov)
                    return
                    [
                        new ChessPlayerMoveEvidence(e4Next, 100, 0.0),
                        new ChessPlayerMoveEvidence(d4Next, 100, 0.75),
                    ];
                return [];
            });

        var bonuses = bias.Bonus(board, legal);
        int e4Index = legal.FindIndex(m => m == e4);
        int d4Index = legal.FindIndex(m => m == d4);

        // Equal and opposite constituent evidence survives composition as disagreement.
        Assert.Equal(0, bonuses[e4Index]);
        // Corroborating evidence from both members moves the shared composite policy.
        Assert.True(bonuses[d4Index] > 0);

        var receipt = bias.Receipt();
        Assert.Equal(2, receipt.Members);
        Assert.Equal(1, receipt.RootReads);
        Assert.Equal(2, receipt.MemberReads);
        Assert.Equal(2, receipt.MembersWithEvidence);
        Assert.True(receipt.MovesInfluenced > 0);
    }

    [Fact]
    public void CompositeBias_NeverExceedsRootAuthorityEnvelope()
    {
        var player = ChessVocabulary.PlayerId("Bobby Fischer");
        var export = ChessPlayerModelExport.Create([player], "test-boundary");
        var board = Board.FromFen(ChessModality.StartFen);
        var legal = MoveGen.Legal(board);
        Hash128 target = NextId(board, legal[0]);

        var bias = new ChessCompositePlayerBias(
            export,
            (_, _, _, _) => [new ChessPlayerMoveEvidence(target, 1_000_000, 1.0)],
            capCp: 150);
        var bonuses = bias.Bonus(board, legal);

        Assert.All(bonuses, static cp => Assert.InRange(cp, -150, 150));
        Assert.True(bonuses[0] > 0);
    }

    private static Hash128 NextId(Board board, ChessMove move)
    {
        var next = board.Clone();
        MoveApply.Make(next, move);
        return ChessCompose.PositionId(next);
    }
}
