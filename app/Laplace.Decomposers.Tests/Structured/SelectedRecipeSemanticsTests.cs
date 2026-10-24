using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

public sealed class SelectedRecipeSemanticsTests
{
    [Theory]
    [InlineData("cili", "ili.ttl", "i46360")]
    [InlineData("propbank", "abandon.xml", "abandon.01")]
    [InlineData("framenet", "Abandonment.xml", "Abandonment")]
    [InlineData("open-english-wordnet", "english-wordnet-sample.xml", "oewn-08242255-n")]
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
}
