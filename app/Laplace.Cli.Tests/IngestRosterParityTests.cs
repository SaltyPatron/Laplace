using System.Reflection;
using System.Text.Json;
using Laplace.Cli;
using Xunit;

namespace Laplace.Cli.Tests;

public sealed class IngestRosterParityTests
{
    /// <summary>
    /// Runtime-only maintenance and alias routes that are deliberately absent from
    /// the seed-cadence manifest. This allowlist may only shrink as the roster is
    /// generated; a new route must be classified explicitly.
    /// </summary>
    private static readonly HashSet<string> OperationalOnlyRoutes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "chess",
            "chess-analyze",
            "chess-books",
            "chess-eval",
            "chess-syzygy",
            "chess-trajectory",
            "code",
            "model",
            "omw-probe",
            "openings",
            "parquet",
            "recipe",
            "safetensor",
            "tabular",
        };

    private const int OperationalOnlyRouteCeiling = 14;

    [Fact]
    public void RuntimeRoutes_MatchManifestPlusExplicitOperationalRoutes()
    {
        var manifestRoutes = ReadManifestRoutes();
        var runtimeRoutes = IngestDispatchTable.RegisteredKeys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = manifestRoutes.Except(runtimeRoutes, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var unclassified = runtimeRoutes
            .Except(manifestRoutes, StringComparer.OrdinalIgnoreCase)
            .Except(OperationalOnlyRoutes, StringComparer.OrdinalIgnoreCase)
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
