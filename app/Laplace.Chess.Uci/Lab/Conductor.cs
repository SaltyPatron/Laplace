using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Laplace.Chess.Uci.Lab;

/// <summary>One engine as fastchess starts it: a display name, the command and its arguments, the UCI options it is
/// given, and an optional node budget per move.</summary>
public sealed record ConductedEngine(string Name, string Cmd, IReadOnlyList<KeyValuePair<string, string>> Options,
    long? Nodes = null, IReadOnlyList<string>? Args = null)
{
    /// <summary>fastchess's -engine block. A node-limited engine in a match without a clock plays tc=inf nodes=N.</summary>
    public List<string> EngineArgs(string? tc, string spec)
    {
        var args = new List<string> { "-engine", $"name={Name}", $"cmd={Cmd}", $"dir={Path.GetDirectoryName(Cmd)}", "proto=uci" };
        if (Args is { Count: > 0 }) args.Add("args=" + string.Join(' ', Args));
        args.AddRange(Options.Select(static o => $"option.{o.Key}={o.Value}"));
        if (Nodes is { } nodes)
        {
            if (tc is null) args.Add("tc=inf");
            args.Add($"nodes={nodes.ToString(CultureInfo.InvariantCulture)}");
        }
        else if (tc is null)
            throw new ArgumentException($"{spec}: a rung needs nodes=N or the match a --tc");
        return args;
    }
}

/// <summary>A finished fastchess run: what was run, how it exited, where its games are, and its closing table.</summary>
public sealed record ConductedRun(string Label, IReadOnlyList<string> Argv, int Exit, string Pgn, string Started, string Finished,
    IReadOnlyList<string> Summary)
{
    public JsonObject ToJson() => new()
    {
        ["label"] = Label, ["argv"] = Json.Array(Argv), ["exit"] = Exit, ["pgn"] = Pgn,
        ["started"] = Started, ["finished"] = Finished, ["summary"] = Json.Array(Summary),
    };
}

/// <summary>
/// The conductor and its instruments: fastchess (deploy/chess-ladder-release.json pins it), the opening suites, the
/// adjudication law (rules, tablebase and a move limit, never an engine's score), Ordo, and the machine's own
/// witnesses (the host, the GPU and its holders, WHEA-Logger event 19).
/// </summary>
public static class Conductor
{
    public static string Fastchess(IReadOnlyDictionary<string, string> cfg)
        => cfg.GetValueOrDefault("LAPLACE_FASTCHESS") ?? LabFiles.Which("fastchess") ?? throw new ArgumentException("LAPLACE_FASTCHESS is not set");

    /// <summary>The opening suite by name under LAPLACE_CHESS_BOOKS (else the data root's pinned books directory), or a
    /// path; fastchess's -openings and -srand, and the book's receipt.</summary>
    public static (List<string> Args, JsonObject Receipt) BookArgs(IReadOnlyDictionary<string, string> cfg, string book, long seed, int plies)
    {
        var lock_ = LabLocks.Ladder["books"]!;
        string root = cfg.GetValueOrDefault("LAPLACE_CHESS_BOOKS")
            ?? Path.Combine(ChessLabConfig.DataRoot(cfg), lock_["directory"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));
        string path = Path.IsPathRooted(book) ? book : Path.Combine(root, book);
        if (!File.Exists(path)) throw new ArgumentException($"opening suite {path} is missing (LAPLACE_CHESS_BOOKS)");
        string format = Path.GetExtension(path).Equals(".pgn", StringComparison.OrdinalIgnoreCase) ? "pgn" : "epd";
        var args = new List<string> { "-openings", $"file={path}", $"format={format}", "order=random" };
        if (format == "pgn") args.Add($"plies={plies}");
        args.AddRange(["-srand", seed.ToString(CultureInfo.InvariantCulture)]);
        var receipt = new JsonObject
        {
            ["file"] = path, ["sha256"] = lock_["files"]?[Path.GetFileName(path)]?.GetValue<string>(), ["order"] = "random",
            ["plies"] = format == "pgn" ? plies : null, ["seed"] = seed, ["commit"] = lock_["commit"]!.GetValue<string>(),
        };
        return (args, receipt);
    }

