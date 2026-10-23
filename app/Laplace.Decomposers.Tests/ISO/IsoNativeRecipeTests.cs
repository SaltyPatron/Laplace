using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Tests.ISO;

public sealed class IsoNativeRecipeTests
{
    [Fact]
    public async Task RealRecordsRetainCodesNamesMembershipRetirementsScriptsAndRegistryValues()
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out string repository));
        string root = Path.Combine(repository, "app", "Laplace.Decomposers.Tests", "Fixtures",
            "NativeRecipes", "iso-639");
        // Complete records selected from the local SIL, LOC, IANA and CLDR files.
        // CLDR keeps its complete language element in an XML packaging wrapper.
        string[] actual = await NativeCorpusAssertions.ClaimsAsync("iso-639",
            Directory.GetFiles(root).Order(StringComparer.Ordinal).ToArray());
        var expected = new List<string>
        {
            "afa\tAfro-Asiatic languages", "afa\tafro-asiatiques, langues",
            "eng\ten", "eng\tEnglish", "eng\tanglais",
            "ger\tdeu", "ger\tde", "ger\tGerman", "ger\tallemand",
            "fri\treference name of language\tWestern Frisian", "fri\tC",
            "fri\tthe identifier to which all instances of this Id should be changed\tfry",
            "amd\treference name of language\tAmapá Creole", "amd\tN",
            "bvs\treference name of language\tBelgian Sign Language", "bvs\tS",
            "bvs\tThe instructions for updating an instance of the retired (split) identifier\t"
                + "Split into Langue des signes de Belgique Francophone [sfb], and Vlaamse Gebarentaal [vgt]",
            "fat\ta member of the macrolanguage\taka", "fat\tA",
            "twi\ta member of the macrolanguage\taka", "twi\tA",
            "aa\tused with\tLatn",
        };
        foreach (var (code, bibliographic, shortCode, name) in new[]
        {
            ("deu", "ger", "de", "German"), ("eng", "eng", "en", "English"),
        })
        {
            expected.Add(code + "\tEquivalent 639-2 identifier of the bibliographic applications code set\t" + bibliographic);
            expected.Add(code + "\tEquivalent 639-2 identifier of the terminology applications code set\t" + code);
            expected.Add(code + "\tEquivalent 639-1 identifier\t" + shortCode);
            expected.Add(code + "\tI");
            expected.Add(code + "\tL");
            expected.Add(code + "\tReference language name\t" + name);
            expected.Add(code + "\tOne of the names associated with this identifier\t" + name);
            expected.Add(code + "\tThe inverted form of this Print_Name form\t" + name);
            expected.Add(shortCode + "\tlanguage");
            expected.Add(shortCode + "\ta description of the tag or subtag in the record\t" + name);
            expected.Add(shortCode + "\ta script used to write the overwhelming majority of documents for the given language\tLatn");
        }
        string[] products = actual.Select(static line => string.Join('\t', line.Split('\t').Skip(3)))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(44, products.Length);
        Assert.Equal(expected.Order(StringComparer.Ordinal), products);
        // ISO publishes names, not dictionary glosses. The registry's own description
        // remains on the registry subtag, rather than becoming a fabricated definition.
        Assert.DoesNotContain(products, static line => line.Contains("HAS_DEFINITION", StringComparison.Ordinal));
    }
}
