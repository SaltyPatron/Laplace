using System.Globalization;
using System.Text.RegularExpressions;

namespace Laplace.Chess.Service;

/// <summary>
/// Extracts per-move engine evaluations (<c>{[%eval …]}</c>) from raw PGN movetext, ALIGNED to the
/// grammar's ordered SAN moves — mirrors <see cref="PgnClocks"/>. Skips when the count doesn't line up
/// 1:1 with <paramref name="moveCount"/> (don't guess).
/// </summary>
internal static partial class PgnEvals
{
    [GeneratedRegex(@"\[%eval\s+([^\]]+)\]")]
    private static partial Regex EvalRegex();

    /// <summary>Centipawn eval from WHITE's perspective per move, or null when absent/misaligned.</summary>
    public static int[]? Centipawns(string gameText, int moveCount)
    {
        var ms = EvalRegex().Matches(gameText);
        if (ms.Count == 0 || ms.Count != moveCount) return null;
        var outv = new int[moveCount];
        for (int i = 0; i < moveCount; i++)
            outv[i] = ParseToken(ms[i].Groups[1].Value.Trim());
        return outv;
    }

    /// <summary>
    /// Parse a Lichess-style eval token: decimal pawns (<c>0.35</c> → 35cp), integer cp, or mate
    /// (<c>#-3</c> → large negative score).
    /// </summary>
    internal static int ParseToken(string token)
    {
        if (token.StartsWith('#'))
        {
            bool neg = token.Contains('-');
            string digits = new string(token.Where(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out int mateIn) || mateIn <= 0) mateIn = 1;
            // Mate scores beyond normal eval range; sign = winning side (negative = Black winning).
            int mag = 20_000 - mateIn * 100;
            return neg ? -mag : mag;
        }

        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
        {
            // Lichess exports decimal pawns for small values; large integers are already cp.
            if (Math.Abs(val) < 50 && token.Contains('.'))
                return (int)Math.Round(val * 100.0);
            return (int)Math.Round(val);
        }
        return 0;
    }

    /// <summary>
    /// Map centipawns (side-to-move perspective) to a Glicko sum score around 0.5 via sigmoid:
    /// <c>sum = games × sigmoid(cp / 400) × 1e9</c>.
    /// </summary>
    internal static long EvalSumFp1e9(int cp, long games)
    {
        double p = 1.0 / (1.0 + Math.Exp(-cp / 400.0));
        return checked((long)Math.Round(p * 1_000_000_000L * games));
    }
}
