using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Tests.OMW;

public sealed class OmwNativeRecipeTests
{
    [Fact]
    public async Task RealLexicalGapRetainsItsPhraseDefinitionAndBothExplicitFalseValues()
    {
        string[] claims = await NativeCorpusAssertions.ClaimsAsync("open-multilingual-wordnet", Fixture());
        Assert.Equal(new[]
        {
            "tenda per doccia\tlexical sense\t[tenda per doccia, language:ita, i58874]",
            "tenda per doccia\tNOUN",
            "[tenda per doccia, language:ita, i58874]\treference\tomw-it-04209239-n",
            "[tenda per doccia, language:ita, i58874]\tis lexicalized\tfalse",
            "omw-it-04209239-n\ttenda per doccia",
            "omw-it-04209239-n\ti58874",
            "omw-it-04209239-n\tNOUN",
            "omw-it-04209239-n\tis lexicalized\tfalse",
            "omw-it-04209239-n\tdefinition of the synset\tle tende che impediscono all'acqua di uscire dall'area della doccia",
            "[wordnet-pos, n]\tdenotes\tNOUN",
            "[wordnet-pos, n]\tdenotes\tNOUN",
            "omw-it-04209239-n\tidentifier vocabulary\tSynset",
            "omw-it-04209239-n\tidentifier vocabulary\tSynset",
            "i58874\tidentifier vocabulary\tili",
            "language:ita\tidentifier vocabulary\tlanguage",
        }.Order(StringComparer.Ordinal), claims.Select(Product).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("true", 2)]
    [InlineData(null, 0)]
    public async Task ExplicitValuesRemainContentAndMissingAttributesDoNotFabricateClaims(string? value, int expected)
    {
        string directory = Path.Combine(Path.GetTempPath(), "laplace-omw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "omw-it.xml");
        try
        {
            // A controlled variation of the real record tests value handling;
            // it is not represented as another upstream corpus observation.
            string xml = await File.ReadAllTextAsync(Fixture());
            xml = xml.Replace(" lexicalized=\"false\"", value is null ? "" : " lexicalized=\"" + value + "\"",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, xml);
            string[] claims = await NativeCorpusAssertions.ClaimsAsync("open-multilingual-wordnet", path);
            string[] properties = claims.Select(Product).Where(static product => product.Contains("\tis lexicalized\t",
                StringComparison.Ordinal)).ToArray();
            Assert.Equal(expected, properties.Length);
            Assert.All(properties, product => Assert.EndsWith("\t" + value, product));
            Assert.Equal(13 + expected, claims.Length);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static string Product(string claim) => string.Join('\t', claim.Split('\t').Skip(3));

    private static string Fixture()
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out string repository));
        return Path.Combine(repository, "app", "Laplace.Decomposers.Tests", "Fixtures", "NativeRecipes", "omw", "omw-it.xml");
    }
}
