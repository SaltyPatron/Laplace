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
    public async Task Ili_MapKeys_AndVersion_UseCanonicalContentTrajectories()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cili-ref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "ili.ttl"),
            "<i35545> a <Instance> ;\n"
            + "  skos:definition \"a governed semantic instance\"@en ;\n"
            + "  dc:source pwn30:02084071-n .\n"
            + "<i35546> a <Concept> ;\n"
            + "  skos:definition \"a governed semantic concept\"@en ;\n"
            + "  dc:source pwn30:02084072-n .\n"
            + "<i35547> a <Concept> ;\n"
            + "  skos:definition \"a native CILI definition\"@en .\n");
        // Three serializations of one PWN 3.0 mapping are packaging, not three
        // witnesses; the dc:source row in ili.ttl is the one admitted.
        await File.WriteAllTextAsync(Path.Combine(dir, "ili-map-pwn30.tab"),
            "i35545\t02084071-n\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "ili-map-wn30.ttl"),
            "ili:i35545 owl:sameAs pwn30:02084071-n .\n");
        // PWN 3.1 ships both shapes; the RDF file is selected once and its
        // namespace/version prefix decodes to the same 8-digit key as the tab file.
        await File.WriteAllTextAsync(Path.Combine(dir, "ili-map-pwn31.tab"),
            "i35545\t02084071-n\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "ili-map-wn31.ttl"),
            "ili:i35545 owl:sameAs pwn31:302084071-n .\n");

        try
        {
            var entities = new List<EntityRow>(); // an id may carry several type interpretations
            var physicalEntities = new HashSet<Hash128>();
            var attestations = new List<AttestationRow>();
            var decomposer = new CILIDecomposer();
            var context = new FakeContext(new NullWriter()) { EcosystemPath = dir };

            await foreach (var change in decomposer.DecomposeAsync(context, DecomposerOptions.Default).WithoutWriter())
            {
                entities.AddRange(change.AllEntities());
                foreach (var entityId in change.AllPhysicalityEntityIds())
                    physicalEntities.Add(entityId);
                attestations.AddRange(change.AllAttestations());
            }

            Hash128 ili = ReferenceAnchor.Id(ReferenceIdentityKind.CiliIli, "i35545")!.Value;
            Assert.Null(ReferenceAnchor.WordNetSynsetKeyId("pwn30", "02084071-n"));
            Assert.Null(ReferenceAnchor.Id(ReferenceIdentityKind.CiliMapVersion, "pwn30"));
            Assert.Null(ReferenceAnchor.WordNetSynsetKeyId("pwn31", "02084071-n"));
            Assert.Null(ReferenceAnchor.Id(ReferenceIdentityKind.CiliMapVersion, "pwn31"));

            Assert.Contains(entities, e => e.Id == ili);
            Assert.DoesNotContain(entities, e => e.Id == ContentEmitter.RootId("02084071-n"));
            Assert.DoesNotContain(entities, e => e.Id == ContentEmitter.RootId("pwn30"));
            Assert.DoesNotContain(entities, e => e.Id == ContentEmitter.RootId("pwn31"));
            Assert.Contains(ili, physicalEntities);
            Assert.DoesNotContain(ContentEmitter.RootId("02084071-n")!.Value, physicalEntities);

            Assert.Equal(ContentEmitter.RootId("i35545")!.Value, ili);

            Hash128 hasSynsetKey = RelationTypeRegistry.RelationTypeId("HAS_SYNSET_KEY");
            Assert.DoesNotContain(attestations, a => a.TypeId == hasSynsetKey);

            Hash128 typedAs = RelationTypeRegistry.RelationTypeId("IS_TYPED_AS");
            Assert.Contains(attestations, a =>
                a.SubjectId == ili && a.TypeId == typedAs
                && a.ObjectId == EntityTypeRegistry.CiliInstance);
            Hash128 concept = ReferenceAnchor.Id(
                ReferenceIdentityKind.CiliIli, "i35546")!.Value;
            Assert.Contains(attestations, a =>
                a.SubjectId == concept && a.TypeId == typedAs
                && a.ObjectId == EntityTypeRegistry.CiliConcept);
            Assert.DoesNotContain(attestations, a =>
                a.SubjectId == ili && a.TypeId == typedAs
                && a.ObjectId == EntityTypeRegistry.WordNetSynset);

            // A PWN-backed gloss in ili.ttl is WordNet's testimony, not a second witness,
            // so no HAS_DEFINITION is attested for it; a CILI-only concept's definition is.
            Hash128 hasDefinition = RelationTypeRegistry.RelationTypeId("HAS_DEFINITION");
            Assert.DoesNotContain(attestations, a =>
                a.SubjectId == ili && a.TypeId == hasDefinition);
            Assert.DoesNotContain(attestations, a =>
                a.SubjectId == concept && a.TypeId == hasDefinition);

            Hash128 nativeConcept = ReferenceAnchor.Id(
                ReferenceIdentityKind.CiliIli, "i35547")!.Value;
            Assert.Contains(attestations, a =>
                a.SubjectId == nativeConcept && a.TypeId == hasDefinition
                && a.SourceId == CILIDecomposer.Source);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SourceManifest_Preserves_Cili_License_And_Release()
    {
        Assert.Equal("CC-BY-4.0", CILISource.License.Spdx);
        Assert.Equal("2016 Initial release", CILISource.License.Version);
        Assert.Contains("Global Wordnet Association", CILISource.License.Copyright);
    }
}
