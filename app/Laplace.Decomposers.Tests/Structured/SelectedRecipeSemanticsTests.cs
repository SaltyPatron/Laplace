using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

public sealed class SelectedRecipeSemanticsTests
{
    [Theory]
    [InlineData("cili", "ili.ttl", "i46360")]
    [InlineData("propbank", "abandon.xml", "abandon.01")]
    [InlineData("framenet", "Abandonment.xml", "Abandonment")]
    public async Task SourceIdentifiersRemainSubjectsWhileDefinitionsRemainTheirClaims(
        string source, string file, string identifier)
    {
        using var fixture = new NativeCorpus();
        Assert.True(LaplaceInstall.TryRepoRoot(out string repository));
        string path = source is "cili" or "propbank"
            ? Path.Combine(Directory.GetParent(fixture.Execution.Recipes)!.FullName,
                "tests", "fixtures", "highway-identities", source, file)
            : Path.Combine(repository, "app", "Laplace.Substrate.Tests", "Fixtures", "NativeRecipes", "xml", file);
        string[] claims = (await fixture.Claims(source, path)).Claims();
        Assert.NotEmpty(claims);
        Assert.Contains(claims, line => line.Split('\t')[3] == identifier);
        Assert.DoesNotContain(claims, line => line.Split('\t')[4] is "definition" or "frame" or "roleset");
        Assert.DoesNotContain(claims, line => line.Split('\t')[3].StartsWith("claim:", StringComparison.Ordinal)
            || line.Split('\t')[3].StartsWith("wit:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IncompleteWordNetScopeRetainsPreviewedSubjectsAndRejectsMissingReferences()
    {
        using var fixture = new NativeCorpus();
        Assert.True(LaplaceInstall.TryRepoRoot(out string repository));
        string path = Path.Combine(repository, "app", "Laplace.Substrate.Tests", "Fixtures", "NativeRecipes", "xml",
            "english-wordnet-sample.xml");
        var result = await fixture.Claims("open-english-wordnet", path);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("semantic_omissions=1", result.Error, StringComparison.Ordinal);
        Assert.Contains("members oewn-hood-n", result.Error, StringComparison.Ordinal);
        Assert.Contains(result.Lines("claim"), line => line.Split('\t')[3] == "oewn-08242255-n"
            && line.Split('\t')[4] == "definition of the synset");
        foreach (string target in new[] { "oewn-07171981-n", "oewn-06960420-n", "oewn-08242011-n" })
            Assert.Contains(result.Lines("claim"), line => line.Split('\t') is { Length: >= 6 } parts
                && parts[3] == "oewn-08242255-n" && parts[5] == target);
    }
}
