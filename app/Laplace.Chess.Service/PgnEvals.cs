using System.Globalization;
using System.Text.RegularExpressions;

namespace Laplace.Chess.Service;

internal static partial class PgnEvals
{
    [GeneratedRegex(@"\[%eval\s+([^\]]+)\]")]
    private static partial Regex EvalRegex();

    public static string[]? EvalTokens(string gameText, int moveCount)
    {
        var ms = EvalRegex().Matches(gameText);
        if (ms.Count == 0 || ms.Count != moveCount) return null;
        var outv = new string[moveCount];
        for (int i = 0; i < moveCount; i++)
            outv[i] = ChessCanonical.EvalToken(ms[i].Groups[1].Value)
                ?? throw new InvalidOperationException("empty eval token");
        return outv;
    }

    public static int[]? Centipawns(string gameText, int moveCount)
    {
        var tokens = EvalTokens(gameText, moveCount);
        if (tokens is null) return null;
        var outv = new int[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
            outv[i] = ParseToken(tokens[i]);
        return outv;
    }

    internal static int ParseToken(string token)
    {
        if (token.StartsWith('#'))
        {
            bool neg = token.Contains('-');
            string digits = new string(token.Where(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out int mateIn) || mateIn <= 0) mateIn = 1;
            int mag = 20_000 - mateIn * 100;
            return neg ? -mag : mag;
        }

        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
        {
            if (Math.Abs(val) < 50 && token.Contains('.'))
                return (int)Math.Round(val * 100.0);
            return (int)Math.Round(val);
        }
        return 0;
    }

    internal static long EvalSumFp1e9(int cp, long games)
    {
        double p = 1.0 / (1.0 + Math.Exp(-cp / 400.0));
        return checked((long)Math.Round(p * 1_000_000_000L * games));
    }
}
