using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Modality.Tests;

/// <summary>
/// Mate recognition by GEOMETRY, not by move order. ChessMotifs.DetectNamedTrap matches SAN
/// sequences from move one, so it can only ever recognise known opening traps; a smothered mate
/// arriving via an unfamiliar move order is invisible to it. These positions are given as FENs
/// with no history at all, which is the whole point.
/// </summary>
public class ChessMateTests
{
    // Black king g8, own pawns f7/g7/h7, white rook delivering check along the 8th.
    private const string BackRank = "R5k1/5ppp/8/8/8/8/8/6K1 b - - 0 1";
    // Black king h8, own rook g8 and pawns g7/h7, white knight on f7. The knight checks THROUGH
    // the wall, which is the only way a fully self-blocked king can be mated.
    private const string Smothered = "6rk/5Npp/8/8/8/8/8/6K1 b - - 0 1";
    // Not mate: same shape but the king has luft at h7.
    private const string BackRankWithLuft = "R5k1/5pp1/7p/8/8/8/8/6K1 b - - 0 1";
    // Check but not mate: the rook can be captured.
    private const string CheckNotMate = "6k1/5ppp/8/8/8/8/8/R5K1 w - - 0 1";

    [Theory]
    [InlineData(BackRank)]
    [InlineData(Smothered)]
    public void IsMate_RecognisesMate(string fen)
        => Assert.True(ChessMate.IsMate(Board.FromFen(fen)), fen);

    [Theory]
    [InlineData(BackRankWithLuft)]
    [InlineData(CheckNotMate)]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    public void IsMate_RejectsNonMate(string fen)
        => Assert.False(ChessMate.IsMate(Board.FromFen(fen)), fen);

    [Fact]
    public void Describe_ReturnsNull_WhenNotMate()
        => Assert.Null(ChessMate.Describe(Board.FromFen(BackRankWithLuft)));

    [Fact]
    public void BackRank_IsNamedAndGeometricallyDescribed()
    {
        var p = ChessMate.Describe(Board.FromFen(BackRank));
        Assert.NotNull(p);
        Assert.Equal("back-rank", p!.Value.Name);
        Assert.Equal(Piece.WRook, p.Value.PrimaryChecker);
        Assert.False(p.Value.CheckerAdjacent);
        // The three squares in front are the king's own pawns.
        Assert.NotEqual(0, p.Value.BlockedByOwn);
    }

    [Fact]
    public void Smothered_IsNamed_AndHasNoEnemyCoveredSquares()
    {
        var p = ChessMate.Describe(Board.FromFen(Smothered));
        Assert.NotNull(p);
        Assert.Equal("smothered", p!.Value.Name);
        Assert.Equal(Piece.WKnight, p.Value.PrimaryChecker);
        // Definitional: every escape is blocked by the king's OWN pieces, none by the enemy.
        Assert.Equal(0, p.Value.CoveredByEnemy);
        Assert.NotEqual(0, p.Value.BlockedByOwn);
    }

    /// <summary>
    /// In a real mate every one of the eight king-zone directions must be off-board, blocked by
    /// an own piece, or covered by the enemy. An empty safe square would BE a legal king move,
    /// so the three masks must partition all eight bits exactly — no gaps, no overlaps.
    /// </summary>
    [Theory]
    [InlineData(BackRank)]
    [InlineData(Smothered)]
    public void ZoneMasks_PartitionAllEightDirections(string fen)
    {
        var p = ChessMate.Describe(Board.FromFen(fen));
        Assert.NotNull(p);
        int blocked = p!.Value.BlockedByOwn, covered = p.Value.CoveredByEnemy, off = p.Value.OffBoard;
        Assert.Equal(0xFF, blocked | covered | off);
        Assert.Equal(0, blocked & covered);
        Assert.Equal(0, blocked & off);
        Assert.Equal(0, covered & off);
    }

    /// <summary>
    /// The table key must be position-independent: the same geometry reached from a different
    /// board must produce the same key. Here the back-rank mate is shifted by adding an
    /// irrelevant white pawn far away — the mate geometry is untouched.
    /// </summary>
    [Fact]
    public void Key_IsInvariantToIrrelevantMaterial()
    {
        var a = ChessMate.Describe(Board.FromFen(BackRank));
        var b = ChessMate.Describe(Board.FromFen("R5k1/5ppp/8/8/3P4/8/8/6K1 b - - 0 1"));
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Value.Key, b!.Value.Key);
    }

    [Fact]
    public void Key_DiffersBetweenDistinctPatterns()
    {
        var back = ChessMate.Describe(Board.FromFen(BackRank));
        var smoth = ChessMate.Describe(Board.FromFen(Smothered));
        Assert.NotNull(back);
        Assert.NotNull(smoth);
        Assert.NotEqual(back!.Value.Key, smoth!.Value.Key);
    }
}
