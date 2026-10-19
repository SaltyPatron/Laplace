using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.WordNet;

public readonly struct WordNetSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("WordNetDecomposer");

    public static string SourceName => "WordNetDecomposer";

    public static Hash128 TrustClass { get; } =
        TrustClassRegistry.Id("StandardsDerived");

    // wndb pointer symbols. The symbol is the predicate the data file wrote.
    public static readonly HashSet<string> PointerSymbols = new(StringComparer.Ordinal)
    {
        "!", "@", "@i", "~", "~i", "#m", "#s", "#p", "%m", "%s", "%p",
        "=", "+", ";c", "-c", ";r", "-r", ";u", "-u", "*", ">", "^", "$", "&", "<", "\\",
    };

    public static IReadOnlyList<string> Relations { get; } = BuildRelations();

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["WordNet_Synset", "WordNet_Sense", "Source_Reference"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.WordNet;

    private static IReadOnlyList<string> BuildRelations()
    {
        var set = new HashSet<string>(StringComparer.Ordinal)
        {
            "HAS_POS", "Sense", "gloss", "example", "lex_filenum", "lemma",
        };
        foreach (string symbol in PointerSymbols)
            set.Add(symbol);
        foreach (var name in EtlSource.LanguageScopeRelations)
            set.Add(name);
        return set.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }
}
