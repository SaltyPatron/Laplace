using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// The vocabulary cache is a LOOKUP change, never an identity change. Chess ids are the
/// corpus: if composing through the structural index produced a different hash than the
/// dictionary path, every position, every trajectory and every MOVE edge already ingested
/// would be orphaned. These pin that it cannot.
/// </summary>
public sealed class ChessVocabularyCacheTests
{
    private static readonly string[] Surfaces =
    [
        "stm:w cr:KQkq ep:- Pe2 Nb1 Ke1 Ra1 Rh1",
        "stm:b cr:kq ep:e3 pe7 nb8 ke8 qd8",
        "stm:w cr:- ep:- Ke1 ke8",
    ];

    [Fact]
    public void EveryPieceSquareTokenResolvesToTheSameNodeAsTheGeneralPath()
    {
        CodepointPerfcache.LoadDefault();
        const string pieces = "PNBRQKpnbrqk";
        lock (ChessCompose.Gate)
        {
            foreach (char pc in pieces)
                for (int f = 0; f < 8; f++)
                    for (int r = 0; r < 8; r++)
                    {
                        string tok = $"{pc}{(char)('a' + f)}{(char)('1' + r)}";
                        Assert.True(ChessVocabularyCache.TryGet(tok, ChessComposeProbe.Compose, out var cached));
                        Assert.Equal(ChessComposeProbe.Compose(tok).Id, cached.Id);
                    }
        }
    }

    [Fact]
    public void PositionIdsAreUnchanged()
    {
        CodepointPerfcache.LoadDefault();
        lock (ChessCompose.Gate)
        {
            foreach (var sf in Surfaces)
            {
                // Position() goes through the cache; the id must equal a Merkle built from
                // the general path token by token.
                var viaCache = ChessCompose.Position(sf).Position.Id;
                var toks = sf.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                var ids = new Hash128[toks.Length];
                for (int i = 0; i < toks.Length; i++) ids[i] = ChessComposeProbe.Compose(toks[i]).Id;
                Assert.Equal(Hash128.Merkle(2, ids), viaCache);
            }
        }
    }

    [Fact]
    public void PositionIdMatchesFullComposition()
    {
        CodepointPerfcache.LoadDefault();
        lock (ChessCompose.Gate)
            foreach (var sf in Surfaces)
                Assert.Equal(ChessCompose.Position(sf).Position.Id, ChessCompose.PositionId(sf));
    }

    [Fact]
    public void NonVocabularyTokensAreRejectedNotGuessed()
    {
        // The pawn aggregates and anything else must fall through, never resolve wrongly.
        foreach (var t in new[] { "wpawns:a2b2", "stm:w", "cr:KQkq", "ep:-", "Xe2", "Pz9", "Pe" })
            Assert.False(ChessVocabularyCache.TryGet(t, _ => throw new System.InvalidOperationException(), out _));
    }
}
