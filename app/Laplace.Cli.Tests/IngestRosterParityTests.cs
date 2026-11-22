using Laplace.Cli;
using Xunit;

namespace Laplace.Cli.Tests;

public sealed class IngestRosterParityTests
{
    private static readonly HashSet<string> OperationalOnlyRoutes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "chess",
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

    private const int OperationalOnlyRouteCeiling = 25;

    [Fact]
    public void RuntimeRoutes_MatchManifestPlusExplicitOperationalRoutes()
    {
        var manifestRoutes = NativeRecipeCatalog.SourceOrder
            .ToHashSet(StringComparer.Ordinal);
        var runtimeRoutes = IngestDispatchTable.RegisteredKeys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = manifestRoutes.Except(runtimeRoutes, StringComparer.OrdinalIgnoreCase).ToList();
        string[] pathCommands = ["document", "repo", "stack", "tiny-codes", "safetensors"];
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
        var orderPath = Path.Combine(NativeRecipeCatalog.RecipesRoot, "order");
        Assert.True(File.Exists(orderPath), $"Missing {orderPath}");

        var sources = File.ReadLines(orderPath)
            .Select(static l => l.Split('#', 2)[0].Trim())
            .Where(static l => l.Length > 0)
            .Select(static l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0])
            .ToList();

        Assert.Equal(sources, NativeRecipeCatalog.ReadSourceOrder(NativeRecipeCatalog.RecipesRoot));
        Assert.Equal(NativeRecipeCatalog.SourceOrder, IngestDispatchTable.CanonicalSourceKeys);
        var directories = Directory.EnumerateDirectories(NativeRecipeCatalog.RecipesRoot)
            .Where(directory => File.Exists(Path.Combine(directory, "source")))
            .Select(Path.GetFileName).OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(directories, IngestDispatchTable.CanonicalSourceKeys.OrderBy(static name => name, StringComparer.Ordinal));

        foreach (var source in sources)
        {
            var roots = IngestDataPaths.ResolveRoots(source);
            Assert.NotEmpty(roots);
            Assert.All(roots, root => Assert.True(Path.IsPathFullyQualified(root), $"Source '{source}' has a relative root: {root}"));
        }
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
    }
}
