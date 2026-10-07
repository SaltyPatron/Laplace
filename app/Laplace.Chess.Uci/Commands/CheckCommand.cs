using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Laplace.Chess.Service.Uci;
using Laplace.Chess.Uci.Lab;

namespace Laplace.Chess.Uci.Commands;

/// <summary>One line of the dependency report.</summary>
public sealed record CheckItem(string Name, string Status, bool Required, JsonNode? Detail)
{
    public JsonObject ToJson() => new() { ["name"] = Name, ["status"] = Status, ["required"] = Required, ["detail"] = Detail?.DeepClone() };
}

/// <summary>The probes the report runs; each is replaceable, so tests stand in for engines, tools and the network.</summary>
public sealed class CheckProbes
{
    public IToolRunner Runner { get; init; } = ProcessToolRunner.Instance;
    public Func<UciProcessStart, UciProcess> StartEngine { get; init; } = UciProcess.Start;
    public Func<string, JsonNode> GitHubJson { get; init; } = DefaultGitHubJson;
    public Func<string?, int, string?, JsonObject> Zstd { get; init; } = ZstdProbe.Probe;
    public Func<IToolRunner, string, string, JsonNode, JsonObject> CutechessGui { get; init; } =
        static (runner, binary, receipt, lock_) => CutechessProbe.VerifyGuiInstall(runner, binary, receipt, lock_);

    private static JsonNode DefaultGitHubJson(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Laplace-dependency-check");
        return JsonNode.Parse(http.GetStringAsync(url).GetAwaiter().GetResult())!;
    }
}

/// <summary>
/// <c>check</c>: the installed chess tools and data, exercising the actual configured binaries. No installation, game
/// creation, account upgrade or substrate write. <c>--check-latest</c> also asks GitHub for the official stable
/// releases. Data coverage and online account readiness are reported apart from executable readiness.
/// </summary>
public static class CheckCommand
{
    private static readonly string[] ExecutableChecks = ["stockfish", "cutechess", "laplace-uci", "cutechess-gui"];

