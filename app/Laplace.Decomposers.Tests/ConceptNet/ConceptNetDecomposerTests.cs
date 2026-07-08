using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.ConceptNet;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.ConceptNet.Tests;

// Validates .scratchpad/16 §4/P4: ConceptNet extract captures /wn/ synset suffix + POS hub links.
public sealed class ConceptNetDecomposerTests
{
    static ConceptNetDecomposerTests()
    {
        CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());
        LanguageReference.EnsureLoaded(TestIngestPaths.Iso639);
    }

    [Fact]
    public async Task Extract_Emits_HasPos_And_CorrespondsTo_For_Wn_Suffixed_Concepts()
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

            Hash128 hasPos = RelationTypeRegistry.Resolve("HAS_POS").Id;
            Hash128 correspondsTo = RelationTypeRegistry.Resolve("CORRESPONDS_TO").Id;
            Hash128 relatedTo = RelationTypeRegistry.Resolve("RELATED_TO").Id;
            Hash128? dogRoot = ContentTierSpine.ResolveRoot("dog");
            Hash128? petRoot = ContentTierSpine.ResolveRoot("pet");
            Hash128? synId = ConceptAnchor.SynsetId(1313093, 'n');

            Assert.NotNull(dogRoot);
            Assert.NotNull(petRoot);
            Assert.NotNull(synId);

            var hasPosSubjects = new HashSet<Hash128>();
            var synSubjects = new HashSet<Hash128>();
            int relatedEdges = 0;

            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
            {
                foreach (var a in change.Attestations)
                {
                    if (a.TypeId == hasPos && a.SubjectId is { } s)
                        hasPosSubjects.Add(s);
                    if (a.TypeId == correspondsTo && a.SubjectId is { } cs)
                        synSubjects.Add(cs);
                    if (a.TypeId == relatedTo)
                        relatedEdges++;
                }
            }

            Assert.Equal(1, relatedEdges);
            Assert.Contains(dogRoot.Value, hasPosSubjects);
            Assert.Contains(petRoot.Value, hasPosSubjects);
            Assert.Contains(dogRoot.Value, synSubjects);
            Assert.DoesNotContain(petRoot.Value, synSubjects);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
