using System.Reflection;
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
        var manifestRoutes = Laplace.Decomposers.Abstractions.EtlManifest.SourceOrder
            .ToHashSet(StringComparer.Ordinal);
        var runtimeRoutes = IngestDispatchTable.RegisteredKeys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = manifestRoutes.Except(runtimeRoutes, StringComparer.OrdinalIgnoreCase).ToList();
        // These existing path/operator commands were previously mixed into the
        // cadence JSON. They do not declare corpus order or another recipe estate.
        string[] pathCommands = ["document", "repo", "stack", "tiny-codes", "operational", "safetensors"];
        var unclassified = runtimeRoutes
            .Except(manifestRoutes, StringComparer.OrdinalIgnoreCase)
            .Except(OperationalOnlyRoutes, StringComparer.OrdinalIgnoreCase)
            .Except(pathCommands, StringComparer.OrdinalIgnoreCase)
            .Where(key => !manifestRoutes.Contains(IngestDispatchTable.CanonicalSourceName(key)))
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
    public void CanonicalRecipeSourcesRetainTheirDeclaredRoots()
    {
        CliRuntime.InitializeServices();
        var orderPath = Path.Combine(Laplace.Decomposers.Abstractions.EtlManifest.RecipesRoot, "order");
        Assert.True(File.Exists(orderPath), $"Missing {orderPath}");

        var sources = File.ReadLines(orderPath)
            .Select(static l => l.Split('#', 2)[0].Trim())
            .Where(static l => l.Length > 0)
            .Select(static l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0])
            .ToList();

        Assert.Equal(sources, Laplace.Decomposers.Abstractions.EtlManifest.SourceOrder);
        Assert.Equal(sources, IngestDispatchTable.CanonicalSourceKeys);
        var directories = Directory.EnumerateDirectories(Laplace.Decomposers.Abstractions.EtlManifest.RecipesRoot)
            .Where(directory => File.Exists(Path.Combine(directory, "source")))
            .Select(Path.GetFileName).OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(directories, IngestDispatchTable.CanonicalSourceKeys.OrderBy(static name => name, StringComparer.Ordinal));

        foreach (var source in sources)
        {
            var roots = IngestDataPaths.ResolveRoots(source);
            Assert.NotEmpty(roots);
            Assert.All(roots, root => Assert.True(Path.IsPathFullyQualified(root), $"Source '{source}' has a relative root: {root}"));
        }
        // A same-format route is not evidence that another corpus is implemented.
        // Native recipe dispatch acceptance is checked separately from this path contract.
    }

    [Fact]
    public void ShellWrapper_ExposesChessTacticOutcomeRoute()
    {
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "scripts", "ingest-source.sh"));
        // The shell groups source keys and forwards the selected key through the
        // shared ingest function; a separate chess-specific invocation is not required.
        var branches = Regex.Matches(script,
            @"(?m)^\s*(?<routes>[a-z0-9|-]+)\)\s*\n(?<body>[\s\S]*?)^\s*;;");
        var branch = Assert.Single(branches.Cast<Match>(), match =>
            match.Groups["routes"].Value.Split('|').Contains("chess-tactic-outcomes", StringComparer.Ordinal));
        var body = branch.Groups["body"].Value;
        Assert.Contains("require_cli", body, StringComparison.Ordinal);
        Assert.Contains("ingest \"$source\"", body, StringComparison.Ordinal);
        Assert.True(body.IndexOf("require_cli", StringComparison.Ordinal)
            < body.IndexOf("ingest \"$source\"", StringComparison.Ordinal),
            "the generic route must require the prepared runtime before invoking ingest");
        Assert.Contains("local -a ingest_args=(\"$@\")", script, StringComparison.Ordinal);
        Assert.Contains("dotnet \"$DLL\" ingest \"${ingest_args[@]}\"", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ucd", "unicode")]
    [InlineData("uca", "unicode")]
    [InlineData("iso639", "iso-639")]
    [InlineData("OEWN", "open-english-wordnet")]
    [InlineData("omw", "open-multilingual-wordnet")]
    [InlineData("wordnet", "princeton-wordnet")]
    [InlineData("ud", "universal-dependencies")]
    [InlineData("atomic2020", "atomic-2020")]
    [InlineData("atomic", "atomic-2020")]
    [InlineData("sbf", "social-bias-frames")]
    [InlineData("socialchem", "social-chemistry-101")]
    [InlineData("mhs", "measuring-hate-speech")]
    [InlineData("wiktextract", "wiktionary")]
    [InlineData("predicatematrix", "predicate-matrix")]
    public void ExistingSourceAliasesSelectCanonicalNativeRecipes(string alias, string expected)
    {
        Assert.Equal(expected, IngestDispatchTable.CanonicalSourceName(alias));
        Assert.Contains(expected, Laplace.Decomposers.Abstractions.EtlManifest.SourceOrder);
        Assert.DoesNotContain(alias, IngestDispatchTable.RegisteredKeys, StringComparer.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var stamped = typeof(IngestRosterParityTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LaplaceRepoRoot")?.Value;
        if (stamped is not null && File.Exists(
                Path.Combine(stamped, "scripts", "ingest-source.sh")))
            return stamped;
        throw new InvalidOperationException("Repository root metadata is missing");
    }
}
