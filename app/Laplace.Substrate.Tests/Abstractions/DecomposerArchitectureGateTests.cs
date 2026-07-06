using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Phase 1.7 architecture gate (doc 13 T4): decomposers are pure record extractors;
/// they must not embed SQL or bypass the ingestion spine to reach Postgres directly.
/// </summary>
public sealed class DecomposerArchitectureGateTests
{
    private static readonly Regex InlineSql = new(
        @"\bSELECT\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ForbiddenWriterRefs = new(
        @"\b(Npgsql(?:DataSource|Connection|Command|SubstrateWriter|WorkingSetApply)|ConsensusAccumulatingWriter)\b",
        RegexOptions.Compiled);

    private static readonly string[] RequiredSpineMarkers =
    [
        "ContentTierSpine",
        "IngestBatchPipeline",
        "RelationTripleDecomposerBase",
        "DecomposerBatch",
        "StructuredGrammarIngest",
        "GrammarComposeIngestSupport",
        "CategoryCorrespondenceIngestSupport",
    ];

    [Fact]
    public void DecomposerProjects_ContainNoInlineSql()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var dir = Path.Combine(repoRoot, "app", "Laplace.Decomposers");
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var text = File.ReadAllText(file);
            if (InlineSql.IsMatch(text))
                violations.Add(Path.GetRelativePath(repoRoot, file));
        }
        Assert.True(violations.Count == 0,
            "Decomposers must not contain inline SQL:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void DecomposerProjects_ContainNoDirectNpgsqlWriterBypass()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var dir = Path.Combine(repoRoot, "app", "Laplace.Decomposers");
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var text = File.ReadAllText(file);
            if (ForbiddenWriterRefs.IsMatch(text))
                violations.Add(Path.GetRelativePath(repoRoot, file));
        }
        Assert.True(violations.Count == 0,
            "Decomposers must not reference Npgsql writers/apply directly (use IDecomposerContext):\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void SubstrateAbstractions_ExportsCentralContentTierSpine()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var spine = Path.Combine(repoRoot, "app", "Laplace.Substrate", "Abstractions", "ContentTierSpine.cs");
        Assert.True(File.Exists(spine), "ContentTierSpine.cs must exist as the single content path");
        var text = File.ReadAllText(spine);
        Assert.Contains("MaxExistenceRounds", text, StringComparison.Ordinal);
        Assert.Contains("BuildTree", text, StringComparison.Ordinal);
        Assert.Contains("BatchExistenceEmitBitmapsAsync", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DecomposerProjects_EachDecomposerUsesIngestionSpine()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var dir = Path.Combine(repoRoot, "app", "Laplace.Decomposers");
            var allowHandBuilder =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Unicode/UnicodeDecomposer.cs",
                "Atomic2020/Atomic2020Decomposer.cs",
                "Audio/AudioDecomposer.cs",
                "Image/ImageDecomposer.cs",
            };
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*Decomposer.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (allowHandBuilder.Contains(rel)) continue;

            var folder = Path.GetDirectoryName(file)!;
            bool hasSpine = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
                .Any(f => RequiredSpineMarkers.Any(m =>
                    File.ReadAllText(f).Contains(m, StringComparison.Ordinal)));
            if (!hasSpine)
                violations.Add(rel);
        }
        Assert.True(violations.Count == 0,
            "Each decomposer package must route through the ingestion spine (or be on the documented allowlist):\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void IngestDescentFlush_AlwaysRunsTierExistence()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var flush = Path.Combine(repoRoot, "app", "Laplace.Substrate", "Abstractions", "IngestDescentFlush.cs");
        var text = File.ReadAllText(flush);
        Assert.Contains("ContentTierSpine.BatchExistenceEmitBitmapsAsync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("bool probe = !config.WorkingSet", text, StringComparison.Ordinal);
    }
}
