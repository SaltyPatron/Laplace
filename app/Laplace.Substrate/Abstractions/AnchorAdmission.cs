using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Central admission decision for adapters whose entity type is only known at runtime.
/// Known source-keyed identities use <see cref="ReferenceAnchor"/>; human-readable
/// semantic category labels continue through <see cref="CategoryAnchor"/>.
/// </summary>
public static class AnchorAdmission
{
    public static Hash128? Id(string key, Hash128 entityTypeId) =>
        ReferenceKind(entityTypeId) is { } kind
            ? ReferenceAnchor.Id(kind, key)
            : CategoryAnchor.Id(key);

    public static Hash128? Emit(
        SubstrateChangeBuilder builder,
        string key,
        Hash128 entityTypeId,
        Hash128 source,
        double trust) =>
        ReferenceKind(entityTypeId) is { } kind
            ? ReferenceAnchor.Emit(builder, kind, key, entityTypeId, source, trust)
            : CategoryAnchor.Emit(builder, key, entityTypeId, source, trust);

    public static ReferenceIdentityKind? ReferenceKind(Hash128 entityTypeId)
    {
        if (entityTypeId == EntityTypeRegistry.PropBankRoleset)
            return ReferenceIdentityKind.PropBankRoleset;
        if (entityTypeId == EntityTypeRegistry.VerbNetClass)
            return ReferenceIdentityKind.VerbNetClass;
        if (entityTypeId == EntityTypeRegistry.FrameNetLu)
            return ReferenceIdentityKind.FrameNetLexicalUnit;
        return null;
    }
}
