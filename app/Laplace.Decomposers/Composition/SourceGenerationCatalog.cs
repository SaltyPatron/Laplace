using Laplace.Decomposers.Structured;
using System.Text.Json;

namespace Laplace.Decomposers.Composition;

/// <summary>
/// One source-generation selection authority. Checked-in recipes, an optional host
/// cookbook, and explicit manifest paths all feed the same resolver. Discovery never
/// enables a draft manifest: only an explicit path or `"selected": true` may replace
/// a legacy source implementation.
/// </summary>
public sealed class SourceGenerationCatalog
{
    private readonly Dictionary<string, SourceGenerationRecipe> _selected = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> Keys => _selected.Keys;

    public SourceGenerationCatalog()
    {
        string? explicitSelection = Environment.GetEnvironmentVariable("LAPLACE_SOURCE_GENERATIONS");
        var candidates = new Dictionary<string, bool>(StringComparer.Ordinal);

        // Repository/deployment cookbook is part of the product, not an environment
        // variable. This makes checked-in recipes executable by default while the
        // selected bit remains the deliberate cut-over switch for each source.
        foreach (string path in BuiltInRecipeManifests())
            candidates.TryAdd(path, false);

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
            {
                if (SameManifest(prior, path)) continue;
                throw new InvalidDataException($"Conflicting source-generation selections '{prior}' and '{path}'.");
            }
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

    private static bool SameManifest(string left, string right)
    {
        var a = File.ReadAllBytes(left);
        var b = File.ReadAllBytes(right);
        return a.AsSpan().SequenceEqual(b);
    }

    private static IEnumerable<string> BuiltInRecipeManifests()
    {
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var cursor = new DirectoryInfo(Path.GetFullPath(start));
            while (cursor is not null)
            {
                string cookbook = Path.Combine(cursor.FullName, "recipes");
                if (Directory.Exists(cookbook))
                {
                    foreach (string path in Directory.EnumerateFiles(
                                 cookbook, "*.source.json", SearchOption.AllDirectories)
                                 .OrderBy(static path => path, StringComparer.Ordinal))
                    {
                        string full = Path.GetFullPath(path);
                        if (emitted.Add(full))
                            yield return full;
                    }
                    break;
                }
                cursor = cursor.Parent;
            }
        }
    }

    private static IEnumerable<string> SplitPaths(string value) => value.Split(
        Path.PathSeparator == ';' ? [';'] : [';', Path.PathSeparator],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
