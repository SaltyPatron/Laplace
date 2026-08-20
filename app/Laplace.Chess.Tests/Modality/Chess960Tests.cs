using Laplace.Chess.Service;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Modality.Chess.Tests;

/// <summary>
/// Chess960 ("Freestyle"), which chess.com exports with X-FEN/Shredder castling.
///
/// These games were refused outright until now — 1,866 across the corpora, 0.8%, and up to
/// 18.8% of an individual chess.com archive. The refusal was deliberate and correct at the
/// time: <c>Board.FromFen</c> threw on a castling field it could not model, because
/// replaying such a game from the standard array records a game that was never played.
///
/// THE FIRST GROUP IS THE POINT OF THE WHOLE CHANGE. Position identity embeds
/// <c>CastleString()</c> (PositionContent.Surface -> "cr:"), so if supporting rook files
/// altered that string for ordinary chess, every position id in the substrate would move
/// and the corpus would need a reseed. It does not: the rook files default to the standard
/// ones and CastleString emits the classic KQkq whenever they hold. These tests are that
/// claim, executable.
/// </summary>
public class Chess960Tests
{
    private const string Startpos = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const string StartposShredder = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w AHah - 0 1";

    // ---- identity must not move -------------------------------------------------------

    [Fact]
    public void StandardPosition_CastlingFieldIsUnchanged()
        => Assert.Equal("KQkq", Board.FromFen(Startpos).CastleString());

    /// <summary>
    /// The same board written in Shredder notation is the SAME position — same castling
    /// field, therefore the same content surface, therefore the same id. A corpus recorded
    /// before this change and one recorded after must collide, not diverge.
    /// </summary>
    [Fact]
    public void ShredderNotationOfStandardBoard_ProducesIdenticalIdentity()
    {
        var m = new ChessModality();
        Assert.Equal("KQkq", Board.FromFen(StartposShredder).CastleString());
        Assert.Equal(m.StateKey(m.FromFen(Startpos)), m.StateKey(m.FromFen(StartposShredder)));
    }

    [Theory]
    [InlineData(1, 20)]
    [InlineData(2, 400)]
    [InlineData(3, 8902)]
    [InlineData(4, 197281)]
    public void ShredderNotationOfStandardBoard_PerftMatchesStandard(int depth, long expected)
        => Assert.Equal(expected, Perft.Run(Board.FromFen(StartposShredder), depth));

    // ---- the shapes ordinary chess cannot produce -------------------------------------

    /// <summary>
    /// The king castles WITHOUT MOVING: it already stands on g1, and only the rook travels.
    /// The generic mover would have deleted it — `Squares[To] = moving; Squares[From] =
    /// Empty` with To == From clears the square it just wrote.
    /// </summary>
    [Fact]
    public void KingSide_KingAlreadyOnDestination_CastlesWithoutMoving()
    {
        var b = Board.FromFen("1rqkbbnr/pppppppp/8/8/8/8/PPPPPPPP/6KR w H - 0 1");
        var mv = FindCastle(b, kingSide: true);
        MoveApply.Make(b, mv);

        Assert.Equal(Piece.WKing, b.Squares[Board.Sq(6, 0)]);   // king stayed on g1
        Assert.Equal(Piece.WRook, b.Squares[Board.Sq(5, 0)]);   // rook h1 -> f1
        Assert.Equal(Piece.Empty, b.Squares[Board.Sq(7, 0)]);
    }

    /// <summary>
    /// King and rook SWAP: the king's destination (c1) is the rook's square and the rook's
    /// destination (d1) is the king's. The generic mover would have scored the king's move
    /// as capturing its own rook.
    /// </summary>
    [Fact]
    public void QueenSide_RookOnKingDestination_ResolvesBothPieces()
    {
        var b = Board.FromFen("1rqkbbnr/pppppppp/8/8/8/8/PPPPPPPP/2RKBBNR w C - 0 1");
        var mv = FindCastle(b, kingSide: false);
        MoveApply.Make(b, mv);

        Assert.Equal(Piece.WKing, b.Squares[Board.Sq(2, 0)]);   // king -> c1
        Assert.Equal(Piece.WRook, b.Squares[Board.Sq(3, 0)]);   // rook c1 -> d1
    }

