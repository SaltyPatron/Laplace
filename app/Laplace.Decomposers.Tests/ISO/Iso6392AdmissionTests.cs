using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.ISO.Tests;

public sealed class Iso6392AdmissionTests
{
    [Fact]
    public void Alias_map_resolves_only_codes_owned_by_ISO_639_3_rows()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "laplace-iso-aliases-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "iso-639-3.tab"),
                "Id\tPart2B\tPart2T\tPart1\tScope\tLanguage_Type\tRef_Name\tComment\n"
                + "deu\tger\tdeu\tde\tI\tL\tGerman\t\n"
                + "eng\teng\teng\ten\tI\tL\tEnglish\t\n");

            var aliases = LanguageGraph.LoadIso6393Aliases(root);

            Assert.Equal("deu", LanguageGraph.ResolveIso6393Code(aliases, "ger"));
            Assert.Equal("deu", LanguageGraph.ResolveIso6393Code(aliases, "de"));
            Assert.Equal("eng", LanguageGraph.ResolveIso6393Code(aliases, "en-US"));
            Assert.Null(LanguageGraph.ResolveIso6393Code(aliases, "afa"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ISO_639_2_collective_code_is_not_promoted_to_language_identity()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "laplace-iso6392-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "iso-639-3.tab"),
                "Id\tPart2B\tPart2T\tPart1\tScope\tLanguage_Type\tRef_Name\tComment\n"
                + "eng\teng\teng\ten\tI\tL\t\t\n");
            await File.WriteAllTextAsync(Path.Combine(root, "ISO-639-2_utf-8.txt"),
                "eng||en||\n"
                + "afa||||\n");

            var entities = new List<EntityRow>();
            var attestations = new List<AttestationRow>();
            var context = new FakeContext(root, new NullWriter());

            await foreach (SubstrateChange change in new ISODecomposer().DecomposeAsync(
                context, DecomposerOptions.Default).WithoutWriter())
            {
                entities.AddRange(change.Entities);
                attestations.AddRange(change.Attestations);
            }

            Hash128 eng = LanguageEntityId.FromIso639_3("eng");
            Hash128 afaLanguage = LanguageEntityId.FromIso639_3("afa");
            Hash128 afaCode = Hash128.OfCanonical("iso639-2:afa");

            Assert.Contains(entities, e =>
                e.Id == eng && e.TypeId == EntityTypeRegistry.Language);
            Assert.DoesNotContain(entities, e =>
                e.Id == afaLanguage && e.TypeId == EntityTypeRegistry.Language);
            Assert.Contains(entities, e =>
                e.Id == afaCode && e.TypeId == EntityTypeRegistry.Iso639Code);
            Assert.DoesNotContain(attestations, a =>
                a.SubjectId == afaLanguage && a.ObjectId == afaCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
