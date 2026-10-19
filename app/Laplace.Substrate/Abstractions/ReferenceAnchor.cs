using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Kinds of source reference key. An identifier kind is the entity: the canonical
/// composition of its name (i46360, abandon.01, a FrameNet frame, a Wikidata Q-id).
/// A packaging kind is an index inside one release — a synset offset, a sense key,
/// a map-file version, a frames.vrb number — and is recorded nowhere.
/// </summary>
public enum ReferenceIdentityKind : ushort
{
    CiliIli = 1,
    // Three-field bridge key. Packaging: it moves and collides across releases.
    WordNetSenseKey = 2,
    // offset-pos serialization (02084071-n). Packaging: pwn30 and pwn31 assign
    // different offsets to one ILI.
    WordNetSynsetKey = 3,
    CiliMapVersion = 4,
    PropBankRoleset = 5,
    VerbNetClass = 6,
    FrameNetLexicalUnit = 7,
    // lemma%ss_type:lex_filenum:lex_id:head_word:head_id. Packaging.
    WordNetExactSenseKey = 8,
    WikidataItem = 9,
    PredicateMatrixPredicate = 10,
    PredicateMatrixVocabulary = 11,
    PredicateMatrixAnnotationValue = 12,
    // Integer keys published by WordNet's frames.vrb and sents.vrb. Packaging.
    WordNetVerbFrame = 13,
    WordNetVerbSentence = 14,
}

/// <summary>
/// Admission of source identifiers.
///
/// An identifier serializes as its own name and is ordinary content, so the same
/// name is the same entity everywhere. A packaging index has no id and stages nothing.
/// </summary>
public static class ReferenceAnchor
{
    public static bool IsPackaging(ReferenceIdentityKind kind) => kind is
        ReferenceIdentityKind.WordNetSenseKey
        or ReferenceIdentityKind.WordNetSynsetKey
        or ReferenceIdentityKind.WordNetExactSenseKey
        or ReferenceIdentityKind.CiliMapVersion
        or ReferenceIdentityKind.WordNetVerbFrame
        or ReferenceIdentityKind.WordNetVerbSentence;

    public static Hash128? Id(ReferenceIdentityKind kind, string? rawKey)
    {
        ValidateKind(kind);
        if (IsPackaging(kind)) return null;
        string? key = Normalize(rawKey);
        return key is null ? null : ContentEmitter.RootId(key);
    }

    public static Hash128 IdUtf8(ReferenceIdentityKind kind, ReadOnlySpan<byte> normalizedKey)
    {
        ValidateKind(kind);
        if (IsPackaging(kind))
            throw new InvalidOperationException(
                "a packaging index is recorded nowhere and has no entity id");
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
        if (IsPackaging(kind)) return null;
        string? key = Normalize(rawKey);
        if (key is null) return null;
        OrderedCompositionComponent? component = ContentEmitter.StageComponent(builder, key, source);
        if (component is not { } realized) return null;
        // entityTypeId is intentionally not written into entity storage here.
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
        if (IsPackaging(kind)) return null;
        if (normalizedKey.IsEmpty) return null;
        OrderedCompositionComponent? component =
            ContentEmitter.StageComponent(builder, normalizedKey, source);
        if (component is not { } realized) return null;
        // Keep source/reference meaning outside canonical entity identity.
        return realized.Id;
    }

    /// <summary>
    /// A synset offset is packaging. pwn30 maps i46360 to 02084071-n and pwn31 maps the
    /// same ILI to 02086723-n, so the offset is not an entity in either release.
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
