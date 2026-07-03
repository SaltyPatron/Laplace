using System.Globalization;

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
