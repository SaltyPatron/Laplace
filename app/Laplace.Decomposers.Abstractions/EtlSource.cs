using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Named anchor resolver a source converges on. These map 1:1 onto the existing shared resolver
/// code (<see cref="ConceptAnchor"/>, <see cref="CategoryAnchor"/>, <see cref="SenseAnchor"/>,
/// <see cref="SourceEntityIdConventions"/>) — the manifest only names them, never reimplements them.
/// </summary>
public enum AnchorResolver
{
    /// <summary>No cross-source concept anchor — content ids stand alone.</summary>
    None,
    /// <summary>WordNet ILI synset via <see cref="ConceptAnchor"/> / <see cref="SourceEntityIdConventions.WordNetIli"/>.</summary>
    IliSynset,
    /// <summary>WordNet sense key via <see cref="SenseAnchor"/>.</summary>
    SenseKey,
    /// <summary>FrameNet frame/LU/class category key via <see cref="CategoryAnchor"/>.</summary>
    FrameCategory,
}

/// <summary>
/// How the source modality is selected for <see cref="StructuredGrammarIngest"/>: a compiled
/// grammar/recipe id ("tsv", "json", ...) and/or a file extension glob. A row whose
/// <see cref="GrammarReady"/> is false names a grammar that is not yet wired into
/// <c>grammar_registry.c</c> + <c>grammars/CMakeLists.txt</c> (turtle for CILI; xml for
/// FrameNet/PropBank/VerbNet); Migrate wires those, after which the row becomes complete.
/// </summary>
public readonly record struct EtlModality(string GrammarId, string? Glob = null, bool GrammarReady = true);

/// <summary>
/// One field-role-to-edge mapping rule for the generic TSV/PSV walker: the subject/object field
/// indices, the relation type name to emit, and whether the field value is content to emit
/// (default) or a key to resolve through the row's <see cref="AnchorResolver"/>.
/// </summary>
public readonly record struct EdgeRule(
    int SubjectField,
    int ObjectField,
    string RelationType,
    EdgeRoleKind SubjectKind = EdgeRoleKind.Content,
    EdgeRoleKind ObjectKind = EdgeRoleKind.Content);

public enum EdgeRoleKind
{
    /// <summary>The field value is surface text emitted as a content entity (its content id is used).</summary>
    Content,
    /// <summary>The field value is a key resolved to a shared anchor id via the row's resolver.</summary>
    Anchor,
}

/// <summary>
/// A data source expressed as data, not a class: where its files are, how they are parsed, which
/// edges its rows produce, and which shared anchor resolver (if any) converges its ids with other
/// sources. The generic <see cref="EtlDecomposer"/> + <see cref="EtlWitness"/> consume this; the
/// reading/batching/dedup/provenance all live in <see cref="StructuredGrammarIngest"/> and the
/// partitioned sink, so the source owns none of it.
/// </summary>
public sealed record EtlSource(
    string Name,
    Hash128 SourceId,
    int Layer,
    Hash128 TrustClassId,
    double Trust,
    string DataKey,
    EtlModality Modality,
    IReadOnlyList<EdgeRule> NodeEdgeMap,
    AnchorResolver Anchor = AnchorResolver.None,
    string? Glob = null,
    IReadOnlyList<string>? BootstrapRelations = null,
    bool AcceptCommentRows = true,
    /// <summary>
    /// Optional per-file context id (e.g. Atomic2020's train/dev/test split, derived from the file
    /// stem). When set, every edge a file's rows emit carries this context, exactly as the bespoke
    /// decomposer tags its splits. The path is the full file path; return null for no context.
    /// </summary>
    Func<string, Hash128?>? ContextIdFromFile = null,
    /// <summary>
    /// When true (and <see cref="Anchor"/> is <see cref="AnchorResolver.IliSynset"/>), a missing CILI
    /// ILI map is a hard error before ingest (OMW/WordNet/CILI require resolved synset anchors);
    /// otherwise it is a warning and ingest proceeds (ConceptNet's synset bridge is best-effort).
    /// </summary>
    bool RequireIliMap = false)
{
    /// <summary>
    /// True when this row can drive the generic <see cref="EtlDecomposer"/> today: its grammar is
    /// compiled and it carries either a declarative edge map or a registered bespoke witness factory.
    /// False rows need a grammar wired first (CILI turtle; FrameNet/PropBank/VerbNet xml).
    /// </summary>
    public bool IsComplete =>
        Modality.GrammarReady && (NodeEdgeMap.Count > 0 || EtlWitnessFactory.IsRegistered(Name));
}
