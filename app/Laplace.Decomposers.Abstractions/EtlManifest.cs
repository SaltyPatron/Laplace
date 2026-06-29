using Laplace.Engine.Core;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Sources as data. Every data source is a row here, not a class. The generic
/// <see cref="EtlDecomposer"/> consumes a row; the CLI looks one up by name with <see cref="Get"/>.
///
/// Rows fall into three kinds (mirroring the plan):
///  - pure field-role sources carry an <see cref="EtlSource.NodeEdgeMap"/> and zero code;
///  - already-grammar-conforming sources whose Transform exceeds field-role mapping name a bespoke
///    witness via <see cref="EtlWitnessFactory"/> (registered by their own project) and are the
///    parity oracle for the generic walker;
///  - concept-convergent sources name an <see cref="AnchorResolver"/> + the grammar they still need
///    (CILI turtle; FrameNet/PropBank/VerbNet xml) — those rows are marked
///    <see cref="EtlModality.GrammarReady"/> = false until Migrate wires the grammar.
/// </summary>
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

    /// <summary>Atomic2020 split context (train/dev/test) from the file stem.</summary>
    private static Hash128? AtomicSplit(string filePath)
    {
        string stem = Path.GetFileNameWithoutExtension(filePath);
        return stem.Length == 0 ? null : Hash128.OfCanonical($"atomic/split/{stem}");
    }

    // Keyed by the lowercase CLI alias (matches IngestCommands' switch + IngestDataPaths).
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
            // ---- Layer 0/1: structural standards (pure field-role / native readers) -------------
            // Unicode + ISO639 are bespoke native readers (UCD multi-file / ISO tables); modelled
            // as rows for completeness but routed via their own decomposers until Migrate folds them.
            ["unicode"] = Row("unicode", "UnicodeDecomposer", 0, "StandardsDerived", TC.StandardsDerived,
                "unicode", new EtlModality("ucd", GrammarReady: false)),
            ["iso639"] = Row("iso639", "ISO639Decomposer", 1, "StandardsDerived", TC.StandardsDerived,
                "iso639", new EtlModality("tsv", GrammarReady: false)),

            // ---- Layer 2: lexical / relational sources ------------------------------------------
            // ConceptNet: bespoke (URI parse + JSON meta + POS/synset bridges) -> witness factory.
            ["conceptnet"] = Row("conceptnet", "ConceptNetDecomposer", 2, "UserCuratedResource", TC.UserCuratedResource,
                "conceptnet", tsv with { RecordFraming = GrammarRecordFraming.Line }, anchor: AnchorResolver.IliSynset,
                bootstrapRelations: ConceptNetBootstrap),

            // Atomic2020: pure head/rel/tail triples -> declarative edge map (parity oracle for the
            // generic TSV walker). Three split files (train/dev/test) under the dir.
            ["atomic2020"] = Row("atomic2020", "Atomic2020Decomposer", 2, "StructuredCorpus", TC.StructuredCorpus,
                "atomic2020", tsv with { RecordFraming = GrammarRecordFraming.Line }, glob: "*.tsv",
                bootstrapRelations: AtomicBootstrap, contextIdFromFile: AtomicSplit),

            // Tatoeba: sentence + link files, external-id keyed -> bespoke witness factory.
            ["tatoeba"] = Row("tatoeba", "TatoebaDecomposer", 2, "StructuredCorpus", TC.StructuredCorpus,
                "tatoeba", new EtlModality("tsv", Glob: "*.csv", RecordFraming: GrammarRecordFraming.Line),
                bootstrapRelations: TatoebaBootstrap),

            // Wiktionary: JSONL with deep sense/etymology/form trees -> bespoke witness factory.
            ["wiktionary"] = Row("wiktionary", "WiktionaryDecomposer", 2, "AcademicCuratedUserInput", TC.AcademicCuratedUserInput,
                "wiktionary", new EtlModality("json", Glob: "*.json*", RecordFraming: GrammarRecordFraming.Line),
                bootstrapRelations: WiktionaryBootstrap),

            // UD CoNLL-U: reuses the psv/tsv row grammar; HEAD/DEPREL columns -> dependency edges.
            ["ud"] = Row("ud", "UDDecomposer", 2, "AcademicCurated", TC.AcademicCurated,
                "ud", new EtlModality("conllu", Glob: "*.conllu", GrammarReady: false),
                bootstrapRelations: UdBootstrap),

            // OpenSubtitles: aligned bitext -> translation links, bespoke alignment reader.
            ["opensubtitles"] = Row("opensubtitles", "OpenSubtitlesDecomposer", 2, "StructuredCorpus", TC.StructuredCorpus,
                "opensubtitles", new EtlModality("tsv", GrammarReady: false),
                bootstrapRelations: new[] { "IS_TRANSLATION_OF", "HAS_LANGUAGE" }),

            // Code / corpora — TreeSitter-grammar driven via their own decomposers (one grammar per
            // language extension), modelled as rows; modality glob selects the language grammar.
            ["code"]       = Row("code", "CodeDecomposer", 2, "StructuredCorpus", TC.StructuredCorpus,
                "code", new EtlModality("code", GrammarReady: false)),
            ["repo"]       = Row("repo", "RepoDecomposer", 2, "StructuredCorpus", TC.StructuredCorpus,
                "repo", new EtlModality("code", GrammarReady: false)),
            ["tabular"]    = Row("tabular", "TabularDecomposer", 2, "StructuredCorpus", TC.StructuredCorpus,
                "tabular", new EtlModality("csv", GrammarReady: false, RecordFraming: GrammarRecordFraming.Line)),
            ["tiny-codes"] = Row("tiny-codes", "TinyCodesDecomposer", 2, "StructuredCorpus", TC.StructuredCorpus,
                "tiny-codes", new EtlModality("json", GrammarReady: false)),
            ["stack"]      = Row("stack", "StackDecomposer", 2, "StructuredCorpus", TC.StructuredCorpus,
                "stack", new EtlModality("code", GrammarReady: false)),
            ["document"]   = Row("document", "DocumentDecomposer", 2, "StructuredCorpus", TC.StructuredCorpus,
                "document", new EtlModality("text", GrammarReady: false)),

            // ---- Layer 3: concept-convergent / cross-source bridges -----------------------------
            // OMW: ILI-anchored synset lexicalization (lemma/def/exe rows) -> bespoke witness factory.
            ["omw"] = Row("omw", "OMWDecomposer", 3, "AcademicCurated", TC.AcademicCurated,
                "omw", new EtlModality("tsv", Glob: "*.tab", RecordFraming: GrammarRecordFraming.Line),
                anchor: AnchorResolver.IliSynset, acceptCommentRows: false,
                bootstrapRelations: OmwBootstrap, requireIliMap: true),

            // WordNet: ILI synset spine (offset/ss_type) — bespoke multi-file reader; row for
            // completeness, wired by Migrate.
            ["wordnet"] = Row("wordnet", "WordNetDecomposer", 2, "AcademicCurated", TC.AcademicCurated,
                "wordnet", new EtlModality("wndb", GrammarReady: false), anchor: AnchorResolver.IliSynset),

            // CILI: ILI definitions/aliases — needs the turtle grammar (turtle uncompiled).
            ["cili"] = Row("cili", "CILIDecomposer", 2, "AcademicCurated", TC.AcademicCurated,
                "cili", new EtlModality("turtle", Glob: "*.ttl", GrammarReady: false),
                anchor: AnchorResolver.IliSynset),

            // FrameNet / PropBank / VerbNet — XML grammar uncompiled; frame/roleset/class anchors.
            ["framenet"] = Row("framenet", "FrameNetDecomposer", 3, "AcademicCurated", TC.AcademicCurated,
                "framenet", new EtlModality("xml", Glob: "*.xml", GrammarReady: false),
                anchor: AnchorResolver.FrameCategory),
            ["propbank"] = Row("propbank", "PropBankDecomposer", 2, "AcademicCurated", TC.AcademicCurated,
                "propbank", new EtlModality("xml", Glob: "*.xml", GrammarReady: false),
                anchor: AnchorResolver.SenseKey),
            ["verbnet"] = Row("verbnet", "VerbNetDecomposer", 2, "AcademicCurated", TC.AcademicCurated,
                "verbnet", new EtlModality("xml", Glob: "*.xml", GrammarReady: false),
                anchor: AnchorResolver.SenseKey),

            // SemLink / MapNet / WordFrameNet / PredicateMatrix — FN<->WN<->VN bridges (anchor rows).
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

    // Bootstrap relation sets mirror each decomposer's InitializeAsync so the generic path seeds the
    // same canonical vocabulary. (For factory-routed sources the witness emits these same edges.)
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

    private static readonly string[] ConceptNetBootstrap =
    {
        "HAS_EXAMPLE", "HAS_LANGUAGE", "HAS_POS", "CORRESPONDS_TO",
        "RELATED_TO", "FORM_OF", "IS_A", "IS_PART_OF", "HAS_A", "USED_FOR", "CAPABLE_OF",
        "AT_LOCATION", "CAUSES", "HAS_SUBEVENT", "HAS_FIRST_SUBEVENT", "HAS_LAST_SUBEVENT",
        "HAS_PREREQUISITE", "HAS_PROPERTY", "MOTIVATED_BY_GOAL", "OBSTRUCTED_BY", "DESIRES",
        "CREATED_BY", "IS_SYNONYM_OF", "IS_ANTONYM_OF", "DISTINCT_FROM", "DERIVED_FROM", "SYMBOL_OF",
        "DEFINED_AS", "MANNER_OF", "LOCATED_NEAR", "HAS_CONTEXT", "SIMILAR_TO",
        "ETYMOLOGICALLY_RELATED_TO", "ETYMOLOGICALLY_DERIVED_FROM", "CAUSES_DESIRE", "MADE_UP_OF",
        "RECEIVES_ACTION", "IS_INSTANCE_OF", "NOT_DESIRES", "NOT_USED_FOR", "NOT_CAPABLE_OF",
        "NOT_HAS_PROPERTY", "ENTAILS",
    };

    private static readonly string[] UdBootstrap =
        { "HAS_POS", "IS_LEMMA_OF", "HAS_FEATURE", "HAS_DEPENDENCY" };

    private static readonly string[] AtomicBootstrap =
    {
        "O_EFFECT", "O_REACT", "O_WANT", "X_ATTR", "X_EFFECT", "X_INTENT", "X_NEED", "X_REACT",
        "X_WANT", "X_REASON", "OBSTRUCTED_BY", "IS_AFTER", "IS_BEFORE", "X_FILLED_BY", "CAUSES",
        "OBJECT_USE", "AT_LOCATION", "HAS_SUBEVENT", "CAPABLE_OF", "DESIRES", "HAS_PROPERTY",
        "MADE_UP_OF", "NOT_DESIRES",
    };
}
