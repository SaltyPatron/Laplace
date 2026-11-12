using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>
/// A Syzygy tablebase verdict for one position, side-to-move POV.
/// <see cref="Wdl"/> is 0..4 (loss / blessed-loss / draw / cursed-win / win —
/// <see cref="SyzygyNative"/> order); <see cref="Dtz"/> is plies to the next zeroing
/// move under optimal play.
/// </summary>
public readonly record struct SyzygyVerdict(
    int Wdl, int Dtz, int From = -1, int To = -1, int Promotes = 0);

/// <summary>Probe boundary: the native kernel in production, a fake in tests.</summary>
public interface ISyzygyProber
{
    /// <summary>Largest man count the loaded table set covers (0 = nothing loaded).</summary>
    int Largest { get; }

    /// <summary>
    /// Thread-safe WDL-only probe (Fathom <c>tb_probe_wdl</c>). Null when no table answers.
    /// Prefer this in the parallel unpack fan-out.
    /// </summary>
    int? ProbeWdl(Board board);

    /// <summary>WDL+DTZ root probe (process-locked — Fathom root is not thread-safe).
    /// Null when no table answers or the position is terminal.</summary>
    SyzygyVerdict? Probe(Board board);
}

/// <summary>The in-process native prober (Fathom kernel via <see cref="SyzygyNative"/>).</summary>
public sealed class SyzygyNativeProber : ISyzygyProber
{
    public int Largest => SyzygyNative.Largest();

    public int? ProbeWdl(Board board)
    {
        if (!board.Rules.IsStandard) return null;
        var bb = ChessSyzygy.ToBitboards(board);
        int wdl = SyzygyNative.ProbeWdl(
            bb.White, bb.Black, bb.Kings, bb.Queens, bb.Rooks,
            bb.Bishops, bb.Knights, bb.Pawns, bb.Ep, board.WhiteToMove);
        return wdl < 0 ? null : wdl;
    }

    public SyzygyVerdict? Probe(Board board)
    {
        if (!board.Rules.IsStandard) return null;
        var bb = ChessSyzygy.ToBitboards(board);
        return SyzygyNative.ProbeRootTransition(
                   bb.White, bb.Black, bb.Kings, bb.Queens, bb.Rooks,
                   bb.Bishops, bb.Knights, bb.Pawns, bb.Ep, board.WhiteToMove)
               is { } v
            ? new SyzygyVerdict(v.Wdl, v.Dtz, v.From, v.To, v.Promotes)
            : null;
    }
}

/// <summary>
/// Syzygy tablebase admission. Fathom decodes package entries into exact optimal transitions,
/// composed as content-addressed position → typed move → position trajectory segments with
/// WDL/DTZ on the vertices. The mapped package is a decoding/search perfcache, not the
/// admitted content. Recorded games use the per-position path; material packages use the
/// compact graph path.

/// <summary>The board as Fathom reads it, and its man count: what a probe of the local tables takes.</summary>
public static class ChessSyzygy
{
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

}