    /// <summary>Make/Unmake must be exact for castling, or perft and the analyzer's replay
    /// diverge from each other in ways that only show up deep in a search.</summary>
    [Theory]
    [InlineData("1rqkbbnr/pppppppp/8/8/8/8/PPPPPPPP/6KR w H - 0 1", true)]
    [InlineData("1rqkbbnr/pppppppp/8/8/8/8/PPPPPPPP/2RKBBNR w C - 0 1", false)]
    public void CastleUnmake_RestoresThePosition(string fen, bool kingSide)
    {
        var b = Board.FromFen(fen);
        string before = b.ToFen();
        var mv = FindCastle(b, kingSide);
        var undo = MoveApply.MakeWithUndo(b, mv);
        MoveApply.Unmake(b, mv, undo);
        Assert.Equal(before, b.ToFen());
    }

    /// <summary>A king that never stood on e1 still loses its rights when it moves. The old
    /// rights table switched on the literal squares 0/4/7/112/116/119.</summary>
    [Fact]
    public void KingMoveOffAnyStartSquare_ClearsBothRights()
    {
        var b = Board.FromFen("1rqkbbnr/pppppppp/8/8/8/8/PPPPPPPP/2RKBBNR w C - 0 1");
        Assert.NotEqual(CastleRights.None, b.Castle);
        MoveApply.Make(b, new ChessMove(Board.Sq(3, 0), Board.Sq(3, 1), Piece.Empty, MoveFlags.None));
        Assert.Equal(CastleRights.None, b.Castle & (CastleRights.WhiteKing | CastleRights.WhiteQueen));
    }

    [Fact]
    public void ShredderFen_RoundTrips()
    {
        const string fen = "nbrkbrnq/pppppppp/8/8/8/8/PPPPPPPP/NBRKBRNQ w FCfc - 0 1";
        Assert.Equal(fen, Board.FromFen(fen).ToFen());
    }

    // ---- the corpus this was blocking -------------------------------------------------

    /// <summary>
    /// The eight Chess960 games in a real chess.com archive, replayed end to end. A wrong
    /// castling rule derails SAN resolution within a few moves, so a 153-ply game finishing
    /// is stronger evidence than any single constructed position.
    /// </summary>
    [SkippableFact]
    public async Task RealChessComFreestyleGames_AllReplay()
    {
        string pgn = Path.Combine(Laplace.Chess.Service.Tests.ChessCorpusPaths.Games,
                                  "AnishGiri_chesscom.pgn");
        Skip.IfNot(File.Exists(pgn), "chess.com archive not present");

        int seen = 0, replayed = 0;
        await foreach (var text in ChessPgnDecomposer.StreamAllGamesAsync(
                           pgn, SearchOption.TopDirectoryOnly, default))
        {
            if (!text.Contains("Chess960")) continue;
            seen++;
            if (ChessPgnDecomposer.TryParseGame(text) is not null) replayed++;
        }
        Assert.True(seen > 0, "expected Chess960 games in this archive");
        Assert.Equal(seen, replayed);
    }

    private static ChessMove FindCastle(Board b, bool kingSide)
    {
        var pseudo = new List<ChessMove>();
        var legal = new List<ChessMove>();
        MoveGen.Legal(b, pseudo, legal);
        var hit = legal.Where(m => kingSide ? m.IsKingSideCastle : m.IsQueenSideCastle).ToList();
        Assert.True(hit.Count == 1,
            $"expected exactly one {(kingSide ? "king" : "queen")}-side castle, got {hit.Count}");
        return hit[0];
    }

    // ---- the fixed set of 960 ---------------------------------------------------------

