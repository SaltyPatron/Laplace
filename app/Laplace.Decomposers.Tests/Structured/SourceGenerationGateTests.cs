using Laplace.Decomposers.Composition;
using Laplace.Decomposers.Structured;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

/// <summary>
/// A source that has a checked-in source generation is read only by its recipe. These gates
/// fail if a legacy C# decomposer is registered (or routed by the CLI) under a key a recipe
/// owns, if resolution of a recipe-owned key yields anything but the recipe decomposer, or if a
/// missing generation root is accepted instead of failing the ingest.
/// </summary>
public sealed class SourceGenerationGateTests
{
    private static IEnumerable<SourceGenerationRecipe> SelectedCheckedInGenerations()
    {
        CodepointPerfcache.LoadDefault();
        string recipes = SourceGenerationRegressionTests.RepositoryFile("recipes");
        foreach (string path in Directory.EnumerateFiles(recipes, "*.source.json", SearchOption.AllDirectories)
                     .OrderBy(static p => p, StringComparer.Ordinal))
        {
            var recipe = SourceGenerationRecipe.Load(path);
            if (recipe.Selected) yield return recipe;
        }
    }

    private static IEnumerable<string> Keys(SourceGenerationRecipe recipe) =>
        recipe.Aliases.Append(recipe.SourceName).Distinct(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Recipe_owned_sources_have_no_built_in_provider()
    {
        var registered = SeedIngestComposition.RegisteredSourceKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shadowed = SelectedCheckedInGenerations()
            .SelectMany(r => Keys(r).Where(registered.Contains).Select(k => $"{k} ({r.ManifestPath})"))
            .ToList();
        Assert.True(shadowed.Count == 0,
            "A legacy C# decomposer is registered under a recipe-owned key:\n  " + string.Join("\n  ", shadowed));
    }

    [Fact]
    public void Cli_routes_no_bespoke_handler_for_a_recipe_owned_source()
    {
        string table = File.ReadAllText(SourceGenerationRegressionTests.RepositoryFile(
            "app/Laplace.Cli/IngestDispatchTable.cs"));
        var routed = SelectedCheckedInGenerations().SelectMany(Keys)
            .Where(k => table.Contains($"(\"{k}\",", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(routed.Count == 0,
            "IngestDispatchTable routes a recipe-owned source to a bespoke handler:\n  " + string.Join("\n  ", routed));
        Assert.Contains("SeedDecomposerResolver.RequireGenerationRoot(sourceKey, sourceRoot)", table, StringComparison.Ordinal);
    }

    [Fact]
    public void Recipe_owned_keys_resolve_only_to_the_recipe_decomposer()
    {
        var resolver = new SeedDecomposerResolver(new NullServiceProvider(), []);
        foreach (var recipe in SelectedCheckedInGenerations())
            foreach (string key in Keys(recipe))
            {
                var decomposer = resolver.Resolve(key);
                Assert.IsType<Decomposer<SourceGenerationRecipe>>(decomposer);
            }
    }

    [Fact]
    public void A_missing_generation_root_fails_instead_of_falling_back()
    {
        string missing = Path.Combine(Path.GetTempPath(), "laplace-missing-root-" + Guid.NewGuid().ToString("N"));
        var ex = Assert.Throws<DirectoryNotFoundException>(() =>
            SeedDecomposerResolver.RequireGenerationRoot("unicode", missing));
        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
        string present = Path.GetTempPath();
        SeedDecomposerResolver.RequireGenerationRoot("unicode", present);
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public void Ingest_paths_declare_one_location_and_probe_no_fallback()
    {
        string paths = File.ReadAllText(SourceGenerationRegressionTests.RepositoryFile("app/Laplace.Cli/IngestDataPaths.cs"));
        Assert.DoesNotContain("Directory.Exists", paths, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Exists", paths, StringComparison.Ordinal);
    }
}
