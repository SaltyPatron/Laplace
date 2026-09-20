using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.Structured;

/// <summary>Installed, versioned source-generation recipe selected by an artifact graph.</summary>
public sealed class InstalledSourceGeneration
{
    private InstalledSourceGeneration(SemanticSourceRecipe recipe, string artifactPath) =>
        (Recipe, ArtifactPath) = (recipe, artifactPath);

    public SemanticSourceRecipe Recipe { get; }
    public string ArtifactPath { get; }
    public string CanonicalProperty(string alias) => Recipe.CanonicalProperty(alias);
    public string CanonicalValue(string property, string value) =>
        Recipe.CanonicalValue(Recipe.CanonicalProperty(property), value);

    public static InstalledSourceGeneration Load(
        string authority, string? release, string syntax)
    {
        string? configured = Environment.GetEnvironmentVariable("LAPLACE_COOKBOOK_PATH");
        string root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "recipes")
            : Path.GetFullPath(configured);
        IEnumerable<string> paths = File.Exists(root) ? [root]
            : Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.recipe.json", SearchOption.AllDirectories)
            : throw new DirectoryNotFoundException($"Cookbook installation '{root}' does not exist.");
        var matches = new Dictionary<Laplace.Engine.Core.Hash128, InstalledSourceGeneration>();
        foreach (string path in paths.Order(StringComparer.Ordinal))
        {
            SemanticSourceRecipe recipe = new LaplaceCookbook().InstallJson(path);
            if (recipe.Authority != authority || recipe.Syntax != syntax
                || (release is not null && recipe.Release != release)) continue;
            matches.TryAdd(recipe.RecipeId, new InstalledSourceGeneration(recipe, Path.GetFullPath(path)));
        }
        return matches.Count switch
        {
            1 => matches.Values.Single(),
            0 => throw new FileNotFoundException(
                $"No installed recipe matches authority='{authority}', release='{release ?? "selected"}', syntax='{syntax}' in '{root}'."),
            _ => throw new InvalidOperationException(
                $"Several installed recipes match authority='{authority}', release='{release ?? "selected"}', syntax='{syntax}'. Select the exact recipe with LAPLACE_COOKBOOK_PATH."),
        };
    }

}
