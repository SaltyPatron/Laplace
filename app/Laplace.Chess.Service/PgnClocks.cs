using System.Text.RegularExpressions;

namespace Laplace.Chess.Service;

/// <summary>
/// Extracts per-move clock readings (<c>{[%clk H:MM:SS(.s)]}</c>) from raw PGN movetext, ALIGNED to the
/// grammar's ordered SAN moves. The <c>pgn</c> tree-sitter grammar deliberately strips comments/clocks
/// (clean SAN for replay), so the rich per-move signal we were dropping — how long each move took — is
/// recovered here by an ordered scan and fed back as evidence weight: a move played after a real think
/// is stronger testimony of intent than a pre-move or a flagged-on-time scramble.
/// </summary>
internal static partial class PgnClocks
{
    [GeneratedRegex(@"\[%clk\s+(\d+):(\d+):(\d+(?:\.\d+)?)\]")]
    private static partial Regex ClkRegex();

    /// <summary>Clock-seconds-remaining in move order (one per move), or an empty array when the game has
    /// no clocks or the count doesn't line up 1:1 with <paramref name="moveCount"/> (don't guess).</summary>
    public static double[] SecondsRemaining(string gameText, int moveCount)
    {
        var tokens = ClockTokens(gameText, moveCount);
        if (tokens is null) return Array.Empty<double>();
        var outv = new double[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            var parts = tokens[i].Split(':');
            if (parts.Length != 3) continue;
            outv[i] = int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60
                + double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
        }
        return outv;
    }

    /// <summary>Canonical <c>H:MM:SS</c> clock strings per move (content-addressed emission), or null when misaligned.</summary>
    public static string[]? ClockTokens(string gameText, int moveCount)
    {
        var ms = ClkRegex().Matches(gameText);
        if (ms.Count == 0 || ms.Count != moveCount) return null;
        var outv = new string[moveCount];
        for (int i = 0; i < moveCount; i++)
        {
            var m = ms[i];
            var canon = ChessCanonical.ClockFromMatch(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
            if (canon is null) return null;
            outv[i] = canon;
        }
        return outv;
    }

    /// <summary>Think-time factor in [0.5, 1.5] for move <paramref name="i"/> (a side's clock drop since
    /// its previous move, scaled by the game's median drop). 1.0 = no clocks / first moves / typical pace;
    /// &lt;1 = rushed/pre-moved; &gt;1 = a real think. Multiplies the move-choice observation count so the
    /// deliberate moves of a strong player carry more weight than reflexes.</summary>
    public static double ThinkFactor(double[] clocks, double medianDrop, int i)
    {
        if (clocks.Length == 0 || i < 2 || medianDrop <= 0) return 1.0;
        double drop = clocks[i - 2] - clocks[i];           // time this side spent on move i
        if (drop <= 0) return 0.5;                          // pre-move / no time spent
        double f = drop / medianDrop;
        return Math.Clamp(f, 0.5, 1.5);
    }

    /// <summary>Median per-move clock drop across the game (the pace baseline for <see cref="ThinkFactor"/>).</summary>
    public static double MedianDrop(double[] clocks)
    {
        if (clocks.Length < 3) return 0;
        var drops = new List<double>(clocks.Length);
        for (int i = 2; i < clocks.Length; i++)
        {
            double d = clocks[i - 2] - clocks[i];
            if (d > 0) drops.Add(d);
        }
        if (drops.Count == 0) return 0;
        drops.Sort();
        return drops[drops.Count / 2];
    }
}
