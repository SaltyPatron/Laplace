using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>Explicit corpus-evaluation settings; gauntlet settings have their own job scope.</summary>
public sealed record StockfishEvaluationOptions
{
    public int Depth { get; init; } = 10;
    public long Nodes { get; init; }
    public int Threads { get; init; } = 1;
    public int HashMb { get; init; } = 16;
    public string NumaPolicy { get; init; } = "auto";
    public string? SyzygyPath { get; init; }
    public string? EvalFile { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public int? Processes { get; init; }

    public static StockfishEvaluationOptions FromEnvironment(int depth, long nodes)
    {
        static string? Read(string suffix) => LaplaceInstall.TryReadConfig(
            "LAPLACE_STOCKFISH_EVAL_" + suffix, "chess-lab.env");
        static int ReadInt(string suffix, int fallback)
        {
            var value = Read(suffix);
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            if (!int.TryParse(value, out int parsed) || parsed <= 0)
                throw new ArgumentException($"LAPLACE_STOCKFISH_EVAL_{suffix} must be a positive integer.");
            return parsed;
        }
        var options = new StockfishEvaluationOptions
        {
            Depth = depth, Nodes = nodes,
            Threads = ReadInt("THREADS", 1), HashMb = ReadInt("HASH_MB", 16),
            NumaPolicy = Read("NUMA_POLICY")?.Trim() is { Length: > 0 } numa ? numa : "auto",
            SyzygyPath = Read("SYZYGY_PATH"), EvalFile = Read("FILE"),
            TimeoutSeconds = ReadInt("TIMEOUT_SECONDS", 30),
            Processes = string.IsNullOrWhiteSpace(Read("PROCESSES")) ? null : ReadInt("PROCESSES", 1),
        };
        options.Validate();
        return options;
    }

    internal void Validate()
    {
        if (Depth is < 1 or > 40 || Nodes < 0 || Threads < 1 || HashMb < 1 || TimeoutSeconds < 1 || Processes < 1)
            throw new ArgumentOutOfRangeException(nameof(StockfishEvaluationOptions),
                "Evaluation requires depth 1..40, nonnegative nodes and positive threads, hash MB and timeout.");
        if (string.IsNullOrWhiteSpace(NumaPolicy) || NumaPolicy.Contains('\n') || NumaPolicy.Contains('\r'))
            throw new ArgumentException("Evaluation NUMA policy must be one UCI option value.");
    }

    internal StockfishEvaluationResources ResolveResources()
        => StockfishEvaluationResources.Resolve(this, CpuTopology.ResolveCpuBoundWorkers(), Environment.ProcessorCount);
}

/// <summary>The admitted evaluator process/thread budget, captured once at job acquisition.</summary>
public sealed record StockfishEvaluationResources(int CpuGrant, int Processes, bool ExplicitProcessOverride,
    long RequestedThreadSlots, bool Oversubscribed)
{
    internal static StockfishEvaluationResources Resolve(StockfishEvaluationOptions options,
        int topologyWorkers, int processCpuQuota)
    {
        options.Validate();
        int grant = Math.Max(1, Math.Min(topologyWorkers, processCpuQuota));
        int processes = options.Processes ?? Math.Max(1, grant / options.Threads);
        long slots = (long)processes * options.Threads;
        return new(grant, processes, options.Processes.HasValue, slots, slots > grant);
    }
}

/// <summary>
/// Content identity of a calculated Stockfish witness. Artifact locations remain receipt
/// metadata; exact engine/network/table bytes, effective UCI options and search policy own
/// the cache and ingest namespace. Legacy v1 caches/markers do not acquire this identity.
/// </summary>
public sealed class StockfishEvaluationRecipe
{
    public string CanonicalManifest { get; }
    public string Id { get; }
    public string EngineSha256 { get; }
    public string EngineName { get; }
    public StockfishEvaluationOptions Settings { get; }
    public IReadOnlyDictionary<string, string> EffectiveOptions { get; }
    public StockfishEvaluationResources Resources { get; }

    public string MarkerKey(string lineIdentity)
        => $"chess/stockfish-eval/{lineIdentity}/v{ChessStockfishEval.Version}/recipe/{Id}";

    public string InputKey(string fen) => $"chess/stockfish-evaluation-input/{Id}/{fen}";

