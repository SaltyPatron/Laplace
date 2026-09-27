using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Admission choice for a key whose kind is known only at runtime: an external reference
/// key goes through <see cref="ReferenceAnchor"/>, a readable category label through
/// <see cref="CategoryAnchor"/>.
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
            : CategoryAnchor.Emit(builder, key, source);

    public static ReferenceIdentityKind? ReferenceKind(Hash128 entityTypeId)
    {
        if (entityTypeId == EntityTypeRegistry.PropBankRoleset)
            return ReferenceIdentityKind.PropBankRoleset;
        if (entityTypeId == EntityTypeRegistry.VerbNetClass)
            return ReferenceIdentityKind.VerbNetClass;
        if (entityTypeId == EntityTypeRegistry.FrameNetLu)
            return ReferenceIdentityKind.FrameNetLexicalUnit;
        if (entityTypeId == EntityTypeRegistry.PredicateMatrixPredicate)
            return ReferenceIdentityKind.PredicateMatrixPredicate;
        return null;
    }
}
