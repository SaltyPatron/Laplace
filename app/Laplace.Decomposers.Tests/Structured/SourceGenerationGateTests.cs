using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Composition;
using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

public sealed class SourceGenerationGateTests
{
    [Fact]
    public void CanonicalCorporaHaveNoManagedFallbackProvider()
    {
        var sources = EtlManifest.SourceOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] aliases = ["iso639", "oewn", "omw", "wordnet", "ud", "atomic2020"];
        Assert.DoesNotContain(SeedIngestComposition.RegisteredSourceKeys, sources.Contains);
        Assert.Empty(SeedIngestComposition.RegisteredSourceKeys.Intersect(aliases, StringComparer.OrdinalIgnoreCase));
        var resolver = new SeedDecomposerResolver(new NullServiceProvider(), []);
        foreach (string key in sources.Concat(aliases))
            Assert.Throws<ArgumentException>(() => resolver.Resolve(key));
    }

    [Fact]
    public void NativeSourcesAreReadFromTheDeclaredRecipeEstate()
    {
        var resolver = new SeedDecomposerResolver(new NullServiceProvider(), []);
        Assert.Equal(EtlManifest.SourceOrder, resolver.SelectedGenerationKeys);
        Assert.NotEmpty(resolver.SelectedGenerationKeys);
        foreach (string source in resolver.SelectedGenerationKeys)
            Assert.True(File.Exists(Path.Combine(EtlManifest.RecipesRoot, source, "source")));
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
