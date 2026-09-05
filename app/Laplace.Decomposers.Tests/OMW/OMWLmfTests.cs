using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.OMW;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Tests.OMW;

public sealed class OMWLmfTests
{
    static OMWLmfTests()
    {
        CodepointPerfcache.LoadDefault();
        LanguageReference.EnsureLoaded(TestIngestPaths.Iso639);
    }

    [Fact]
    public async Task WnLmf14_Preserves_Lexicon_Entry_Form_Sense_Synset_And_Relations()
    {
        Assert.Equal(Enum.GetValues<OmwRelation>().Length, OMWSource.Relations.Count);
        string root = NewRoot();
        try
        {
            string dir = Directory.CreateDirectory(Path.Combine(root, "omw-xy")).FullName;
            string xml = Path.Combine(dir, "omw-xy.xml");
            await File.WriteAllTextAsync(xml, FixtureXml);

            var records = await ReadAllAsync(xml, "omw-lmf/xml/omw-xy/omw-xy.xml");

            var lexicon = Assert.IsType<OmwLmfLexicon>(records[0]);
            Assert.Equal("2.0", lexicon.Version);
            Assert.Equal("https://license.example/xy", lexicon.License);
            Assert.Contains(records, record => record is OmwLmfRequires
            {
                Reference: "oewn", Version: "2025"
            });
            var entry = Assert.Single(records.OfType<OmwLmfLexicalEntry>());
            Assert.Equal("brokenplural", entry.LemmaType);
            Assert.Equal(["mice"], entry.Forms.Select(static form => form.WrittenForm));
            Assert.Contains(entry.Forms[0].Tags, tag => tag is { Category: "number", Value: "plural" });
            var sense = Assert.Single(entry.Senses);
            Assert.Equal("mouse%1:05:00::", sense.Identifier);
            Assert.Equal("frame-1", sense.Subcategorization);
            Assert.Equal("attributive", sense.AdjectivePosition);
            Assert.Equal("17", sense.Count);
            Assert.Contains(sense.Relations, relation =>
                relation is { Type: "antonym", Target: "omw-xy-rat-sense", Confidence: 0.75 });
            var synset = Assert.Single(records.OfType<OmwLmfSynset>());
            Assert.Equal("i1", synset.Ili);
            Assert.Equal("omw-xy", synset.Lexicon);
            Assert.Equal("omw-xy-mouse-n", synset.Id);
            Assert.Equal(["an animal"], synset.Definitions);
            Assert.Equal(["a mouse ran"], synset.Examples);
            Assert.Contains(synset.Relations, relation =>
                relation is { Type: "hypernym", Target: "omw-xy-animal-n" });
            Assert.Contains(records, record => record is OmwLmfSyntacticBehaviour
            {
                Id: "frame-1", Frame: "Somebody ----s"
            });

            var builder = new SubstrateChangeBuilder(OMWDecomposer.Source, "omw-lmf-test");
            foreach (OmwLmfRecord record in records) OMWLmfEmitter.Emit(builder, record);
            SubstrateChange change = builder.Build();

            Hash128 entryId = OMWLmfEmitter.Identity("entry", "omw-xy", "omw-xy-mouse-n");
            Hash128 senseId = OMWLmfEmitter.Identity("sense", "omw-xy", "omw-xy-mouse-sense");
            Hash128 synsetId = OMWLmfEmitter.Identity("synset", "omw-xy", "omw-xy-mouse-n");
            Assert.Contains(change.Entities, entity =>
                entity.Id == entryId && entity.TypeId == OMWSource.LexicalEntryTypeId);
            Assert.Contains(change.Entities, entity =>
                entity.Id == senseId && entity.TypeId == EntityTypeRegistry.WordNetSense);
            Assert.Contains(change.Entities, entity =>
                entity.Id == synsetId && entity.TypeId == EntityTypeRegistry.WordNetSynset);
            AssertEdge(change, entryId, "HAS_SENSE", senseId);
            AssertEdge(change, senseId, "IS_SENSE_OF", synsetId);
            AssertUnorderedEdge(change, synsetId, "CORRESPONDS_TO",
                ReferenceAnchor.Id(ReferenceIdentityKind.CiliIli, "i1")!.Value);
            AssertUnorderedEdge(change, senseId, "IS_ANTONYM_OF",
                OMWLmfEmitter.Identity("sense", "omw-xy", "omw-xy-rat-sense"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ArtifactGraph_Enumerates_Complete_Release_And_Schedules_The_Same_Admitted_Set()
    {
        string root = NewRoot();
        try
        {
            await WriteAsync(root, "omw-2.0.tar.xz", "archive");
            await WriteAsync(root, "index.toml", "[omw-xy]");
            await WriteAsync(root, "extracted/omw-xy/omw-xy.xml", FixtureXml);
            await WriteAsync(root, "extracted/omw-xy/LICENSE", "license text");
            await WriteAsync(root, "extracted/omw-xy/citation.bib", "@book{x}");
            await WriteAsync(root, "extracted/omw-xy/README", "notes");
            await WriteAsync(root, "extracted/omw-xy/unparsed.dat", "opaque");
            await WriteAsync(root, "extracted/omw-fr/omw-fr.xml",
                FixtureXml.Replace("omw-xy", "omw-fr").Replace("language=\"xy\"", "language=\"fr\""));

            var options = DecomposerOptions.Default with
            {
                Languages = LanguageFilter.FromSpec("fr")
            };
            IngestArtifactGraph graph = Assert.IsType<IngestArtifactGraph>(
                OMWLmfArtifacts.Build(root, options));

            Assert.Equal(8, graph.Artifacts.Count);
            Assert.Contains(graph.Artifacts, artifact =>
                artifact.RelativePath == "omw-2.0.tar.xz"
                && artifact.Disposition == IngestArtifactDisposition.EquivalentPackaging);
            Assert.Contains(graph.Artifacts, artifact =>
                artifact.RelativePath.EndsWith("unparsed.dat", StringComparison.Ordinal)
                && artifact.Disposition == IngestArtifactDisposition.Unsupported
                && artifact.Notes.Length > 0);
            Assert.Contains(graph.Artifacts, artifact =>
                artifact.RelativePath.EndsWith("omw-xy.xml", StringComparison.Ordinal)
                && artifact.Disposition == IngestArtifactDisposition.ExcludedWithReason);
            Assert.Equal(2, graph.Selected.Count);
            Assert.All(graph.Selected, artifact => Assert.True(
                artifact.FileLabel.Contains("/xml/", StringComparison.Ordinal)
                || artifact.FileLabel.Contains("/index/", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Hypernym_And_Inverse_Hyponym_Emit_The_Same_Canonical_Arena_And_Endpoints()
    {
        const string lexicon = "omw-xy";
        const string child = "omw-xy-mouse-n";
        const string parent = "omw-xy-animal-n";
        Hash128 childId = OMWLmfEmitter.Identity("synset", lexicon, child);
        Hash128 parentId = OMWLmfEmitter.Identity("synset", lexicon, parent);
        Hash128 isA = RelationTypeRegistry.Resolve("IS_A").Id;

        SubstrateChange hypernym = EmitSynsetRelation(
            lexicon, child, new OmwLmfRelation(parent, "hypernym", 1.0));
        SubstrateChange hyponym = EmitSynsetRelation(
            lexicon, parent, new OmwLmfRelation(child, "hyponym", 1.0));

        AttestationRow forward = Assert.Single(hypernym.Attestations.Where(row =>
            row.TypeId == isA && row.SubjectId == childId && row.ObjectId == parentId));
        AttestationRow inverse = Assert.Single(hyponym.Attestations.Where(row =>
            row.TypeId == isA && row.SubjectId == childId && row.ObjectId == parentId));
        Assert.Equal(forward.Id, inverse.Id);
        Assert.Equal(RelationTypeRegistry.Resolve("HAS_HYPERNYM").Id, forward.TypeId);
        Assert.Equal(RelationTypeRegistry.Resolve("HAS_HYPONYM").Id, inverse.TypeId);
    }

    [Fact]
    public void Exemplification_Directions_Emit_One_UsageDomain_Arena_Not_Textual_Examples()
    {
        const string lexicon = "omw-xy";
        const string member = "omw-xy-informal-n";
        const string usageDomain = "omw-xy-colloquial-n";
        Hash128 memberId = OMWLmfEmitter.Identity("synset", lexicon, member);
        Hash128 domainId = OMWLmfEmitter.Identity("synset", lexicon, usageDomain);
        Hash128 usageType = RelationTypeRegistry.Resolve("HAS_DOMAIN_USAGE").Id;
        Hash128 exampleType = RelationTypeRegistry.Resolve("HAS_EXAMPLE").Id;

        SubstrateChange exemplifies = EmitSynsetRelation(
            lexicon, member, new OmwLmfRelation(usageDomain, "exemplifies", 1.0));
        SubstrateChange isExemplifiedBy = EmitSynsetRelation(
            lexicon, usageDomain, new OmwLmfRelation(member, "is_exemplified_by", 1.0));

        AttestationRow forward = Assert.Single(exemplifies.Attestations.Where(row =>
            row.TypeId == usageType && row.SubjectId == domainId && row.ObjectId == memberId));
        AttestationRow inverse = Assert.Single(isExemplifiedBy.Attestations.Where(row =>
            row.TypeId == usageType && row.SubjectId == domainId && row.ObjectId == memberId));
        Assert.Equal(forward.Id, inverse.Id);
        Assert.Equal(RelationTypeRegistry.Resolve("IS_DOMAIN_USAGE_MEMBER").Id, forward.TypeId);
        Assert.DoesNotContain(exemplifies.Attestations, row => row.TypeId == exampleType);
        Assert.DoesNotContain(isExemplifiedBy.Attestations, row => row.TypeId == exampleType);
    }

    [SkippableFact]
    public async Task Current_Vault_OMW2_Enumerates_32_Lexicons_And_Parses_A_Real_One()
    {
        const string root = "/vault/Data/.refresh-20260903/OMW-2.0";
        Skip.IfNot(Directory.Exists(root), "staged OMW 2.0 estate is not mounted");

        IngestArtifactGraph graph = Assert.IsType<IngestArtifactGraph>(
            OMWLmfArtifacts.Build(root, DecomposerOptions.Default));
        Assert.Equal(32, graph.Artifacts.Count(artifact =>
            artifact.Disposition == IngestArtifactDisposition.Admitted
            && artifact.MediaType == "application/xml"));
        Assert.Contains(graph.Artifacts, artifact =>
            artifact.RelativePath == "omw-2.0.tar.xz"
            && artifact.Disposition == IngestArtifactDisposition.EquivalentPackaging);
        Assert.Contains(graph.Artifacts, artifact =>
            artifact.RelativePath == "index.toml"
            && artifact.Disposition == IngestArtifactDisposition.Admitted);

        IngestArtifact nynorsk = Assert.Single(graph.Selected, artifact =>
            artifact.RelativePath.EndsWith("omw-nn/omw-nn.xml", StringComparison.Ordinal));
        var records = await ReadAllAsync(nynorsk.Path, nynorsk.FileLabel);
        var header = Assert.Single(records.OfType<OmwLmfLexicon>());
        Assert.Equal("omw-nn", header.Id);
        Assert.Equal("nn", header.LanguageCode);
        Assert.Equal("2.0", header.Version);
        Assert.NotEmpty(records.OfType<OmwLmfLexicalEntry>());
        Assert.Contains(records.OfType<OmwLmfSynset>(), synset => synset.Ili.Length > 0);
    }

    private static async Task<List<OmwLmfRecord>> ReadAllAsync(string path, string label)
    {
        var records = new List<OmwLmfRecord>();
        await foreach (OmwLmfRecord record in OMWLmfParser.ReadAsync(path, label))
            records.Add(record);
        return records;
    }

    private static SubstrateChange EmitSynsetRelation(
        string lexicon, string subject, OmwLmfRelation relation)
    {
        var builder = new SubstrateChangeBuilder(
            OMWDecomposer.Source, $"omw-lmf-relation-test/{subject}/{relation.Type}");
        OMWLmfEmitter.Emit(builder, new OmwLmfSynset(
            lexicon, "xy", subject, "", "n", "true", "noun.attribute", "",
            [], [], [], [relation]));
        return builder.Build();
    }

    private static void AssertEdge(
        SubstrateChange change, Hash128 subject, string relation, Hash128 obj)
    {
        Hash128 type = RelationTypeRegistry.RelationTypeId(relation);
        Assert.Contains(change.Attestations, attestation =>
            attestation.SubjectId == subject
            && attestation.TypeId == type
            && attestation.ObjectId == obj);
    }

    private static void AssertUnorderedEdge(
        SubstrateChange change, Hash128 left, string relation, Hash128 right)
    {
        Hash128 type = RelationTypeRegistry.RelationTypeId(relation);
        Assert.Contains(change.Attestations, attestation =>
            attestation.TypeId == type
            && ((attestation.SubjectId == left && attestation.ObjectId == right)
                || (attestation.SubjectId == right && attestation.ObjectId == left)));
    }

    private static async Task WriteAsync(string root, string relative, string content)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private static string NewRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), $"laplace-omw-lmf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private const string FixtureXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <LexicalResource xmlns="http://globalwordnet.org/schemas/WN-LMF/1.4"
                         xmlns:dc="https://globalwordnet.github.io/schemas/dc/">
          <Lexicon id="omw-xy" label="Example Wordnet" language="xy" version="2.0"
                   license="https://license.example/xy" url="https://example/xy"
                   citation="Example Citation" email="editor@example.test">
            <Requires ref="oewn" version="2025" />
            <LexicalEntry id="omw-xy-mouse-n" index="mouse">
              <Lemma writtenForm="mouse" partOfSpeech="n" type="brokenplural" />
              <Form writtenForm="mice"><Tag category="number">plural</Tag></Form>
              <Sense id="omw-xy-mouse-sense" synset="omw-xy-mouse-n" n="1"
                     dc:identifier="mouse%1:05:00::" subcat="frame-1"
                     adjposition="attributive">
                <Count>17</Count>
                <SenseRelation target="omw-xy-rat-sense" relType="antonym" confidence="0.75" />
              </Sense>
            </LexicalEntry>
            <Synset id="omw-xy-mouse-n" ili="i1" partOfSpeech="n"
                    members="omw-xy-mouse-n" lexicalized="true" lexfile="noun.animal"
                    dc:identifier="mouse.n.01">
              <Definition>an animal</Definition>
              <Example>a mouse ran</Example>
              <SynsetRelation target="omw-xy-animal-n" relType="hypernym" />
            </Synset>
            <SyntacticBehaviour id="frame-1" subcategorizationFrame="Somebody ----s" />
          </Lexicon>
        </LexicalResource>
        """;
}
