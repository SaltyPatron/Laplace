using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[CollectionDefinition("LanguageReference")]
public sealed class LanguageReferenceCollection { }

[Collection("LanguageReference")]
[Trait("Tier", "fast")]
public sealed class LanguageReferenceFastTests : IDisposable
{
    private readonly string _dir;

    public LanguageReferenceFastTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "laplace-iso639-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        File.WriteAllLines(Path.Combine(_dir, "iso-639-3.tab"), new[]
        {
            "Id\tPart2B\tPart2T\tPart1\tScope\tType\tRef_Name\tComment",
            "eng\teng\teng\ten\tI\tL\tEnglish\t",
            "deu\tger\tdeu\tde\tI\tL\tGerman\t",
            "fra\tfre\tfra\tfr\tI\tL\tFrench\t",
        });
        File.WriteAllLines(Path.Combine(_dir, "ISO-639-2_utf-8.txt"), new[]
        {
            "ger|deu|de|German|allemand",
            "fre|fra|fr|French|français",
            "ber|||Berber languages|berbères, langues",
        });

        LanguageReference.Load(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void CollectiveCode_Ber_ResolvesToItself_NotUnd()
    {
        Assert.Equal("ber", LanguageReference.ResolveCode("ber"));
        Assert.Equal(LanguageEntityId.FromIso639_3("ber"), LanguageReference.Resolve("ber"));
        Assert.NotEqual(LanguageEntityId.FromIso639_3("und"), LanguageReference.Resolve("ber"));
    }

    [Fact]
    public void BibliographicAndAlpha2_ConvergeToTerminologicId()
    {
        var deu = LanguageEntityId.FromIso639_3("deu");
        Assert.Equal(deu, LanguageReference.Resolve("deu"));
        Assert.Equal(deu, LanguageReference.Resolve("ger"));
        Assert.Equal(deu, LanguageReference.Resolve("de"));
    }

    [Fact]
    public void Unknown_RoutesToUnd_AndIsCounted()
    {
        long before = LanguageReference.ResolveMisses;
        Assert.Equal(LanguageEntityId.FromIso639_3("und"), LanguageReference.Resolve("zz-nope"));
        Assert.True(LanguageReference.ResolveMisses > before, "miss must be counted, never silent");
    }
}
