using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Modality.Chess.Tests;

/// <summary>
/// The monorepo computes the Engine's chess ids. The expected values are not computed here: each is the id the
/// Laplace-Engine <c>laplace ingest --claims-tsv -s chess-com</c> printed for a PGN game of no tags (a record
/// of a tagless game is its line [P0, M0, P1, ...], and of a game of no moves its start position), so one
/// equal id pins every position and move on the line.
/// </summary>
public sealed class ChessEngineIdentityTests
{
    // A game of no moves and no tags holds its start position (a one-child composition is its child).
    private const string EngineStart = "6546b7964667fe459eead69e9ef46c87";

    private static string Hex(Hash128 id) => Convert.ToHexString(id.ToBytes()).ToLowerInvariant();

    private static Board Play(Board start, string movetext, List<ChessMove> played)
    {
        var board = start.Clone();
        foreach (var san in movetext.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var legal = MoveGen.Legal(board);
            var move = San.Resolve(board, legal, san);
            Assert.NotNull(move);
            played.Add(move!.Value);
            MoveApply.Make(board, move.Value);
        }
        return board;
    }

    private static Hash128 Line(string movetext)
    {
        var start = Board.FromFen(ChessModality.StartFen);
        var moves = new List<ChessMove>();
        Play(start, movetext, moves);
        return ChessEngineIdentity.LineId(ChessEngineIdentity.Trajectory(start, moves));
    }

    private static string LineHex(string movetext) => Hex(Line(movetext));

    // What the Engine prints for a tagless, unfinished game: its record is its line. A record is never the subject
    // of a claim and an unfinished game claims no move, so the record it holds is the line itself.
    private static string EngineHeldHex(Hash128 line)
        => Hex(line);

    [Fact]
    public void StartPosition_IsTheEnginesId()
    {
        var start = Board.FromFen(ChessModality.StartFen);
        Assert.Equal(EngineStart, EngineHeldHex(ChessEngineIdentity.PositionId(start)));
    }

    [Theory]
    [InlineData("e4", "aa9a24415d37db0497aa2bf1f8e99f8e")]
    [InlineData("e4 e5 Nf3 Nc6", "193e5b5676f2c6fbec93eb4070dafba3")]
    [InlineData("Nf3 Nc6 e4 e5", "6271dcd6936759e26540d38a5cb99e7d")]
    // e5 beside d5: the en passant square d6 is capturable, so it is in the position.
    [InlineData("e4 a6 e5 d5", "5fce7d0b28094d3c4c9e41d2c6cdbd90")]
    // White has castled, Black has not: the masks are 0 and 137.
    [InlineData("e4 e5 Nf3 Nc6 Bc4 Bc5 O-O Nf6", "a94bc1ab79b97c852a250fa0166b0cec")]
    [InlineData("e4 e5 Nf3 Nc6 Bc4 Bc5 O-O Nf6 Re1 O-O", "cdbe2059c4113476f296a37298bc1d2a")]
    public void Lines_AreTheEnginesRecords(string movetext, string engineLineId)
        => Assert.Equal(engineLineId, EngineHeldHex(Line(movetext)));

    [Fact]
    public void Transposition_ReachesOnePositionId_AndTheEngineLinesDiffer()
    {
        var start = Board.FromFen(ChessModality.StartFen);
        var a = Play(start, "e4 e5 Nf3 Nc6", new List<ChessMove>());
        var b = Play(start, "Nf3 Nc6 e4 e5", new List<ChessMove>());
        Assert.Equal(ChessEngineIdentity.PositionId(a), ChessEngineIdentity.PositionId(b));
        // Two different trajectories (the Engine's two record ids above) over one shared final position.
        Assert.NotEqual(LineHex("e4 e5 Nf3 Nc6"), LineHex("Nf3 Nc6 e4 e5"));
    }

    [Fact]
    public void ShortCastling_TransitionIsPureBeforeAfterPositionPair()
    {
        var board = Board.FromFen("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1");
        var castle = MoveGen.Legal(board).Single(m => m.IsKingSideCastle);
        var next = board.Clone();
        MoveApply.Make(next, castle);
        var expected = ChessEngineIdentity.Compose(new[]
        {
            ChessEngineIdentity.PositionId(board),
            ChessEngineIdentity.PositionId(next),
        });
        Assert.Equal(expected, ChessEngineIdentity.TransitionId(ChessEngineIdentity.PositionId(board), ChessEngineIdentity.PositionId(next)));
    }

    [Fact]
    public void CastlingMasks_AreTheEnginesBits()
    {
        var start = Board.FromFen(ChessModality.StartFen);
        Assert.Equal(137u, ChessEngineIdentity.CastleMask(start, white: true));   // a, e, h
        Assert.Equal(137u, ChessEngineIdentity.CastleMask(start, white: false));
        var c960 = Board.FromFen("nqrkbbrn/pppppppp/8/8/8/8/PPPPPPPP/NQRKBBRN w GCgc - 0 1");
        Assert.Equal(50u, ChessEngineIdentity.CastleMask(c960, white: true));     // c, d, g
        var none = Board.FromFen("4k3/8/8/8/8/8/8/4K3 w - - 0 1");
        Assert.Equal(0u, ChessEngineIdentity.CastleMask(none, white: true));
    }

