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
    bool HasDedicatedDecomposer = false)
{
    public bool IsComplete =>
        Modality.GrammarReady && (NodeEdgeMap.Count > 0 || EtlWitnessFactory.IsRegistered(Name));

    /// <summary>True when CLI dispatch must use the source's own IDecomposer, never EtlDecomposer.</summary>
    public bool IsRoutableViaEtl => !HasDedicatedDecomposer && IsComplete;
}