    private StockfishEvaluationRecipe(string manifest, string engineSha256, string engineName,
        StockfishEvaluationOptions settings, IReadOnlyDictionary<string, string> effectiveOptions,
        StockfishEvaluationResources resources)
    {
        CanonicalManifest = manifest;
        Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        EngineSha256 = engineSha256;
        EngineName = engineName;
        Settings = settings;
        EffectiveOptions = effectiveOptions;
        Resources = resources;
    }

    internal static StockfishEvaluationRecipe Capture(string executable, string engineName,
        StockfishEvaluationOptions settings, IReadOnlyDictionary<string, string> effectiveOptions)
    {
        settings.Validate();
        string binaryHash = FileSha256(executable);
        var canonicalOptions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in effectiveOptions) canonicalOptions.Add(key, value);
        var networks = new List<Artifact>();
        foreach (var (key, value) in effectiveOptions.Where(static kv => kv.Key.StartsWith("EvalFile", StringComparison.Ordinal)))
        {
            if (key == "EvalFile" && !string.IsNullOrWhiteSpace(settings.EvalFile))
            {
                var file = Path.GetFullPath(settings.EvalFile);
                string hash = FileSha256(file);
                networks.Add(new Artifact(key, hash));
                canonicalOptions[key] = "sha256:" + hash;
            }
            else
            {
                // The source build embeds its default NNUE bytes in the hashed executable.
                // Include possible external fallback bytes as well for explicitly selected
                // custom builds with NNUE embedding disabled.
                networks.Add(new Artifact(key + "/embedded/" + Path.GetFileName(value), binaryHash));
                foreach (var directory in new[] { Environment.CurrentDirectory, Path.GetDirectoryName(Path.GetFullPath(executable))! }.Distinct())
                {
                    var candidate = Path.Combine(directory, value);
                    if (File.Exists(candidate))
                        networks.Add(new Artifact(key + "/external/" + Path.GetFileName(value), FileSha256(candidate)));
                }
            }
        }

        var tables = new List<Artifact>();
        if (effectiveOptions.TryGetValue("SyzygyPath", out var tablePath) && !string.IsNullOrWhiteSpace(tablePath))
        {
            foreach (var directory in tablePath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                foreach (var file in Directory.EnumerateFiles(directory)
                             .Where(static p => p.EndsWith(".rtbw", StringComparison.OrdinalIgnoreCase)
                                 || p.EndsWith(".rtbz", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(static p => p, StringComparer.Ordinal))
                    tables.Add(new Artifact(Path.GetFileName(file), FileSha256(file)));
            if (tables.Count == 0) throw new InvalidDataException("Selected Stockfish Syzygy path contains no tablebase files.");
            // Search order matters when duplicate material names have different bytes.
            canonicalOptions["SyzygyPath"] = "content-manifest";
        }
        var resources = settings.ResolveResources();
        string manifest = JsonSerializer.Serialize(new
        {
            schema = "laplace-stockfish-evaluation/v2",
            analyzerVersion = ChessStockfishEval.Version,
            engineName, engineSha256 = binaryHash,
            options = canonicalOptions,
            networks = networks.Distinct().OrderBy(static a => a.Name, StringComparer.Ordinal)
                .ThenBy(static a => a.Sha256, StringComparer.Ordinal),
            tablebases = tables,
            depth = settings.Nodes > 0 ? (int?)null : settings.Depth,
            nodes = settings.Nodes,
            timeoutSeconds = settings.TimeoutSeconds,
            resources,
            input = "complete-fen",
            searchState = "ucinewgame-before-each-position",
            reproducibility = settings.Threads == 1 ? "single-thread-cold-search" : "parallel-search-first-successful-observation",
        });
        return new StockfishEvaluationRecipe(manifest, binaryHash, engineName, settings,
            effectiveOptions.ToDictionary(static kv => kv.Key, static kv => kv.Value, StringComparer.Ordinal), resources);
    }

    // Injected evaluators are explicit test providers, never silently identified as the
    // locally installed Stockfish binary. The production constructor requires Capture.
    internal static StockfishEvaluationRecipe ForTests(string declaration,
        StockfishEvaluationOptions? settings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declaration);
        settings ??= new StockfishEvaluationOptions();
        string manifest = JsonSerializer.Serialize(new { schema = "test-stockfish-evaluator/v2", declaration, settings });
        return new StockfishEvaluationRecipe(manifest, "test-provider", declaration, settings,
            new Dictionary<string, string>(), settings.ResolveResources());
    }

    internal static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record Artifact(string Name, string Sha256);
}
