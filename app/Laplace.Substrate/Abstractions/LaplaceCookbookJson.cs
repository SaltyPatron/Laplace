using System.Text.Json;
using System.Text.Json.Serialization;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Portable Cookbook artifact. Installing this document adds a source generation;
/// it does not require a source-named class, rebuild, or application release.
/// </summary>
public sealed record LaplaceRecipeDocument(
    string Authority,
    string Release,
    string Provider,
    string Syntax,
    IReadOnlyList<SourceRecipeField> Fields,
    IReadOnlyList<SourceRecipeStructure>? Structures = null,
    IReadOnlyDictionary<string, string>? ValueAliases = null,
    IReadOnlyList<SourceRecipeProviderRoute>? ProviderRoutes = null,
    IReadOnlyList<SourceRecipeArtifact>? Artifacts = null);

public static class LaplaceCookbookJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static SemanticSourceRecipe InstallJson(
        this LaplaceCookbook cookbook,
        Stream artifact)
    {
        ArgumentNullException.ThrowIfNull(cookbook);
        ArgumentNullException.ThrowIfNull(artifact);
        LaplaceRecipeDocument document = JsonSerializer.Deserialize<LaplaceRecipeDocument>(
            artifact, Options) ?? throw new InvalidDataException("Laplace Recipe artifact is empty.");
        var recipe = new SemanticSourceRecipe(
            document.Authority,
            document.Release,
            document.Provider,
            document.Syntax,
            document.Fields,
            document.Structures,
            document.ValueAliases,
            document.ProviderRoutes,
            document.Artifacts);
        cookbook.Register(recipe);
        return recipe;
    }

    public static SemanticSourceRecipe InstallJson(
        this LaplaceCookbook cookbook,
        string artifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        using FileStream stream = File.OpenRead(artifactPath);
        return cookbook.InstallJson(stream);
    }

    public static void WriteJson(SemanticSourceRecipe recipe, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(destination);
        var document = new LaplaceRecipeDocument(
            recipe.Authority,
            recipe.Release,
            recipe.Provider,
            recipe.Syntax,
            recipe.Fields,
            recipe.Structures,
            recipe.ValueAliases,
            recipe.ProviderRoutes,
            recipe.Artifacts);
        JsonSerializer.Serialize(destination, document, Options);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