    public static (JsonObject Report, int Exit) Run(ChessCommandLine cmd, CheckProbes? probes = null,
        IReadOnlyDictionary<string, string>? config = null)
    {
        probes ??= new CheckProbes();
        string prefix = cmd.Value("prefix") ?? ChessLabConfig.DefaultPrefix();
        var cfg = config ?? ChessLabConfig.Read(prefix);
        bool windows = OperatingSystem.IsWindows();
        string suffix = windows ? ".exe" : "";
        string repo = ChessLabConfig.RepoRoot() ?? AppContext.BaseDirectory;
        string external = cfg.GetValueOrDefault("LAPLACE_EXTERNAL") ?? (windows ? Path.Combine(repo, "external") : "/build/external");
        string source = cfg.GetValueOrDefault("LAPLACE_STOCKFISH_SOURCE") ?? Path.Combine(external, "stockfish");
        string stockfish = cfg.GetValueOrDefault("LAPLACE_STOCKFISH") ?? Path.Combine(source, "src", "stockfish" + suffix);
        string ccDefault = cfg.GetValueOrDefault("LAPLACE_CUTECHESS_BUILD") is { } build ? Path.Combine(build, "cutechess-cli" + suffix)
            : windows ? Path.Combine(Environment.GetEnvironmentVariable("LAPLACE_BUILD_ROOT") ?? @"D:\Data\Laplace", "build-cutechess", "cutechess-cli.exe")
            : Path.Combine(prefix, "bin", "cutechess-cli");
        string cutechess = cfg.GetValueOrDefault("LAPLACE_CUTECHESS") ?? ccDefault;
        string uci = cmd.Value("uci") ?? Path.Combine(prefix, "app", "laplace-uci" + suffix);
        var report = new List<CheckItem>();

        string version = LabLocks.Stockfish["version"]!.GetValue<string>();
        Check(report, "stockfish", () => new JsonObject
        {
            ["path"] = stockfish, ["version"] = version, ["search"] = ProbeStockfish(probes, stockfish, version),
        });
        Check(report, "cutechess", () => CutechessProbe.Probe(probes.Runner, cutechess, LabLocks.Cutechess));
        Check(report, "laplace-uci", () => new JsonObject { ["path"] = uci, ["bestmove"] = CheckUciRuntime(probes, uci) });
        Check(report, "zstandard-pgn-codec", () => probes.Zstd(cfg.GetValueOrDefault("LAPLACE_ZSTD_LIBRARY"),
            int.Parse(cfg.GetValueOrDefault("LAPLACE_ZSTD_WINDOW_LOG_MAX", "27"), System.Globalization.CultureInfo.InvariantCulture),
            LabLocks.Zstd["version"]!.GetValue<string>()));

        // the ladder: the conductor, the rating tool, Lc0 and the opening suites (deploy/chess-ladder-release.json)
        bool requireLadder = cmd.Flag("require-ladder");
        string dataRoot = ChessLabConfig.DataRoot(cfg);
        foreach (var (name, key, action) in new (string, string, Func<JsonNode>)[]
                 {
                     ("fastchess", "LAPLACE_FASTCHESS", () => Fastchess(probes.Runner, Required(cfg, "LAPLACE_FASTCHESS"))),
                     ("ordo", "LAPLACE_ORDO", () => Ordo(probes.Runner, Required(cfg, "LAPLACE_ORDO"))),
                     ("lc0", "LAPLACE_LC0", () => Lc0(Required(cfg, "LAPLACE_LC0"), cfg.GetValueOrDefault("LAPLACE_LC0_NET"), cfg.GetValueOrDefault("LAPLACE_LC0_BACKEND"))),
                     ("opening-suites", "LAPLACE_CHESS_BOOKS", () => Books(cfg.GetValueOrDefault("LAPLACE_CHESS_BOOKS")
                         ?? Path.Combine(dataRoot, LabLocks.Ladder["books"]!["directory"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar)))),
                 })
        {
            if (cfg.ContainsKey(key) || requireLadder || name == "opening-suites") Check(report, name, action, requireLadder);
            else report.Add(new CheckItem(name, "not-configured", false, $"set {key} in chess-lab.env (deploy/windows/chess-lab.env.example)"));
        }

        bool guiConfigured = cfg.ContainsKey("LAPLACE_CUTECHESS_GUI") || cfg.ContainsKey("LAPLACE_CUTECHESS_GUI_RECEIPT");
        if (cmd.Flag("cutechess-gui") || guiConfigured)
        {
            string gui = cfg.GetValueOrDefault("LAPLACE_CUTECHESS_GUI") ?? Path.Combine(prefix, "bin", "cutechess" + suffix);
            string receipt = cfg.GetValueOrDefault("LAPLACE_CUTECHESS_GUI_RECEIPT")
                ?? Path.Combine(cfg.GetValueOrDefault("LAPLACE_CUTECHESS_BUILD", "/build/cutechess"), "laplace-cutechess-gui-build.json");
            Check(report, "cutechess-gui", () => probes.CutechessGui(probes.Runner, gui, receipt, LabLocks.Cutechess));
        }
        else
            report.Add(new CheckItem("cutechess-gui", "not-configured", false,
                "Linux host setup and publish provision the official GUI; --cutechess-gui requires its retained build and offscreen runtime proof."));

        var data = DataInventory(cfg, dataRoot);
        for (int i = 0; i < 2; i++) data[i] = data[i] with { Required = cmd.Flag("require-data") };
        report.AddRange(data);

        if (cmd.Flag("check-latest"))
        {
            Check(report, "install-stockfish-latest", () => StockfishLatest(probes));
            Check(report, "provision-cutechess-latest", () => CutechessLatest(probes));
            Check(report, "install-zstd-latest", () => ZstdLatest(probes));
        }

        bool failed = report.Any(static i => i.Required && i.Status is not ("ready" or "present"));
        var values = new JsonObject();
        foreach (var (k, v) in cfg.OrderBy(static p => p.Key, StringComparer.Ordinal)) values[k] = v;
        var json = new JsonObject
        {
            ["executable_ready"] = report.Where(static i => i.Required && ExecutableChecks.Contains(i.Name)).All(static i => i.Status == "ready"),
            ["configured_settings"] = new JsonObject
            {
                ["scope"] = "Selected environment and installed configuration files; live process settings require a separate runtime observation.",
                ["values"] = values,
            },
            ["checks"] = new JsonArray(report.Select(static i => (JsonNode?)i.ToJson()).ToArray()),
        };
        return (json, failed ? 1 : 0);
    }

