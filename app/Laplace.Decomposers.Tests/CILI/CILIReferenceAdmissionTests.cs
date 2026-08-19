using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.CILI.Tests;

public sealed class CILIReferenceAdmissionTests
{
    static CILIReferenceAdmissionTests() => CodepointPerfcache.LoadDefault();

    [Fact]
    public async Task Ili_MapKeys_AndVersion_AreGovernedReferences_NotTextContent()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cili-ref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "ili.ttl"),
            "<i35545> a ili:Concept ;\n  skos:definition \"a governed semantic concept\" .\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "ili-map-pwn30.tab"),
            "i35545\t02084071-n\n");

        try
        {
            var entities = new Dictionary<Hash128, EntityRow>();
            var physicalEntities = new HashSet<Hash128>();
            var attestations = new List<AttestationRow>();
            var decomposer = new CILIDecomposer();
            var context = new FakeContext(new NullWriter()) { EcosystemPath = dir };

            await foreach (var change in decomposer.DecomposeAsync(context, DecomposerOptions.Default))
            {
                foreach (var entity in change.Entities) entities[entity.Id] = entity;
                foreach (var physicality in change.Physicalities)
                    physicalEntities.Add(physicality.EntityId);
                attestations.AddRange(change.Attestations);
            }

            Hash128 ili = ReferenceAnchor.Id(ReferenceIdentityKind.CiliIli, "i35545")!.Value;
            Hash128 key = ReferenceAnchor.WordNetSynsetKeyId("pwn30", "02084071-n")!.Value;
            Hash128 version = ReferenceAnchor.Id(
                ReferenceIdentityKind.CiliMapVersion, "pwn30")!.Value;

            Assert.Equal(EntityTypeRegistry.WordNetSynset, entities[ili].TypeId);
            Assert.Equal(EntityTypeRegistry.SourceReference, entities[key].TypeId);
            Assert.Equal(EntityTypeRegistry.SourceVersion, entities[version].TypeId);
            Assert.DoesNotContain(ili, physicalEntities);
            Assert.DoesNotContain(key, physicalEntities);
            Assert.DoesNotContain(version, physicalEntities);

            Assert.DoesNotContain(ContentEmitter.RootId("i35545")!.Value, entities.Keys);
            Assert.DoesNotContain(ContentEmitter.RootId("02084071-n")!.Value, entities.Keys);
            Assert.DoesNotContain(ContentEmitter.RootId("pwn30")!.Value, entities.Keys);

            Hash128 hasSynsetKey = RelationTypeRegistry.RelationTypeId("HAS_SYNSET_KEY");
            Assert.Contains(attestations, a =>
                a.SubjectId == ili && a.TypeId == hasSynsetKey
                && a.ObjectId == key && a.ContextId == version);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
