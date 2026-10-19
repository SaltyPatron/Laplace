using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.ConceptNet;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Decomposers.ConceptNet.Tests;

// ConceptNet extraction keeps the /wn/ synset suffix and the POS segment of each concept URI.
public sealed class ConceptNetDecomposerTests
{
    static ConceptNetDecomposerTests()
    {
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());
    }

    [Fact]
    public async Task Extract_DepositsUposOnTheLexicalization_For_Wn_Suffixed_Concepts()
    {
        string cili = TestInstall.ResolveCiliOrFallback();
        if (!TestInstall.HasFullCiliMap(cili)) return;

        string dir = Path.Combine(Path.GetTempPath(), "laplace-cn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // dog/n/wn/animal — POS + synset hub; pet/n — POS only.
            await File.WriteAllTextAsync(
                Path.Combine(dir, "assertions.csv"),
                "a1\t/r/RelatedTo\t/c/en/dog/n/wn/animal\t/c/en/pet/n\t{\"weight\": 2.0}\n",
                new UTF8Encoding(false));

            var dec = new ConceptNetDecomposer();
            var ctx = new FakeContext(dir, new NullWriter());

            Hash128 hasPos = RelationTypeRegistry.RelationTypeId("HAS_POS");
            Hash128 sense = RelationTypeRegistry.RelationTypeId("Sense");
            Hash128 relatedTo = RelationTypeRegistry.RelationTypeId("RelatedTo");
            Hash128? dogRoot = ContentTierSpine.ResolveRoot("dog");
            Hash128? petRoot = ContentTierSpine.ResolveRoot("pet");
            Hash128? synId = ConceptAnchor.SynsetId(1313093, 'n', "pwn30");
            string? dogIli = SourceEntityIdConventions.WordNetIli(1313093, 'n', "pwn30");
            Hash128? iliId = dogIli is null ? null : ContentEmitter.RootId(dogIli);
            Hash128? english = ContentEmitter.RootId("en");

            Assert.NotNull(dogRoot);
            Assert.NotNull(petRoot);
            Assert.NotNull(synId);
            Assert.NotNull(iliId);
            Assert.NotNull(english);

            Hash128 lexicalization = Hash128.Merkle(
                (byte)(EntityTier.Word + 1),
                [dogRoot.Value, english.Value, iliId.Value]);
            var senseSubjects = new HashSet<Hash128>();
            var relatedEdges = new List<AttestationRow>();
            var bankCarriers = new HashSet<Hash128>();

            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default).WithoutWriter())
            {
                foreach (var a in change.Attestations)
                {
                    Assert.NotEqual(hasPos, a.TypeId);
                    if (a.TypeId == sense && a.SubjectId is { } cs)
                        senseSubjects.Add(cs);
                    if (a.TypeId == relatedTo)
                        relatedEdges.Add(a);
                }
                if (!change.IntentStages.IsDefaultOrEmpty)
                {
                    var parsed = CopyTupleParser.ParsePhysicalities(
                        change.IntentStages
                            .Where(static stage => !stage.IsInvalid)
                            .Select(static stage => stage.TupleBuffer(IntentStageTable.Physicalities))
                            .ToList());
                    for (int i = 0; i < parsed.Types.Count; i++)
                        if (parsed.Types[i] == (short)PhysicalityType.LexicalBank)
                            bankCarriers.Add(parsed.EntityIds[i]);
                }
            }

            var semanticEdge = Assert.Single(relatedEdges);
            var endpoints = new HashSet<Hash128>
            {
                semanticEdge.SubjectId,
                semanticEdge.ObjectId!.Value,
            };
            Assert.Contains(synId.Value, endpoints);
            Assert.Contains(petRoot.Value, endpoints);
            Assert.DoesNotContain(dogRoot.Value, endpoints);
            Assert.Contains(lexicalization, bankCarriers);
            Assert.Contains(iliId.Value, bankCarriers);
            Assert.DoesNotContain(dogRoot.Value, bankCarriers);
            Assert.DoesNotContain(petRoot.Value, bankCarriers);
            Assert.Contains(dogRoot.Value, senseSubjects);
            Assert.DoesNotContain(petRoot.Value, senseSubjects);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Repeated_Node_Metadata_Is_One_Source_Declaration_Not_Edge_Degree()
    {
        string cili = TestInstall.ResolveCiliOrFallback();
        if (!TestInstall.HasFullCiliMap(cili)) return;

        string dir = Path.Combine(Path.GetTempPath(), "laplace-cn-degree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "assertions.csv"),
                "a1\t/r/RelatedTo\t/c/en/dog/n/wn/animal\t/c/en/pet/n\t{\"weight\": 1.0}\n"
                + "a2\t/r/CapableOf\t/c/en/dog/n/wn/animal\t/c/en/bark/v\t{\"weight\": 1.0}\n",
                new UTF8Encoding(false));

            var dec = new ConceptNetDecomposer();
            var ctx = new FakeContext(dir, new NullWriter());
            var attestations = new List<AttestationRow>();
            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default).WithoutWriter())
                attestations.AddRange(change.Attestations);

            Hash128 dog = ContentTierSpine.ResolveRoot("dog")!.Value;
            Hash128 english = ContentEmitter.RootId("en")!.Value;

            static AttestationRow One(
                IEnumerable<AttestationRow> rows, Hash128 subject, string relation, Hash128 obj) =>
                Assert.Single(rows, a =>
                    a.SubjectId == subject
                    && a.TypeId == RelationTypeRegistry.RelationTypeId(relation)
                    && a.ObjectId == obj);

            Assert.DoesNotContain(attestations, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_POS"));
            Hash128 lexicalization = Hash128.Merkle(
                (byte)(EntityTier.Word + 1),
                [dog, english, ContentEmitter.RootId(
                    SourceEntityIdConventions.WordNetIli(1313093, 'n', "pwn30")!)!.Value]);
            Assert.Equal(1, One(attestations, dog, "Sense", lexicalization).ObservationCount);
            Assert.DoesNotContain(attestations, a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_LANGUAGE"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // NotDesires is ConceptNet's predicate. A negative weight is not how the file denies.
    [Theory]
    [InlineData("/r/NotDesires", "NotDesires")]
    [InlineData("/r/NotUsedFor", "NotUsedFor")]
    [InlineData("/r/NotCapableOf", "NotCapableOf")]
    [InlineData("/r/NotHasProperty", "NotHasProperty")]
    public void NotRelations_KeepTheSourcePredicate(
        string relationUri, string expectedType)
    {
        Assert.True(ConceptNetRelations.TryResolveType(
            System.Text.Encoding.UTF8.GetBytes(relationUri), out var typeName,
            out _, out bool negated));
        Assert.Equal(expectedType, typeName);
        Assert.False(negated);
    }

    [Theory]
    [InlineData("/r/Desires")]
    [InlineData("/r/UsedFor")]
    [InlineData("/r/CapableOf")]
    [InlineData("/r/HasProperty")]
    public void PositiveRelations_AreNotNegated(string relationUri)
    {
        Assert.True(ConceptNetRelations.TryResolveType(
            System.Text.Encoding.UTF8.GetBytes(relationUri), out _, out _, out bool negated));
        Assert.False(negated);
    }
}
