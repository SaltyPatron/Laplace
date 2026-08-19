using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class SenseAnchor
{
    private static readonly Hash128 SenseTypeId = EntityTypeRegistry.WordNetSense;

    public static Hash128? Id(string? rawSenseKey)
    {
        string? key = rawSenseKey is null ? null : SourceEntityIdConventions.NormalizeSenseKey(rawSenseKey);
        return key is null ? null : ReferenceAnchor.Id(ReferenceIdentityKind.WordNetSenseKey, key);
    }

    public static Hash128? IdNormalized(string normalizedSenseKey) =>
        ReferenceAnchor.Id(ReferenceIdentityKind.WordNetSenseKey, normalizedSenseKey);

    public static Hash128? Emit(
        SubstrateChangeBuilder b, string rawSenseKey, Hash128 source, double trust)
    {
        string? key = SourceEntityIdConventions.NormalizeSenseKey(rawSenseKey);
        return key is null ? null : ReferenceAnchor.Emit(
            b, ReferenceIdentityKind.WordNetSenseKey, key, SenseTypeId, source, trust);
    }
}