    /// <summary>All 960 derived, all distinct — the derivation is right or this is not 960.</summary>
    [Fact]
    public void TheSetHasExactly960DistinctArrays()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int n = 0; n < Chess960Positions.Count; n++) Assert.True(seen.Add(Chess960Positions.BackRank(n)));
        Assert.Equal(960, seen.Count);
    }

    /// <summary>Standard chess is SP 518. Not 0, not 1 — SP 0 is BBQNNRKR.</summary>
    [Fact]
    public void StandardChessIsPosition518()
    {
        Assert.Equal("RNBQKBNR", Chess960Positions.BackRank(Chess960Positions.StandardNumber));
        Assert.Equal(518, Chess960Positions.StandardNumber);
        Assert.Equal(518, Chess960Positions.TryNumber("RNBQKBNR"));
        Assert.Equal("BBQNNRKR", Chess960Positions.BackRank(0));
    }

    /// <summary>Every array in the set is castleable: bishops on opposite colours, king
    /// between the rooks. If the derivation produced an illegal array, castling geometry
    /// would be undefined for it.</summary>
    [Fact]
    public void EveryArrayIsLegal()
    {
        for (int n = 0; n < Chess960Positions.Count; n++)
        {
            string rank = Chess960Positions.BackRank(n);
            var bishops = Enumerable.Range(0, 8).Where(f => rank[f] == 'B').ToList();
            Assert.Equal(2, bishops.Count);
            Assert.NotEqual(bishops[0] % 2, bishops[1] % 2);          // opposite colours

            var rooks = Enumerable.Range(0, 8).Where(f => rank[f] == 'R').ToList();
            int king = rank.IndexOf('K');
            Assert.Equal(2, rooks.Count);
            Assert.True(rooks[0] < king && king < rooks[1], $"SP {n}: king not between rooks");
        }
    }

    /// <summary>The eight Freestyle games in the corpus are members of the standard
    /// enumeration, so each can be NAMED by its board number.</summary>
    [Theory]
    [InlineData("NBRKBRNQ", 376)]
    [InlineData("BBRNNKRQ", 464)]
    [InlineData("BNRQNBKR", 130)]
    [InlineData("RKNQBBRN", 826)]
    [InlineData("RKQRBBNN", 906)]
    [InlineData("BRKBNNQR", 737)]
    [InlineData("NRBBQKNR", 229)]
    [InlineData("QNBBRNKR", 101)]
    public void RealCorpusFreestyleArrays_AreInTheSet(string backRank, int expected)
        => Assert.Equal(expected, Chess960Positions.TryNumber(backRank));

    [Fact]
    public void NonMemberBackRank_HasNoNumber()
        => Assert.Null(Chess960Positions.TryNumber("RNBQKBNQ"));   // two queens, no king

    /// <summary>
    /// THE RULE IS THE AUTHORITY, NOT THE LIST. Double Fischer Random gives White and Black
    /// different back ranks — legal under the format, and absent from Scharnagl's
    /// enumeration, which only numbers symmetric arrays. Such a game must still PARSE,
    /// still CASTLE, and simply carry no board number.
    ///
    /// The list is somebody else's; the rules are the format's. Refusing a position for
    /// being unnumbered would be the EXISTS-collapses-the-distinction error again —
    /// unattested is not attested-false.
    /// </summary>
    [Fact]
    public void AsymmetricStart_PlaysFine_AndSimplyHasNoNumber()
    {
        // White NBRKBRNQ (#376), Black nqrkbbrn — different arrays, both legal.
        const string dfrc = "nqrkbbrn/pppppppp/8/8/8/8/PPPPPPPP/NBRKBRNQ w FCgc - 0 1";
        var b = Board.FromFen(dfrc);

        Assert.Null(Chess960Positions.TryNumberOfStart(b));        // no name...
        Assert.Equal(CastleRights.All, b.Castle);                  // ...but fully playable
        Assert.Equal(dfrc, b.ToFen());

        var pseudo = new List<ChessMove>();
        var legal = new List<ChessMove>();
        MoveGen.Legal(b, pseudo, legal);
        Assert.NotEmpty(legal);
        Assert.Equal(20, legal.Count);   // 16 pawn + 4 knight moves from any 960 array
    }

    /// <summary>A START position reports its number; a MID-GAME one reports none, because it
    /// has none — which is why the engine keys on rook files, not on this.</summary>
    [Fact]
    public void OnlyStartingArraysHaveANumber()
    {
        Assert.Equal(518, Chess960Positions.TryNumberOfStart(Board.FromFen(Startpos)));
        Assert.Equal(376, Chess960Positions.TryNumberOfStart(
            Board.FromFen("nbrkbrnq/pppppppp/8/8/8/8/PPPPPPPP/NBRKBRNQ w FCfc - 0 1")));
        Assert.Null(Chess960Positions.TryNumberOfStart(
            Board.FromFen("rnbqkbnr/pppppppp/8/8/8/4P3/PPPP1PPP/RNBQKBNR b KQkq e3 0 1")));
    }

    /// <summary>
    /// In Chess960 a CASTLE and an ordinary king move can share (from, to). Found on a real
    /// game — DenLaz_chesscom.pgn, white king d1, rooks a1/f1 — where the source writes
    /// "Kc1" for the ordinary step d1->c1, and the queen-side castle also ends on c1. The
    /// resolver matched both, called it ambiguous, and dropped a 58-ply game.
    ///
    /// Standard chess cannot produce this: the king starts on e1, castling lands it two
    /// squares away, and "Kc1" from e1 is not a legal king move. A castle is only ever
    /// spelled O-O / O-O-O, so a piece-move SAN must never match one.
    /// </summary>
    [Fact]
    public void KingMove_SharingItsSquareWithACastle_IsNotAmbiguous()
    {
        var b = Board.FromFen("rb1k1r1q/1p1bpp1p/2p2np1/p2p4/P1n2PP1/1NP4P/1PBPP1Q1/R2KBRN1 w FAfa - 1 9");
        var pseudo = new List<ChessMove>();
        var legal = new List<ChessMove>();
        MoveGen.Legal(b, pseudo, legal);

        // Both really are legal and both really do land on c1.
        Assert.Contains(legal, m => m.To == Board.Sq(2, 0) && m.IsQueenSideCastle);
        Assert.Contains(legal, m => m.To == Board.Sq(2, 0) && !m.IsCastle);

        var king = San.Resolve(b, legal, "Kc1");
        Assert.NotNull(king);
        Assert.False(king!.Value.IsCastle);

        var castle = San.Resolve(b, legal, "O-O-O");
        Assert.NotNull(castle);
        Assert.True(castle!.Value.IsQueenSideCastle);
    }

    // ---- the collision, enumerated rather than stumbled on --------------------------

    /// <summary>
    /// Half the arrays can spell a castle and a king move with the same (from, to). This is
    /// the census; the resolver rule is tested above on the real game that exposed it.
    /// </summary>
    [Fact]
    public void HalfOfAllArraysCanCollideACastleWithAKingMove()
    {
        int kingSide = 0, queenSide = 0, either = 0;
        for (int n = 0; n < Chess960Positions.Count; n++)
        {
            var g = Chess960Positions.Geometry(n);
            if (g.KingSideSharesDestinationWithKingMove) kingSide++;
            if (g.QueenSideSharesDestinationWithKingMove) queenSide++;
            if (g.CanCollideWithKingMove) either++;
        }
        Assert.Equal(168, kingSide);
        Assert.Equal(312, queenSide);
        Assert.Equal(480, either);          // exactly half of 960
    }

    /// <summary>Ordinary chess cannot produce it — both destinations are two squares from
    /// e1, so no legal king move reaches them. That is why it went unseen.</summary>
    [Fact]
    public void StandardChessCannotCollide()
    {
        var g = Chess960Positions.Geometry(Chess960Positions.StandardNumber);
        Assert.Equal(4, g.KingFile);        // e1
        Assert.Equal(7, g.KingRookFile);    // h1
        Assert.Equal(0, g.QueenRookFile);   // a1
        Assert.False(g.CanCollideWithKingMove);
    }

    /// <summary>The array from the game that actually broke: SP 664, king on d1.</summary>
    [Fact]
    public void TheArrayThatExposedIt_IsFlagged()
    {
        Assert.Equal(664, Chess960Positions.TryNumber("RBNKBRNQ"));
        var g = Chess960Positions.Geometry(664);
        Assert.Equal(3, g.KingFile);                                   // d1
        Assert.True(g.QueenSideSharesDestinationWithKingMove);         // d1 -> c1
        Assert.False(g.KingSideSharesDestinationWithKingMove);
    }

    /// <summary>Geometry agrees with what FromFen derives, for every one of the 960 — the
    /// table and the parser must not drift.</summary>
    [Fact]
    public void GeometryMatchesWhatTheParserDerives_ForAll960()
    {
        for (int n = 0; n < Chess960Positions.Count; n++)
        {
            string rank = Chess960Positions.BackRank(n);
            var b = Board.FromFen($"{rank.ToLowerInvariant()}/pppppppp/8/8/8/8/PPPPPPPP/{rank} w KQkq - 0 1");
            var g = Chess960Positions.Geometry(n);
            Assert.Equal(g.KingRookFile, b.WhiteKingRookFile);
            Assert.Equal(g.QueenRookFile, b.WhiteQueenRookFile);
            Assert.Equal(g.KingRookFile, b.BlackKingRookFile);
            Assert.Equal(g.QueenRookFile, b.BlackQueenRookFile);
        }
    }
}
