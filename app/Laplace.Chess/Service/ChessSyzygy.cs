using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

/// <summary>
/// A Syzygy tablebase verdict for one position, side-to-move POV.
/// <see cref="Wdl"/> is 0..4 (loss / blessed-loss / draw / cursed-win / win —
/// <see cref="SyzygyNative"/> order); <see cref="Dtz"/> is plies to the next zeroing
/// move under optimal play.
/// </summary>
public readonly record struct SyzygyVerdict(int Wdl, int Dtz);

/// <summary>Probe boundary: the native kernel in production, a fake in tests.</summary>
public interface ISyzygyProber
{
    /// <summary>Largest man count the loaded table set covers (0 = nothing loaded).</summary>
    int Largest { get; }

    /// <summary>Exact verdict for the board, or null when no table answers (terminal
    /// position, missing table, castling rights).</summary>
    SyzygyVerdict? Probe(Board board);
}

/// <summary>The in-process native prober (Fathom kernel via <see cref="SyzygyNative"/>).</summary>
public sealed class SyzygyNativeProber : ISyzygyProber
{
    public int Largest => SyzygyNative.Largest();

    public SyzygyVerdict? Probe(Board board)
    {
        var bb = ChessSyzygy.ToBitboards(board);
        return SyzygyNative.ProbeRoot(
                   bb.White, bb.Black, bb.Kings, bb.Queens, bb.Rooks,
                   bb.Bishops, bb.Knights, bb.Pawns, bb.Ep, board.WhiteToMove)
               is { } v
            ? new SyzygyVerdict(v.Wdl, v.Dtz)
            : null;
    }
}

/// <summary>
/// CALCULATED Syzygy probe pass (campaign PR-8): replay a witnessed line, probe every
/// non-terminal position the loaded table set covers (men ≤ largest, no castling
/// rights), attest the exact endgame oracle facts under the ChessSyzygy source:
/// (position, HAS_WDL, token) — five-valued, side-to-move POV — and
/// (position, HAS_DTZ, scalar content), both ctx = the LINE. GH #736: a tablebase
/// verdict is a pure function of the position, so the unit is the LINE — marker
/// chess/syzygy/{lineId}/{v}, exactly the stockfish-eval grain. Trust class
/// StandardsDerived: a mathematically exact oracle is a high-weight witness, still one
/// voice — phi does the rest. Versioned and evictable (evict_source, #508).
/// </summary>
public static class ChessSyzygy
{
    public const int Version = 1;

    public const string SourceName = "ChessSyzygy";
    public static readonly Hash128 SourceId = SubstrateCanonicalIds.Source(SourceName);
    public static readonly Hash128 TrustClassId = ChessVocabulary.SyzygyTrustClass;

    /// <summary>Witness weight of the oracle's testimony (the StandardsDerived trust band).</summary>
    public const double Weight = TC.StandardsDerived;

    public static Hash128 MarkerId(Hash128 lineId, int version)
        => Hash128.OfCanonical($"chess/syzygy/{lineId}/{version}");

    /// <summary>Five-valued WDL content token, side-to-move POV (Fathom order 0..4).</summary>
    public static string WdlToken(int wdl) => wdl switch
    {
        SyzygyNative.Loss => "loss",
        SyzygyNative.BlessedLoss => "blessed-loss",
        SyzygyNative.Draw => "draw",
        SyzygyNative.CursedWin => "cursed-win",
        SyzygyNative.Win => "win",
        _ => throw new ArgumentOutOfRangeException(nameof(wdl), wdl, "WDL is 0..4"),
    };

    public readonly record struct Bitboards(
        ulong White, ulong Black, ulong Kings, ulong Queens, ulong Rooks,
        ulong Bishops, ulong Knights, ulong Pawns, uint Ep);

    /// <summary>
    /// 0x88 board → Fathom bitboards (a1=bit0..h8=bit63). Ep is the en-passant square
    /// only when a legal capture exists (the same canonical-ep fact position identity
    /// uses — <see cref="ChessModality.CapturableEpSquare"/>), else 0.
    /// </summary>
    public static Bitboards ToBitboards(Board b)
    {
        ulong white = 0, black = 0, kings = 0, queens = 0, rooks = 0,
              bishops = 0, knights = 0, pawns = 0;
        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            var p = b.Squares[sq];
            if (p == Piece.Empty) continue;
            ulong bit = 1UL << (Board.RankOf(sq) * 8 + Board.FileOf(sq));
            if (Board.IsWhite(p)) white |= bit; else black |= bit;
            switch (Board.TypeOf(p))
            {
                case Piece.WKing: kings |= bit; break;
                case Piece.WQueen: queens |= bit; break;
                case Piece.WRook: rooks |= bit; break;
                case Piece.WBishop: bishops |= bit; break;
                case Piece.WKnight: knights |= bit; break;
                case Piece.WPawn: pawns |= bit; break;
            }
        }
        int ep = ChessModality.CapturableEpSquare(b);
        uint ep64 = ep < 0 ? 0u : (uint)(Board.RankOf(ep) * 8 + Board.FileOf(ep));
        return new Bitboards(white, black, kings, queens, rooks, bishops, knights, pawns, ep64);
    }

    public static int MenCount(Board b)
    {
        int men = 0;
        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            if (b.Squares[sq] != Piece.Empty) men++;
        }
        return men;
    }

    public static void DeriveGame(SubstrateChangeBuilder b, ChessWitnessedGame game, ISyzygyProber prober)
    {
        var m = new ChessModality();
        // Unreadable start: probe nothing rather than probe a board the game never had.
        if (ChessAnalyze.InitialState(game.StartFen, m) is not { } start) return;
        var state = start.Initial;
        int largest = prober.Largest;

        var cur = state;
        int n = game.Moves.Count;
        for (int ply = 0; ply <= n; ply++)
        {
            // Terminal positions need no oracle (the game record already witnesses the
            // result); castling rights are outside every tablebase's domain.
            if (largest > 0
                && m.Terminal(cur) is null
                && cur.Board.Castle == CastleRights.None
                && MenCount(cur.Board) <= largest
                && prober.Probe(cur.Board) is { } verdict)
            {
                var node = ChessGraph.EmitComposed(b, m.StateKey(cur), SourceId);
                // ctx = the LINE: exact-oracle testimony on the position reached along
                // it — line-grain, never per-playing provenance (stockfish-eval parity).
                if (ContentEmitter.Emit(b, WdlToken(verdict.Wdl), SourceId) is { } wdlId)
                    b.AddAttestation(NativeAttestation.Categorical(
                        node.Position.Id, "HAS_WDL", wdlId, SourceId, game.LineId, Weight));
                if (ContentEmitter.Emit(b, verdict.Dtz.ToString(), SourceId) is { } dtzId)
                    b.AddAttestation(NativeAttestation.Categorical(
                        node.Position.Id, "HAS_DTZ", dtzId, SourceId, game.LineId, Weight));
            }

            if (ply == n) break;
            var mv = San.Resolve(cur.Board, m.LegalActions(cur), game.Moves[ply]);
            if (mv is null) break; // unreplayable movetext — stop; partial probes stand
            cur = m.Apply(cur, mv.Value);
        }

        b.AddEntity(MarkerId(game.LineId, Version), EntityTier.Document,
            ChessVocabulary.AnalysisMarkerType, SourceId);
        if (ContentEmitter.Emit(b, Version.ToString(), SourceId) is { } vId)
            b.AddAttestation(NativeAttestation.Categorical(
                game.LineId, "ANALYZED_AT", vId, SourceId, null, Weight));
    }
}
