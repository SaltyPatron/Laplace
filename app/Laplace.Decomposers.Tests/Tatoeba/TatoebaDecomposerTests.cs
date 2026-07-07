using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Tatoeba.Tests;

// Validates the tier/identity fix (.scratchpad/16 §2a): Tatoeba translation edges must
// anchor on the CONTENT ROOTS of the sentence text (content-addressed, shared with any
// other source that ingests the same text), NOT on synthetic per-id Tatoeba_Sentence ref
// entities. HAS_LANGUAGE must sit on the content root, once per sentence.
public sealed class TatoebaDecomposerTests
{
    static TatoebaDecomposerTests()
    {
        CodepointPerfcache.Load(ResolvePerfcacheBlob());
        LanguageReference.EnsureLoaded(TestIngestPaths.Iso639);
    }

    private static string ResolvePerfcacheBlob() => TestInstall.ResolvePerfcacheOrThrow();

    private const string EnText = "The cat sat on the mat.";
    private const string FrText = "Le chat s'est assis sur le tapis.";

    [Fact]
    public async Task Translation_Anchors_On_Content_Roots_Not_Ref_Entities()
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
            Hash128 languageType = RelationTypeRegistry.Resolve("HAS_LANGUAGE").Id;

            var translationSubjects = new HashSet<Hash128>();
            var translationObjects = new HashSet<Hash128>();
            var languageSubjects = new HashSet<Hash128>();

            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
                foreach (var a in change.Attestations)
                {
                    if (a.TypeId == translationType)
                    {
                        translationSubjects.Add(a.SubjectId);
                        if (a.ObjectId is { } o) translationObjects.Add(o);
                    }
                    else if (a.TypeId == languageType) languageSubjects.Add(a.SubjectId);
                }

            Hash128 enRef = SourceEntityIdConventions.TatoebaSentence(1);
            Hash128 frRef = SourceEntityIdConventions.TatoebaSentence(2);

            // Exactly one translation edge between two content-addressed roots.
            Assert.Single(translationSubjects);
            Assert.Single(translationObjects);

            // The core fix: the edge must NOT anchor on the synthetic per-id ref entities
            // (Tatoeba_Sentence). Those broke content-addressing (same text ≠ merged across
            // sources). Translation now rides the real UAX content roots.
            Assert.DoesNotContain(enRef, translationSubjects);
            Assert.DoesNotContain(frRef, translationSubjects);
            Assert.DoesNotContain(enRef, translationObjects);
            Assert.DoesNotContain(frRef, translationObjects);

            // Both endpoints are the content roots that carry the sentence's HAS_LANGUAGE —
            // i.e. the same entity any other source (OpenSubtitles, a UAX parse) produces for
            // that text — proving the edge is on the content root, not the ref entity.
            Assert.True(translationSubjects.IsSubsetOf(languageSubjects),
                "translation subject must be a content root that also carries HAS_LANGUAGE");
            Assert.True(translationObjects.IsSubsetOf(languageSubjects),
                "translation object must be a content root that also carries HAS_LANGUAGE");
            Assert.DoesNotContain(enRef, languageSubjects);
            Assert.DoesNotContain(frRef, languageSubjects);
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

            int translationEdges = 0;
            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
                foreach (var a in change.Attestations)
                    if (a.TypeId == translationType) translationEdges++;

            // No content root for id 999 -> the link is skipped, never a dangling edge.
            Assert.Equal(0, translationEdges);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
