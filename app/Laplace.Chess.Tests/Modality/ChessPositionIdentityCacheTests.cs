using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Modality.Chess.Tests;

public sealed class ChessPositionIdentityCacheTests
{
    private static readonly (byte Domain, int Count)[] ScalarDomains =
    [
        (ChessPositionIdentity.SideDomain, 2),
        (ChessPositionIdentity.CastlingDomain, 16),
        (ChessPositionIdentity.EnPassantDomain, 65),
        (ChessPositionIdentity.PieceSquareDomain, 12 * 64),
        (ChessPositionIdentity.MovePieceDomain, 12),
        (ChessPositionIdentity.MoveFromDomain, 64),
        (ChessPositionIdentity.MoveToDomain, 64),
        (ChessPositionIdentity.MoveFlagsDomain, 16),
        (ChessPositionIdentity.MovePromotionDomain, 16),
    ];

    [Fact]
    public void EveryFixedScalarIdMatchesTheUncachedNativeCalculation()
    {
        int checkedIds = 0;
        foreach (var (domain, count) in ScalarDomains)
        {
            for (ushort value = 0; value < count; ++value)
            {
                var atom = ChessPositionIdentity.Atom.Scalar(domain, value);
                Assert.Equal(UncachedAtomId(atom), ChessPositionIdentity.AtomId(atom));
                ++checkedIds;
            }
        }
        Assert.Equal(1023, checkedIds);
    }

    [Fact]
    public void MissesAndArbitraryRuleDigestsKeepTheNativeIdentityLaw()
    {
        foreach (var (domain, count) in ScalarDomains)
        {
            foreach (ushort value in new[] { (ushort)count, ushort.MaxValue })
            {
                var atom = ChessPositionIdentity.Atom.Scalar(domain, value);
                Assert.Equal(UncachedAtomId(atom), ChessPositionIdentity.AtomId(atom));
            }
        }
        foreach (byte domain in new byte[] { 0, ChessPositionIdentity.CastlingRookOverrideDomain,
                     ChessPositionIdentity.AnnotationMissingDomain, 127 })
        {
            var atom = ChessPositionIdentity.Atom.Scalar(domain, 0xabcd);
            Assert.Equal(UncachedAtomId(atom), ChessPositionIdentity.AtomId(atom));
        }
        for (ushort value = 0; value < 128; ++value)
        {
            var digest = UncachedAtomId(ChessPositionIdentity.Atom.Scalar(7, value));
            var atom = ChessPositionIdentity.Atom.Rule(digest);
            Assert.Equal(UncachedAtomId(atom), ChessPositionIdentity.AtomId(atom));
            // HasDigest remains authoritative even for a domain with scalar entries.
            var digestInScalarDomain = new ChessPositionIdentity.Atom(
                ChessPositionIdentity.SideDomain, 1, digest, true);
            Assert.Equal(UncachedAtomId(digestInScalarDomain),
                ChessPositionIdentity.AtomId(digestInScalarDomain));
        }
    }

    [Theory]
    [InlineData(ChessModality.StartFen)]
    [InlineData("nqrkbbrn/pppppppp/8/8/8/8/PPPPPPPP/NQRKBBRN w GCgc - 0 1")]
    [InlineData("4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 1")]
    [InlineData("4k3/8/8/3p4/8/8/8/4K3 w - d6 0 1")]
    [InlineData("rnbqkbnr/pppppppp/8/8/PPPPPPPP/PPPPPPPP/PPPPPPPP/4K3 w kq - 0 1")]
    [InlineData("rnbqkbnr/pppppppp/pppppppp/pppppppp/PPPPPPPP/PPPPPPPP/PPPPPPPP/RNBQKBNR w - - 0 1")]
    public void CompletePositionIdsMatchUncachedNativeComposition(string fen)
    {
        var board = Board.FromFen(fen);
        foreach (var rules in new[] { ChessVariantRules.Standard, ChessVariants.Atomic,
                     ChessVariants.ThreeCheck, ChessVariantRules.Standard with { WinByCheckCount = 101 } })
        {
            var expected = UncachedPositionId(board, rules);
            Assert.Equal(expected, ChessPositionIdentity.PositionId(board, rules));
            Assert.Equal(expected, ChessPositionIdentity.PositionId(board, rules));
        }
    }

