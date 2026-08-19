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
        // Catalog-dual Syzygy unpack: bounded board/product channels into Compose.
        "Laplace.Chess/Service/SyzygyTableUnpack.cs",
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
    /// Decomposer implementations do not interpret the raw batch option. Conditional,
    /// clamp and fallback variants all fork operator semantics and the machine sizing model.
    /// IngestPipelineDefaults is the one resolver; a source that needs different sizing adds
    /// an IngestSourceProfile and passes the complete DecomposerOptions through.
    /// </summary>
    private static readonly Regex HandRolledBatch = new(
        @"\boptions\.BatchSize\b|\bBatchConfigDefaults\.Resolve\s*\(",
        RegexOptions.Compiled);

    private static readonly HashSet<string> BatchOptionAuthorities = new(StringComparer.Ordinal)
    {
        "BatchConfigDefaults.cs",
        "Decomposer.cs",
        "IngestRunner.cs",
    };

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
                if (BatchOptionAuthorities.Contains(Path.GetFileName(file))) continue;
                if (HandRolledBatch.IsMatch(File.ReadAllText(file)))
                    violations.Add(Path.GetRelativePath(repoRoot, file));
            }
        }
        Assert.True(violations.Count == 0,
            "Pass DecomposerOptions to IngestPipelineDefaults; decomposer implementations "
            + "must not inspect options.BatchSize or combine it with private literals:\n"
            + string.Join("\n", violations));
    }

    /// <summary>
    /// A decomposer must not hand-roll a parser for a format the grammar registry
    /// already covers.
    ///
    /// The law is written in this repo: "Tree-sitter's job is narrow: unpack container
    /// formats, then hand off." 299 grammars are vendored and ~70 are registered in
    /// engine/core/src/grammar_registry.c -- including xml, json, csv, tsv, tab, conllu,
    /// ttl, markdown and sql. Nine decomposers route through them. Fourteen parse around
    /// a grammar that exists for their exact format, and UD is the sharpest case: conllu
    /// is registered specifically for it and the decomposer still hand-rolls.
    ///
    /// It was recorded twice before and fixed neither time -- .scratchpad/34 for PGN
    /// ("a direct violation of this project's own stated law; PGN is a container format;
    /// nothing here currently hands it to tree-sitter") and .scratchpad/30 for the
    /// vendored gitcommit/gitdiff grammars left unregistered. .scratchpad/13 names the
    /// mechanism: "every new source is written by copying the nearest neighbor, forking
    /// further." Ten other decomposer laws have a gate here. This one had none, which is
    /// why it drifted for three sessions.
    ///
    /// SHRINK-ONLY. The allowlist is the measured population as of 2026-08-10, so the
    /// gate lands enumerated instead of red on merge day. Removing an entry is the fix;
    /// adding one is a regression that must be argued for in the diff.
    /// </summary>
    [Fact]
    public void DecomposerProjects_NoHandRolledParserForARegisteredGrammar()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();

        // Formats the registry already unpacks. Parsed from the registry itself rather
        // than hardcoded, so registering a new grammar tightens this gate automatically.
        var registryPath = Path.Combine(repoRoot, "engine", "core", "src", "grammar_registry.c");
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(registryPath))
            foreach (Match m in Regex.Matches(File.ReadAllText(registryPath), "\"([a-z0-9_+-]+)\""))
                registered.Add(m.Groups[1].Value);

        // Hand-rolled parser idioms, mapped to the format they stand in for.
        var handRolled = new (Regex Pattern, string Format)[]
        {
            (new Regex(@"\bXDocument\b|\bXmlReader\b|\bXElement\b", RegexOptions.Compiled), "xml"),
            (new Regex(@"\bJsonDocument\b|\bUtf8JsonReader\b", RegexOptions.Compiled), "json"),
        };

        // Measured population 2026-08-10, shrink-only. NINETEEN files, not the fourteen
        // a grep over *Decomposer.cs suggested -- the hand-rolling lives in helper types
        // (WiktionaryEntry, LlamaTokenizerParser, ConceptNetUri, LanguageGraph), which is
        // exactly how it stayed invisible: the decomposer looks clean and the parser sits
        // one file over.
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "app/Laplace.Decomposers/FrameNet/FrameNetDecomposer.cs",
            "app/Laplace.Decomposers/FrameNet/FrameNetLuIngest.cs",
            "app/Laplace.Decomposers/PropBank/PropBankDecomposer.cs",
            "app/Laplace.Decomposers/VerbNet/VerbNetDecomposer.cs",
            "app/Laplace.Decomposers/SemLink/SemLinkDecomposer.cs",
            "app/Laplace.Decomposers/SemLink/SemLinkIngestAdapter.cs",
            "app/Laplace.Decomposers/Wiktionary/WiktionaryDecomposer.cs",
            "app/Laplace.Decomposers/Wiktionary/WiktionaryEntry.cs",
            "app/Laplace.Decomposers/Wiktionary/WiktionaryGrammarWitness.cs",
            "app/Laplace.Decomposers/Wiktionary/WiktionaryJsonFilter.cs",
            "app/Laplace.Decomposers/ConceptNet/ConceptNetUri.cs",
            "app/Laplace.Decomposers/ISO/LanguageGraph.cs",
            "app/Laplace.Decomposers/Model/LlamaRecipeExtractor.cs",
            "app/Laplace.Decomposers/Model/LlamaTokenizerParser.cs",
            "app/Laplace.Decomposers/Model/ModelConfigReader.cs",
            "app/Laplace.Decomposers/Model/RecipeDescriptor.cs",
            "app/Laplace.Decomposers/Model/RecipeExtractor.cs",

            // Over HTTP, and still violations. An earlier draft of this gate exempted
            // anything touching HttpClient on the reasoning that "a REST reply is a wire
            // format, not a container." That is wrong: a response carries a Content-Type
            // and a body, the body is the same container it would be on disk, and
            // Content-Type IS the format declaration. Transport does not change payload.
            //
            // The two files prove it. ChessGameFetcher sends
            // `Accept: application/x-chess-pgn` -- it requests a PGN container, and `pgn`
            // is registered. LichessBot reads NDJSON with ReadLineAsync and a bare
            // `catch { }` that swallows every unparseable line. The exemption was written
            // to make this gate pass, which is the same defect as adjusting a test to fit
            // the code. Listed as measured violations instead.
            "app/Laplace.Chess/Service/ChessGameFetcher.cs",
            "app/Laplace.Chess/Service/LichessBot.cs",
        };

        var violations = new List<string>();
        foreach (var dir in DecomposerProjectRoots(repoRoot))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                if (allowed.Contains(rel)) continue;
                var text = File.ReadAllText(file);

                foreach (var (pattern, format) in handRolled)
                    if (registered.Contains(format) && pattern.IsMatch(text))
                        violations.Add($"{rel} hand-rolls {format} (grammar '{format}' IS registered)");
            }
        }

        // LINE-ORIENTED FORMATS. The block above only knows two idioms -- XDocument and
        // JsonDocument -- so it scores zero for every lane that hand-rolls a delimited
        // format, and TEN registered grammars were invisible to it: conllu, csv, tsv,
        // tab, pgn, markdown, md, ttl, turtle, sql.
        //
        // The commit that introduced this gate said "UD is the sharpest: conllu is
        // registered specifically for it and the decomposer still hand-rolls." That
        // sentence was written in the commit message of a gate that could not detect it.
        // UdConlluParser uses neither XDocument nor JsonDocument, so it was never in the
        // allowlist and never could have been -- and UD is the slowest lane on the box.
        // "Nineteen violations" measured which C# TYPE was used, not which law was broken.
        //
        // Format association is by DIRECTORY, not by file. UDDecomposer.cs names
        // "*.conllu"; UdConlluParser.cs one file over does the parsing and names no
        // extension at all. File-local matching finds 2 files and misses UD entirely --
        // the same "the parser sits one file over" evasion recorded above.
        //
        // Detection requires BOTH line reading AND delimiter field extraction, because
        // directory scope alone over-reports badly (38 files, including IngestInventory
        // and LanguageFilter, which merely live beside a file that names a format).
        var lineFormats = new[] { "conllu", "csv", "tsv", "tab", "pgn", "markdown", "md", "ttl", "turtle" }
            .Where(registered.Contains).ToArray();
        var extPattern = new Regex(
            "\"\\*?\\.(" + string.Join('|', lineFormats) + ")\"",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var lineRead = new Regex(
            @"\bReadLine\b|\bReadLineAsync\b|\bReadAllLines\b|\bStreamingUtf8LineReader\b",
            RegexOptions.Compiled);
        var fieldSplit = new Regex(
            @"Split\s*\(\s*['""]?\\?[t,|]|IndexOf\s*\(\s*\(?byte\)?\s*['""]\\?[t,|]|\bTryField\b|['""]\\t['""]",
            RegexOptions.Compiled);

        // GrammarRowReader.ReadFieldsAsync(path, modalityId) IS the compliant route for a
        // line format -- it takes the modality and reads fields through the grammar. It
        // reads lines and splits fields BECAUSE it is the bridge, so exempting it is not
        // a carve-out; flagging it would be flagging the fix. Everything else has this
        // available and does not call it.
        var lineExempt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "app/Laplace.Substrate/Abstractions/GrammarRowReader.cs",
        };

        // Measured 2026-08-10, shrink-only. THREE, each with GrammarRowReader available:
        //   UdConlluParser.cs        conllu, registered FOR UD. The 3,124s lane.
        //   TabBridgeHelpers.cs      .tab rows straight through StreamingUtf8LineReader.
        //   ChessOpeningsDecomposer  ReadLineAsync + line.Split('\t').
        var lineAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "app/Laplace.Decomposers/UD/UdConlluParser.cs",
            "app/Laplace.Substrate/Abstractions/TabBridgeHelpers.cs",
            "app/Laplace.Chess/Service/ChessOpeningsDecomposer.cs",
        };

        if (lineFormats.Length > 0)
        {
            var dirFormats = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            var scanned = new List<(string Rel, string Path, string Text)>();
            foreach (var dir in DecomposerProjectRoots(repoRoot))
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
                    if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                    if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                    var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                    var text = File.ReadAllText(file);
                    scanned.Add((rel, Path.GetDirectoryName(file)!, text));
                    foreach (Match m in extPattern.Matches(text))
                    {
                        var key = Path.GetDirectoryName(file)!;
                        if (!dirFormats.TryGetValue(key, out var set))
                            dirFormats[key] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.Add(m.Groups[1].Value.ToLowerInvariant());
                    }
                }
            }

            foreach (var (rel, dirPath, text) in scanned)
            {
                if (lineAllowed.Contains(rel) || lineExempt.Contains(rel)) continue;
                if (!dirFormats.TryGetValue(dirPath, out var fmts)) continue;
                if (!lineRead.IsMatch(text) || !fieldSplit.IsMatch(text)) continue;
                violations.Add(
                    $"{rel} hand-rolls a delimited format its own lane declares ({string.Join(", ", fmts)}) "
                    + "— GrammarRowReader.ReadFieldsAsync(path, modalityId) is the registered route");
            }
        }

        Assert.True(violations.Count == 0,
            "Container formats go through the grammar registry, not a hand-rolled parser. "
            + "Register/route the format instead of parsing it in C#:\n"
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
    public void GenericDecomposer_OwnsTheOnlyProductionDriver()
    {
        var driver = typeof(Decomposer<>).GetMethod(nameof(IDecomposer.DecomposeAsync));
        Assert.NotNull(driver);
        // Interface implementations are emitted as virtual/final in IL even when the
        // C# method is not virtual. Final is the part that prevents vendor replacement.
        Assert.True(driver.IsFinal,
            "vendor decomposers must not replace the generic single/multi-file driver");
    }

    [Fact]
    public void RuntimeEtl_UsesTheGenericMultiFileVendorContract()
    {
        Assert.True(typeof(EtlDecomposer).IsSubclassOf(
            typeof(DecomposerMultiFile<GrammarIngestRecord>)));
        Assert.False(typeof(DecomposerMultiPhase).IsAssignableFrom(typeof(EtlDecomposer)));
    }

    [Fact]
    public void ProductionHotPath_DoesNotCallTheNativeNoOpContentReset()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var substrate = Path.Combine(repoRoot, "app", "Laplace.Substrate");
        var violations = Directory.EnumerateFiles(substrate, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}"))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(file => File.ReadAllText(file).Contains("IntentStage.ResetContentBank", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(repoRoot, file))
            .ToList();

        Assert.Empty(violations);
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
