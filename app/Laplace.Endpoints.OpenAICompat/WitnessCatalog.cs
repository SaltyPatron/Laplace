using System.Text.Json;
using System.Text.Json.Serialization;
using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed class WitnessCatalog
{
    private static readonly string[] FeaturedRefs = ["dog", "i46531", "Self_motion", "chess"];

    private readonly WitnessManifestRoot _root;

    private WitnessCatalog(WitnessManifestRoot root) => _root = root;

    public static WitnessCatalog Load()
    {
        foreach (var path in CandidatePaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                var root = JsonSerializer.Deserialize(json, WitnessManifestJsonContext.Default.WitnessManifestRoot);
                if (root?.Cadence is { Count: > 0 })
                    return new WitnessCatalog(root);
            }
            catch
            {
                // fall through
            }
        }

        var asm = typeof(WitnessCatalog).Assembly;
        var resource = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("witness-manifest.json", StringComparison.OrdinalIgnoreCase));
        if (resource is not null)
        {
            using var stream = asm.GetManifestResourceStream(resource)!;
            var root = JsonSerializer.Deserialize(stream, WitnessManifestJsonContext.Default.WitnessManifestRoot);
            if (root?.Cadence is { Count: > 0 })
                return new WitnessCatalog(root);
        }

        return new WitnessCatalog(new WitnessManifestRoot { Cadence = [] });
    }

    public IReadOnlyList<string> FeaturedRefsList() => FeaturedRefs;

    public IReadOnlyList<ExploreStageRow> BuildStages(IReadOnlyDictionary<string, ExploreSourceRow> liveByKey)
    {
        var output = new List<ExploreStageRow>(_root.Cadence.Count);
        foreach (var stage in _root.Cadence.OrderBy(s => s.Order))
        {
            var sources = new List<ExploreStageSourceRow>();
            foreach (var src in stage.Sources)
            {
                var cli = src.Cli ?? "";
                liveByKey.TryGetValue(cli, out var live);
                sources.Add(new ExploreStageSourceRow(
                    Cli: cli,
                    Layer: src.Layer ?? live?.Layer,
                    Role: src.Role ?? src.Links ?? live?.Role,
                    Links: src.Links));
            }

            output.Add(new ExploreStageRow(
                Stage: stage.Stage ?? "",
                Order: stage.Order,
                Law: stage.Law,
                Sources: sources));
        }

        return output;
    }

    public static string? StageForSource(WitnessManifestRoot root, string cli)
    {
        foreach (var stage in root.Cadence)
        {
            foreach (var src in stage.Sources)
            {
                if (string.Equals(src.Cli, cli, StringComparison.OrdinalIgnoreCase))
                    return stage.Stage;
            }
        }

        return null;
    }

    internal WitnessManifestRoot Root => _root;

    private static IEnumerable<string> CandidatePaths()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_ROOT");
        if (!string.IsNullOrWhiteSpace(env))
            yield return Path.Combine(env, "scripts", "win", "witness-manifest.json");

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            yield return Path.Combine(dir.FullName, "scripts", "win", "witness-manifest.json");
    }
}

internal sealed class WitnessManifestRoot
{
    [JsonPropertyName("cadence")]
    public List<WitnessManifestStage> Cadence { get; set; } = [];
}

internal sealed class WitnessManifestStage
{
    [JsonPropertyName("stage")]
    public string? Stage { get; set; }

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("law")]
    public string? Law { get; set; }

    [JsonPropertyName("sources")]
    public List<WitnessManifestSource> Sources { get; set; } = [];
}

internal sealed class WitnessManifestSource
{
    [JsonPropertyName("cli")]
    public string? Cli { get; set; }

    [JsonPropertyName("layer")]
    public string? Layer { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("links")]
    public string? Links { get; set; }
}

[JsonSerializable(typeof(WitnessManifestRoot))]
internal partial class WitnessManifestJsonContext : JsonSerializerContext;
