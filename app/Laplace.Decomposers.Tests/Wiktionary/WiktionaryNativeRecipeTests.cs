using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Tests.Wiktionary;

public sealed class WiktionaryNativeRecipeTests
{
    [Fact]
    public async Task RealEntriesRetainSeparateLanguagesSensesPronunciationsAndRegionalTags()
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out string repository));
        string fixture = Path.Combine(repository, "app", "Laplace.Decomposers.Tests", "Fixtures",
            "NativeRecipes", "wiktionary", "raw-wiktextract-data.jsonl");
        // Complete records 7 and 8 from the local 2026-08-28 raw extraction.
        // Assert the extracted product; the JSON container is curated packaging.
        string[] actual = await NativeCorpusAssertions.ClaimsAsync("wiktionary", fixture);
        var expected = new List<string>
        {
            "[en, free, num]\tname of the language\tEnglish",
            "[en, free, num]\tword sense\tPronunciation spelling of three",
            "Pronunciation spelling of three\tgloss string with less cleaning\t(colloquial) Pronunciation spelling of three",
            "Pronunciation spelling of three\talternative form of\tthree",
            "[gl, free, verb]\tname of the language\tGalician",
        };
        foreach (string tag in new[] { "alt-of", "colloquial", "pronunciation-spelling" })
            expected.Add("Pronunciation spelling of three\t" + tag);
        foreach (string pronunciation in new[] { "frē", "/fɹiː/", "[fɹɪi̯]", "/fɹɪj/", "/fɹi/" })
            expected.Add("[en, free, num]\tpronunciation\t" + pronunciation);
        expected.AddRange(new[]
        {
            "/fɹiː/\tReceived-Pronunciation", "[fɹɪi̯]\tReceived-Pronunciation",
            "/fɹɪj/\tBritish", "/fɹɪj/\tSouthern", "/fɹɪj/\tStandard", "/fɹi/\tGeneral-American",
        });
        AddSense("first/third-person singular present subjunctive",
            ["first-person", "form-of", "present", "singular", "subjunctive", "third-person"]);
        AddSense("third-person singular imperative", ["form-of", "imperative", "singular", "third-person"]);
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Select(Product).Order(StringComparer.Ordinal));

        void AddSense(string detail, string[] tags)
        {
            string sense = "inflection of frear:" + detail;
            expected.Add("[gl, free, verb]\tword sense\t" + sense);
            expected.Add(sense + "\tinflected form of\tfrear");
            expected.AddRange(tags.Select(tag => sense + "\t" + tag));
        }
    }

    [Fact]
    public async Task CompiledRecipeKeepsExamplesAndSynonymsOnTheirOwnSense()
    {
        string directory = Path.Combine(Path.GetTempPath(), "laplace-wiktionary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "raw-wiktextract-data.jsonl");
            // Controlled compiler input: two senses of one entry, not an upstream corpus proof.
            await File.WriteAllTextAsync(path, """
                {"word":"bank","lang_code":"en","pos":"noun","senses":[{"glosses":["a financial institution"],"examples":[{"text":"She deposited money at the bank."}],"synonyms":[{"word":"depository"}]},{"glosses":["sloping land beside a river"],"examples":[{"text":"They sat on the river bank."}],"synonyms":[{"word":"riverbank"}]}]}
                """ + "\n");
            string[] actual = await NativeCorpusAssertions.ClaimsAsync("wiktionary", path);
            string[] expected =
            [
                "[en, bank, noun]\tword sense\ta financial institution",
                "[en, bank, noun]\tword sense\tsloping land beside a river",
                "a financial institution\tusage example\tShe deposited money at the bank.",
                "a financial institution\tsynonym\tdepository",
                "sloping land beside a river\tusage example\tThey sat on the river bank.",
                "sloping land beside a river\tsynonym\triverbank",
            ];
            Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Select(Product).Order(StringComparer.Ordinal));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string Product(string line) => string.Join('\t', line.Split('\t').Skip(3));
}