    public static void Check(List<CheckItem> report, string name, Func<JsonNode> action, bool required = true)
    {
        try { report.Add(new CheckItem(name, "ready", required, action())); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            report.Add(new CheckItem(name, "failed", required, ex.Message));
        }
    }

    private static string Required(IReadOnlyDictionary<string, string> cfg, string key)
        => cfg.GetValueOrDefault(key) ?? throw new KeyNotFoundException($"'{key}'");

    private static readonly HashSet<string> StartLegal = BuildStartLegal();

    private static HashSet<string> BuildStartLegal()
    {
        var legal = new HashSet<string>(StringComparer.Ordinal) { "b1a3", "b1c3", "g1f3", "g1h3" };
        foreach (char f in "abcdefgh") { legal.Add($"{f}2{f}3"); legal.Add($"{f}2{f}4"); }
        return legal;
    }

    /// <summary>Readiness and a legal search, including usable embedded NNUE data: the handshake names the release,
    /// the strength options exist, and a depth-1 search from the start position plays a legal move.</summary>
    public static string ProbeStockfish(CheckProbes probes, string binary, string version)
    {
        using var engine = probes.StartEngine(new UciProcessStart(binary));
        if (!engine.HandshakeLines.Contains("id name Stockfish " + version))
            throw new InvalidOperationException("Stockfish version/UCI handshake did not match the release lock");
        foreach (var option in new[] { "Threads", "Hash", "UCI_LimitStrength", "UCI_Elo" })
            if (!engine.HandshakeLines.Any(l => l.StartsWith("option name " + option + " type ", StringComparison.Ordinal)))
                throw new InvalidOperationException($"Stockfish required UCI option is absent: {option}");
        engine.SetOption("Threads", "1");
        engine.SetOption("Hash", "16");
        engine.IsReady(TimeSpan.FromSeconds(30));
        engine.NewGame(TimeSpan.FromSeconds(30));
        var result = engine.Search(ChessPosition.Start, new UciLimits(Depth: 1), TimeSpan.FromSeconds(30), "fresh");
        if (result.BestMove.Move?.Text is not { } move || !StartLegal.Contains(move))
            throw new InvalidOperationException("Stockfish did not return a legal move from the initial position");
        if (engine.Quit(TimeSpan.FromSeconds(10)) is not 0) throw new InvalidOperationException("Stockfish exited unsuccessfully after quit");
        return engine.HandshakeLines.First(static l => l.StartsWith("option name UCI_Elo ", StringComparison.Ordinal));
    }

