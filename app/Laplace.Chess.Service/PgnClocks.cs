using System.Text.RegularExpressions;

namespace Laplace.Chess.Service;

internal static partial class PgnClocks
{
    [GeneratedRegex(@"\[%clk\s+(\d+):(\d+):(\d+(?:\.\d+)?)\]")]
    private static partial Regex ClkRegex();

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

    public static double ThinkFactor(double[] clocks, double medianDrop, int i)
    {
        if (clocks.Length == 0 || i < 2 || medianDrop <= 0) return 1.0;
        double drop = clocks[i - 2] - clocks[i];
        if (drop <= 0) return 0.5;
        double f = drop / medianDrop;
        return Math.Clamp(f, 0.5, 1.5);
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
