using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Unified ingest pipeline architecture gate: decomposers subclass
/// <see cref="Decomposer{TRecord}"/> (or documented allowlist); no inline SQL,
/// no direct pipeline bypass, no hand SubstrateChangeBuilder in DecomposeAsync.
/// </summary>
public sealed class DecomposerArchitectureGateTests
{
    private static readonly Regex InlineSql = new(
        @"\bSELECT\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ForbiddenWriterRefs = new(
        @"\b(Npgsql(?:DataSource|Connection|Command|SubstrateWriter|WorkingSetApply)|ConsensusAccumulatingWriter)\b",
        RegexOptions.Compiled);

    private static readonly Regex DirectPipelineCall = new(
        @"\bIngestBatchPipeline\.(?:RunAsync|RunMultiFileAsync)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex HandBuilderInDecompose = new(
        @"new\s+SubstrateChangeBuilder\s*\(",
        RegexOptions.Compiled);

    private static readonly HashSet<string> UnicodeAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Unicode/UnicodeDecomposer.cs",
    };

    private static readonly HashSet<string> HandBuilderAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Unicode/UnicodeDecomposer.cs",
    };

    /// <summary>
    /// DecomposerOrchestrator was removed in Wave 3 — multi-phase sources use
    /// <see cref="DecomposerMultiPhase"/> with nested ComposeDecomposerPhase types.
    /// </summary>
    private static readonly HashSet<string> MultiPhaseAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Laplace.Decomposers/CILI/CILIDecomposer.cs",
        "Laplace.Decomposers/FrameNet/FrameNetDecomposer.cs",
        "Laplace.Decomposers/ISO/ISODecomposer.cs",
        "Laplace.Decomposers/Model/ModelDecomposer.cs",
        "Laplace.Decomposers/SemLink/SemLinkDecomposer.cs",
        "Laplace.Decomposers/WordNet/WordNetDecomposer.cs",
    };

    /// <summary>Direct IngestBatchPipeline in *Decomposer.cs until orchestrator migrates.</summary>
    private static readonly HashSet<string> PipelineInDecomposerAllowlist = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Direct IngestBatchPipeline in *Ingest*.cs adapter modules pending spine migration.</summary>
    private static readonly HashSet<string> PipelineInIngestAdapterAllowlist = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hand-rolled parallel file workers pending spine migration.</summary>
    private static readonly HashSet<string> ParallelIngestAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Laplace.Chess/Service/ChessLabService.cs",
    };

    private static readonly Regex ResolveFileWorkersCall = new(
        @"\bIngestParallelism\.ResolveFileWorkers\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex BoundedChannelCreate = new(
        @"\bChannel\.CreateBounded\s*<",
        RegexOptions.Compiled);

    private static IEnumerable<string> DecomposerProjectRoots(string repoRoot)
    {
        yield return Path.Combine(repoRoot, "app", "Laplace.Decomposers");
        yield return Path.Combine(repoRoot, "app", "Laplace.Chess");
    }

    [Fact]
    public void DecomposerProjects_ContainNoInlineSql()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var violations = new List<string>();
        foreach (var dir in DecomposerProjectRoots(repoRoot))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*Decomposer.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                var text = File.ReadAllText(file);
                if (InlineSql.IsMatch(text))
                    violations.Add(Path.GetRelativePath(repoRoot, file));
            }
        }
        Assert.True(violations.Count == 0,
            "Decomposers must not contain inline SQL:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void DecomposerProjects_ContainNoDirectNpgsqlWriterBypass()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var violations = new List<string>();
        foreach (var dir in DecomposerProjectRoots(repoRoot))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*Decomposer.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                var text = File.ReadAllText(file);
                if (ForbiddenWriterRefs.IsMatch(text))
                    violations.Add(Path.GetRelativePath(repoRoot, file));
            }
        }
        Assert.True(violations.Count == 0,
            "Decomposers must not reference Npgsql writers/apply directly (use IDecomposerContext):\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void SubstrateAbstractions_ExportsDecomposerBaseAndContentTierSpine()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var decomposer = Path.Combine(repoRoot, "app", "Laplace.Substrate", "Abstractions", "Decomposer.cs");
        Assert.True(File.Exists(decomposer), "Decomposer.cs must exist as the unified ingest base");
        var decomposerText = File.ReadAllText(decomposer);
        Assert.Contains("Decomposer<TRecord>", decomposerText, StringComparison.Ordinal);
        Assert.Contains("IngestPipelineDefaults", decomposerText, StringComparison.Ordinal);

        var spine = Path.Combine(repoRoot, "app", "Laplace.Substrate", "Abstractions", "ContentTierSpine.cs");
        Assert.True(File.Exists(spine), "ContentTierSpine.cs must exist as the single content path");
        var spineText = File.ReadAllText(spine);
        Assert.Contains("MaxExistenceRounds", spineText, StringComparison.Ordinal);
        Assert.Contains("BuildTree", spineText, StringComparison.Ordinal);
        Assert.Contains("BatchExistenceEmitBitmapsAsync", spineText, StringComparison.Ordinal);
    }

    [Fact]
    public void DecomposerProjects_EachDecomposerInheritsDecomposerBase()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var violations = new List<string>();
        foreach (var dir in DecomposerProjectRoots(repoRoot))
        {
            if (!Directory.Exists(dir)) continue;
            var projectRel = Path.GetRelativePath(Path.Combine(repoRoot, "app"), dir).Replace('\\', '/');
            foreach (var file in Directory.EnumerateFiles(dir, "*Decomposer.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
                var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                if (UnicodeAllowlist.Contains(rel)) continue;

                var text = File.ReadAllText(file);
                bool inheritsBase =
                    Regex.IsMatch(text, @":\s*(?:\w+\s*,\s*)*(?:RelationTripleDecomposerBase|RelationTripleDecomposer|ComposeDecomposer<|GrammarComposeDecomposer|GrammarIngestDecomposer|CategoryCorrespondenceDecomposer|DecomposerMultiFile<|DecomposerPhase<|DecomposerMultiPhase|Decomposer<)")
                    || text.Contains(": Decomposer<", StringComparison.Ordinal);
                if (!inheritsBase)
                    violations.Add($"{projectRel}/{rel}");
            }
        }
        Assert.True(violations.Count == 0,
            "Each decomposer must inherit Decomposer<T> (or documented allowlist):\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void DecomposerProjects_NoDirectPipelineCallsFromDecomposerCode()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var violations = new List<string>();
        foreach (var dir in DecomposerProjectRoots(repoRoot))
        {
            if (!Directory.Exists(dir)) continue;
            var projectRel = Path.GetRelativePath(Path.Combine(repoRoot, "app"), dir).Replace('\\', '/');
            foreach (var file in Directory.EnumerateFiles(dir, "*Decomposer.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                if (UnicodeAllowlist.Contains(rel)) continue;
                if (PipelineInDecomposerAllowlist.Contains($"{projectRel}/{rel}")) continue;
                var text = File.ReadAllText(file);
                if (DirectPipelineCall.IsMatch(text))
                    violations.Add($"{projectRel}/{rel}");
            }
        }
        Assert.True(violations.Count == 0,
            "Decomposer projects must not call IngestBatchPipeline directly (use Decomposer<T> base):\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void DecomposerProjects_NoDirectPipelineCallsFromIngestAdapters()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var violations = new List<string>();
        foreach (var dir in DecomposerProjectRoots(repoRoot))
        {
            if (!Directory.Exists(dir)) continue;
            var projectRel = Path.GetRelativePath(Path.Combine(repoRoot, "app"), dir).Replace('\\', '/');
            foreach (var file in Directory.EnumerateFiles(dir, "*Ingest*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                var relPath = $"{projectRel}/{rel}";
                if (PipelineInIngestAdapterAllowlist.Contains(relPath)) continue;
                var text = File.ReadAllText(file);
                if (DirectPipelineCall.IsMatch(text))
                    violations.Add(relPath);
            }
        }
        Assert.True(violations.Count == 0,
            "Ingest adapter modules must not call IngestBatchPipeline directly (route through Decomposer<T>):\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void DecomposerProjects_NoHandRolledParallelIngest()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var violations = new List<string>();
        foreach (var dir in DecomposerProjectRoots(repoRoot))
        {
            if (!Directory.Exists(dir)) continue;
            var projectRel = Path.GetRelativePath(Path.Combine(repoRoot, "app"), dir).Replace('\\', '/');
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                var relPath = $"{projectRel}/{rel}";
                if (ParallelIngestAllowlist.Contains(relPath)) continue;
                var text = File.ReadAllText(file);
                if (ResolveFileWorkersCall.IsMatch(text) || BoundedChannelCreate.IsMatch(text))
                    violations.Add(relPath);
            }
        }
        Assert.True(violations.Count == 0,
            "Decomposer projects must not hand-roll parallel ingest (ResolveFileWorkers/Channel.CreateBounded):\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void DecomposerProjects_ContainNoDecomposerOrchestrator()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var found = new List<string>();
        foreach (var dir in DecomposerProjectRoots(repoRoot))
        {
            if (!Directory.Exists(dir)) continue;
            var projectRel = Path.GetRelativePath(Path.Combine(repoRoot, "app"), dir).Replace('\\', '/');
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
                var text = File.ReadAllText(file);
                if (text.Contains("DecomposerOrchestrator", StringComparison.Ordinal))
                    found.Add($"{projectRel}/{Path.GetRelativePath(dir, file).Replace('\\', '/')}");
            }
        }
        Assert.True(found.Count == 0,
            "DecomposerOrchestrator was removed in Wave 3; use DecomposerMultiPhase or Decomposer<T>:\n"
            + string.Join("\n", found));
    }

    [Fact]
    public void DecomposerMultiPhase_AllowlistMatchesTree()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var found = new List<string>();
        foreach (var dir in DecomposerProjectRoots(repoRoot))
        {
            if (!Directory.Exists(dir)) continue;
            var projectRel = Path.GetRelativePath(Path.Combine(repoRoot, "app"), dir).Replace('\\', '/');
            foreach (var file in Directory.EnumerateFiles(dir, "*Decomposer.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
                var text = File.ReadAllText(file);
                if (!text.Contains(": DecomposerMultiPhase", StringComparison.Ordinal)) continue;
                var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                found.Add($"{projectRel}/{rel}");
            }
        }

        var unknown = found.Where(p => !MultiPhaseAllowlist.Contains(p)).ToList();
        var stale = MultiPhaseAllowlist.Where(k => !found.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();

        Assert.True(unknown.Count == 0,
            "New DecomposerMultiPhase sources must be added to MultiPhaseAllowlist:\n"
            + string.Join("\n", unknown));
        Assert.True(stale.Count == 0,
            "Remove migrated sources from MultiPhaseAllowlist:\n"
            + string.Join("\n", stale));
    }

    [Fact]
    public void DecomposerProjects_DecomposeAsync_AvoidsHandSubstrateChangeBuilder()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var violations = new List<string>();
        foreach (var dir in DecomposerProjectRoots(repoRoot))
        {
            if (!Directory.Exists(dir)) continue;
            var projectRel = Path.GetRelativePath(Path.Combine(repoRoot, "app"), dir).Replace('\\', '/');
            foreach (var file in Directory.EnumerateFiles(dir, "*Decomposer.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
                var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                if (HandBuilderAllowlist.Contains(rel)) continue;

                var text = File.ReadAllText(file);
                if (!text.Contains("DecomposeAsync", StringComparison.Ordinal)) continue;

                var decomposeBody = Regex.Match(
                    text,
                    @"DecomposeAsync[\s\S]*?(?=\r?\n    (?:public |private |internal |protected |public override |public sealed override ))");
                if (decomposeBody.Success && HandBuilderInDecompose.IsMatch(decomposeBody.Value))
                    violations.Add($"{projectRel}/{rel}");
                else if (!decomposeBody.Success && HandBuilderInDecompose.IsMatch(text))
                    violations.Add($"{projectRel}/{rel}");
            }
        }
        Assert.True(violations.Count == 0,
            "DecomposeAsync must route through Decomposer<T>, not hand builders:\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void SubstrateAbstractions_ExportsSharedExtractors()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var abstractions = Path.Combine(repoRoot, "app", "Laplace.Substrate", "Abstractions");
        Assert.True(File.Exists(Path.Combine(abstractions, "SharedParquetRecordStream.cs")));
        Assert.True(File.Exists(Path.Combine(abstractions, "SharedXmlFramesetReader.cs")));
        Assert.True(File.Exists(Path.Combine(abstractions, "FrameNetLemmaHelper.cs")));
        Assert.True(File.Exists(Path.Combine(abstractions, "TabBridgeHelpers.cs")));
    }

    [Fact]
    public void IngestPipeline_WorkingSetDefersBulkDescentUntilFinalize()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var pipeline = Path.Combine(repoRoot, "app", "Laplace.Substrate", "Abstractions", "IngestPipeline.cs");
        var flush = Path.Combine(repoRoot, "app", "Laplace.Substrate", "Abstractions", "IngestDescentFlush.cs");
        var pipelineText = File.ReadAllText(pipeline);
        var flushText = File.ReadAllText(flush);
        Assert.Contains("WorkingSetDeferredBatch", pipelineText, StringComparison.Ordinal);
        Assert.Contains("FinalizeWorkingSetAsync", pipelineText, StringComparison.Ordinal);
        Assert.Contains("ComposeBatchAsync", flushText, StringComparison.Ordinal);
        Assert.Contains("FinalizeWorkingSetAsync", flushText, StringComparison.Ordinal);
        Assert.Contains("BulkDescent", flushText, StringComparison.Ordinal);
    }

    [Fact]
    public void IngestDescentFlush_AlwaysRunsTierExistence()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var flush = Path.Combine(repoRoot, "app", "Laplace.Substrate", "Abstractions", "IngestDescentFlush.cs");
        var text = File.ReadAllText(flush);
        Assert.Contains("ContentTierSpine.BatchExistenceEmitBitmapsAsync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("bool probe = !config.WorkingSet", text, StringComparison.Ordinal);
        Assert.Contains("BulkDescent", text, StringComparison.Ordinal);
    }
}
