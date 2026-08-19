using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;













/// <summary>
/// Admission path for human-readable semantic category labels. Opaque source/catalog
/// keys belong in <see cref="ReferenceAnchor"/> and must not pass through this API.
/// </summary>
public static class CategoryAnchor
{
    private static readonly Hash128 IsTypedAsTypeId =
        RelationTypeRegistry.RelationTypeId("IS_TYPED_AS");






    public static Hash128? Emit(
        SubstrateChangeBuilder b, string key, Hash128 categoryTypeId, Hash128 source, double trust)
    {
        string? normalized = Normalize(key);
        if (normalized is null) return null;
        Hash128? id = ContentEmitter.Emit(b, normalized, source);
        if (id is null) return null;
        AttestCategory(b, id.Value, categoryTypeId, source, trust);
        return id;
    }


    public static void AttestCategory(
        SubstrateChangeBuilder b, Hash128 anchor, Hash128 categoryTypeId, Hash128 source, double trust)
        => b.AddAttestation(NativeAttestation.CategoricalResolved(
            anchor, IsTypedAsTypeId, categoryTypeId, source, null, trust));

    public static Hash128 CategoryAttestationId(
        Hash128 anchor, Hash128 categoryTypeId, Hash128 source) =>
        NativeAttestation.ComputeId(anchor, IsTypedAsTypeId, categoryTypeId, source, null);





    public static Hash128? Id(string key) =>
        Normalize(key) is { } normalized ? ContentEmitter.RootId(normalized) : null;






    private static string? Normalize(string key) =>
        string.IsNullOrEmpty(key) ? null : key.Trim() is { Length: > 0 } trimmed ? trimmed : null;
}
