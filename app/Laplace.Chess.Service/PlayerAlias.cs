using System.Text.RegularExpressions;

namespace Laplace.Chess.Service;

/// <summary>Canonical player-name normalization for stable <see cref="ChessVocabulary.PlayerId"/> hashing.</summary>
internal static partial class PlayerAlias
{
    [GeneratedRegex(@"[^\p{L}\p{N}\s]+")]
    private static partial Regex PunctRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();

    /// <summary>Lowercase, strip punctuation, <c>"Last, First"</c> → <c>"first last"</c>.</summary>
    public static string Canonical(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        int comma = s.IndexOf(',');
        if (comma >= 0)
        {
            string last = s[..comma].Trim();
            string first = s[(comma + 1)..].Trim();
            s = $"{first} {last}";
        }
        s = s.ToLowerInvariant();
        s = PunctRegex().Replace(s, " ");
        s = SpaceRegex().Replace(s, " ").Trim();
        return s;
    }
}
