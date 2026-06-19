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
        if (!TatoebaParse.TryInt64(aField, out long a)) return false;
        if (!TatoebaParse.TryInt64(bField, out long b)) return false;
        return allowedIds.Contains(a) && allowedIds.Contains(b);
    }
}
