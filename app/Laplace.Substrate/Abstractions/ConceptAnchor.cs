using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;








public static class ConceptAnchor
{
    private static readonly Hash128 SynsetTypeId = EntityTypeRegistry.WordNetSynset;









    public static Hash128? EmitAnchor(
        SubstrateChangeBuilder b, long offset, char ssType, Hash128 source, string? version)
    {
        string? ili = SourceEntityIdConventions.WordNetIli(offset, ssType, version);
        return ili is null ? null : ReferenceAnchor.Declare(
            b, ReferenceIdentityKind.CiliIli, ili, SynsetTypeId, source);
    }












    public static Hash128? EmitSynset(
        SubstrateChangeBuilder b, long offset, char ssType, Hash128 source, double trust, string? version)
    {
        return EmitAnchor(b, offset, ssType, source, version);
    }





    public static Hash128? SynsetId(long offset, char ssType, string? version)
    {
        string? ili = SourceEntityIdConventions.WordNetIli(offset, ssType, version);
        return ili is null ? null : ReferenceAnchor.Id(ReferenceIdentityKind.CiliIli, ili);
    }

}
