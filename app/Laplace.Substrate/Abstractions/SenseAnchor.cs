using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class SenseAnchor
{
    private static readonly Hash128 SenseTypeId = EntityTypeRegistry.WordNetSense;
    private static readonly Hash128 CompatibilityTypeId = EntityTypeRegistry.SourceReference;

    /// <summary>
    /// Resolves the three-field sense key. It is many-to-many for adjective satellites, so
    /// it never stands for an exact PWN sense.
    /// </summary>
    public static Hash128? Id(string? rawSenseKey)
    {
        string? key = rawSenseKey is null ? null : SourceEntityIdConventions.NormalizeSenseKey(rawSenseKey);
        return key is null ? null : ReferenceAnchor.Id(ReferenceIdentityKind.WordNetSenseKey, key);
    }

    public static Hash128? IdNormalized(string normalizedSenseKey) =>
        ReferenceAnchor.Id(ReferenceIdentityKind.WordNetSenseKey, normalizedSenseKey);

    public static Hash128? ExactId(string? rawSenseKey)
    {
        string? key = rawSenseKey is null
            ? null
            : SourceEntityIdConventions.NormalizeExactSenseKey(rawSenseKey);
        return key is null
            ? null
            : ReferenceAnchor.Id(ReferenceIdentityKind.WordNetExactSenseKey, key);
    }

    public static Hash128? Emit(
        SubstrateChangeBuilder b, string rawSenseKey, Hash128 source, double trust)
    {
        string? key = SourceEntityIdConventions.NormalizeSenseKey(rawSenseKey);
        return key is null ? null : ReferenceAnchor.Emit(
            b, ReferenceIdentityKind.WordNetSenseKey, key, CompatibilityTypeId, source, trust);
    }

    public static Hash128? EmitExact(
        SubstrateChangeBuilder b, string rawSenseKey, Hash128 source, double trust)
    {
        string? key = SourceEntityIdConventions.NormalizeExactSenseKey(rawSenseKey);
        return key is null ? null : ReferenceAnchor.Emit(
            b, ReferenceIdentityKind.WordNetExactSenseKey, key, SenseTypeId, source, trust);
    }
}
