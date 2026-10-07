using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using Laplace.Chess.Service.Uci;
using Laplace.Chess.Uci.Commands;
using Laplace.Chess.Uci.Lab;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Chess.Uci.Tests;

/// <summary>A tool runner that answers from a script and records every run; a transcript run gets its file written.</summary>
internal sealed class FakeRunner(Func<ToolRun, ToolResult> answer) : IToolRunner
{
    public readonly List<ToolRun> Runs = [];

    public ToolResult Run(ToolRun run)
    {
        lock (Runs) Runs.Add(run);
        var result = answer(run);
        if (run.TranscriptFile is not null) File.WriteAllText(run.TranscriptFile, result.Stdout);
        return result;
    }
}

/// <summary>A scripted UCI engine on anonymous pipes, attached through the generic client.</summary>
internal sealed class ScriptedEngine : IDisposable
{
    private readonly AnonymousPipeServerStream _toEngine = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream _fromEngine = new(PipeDirection.In);
    private readonly StreamWriter _engineOut;
    public UciProcess Client { get; }

    public ScriptedEngine(Func<string, IEnumerable<string>?> script)
    {
        var engineIn = new StreamReader(new AnonymousPipeClientStream(PipeDirection.In, _toEngine.ClientSafePipeHandle));
        _engineOut = new StreamWriter(new AnonymousPipeClientStream(PipeDirection.Out, _fromEngine.ClientSafePipeHandle)) { AutoFlush = true };
        _ = Task.Run(() =>
        {
            try
            {
                while (engineIn.ReadLine() is { } line)
                {
                    var answer = script(line);
                    if (answer is null) { _engineOut.Dispose(); return; }
                    foreach (var a in answer) _engineOut.WriteLine(a);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        });
        Client = UciProcess.Attach(new StreamWriter(_toEngine) { AutoFlush = true }, new StreamReader(_fromEngine), TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        Client.Dispose();
        try { _engineOut.Dispose(); } catch { }
        _fromEngine.Dispose();
    }
}

/// <summary>laplace-uci's lab commands: parsing, the configuration they read, check, match, the ladder (against fake tools),
/// review, lichess settings and inspect; real binaries where installed.</summary>
public sealed class LabCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("laplace-uci-lab-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string File_(string name, string text = "x")
    {
        string path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    // ---- parsing ----

    [Fact]
    public void Parse_TakesPositionalsFlagsAndRepeatedOptions()
    {
        var cmd = ChessCommandLine.Parse(["ladder", "rate", "RUN", "--anchor", "SF19-n1024", "--json"]);
        Assert.Equal(["rate", "RUN"], cmd.Positionals);
        Assert.Equal("SF19-n1024", cmd.Value("anchor"));
        Assert.True(cmd.Flag("json"));

        var lichess = ChessCommandLine.Parse(["lichess", "--speed", "blitz", "--speed", "rapid", "--rated"]);
        Assert.Equal(["blitz", "rapid"], lichess.Repeated["speed"]);
        Assert.True(lichess.Flag("rated"));

        Assert.Throws<ArgumentException>(() => ChessCommandLine.Parse(["analyse", "stray"]));
        Assert.Throws<ArgumentException>(() => ChessCommandLine.Parse(["match", "--engine1"]));
        Assert.Throws<ArgumentException>(() => ChessCommandLine.Parse(["nonsense"]));
        Assert.Equal("uci", ChessCommandLine.Parse([]).Verb);
    }

    // ---- configuration (the Python checker's rules) ----

    [Fact]
    public void Configuration_NeverCollectsCredentials_AndFirstFileWins()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "app"));
        Directory.CreateDirectory(Path.Combine(_dir, "secrets"));
        File.WriteAllText(Path.Combine(_dir, "app", "laplace-api.env"),
            "LICHESS_API=secret\nLAPLACE_STOCKFISH_SOURCE=/old\nLAPLACE_STOCKFISH_SOURCE=\"/selected/source\"\n"
            + "LAPLACE_STOCKFISH=/installed/binary\nLAPLACE_STOCKFISH_EVAL_THREADS=4\nLAPLACE_CUTECHESS_GUI=/selected/cutechess\n");
        File.WriteAllText(Path.Combine(_dir, "secrets", "chess-lab.env"),
            "LAPLACE_STOCKFISH_SOURCE=/stale-secret\nLAPLACE_STOCKFISH_EVAL_HASH_MB=64\n# LAPLACE_ORDO=/commented\n");
        var config = ChessLabConfig.Read(_dir, includeEnvironment: false, repoRoot: _dir);
        Assert.Equal("/selected/source", config["LAPLACE_STOCKFISH_SOURCE"]);
        Assert.Equal("/installed/binary", config["LAPLACE_STOCKFISH"]);
        Assert.Equal("4", config["LAPLACE_STOCKFISH_EVAL_THREADS"]);
        Assert.Equal("64", config["LAPLACE_STOCKFISH_EVAL_HASH_MB"]);
        Assert.Equal("/selected/cutechess", config["LAPLACE_CUTECHESS_GUI"]);
        Assert.DoesNotContain("LICHESS_API", config.Keys);
        Assert.DoesNotContain("LAPLACE_ORDO", config.Keys);

        var supplied = ChessLabConfig.Read(_dir, keys: new HashSet<string> { "LAPLACE_STOCKFISH_EVAL_PROCESSES" },
            apiEnvironment: "LAPLACE_STOCKFISH_EVAL_PROCESSES=3\nLAPLACE_STOCKFISH=/x\n", includeEnvironment: false, repoRoot: _dir);
        Assert.Equal(new Dictionary<string, string> { ["LAPLACE_STOCKFISH_EVAL_PROCESSES"] = "3" }, supplied);
    }

    // ---- check ----

    [Fact]
    public void DataInventory_ShowsMissingCompanionsEmptyFilesAndOpenings()
    {
        string tables = Path.Combine(_dir, "Games", "Chess", "syzygy", "3-4-5");
        Directory.CreateDirectory(tables);
        File.WriteAllText(Path.Combine(tables, "KQvK.rtbw"), "fixture");
        var report = CheckCommand.DataInventory(new Dictionary<string, string>(), _dir);
        Assert.Equal("incomplete", report[0].Status);
        Assert.Equal("KQvK", report[0].Detail!["missing_dtz"]![0]!.GetValue<string>());
        File.WriteAllText(Path.Combine(tables, "KQvK.rtbz"), "fixture");
        report = CheckCommand.DataInventory(new Dictionary<string, string>(), _dir);
        Assert.Equal("present", report[0].Status);
        Assert.Contains("checksums not verified", report[0].Detail!["verification"]!.GetValue<string>());
        Assert.Equal("incomplete", report[1].Status);

        string openings = Path.Combine(_dir, "Games", "Chess", "lichess-openings");
        Directory.CreateDirectory(openings);
        foreach (var l in "abcde") File.WriteAllText(Path.Combine(openings, l + ".tsv"), "eco\tname\tpgn\n");
        Assert.Equal("present", CheckCommand.DataInventory(new Dictionary<string, string>(), _dir)[1].Status);
        Assert.Equal("incomplete", CheckCommand.DataInventory(
            new Dictionary<string, string> { ["LAPLACE_CHESS_OPENINGS"] = Path.Combine(_dir, "missing") }, _dir)[1].Status);

        File.WriteAllText(Path.Combine(tables, "KQvK.rtbz"), "");
        var empty = CheckCommand.DataInventory(new Dictionary<string, string> { ["LAPLACE_SYZYGY"] = tables + Path.PathSeparator + Path.Combine(_dir, "nope") }, _dir)[0];
        Assert.Equal("incomplete", empty.Status);
        Assert.Single(empty.Detail!["missing_roots"]!.AsArray());
        Assert.Single(empty.Detail!["empty_files"]!.AsArray());
    }

    [Fact]
    public void Check_FailedProbeIsARequiredFailure_AndTheGuiFlagRequiresTheVerifiedBuild()
    {
        var report = new List<CheckItem>();
        CheckCommand.Check(report, "stockfish", () => throw new FileNotFoundException("missing executable"));
        Assert.Equal("failed", report[0].Status);
        Assert.True(report[0].Required);

        foreach (bool succeeds in new[] { true, false })
        {
            var probes = new CheckProbes
            {
                Runner = new FakeRunner(static _ => new ToolResult(1, "", "absent")),
                StartEngine = static _ => throw new UciEngineException(UciFailure.Unavailable, "not here"),
                Zstd = static (_, _, _) => new JsonObject { ["version"] = "1.5.7" },
                CutechessGui = (_, binary, receipt, _) => succeeds
                    ? new JsonObject { ["runtime"] = new JsonObject { ["status"] = "ready-headless" }, ["binary"] = binary, ["receipt"] = receipt }
                    : throw new FileNotFoundException("no retained official GUI build"),
            };
            var cmd = ChessCommandLine.Parse(["check", "--prefix", _dir, "--cutechess-gui"]);
            var (json, exit) = CheckCommand.Run(cmd, probes, new Dictionary<string, string>());
            var gui = json["checks"]!.AsArray().First(c => c!["name"]!.GetValue<string>() == "cutechess-gui")!;
            Assert.True(gui["required"]!.GetValue<bool>());
            Assert.Equal(succeeds ? "ready" : "failed", gui["status"]!.GetValue<string>());
            Assert.Equal(1, exit); // stockfish, cutechess and laplace-uci are absent here
            Assert.False(json["executable_ready"]!.GetValue<bool>());
            var ladder = json["checks"]!.AsArray().First(c => c!["name"]!.GetValue<string>() == "fastchess")!;
            Assert.Equal("not-configured", ladder["status"]!.GetValue<string>());
        }
    }

    [Fact]
    public void ProbeStockfish_RequiresTheLockedNameTheStrengthOptionsAndALegalMove()
    {
        IEnumerable<string>? Script(string line, string name, string move) => line switch
        {
            "uci" => [$"id name {name}", "option name Threads type spin default 1 min 1 max 512", "option name Hash type spin default 16 min 1 max 1024",
                      "option name UCI_LimitStrength type check default false", "option name UCI_Elo type spin default 1320 min 1320 max 3190", "uciok"],
            "isready" => ["readyok"],
            "quit" => null,
            _ when line.StartsWith("go", StringComparison.Ordinal) => ["info depth 1 score cp 20 nodes 20 pv " + move, "bestmove " + move],
            _ => [],
        };
        using (var good = new ScriptedEngine(l => Script(l, "Stockfish 19", "e2e4")))
            Assert.StartsWith("option name UCI_Elo ", CheckCommand.ProbeStockfish(new CheckProbes { StartEngine = _ => good.Client }, "sf", "19"));
        using (var wrong = new ScriptedEngine(l => Script(l, "Stockfish 18", "e2e4")))
            Assert.Throws<InvalidOperationException>(() => CheckCommand.ProbeStockfish(new CheckProbes { StartEngine = _ => wrong.Client }, "sf", "19"));
        using (var illegal = new ScriptedEngine(l => Script(l, "Stockfish 19", "e2e5")))
            Assert.Throws<InvalidOperationException>(() => CheckCommand.ProbeStockfish(new CheckProbes { StartEngine = _ => illegal.Client }, "sf", "19"));
        using (var uci = new ScriptedEngine(l => Script(l, "Laplace", "g1f3")))
            Assert.Equal("g1f3", CheckCommand.CheckUciRuntime(new CheckProbes { StartEngine = _ => uci.Client }, "laplace-uci"));
    }

    [Fact]
    public void Cutechess_VersionAndQtAreChecked()
    {
        var lock_ = LabLocks.Cutechess;
        string version = lock_["version"]!.GetValue<string>(), qt = lock_["qt_version"]!.GetValue<string>();
        var ok = new FakeRunner(_ => new ToolResult(0, $"cutechess-cli {version}\r\nUsing Qt version {qt}\r\n", ""));
        Assert.Equal(qt, CutechessProbe.Probe(ok, "cutechess-cli", lock_)["qt_version"]!.GetValue<string>());
        var old = new FakeRunner(_ => new ToolResult(0, $"cutechess-cli {version}\nUsing Qt version 6.5.0\n", ""));
        Assert.Throws<InvalidOperationException>(() => CutechessProbe.Probe(old, "cutechess-cli", lock_));
    }

    [Fact]
    public void PinnedTools_AreRecognisedOnlyByTheirLockedBytes()
    {
        string tool = File_("fastchess.exe", "not the pinned bytes");
        var ex = Assert.Throws<InvalidOperationException>(() => CheckCommand.PinnedBinary("fastchess", tool, LabLocks.Ladder["fastchess"]!));
        Assert.Contains("not a binary pinned", ex.Message);
        Directory.CreateDirectory(Path.Combine(_dir, "books"));
        Assert.Throws<FileNotFoundException>(() => CheckCommand.Books(Path.Combine(_dir, "books")));
        Assert.Throws<InvalidOperationException>(() => CheckCommand.Lc0(tool, File_("net.pb.gz"), null));
    }

    [Fact]
    public void Latest_ComparesTheLockWithTheOfficialRelease()
    {
        var lock_ = LabLocks.Stockfish;
        var current = new CheckProbes
        {
            GitHubJson = url => url.EndsWith("/latest", StringComparison.Ordinal)
                ? new JsonObject { ["tag_name"] = lock_["tag"]!.GetValue<string>(), ["draft"] = false, ["prerelease"] = false }
                : new JsonObject { ["sha"] = lock_["commit"]!.GetValue<string>() },
        };
        Assert.True(CheckCommand.StockfishLatest(current)["current"]!.GetValue<bool>());
        var stale = new CheckProbes { GitHubJson = _ => new JsonObject { ["tag_name"] = "sf_99" } };
        Assert.Throws<InvalidOperationException>(() => CheckCommand.StockfishLatest(stale));
        Assert.Throws<InvalidOperationException>(() => CheckCommand.CutechessLatest(stale));
    }

    [SkippableFact]
    public void Zstd_NativeStreamingAbiDecodesTheFixture()
    {
        string? library = new[] { Environment.GetEnvironmentVariable("LAPLACE_ZSTD_LIBRARY"), @"D:\Libraries\zstd\1.5.7\bin\zstd.dll", "/usr/lib/x86_64-linux-gnu/libzstd.so.1" }
            .FirstOrDefault(static p => !string.IsNullOrEmpty(p) && File.Exists(p));
        Skip.If(library is null, "no native Zstandard library here");
        var result = ZstdProbe.Probe(library, 27);
        Assert.Equal(64, result["fixture_sha256"]!.GetValue<string>().Length);
        Assert.Equal(151, result["decoded_bytes"]!.GetValue<int>());
        Assert.Throws<ArgumentException>(() => ZstdProbe.Probe(library, 9));
    }

    [SkippableFact]
    public void SourceIntegrity_ReadsTrackedBytesAgainstTheCommit()
    {
        Skip.If(LabFiles.Which("git") is null, "git is not on the PATH");
        string repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(repo);
        void Git(params string[] args)
        {
            var psi = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = repo };
            foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false" }.Concat(args)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
        }
        Git("init", "-q");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "alpha\n");
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        File.WriteAllText(Path.Combine(repo, "src", "b.c"), "int b;\n");
        Git("add", ".");
        Git("commit", "-q", "-m", "fixture");
        string head = SourceIntegrity.GitText(repo, "rev-parse", "HEAD");
        var ok = SourceIntegrity.VerifyCheckout(repo, head, "Fixture");
        Assert.Equal(2, ok["tracked_files"]!.GetValue<int>());
        Assert.Equal(13, ok["tracked_blob_bytes"]!.GetValue<long>());
        File.WriteAllText(Path.Combine(repo, "a.txt"), "alphA\n");
        var ex = Assert.Throws<InvalidOperationException>(() => SourceIntegrity.VerifyCheckout(repo, head, "Fixture"));
        Assert.Contains("local byte changes", ex.Message);
    }

    // ---- the conductor, match and ladder against fake tools ----

    private (Dictionary<string, string> Config, FakeRunner Runner) FakeLab()
    {
        string books = Path.Combine(_dir, "books");
        File_("books/8moves_v3.pgn", "[Event \"?\"]\n\n1. e4 e5 *\n");
        var config = new Dictionary<string, string>
        {
            ["LAPLACE_FASTCHESS"] = File_("tools/fastchess.exe", "fastchess"),
            ["LAPLACE_ORDO"] = File_("tools/ordo.exe", "ordo"),
            ["LAPLACE_STOCKFISH"] = File_("tools/stockfish.exe", "stockfish"),
            ["LAPLACE_CHESS_BOOKS"] = books,
        };
        var runner = new FakeRunner(run =>
        {
            string name = Path.GetFileNameWithoutExtension(run.Exe);
            if (name == "fastchess")
            {
                string pgn = run.Args.First(static a => a.StartsWith("file=", StringComparison.Ordinal) && a.EndsWith(".pgn", StringComparison.Ordinal))[5..];
                if (run.Args.Contains("-pgnout")) pgn = run.Args[run.Args.ToList().IndexOf("-pgnout") + 1][5..];
                File.WriteAllText(pgn, "[Event \"x\"]\n[White \"A\"]\n[Black \"B\"]\n[Result \"1-0\"]\n\n1. e4 1-0\n");
                return new ToolResult(0, "Results of A vs B (inf, NULL, NULL, 8moves_v3.pgn):\nElo: 12.00 +/- 3.00, nElo: 15.00 +/- 4.00\nLOS: 99.00 %, DrawRatio: 10.00 %, PairsRatio: 2.00\nGames: 2, Wins: 1, Losses: 1, Draws: 0, Points: 1.0 (50.00 %)\nPtnml(0-2): [0, 0, 1, 0, 0]\nnoise\n", "");
            }
            if (name == "ordo")
            {
                File.WriteAllText(run.Args[run.Args.ToList().IndexOf("-o") + 1], "   # PLAYER  : RATING\n   1 SF19-n512 : 10.0\n");
                return new ToolResult(0, "", "");
            }
            if (name == "stockfish")
                return run.Args[0] == "bench" ? new ToolResult(0, "", "Nodes searched  : 2497913\n") : new ToolResult(0, "Compiled by : fake\n", "");
            if (name == "wevtutil") return new ToolResult(0, "<Event xmlns='x'></Event><Event xmlns='x'></Event>", "");
            return new ToolResult(1, "", "unexpected tool " + run.Exe);
        });
        return (config, runner);
    }

    [Fact]
    public void Calibrate_PlaysEachRungAgainstTheNext_RatesWithOrdo_AndWritesTheReceipt()
    {
        var (config, runner) = FakeLab();
        var cmd = ChessCommandLine.Parse(["ladder", "calibrate", "--rungs", "sf:nodes=256,sf:nodes=512,sf:nodes=1024", "--games", "4",
            "--concurrency", "2", "--affinity", "16-31", "--out", Path.Combine(_dir, "runs")]);
        var progress = new List<string>();
        var result = LadderCommand.Run(cmd, runner, progress.Add, config);
        Assert.False(result.Failed);
        Assert.Contains("SF19-n512", result.Table);
        Assert.Equal(2, progress.Count);
        var fast = runner.Runs.Where(static r => Path.GetFileNameWithoutExtension(r.Exe) == "fastchess").ToList();
        Assert.Equal(2, fast.Count);
        var argv = fast[0].Args.ToList();
        Assert.Contains("name=SF19-n256", argv);
        Assert.Contains("option.Threads=1", argv);
        Assert.Contains("tc=inf", argv);
        Assert.Equal("2", argv[argv.IndexOf("-rounds") + 1]);
        Assert.Equal("16-31", argv[argv.IndexOf("-use-affinity") + 1]);
        Assert.Equal("200", argv[argv.IndexOf("-maxmoves") + 1]);
        Assert.Equal("20261006", argv[argv.IndexOf("-srand") + 1]);

        var receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(result.Run!, "receipt.json")))!;
        Assert.Equal("calibrate", receipt["kind"]!.GetValue<string>());
        Assert.Equal(3, receipt["rungs"]!.AsArray().Count);
        Assert.Equal(2497913, receipt["stockfish"]!["bench_nodes"]!.GetValue<long>());
        Assert.Equal(2, receipt["matches"]!.AsArray().Count);
        Assert.Contains("Elo:", receipt["matches"]![0]!["summary"]!.ToJsonString());
        Assert.Equal("SF19-n256", receipt["ordo"]!["anchor"]!.GetValue<string>());
        if (OperatingSystem.IsWindows()) Assert.Equal(2, receipt["whea19"]!["before"]!.GetValue<int>());
        Assert.False(receipt["conductor"]!["pinned"]!.GetValue<bool>());
        Assert.Contains("not a Glicko-2 standing", receipt["statement"]!.GetValue<string>());
    }

    [Fact]
    public void Ladder_RejectsOddGamesUnknownRungsAndLc0WithoutTheGpu()
    {
        var (config, runner) = FakeLab();
        Assert.Throws<ArgumentException>(() => LadderCommand.Run(ChessCommandLine.Parse(["ladder", "calibrate", "--rungs", "sf:nodes=1", "--games", "3"]), runner, null, config));
        Assert.Throws<ArgumentException>(() => LadderCommand.Run(ChessCommandLine.Parse(["ladder", "calibrate", "--rungs", "xx:nodes=1"]), runner, null, config));
        config["LAPLACE_LC0"] = File_("tools/lc0.exe");
        config["LAPLACE_LC0_NET"] = File_("nets/BT4-1024x15x32h-swa-6147500-policytune-332.pb.gz");
        var rung = new Rung("lc0:nodes=64", config);
        Assert.Equal("Lc0-BT4-n64", rung.Name);
        Assert.Equal("1", rung.Options["MinibatchSize"]);
        var held = new FakeRunner(r => Path.GetFileName(r.Exe) == "nvidia-smi"
            ? new ToolResult(0, "NVIDIA GeForce, 999.0, 1000 MiB, 1000 MiB", "")
            : new ToolResult(0, "STATE : 4 RUNNING", ""));
        if (OperatingSystem.IsWindows())
            Assert.Contains("chess-GPU mode", Assert.Throws<ArgumentException>(() => LadderCommand.GuardGpu(held, [rung])).Message);
    }

    [Fact]
    public void Match_ConductsTwoEngines_AndReceiptsToolsEnginesArgvBookAndSeed()
    {
        var (config, runner) = FakeLab();
        string a = File_("engines/alpha.exe", "alpha"), b = File_("engines/beta.exe", "beta");
        var cmd = ChessCommandLine.Parse(["match", "--engine1", a + ":nodes=100:Hash=8", "--engine2", b + ":name=Beta", "--nodes", "50",
            "--games", "2", "--seed", "7", "--out", Path.Combine(_dir, "match")]);
        var receipt = MatchCommand.Run(cmd, runner, config,
            identify: start => new EngineIdentity(start.ExePath, UciProcess.FileSha256(start.ExePath), Path.GetFileNameWithoutExtension(start.ExePath), null, []));
        var argv = receipt["argv"]!.AsArray().Select(static n => n!.GetValue<string>()).ToList();
        Assert.Contains("name=alpha", argv);
        Assert.Contains("name=Beta", argv);
        Assert.Contains("option.Hash=8", argv);
        Assert.Contains("nodes=100", argv);
        Assert.Contains("nodes=50", argv); // --nodes fills the engine that gave none
        Assert.Equal("7", argv[argv.IndexOf("-srand") + 1]);
        var engines = receipt["engines"]!.AsArray();
        Assert.Equal(LabFiles.Sha256(a), engines[0]!["sha256"]!.GetValue<string>());
        Assert.Equal("alpha", engines[0]!["identity"]!["name"]!.GetValue<string>());
        Assert.Equal(LabFiles.Sha256(config["LAPLACE_FASTCHESS"]), receipt["conductor"]!["sha256"]!.GetValue<string>());
        Assert.Equal(LabFiles.Sha256(Path.Combine(_dir, "books", "8moves_v3.pgn")), receipt["book"]!["sha256_actual"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(_dir, "match", "receipt.json")));
        Assert.Equal(0, receipt["match"]!["exit"]!.GetValue<int>());
    }

    [Fact]
    public void MatchEngineSpec_ParsesWindowsPathsAndKeys()
    {
        var spec = MatchEngineSpec.Parse(@"C:\engines\x.exe:nodes=5:profile=ladder:Threads=2");
        Assert.Equal(@"C:\engines\x.exe", spec.Engine);
        Assert.Equal(5, spec.Nodes);
        Assert.Equal("ladder", spec.Profile);
        Assert.Equal([new KeyValuePair<string, string>("Threads", "2")], spec.Options);
        Assert.Equal("stockfish", MatchEngineSpec.Parse("stockfish").Engine);
        Assert.Throws<ArgumentException>(() => MatchEngineSpec.Parse("stockfish:bad"));
    }

    [SkippableFact]
    public void Match_RealFastchess_StockfishAgainstItself()
    {
        var cfg = ChessLabConfig.Read();
        Skip.IfNot(cfg.TryGetValue("LAPLACE_FASTCHESS", out var fc) && File.Exists(fc), "fastchess is not installed (LAPLACE_FASTCHESS)");
        Skip.IfNot(Laplace.Chess.Uci.Engines.ChessEngineCatalog.Resolve("stockfish", "ladder").Available, "Stockfish is not installed");
        var cmd = ChessCommandLine.Parse(["match", "--engine1", "stockfish:nodes=64", "--engine2", "stockfish:nodes=128", "--games", "2",
            "--book", "none", "--maxmoves", "40", "--no-tb", "--out", Path.Combine(_dir, "real")]);
        var receipt = MatchCommand.Run(cmd);
        Assert.Equal(0, receipt["match"]!["exit"]!.GetValue<int>());
        Assert.True(File.Exists(receipt["pgn"]!.GetValue<string>()));
        Assert.StartsWith("Stockfish", receipt["engines"]![0]!["identity"]!["name"]!.GetValue<string>());
    }

    // ---- review, lichess, inspect ----

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Review_WrapsChessGameReview()
    {
        var result = ReviewCommand.Run(ChessCommandLine.Parse(["review", Fixture("inspect-standard.pgn"), "--depth", "1"]));
        var game = Assert.Single(result.Games);
        Assert.Equal("FabianoCaruana", game.White);
        Assert.Contains("reviewed 1 games (depth 1)", ReviewCommand.Render(result));
        Assert.Throws<ArgumentException>(() => ReviewCommand.Run(ChessCommandLine.Parse(["review", Path.Combine(_dir, "none.pgn")])));
    }

    [Fact]
    public void Lichess_ParsesSettingsWithoutConnecting()
    {
        var s = LichessCommand.Parse(ChessCommandLine.Parse(["lichess", "--token", "t0k", "--engine", "stockfish", "--max-concurrent", "2", "--speed", "Blitz", "--rated"]));
        Assert.Equal(("t0k", "stockfish", 2, true), (s.Token, s.Engine, s.MaxConcurrent, s.Rated));
        Assert.Equal(["blitz"], s.Speeds!);
    }

    private static void RequireTier0()
    {
        try { CodepointPerfcache.LoadDefault(); }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or DirectoryNotFoundException)
        {
            throw new SkipException("the tier-0 perfcache is not installed: " + ex.Message);
        }
    }

    [SkippableFact]
    public void Inspect_StandardGame_ShowsIdentitiesHeadersAndFindings()
    {
        RequireTier0();
        var report = InspectCommand.Run(ChessCommandLine.Parse(["inspect", Fixture("inspect-standard.pgn")]));
        var g = Assert.Single(report.Games);
        Assert.True(g.Stored);
        Assert.Equal("played game", g.RecordKinds[0]);
        Assert.True(g.Line!.Verified);
        Assert.True(g.Playing!.Verified);
        Assert.True(g.Event!.Verified);
        Assert.Contains("|Hash128 { Hi = ", g.Playing.Preimage);
        Assert.NotEqual(g.Line.Id, g.Line.LawId);
        Assert.Equal(53, g.Plies.Count);
        Assert.Equal("d2d4", g.Plies[0].Uci);
        Assert.Equal(5, g.Plies[0].MoveAtoms.Count);
        Assert.Equal("90 a0 b0 a0 b0", g.Plies[0].MoveAtoms[0].Bytes);
        Assert.Equal(g.Plies[0].To, InspectCommand.Run(ChessCommandLine.Parse(["inspect", "d4"])).Games[0].Plies[0].To); // one position, whatever spelled it
        HeaderView H(string tag) => g.Headers.Single(h => h.Tag == tag);
        Assert.Equal("hashed only", H("Site").Summary);
        Assert.Equal("hashed only", H("Round").Summary);
        Assert.Equal("dropped", H("Link").Summary);
        Assert.Equal("dropped", H("UTCDate").Summary);
        Assert.Equal("content", H("ECO").Summary);
        Assert.Equal("content + hashed", H("Event").Summary);
        Assert.Contains(H("White").Dispositions, d => d.Contains("\"fabianocaruana\" (folded)"));
        Assert.Equal("not read", H("Result").Summary);
        Assert.Contains(g.Findings, f => f.StartsWith("#46.2", StringComparison.Ordinal) && f.Contains("Link"));
        Assert.Contains(g.Findings, f => f.StartsWith("#46.3", StringComparison.Ordinal));
        Assert.Contains(g.Entities, e => e.Role == "LINE" && e.Type == "Chess_Game");
        Assert.Contains("-- findings", InspectCommand.Render(report));
    }

    [SkippableFact]
    public void Inspect_Chess960_StartsAtTheSetUpPosition_WithNoRulesAtom()
    {
        RequireTier0();
        var g = InspectCommand.Run(ChessCommandLine.Parse(["inspect", Fixture("inspect-chess960.pgn"), "--where", "Variant=Chess960"])).Games.Single();
        Assert.Contains(g.RecordKinds, k => k.Contains("Chess960", StringComparison.Ordinal));
        Assert.Contains(g.RecordKinds, k => k.StartsWith("custom start", StringComparison.Ordinal));
        Assert.StartsWith("bbqrknrn/", g.Start.Fen);
        Assert.DoesNotContain(g.Start.Atoms, a => a.Domain == "rules");
        Assert.Equal("dropped", g.Headers.Single(h => h.Tag == "Variant").Summary);
        Assert.Contains(g.Headers.Single(h => h.Tag == "FEN").Dispositions, d => d.StartsWith("identity", StringComparison.Ordinal));
        Assert.Contains(g.Plies, p => p.San == "O-O" && p.Uci == "e1g1");
        Assert.Contains(g.Findings, f => f.StartsWith("#46.6", StringComparison.Ordinal) && f.Contains("Chess960"));
    }

    [SkippableFact]
    public void Inspect_IncompleteStudyAndEmptyGame_AreTheirRecordKinds()
    {
        RequireTier0();
        string pgn = File_("kinds.pgn",
            "[Event \"Study\"]\n[Site \"?\"]\n[Date \"????.??.??\"]\n[Round \"?\"]\n[White \"Carlsen, Magnus\"]\n[Black \"?\"]\n[Result \"*\"]\n\n"
            + "1. e4 {best by test} e5 2. Nf3 $1 (2. f4 exf4) Nc6 *\n\n"
            + "[Event \"Empty\"]\n[Site \"?\"]\n[Date \"2026.10.07\"]\n[Round \"1\"]\n[White \"A\"]\n[Black \"B\"]\n[Result \"1/2-1/2\"]\n\n1/2-1/2\n");
        var games = InspectCommand.Run(ChessCommandLine.Parse(["inspect", pgn, "--max-games", "2"])).Games;
        Assert.False(games[0].Stored);
        Assert.Contains(games[0].RecordKinds, k => k.StartsWith("incomplete", StringComparison.Ordinal));
        Assert.Contains(games[0].RecordKinds, k => k.StartsWith("study", StringComparison.Ordinal) && k.Contains("2 variation plies"));
        Assert.Equal(4, games[0].Plies.Count);
        Assert.True(games[1].Stored);
        Assert.Empty(games[1].Plies);
        Assert.Equal(games[1].Start.Id, games[1].Line!.Id);
        Assert.Contains(games[1].Findings, f => f.StartsWith("#46.7", StringComparison.Ordinal) && f.Contains("Chess_Game"));
    }

    [SkippableFact]
    public void Inspect_FenAndMoves_AreABareLine()
    {
        RequireTier0();
        var fen = InspectCommand.Run(ChessCommandLine.Parse(["inspect", "4k3/8/8/8/8/8/8/R3K2R w KQ - 0 1", "--moves", "e1g1"])).Games.Single();
        Assert.Equal("fen", InspectCommand.Run(ChessCommandLine.Parse(["inspect", "4k3/8/8/8/8/8/8/R3K2R w KQ - 0 1"])).Kind);
        Assert.Equal("O-O", fen.Plies.Single().San);
        Assert.Contains("White g-side, White c-side", fen.Start.Castling);
        var moves = InspectCommand.Run(ChessCommandLine.Parse(["inspect", "1. e4 e5 2. Nf3"])).Games.Single();
        Assert.Equal(3, moves.Plies.Count);
        Assert.Null(moves.Playing);
        Assert.Throws<ArgumentException>(() => InspectCommand.Run(ChessCommandLine.Parse(["inspect", "e4 e4"])));
    }
}
