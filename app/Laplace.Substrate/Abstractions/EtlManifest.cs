using Laplace.Engine.Core;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Abstractions;

public static class EtlManifest
{
    /// <summary>The same recipe estate used by the native Engine.</summary>
    public static string RecipesRoot
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("LAPLACE_RECIPES");
            if (string.IsNullOrWhiteSpace(configured))
            {
                var bundled = Path.Combine(AppContext.BaseDirectory, "native", "recipe-engine", "share", "laplace", "recipes");
                var workspace = Environment.GetEnvironmentVariable("LAPLACE_SRC");
                if (File.Exists(Path.Combine(bundled, "order"))) configured = bundled;
                else if (!string.IsNullOrWhiteSpace(workspace))
                    configured = Path.Combine(workspace, "Laplace-Engine", "recipes");
                else if (LaplaceInstall.TryRepoRoot(out var repository))
                    configured = Path.Combine(Path.GetDirectoryName(repository)!, "Laplace-Engine", "recipes");
                else
                    configured = Path.Combine(AppContext.BaseDirectory, "recipes");
            }
            var root = Path.GetFullPath(configured);
            if (!File.Exists(Path.Combine(root, "order")))
                throw new InvalidOperationException($"Native recipe estate has no order declaration: {root}. Declare LAPLACE_RECIPES.");
            return root;
        }
    }

    public static IReadOnlyList<string> SourceOrder => Catalog.Sources.Select(source => source.Name).ToArray();
    public static NativeIngestCatalog Catalog => NativeIngestCatalog.Read(RecipesRoot);

    /// <summary>Validate the declared topological order, including each source's after edges.</summary>
    public static IReadOnlyList<string> ReadSourceOrder(string recipeRoot) => NativeIngestCatalog.ReadSourceOrder(recipeRoot);

    /// <summary>Normalize historical input spellings before selecting a Native source.</summary>
    public static string CanonicalSourceName(string source) => Catalog.Resolve(source);

    private static Hash128 Src(string name) => Hash128.Text($"substrate/source/{name}/v1");

    private static EtlSource Row(
        string name, string decomposerName, int layer, string trustClass,
        string dataKey, EtlModality modality, EdgeRule[]? edges = null,
        AnchorResolver anchor = AnchorResolver.None, string? glob = null,
        string[]? bootstrapRelations = null, bool acceptCommentRows = true,
        Func<string, Hash128?>? contextIdFromFile = null, bool requireIliMap = false,
        bool hasDedicatedDecomposer = false, string? languageScope = null) =>
        new(
            Name: decomposerName,
            SourceId: Src(decomposerName),
            Layer: layer,
            TrustClassId: TrustClassRegistry.Id(trustClass),
            Trust: TC.ForClassName(trustClass),
            DataKey: dataKey,
            Modality: modality,
            NodeEdgeMap: edges ?? Array.Empty<EdgeRule>(),
            Anchor: anchor,
            Glob: glob,
            BootstrapRelations: bootstrapRelations,
            AcceptCommentRows: acceptCommentRows,
            ContextIdFromFile: contextIdFromFile,
            RequireIliMap: requireIliMap,
            HasDedicatedDecomposer: hasDedicatedDecomposer,
            LanguageScope: languageScope);

    private static readonly Lazy<Dictionary<string, EtlSource>> _rows = new(Build);

    public static EtlSource Get(string cliName) =>
        _rows.Value.TryGetValue(CanonicalSourceName(cliName), out var r)
            ? r
            : throw new KeyNotFoundException($"no EtlManifest row for source '{cliName}'");

    public static bool TryGet(string cliName, out EtlSource src) => _rows.Value.TryGetValue(CanonicalSourceName(cliName), out src!);

    public static IReadOnlyCollection<string> Names => _rows.Value.Keys;

    private static Dictionary<string, EtlSource> Build()
    {
        var tsv = new EtlModality("tsv");
        var json = new EtlModality("json");
        var psv = new EtlModality("psv");

        var m = new Dictionary<string, EtlSource>(StringComparer.OrdinalIgnoreCase)
        {



            ["unicode"] = Row("unicode", "Unicode", 0, "StandardsDerived",
                "unicode", new EtlModality("ucd", GrammarReady: false)),
            ["iso-639"] = Row("iso-639", "ISO639Decomposer", 1, "StandardsDerived",
                "iso639", new EtlModality("tsv", GrammarReady: false)),



            // atomic2020 + conceptnet are triple sources pinned to their lean
            // RelationTripleDecomposerBase classes. EtlManifest is descriptive
            // source/parser metadata only; executable declarative ingestion lives
            // in SourceGenerationRecipe.

            ["tatoeba"] = Row("tatoeba", "TatoebaDecomposer", 2, "StructuredCorpus",
                "tatoeba", new EtlModality("tsv", Glob: "*.csv", RecordFraming: GrammarRecordFraming.Line),
                bootstrapRelations: TatoebaBootstrap, hasDedicatedDecomposer: true),


            ["wiktionary"] = Row("wiktionary", "WiktionaryDecomposer", 2, "AcademicCuratedWithUserInput",
                "wiktionary", new EtlModality("json", Glob: "*.json*", RecordFraming: GrammarRecordFraming.Line),
                bootstrapRelations: WiktionaryBootstrap, hasDedicatedDecomposer: true),


            ["universal-dependencies"] = Row("universal-dependencies", "UD", 2, "AcademicCurated",
                "ud", new EtlModality("conllu", Glob: "*.conllu", GrammarReady: false),
                bootstrapRelations: UdBootstrap),


            ["opensubtitles"] = Row("opensubtitles", "OpenSubtitlesDecomposer", 2, "StructuredCorpus",
                "opensubtitles", new EtlModality("tsv", GrammarReady: false),
                bootstrapRelations: new[] { "IS_TRANSLATION_OF", "HAS_LANGUAGE" }),



            ["code"] = Row("code", "CodeDecomposer", 2, "StructuredCorpus",
                "code", new EtlModality("code", GrammarReady: false)),
            ["repo"] = Row("repo", "RepoDecomposer", 2, "StructuredCorpus",
                "repo", new EtlModality("code", GrammarReady: false)),
            ["tabular"] = Row("tabular", "TabularDecomposer", 2, "StructuredCorpus",
                "tabular", new EtlModality("csv", GrammarReady: false, RecordFraming: GrammarRecordFraming.Line)),
            ["tiny-codes"] = Row("tiny-codes", "TinyCodesDecomposer", 2, "StructuredCorpus",
                "tiny-codes", new EtlModality("json", GrammarReady: false)),
            ["stack"] = Row("stack", "StackDecomposer", 2, "StructuredCorpus",
                "stack", new EtlModality("code", GrammarReady: false)),
            ["document"] = Row("document", "DocumentDecomposer", 2, "StructuredCorpus",
                "document", new EtlModality("text", GrammarReady: false)),



            ["open-multilingual-wordnet"] = Row("open-multilingual-wordnet", "OMWDecomposer", 1, "AcademicCurated",
                "omw", new EtlModality("tsv", Glob: "*.tab", RecordFraming: GrammarRecordFraming.Line),
                anchor: AnchorResolver.IliSynset, acceptCommentRows: false,
                bootstrapRelations: OmwBootstrap, requireIliMap: true, hasDedicatedDecomposer: true),



            // languageScope "eng" below: these sources are monolingual English by
            // construction and each asserts it implicitly. CILI and MapNet are NOT scoped —
            // an ILI concept and a cross-resource mapping are language-neutral, and
            // stamping them English would attest something the source does not say.
            ["princeton-wordnet"] = Row("princeton-wordnet", "WordNetDecomposer", 2, "AcademicCurated",
                "wordnet", new EtlModality("wndb", GrammarReady: false), anchor: AnchorResolver.IliSynset,
                languageScope: "eng"),


            ["cili"] = Row("cili", "CILIDecomposer", 1, "AcademicCurated",
                "cili", new EtlModality("turtle", Glob: "*.ttl", GrammarReady: false),
                anchor: AnchorResolver.IliSynset),


            // FrameNet and PropBank are read through their recipes (recipes/framenet/1.7,
            // recipes/propbank/3.4) and named by their source generations.
            ["framenet"] = Row("framenet", "FrameNet", 3, "AcademicCurated",
                "framenet", new EtlModality("xml", Glob: "*.xml", GrammarReady: false),
                anchor: AnchorResolver.FrameCategory),
            ["propbank"] = Row("propbank", "PropBank", 2, "AcademicCurated",
                "propbank", new EtlModality("xml", Glob: "*.xml", GrammarReady: false),
                anchor: AnchorResolver.SenseKey),
            ["verbnet"] = Row("verbnet", "VerbNetDecomposer", 2, "AcademicCurated",
                "verbnet", new EtlModality("xml", Glob: "*.xml", GrammarReady: false),
                anchor: AnchorResolver.SenseKey, languageScope: "eng"),


            ["semlink"] = Row("semlink", "SemLinkDecomposer", 3, "AcademicCurated",
                "semlink", new EtlModality("json", Glob: "*.json", GrammarReady: false),
                anchor: AnchorResolver.IliSynset, languageScope: "eng"),
            ["mapnet"] = Row("mapnet", "MapNetDecomposer", 3, "AcademicCurated",
                "mapnet", new EtlModality("tsv", Glob: "*.tsv", GrammarReady: false,
                    RecordFraming: GrammarRecordFraming.Line),
                anchor: AnchorResolver.IliSynset),
            ["wordframenet"] = Row("wordframenet", "WordFrameNetDecomposer", 3, "AcademicCurated",
                "wordframenet", new EtlModality("text", GrammarReady: false),
                anchor: AnchorResolver.FrameCategory, languageScope: "eng"),
            // Native corpus identity is predicate-matrix; metadata remains available
            // to the provider adapters while their implementation is retired.
            ["predicate-matrix"] = Row("predicate-matrix", "PredicateMatrixDecomposer", 3, "AcademicCurated",
                "predicatematrix", new EtlModality("tsv", Glob: "*.txt", GrammarReady: false,
                    RecordFraming: GrammarRecordFraming.Line),
                anchor: AnchorResolver.IliSynset),
        };
        return m;
    }



    private static readonly string[] OmwBootstrap =
        { "HAS_DEFINITION", "HAS_EXAMPLE", "IS_SYNONYM_OF", "HAS_LANGUAGE", "HAS_POS" };

    // HAS_EXTERNAL_ID dropped 2026-07-28 with the surrogate anchors: Tatoeba no longer
    // attests its row numbers at all (they are ingest scaffolding resolved by TatoebaIdMap).
    // This list is the SECOND place the source's relations are declared — TatoebaSource.Relations
    // is the other — which is how it went stale while the first one was updated.
    private static readonly string[] TatoebaBootstrap =
        { "HAS_LANGUAGE", "IS_TRANSLATION_OF" };

    private static readonly string[] WiktionaryBootstrap =
    {
        "HAS_LANGUAGE", "HAS_POS", "HAS_DEFINITION", "HAS_EXAMPLE", "IS_SYNONYM_OF", "IS_ANTONYM_OF",
        "HAS_HYPONYM", "HAS_PART", "IS_PART_OF", "RELATED_TO", "HAS_HYPERNYM", "MANNER_OF",
        "DERIVED_FROM", "IS_COORDINATE_TERM_WITH", "HAS_USAGE_REGISTER", "TRANSCRIBES_AS",
        "IS_TRANSLATION_OF", "FORM_OF", "HAS_FEATURE", "HAS_ETYMOLOGY",
        "ETYMOLOGICALLY_DERIVED_FROM", "ETYMOLOGICALLY_RELATED_TO",
    };

    private static readonly string[] UdBootstrap =
        // DEPENDS_ON, not "HAS_DEPENDENCY" — the latter existed nowhere in the
        // repo (no manifest entry, no emitter; #1057 defect 5, phantom name).
        { "HAS_POS", "IS_LEMMA_OF", "HAS_FEATURE", "DEPENDS_ON" };
}
