using Laplace.Engine.Core;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Abstractions;

public static class EtlManifest
{
    private static Hash128 Src(string name) => Hash128.OfCanonical($"substrate/source/{name}/v1");
    private static Hash128 TrustClass(string cls) => Hash128.OfCanonical($"substrate/trust_class/{cls}/v1");

    private static EtlSource Row(
        string name, string decomposerName, int layer, string trustClass, double trust,
        string dataKey, EtlModality modality, EdgeRule[]? edges = null,
        AnchorResolver anchor = AnchorResolver.None, string? glob = null,
        string[]? bootstrapRelations = null, bool acceptCommentRows = true,
        Func<string, Hash128?>? contextIdFromFile = null, bool requireIliMap = false) =>
        new(
            Name: decomposerName,
            SourceId: Src(decomposerName),
            Layer: layer,
            TrustClassId: TrustClass(trustClass),
            Trust: trust,
            DataKey: dataKey,
            Modality: modality,
            NodeEdgeMap: edges ?? Array.Empty<EdgeRule>(),
            Anchor: anchor,
            Glob: glob,
            BootstrapRelations: bootstrapRelations,
            AcceptCommentRows: acceptCommentRows,
            ContextIdFromFile: contextIdFromFile,
            RequireIliMap: requireIliMap);

    private static readonly Dictionary<string, EtlSource> _rows = Build();

    public static EtlSource Get(string cliName) =>
        _rows.TryGetValue(cliName, out var r)
            ? r
            : throw new KeyNotFoundException($"no EtlManifest row for source '{cliName}'");

    public static bool TryGet(string cliName, out EtlSource src) => _rows.TryGetValue(cliName, out src!);

    public static bool IsRoutable(string cliName) => _rows.TryGetValue(cliName, out var r) && r.IsComplete;

    public static IReadOnlyCollection<string> Names => _rows.Keys;

