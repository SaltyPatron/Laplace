namespace Laplace.Decomposers.Abstractions;






public sealed class LanguageFilter
{
    private readonly HashSet<string> _canon;

    private LanguageFilter(HashSet<string> canon) => _canon = canon;

    public bool IsActive => _canon.Count > 0;


    public static LanguageFilter? ForSource(string sourceKey) => null;

    public static LanguageFilter FromSpec(string commaSeparated)
    {
        LanguageReference.EnsureLoaded();
        var canon = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = new List<string>();
        foreach (var part in commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? c = LanguageReference.ResolveCode(part);
            if (c is not null) canon.Add(c);
            else unresolved.Add(part);
        }
        if (unresolved.Count > 0)
            throw new ArgumentException(
                $"LanguageFilter: unresolvable language(s) in '{commaSeparated}': {string.Join(", ", unresolved)}");
        return new LanguageFilter(canon);
    }

    public bool MatchesRaw(string? rawLangCode)
    {
        if (!IsActive) return true;
        if (string.IsNullOrWhiteSpace(rawLangCode)) return false;
        string? c = LanguageReference.ResolveCode(rawLangCode);
        return c is not null && _canon.Contains(c);
    }


    public bool MatchesAll(params string?[] rawLangCodes)
    {
        if (!IsActive) return true;
        foreach (var raw in rawLangCodes)
            if (!MatchesRaw(raw)) return false;
        return true;
    }

    public bool MatchesAllUtf8(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        if (!IsActive) return true;
        return MatchesRawUtf8(first) && MatchesRawUtf8(second);
    }

    private bool MatchesRawUtf8(ReadOnlySpan<byte> rawLangCode)
    {
        if (!IsActive) return true;
        if (rawLangCode.IsEmpty) return false;
        string? c = rawLangCode.Length <= 8
            ? LanguageReference.ResolveCode(Utf8ToString(rawLangCode))
            : LanguageReference.ResolveCode(System.Text.Encoding.UTF8.GetString(rawLangCode));
        return c is not null && _canon.Contains(c);
    }

    private static string Utf8ToString(ReadOnlySpan<byte> utf8)
    {
        Span<char> chars = stackalloc char[8];
        int n = System.Text.Encoding.UTF8.GetChars(utf8, chars);
        return new string(chars[..n]);
    }


    public bool MatchesAny(params string?[] rawLangCodes)
    {
        if (!IsActive) return true;
        foreach (var raw in rawLangCodes)
            if (MatchesRaw(raw)) return true;
        return false;
    }


    public bool MatchesUdTreebankFile(string conlluFileName)
    {
        if (!IsActive) return true;
        int under = conlluFileName.IndexOf('_');
        string baseLang = under > 0 ? conlluFileName[..under] : conlluFileName;
        int dot = baseLang.IndexOf('.');
        if (dot > 0) baseLang = baseLang[..dot];
        return MatchesRaw(baseLang);
    }


    public bool MatchesLanguagePair(string pairToken)
    {
        if (!IsActive) return true;
        if (string.IsNullOrWhiteSpace(pairToken)) return false;
        var parts = pairToken.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return MatchesRaw(parts.Length == 1 ? parts[0] : null);
        return MatchesAny(parts[0], parts[1]);
    }
}
