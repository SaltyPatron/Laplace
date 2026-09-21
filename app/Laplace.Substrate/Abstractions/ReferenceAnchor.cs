using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Identity domains retained for source-format normalization and call-site compatibility.
/// The domain is NOT a second identity universe: the canonical serialization is ordinary
/// content, so the same bytes converge on the same entity and physicality everywhere.
/// Source/version/domain meaning belongs in type interpretations, relations, and context.
/// </summary>
public enum ReferenceIdentityKind : ushort
{
    CiliIli = 1,
    // Persisted three-field bridge key. It can refer to multiple adjective-satellite
    // senses; keep the value stable and never promote it to exact native identity.
    WordNetSenseKey = 2,
    WordNetSynsetKey = 3,
    CiliMapVersion = 4,
    PropBankRoleset = 5,
    VerbNetClass = 6,
    FrameNetLexicalUnit = 7,
    // Complete lemma%ss_type:lex_filenum:lex_id:head_word:head_id source key.
    WordNetExactSenseKey = 8,
    WikidataItem = 9,
    PredicateMatrixPredicate = 10,
    PredicateMatrixVocabulary = 11,
    PredicateMatrixAnnotationValue = 12,
    // Exact integer coordinates published by WordNet's frames.vrb and sents.vrb.
    // Keeping them as source references lets data.* and sentidx.vrb resolve their
    // operands without reopening a different physical artifact.
    WordNetVerbFrame = 13,
    WordNetVerbSentence = 14,
}

/// <summary>
/// Admission path for source/catalog references.
///
/// A reference serialization is still content. Emitting one stages the normal Unicode/content
/// trajectory and records the requested semantic/source-reference type as an interpretation.
/// This preserves Laplace's global convergence law:
///
/// same canonical content -> same entity -> same physicality/trajectory
///
/// Version, source system, and proposition role remain explicit state around that entity.
/// </summary>
public static class ReferenceAnchor
{
    public static Hash128? Id(ReferenceIdentityKind kind, string? rawKey)
    {
        ValidateKind(kind);
        string? key = Normalize(rawKey);
        return key is null ? null : ContentEmitter.RootId(key);
    }

    public static Hash128 IdUtf8(ReferenceIdentityKind kind, ReadOnlySpan<byte> normalizedKey)
    {
        ValidateKind(kind);
        if (normalizedKey.IsEmpty)
            throw new ArgumentException("reference key must not be empty", nameof(normalizedKey));
        return ContentEmitter.RootId(normalizedKey)
            ?? throw new InvalidOperationException("reference key could not be composed as canonical content");
    }

    public static Hash128? Emit(
        SubstrateChangeBuilder builder,
        ReferenceIdentityKind kind,
        string? rawKey,
        Hash128 entityTypeId,
        Hash128 source,
        double trust)
    {
        Hash128? id = Declare(builder, kind, rawKey, entityTypeId, source);
        if (id is null) return null;
        CategoryAnchor.AttestCategory(builder, id.Value, entityTypeId, source, trust);
        return id;
    }

    public static Hash128? Declare(
        SubstrateChangeBuilder builder,
        ReferenceIdentityKind kind,
        string? rawKey,
        Hash128 entityTypeId,
        Hash128 source)
    {
        ValidateKind(kind);
        string? key = Normalize(rawKey);
        if (key is null) return null;
        OrderedCompositionComponent? component = ContentEmitter.StageComponent(builder, key, source);
        if (component is not { } realized) return null;
        builder.AddEntityInterpretation(new EntityInterpretationRow(
            realized.Id, realized.Tier, entityTypeId, source));
        return realized.Id;
    }

    public static Hash128? EmitUtf8(
        SubstrateChangeBuilder builder,
        ReferenceIdentityKind kind,
        ReadOnlySpan<byte> normalizedKey,
        Hash128 entityTypeId,
        Hash128 source,
        double trust)
    {
        Hash128? id = DeclareUtf8(builder, kind, normalizedKey, entityTypeId, source);
        if (id is null) return null;
        CategoryAnchor.AttestCategory(builder, id.Value, entityTypeId, source, trust);
        return id;
    }

    public static Hash128? DeclareUtf8(
        SubstrateChangeBuilder builder,
        ReferenceIdentityKind kind,
        ReadOnlySpan<byte> normalizedKey,
        Hash128 entityTypeId,
        Hash128 source)
    {
        ValidateKind(kind);
        if (normalizedKey.IsEmpty) return null;
        OrderedCompositionComponent? component =
            ContentEmitter.StageComponent(builder, normalizedKey, source);
        if (component is not { } realized) return null;
        builder.AddEntityInterpretation(new EntityInterpretationRow(
            realized.Id, realized.Tier, entityTypeId, source));
        return realized.Id;
    }

    /// <summary>
    /// The offset/POS serialization is content. WordNet generation is proposition context,
    /// not part of the content identity. A pwn30 and pwn31 row with the same exact key
    /// therefore converge on one key entity while HAS_SYNSET_KEY keeps the version context.
    /// </summary>
    public static Hash128? WordNetSynsetKeyId(string version, string key)
    {
        RequireVersion(version);
        return Id(ReferenceIdentityKind.WordNetSynsetKey, key);
    }

    public static Hash128? DeclareWordNetSynsetKey(
        SubstrateChangeBuilder builder,
        string version,
        string key,
        Hash128 source)
    {
        RequireVersion(version);
        return Declare(builder, ReferenceIdentityKind.WordNetSynsetKey, key,
            EntityTypeRegistry.SourceReference, source);
    }

    private static void ValidateKind(ReferenceIdentityKind kind)
    {
        if (kind is < ReferenceIdentityKind.CiliIli or > ReferenceIdentityKind.WordNetVerbSentence)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown reference identity domain");
    }

    private static void RequireVersion(string version)
    {
        if (Normalize(version) is null)
            throw new ArgumentException("reference version must not be empty", nameof(version));
    }

    private static string? Normalize(string? rawKey) =>
        string.IsNullOrWhiteSpace(rawKey) ? null : rawKey.Trim();
}