    /// <summary>A 200-move limit, and Syzygy adjudication (5 pieces, both sides) when the tables are installed.</summary>
    public static List<string> Adjudication(IReadOnlyDictionary<string, string> cfg, int maxMoves = 200, bool tablebases = true)
    {
        var args = new List<string> { "-maxmoves", maxMoves.ToString(CultureInfo.InvariantCulture) };
        if (tablebases && cfg.GetValueOrDefault("LAPLACE_SYZYGY") is { Length: > 0 } syzygy)
            args.AddRange(["-tb", syzygy, "-tbpieces", "5", "-tbadjudicate", "BOTH"]);
        return args;
    }

    /// <summary>One fastchess run: the engines, then -each, the caller's arguments (book, adjudication), the pairing,
    /// the PGN and log beside a transcript, and the event name. Colour-swapped pairs: games is rounds of two.</summary>
    public static ConductedRun Run(IToolRunner runner, string fastchess, string outDir, string label,
        IReadOnlyList<(ConductedEngine Engine, string Spec)> engines, IEnumerable<string> extra, int games, int concurrency,
        string? affinity, string? tc, string? tournament = null, string? eventName = null)
    {
        string pgn = Path.Combine(outDir, label + ".pgn");
        var argv = new List<string> { fastchess };
        foreach (var (engine, spec) in engines) argv.AddRange(engine.EngineArgs(tc, spec));
        argv.Add("-each");
        if (tc is not null) argv.Add($"tc={tc}");
        argv.Add("timemargin=10000");
        argv.AddRange(extra);
        argv.AddRange(["-games", "2", "-rounds", Math.Max(1, games / 2).ToString(CultureInfo.InvariantCulture),
            "-concurrency", concurrency.ToString(CultureInfo.InvariantCulture), "-recover",
            "-report", "penta=true", "-ratinginterval", "0", "-autosaveinterval", "0",
            "-pgnout", $"file={pgn}", "nodes=true", "nps=true", "tbhits=true",
            "-log", $"file={Path.Combine(outDir, label + ".log")}", "level=warn",
            "-event", eventName ?? $"laplace-ladder/{Path.GetFileName(outDir)}/{label}"]);
        if (tournament is not null) argv.AddRange(["-tournament", tournament]);
        if (affinity is not null) argv.AddRange(["-use-affinity", affinity]);
        string started = Now();
        string transcript = Path.Combine(outDir, label + ".out");
        int exit = runner.Run(new ToolRun(fastchess, argv.Skip(1).ToList(), outDir, TranscriptFile: transcript)).ExitCode;
        string text = File.Exists(transcript) ? File.ReadAllText(transcript) : "";
        return new ConductedRun(label, argv, exit, pgn, started, Now(), Summary(text, engines.Count));
    }

    private static readonly Regex SummaryLine = new(@"^\s*(Results of|Elo:|LOS:|Games:|Ptnml|Score of|Elo difference|Rank\s+Name)");
    private static readonly Regex TableRow = new(@"^\s+\d+\s+\S+\s+(-?[\d.]+|-?inf)\s");

    /// <summary>A match's closing block, or a tournament's final table (header and one row per engine).</summary>
    public static List<string> Summary(string transcript, int engines)
    {
        var lines = transcript.Replace("\r", "").Split('\n').Where(l => SummaryLine.IsMatch(l) || TableRow.IsMatch(l)).ToList();
        int keep = engines + 9;
        return lines.Count > keep ? lines[^keep..] : lines;
    }

