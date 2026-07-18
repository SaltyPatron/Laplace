using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Tatoeba.Tests;

// Validates the ORDER-INDEPENDENT Tatoeba design: translation edges anchor on the DETERMINISTIC
// TatoebaSentence(id) external-id (computable from the numeric ids alone — no runtime id->root map,
// so sentences and links ingest fully in parallel). The mesh/merge onto shared content roots is the
// read-side join across the HAS_EXTERNAL_ID bridge (record vs calculate). HAS_LANGUAGE sits on the
// content root, once per sentence.
public sealed class TatoebaDecomposerTests
{
    static TatoebaDecomposerTests()
    {
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(ResolvePerfcacheBlob());
        LanguageReference.EnsureLoaded(TestIngestPaths.Iso639);
    }

    private static string ResolvePerfcacheBlob() => TestInstall.ResolvePerfcacheOrThrow();

    private const string EnText = "The cat sat on the mat.";
    private const string FrText = "Le chat s'est assis sur le tapis.";

    [Fact]
    public async Task Translation_Anchors_On_Deterministic_ExternalIds_Bridged_To_Content_Roots()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-tatoeba-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // id \t lang \t text
            await File.WriteAllTextAsync(Path.Combine(dir, "sentences.csv"),
                $"1\teng\t{EnText}\n2\tfra\t{FrText}\n", new UTF8Encoding(false));
            // idA \t idB
            await File.WriteAllTextAsync(Path.Combine(dir, "links.csv"), "1\t2\n", new UTF8Encoding(false));

            var dec = new TatoebaDecomposer();
            var ctx = new FakeContext(dir, new NullWriter());

            Hash128 translationType = RelationTypeRegistry.Resolve("IS_TRANSLATION_OF").Id;
            Hash128 externalIdType = RelationTypeRegistry.Resolve("HAS_EXTERNAL_ID").Id;
            Hash128 languageType = RelationTypeRegistry.Resolve("HAS_LANGUAGE").Id;

            var translationSubjects = new HashSet<Hash128>();
            var translationObjects = new HashSet<Hash128>();
            var externalIdSubjects = new HashSet<Hash128>();   // content roots
            var externalIdObjects = new HashSet<Hash128>();    // the deterministic ref anchors
            var languageSubjects = new HashSet<Hash128>();

            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
                foreach (var a in change.Attestations)
                {
                    if (a.TypeId == translationType)
                    {
                        translationSubjects.Add(a.SubjectId);
                        if (a.ObjectId is { } o) translationObjects.Add(o);
                    }
                    else if (a.TypeId == externalIdType)
                    {
                        externalIdSubjects.Add(a.SubjectId);
                        if (a.ObjectId is { } o) externalIdObjects.Add(o);
                    }
                    else if (a.TypeId == languageType) languageSubjects.Add(a.SubjectId);
                }

            Hash128 enRef = SourceEntityIdConventions.TatoebaSentence(1);
            Hash128 frRef = SourceEntityIdConventions.TatoebaSentence(2);

            // Order-independent: the translation rides the DETERMINISTIC TatoebaSentence(id) anchors
            // (computable from the ids alone — no sentence-before-link ordering, no runtime map).
            // IS_TRANSLATION_OF is symmetric — endpoints are canonicalized by hash order — so assert
            // the endpoint SET, not the direction.
            Assert.Single(translationSubjects);
            Assert.Single(translationObjects);
            var endpoints = new HashSet<Hash128>(translationSubjects);
            endpoints.UnionWith(translationObjects);
            Assert.Equal(new HashSet<Hash128> { enRef, frRef }, endpoints);

            // The mesh/merge is the read-side join across the HAS_EXTERNAL_ID bridge: each ref anchor
            // is bridged FROM the real content root, which also carries HAS_LANGUAGE — so a query
            // resolves the translation onto the shared, content-addressed roots.
            Assert.Contains(enRef, externalIdObjects);
            Assert.Contains(frRef, externalIdObjects);
            Assert.True(externalIdSubjects.IsSubsetOf(languageSubjects),
                "each ref anchor must bridge from a content root that also carries HAS_LANGUAGE");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Link_To_Absent_Sentence_Emits_No_Dangling_Translation()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-tatoeba-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Only sentence 1 exists; link references 1 -> 999 (absent).
            await File.WriteAllTextAsync(Path.Combine(dir, "sentences.csv"),
                $"1\teng\t{EnText}\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(dir, "links.csv"), "1\t999\n", new UTF8Encoding(false));

            var dec = new TatoebaDecomposer();
            var ctx = new FakeContext(dir, new NullWriter());
            Hash128 translationType = RelationTypeRegistry.Resolve("IS_TRANSLATION_OF").Id;
            Hash128 externalIdType = RelationTypeRegistry.Resolve("HAS_EXTERNAL_ID").Id;

            var translationEdges = new List<(Hash128 S, Hash128? O)>();
            var bridgedRefs = new HashSet<Hash128>();   // refs that a content root bridges to
            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
                foreach (var a in change.Attestations)
                {
                    if (a.TypeId == translationType) translationEdges.Add((a.SubjectId, a.ObjectId));
                    else if (a.TypeId == externalIdType && a.ObjectId is { } o) bridgedRefs.Add(o);
                }

            Hash128 ref1 = SourceEntityIdConventions.TatoebaSentence(1);
            Hash128 ref999 = SourceEntityIdConventions.TatoebaSentence(999);

            // Order-independent: the witnessed link is recorded on the deterministic ref anchors even
            // though sentence 999 is absent — no map to consult, no ordering. The link lane mints both
            // anchors so the edge is referentially sound. (IS_TRANSLATION_OF is symmetric — endpoints
            // canonicalized by hash order — so compare the endpoint SET.)
            Assert.Single(translationEdges);
            var endpoints = new HashSet<Hash128> { translationEdges[0].S };
            if (translationEdges[0].O is { } obj) endpoints.Add(obj);
            Assert.Equal(new HashSet<Hash128> { ref1, ref999 }, endpoints);

            // But 999 has no sentence, so no content root bridges to it: the edge is UNGROUNDED. Only
            // the present sentence (1) gets a HAS_EXTERNAL_ID bridge, so the read-side grounded-
            // translation join naturally excludes this dangling link.
            Assert.Contains(ref1, bridgedRefs);
            Assert.DoesNotContain(ref999, bridgedRefs);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
