using System.Text;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.Tatoeba;

internal static class TatoebaRowFilter
{
    public static bool MatchesSentenceLanguageFilter(ReadOnlySpan<byte> line, LanguageFilter langs)
    {
        if (!langs.IsActive) return true;
        if (!TsvSpan.TryField(line, 1, out var langField)) return false;
        string lang = Encoding.UTF8.GetString(langField).Trim();
        return langs.MatchesRaw(lang);
    }

    public static bool MatchesLinkFilter(ReadOnlySpan<byte> line, HashSet<long>? allowedIds)
    {
        if (allowedIds is null) return true;
        if (!TsvSpan.TryField(line, 0, out var aField)) return false;
        if (!TsvSpan.TryField(line, 1, out var bField)) return false;
        if (!TryParseInt64(aField, out long a)) return false;
        if (!TryParseInt64(bField, out long b)) return false;
        return allowedIds.Contains(a) && allowedIds.Contains(b);
    }

    private static bool TryParseInt64(ReadOnlySpan<byte> s, out long v)
    {
        v = 0;
        if (s.IsEmpty) return false;
        for (int i = 0; i < s.Length; i++)
        {
            byte c = s[i];
            if (c < (byte)'0' || c > (byte)'9') return false;
            v = checked(v * 10 + (c - (byte)'0'));
        }
        return true;
    }
}