    [Fact]
    public void CompleteMoveIdsMatchUncachedNativeComposition()
    {
        foreach (var piece in new[] { Piece.WPawn, Piece.BPawn, Piece.WKing, Piece.BQueen })
        foreach (var promotion in new[] { Piece.Empty, Piece.WQueen, Piece.WRook, Piece.WBishop, Piece.WKnight })
        for (byte flags = 0; flags < 16; ++flags)
        {
            var move = new ChessMove(Board.Sq(4, 1), Board.Sq(4, 3), promotion, (MoveFlags)flags);
            var atoms = new ChessPositionIdentity.Atom[5];
            ChessPositionIdentity.FillMoveAtoms(piece, move, atoms);
            var ids = atoms.Select(atom => UncachedAtomId(atom)).ToArray();
            Assert.Equal(Hash128.Merkle(ChessPositionIdentity.PositionTier, ids),
                ChessPositionIdentity.MoveId(piece, move));
        }
    }

    [Fact]
    public void RareCastlingRookOverridesRemainDistinctAndUseTheNativeMissPath()
    {
        var first = Board.FromFen("4k3/8/8/8/8/8/8/4K1RR w G - 0 1");
        var second = Board.FromFen("4k3/8/8/8/8/8/8/4K1RR w H - 0 1");
        Span<ChessPositionIdentity.Atom> atoms = stackalloc ChessPositionIdentity.Atom[ChessPositionIdentity.MaxAtoms];
        int count = ChessPositionIdentity.FillAtoms(first, ChessVariantRules.Standard, atoms);
        Assert.Contains(atoms[..count].ToArray(),
            atom => atom.Domain == ChessPositionIdentity.CastlingRookOverrideDomain);
        var firstId = ChessPositionIdentity.PositionId(first);
        var secondId = ChessPositionIdentity.PositionId(second);
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(UncachedPositionId(first, ChessVariantRules.Standard), firstId);
        Assert.Equal(UncachedPositionId(second, ChessVariantRules.Standard), secondId);
    }

    [Fact]
    public void ConcurrentLookupsReturnTheSameNativeDerivedIds()
    {
        var atoms = ScalarDomains.SelectMany(domain => Enumerable.Range(0, domain.Count)
            .Select(value => ChessPositionIdentity.Atom.Scalar(domain.Domain, (ushort)value))).ToArray();
        var expected = atoms.Select(atom => UncachedAtomId(atom)).ToArray();
        Parallel.For(0, atoms.Length * 8, i =>
        {
            int index = i % atoms.Length;
            Assert.Equal(expected[index], ChessPositionIdentity.AtomId(atoms[index]));
        });
    }

    // Reference the pre-lookup native calculation. This deliberately retains
    // the actual byte-atom and Merkle entrypoints, not a second hash algorithm.
    internal static Hash128 UncachedAtomId(in ChessPositionIdentity.Atom atom)
    {
        Span<byte> bytes = stackalloc byte[33];
        int count = ChessPositionIdentity.FillAtomBytes(atom, bytes);
        Span<Hash128> children = stackalloc Hash128[count];
        for (int i = 0; i < count; ++i) children[i] = ByteAtoms.Id(bytes[i]);
        return Hash128.Merkle(ChessPositionIdentity.AtomTier, children);
    }

    internal static Hash128 UncachedPositionId(Board board, ChessVariantRules rules)
    {
        Span<ChessPositionIdentity.Atom> atoms = stackalloc ChessPositionIdentity.Atom[ChessPositionIdentity.MaxAtoms];
        int count = ChessPositionIdentity.FillAtoms(board, rules, atoms);
        Span<Hash128> children = stackalloc Hash128[count];
        for (int i = 0; i < count; ++i) children[i] = UncachedAtomId(atoms[i]);
        return Hash128.Merkle(ChessPositionIdentity.PositionTier, children);
    }
}
