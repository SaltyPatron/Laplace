namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Witness-scope control: which languages to ingest from multilingual sources.
/// Null / inactive = all languages (full witness). Active = only resolved ISO 639-3 codes.
/// Same machinery, selective language scope — not source demotion.
/// </summary>
public sealed class LanguageFilter
{
    private readonly HashSet<string> _canon;

    private LanguageFilter(HashSet<string> canon) => _canon = canon;

    public bool IsActive => _canon.Count > 0;

    /// <summary>Resolve filter for a witness source. Per-source env overrides global.</summary>
    public static LanguageFilter? ForSource(string sourceKey)
    {
        string? spec = PerSourceEnv(sourceKey) ?? GlobalEnv();
        if (string.IsNullOrWhiteSpace(spec)) return null;
        return FromSpec(spec);
    }

    public static LanguageFilter FromSpec(string commaSeparated)
    {
        LanguageReference.EnsureLoaded();
        var canon = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? c = LanguageReference.ResolveCode(part);
            if (c is not null) canon.Add(c);
        }
        if (canon.Count == 0)
            throw new ArgumentException($"LanguageFilter: no resolvable languages in '{commaSeparated}'");
        return new LanguageFilter(canon);
    }

    public bool MatchesRaw(string? rawLangCode)
    {
        if (!IsActive) return true;
        if (string.IsNullOrWhiteSpace(rawLangCode)) return false;
        string? c = LanguageReference.ResolveCode(rawLangCode);
        return c is not null && _canon.Contains(c);
    }

    /// <summary>Every supplied language must resolve into the filter set.</summary>
    public bool MatchesAll(params string?[] rawLangCodes)
    {
        if (!IsActive) return true;
        foreach (var raw in rawLangCodes)
            if (!MatchesRaw(raw)) return false;
        return true;
    }

    /// <summary>At least one supplied language must resolve into the filter set.</summary>
    public bool MatchesAny(params string?[] rawLangCodes)
    {
        if (!IsActive) return true;
        foreach (var raw in rawLangCodes)
            if (MatchesRaw(raw)) return true;
        return false;
    }

    /// <summary>UD treebank files: en_ewt-ud-train.conllu → base lang en; filter includes dialect treebanks when base matches.</summary>
    public bool MatchesUdTreebankFile(string conlluFileName)
    {
        if (!IsActive) return true;
        int under = conlluFileName.IndexOf('_');
        string baseLang = under > 0 ? conlluFileName[..under] : conlluFileName;
        int dot = baseLang.IndexOf('.');
        if (dot > 0) baseLang = baseLang[..dot];
        return MatchesRaw(baseLang);
    }

    /// <summary>OpenSubtitles / bilingual pair token: en-es, en-zh_CN.</summary>
    public bool MatchesLanguagePair(string pairToken)
    {
        if (!IsActive) return true;
        if (string.IsNullOrWhiteSpace(pairToken)) return false;
        var parts = pairToken.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return MatchesRaw(parts.Length == 1 ? parts[0] : null);
        return MatchesAny(parts[0], parts[1]);
    }

    private static string? GlobalEnv() =>
        Environment.GetEnvironmentVariable("LAPLACE_INGEST_LANGS");

    private static string? PerSourceEnv(string sourceName)
    {
        string key = SourceEnvKey(sourceName);
        return Environment.GetEnvironmentVariable($"LAPLACE_{key}_LANGS")
            ?? Environment.GetEnvironmentVariable($"LAPLACE_INGEST_LANGS_{key}");
    }

    private static string SourceEnvKey(string sourceName)
    {
        const string suffix = "Decomposer";
        string s = sourceName.Trim();
        if (s.EndsWith(suffix, StringComparison.Ordinal))
            s = s[..^suffix.Length];
        return s.ToUpperInvariant().Replace('-', '_');
    }
}
