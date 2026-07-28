using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Tatoeba.Tests;

// sentences.csv is the ENTITY file; links.csv is the ATTESTATION file.
//
// Tatoeba asserts two things and no more: this text exists in this language, and this text
// translates that text. Both are facts about SENTENCES, so both are attested on the
// content-addressed roots. The numeric row id is scaffolding — it exists only because the
// links file cannot inline the text — so it is resolved at initialize and never stored.
//
// These tests previously asserted the opposite: that links minted a `tatoeba/sentence/{id}`
// entity per side and attested between those, with the real sentence-level translation left
// as a read-side join across HAS_EXTERNAL_ID. That is source-keyed identity — a row number
// promoted to an entity id — which is the entity-resolution table content addressing exists
// to abolish, and it cost ~1.56 entity rows per link (measured: the largest row category of
// the link phase).
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

    private static async Task<(List<AttestationRow> Attestations, List<EntityRow> Entities)> RunAsync(string dir)
    {
        var dec = new TatoebaDecomposer();
        var ctx = new FakeContext(dir, new NullWriter());
        // InitializeAsync builds the id -> content-root map the link lane resolves through.
        // IngestRunner calls it before DecomposeAsync (IngestRunner.cs:106); the decomposer
        // throws rather than silently dropping every link if it was skipped.
        await dec.InitializeAsync(ctx);

        var attestations = new List<AttestationRow>();
        var entities = new List<EntityRow>();
        await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
        {
            attestations.AddRange(change.Attestations);
            entities.AddRange(change.Entities);
        }
        return (attestations, entities);
    }

    [Fact]
    public async Task Translation_Is_Attested_Between_Content_Roots_And_Mints_No_Surrogate()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-tatoeba-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "sentences.csv"),
                $"1\teng\t{EnText}\n2\tfra\t{FrText}\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(dir, "links.csv"), "1\t2\n", new UTF8Encoding(false));

            var (attestations, entities) = await RunAsync(dir);

            Hash128 translationType = RelationTypeRegistry.Resolve("IS_TRANSLATION_OF").Id;
            Hash128 languageType = RelationTypeRegistry.Resolve("HAS_LANGUAGE").Id;

            Hash128 enRoot = ContentTierSpine.ResolveRoot(EnText)!.Value;
            Hash128 frRoot = ContentTierSpine.ResolveRoot(FrText)!.Value;

            // The translation is between the SENTENCES. IS_TRANSLATION_OF is symmetric —
            // endpoints canonicalize by hash order — so assert the endpoint SET.
            var edges = attestations.Where(a => a.TypeId == translationType).ToList();
            Assert.Single(edges);
            var endpoints = new HashSet<Hash128> { edges[0].SubjectId };
            if (edges[0].ObjectId is { } o) endpoints.Add(o);
            Assert.Equal(new HashSet<Hash128> { enRoot, frRoot }, endpoints);

            // Language sits on the content root, once per sentence.
            var langSubjects = attestations.Where(a => a.TypeId == languageType)
                                           .Select(a => a.SubjectId).ToHashSet();
            Assert.Contains(enRoot, langSubjects);
            Assert.Contains(frRoot, langSubjects);

            // The row number is scaffolding: no entity anywhere carries a surrogate id, and
            // HAS_EXTERNAL_ID is not emitted at all.
            Hash128 surrogate1 = Hash128.OfCanonical("tatoeba/sentence/1");
            Hash128 surrogate2 = Hash128.OfCanonical("tatoeba/sentence/2");
            Assert.DoesNotContain(surrogate1, entities.Select(e => e.Id));
            Assert.DoesNotContain(surrogate2, entities.Select(e => e.Id));
            Assert.DoesNotContain("HAS_EXTERNAL_ID", TatoebaSource.Relations);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Link_To_Absent_Sentence_Is_Dropped_Not_Grounded_On_A_Synthetic_Node()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-tatoeba-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Only sentence 1 exists; the link references 1 -> 999 (absent).
            await File.WriteAllTextAsync(Path.Combine(dir, "sentences.csv"),
                $"1\teng\t{EnText}\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(dir, "links.csv"), "1\t999\n", new UTF8Encoding(false));

            var (attestations, entities) = await RunAsync(dir);

            Hash128 translationType = RelationTypeRegistry.Resolve("IS_TRANSLATION_OF").Id;

            // An edge between an id we cannot resolve to text asserts nothing about language.
            // It is DROPPED — not grounded on a bare synthetic node that would read as an
            // unattested entity pretending to be a sentence.
            Assert.Empty(attestations.Where(a => a.TypeId == translationType));
            Assert.DoesNotContain(Hash128.OfCanonical("tatoeba/sentence/999"), entities.Select(e => e.Id));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