    /// <summary>Ordo over a run's PGNs (and a calibration run's, so a gauntlet's database is connected), one player fixed.</summary>
    public static JsonObject Ordo(IToolRunner runner, IReadOnlyDictionary<string, string> cfg, string outDir, string anchor,
        double anchorElo, int simulations = 1000, string? ladder = null)
    {
        string binary = cfg.GetValueOrDefault("LAPLACE_ORDO") ?? LabFiles.Which("ordo") ?? throw new ArgumentException("LAPLACE_ORDO is not set");
        var pgns = Directory.EnumerateFiles(outDir, "*.pgn").Where(static p => Path.GetFileName(p) != "all.pgn").Order(StringComparer.Ordinal).ToList();
        if (ladder is not null)
            pgns.AddRange(Directory.EnumerateFiles(ladder, "*.pgn").Where(static p => Path.GetFileName(p) != "all.pgn").Order(StringComparer.Ordinal));
        string combined = Path.Combine(outDir, "all.pgn");
        using (var sink = new StreamWriter(combined, false, new UTF8Encoding(false)))
            foreach (var p in pgns) sink.Write(File.ReadAllText(p).TrimEnd() + "\n\n");
        var argv = new List<string> { binary, "-p", combined, "-a", anchorElo.ToString(CultureInfo.InvariantCulture), "-A", anchor, "-W", "-D",
            "-s", simulations.ToString(CultureInfo.InvariantCulture), "-o", Path.Combine(outDir, "ratings.txt"), "-c", Path.Combine(outDir, "ratings.csv") };
        var reply = runner.Run(new ToolRun(binary, argv.Skip(1).ToList()));
        if (reply.ExitCode != 0)
            // an unconnected database (an all-loss gauntlet, say) is a result to report, not a crash
            return new JsonObject { ["argv"] = Json.Array(argv), ["error"] = $"ordo exited {reply.ExitCode}", ["table"] = reply.Stdout + reply.Stderr };
        return new JsonObject { ["argv"] = Json.Array(argv), ["table"] = File.ReadAllText(Path.Combine(outDir, "ratings.txt")) };
    }

    /// <summary>The GPU (name, driver, memory) and, on Windows, which embedding services still hold it.</summary>
    public static (string? Gpu, List<string>? Held) GpuMode(IToolRunner runner)
    {
        string? line;
        try
        {
            line = runner.Run(new ToolRun("nvidia-smi", ["--query-gpu=name,driver_version,memory.free,memory.used", "--format=csv,noheader"],
                Timeout: TimeSpan.FromSeconds(30))).Stdout.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (null, null); }
        List<string>? held = null;
        if (OperatingSystem.IsWindows())
            held = new[] { "llama-embedding", "llama-embedding-text", "llama-reranker" }
                .Where(s => !runner.Run(new ToolRun("sc.exe", ["query", s])).Stdout.Contains("STOPPED", StringComparison.Ordinal)).ToList();
        return (line, held);
    }