    [Fact]
    public void UnderpromotionTransitions_ReachDistinctAfterPositions()
    {
        var board = Board.FromFen("4k3/P7/8/8/8/8/8/4K3 w - - 0 1");
        var queen = MoveGen.Legal(board).Single(m => m.IsPromotion && Board.TypeOf(m.Promotion) == Piece.WQueen);
        var knight = MoveGen.Legal(board).Single(m => m.IsPromotion && Board.TypeOf(m.Promotion) == Piece.WKnight);
        var nextQ = board.Clone(); MoveApply.Make(nextQ, queen);
        var nextN = board.Clone(); MoveApply.Make(nextN, knight);
        Assert.NotEqual(
            ChessEngineIdentity.TransitionId(ChessEngineIdentity.PositionId(board), ChessEngineIdentity.PositionId(nextQ)),
            ChessEngineIdentity.TransitionId(ChessEngineIdentity.PositionId(board), ChessEngineIdentity.PositionId(nextN)));
    }

    [Fact]
    public void CrazyhousePosition_ComposesHandInQrbnpOrderAndPromotedSquares()
    {
        var fen = "r4qk1/ppp1b2p/3pN1pP/3P1b2/8/3P4/PP3PPP/R1Bb1RK1[RPqnnnp] b - - 1 20";
        var board = Board.FromFen(fen, ChessVariants.Crazyhouse);
        Assert.True(ChessEngineIdentity.TryPositionId(board, out var actual));

        var withoutHand = Board.FromFen("r4qk1/ppp1b2p/3pN1pP/3P1b2/8/3P4/PP3PPP/R1Bb1RK1 b - - 1 20");
        Assert.NotEqual(ChessEngineIdentity.PositionId(withoutHand), actual);

        var promotedBoard = Board.FromFen("r1b2rk1/pp3p2/5ppb/4p3/2n5/2PBP1P1/PP1rN3/1K5q~[QQBNPrnppp] w - - 0 33", ChessVariants.Crazyhouse);
        var unpromotedBoard = Board.FromFen("r1b2rk1/pp3p2/5ppb/4p3/2n5/2PBP1P1/PP1rN3/1K5q[QQBNPrnppp] w - - 0 33", ChessVariants.Crazyhouse);
        Assert.NotEqual(ChessEngineIdentity.PositionId(unpromotedBoard), ChessEngineIdentity.PositionId(promotedBoard));
    }

    [Fact]
    public void CrazyhouseDropTransition_DepletesHandAndPlacesPiece()
    {
        var board = Board.FromFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR[P] w KQkq - 0 1", ChessVariants.Crazyhouse);
        var drop = MoveGen.Legal(board).First(m => m.IsDrop && Board.TypeOf(m.Promotion) == Piece.WPawn && m.To == Board.Sq(4, 3));
        var next = board.Clone();
        MoveApply.Make(next, drop);
        var expected = ChessEngineIdentity.Compose(new[]
        {
            ChessEngineIdentity.PositionId(board),
            ChessEngineIdentity.PositionId(next),
        });
        Assert.Equal(expected, ChessEngineIdentity.TransitionId(ChessEngineIdentity.PositionId(board), ChessEngineIdentity.PositionId(next)));
    }

    [Fact]
    public void ThreeCheckPosition_ComposesChecksGiven()
    {
        var a = Board.FromFen("r5k1/ppp2rpp/8/4p1B1/6n1/2Nq4/PP1Q2P1/nK5R w - - 2+0 0 19", ChessVariants.ThreeCheck);
        var b = Board.FromFen("r5k1/ppp2rpp/8/4p1B1/6n1/2Nq4/PP1Q2P1/nK5R w - - +1+3 0 19", ChessVariants.ThreeCheck);
        var c = Board.FromFen("r5k1/ppp2rpp/8/4p1B1/6n1/2Nq4/PP1Q2P1/nK5R w - - 3+3 0 19", ChessVariants.ThreeCheck);
        Assert.Equal(ChessEngineIdentity.PositionId(a), ChessEngineIdentity.PositionId(b));
        Assert.NotEqual(ChessEngineIdentity.PositionId(a), ChessEngineIdentity.PositionId(c));
    }

    [Fact]
    public void RuleSetsBeyondEightByEight_HaveNoEngineIdentity()
    {
        var board = Board.FromFen(ChessModality.StartFen, ChessVariants.Capablanca);
        Assert.False(ChessEngineIdentity.TryPositionId(board, out _));
    }
}
