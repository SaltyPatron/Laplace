using System.Text;
using System.Xml.Linq;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

// The window setting is process-wide; keep these real Engine executions apart
// from tests that change the process environment.
[Collection("cpu-topology-global")]
public sealed class NativeXmlRecipeTests
{
    [Theory]
    [InlineData("open-english-wordnet", "english-wordnet-sample.xml")]
    [InlineData("framenet", "Abandonment.xml")]
    [InlineData("propbank", "abandon.xml")]
    public async Task XmlRecordTypesAndAttributesSurviveWindowAndWorkerBoundaries(string source, string name)
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out string repository));
        string path = source == "propbank"
            ? Path.Combine(Directory.GetParent(EtlManifest.RecipesRoot)!.FullName, "tests", "fixtures",
                "highway-identities", "propbank", name)
            : Path.Combine(repository, "app", "Laplace.Substrate.Tests", "Fixtures", "NativeRecipes", "xml", name);
        var document = XDocument.Load(path);
        string? previous = Environment.GetEnvironmentVariable("LAPLACE_RECORD_WINDOW_BYTES");
        try
        {
            // A token, an attribute and a complete record all cross these tiny
            // reads. The second run uses the normal production window.
            Environment.SetEnvironmentVariable("LAPLACE_RECORD_WINDOW_BYTES", "17");
            string[] windowed = await Claims(source, path, 4);
            Environment.SetEnvironmentVariable("LAPLACE_RECORD_WINDOW_BYTES", null);
            string[] normal = await Claims(source, path, 1);
            Assert.Equal(normal.Order(StringComparer.Ordinal), windowed.Order(StringComparer.Ordinal));

            var claims = windowed.ToHashSet(StringComparer.Ordinal);
            IEnumerable<XElement> Elements(string element) => document.Descendants()
                .Where(node => node.Name.LocalName == element);
            static string Attr(XElement node, string attribute) => node.Attribute(attribute)!.Value;

            if (source == "open-english-wordnet")
            {
                Assert.Single(Elements("LexicalEntry"));
                Assert.Single(Elements("Synset"));
                foreach (XElement entry in Elements("LexicalEntry"))
                {
                    XElement lemma = entry.Elements().Single(node => node.Name.LocalName == "Lemma");
                    Assert.Contains($"{Attr(lemma, "writtenForm")}\t{Attr(lemma, "partOfSpeech")}", claims);
                    Assert.Contains(claims, claim => claim.StartsWith(Attr(lemma, "writtenForm") + "\tlexical sense\t",
                        StringComparison.Ordinal));
                }
                foreach (XElement synset in Elements("Synset"))
                {
                    string id = Attr(synset, "id");
                    Assert.Contains($"{id}\t{Attr(synset, "ili")}", claims);
                    Assert.Contains($"{id}\t{Attr(synset, "lexfile")}", claims);
                    foreach (XElement definition in synset.Elements().Where(node => node.Name.LocalName == "Definition"))
                        Assert.Contains($"{id}\tdefinition of the synset\t{definition.Value}", claims);
                }
            }
            else if (source == "framenet")
            {
                string frame = Attr(document.Root!, "name");
                Assert.Equal(12, Elements("FE").Count());
                Assert.Equal(5, Elements("lexUnit").Count());
                foreach (XElement element in Elements("FE"))
                {
                    string id = $"[{frame}, {Attr(element, "name")}]";
                    Assert.Contains($"{frame}\tframe element\t{id}", claims);
                    Assert.Contains($"{id}\t{Attr(element, "coreType")}", claims);
                    string abbreviation = Attr(element, "abbrev");
                    if (abbreviation.Length > 0) Assert.Contains($"{id}\tabbreviated\t{abbreviation}", claims);
                }
                foreach (XElement unit in Elements("lexUnit"))
                {
                    string id = $"[{frame}, {Attr(unit, "name")}]";
                    Assert.Contains($"{frame}\tlexical unit\t{id}", claims);
                    Assert.Contains($"{id}\t{Attr(unit, "POS")}", claims);
                    XElement lexeme = Assert.Single(unit.Elements(), node => node.Name.LocalName == "lexeme");
                    Assert.Contains(string.Join('\t', id, Attr(lexeme, "name"), Attr(lexeme, "POS"),
                        Attr(lexeme, "breakBefore"), Attr(lexeme, "headword")), claims);
                }
            }
            else
            {
                Assert.Equal(3, Elements("roleset").Count());
                foreach (XElement roleset in Elements("roleset"))
                {
                    string id = Attr(roleset, "id");
                    Assert.Contains($"abandon\tsense\t{id}", claims);
                    Assert.Contains($"{id}\tsense description\t{Attr(roleset, "name")}", claims);
                    foreach (XElement role in roleset.Descendants().Where(node => node.Name.LocalName == "role"))
                        Assert.Contains($"{id}\tmneumonic\t{Attr(role, "descr")}", claims);
                }
            }
        }
        finally { Environment.SetEnvironmentVariable("LAPLACE_RECORD_WINDOW_BYTES", previous); }
    }

    [Theory]
    [InlineData(17, 1)]
    [InlineData(17, 4)]
    [InlineData(8388608, 1)]
    [InlineData(8388608, 4)]
    public async Task NonScalarAddressesRetainTheirOwnIdentityAndUncachedProperties(int window, int threads)
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out string repository));
        string path = Path.Combine(repository, "app", "Laplace.Substrate.Tests", "Fixtures", "NativeRecipes",
            "ucd-surrogates", "ucd.all.flat.xml");
        var document = XDocument.Load(path);
        var expected = new List<string>();
        foreach (XElement range in document.Descendants("surrogate"))
        {
            string subject = $"[{range.Attribute("first-cp")!.Value}, {range.Attribute("last-cp")!.Value}]";
            foreach ((string attribute, string property) in new[]
                { ("age", "Age"), ("blk", "Block"), ("gc", "General_Category"), ("lb", "Line_Break") })
                expected.Add($"{subject}\t{property}\t{range.Attribute(attribute)!.Value}");
        }
        Assert.Equal(12, expected.Count);
        foreach (XElement block in document.Descendants("block"))
        {
            string subject = block.Attribute("name")!.Value;
            string first = block.Attribute("first-cp")!.Value;
            string last = block.Attribute("last-cp")!.Value;
            expected.Add($"{subject}\tStart Code\t{(first == "0000" ? "\0" : first)}");
            expected.Add($"{subject}\tEnd Code\t{(last == "007F" ? "\u007f" : last)}");
        }
        Assert.Equal(20, expected.Count);
        string? previous = Environment.GetEnvironmentVariable("LAPLACE_RECORD_WINDOW_BYTES");
        try
        {
            Environment.SetEnvironmentVariable("LAPLACE_RECORD_WINDOW_BYTES", window.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string[] claims = await Claims("unicode", path, threads);
            Assert.Equal(expected.Order(StringComparer.Ordinal), claims.Order(StringComparer.Ordinal));
            Assert.DoesNotContain(claims, static claim => claim.Contains("type:#-1", StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable("LAPLACE_RECORD_WINDOW_BYTES", previous); }
    }

    [Theory]
    [InlineData("ucd-invalid-atom")]
    [InlineData("ucd-invalid-sequence")]
    public async Task InvalidScalarProductsFailBeforeTheyCanBecomeZeroChildEntities(string fixture)
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out string repository));
        string path = Path.Combine(repository, "app", "Laplace.Substrate.Tests", "Fixtures", "NativeRecipes",
            fixture, "ucd.all.flat.xml");
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = await NativeRecipeExecution.Configured().ExecuteAsync(
            new NativeRecipeRequest(NativeRecipeMode.Claims, "unicode", [path], Threads: 1), output, error);
        Assert.NotEqual(0, code);
        Assert.Contains("invalid scalar content", Encoding.UTF8.GetString(error.ToArray()), StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(path), Encoding.UTF8.GetString(error.ToArray()), StringComparison.Ordinal);
    }

    private static async Task<string[]> Claims(string source, string path, int threads)
    {
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = await NativeRecipeExecution.Configured().ExecuteAsync(
            new NativeRecipeRequest(NativeRecipeMode.Claims, source, [path], Threads: threads), output, error);
        string stdout = Encoding.UTF8.GetString(output.ToArray());
        Assert.True(code == 0, Encoding.UTF8.GetString(error.ToArray()) + stdout);
        Assert.Contains("unresolved obligations: 0", stdout, StringComparison.Ordinal);
        return stdout.Split('\n').Select(static line => line.TrimEnd('\r'))
            .Where(static line => line.StartsWith("claim\t", StringComparison.Ordinal))
            .Select(static line => line.Split('\t', 4)[3]).ToArray();
    }
}
