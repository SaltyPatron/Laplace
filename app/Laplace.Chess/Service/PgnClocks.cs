using System.Text.RegularExpressions;

namespace Laplace.Chess.Service;

internal static partial class PgnClocks
{
    // Two clock dialects, two quantities. Lichess [%clk H:M:S] carries time REMAINING —
    // SecondsRemaining/ThinkFactor diff consecutive readings to recover per-move think time.
    // cutechess-cli comments ("{+0.48/17 0.13s}") carry per-move time SPENT directly — the
    // very quantity the remaining-clock dance exists to reconstruct — so SpentSeconds hands
    // it straight to ThinkFactorFromSpent. No synthetic remaining-clock series is ever
    // fabricated from spent time (it would mint a quantity the source never asserted); the
    // spent dialect feeds think-class only, never HAS_CLOCK deposits.
    [GeneratedRegex(@"\[%clk\s+(\d+):(\d+):(\d+(?:\.\d+)?)\]")]
    private static partial Regex ClkRegex();

    [GeneratedRegex(@"\{[^{}]*?(\d+(?:\.\d+)?)s\}")]
    private static partial Regex SpentRegex();

    public static double[] SecondsRemaining(string gameText, int moveCount)
    {
        var tokens = ClockTokens(gameText, moveCount);
        if (tokens is null) return Array.Empty<double>();
        var outv = new double[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
            outv[i] = TryParseHms(tokens[i], out double sec) ? sec : 0;
        return outv;
    }

    /// <summary>
    /// Parse a remaining-clock token <c>H:M:S</c>. Returns false on malformed
    /// fields instead of throwing — one bad <c>%clk</c> must not abort a corpus
    /// run (CONSOLIDATION §3.7 / same class as #596).
    /// </summary>
    public static bool TryParseHms(string? token, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split(':');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out int h)) return false;
        if (!int.TryParse(parts[1], out int m)) return false;
        if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double s))
            return false;
        seconds = h * 3600 + m * 60 + s;
        return true;
    }

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

    /// <summary>Per-move seconds SPENT from cutechess-style comments; null unless every ply has one.</summary>
    public static double[]? SpentSeconds(string gameText, int moveCount)
    {
        var ms = SpentRegex().Matches(gameText);
        if (ms.Count == 0 || ms.Count != moveCount) return null;
        var outv = new double[moveCount];
        for (int i = 0; i < moveCount; i++)
        {
            if (!double.TryParse(ms[i].Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out outv[i]))
                return null;
        }
        return outv;
    }

    public static double MedianSpent(double[]? spent)
    {
        if (spent is null || spent.Length < 3) return 0;
        var positive = spent.Where(s => s > 0).ToList();
        if (positive.Count == 0) return 0;
        positive.Sort();
        return positive[positive.Count / 2];
    }

    /// <summary>Same clamp semantics as ThinkFactor, but on directly-witnessed spent time.</summary>
    public static double ThinkFactorFromSpent(double[] spent, double medianSpent, int i)
    {
        if (spent.Length == 0 || i >= spent.Length || medianSpent <= 0) return 1.0;
        if (spent[i] <= 0) return 0.5;
        return Math.Clamp(spent[i] / medianSpent, 0.5, 1.5);
    }

    public static double ThinkFactor(double[] clocks, double medianDrop, int i)
    {
        if (clocks.Length == 0 || i < 2 || medianDrop <= 0) return 1.0;
        double drop = clocks[i - 2] - clocks[i];
        if (drop <= 0) return 0.5;
        double f = drop / medianDrop;
        return Math.Clamp(f, 0.5, 1.5);
    }

    /// <summary>
    /// Median remaining clock for the plies of one side (parity = ply index mod 2 —
    /// same-parity plies belong to one player regardless of who moved first), from the
    /// game's OWN remaining-time series. This is the lens threshold for "low clock":
    /// under a monotone-decaying clock it splits the game at the point where half the
    /// player's time story is behind them — adaptive to the control, no constants.
    /// 0 when the dialect carries no remaining clock (cutechess spent-only games).
    /// </summary>
    public static double MedianRemaining(double[] clocks, int parity)
    {
        if (clocks.Length == 0) return 0;
        var side = new List<double>(clocks.Length / 2 + 1);
        for (int i = parity; i < clocks.Length; i += 2)
            if (clocks[i] > 0) side.Add(clocks[i]);
        if (side.Count == 0) return 0;
        side.Sort();
        return side[side.Count / 2];
    }

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
