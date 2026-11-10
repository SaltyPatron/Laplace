using Laplace.Chess.Service;
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
    private const string EngineStart = "98522a8a1f8931ba76d1992a94e7dbcd";

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

    // What the Engine prints for a tagless, unfinished game: its record is its line, and it attests the one claim
    // [record, line], so the record it holds is [record, [record, line]].
    private static string EngineHeldHex(Hash128 line)
        => Hex(ChessEngineIdentity.Compose(new[] { line, ChessEngineIdentity.Compose(new[] { line, line }) }));

    [Fact]
    public void StartPosition_IsTheEnginesId()
    {
        var start = Board.FromFen(ChessModality.StartFen);
        Assert.Equal(EngineStart, EngineHeldHex(ChessEngineIdentity.PositionId(start)));
    }

    [Fact]
    public void TheMonorepoLegacyId_DiffersFromTheEnginesForTheSameBoard()
    {
        // The id the domain-tagged atoms compose: this is the divergence the Engine id replaces.
        var start = Board.FromFen(ChessModality.StartFen);
        lock (ChessCompose.Gate)
            Assert.NotEqual(EngineStart, EngineHeldHex(ChessPositionIdentity.PositionId(start)));
    }

    [Theory]
    [InlineData("e4", "bab7041d3051e91c3f361ec0124aff8e")]
    [InlineData("e4 e5 Nf3 Nc6", "9a2ace38a08cddfc0ffcb944e6efcf44")]
    [InlineData("Nf3 Nc6 e4 e5", "48858d05244b68465d2c0f1e7bac323f")]
    // e5 beside d5: the en passant square d6 is capturable, so it is in the position.
    [InlineData("e4 a6 e5 d5", "dead9d5c40c1fa81fed0b5f6f5dd254c")]
    // White has castled, Black has not: the masks are 0 and 137.
    [InlineData("e4 e5 Nf3 Nc6 Bc4 Bc5 O-O Nf6", "df5e2f9cf8fb64057b0d3328e1a90d84")]
    [InlineData("e4 e5 Nf3 Nc6 Bc4 Bc5 O-O Nf6 Re1 O-O", "da3caadfc3f2c4dc833aa46b44419add")]
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
    public void ShortCastling_IsMovedAsTheKingOntoItsOwnRook()
    {
        var board = Board.FromFen("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1");
        var castle = MoveGen.Legal(board).Single(m => m.IsKingSideCastle);
        // [K, e1, h1]
        var expected = ChessEngineIdentity.Compose(new[]
        {
            ChessEngineIdentity.Codepoint('K'),
            ChessEngineIdentity.Square(4, 0),
            ChessEngineIdentity.Square(7, 0),
        });
        Assert.Equal(expected, ChessEngineIdentity.MoveId(board, castle));
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
    public void PromotionMove_CarriesThePromotedPieceInTheMoversCase()
    {
        var board = Board.FromFen("4k3/P7/8/8/8/8/8/4K3 w - - 0 1");
        var queen = MoveGen.Legal(board).Single(m => m.IsPromotion && Board.TypeOf(m.Promotion) == Piece.WQueen);
        var expected = ChessEngineIdentity.Compose(new[]
        {
            ChessEngineIdentity.Codepoint('P'),
            ChessEngineIdentity.Square(0, 6),
            ChessEngineIdentity.Square(0, 7),
            ChessEngineIdentity.Codepoint('Q'),
        });
        Assert.Equal(expected, ChessEngineIdentity.MoveId(board, queen));
    }

    [Fact]
    public void RuleSetsTheEngineDoesNotRead_HaveNoEngineIdentity()
    {
        var board = Board.FromFen(ChessModality.StartFen, ChessVariants.Crazyhouse);
        Assert.False(ChessEngineIdentity.TryPositionId(board, out _));
    }
}
