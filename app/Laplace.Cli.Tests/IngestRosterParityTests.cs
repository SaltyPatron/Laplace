using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Laplace.Cli;
using Xunit;

namespace Laplace.Cli.Tests;

public sealed class IngestRosterParityTests
{
    /// <summary>
    /// Routes that dispatch but are absent from the seed-cadence manifest; every such
    /// route must be listed here explicitly.
    /// </summary>
    private static readonly HashSet<string> OperationalOnlyRoutes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "agents",
            "chess",
            "chess-analyze",
            "chess-books",
            "chess-eval",
            "chess-move-outcomes",
            "chess-opening-match",
            "chess-player-context-outcomes",
            "chess-position-outcomes",
            "chess-syzygy",
            "chess-tactic-outcomes",
            "chess-transitions",
            "chess-trajectory",
            "code",
            "frame-video",
            "model",
            "model-corroborate",
            "omw-probe",
            "openings",
            "parquet",
            "recipe",
            "rgba-image",
            "safetensor",
            "tabular",
            "track-audio",
        };

    // Upper bound on OperationalOnlyRoutes, so adding a route outside the manifest is an
    // explicit edit here. These routes read admitted structure or an operator-supplied path
    // (derivation lanes over recorded games, media format lanes, agents, code/tabular/parquet,
    // model-corroborate) rather than a corpus the seed cadence orders.
    private const int OperationalOnlyRouteCeiling = 25;

    [Fact]
    public void RuntimeRoutes_MatchManifestPlusExplicitOperationalRoutes()
    {
        // Selected source generations are routes too; they come from the CLI's services.
        CliRuntime.InitializeServices();
        var manifestRoutes = ReadManifestRoutes();
        var runtimeRoutes = IngestDispatchTable.RegisteredKeys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = manifestRoutes.Except(runtimeRoutes, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // A source generation answers to its source name and each alias: one route.
        var generations = new Laplace.Decomposers.Composition.SourceGenerationCatalog();
        bool SameGenerationAsLadderRoute(string key) =>
            generations.TryGet(key, out var recipe)
            && recipe.Aliases.Append(recipe.SourceName).Any(manifestRoutes.Contains);
        var unclassified = runtimeRoutes
            .Except(manifestRoutes, StringComparer.OrdinalIgnoreCase)
            .Except(OperationalOnlyRoutes, StringComparer.OrdinalIgnoreCase)
            .Where(key => !SameGenerationAsLadderRoute(key))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var stale = OperationalOnlyRoutes.Except(runtimeRoutes, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(missing.Count == 0,
            "Manifest sources missing from C# dispatch:\n  " + string.Join("\n  ", missing));
        Assert.True(unclassified.Count == 0,
            "Runtime routes absent from the manifest and operational allowlist:\n  "
            + string.Join("\n  ", unclassified));
        Assert.True(stale.Count == 0,
            "Operational-only routes no longer dispatch; remove stale entries:\n  "
            + string.Join("\n  ", stale));
        Assert.True(OperationalOnlyRoutes.Count <= OperationalOnlyRouteCeiling,
            $"{nameof(OperationalOnlyRoutes)} has {OperationalOnlyRoutes.Count} entries; "
            + $"shrink-only ceiling is {OperationalOnlyRouteCeiling}.");
    }

    [Fact]
    public void ShellWrapper_ExposesChessTacticOutcomeRoute()
    {
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "scripts", "ingest-source.sh"));
        // The shell groups source keys and forwards the selected key through the
        // shared ingest function; a separate chess-specific invocation is not required.
        var branches = Regex.Matches(script,
            @"(?m)^\s*(?<routes>[a-z0-9|-]+)\)\s*\n(?<body>[\s\S]*?)^\s*;;");
        var branch = Assert.Single(branches.Cast<Match>().Where(match =>
            match.Groups["routes"].Value.Split('|').Contains("chess-tactic-outcomes", StringComparer.Ordinal)));
        var body = branch.Groups["body"].Value;
        Assert.Contains("require_cli", body, StringComparison.Ordinal);
        Assert.Contains("ingest \"$source\"", body, StringComparison.Ordinal);
        Assert.True(body.IndexOf("require_cli", StringComparison.Ordinal)
            < body.IndexOf("ingest \"$source\"", StringComparison.Ordinal),
            "the generic route must require the prepared runtime before invoking ingest");
        Assert.Contains("local -a ingest_args=(\"$@\")", script, StringComparison.Ordinal);
        Assert.Contains("dotnet \"$DLL\" ingest \"${ingest_args[@]}\"", script, StringComparison.Ordinal);
    }

    private static HashSet<string> ReadManifestRoutes()
    {
        var path = Path.Combine(
            FindRepoRoot(), "scripts", "win", "witness-manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in document.RootElement.GetProperty("cadence").EnumerateArray())
        {
            foreach (var source in stage.GetProperty("sources").EnumerateArray())
            {
                routes.Add(source.GetProperty("cli").GetString()
                    ?? throw new InvalidDataException("manifest source has null cli"));
            }
        }
        return routes;
    }

    private static string FindRepoRoot()
    {
        var stamped = typeof(IngestRosterParityTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LaplaceRepoRoot")?.Value;
        if (stamped is not null && File.Exists(
                Path.Combine(stamped, "scripts", "win", "witness-manifest.json")))
            return stamped;
        throw new InvalidOperationException("Repository root metadata is missing");
    }
}
