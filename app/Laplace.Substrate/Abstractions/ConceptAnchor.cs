using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;








public static class ConceptAnchor
{
    private static readonly Hash128 SynsetTypeId = EntityTypeRegistry.WordNetSynset;









    public static Hash128? EmitAnchor(SubstrateChangeBuilder b, long offset, char ssType, Hash128 source)
    {
        string? ili = SourceEntityIdConventions.WordNetIli(offset, ssType);
        return ili is null ? null : ContentEmitter.Emit(b, ili, source);
    }





    public static void AttestSynsetCategory(SubstrateChangeBuilder b, Hash128 synId, Hash128 source, double trust)
        => b.AddAttestation(NativeAttestation.Categorical(synId, "IS_TYPED_AS", SynsetTypeId, source, trust));







    public static Hash128? EmitSynset(
        SubstrateChangeBuilder b, long offset, char ssType, Hash128 source, double trust)
    {
        Hash128? id = EmitAnchor(b, offset, ssType, source);
        if (id is null) return null;
        AttestSynsetCategory(b, id.Value, source, trust);
        return id;
    }





    public static Hash128? SynsetId(long offset, char ssType, string version = "pwn30")
    {
        string? ili = SourceEntityIdConventions.WordNetIli(offset, ssType, version);
        return ili is null ? null : ContentEmitter.RootId(ili);
    }
}
