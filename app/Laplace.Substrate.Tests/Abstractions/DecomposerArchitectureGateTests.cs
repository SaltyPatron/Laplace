using System.Reflection;
using System.Text.RegularExpressions;
using Laplace.Decomposers.Tatoeba;
using Laplace.Engine.Core;
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
    };

    private static readonly HashSet<string> HandBuilderAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
    };

    /// <summary>
    /// DecomposerOrchestrator was removed in Wave 3 — multi-phase sources use
    /// <see cref="DecomposerMultiPhase"/> with nested ComposeDecomposerPhase types.
    /// </summary>
    private static readonly HashSet<string> MultiPhaseAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Laplace.Decomposers/CILI/CILIDecomposer.cs",
        "Laplace.Decomposers/ISO/ISODecomposer.cs",
        "Laplace.Decomposers/Model/ModelDecomposer.cs",
        "Laplace.Decomposers/SemLink/SemLinkDecomposer.cs",
        // Two phases: sentences.csv (entities) then links.csv (attestations). The second
        // needs the id -> content-root map the first produces as a free side effect.
        "Laplace.Decomposers/Tatoeba/TatoebaDecomposer.cs",
        "Laplace.Decomposers/Unicode/UnicodeDecomposer.cs",
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
        // Reviewed 2026-07-10: model-lane layer fan-out — independent layers
        // produced into ONE bounded channel (single reader preserves the
        // async-enumerator contract for the runner); width machine-derived
        // (WorkingSetBudgetBytes / per-layer buffer footprint, clamped to
        // ComposeWorkers). Migrates to the spine with Issue 45's remainder.
        "Laplace.Decomposers/Model/ModelTokenEdgeETL.cs",
        // Catalog-dual Syzygy unpack: bounded board/product channels into Compose.
        "Laplace.Chess/Service/SyzygyTableUnpack.cs",
        // Reviewed 2026-08-04: PGN throughput lane. Two bounded channels, each a
        // single-reader fan-in so the async-enumerator contract the runner depends on
        // is preserved: game text -> full parse, and game text -> ChessPlayingPeek for
        // the novelty gate (headers + movetext id only, so a re-ingest pays a peek
        // instead of a full parse per already-present playing). Widths derive from the
        // machine (workers * 8), not a tuned constant. The work is CPU-bound parsing
        // ahead of Compose, not substrate I/O — the spine still owns batching, dedup,
        // descent, fold and COPY. Migrates to the spine with the other three.
        "Laplace.Chess/Service/ChessPgnDecomposer.cs",
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

    /// <summary>
    /// The hand-rolled batch idiom. Nine sites wrote some form of
    /// `options.BatchSize > 1 ? options.BatchSize : &lt;literal&gt;`, and five never consulted
    /// IngestSizing at all — so those sources ingested with an identical batch on a 4-core
    /// laptop and a 128 GB server, while CLAUDE.md documented that batch sizing
    /// "deliberately has no env override" because IngestSizing/MemoryTopology own it.
    /// A private `? : 2048` overrides the machine model exactly as effectively as an env
    /// var would. IngestPipelineDefaults.ResolveBatch(profile, options) is the one resolver;
    /// a source that needs different sizing adds an IngestSourceProfile, never a literal.
    /// </summary>
    private static readonly Regex HandRolledBatch = new(
        @"BatchSize\s*>\s*1\s*\?",
        RegexOptions.Compiled);

    /// <summary>
    /// ISeedSource.Profile is the RUN-LEVEL sizing authority: it reaches
    /// IDecomposer.SizingProfile, then IngestCommands.BuildIngestOptions, and sets
    /// record_batch / commit_rows / ws_record_cap / probe_chunk / max_intents_per_commit
    /// for the entire run (the `ingest_source_sizing:` log line).
    ///
    /// It is a SEPARATE declaration site from the per-file IngestBatchConfig, and that is
    /// how it went stale: IngestSourceProfile.Tatoeba was added and wired into the
    /// decomposer's ConfigForFile while TatoebaSource.Profile still returned Default, so a
    /// live ingest sized itself off Default and nothing said so. Same for Omw, Iso, Cili
    /// and FrameNet.
    ///
    /// If a profile exists bearing a source's name, that source must use it. A source with
    /// no dedicated profile legitimately returns Default or a shared one (RelationTriple,
    /// Wiktionary, …) and is not flagged.
    /// </summary>
    [Fact]
    public void SeedSources_UseTheirOwnSizingProfile_WhenOneExists()
    {
        var profiles = typeof(IngestSourceProfile)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(IngestSourceProfile))
            .ToDictionary(f => f.Name, f => (IngestSourceProfile)f.GetValue(null)!,
                          StringComparer.OrdinalIgnoreCase);

        var sources = typeof(TatoebaSource).Assembly.GetTypes()
            .Where(t => t is { IsClass: false or true, IsAbstract: false } || t.IsValueType)
            .Where(t => t.GetInterfaces().Any(i => i.Name == "ISeedSource"))
            .ToList();
        Assert.NotEmpty(sources);

        var violations = new List<string>();
        foreach (var src in sources)
        {
            var prop = src.GetProperty("Profile", BindingFlags.Public | BindingFlags.Static);
            if (prop is null) continue;
            var actual = (IngestSourceProfile?)prop.GetValue(null);
            if (actual is null) continue;

            // "TatoebaSource" -> "Tatoeba"; "ISOSource" -> "ISO" (matched case-insensitively).
            string bare = src.Name.EndsWith("Source", StringComparison.Ordinal)
                ? src.Name[..^"Source".Length] : src.Name;
            if (!profiles.TryGetValue(bare, out var owned)) continue;   // no dedicated profile
            if (!ReferenceEquals(actual, owned))
                violations.Add($"{src.Name}.Profile is not IngestSourceProfile.{bare} "
                               + $"(got {actual.EstBytesPerRecord}B/{actual.EstComposeUnitsPerRecord}u, "
                               + $"expected {owned.EstBytesPerRecord}B/{owned.EstComposeUnitsPerRecord}u)");
        }

        Assert.True(violations.Count == 0,
            "ISeedSource.Profile sets run-level ingest sizing and must use the source's own "
            + "IngestSourceProfile where one exists:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void IngestLanes_ResolveBatchThroughTheSizingAuthority_NeverAPrivateLiteral()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var roots = DecomposerProjectRoots(repoRoot)
            .Append(Path.Combine(repoRoot, "app", "Laplace.Substrate"));
        var violations = new List<string>();
        foreach (var dir in roots)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                // Decomposer.cs documents the banned idiom in ResolveBatch's own summary.
                if (Path.GetFileName(file).Equals("Decomposer.cs", StringComparison.Ordinal)) continue;
                if (HandRolledBatch.IsMatch(File.ReadAllText(file)))
                    violations.Add(Path.GetRelativePath(repoRoot, file));
            }
        }
        Assert.True(violations.Count == 0,
            "Resolve record batches with IngestPipelineDefaults.ResolveBatch(profile, options); "
            + "add an IngestSourceProfile instead of a private literal:\n"
            + string.Join("\n", violations));
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
                    // ComposeDecomposerMultiFile< must be listed EXPLICITLY. `ComposeDecomposer<`
                    // does not cover it — "MultiFile" sits between the name and the angle
                    // bracket, so the alternative never matches and a legitimate base class
                    // reads as no base class at all.
                    Regex.IsMatch(text, @":\s*(?:\w+\s*,\s*)*(?:RelationTripleMultiFileDecomposerBase|RelationTripleDecomposerBase|RelationTripleDecomposer|ComposeDecomposerMultiFile<|ComposeDecomposer<|GrammarComposeDecomposer|GrammarIngestDecomposer|CategoryCorrespondenceDecomposer|DecomposerMultiFile<|DecomposerPhase<|DecomposerMultiPhase|Decomposer<)")
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

    /// <summary>
    /// Multi-file is already file-major (<see cref="DecomposerMultiFile{TRecord}"/>).
    /// Nesting it inside MultiPhase (FrameNet's old FnMultiFilePhase ×3) restarts the
    /// file pool per phase — phase-outer, not file-outer. ComposeDecomposerPhase over
    /// monolith streams is fine; MultiFile inside MultiPhase is not.
    /// </summary>
    [Fact]
    public void MultiPhase_DoesNotNest_DecomposerMultiFile()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var nest = new Regex(
            @":\s*DecomposerMultiFile\s*<",
            RegexOptions.Compiled);
        var violations = new List<string>();
        foreach (var rel in MultiPhaseAllowlist)
        {
            var path = Path.Combine(repoRoot, "app", rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"missing MultiPhaseAllowlist entry: {rel}");
            if (nest.IsMatch(File.ReadAllText(path)))
                violations.Add(rel);
        }
        // Also scan any *Decomposer.cs that still declares MultiPhase (stale allowlist race).
        foreach (var dir in DecomposerProjectRoots(repoRoot))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*Decomposer.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
                var text = File.ReadAllText(file);
                if (!text.Contains(": DecomposerMultiPhase", StringComparison.Ordinal)) continue;
                if (!nest.IsMatch(text)) continue;
                var rel = Path.GetRelativePath(Path.Combine(repoRoot, "app"), file).Replace('\\', '/');
                if (!violations.Contains(rel, StringComparer.OrdinalIgnoreCase))
                    violations.Add(rel);
            }
        }

        Assert.True(violations.Count == 0,
            "DecomposerMultiFile is the file-major spine — do not nest it in MultiPhase:\n"
            + string.Join("\n", violations));
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

    [Fact]
    public void SeedControlPlane_ContractsExist()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var abs = Path.Combine(repoRoot, "app", "Laplace.Substrate", "Abstractions");
        Assert.True(File.Exists(Path.Combine(abs, "SourceManifest.cs")));
        Assert.True(File.Exists(Path.Combine(abs, "SeedScope.cs")));
        Assert.True(File.Exists(Path.Combine(abs, "SourceLicense.cs")));
        var decomposer = File.ReadAllText(Path.Combine(abs, "Decomposer.cs"));
        Assert.Contains("Decomposer<TRecord, TSource, TScope>", decomposer, StringComparison.Ordinal);
        Assert.Contains("DecomposerMultiPhase<TSource, TScope>", decomposer, StringComparison.Ordinal);
        Assert.Contains("ISeedSource", File.ReadAllText(Path.Combine(abs, "SourceManifest.cs")), StringComparison.Ordinal);
        Assert.Contains("ISourceManifest", File.ReadAllText(Path.Combine(abs, "SourceManifest.cs")), StringComparison.Ordinal);
        Assert.Contains("ISeedScope", File.ReadAllText(Path.Combine(abs, "SeedScope.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void FamilyAwareBootstrap_ChildPullsParentRoot()
    {
        var expanded = SourceVocabularyBootstrap.ExpandRelationsWithFamily(new[] { "HAS_XPOS" });
        Assert.Contains("HAS_XPOS", expanded);
        Assert.Contains("HAS_POS", expanded);
        Assert.Equal("HAS_POS", SourceVocabularyBootstrap.FamilyRootCanonical("HAS_XPOS"));
        Assert.True(SourceVocabularyBootstrap.DeclaredCoversEmitted(new[] { "HAS_XPOS" }, "HAS_POS"));
        Assert.True(SourceVocabularyBootstrap.DeclaredCoversEmitted(new[] { "HAS_POS" }, "HAS_XPOS"));
        Assert.False(SourceVocabularyBootstrap.DeclaredCoversEmitted(new[] { "IS_A" }, "HAS_XPOS"));
    }

    [Fact]
    public void SeedScopes_DensePrefixesMatchPerfcache()
    {
        Assert.True(AsciiScope.InScope(0x7F));
        Assert.False(AsciiScope.InScope(0x80));
        Assert.Equal(ScopeTier.Ascii, AsciiScope.Tier);
        Assert.True(BmpScope.InScope(0xFFFF));
        Assert.False(BmpScope.InScope(0x10000));
        Assert.True(FullScope.InScope(0x10FFFF));
        Assert.False(FullScope.InScope(0x110000));
    }

    [Fact]
    public void ProductionDecomposers_DoNotImplementIDecomposerDirectly()
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
                var text = File.ReadAllText(file);
                // Direct ": IDecomposer" without an intervening base class name.
                if (Regex.IsMatch(text, @":\s*IDecomposer\b"))
                    violations.Add($"{projectRel}/{rel}");
            }
        }
        Assert.True(violations.Count == 0,
            "Production decomposers must not implement IDecomposer directly:\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void UnicodeAndHandBuilderAllowlists_AreEmpty()
    {
        Assert.Empty(UnicodeAllowlist);
        Assert.Empty(HandBuilderAllowlist);
    }

    /// <summary>
    /// MultiPhase orchestrators must call <c>RunPhaseAsync</c>. Hand
    /// <c>new SubstrateChangeBuilder</c> / <c>Writer.ApplyAsync</c> /
    /// <c>yield return Build*</c> inside <c>RunIngestAsync</c> reinvent the
    /// wheel (Unicode #776/#779). Nested <see cref="ComposeDecomposerPhase{T}"/>
    /// Compose callbacks are fine — the pipeline owns the builder.
    /// Model still hand-builds inside RunIngestAsync; shrink this allowlist only.
    /// </summary>
    private static readonly HashSet<string> MultiPhaseRunIngestHandAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Laplace.Decomposers/Model/ModelDecomposer.cs",
    };

    [Fact]
    public void DecomposerMultiPhase_RunIngestAsync_DoesNotHandBuildOrApply()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var violations = new List<string>();
        var runIngest = new Regex(
            @"RunIngestAsync\s*\([\s\S]*?(?=\r?\n    (?:public |private |internal |protected |public override |public sealed override |private (?:sealed |abstract )?class |private readonly record))",
            RegexOptions.Compiled);
        var handBuild = new Regex(@"new\s+SubstrateChangeBuilder\s*\(", RegexOptions.Compiled);
        var writerApply = new Regex(@"Writer\.ApplyAsync\s*\(", RegexOptions.Compiled);
        var yieldBuild = new Regex(@"yield\s+return\s+Build\w+\s*\(", RegexOptions.Compiled);

        foreach (var rel in MultiPhaseAllowlist)
        {
            var path = Path.Combine(repoRoot, "app", rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"missing MultiPhaseAllowlist entry: {rel}");
            var text = File.ReadAllText(path);
            var m = runIngest.Match(text);
            if (!m.Success)
            {
                violations.Add($"{rel}: no RunIngestAsync body matched");
                continue;
            }
            var body = m.Value;
            if (!body.Contains("RunPhaseAsync", StringComparison.Ordinal))
                violations.Add($"{rel}: RunIngestAsync never calls RunPhaseAsync");
            if (MultiPhaseRunIngestHandAllowlist.Contains(rel)) continue;
            if (handBuild.IsMatch(body))
                violations.Add($"{rel}: RunIngestAsync constructs SubstrateChangeBuilder");
            if (writerApply.IsMatch(body))
                violations.Add($"{rel}: RunIngestAsync calls Writer.ApplyAsync");
            if (yieldBuild.IsMatch(body))
                violations.Add($"{rel}: RunIngestAsync yield-returns Build* helper");
        }

        Assert.True(violations.Count == 0,
            "DecomposerMultiPhase.RunIngestAsync must only orchestrate RunPhaseAsync:\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void UnicodeDecomposer_HasNoHandBuilderOrWriterApply()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var path = Path.Combine(repoRoot, "app", "Laplace.Decomposers", "Unicode", "UnicodeDecomposer.cs");
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("new SubstrateChangeBuilder", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Writer.ApplyAsync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IntentStage.ResetContentBank", text, StringComparison.Ordinal);
        Assert.Contains("RunPhaseAsync", text, StringComparison.Ordinal);
        Assert.Contains("ComposeDecomposerPhase", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HandlerHotPaths_DoNotResolveFromContainer()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var violations = new List<string>();
        var forbidden = new Regex(
            @"\b(GetRequiredService|GetService|CreateScope)\s*<|\.GetRequiredService\(|\.GetService\(|container\.Resolve\b",
            RegexOptions.Compiled);
        foreach (var dir in DecomposerProjectRoots(repoRoot))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*Handler*.cs", SearchOption.AllDirectories)
                         .Concat(Directory.EnumerateFiles(dir, "*Compose*.cs", SearchOption.AllDirectories)))
            {
                if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                var text = File.ReadAllText(file);
                if (forbidden.IsMatch(text))
                    violations.Add(Path.GetRelativePath(repoRoot, file));
            }
        }
        // Composition root may resolve; handlers must not.
        Assert.True(violations.Count == 0,
            "No DI resolve in handler/compose hot paths:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void SeedIngestComposition_ExistsAsSharedRoot()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var path = Path.Combine(repoRoot, "app", "Laplace.Decomposers", "Composition", "SeedIngestComposition.cs");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("AddLaplaceSeedIngest", text, StringComparison.Ordinal);
        Assert.Contains("ISeedDecomposerResolver", text, StringComparison.Ordinal);
        Assert.Contains("IContentRecordAdapter", text, StringComparison.Ordinal);
    }
}
