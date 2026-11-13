using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

public sealed class SourceGenerationRegressionTests
{
    internal static string RepositoryFile(string relative)
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out string root));
        return Path.Combine(root, relative);
    }
    [Theory]
    [InlineData("iso639", "iso-639")]
    [InlineData("iso-639", "iso-639")]
    [InlineData("oewn", "open-english-wordnet")]
    [InlineData("open-english-wordnet", "open-english-wordnet")]
    [InlineData("omw", "open-multilingual-wordnet")]
    [InlineData("open-multilingual-wordnet", "open-multilingual-wordnet")]
    [InlineData("ud", "universal-dependencies")]
    [InlineData("universal-dependencies", "universal-dependencies")]
    public void PreviousSourceAliasesResolveThroughTheCanonicalNativeEstate(string alias, string expected)
        => Assert.Equal(expected, EtlManifest.Catalog.Resolve(alias));

    [Fact]
    public void HostCompositionListsCanonicalNativeSourcesWithoutLoadingJsonGenerations()
    {
        var resolver = new Composition.SeedDecomposerResolver(new NullServiceProvider(), []);
        Assert.Equal(EtlManifest.SourceOrder, resolver.SelectedGenerationKeys);
        Assert.Contains("open-multilingual-wordnet", resolver.SelectedGenerationKeys);
        Assert.Contains("universal-dependencies", resolver.SelectedGenerationKeys);
    }

    [Fact]
    public void CanonicalEstateContainsExecutableRecipesAndNoJsonRecipeLanguage()
    {
        string estate = EtlManifest.RecipesRoot;
        Assert.NotEmpty(Directory.EnumerateFiles(estate, "*.recipe", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.EnumerateFiles(estate, "*.format", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(estate, "*.recipe.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(estate, "*.source.json", SearchOption.AllDirectories));
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
