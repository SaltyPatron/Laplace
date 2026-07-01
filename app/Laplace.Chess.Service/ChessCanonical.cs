using System.Globalization;

namespace Laplace.Chess.Service;

/// <summary>
/// Canonical surfaces for chess metadata so identical values dedupe through
/// <see cref="Decomposers.Abstractions.ContentEmitter"/> (unicode codepoint ladder).
/// </summary>
public static class ChessCanonical
{
    /// <summary>Normalize PGN <c>[%clk H:MM:SS]</c> to <c>H:MM:SS</c> (zero-padded minutes/seconds).</summary>
    public static string? ClockFromSeconds(double secondsRemaining)
    {
        if (secondsRemaining < 0 || double.IsNaN(secondsRemaining)) return null;
        int total = (int)Math.Floor(secondsRemaining);
        int h = total / 3600;
        int m = (total % 3600) / 60;
        int s = total % 60;
        return $"{h}:{m:D2}:{s:D2}";
    }

    /// <summary>Parse raw <c>[%clk …]</c> capture groups into canonical <c>H:MM:SS</c>.</summary>
    public static string? ClockFromMatch(string hours, string minutes, string seconds)
    {
        if (!int.TryParse(hours, out int h)) return null;
        if (!int.TryParse(minutes, out int m)) return null;
        if (!double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out double sec)) return null;
        return ClockFromSeconds(h * 3600 + m * 60 + sec);
    }

    /// <summary>Normalize eval token from PGN (trim; preserve mate notation).</summary>
    public static string? EvalToken(string raw)
    {
        var t = raw.Trim();
        return t.Length == 0 ? null : t;
    }

    /// <summary>Think-time class from <see cref="PgnClocks.ThinkFactor"/>.</summary>
    public static string ThinkClass(double thinkFactor) => thinkFactor switch
    {
        <= 0.75 => "rushed",
        >= 1.25 => "deep",
        _ => "normal",
    };

    /// <summary>ECO code: uppercase, trimmed.</summary>
    public static string? Eco(string raw)
    {
        var t = raw.Trim().ToUpperInvariant();
        return t.Length == 0 || t == "?" ? null : t;
    }

    /// <summary>Opening name: trimmed, collapse internal whitespace.</summary>
    public static string? OpeningName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return string.Join(' ', raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