    private static Dictionary<string, EtlSource> Build()
    {
        var tsv = new EtlModality("tsv");
        var json = new EtlModality("json");
        var psv = new EtlModality("psv");

        var m = new Dictionary<string, EtlSource>(StringComparer.OrdinalIgnoreCase)
        {



            ["unicode"] = Row("unicode", "UnicodeDecomposer", 0, "StandardsDerived", TC.StandardsDerived,
                "unicode", new EtlModality("ucd", GrammarReady: false)),
            ["iso639"] = Row("iso639", "ISO639Decomposer", 1, "StandardsDerived", TC.StandardsDerived,
                "iso639", new EtlModality("tsv", GrammarReady: false)),



            // atomic2020 + conceptnet are triple sources pinned to their lean
            // RelationTripleDecomposerBase classes (dispatched explicitly in
            // IngestCommands before the IsRoutable check) — they no longer route
            // through EtlDecomposer/grammar-compose, so they carry no manifest row.

            ["tatoeba"] = Row("tatoeba", "TatoebaDecomposer", 2, "StructuredCorpus", TC.StructuredCorpus,
                "tatoeba", new EtlModality("tsv", Glob: "*.csv", RecordFraming: GrammarRecordFraming.Line),
                bootstrapRelations: TatoebaBootstrap),


            ["wiktionary"] = Row("wiktionary", "WiktionaryDecomposer", 2, "AcademicCuratedUserInput", TC.AcademicCuratedUserInput,
                "wiktionary", new EtlModality("json", Glob: "*.json*", RecordFraming: GrammarRecordFraming.Line),
                bootstrapRelations: WiktionaryBootstrap),


            ["ud"] = Row("ud", "UDDecomposer", 2, "AcademicCurated", TC.AcademicCurated,
                "ud", new EtlModality("conllu", Glob: "*.conllu", GrammarReady: false),
                bootstrapRelations: UdBootstrap),


            ["opensubtitles"] = Row("opensubtitles", "OpenSubtitlesDecomposer", 2, "StructuredCorpus", TC.StructuredCorpus,
                "opensubtitles", new EtlModality("tsv", GrammarReady: false),
                bootstrapRelations: new[] { "IS_TRANSLATION_OF", "HAS_LANGUAGE" }),



            ["code"] = Row("code", "CodeDecomposer", 2, "StructuredCorpus", TC.StructuredCorpus,
                "code", new EtlModality("code", GrammarReady: false)),
            ["repo"] = Row("repo", "RepoDecomposer", 2, "StructuredCorpus", TC.StructuredCorpus,
                "repo", new EtlModality("code", GrammarReady: false)),
            ["tabular"] = Row("tabular", "TabularDecomposer", 2, "StructuredCorpus", TC.StructuredCorpus,
                "tabular", new EtlModality("csv", GrammarReady: false, RecordFraming: GrammarRecordFraming.Line)),
            ["tiny-codes"] = Row("tiny-codes", "TinyCodesDecomposer", 2, "StructuredCorpus", TC.StructuredCorpus,
                "tiny-codes", new EtlModality("json", GrammarReady: false)),
            ["stack"] = Row("stack", "StackDecomposer", 2, "StructuredCorpus", TC.StructuredCorpus,
                "stack", new EtlModality("code", GrammarReady: false)),
            ["document"] = Row("document", "DocumentDecomposer", 2, "StructuredCorpus", TC.StructuredCorpus,
                "document", new EtlModality("text", GrammarReady: false)),



            ["omw"] = Row("omw", "OMWDecomposer", 3, "AcademicCurated", TC.AcademicCurated,
                "omw", new EtlModality("tsv", Glob: "*.tab", RecordFraming: GrammarRecordFraming.Line),
                anchor: AnchorResolver.IliSynset, acceptCommentRows: false,
                bootstrapRelations: OmwBootstrap, requireIliMap: true),



            ["wordnet"] = Row("wordnet", "WordNetDecomposer", 2, "AcademicCurated", TC.AcademicCurated,
                "wordnet", new EtlModality("wndb", GrammarReady: false), anchor: AnchorResolver.IliSynset),


            ["cili"] = Row("cili", "CILIDecomposer", 2, "AcademicCurated", TC.AcademicCurated,
                "cili", new EtlModality("turtle", Glob: "*.ttl", GrammarReady: false),
                anchor: AnchorResolver.IliSynset),


            ["framenet"] = Row("framenet", "FrameNetDecomposer", 3, "AcademicCurated", TC.AcademicCurated,
                "framenet", new EtlModality("xml", Glob: "*.xml", GrammarReady: false),
                anchor: AnchorResolver.FrameCategory),
            ["propbank"] = Row("propbank", "PropBankDecomposer", 2, "AcademicCurated", TC.AcademicCurated,
                "propbank", new EtlModality("xml", Glob: "*.xml", GrammarReady: false),
                anchor: AnchorResolver.SenseKey),
            ["verbnet"] = Row("verbnet", "VerbNetDecomposer", 2, "AcademicCurated", TC.AcademicCurated,
                "verbnet", new EtlModality("xml", Glob: "*.xml", GrammarReady: false),
                anchor: AnchorResolver.SenseKey),


            ["semlink"] = Row("semlink", "SemLinkDecomposer", 3, "AcademicCurated", TC.AcademicCurated,
                "semlink", new EtlModality("json", Glob: "*.json", GrammarReady: false),
                anchor: AnchorResolver.IliSynset),
            ["mapnet"] = Row("mapnet", "MapNetDecomposer", 3, "AcademicCurated", TC.AcademicCurated,
                "mapnet", new EtlModality("tsv", Glob: "*.tsv", GrammarReady: false,
                    RecordFraming: GrammarRecordFraming.Line),
                anchor: AnchorResolver.IliSynset),
            ["wordframenet"] = Row("wordframenet", "WordFrameNetDecomposer", 3, "AcademicCurated", TC.AcademicCurated,
                "wordframenet", new EtlModality("text", GrammarReady: false),
                anchor: AnchorResolver.FrameCategory),
            ["predicatematrix"] = Row("predicatematrix", "PredicateMatrixDecomposer", 3, "AcademicCurated", TC.AcademicCurated,
                "predicatematrix", new EtlModality("tsv", Glob: "*.txt", GrammarReady: false,
                    RecordFraming: GrammarRecordFraming.Line),
                anchor: AnchorResolver.IliSynset),
        };
        return m;
    }



    private static readonly string[] OmwBootstrap =
        { "HAS_DEFINITION", "HAS_EXAMPLE", "IS_SYNONYM_OF", "HAS_LANGUAGE", "HAS_POS" };

    private static readonly string[] TatoebaBootstrap =
        { "HAS_EXTERNAL_ID", "HAS_LANGUAGE", "IS_TRANSLATION_OF" };

    private static readonly string[] WiktionaryBootstrap =
    {
        "HAS_LANGUAGE", "HAS_POS", "HAS_DEFINITION", "HAS_EXAMPLE", "IS_SYNONYM_OF", "IS_ANTONYM_OF",
        "HAS_HYPONYM", "HAS_PART", "IS_PART_OF", "RELATED_TO", "HAS_HYPERNYM", "MANNER_OF",
        "DERIVED_FROM", "IS_COORDINATE_TERM_WITH", "HAS_USAGE_REGISTER", "TRANSCRIBES_AS",
        "IS_TRANSLATION_OF", "FORM_OF", "HAS_FEATURE", "HAS_ETYMOLOGY",
        "BORROWED_FROM", "INHERITED_FROM", "ETYMOLOGICALLY_DERIVED_FROM", "ETYMOLOGICALLY_RELATED_TO",
    };

    private static readonly string[] UdBootstrap =
        { "HAS_POS", "IS_LEMMA_OF", "HAS_FEATURE", "HAS_DEPENDENCY" };
}