    /// <summary>A published laplace-uci answers uciok and readyok, completes a depth-1 search with its info line, plays a
    /// legal starting move, and exits 0 on quit. Pure search: the substrate is off and logs go to a temporary directory.</summary>
    public static string CheckUciRuntime(CheckProbes probes, string executable)
    {
        string logs = Path.Combine(Path.GetTempPath(), "laplace-uci-check-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(logs);
        try
        {
            var env = new Dictionary<string, string?> { ["LAPLACE_UCI_SUBSTRATE"] = "off", ["LAPLACE_OPS_LOG_DIR"] = logs };
            using var engine = probes.StartEngine(new UciProcessStart(Path.GetFullPath(executable), Environment: env, HandshakeTimeout: TimeSpan.FromSeconds(15)));
            engine.IsReady(TimeSpan.FromSeconds(15));
            bool depthOne = false;
            var result = engine.Search(ChessPosition.Start, new UciLimits(Depth: 1), TimeSpan.FromSeconds(15), "warm",
                onInfo: info => depthOne |= info.Depth == 1);
            if (result.BestMove.Move?.Text is not { } move || !StartLegal.Contains(move))
                throw new InvalidOperationException("UCI returned no legal starting-position move");
            if (!depthOne) throw new InvalidOperationException("UCI did not complete the requested search depth");
            if (engine.Quit(TimeSpan.FromSeconds(15)) is not 0) throw new InvalidOperationException("UCI exited unsuccessfully after quit");
            return move;
        }
        finally
        {
            try { Directory.Delete(logs, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>An executable the ladder uses: present, one of the lock's pinned binaries by SHA-256.</summary>
    public static JsonObject PinnedBinary(string name, string binary, JsonNode lock_)
    {
        if (!File.Exists(binary)) throw new FileNotFoundException($"{name} not found at {binary}");
        string digest = LabFiles.Sha256(binary);
        var pinned = lock_["assets"]!.AsObject().FirstOrDefault(a => a.Value?["binary_sha256"]?.GetValue<string>() == digest);
        if (pinned.Key is null)
            throw new InvalidOperationException($"{name} at {binary} has SHA-256 {digest}, not a binary pinned in deploy/chess-ladder-release.json");
        return new JsonObject { ["path"] = binary, ["sha256"] = digest, ["asset"] = pinned.Key, ["version"] = lock_["version"]!.GetValue<string>() };
    }

    private static string Says(IToolRunner runner, string binary, string argument)
        => runner.Run(new ToolRun(binary, [argument], Timeout: TimeSpan.FromSeconds(60))).Output.Trim();

    public static JsonObject Fastchess(IToolRunner runner, string binary)
    {
        var lock_ = LabLocks.Ladder["fastchess"]!;
        var detail = PinnedBinary("fastchess", binary, lock_);
        string line = Says(runner, binary, "-version");
        string versionLine = lock_["version_line"]!.GetValue<string>(), commit = lock_["commit"]!.GetValue<string>()[..7];
        if (!line.Contains(versionLine, StringComparison.Ordinal) || !line.Contains(commit, StringComparison.Ordinal))
            throw new InvalidOperationException($"fastchess says '{line}', the lock is {versionLine} at {commit}");
        detail["says"] = line.Split('\n')[^1].Trim();
        return detail;
    }

    public static JsonObject Ordo(IToolRunner runner, string binary)
    {
        var lock_ = LabLocks.Ladder["ordo"]!;
        var detail = OperatingSystem.IsWindows() ? PinnedBinary("ordo", binary, lock_) : new JsonObject { ["path"] = binary };
        string line = Says(runner, binary, "-v");
        if (!line.Contains(lock_["version_line"]!.GetValue<string>(), StringComparison.Ordinal))
            throw new InvalidOperationException($"ordo says '{line}', the lock is {lock_["version_line"]}");
        detail["says"] = line.Split('\n')[0].Trim();
        return detail;
    }

    /// <summary>Lc0's binary and network by SHA-256. The engine is not started: it needs the GPU, which HART-DESKTOP lends
    /// it only in chess-GPU mode (Laplace-Operations chess/chess-gpu.ps1).</summary>
    public static JsonObject Lc0(string binary, string? network, string? backend)
    {
        var lock_ = LabLocks.Ladder["lc0"]!;
        var detail = OperatingSystem.IsWindows() ? PinnedBinary("lc0", binary, lock_) : new JsonObject { ["path"] = binary };
        if (network is null || !File.Exists(network))
            throw new InvalidOperationException($"LAPLACE_LC0_NET does not name a network file ({network ?? "None"})");
        string digest = LabFiles.Sha256(network);
        var pinned = lock_["networks"]![Path.GetFileName(network)];
        if (pinned is null || pinned["sha256_tofu"]?.GetValue<string>() != digest)
            throw new InvalidOperationException($"network {Path.GetFileName(network)} has SHA-256 {digest}, not the one deploy/chess-ladder-release.json pins");
        detail["network"] = network;
        detail["network_sha256"] = digest;
        detail["backend"] = backend ?? "lc0 default (cuda-auto)";
        return detail;
    }

    public static JsonObject Books(string directory)
    {
        var lock_ = LabLocks.Ladder["books"]!;
        var files = new JsonObject();
        foreach (var (name, digest) in lock_["files"]!.AsObject())
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path)) throw new FileNotFoundException($"opening suite {path} is missing");
            string actual = LabFiles.Sha256(path);
            if (actual != digest!.GetValue<string>()) throw new InvalidOperationException($"opening suite {path} has SHA-256 {actual}, the lock says {digest}");
            files[name] = actual;
        }
        return new JsonObject { ["path"] = directory, ["commit"] = lock_["commit"]!.GetValue<string>(), ["files"] = files };
    }

    /// <summary>Syzygy (every configured root present, every table nonempty and paired WDL/DTZ), the opening TSVs, and a
    /// pointer to the Lichess status endpoint. Pairing finds missing companions; it is not a checksum or a roster proof.</summary>
    public static List<CheckItem> DataInventory(IReadOnlyDictionary<string, string> cfg, string dataRoot)
    {
        string chess = Path.Combine(dataRoot, "Games", "Chess");
        var roots = cfg.GetValueOrDefault("LAPLACE_SYZYGY") is { } configured
            ? configured.Split(Path.PathSeparator).Select(static p => p.Trim()).Where(static p => p.Length > 0).Select(Path.GetFullPath).Distinct().ToList()
            : [Path.Combine(chess, "syzygy")];
        var missingRoots = roots.Where(static r => !Directory.Exists(r)).ToList();
        var files = roots.Where(Directory.Exists).SelectMany(static r => Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories))
            .Where(static f => Path.GetExtension(f).ToLowerInvariant() is ".rtbw" or ".rtbz").Distinct().ToList();
        var empty = files.Where(static f => new FileInfo(f).Length == 0).Order(StringComparer.Ordinal).ToList();
        var wdl = files.Where(static f => Path.GetExtension(f).Equals(".rtbw", StringComparison.OrdinalIgnoreCase) && new FileInfo(f).Length > 0)
            .Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
        var dtz = files.Where(static f => Path.GetExtension(f).Equals(".rtbz", StringComparison.OrdinalIgnoreCase) && new FileInfo(f).Length > 0)
            .Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
        var tables = new JsonObject
        {
            ["paths"] = Json.Array(roots), ["wdl"] = wdl.Count, ["dtz"] = dtz.Count, ["missing_roots"] = Json.Array(missingRoots),
            ["empty_files"] = Json.Array(empty),
            ["native_path"] = string.Join(Path.PathSeparator, files.Select(static f => Path.GetDirectoryName(f)!).Distinct().Order(StringComparer.Ordinal)),
            ["missing_dtz"] = Json.Array(wdl.Except(dtz).Order(StringComparer.Ordinal)!), ["missing_wdl"] = Json.Array(dtz.Except(wdl).Order(StringComparer.Ordinal)!),
            ["verification"] = "nonempty files and matching material names; checksums not verified",
        };
        bool syzygyPresent = roots.Count > 0 && missingRoots.Count == 0 && empty.Count == 0 && wdl.Count > 0 && wdl.SetEquals(dtz);
        var candidates = cfg.GetValueOrDefault("LAPLACE_CHESS_OPENINGS") is { } o ? [o]
            : new List<string> { Path.Combine(chess, "lichess-openings"), Path.Combine(chess, "openings") };
        string openingRoot = candidates.FirstOrDefault(static r => "abcde".All(l => File.Exists(Path.Combine(r, l + ".tsv")))) ?? candidates[0];
        var openingFiles = "abcde".Select(static l => l + ".tsv").Where(f => File.Exists(Path.Combine(openingRoot, f))).ToList();
        return
        [
            new CheckItem("syzygy", syzygyPresent ? "present" : "incomplete", false, tables),
            new CheckItem("openings", openingFiles.Count == 5 ? "present" : "incomplete", false,
                new JsonObject { ["path"] = openingRoot, ["files"] = Json.Array(openingFiles) }),
            new CheckItem("lichess", "check-service", false,
                "GET /chess/lichess/status reports verified BOT account and bot:play access; no separate lichess-bot or python-chess installation is used."),
        ];
    }

    public static JsonObject StockfishLatest(CheckProbes probes)
    {
        var lock_ = LabLocks.Stockfish;
        string tag = lock_["tag"]!.GetValue<string>();
        var upstream = probes.GitHubJson("https://api.github.com/repos/official-stockfish/Stockfish/releases/latest");
        string? latest = upstream["tag_name"]?.GetValue<string>();
        if (upstream["draft"]?.GetValue<bool>() == true || upstream["prerelease"]?.GetValue<bool>() == true || latest != tag)
            throw new InvalidOperationException($"Stockfish source pin is stale: pinned {tag}, upstream {latest}. Update deploy/linux/stockfish-release.json "
                + "to the official stable tag and commit, then rebuild the external checkout.");
        string commit = probes.GitHubJson("https://api.github.com/repos/official-stockfish/Stockfish/commits/" + tag)["sha"]!.GetValue<string>();
        if (commit != lock_["commit"]!.GetValue<string>()) throw new InvalidOperationException("Stockfish official release commit differs from the source pin");
        return new JsonObject { ["component"] = "stockfish", ["version"] = lock_["version"]!.GetValue<string>(), ["tag"] = tag, ["commit"] = commit, ["current"] = true };
    }

    public static JsonObject CutechessLatest(CheckProbes probes)
    {
        var lock_ = LabLocks.Cutechess;
        var release = probes.GitHubJson(lock_["latest_api"]!.GetValue<string>());
        string tag = lock_["tag"]!.GetValue<string>();
        string? latest = release["tag_name"]?.GetValue<string>();
        if (release["draft"]?.GetValue<bool>() == true || release["prerelease"]?.GetValue<bool>() == true || latest != tag)
            throw new InvalidOperationException($"CuteChess release lock {tag} differs from upstream {latest ?? "unknown"}; update the verified release lock");
        return new JsonObject { ["component"] = "cutechess", ["locked"] = tag, ["latest"] = latest, ["current"] = true };
    }

    public static JsonObject ZstdLatest(CheckProbes probes)
    {
        var lock_ = LabLocks.Zstd;
        string tag = lock_["tag"]!.GetValue<string>();
        var release = probes.GitHubJson(lock_["latest_api"]!.GetValue<string>());
        string? latest = release["tag_name"]?.GetValue<string>();
        if (release["draft"]?.GetValue<bool>() == true || release["prerelease"]?.GetValue<bool>() == true || latest != tag)
            throw new InvalidOperationException($"Zstandard lock {tag} is stale; official stable release is {latest}");
        string commit = probes.GitHubJson("https://api.github.com/repos/facebook/zstd/commits/" + tag)["sha"]!.GetValue<string>();
        if (commit != lock_["commit"]!.GetValue<string>()) throw new InvalidOperationException("Zstandard official stable tag changed from the locked commit");
        return new JsonObject { ["component"] = "zstd", ["version"] = lock_["version"]!.GetValue<string>(), ["tag"] = tag, ["commit"] = commit, ["current"] = true };
    }
}
