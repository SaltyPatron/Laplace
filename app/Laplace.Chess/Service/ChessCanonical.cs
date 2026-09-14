using System.Globalization;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

public static class ChessCanonical
{
    public static string? ClockFromSeconds(double secondsRemaining)
    {
        if (secondsRemaining < 0 || double.IsNaN(secondsRemaining)) return null;
        int total = (int)Math.Floor(secondsRemaining);
        int h = total / 3600;
        int m = (total % 3600) / 60;
        int s = total % 60;
        return $"{h}:{m:D2}:{s:D2}";
    }

    public static string? ClockFromMatch(string hours, string minutes, string seconds)
    {
        if (!int.TryParse(hours, out int h)) return null;
        if (!int.TryParse(minutes, out int m)) return null;
        if (!double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out double sec)) return null;
        return ClockFromSeconds(h * 3600 + m * 60 + sec);
    }

    public static string? EvalToken(string raw)
    {
        var t = raw.Trim();
        return t.Length == 0 ? null : t;
    }

    public static string ThinkClass(double thinkFactor) => thinkFactor switch
    {
        <= 0.75 => "rushed",
        >= 1.25 => "deep",
        _ => "normal",
    };

    /// <summary>
    /// Board-state phase, not move-number folklore. This uses the same PeSTO phase material
    /// weights as the evaluator (N/B=1, R=2, Q=4; starting total 24) and names a reusable
    /// context content value. Because the class is derived from the board itself it works for
    /// arbitrary FENs, transpositions and non-standard starts instead of assuming that move 12
    /// must be "opening" or move 40 must be "endgame".
    /// </summary>
    public static string PhaseClass(Board board)
    {
        ArgumentNullException.ThrowIfNull(board);
        int phase = 0;
        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            phase += Board.TypeOf(board.Squares[sq]) switch
            {
                Piece.WKnight or Piece.WBishop => 1,
                Piece.WRook => 2,
                Piece.WQueen => 4,
                _ => 0,
            };
        }
        phase = Math.Min(24, phase);
        return phase switch
        {
            >= 17 => "phase:opening",
            >= 9 => "phase:middlegame",
            _ => "phase:endgame",
        };
    }

    /// <summary>
    /// Phase × clock × spent lens over one ply, refining <see cref="ThinkClass"/> with
    /// the game's OWN distributions — no operator constants anywhere: the spent cut
    /// points are ThinkClass's existing factor thresholds (relative to the game's median
    /// think), the low-clock threshold is the player's own median remaining
    /// (<see cref="PgnClocks.MedianRemaining"/>), the flagging threshold is the game's
    /// median per-move cost, and the phase bound is a tertile of the game's own length.
    /// Clock lenses derive only when the source witnessed a remaining clock — the
    /// cutechess spent dialect carries none, and fabricating one would mint a quantity
    /// the source never asserted.
    ///
    ///   flagging      — remaining below the game's own median per-move cost: the clock
    ///                   cannot fund one more typical think, so speed is forced.
    ///   pressed_think — long think on a low clock: the player judged the moment
    ///                   critical enough to spend scarce time.
    ///   planned_quick — early-phase fast move with no clock pressure: book-consistent
    ///                   preparation, not haste.
    ///
    /// The fourth lens of the family — late, fast, low clock — IS the base "rushed"
    /// class (same content value, deposited by ThinkClass already); returning it here
    /// would double-witness the same cell from one ply, so this returns null and the
    /// read side gets that lens from the base deposit. Null = no lens adds information.
    /// </summary>
    public static string? ThinkLens(
        int ply, int plyCount, double thinkFactor,
        double remaining, double medianRemaining, double medianDrop)
    {
        bool hasClock = remaining > 0 && medianRemaining > 0 && medianDrop > 0;
        bool lowClock = hasClock && remaining < medianRemaining;
        string cls = ThinkClass(thinkFactor);
        if (hasClock && remaining < medianDrop) return "flagging";
        if (lowClock && cls == "deep") return "pressed_think";
        if (3 * ply < plyCount && cls == "rushed" && !lowClock) return "planned_quick";
        return null;
    }

    public static string? Eco(string raw)
    {
        var t = raw.Trim().ToUpperInvariant();
        return t.Length == 0 || t == "?" ? null : t;
    }

    public static string? OpeningName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return string.Join(' ', raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}