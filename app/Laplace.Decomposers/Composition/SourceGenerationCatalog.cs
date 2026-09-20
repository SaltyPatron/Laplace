using Laplace.Decomposers.Structured;
using System.Text.Json;

namespace Laplace.Decomposers.Composition;

/// <summary>
/// Explicit host selection of source generations. Catalog discovery never enables
/// a draft manifest; dedicated selection and catalog selection share one resolver.
/// </summary>
public sealed class SourceGenerationCatalog
{
    private readonly Dictionary<string, SourceGenerationRecipe> _selected = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> Keys => _selected.Keys;

    public SourceGenerationCatalog()
    {
        string? explicitSelection = Environment.GetEnvironmentVariable("LAPLACE_SOURCE_GENERATIONS");
        var candidates = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(explicitSelection))
            foreach (string path in SplitPaths(explicitSelection)) candidates[Path.GetFullPath(path)] = true;
        string? cookbook = Environment.GetEnvironmentVariable("LAPLACE_COOKBOOK_PATH");
        if (!string.IsNullOrWhiteSpace(cookbook))
        {
            foreach (string entry in SplitPaths(cookbook))
            {
                IEnumerable<string> paths = Directory.Exists(entry)
                    ? Directory.EnumerateFiles(entry, "*.source.json", SearchOption.AllDirectories)
                    : entry.EndsWith(".source.json", StringComparison.OrdinalIgnoreCase) ? [entry] : [];
                foreach (string path in paths) candidates.TryAdd(Path.GetFullPath(path), false);
            }
        }
        var sourceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sourceIds = new Dictionary<Laplace.Engine.Core.Hash128, string>();
        foreach ((string path, bool explicitlySelected) in candidates.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!explicitlySelected)
            {
                using var input = File.OpenRead(path);
                using var metadata = JsonDocument.Parse(input);
                if (!metadata.RootElement.TryGetProperty("selected", out var selected)
                    || selected.ValueKind != JsonValueKind.True) continue;
            }
            SourceGenerationRecipe recipe = SourceGenerationRecipe.Load(path);
            if (sourceNames.TryGetValue(recipe.SourceName, out string? prior)
                || sourceIds.TryGetValue(recipe.SourceId, out prior))
                throw new InvalidDataException($"Conflicting source-generation selections '{prior}' and '{path}'.");
            sourceNames.Add(recipe.SourceName, path);
            sourceIds.Add(recipe.SourceId, path);
            IEnumerable<string> builtinAliases = SeedIngestComposition.Registry
                .Where(entry => entry.Decomposer.Name.Equals(recipe.SourceName, StringComparison.OrdinalIgnoreCase))
                .Select(static entry => entry.Key);
            foreach (string key in recipe.Aliases.Concat(builtinAliases).Append(recipe.SourceName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (_selected.TryGetValue(key, out var existing))
                    throw new InvalidDataException($"Source alias '{key}' selects both '{existing.ManifestPath}' and '{path}'.");
                _selected.Add(key, recipe);
            }
        }
    }

    public bool TryGet(string key, out SourceGenerationRecipe recipe) => _selected.TryGetValue(key, out recipe!);

    private static IEnumerable<string> SplitPaths(string value) => value.Split(
        Path.PathSeparator == ';' ? [';'] : [';', Path.PathSeparator],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
