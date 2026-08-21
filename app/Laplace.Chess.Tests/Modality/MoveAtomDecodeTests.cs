using System;
using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Modality.Chess.Tests;

/// <summary>
/// A stored move id is decodable without a board. ChessReplay resolves one by generating
/// every legal action and hashing each (~35 per ply) because it treats the id as opaque;
/// a fold over games that already happened does not need to search for them.
/// </summary>
public sealed class MoveAtomDecodeTests
{
    /// <summary>
    /// The id of 1.e4 as it is actually stored in this substrate, read out of a real game's
    /// move trajectory (game aa651769..., ply 1). Pinning it here means the decode is checked
    /// against the corpus, not against a fixture this test invented.
    /// </summary>
    private const string StoredE4 = "fe6ea447874e7e473a3cda37b881d579";

    [Fact]
    public void ComposedMoveId_MatchesTheStoredCorpusId()
    {
        var move = new ChessMove(Board.Sq(4, 1), Board.Sq(4, 3), Piece.Empty, MoveFlags.DoublePush);
        var id = ChessCompose.MoveId(Piece.WPawn, move);
        Assert.Equal(StoredE4, Convert.ToHexString(id.ToBytes()).ToLowerInvariant());
    }

    [Fact]
    public void MoveAtoms_DecodeBackToPieceFromTo()
    {
        var move = new ChessMove(Board.Sq(4, 1), Board.Sq(4, 3), Piece.Empty, MoveFlags.DoublePush);
        Span<ChessPositionIdentity.Atom> atoms = stackalloc ChessPositionIdentity.Atom[5];
        int n = ChessPositionIdentity.FillMoveAtoms(Piece.WPawn, move, atoms);
        Assert.Equal(5, n);

        var index = ChessPositionIdentity.MoveAtomIndex;
        int piece = -1, from = -1, to = -1;
        for (int i = 0; i < n; i++)
        {
            Assert.True(index.TryGetValue(ChessPositionIdentity.AtomId(atoms[i]), out var d),
                $"atom {i} (domain {atoms[i].Domain}) is not in the reverse index");
            if (d.Domain == ChessPositionIdentity.MovePieceDomain) piece = d.Value;
            if (d.Domain == ChessPositionIdentity.MoveFromDomain) from = d.Value;
            if (d.Domain == ChessPositionIdentity.MoveToDomain) to = d.Value;
        }

        Assert.Equal(0, piece);                 // WPawn
        Assert.Equal((1 << 3) | 4, from);       // e2
        Assert.Equal((3 << 3) | 4, to);         // e4
    }

    [Theory]
    [InlineData(0)]   // WPawn
    [InlineData(5)]   // WKing
    [InlineData(6)]   // BPawn
    [InlineData(11)]  // BKing
    public void EveryPieceOrdinal_RoundTrips(ushort ordinal)
    {
        var atom = ChessPositionIdentity.Atom.Scalar(ChessPositionIdentity.MovePieceDomain, ordinal);
        Assert.True(ChessPositionIdentity.MoveAtomIndex.TryGetValue(
            ChessPositionIdentity.AtomId(atom), out var d));
        Assert.Equal(ChessPositionIdentity.MovePieceDomain, d.Domain);
        Assert.Equal(ordinal, d.Value);
    }

    [Fact]
    public void EverySquare_RoundTripsOnBothMoveEnds()
    {
        for (ushort sq = 0; sq < 64; sq++)
        {
            foreach (byte domain in new[] { ChessPositionIdentity.MoveFromDomain,
                                            ChessPositionIdentity.MoveToDomain })
            {
                var atom = ChessPositionIdentity.Atom.Scalar(domain, sq);
                Assert.True(ChessPositionIdentity.MoveAtomIndex.TryGetValue(
                    ChessPositionIdentity.AtomId(atom), out var d), $"{domain}/{sq} missing");
                Assert.Equal(domain, d.Domain);
                Assert.Equal(sq, d.Value);
            }
        }
    }
}
