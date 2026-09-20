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

    public static InstalledSourceGeneration Load(string relativePath)
    {
        string? configured = Environment.GetEnvironmentVariable("LAPLACE_COOKBOOK_PATH");
        string[] candidates = string.IsNullOrWhiteSpace(configured)
            ? [Path.Combine(AppContext.BaseDirectory, relativePath),
               Path.Combine(Environment.CurrentDirectory, relativePath)]
            : [File.Exists(configured) ? configured : Path.Combine(configured, relativePath)];
        string artifact = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                $"Cookbook source generation '{relativePath}' is not installed.");
        SemanticSourceRecipe recipe = new LaplaceCookbook().InstallJson(artifact);
        return new InstalledSourceGeneration(recipe, Path.GetFullPath(artifact));
    }

}
