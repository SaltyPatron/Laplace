using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;









[Collection("GrammarPerfcache")]
public class ConceptAnchorTests
{
    [SkippableFact]
    public void EmitSynset_ProducesGovernedSemanticAnchorWithoutContentTree()
    {
        string cili = TestPathHelpers.CiliOrFallback();
        Skip.IfNot(File.Exists(Path.Combine(cili, IliMap.MapFileName)), "CILI map not present");

        CodepointPerfcache.LoadDefault();

        var source = SubstrateCanonicalIds.OfVersioned("source", "test", "wn-anchor");
        var b = new SubstrateChangeBuilder(source, "test/concept-anchor", null,
            entityCapacity: 64, physicalityCapacity: 64, attestationCapacity: 64);


        Hash128? id = ConceptAnchor.EmitSynset(b, 10676319, 'n', source, SourceTrust.StandardsDerived);

        Assert.NotNull(id);
        Assert.Equal(id, ConceptAnchor.SynsetId(10676319, 'n'));




        Assert.Equal(0, b.ContentStage.EntityCount);

        var change = b.Build();
        var entity = Assert.Single(change.Entities);
        Assert.Equal(id, entity.Id);
        Assert.Equal(EntityTypeRegistry.WordNetSynset, entity.TypeId);
        Assert.Empty(change.Physicalities);
        Assert.False(EntityIdentityPolicy.RequiresPhysicality(entity.TypeId));
        var typedAs = RelationTypeRegistry.RelationTypeId("IS_TYPED_AS");
        Assert.Contains(change.Attestations, a =>
            a.SubjectId == id!.Value && a.TypeId == typedAs && a.ObjectId == EntityTypeRegistry.WordNetSynset);
    }








    [SkippableFact]
    public void Satellite_ResolvesUnderBothPos_AsCollapseDoesNotDrop()
    {
        string cili = TestPathHelpers.CiliOrFallback();
        string mapPath = Path.Combine(cili, IliMap.MapFileName);
        Skip.IfNot(File.Exists(mapPath), "CILI map not present");
        Skip.If(IliMap.Load(cili).Count < 100_000, "stub/minimal CILI tree — full map required");

        CodepointPerfcache.LoadDefault();


        long satOffset = -1;
        foreach (var line in File.ReadLines(mapPath))
        {
            var op = line.AsSpan(line.IndexOf('\t') + 1).Trim();
            int d = op.LastIndexOf('-');
            if (d <= 0) continue;
            var posSpan = op[(d + 1)..];
            if (posSpan.Length != 1 || posSpan[0] != 's') continue;
            if (long.TryParse(op[..d], out satOffset)) break;
        }
        Assert.True(satOffset > 0, "expected a satellite synset in the CILI map");




        var asS = ConceptAnchor.SynsetId(satOffset, 's');
        var asA = ConceptAnchor.SynsetId(satOffset, 'a');
        Assert.NotNull(asS);
        Assert.NotNull(asA);
        Assert.Equal(asS, asA);
    }
}
