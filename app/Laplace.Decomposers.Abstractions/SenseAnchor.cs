using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// WordNet sense keys (lemma%ss_type:lex_filenum:lex_id) as content-addressed category anchors.
/// Converges VerbNet CORRESPONDS_TO edges with WordNet index.sense output.
/// </summary>
public static class SenseAnchor
{
    private static readonly Hash128 SenseTypeId = EntityTypeRegistry.WordNetSense;

    public static Hash128? Id(string? rawSenseKey)
    {
        string? key = rawSenseKey is null ? null : SourceEntityIdConventions.NormalizeSenseKey(rawSenseKey);
        return key is null ? null : CategoryAnchor.Id(key);
    }

    public static Hash128? IdNormalized(string normalizedSenseKey) =>
        CategoryAnchor.Id(normalizedSenseKey);

    public static Hash128? Emit(
        SubstrateChangeBuilder b, string rawSenseKey, Hash128 source, double trust)
    {
        string? key = SourceEntityIdConventions.NormalizeSenseKey(rawSenseKey);
        return key is null ? null : CategoryAnchor.Emit(b, key, SenseTypeId, source, trust);
    }

    public static void AttestSenseCategory(
        SubstrateChangeBuilder b, Hash128 senseId, Hash128 source, double trust)
        => CategoryAnchor.AttestCategory(b, senseId, SenseTypeId, source, trust);
}
