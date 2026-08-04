using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Modality.Tests;

/// <summary>
/// Equivalence gate for the precomputed attack tables.
///
/// The tables exist to replace runtime ray-walking, so the only thing that matters is that they
/// agree with the walk they replace — BIT FOR BIT, on every square, under every relevant
/// occupancy. This is checked against an independent walk written here rather than against
/// MoveGen, so a shared bug in one implementation cannot hide behind the other.
///
/// Sliding pieces are checked over EVERY subset of each square's relevant-occupancy mask
/// (carry-rippler enumeration), which is exhaustive for the tables' whole index domain:
/// 5,248 bishop entries + 102,400 rook entries. Not a sample — the complete table.
/// </summary>
public class ChessAttacksTests
{
    private static readonly (int df, int dr)[] RookDirs = { (0, 1), (0, -1), (1, 0), (-1, 0) };
    private static readonly (int df, int dr)[] BishopDirs = { (1, 1), (1, -1), (-1, 1), (-1, -1) };

    /// <summary>Independent reference walk: ray outward, include the first blocker, stop.</summary>
    private static ulong RefSlide(int sq, ulong occ, (int df, int dr)[] dirs)
    {
        ulong r = 0;
        int f0 = sq & 7, r0 = sq >> 3;
        foreach (var (df, dr) in dirs)
        {
            int f = f0 + df, k = r0 + dr;
            while ((uint)f < 8 && (uint)k < 8)
            {
                int t = (k << 3) | f;
                r |= 1UL << t;
                if ((occ & (1UL << t)) != 0) break;
                f += df; k += dr;
            }
        }
        return r;
    }

    private static ulong RelevantMask(int sq, (int df, int dr)[] dirs)
    {
        ulong r = 0;
        int f0 = sq & 7, r0 = sq >> 3;
        foreach (var (df, dr) in dirs)
        {
            int f = f0 + df, k = r0 + dr;
            while ((uint)(f + df) < 8 && (uint)(k + dr) < 8)
            {
                r |= 1UL << ((k << 3) | f);
                f += df; k += dr;
            }
        }
        return r;
    }

