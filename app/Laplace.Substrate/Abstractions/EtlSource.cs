using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

public enum AnchorResolver
{
    None,
    IliSynset,
    SenseKey,
    FrameCategory,
}

public enum GrammarRecordFraming
{
    Grammar,
    Line,
}

public readonly record struct EtlModality(
    string GrammarId,
    string? Glob = null,
    bool GrammarReady = true,
    GrammarRecordFraming RecordFraming = GrammarRecordFraming.Grammar);

public readonly record struct EdgeRule(
    int SubjectField,
    int ObjectField,
    string RelationType,
    EdgeRoleKind SubjectKind = EdgeRoleKind.Content,
    EdgeRoleKind ObjectKind = EdgeRoleKind.Content);

public enum EdgeRoleKind
{
    Content,
    Anchor,
}

/// <param name="LanguageScope">
/// ISO 639-3 code the source asserts for every LEXICAL SURFACE it deposits, or null
/// when the source is multilingual (it attests language per row) or language-neutral.
///
/// This is a RECORD, not an inference. Princeton WordNet is English by construction and
/// that construction is the source asserting it — spec 08's record-vs-calculate law.
/// Nine monolingual sources declared no HAS_LANGUAGE at all (WordNet, FrameNet, PropBank,
/// VerbNet, SemLink, WordFrameNet, PredicateMatrix among them), so `converse.word_language()` was
/// built to INFER at read time a fact the source states for free, and every English sense
/// in the substrate reads back as language-unattested.
///
/// Scope is declared here and applied by the decomposer, deliberately. It cannot be
/// blanket-applied at SubstrateChangeBuilder.AddEntity: a WordNet SYNSET is ILI-shared and
/// language-neutral, as are POS tags and lex categories, so stamping every deposited id
/// would attest something false — and AddEntity is 49.9% of the compose path. The source
/// knows WHICH language; only the decomposer knows WHICH ids are its words.
/// </param>
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
    Func<string, Hash128?>? ContextIdFromFile = null,
    bool RequireIliMap = false,
    bool HasDedicatedDecomposer = false,
    IngestSourceProfile? Profile = null,
    string? LanguageScope = null)
{
    public bool IsComplete =>
        Modality.GrammarReady && (NodeEdgeMap.Count > 0 || EtlWitnessFactory.IsRegistered(Name));

    /// <summary>True when CLI dispatch must use the source's own IDecomposer, never EtlDecomposer.</summary>
    public bool IsRoutableViaEtl => !HasDedicatedDecomposer && IsComplete;

    /// <summary>
    /// Entity id of the declared language scope, or null when the source has none.
    /// Resolved through LanguageReference so the id comes from the same place every other
    /// language id does — a decomposer must never construct one, per the identity law.
    /// </summary>
    public Hash128? LanguageScopeId =>
        LanguageScope is null ? null : LanguageReference.IdForResolvedCode(LanguageScope);

    /// <summary>
    /// The relation a declared scope obliges the source to emit.
    ///
    /// THE ONLY place this vocabulary word appears for the language-scope feature —
    /// declaration, InitializeAsync, and every emit site read it from here. G3 counts
    /// governed relation names literal-by-literal in C#, and it is right to: the first
    /// draft of this change spelled HAS_LANGUAGE three times across two files, which is
    /// exactly the per-source hand-roll that left nine sources silent in the first place.
    /// One name, one home.
    /// </summary>
    public const string LanguageScopeRelation = "HAS_LANGUAGE";

    /// <summary>
    /// Folded into a scoped decomposer's InitializeAsync declaration so it cannot fault
    /// the native attestation path by emitting a relation it never declared.
    /// </summary>
    public static readonly string[] LanguageScopeRelations = { LanguageScopeRelation };
}
