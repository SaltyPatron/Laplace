using System.Text;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.ConceptNet;

internal static class ConceptNetRowFilter
{
    public static bool MatchesLanguageFilter(ReadOnlySpan<byte> line, LanguageFilter langs)
    {
        if (!langs.IsActive) return true;
        if (!TsvSpan.TryField(line, 2, out var startUri)) return false;
        if (!TsvSpan.TryField(line, 3, out var endUri)) return false;
        if (!ConceptNetUri.TryParseLangAndTerm(startUri, out var startLang, out _)) return false;
        if (!ConceptNetUri.TryParseLangAndTerm(endUri, out var endLang, out _)) return false;
        return langs.MatchesAllUtf8(startLang, endLang);
    }

    public static bool MatchesLanguageFilter(string line, LanguageFilter langs)
    {
        if (!langs.IsActive) return true;
        return MatchesLanguageFilter(Encoding.UTF8.GetBytes(line), langs);
    }
}