    [Fact]
    public void Rook_MatchesRayWalk_OverEveryOccupancySubset()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            ulong mask = RelevantMask(sq, RookDirs);
            ulong sub = 0;
            do
            {
                Assert.Equal(RefSlide(sq, sub, RookDirs), ChessAttacks.Rook(sq, sub));
                sub = (sub - mask) & mask;
            } while (sub != 0);
        }
    }

    [Fact]
    public void Bishop_MatchesRayWalk_OverEveryOccupancySubset()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            ulong mask = RelevantMask(sq, BishopDirs);
            ulong sub = 0;
            do
            {
                Assert.Equal(RefSlide(sq, sub, BishopDirs), ChessAttacks.Bishop(sq, sub));
                sub = (sub - mask) & mask;
            } while (sub != 0);
        }
    }

    /// <summary>
    /// Occupancy OUTSIDE the relevant mask must not change the answer — that is the entire
    /// justification for excluding edge squares from the index, and getting it wrong is how a
    /// slider table silently returns a stale attack set in the middlegame.
    /// </summary>
    [Fact]
    public void Sliders_IgnoreOccupancyOutsideTheRelevantMask()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            ulong rmask = RelevantMask(sq, RookDirs);
            ulong bmask = RelevantMask(sq, BishopDirs);
            ulong outsideR = ~rmask & ~(1UL << sq);
            ulong outsideB = ~bmask & ~(1UL << sq);
            Assert.Equal(ChessAttacks.Rook(sq, 0), ChessAttacks.Rook(sq, outsideR));
            Assert.Equal(ChessAttacks.Bishop(sq, 0), ChessAttacks.Bishop(sq, outsideB));
        }
    }

    [Fact]
    public void Queen_IsTheUnionOfRookAndBishop()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            ulong occ = 0x0000_1818_1800_0000UL;   // an arbitrary central blob
            Assert.Equal(ChessAttacks.Rook(sq, occ) | ChessAttacks.Bishop(sq, occ),
                         ChessAttacks.Queen(sq, occ));
        }
    }

    [Theory]
    // Corner, edge and centre — the three cases leaper tables get wrong by wrapping files.
    [InlineData(0, 2)]    // a1 knight: b3, c2
    [InlineData(27, 8)]   // d4 knight: full 8
    [InlineData(7, 2)]    // h1 knight: f2, g3
    public void Knight_HasExpectedDegree(int sq, int expected)
        => Assert.Equal(expected, System.Numerics.BitOperations.PopCount(ChessAttacks.Knight(sq)));

    [Theory]
    [InlineData(0, 3)]    // a1 king
    [InlineData(27, 8)]   // d4 king
    [InlineData(63, 3)]   // h8 king
    public void King_HasExpectedDegree(int sq, int expected)
        => Assert.Equal(expected, System.Numerics.BitOperations.PopCount(ChessAttacks.King(sq)));

    [Fact]
    public void Knight_AndKing_NeverWrapAcrossFiles()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            int file = sq & 7;
            foreach (ulong tbl in new[] { ChessAttacks.Knight(sq), ChessAttacks.King(sq) })
            {
                for (int t = 0; t < 64; t++)
                {
                    if ((tbl & (1UL << t)) == 0) continue;
                    Assert.True(System.Math.Abs((t & 7) - file) <= 2,
                        $"square {sq} attacks {t}: file distance > 2 means the mask wrapped");
                }
            }
        }
    }

    [Fact]
    public void Pawn_AttacksDiagonallyForward_AndNeverWraps()
    {
        // e4 (bit 28) white attacks d5, f5; black attacks d3, f3.
        Assert.Equal((1UL << 35) | (1UL << 37), ChessAttacks.Pawn(28, white: true));
        Assert.Equal((1UL << 19) | (1UL << 21), ChessAttacks.Pawn(28, white: false));
        // a-file and h-file pawns attack exactly one square, never wrapping to the far side.
        Assert.Equal(1, System.Numerics.BitOperations.PopCount(ChessAttacks.Pawn(24, white: true)));
        Assert.Equal(1, System.Numerics.BitOperations.PopCount(ChessAttacks.Pawn(31, white: true)));
    }

    [Fact]
    public void Between_IsExclusive_AndEmptyForUnalignedSquares()
    {
        Assert.Equal(0UL, ChessAttacks.Between(0, 0));
        Assert.Equal(0UL, ChessAttacks.Between(0, 1));           // adjacent: nothing between
        Assert.Equal((1UL << 1) | (1UL << 2), ChessAttacks.Between(0, 3));   // a1..d1
        Assert.Equal(0UL, ChessAttacks.Between(0, 10));          // a1/c2: not aligned
        // Symmetric both ways round.
        for (int a = 0; a < 64; a++)
            for (int b = 0; b < 64; b++)
                Assert.Equal(ChessAttacks.Between(a, b), ChessAttacks.Between(b, a));
    }

    [Fact]
    public void Line_ContainsBothEndpoints_WhenAligned()
    {
        for (int a = 0; a < 64; a++)
        {
            for (int b = 0; b < 64; b++)
            {
                if (a == b) continue;
                ulong line = ChessAttacks.Line(a, b);
                if (line == 0) continue;               // not aligned — nothing to assert
                Assert.NotEqual(0UL, line & (1UL << a));
                Assert.NotEqual(0UL, line & (1UL << b));
            }
        }
    }

    /// <summary>
    /// A queen on an empty board reaches exactly the squares sharing its rank, file or diagonals.
    /// Independent of the table's internals, so it catches a systematically shifted index.
    /// </summary>
    [Fact]
    public void Queen_OnEmptyBoard_ReachesEveryAlignedSquare()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            ulong got = ChessAttacks.Queen(sq, 0);
            int f0 = sq & 7, r0 = sq >> 3;
            for (int t = 0; t < 64; t++)
            {
                if (t == sq) continue;
                int f = t & 7, r = t >> 3;
                bool aligned = f == f0 || r == r0 || System.Math.Abs(f - f0) == System.Math.Abs(r - r0);
                Assert.Equal(aligned, (got & (1UL << t)) != 0);
            }
        }
    }
}
