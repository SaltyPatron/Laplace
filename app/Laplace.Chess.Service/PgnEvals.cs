using System.Globalization;
using System.Text.RegularExpressions;

namespace Laplace.Chess.Service;

internal static partial class PgnEvals
{
    [GeneratedRegex(@"\[%eval\s+([^\]]+)\]")]
    private static partial Regex EvalRegex();

    // cutechess-cli's own PGN comments (produced by CutechessRunner/ChessLabRunners self-play/lab
    // tournaments) never use the lichess [%eval ...] annotation — they're bare "{+0.48/17 0.13s}"
    // (eval/depth time-spent), or "{+M3/12 0.05s}" for a mate score (MoveEvaluation::scoreText in
    // the vendored cutechess source), so EvalRegex alone silently finds zero matches for that
    // entire source. Confirmed against a real cutechess-cli match built from the vendored submodule.
    [GeneratedRegex(@"\{\s*([+-]?(?:\d+\.\d+|M\d+))/\d+\s")]
    private static partial Regex CutechessEvalRegex();

    // Normalizes cutechess's "+M3"/"-M3" mate notation to lichess/ParseToken's "#"-prefixed form
    // ("#+3"/"#-3") so both formats flow through the same ParseToken mate branch unchanged.
    private static string NormalizeCutechessToken(string raw)
    {
        int m = raw.IndexOf('M');
        return m < 0 ? raw : "#" + raw[..m] + raw[(m + 1)..];
    }

    public static string[]? EvalTokens(string gameText, int moveCount)
    {
        var ms = EvalRegex().Matches(gameText);
        if (ms.Count == 0 || ms.Count != moveCount)
        {
            var cc = CutechessEvalRegex().Matches(gameText);
            if (cc.Count == 0 || cc.Count != moveCount) return null;
            var ccOut = new string[moveCount];
            for (int i = 0; i < moveCount; i++)
                ccOut[i] = ChessCanonical.EvalToken(NormalizeCutechessToken(cc[i].Groups[1].Value))
                    ?? throw new InvalidOperationException("empty eval token");
            return ccOut;
        }
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
