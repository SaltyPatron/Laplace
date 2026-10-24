using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

public sealed class RecipeLawGateTests
{
    [Fact]
    public void CanonicalRecipesDoNotMintTheRetiredSenseKeyEntityType()
    {
        string[] recipes = Directory.GetFiles(EtlManifest.RecipesRoot, "*.recipe", SearchOption.AllDirectories);
        Assert.NotEmpty(recipes);
        foreach (string path in recipes)
            Assert.DoesNotContain("WordNet_Sense_Key", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void SenseKeyEntityTypeIsNotGoverned()
    {
        string types = File.ReadAllText(SourceGenerationRegressionTests.RepositoryFile("engine/manifest/entity_types.toml"));
        Assert.DoesNotContain("\"WordNet_Sense_Key\"", types, StringComparison.Ordinal);
    }

    [Fact]
    public void ThereIsNoSecondJsonRecipeLanguageOrRcpRuntime()
    {
        string root = SourceGenerationRegressionTests.RepositoryFile(".");
        string retiredRecipes = Path.Combine(root, "recipes");
        if (Directory.Exists(retiredRecipes))
            Assert.Empty(Directory.GetFiles(retiredRecipes, "*.json", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(root, "engine/core/src/recipe_stream.cpp")));
        Assert.False(File.Exists(Path.Combine(root, "app/Laplace.Core/Core/NativeRecipeStream.cs")));
        string build = File.ReadAllText(Path.Combine(root, "engine/core/CMakeLists.txt"));
        Assert.DoesNotContain("src/recipe_stream.cpp", build, StringComparison.Ordinal);
        Assert.DoesNotContain("include/laplace/core/recipe_stream.h", build, StringComparison.Ordinal);
    }
}