    /// <summary>The count of WHEA-Logger event 19 (corrected hardware errors) in the System log; null off Windows.</summary>
    public static int? Whea19(IToolRunner runner)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var reply = runner.Run(new ToolRun("wevtutil.exe",
            ["qe", "System", "/q:*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=19)]]", "/f:xml"],
            Timeout: TimeSpan.FromSeconds(120)));
        return Regex.Matches(reply.Stdout, "<Event[ >]").Count;
    }

    /// <summary>The installed Stockfish's identity: tag and commit, binary SHA-256, the compiler line, the network and its
    /// SHA-256, and the bench signature, which must be the generation's (deploy/stockfish-profiles.json).</summary>
    public static JsonObject StockfishReceipt(IToolRunner runner, string cmd)
    {
        var profiles = LabLocks.StockfishProfiles["identity"]!;
        var lock_ = LabLocks.Stockfish;
        var bench = runner.Run(new ToolRun(cmd, ["bench"], Timeout: TimeSpan.FromSeconds(300)));
        var nodes = Regex.Match(bench.Output, @"Nodes searched\s*:\s*(\d+)");
        long? benchNodes = nodes.Success ? long.Parse(nodes.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        string compiler = runner.Run(new ToolRun(cmd, ["compiler"], Timeout: TimeSpan.FromSeconds(60))).Stdout.Trim();
        string network = profiles["network"]!.GetValue<string>();
        string dir = Path.GetDirectoryName(Path.GetFullPath(cmd))!;
        string? netFile = new[] { Path.Combine(dir, network), Path.Combine(Path.GetDirectoryName(dir) ?? dir, "src", network) }.FirstOrDefault(File.Exists);
        long expected = profiles["bench_nodes"]!.GetValue<long>();
        var record = new JsonObject
        {
            ["tag"] = lock_["tag"]!.GetValue<string>(), ["commit"] = lock_["commit"]!.GetValue<string>(),
            ["binary"] = Path.GetFullPath(cmd), ["binary_sha256"] = LabFiles.Sha256(cmd), ["compiler"] = compiler, ["network"] = network,
            ["network_sha256"] = netFile is null ? null : LabFiles.Sha256(netFile),
            ["network_note"] = netFile is null ? "embedded in the binary; no copy beside it"
                : "embedded in the binary; the file's SHA-256 begins with the name's 12 hex digits",
            ["bench_nodes"] = benchNodes, ["bench_expected"] = expected,
            ["host"] = Environment.MachineName, ["measured_at"] = Now(),
        };
        if (netFile is not null && !record["network_sha256"]!.GetValue<string>().StartsWith(network.Split('-')[1].Split('.')[0], StringComparison.Ordinal))
            throw new ArgumentException($"{netFile} does not hash to its name");
        if (benchNodes != expected)
            throw new ArgumentException($"bench is {benchNodes?.ToString(CultureInfo.InvariantCulture) ?? "None"}, {lock_["tag"]}'s is {expected}: a different engine");
        return record;
    }

    /// <summary>The conductor's identity: path, SHA-256, the pinned version and commit, and whether the bytes are a pinned asset.</summary>
    public static JsonObject ConductorIdentity(string fastchess)
    {
        var lock_ = LabLocks.Ladder["fastchess"]!;
        string sha = LabFiles.Sha256(fastchess);
        return new JsonObject
        {
            ["path"] = fastchess, ["sha256"] = sha, ["lock"] = lock_["version"]!.GetValue<string>(),
            ["commit"] = lock_["commit"]!.GetValue<string>(),
            ["pinned"] = lock_["assets"]!.AsObject().Any(a => a.Value?["binary_sha256"]?.GetValue<string>() == sha),
        };
    }

    public static JsonObject Machine() => new()
    {
        ["host"] = Environment.MachineName, ["platform"] = RuntimeInformation.OSDescription,
        ["processor"] = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString(),
    };

    public static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);

    public static string NewRun(string baseDir, string kind)
    {
        string dir = Path.Combine(baseDir, $"{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{kind}");
        if (Directory.Exists(dir)) throw new IOException($"{dir} already exists");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string DefaultLadderDir()
        => Environment.GetEnvironmentVariable("LAPLACE_CHESS_LADDER_DIR") is { Length: > 0 } d ? d
            : OperatingSystem.IsWindows() ? @"D:\Data\Laplace\work\chess\ladder" : "/vault/work/chess/ladder";

    public static string DefaultMatchDir()
        => Environment.GetEnvironmentVariable("LAPLACE_CHESS_MATCH_DIR") is { Length: > 0 } d ? d
            : OperatingSystem.IsWindows() ? @"D:\Data\Laplace\work\chess\match" : "/vault/work/chess/match";
}

internal static class Json
{
    public static JsonArray Array(IEnumerable<string> items) => new(items.Select(static i => (JsonNode?)JsonValue.Create(i)).ToArray());

    public static JsonObject Options(IEnumerable<KeyValuePair<string, string>> options)
    {
        var o = new JsonObject();
        foreach (var (k, v) in options) o[k] = v;
        return o;
    }
}
