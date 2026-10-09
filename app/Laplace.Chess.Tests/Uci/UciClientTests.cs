using System.IO.Pipes;
using Laplace.Chess.Service;
using Laplace.Chess.Service.Uci;
using Laplace.Chess.Uci.Engines;
using Xunit;

namespace Laplace.Chess.Uci.Tests;

/// <summary>The generic UCI client and its codec, against a scripted in-process engine; then one real search each for
/// Stockfish, Lc0 and laplace-uci, skipped where the binary is not installed.</summary>
public sealed class UciClientTests
{
    /// <summary>A scripted engine on anonymous pipes: each command line maps to the lines it answers.</summary>
    private sealed class FakeEngine : IDisposable
    {
        private readonly AnonymousPipeServerStream _toEngine = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream _fromEngine = new(PipeDirection.In);
        private readonly StreamWriter _engineOut;
        public readonly List<string> Received = [];
        public UciProcess Client { get; }

        public FakeEngine(Func<string, IEnumerable<string>?> script, TimeSpan? handshake = null)
        {
            var engineIn = new StreamReader(new AnonymousPipeClientStream(PipeDirection.In, _toEngine.ClientSafePipeHandle));
            _engineOut = new StreamWriter(new AnonymousPipeClientStream(PipeDirection.Out, _fromEngine.ClientSafePipeHandle)) { AutoFlush = true };
            _ = Task.Run(() =>
            {
                try
                {
                    while (engineIn.ReadLine() is { } line)
                    {
                        lock (Received) Received.Add(line);
                        var answer = script(line);
                        if (answer is null) { _engineOut.Dispose(); return; } // the engine exits
                        foreach (var a in answer) _engineOut.WriteLine(a);
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            });
            Client = UciProcess.Attach(new StreamWriter(_toEngine) { AutoFlush = true }, new StreamReader(_fromEngine),
                handshake ?? TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            Client.Dispose();
            try { _engineOut.Dispose(); } catch { }
            _fromEngine.Dispose();
        }
    }

    private static IEnumerable<string>? Standard(string line) => line switch
    {
        "uci" => ["id name Fake 1.0", "id author Tests", "option name Hash type spin default 16 min 1 max 1024",
                  "option name MultiPV type spin default 1 min 1 max 8", "option name Clear Hash type button",
                  "option name Mode type combo default a var a var b", "uciok"],
        "isready" => ["readyok"],
        "quit" => null,
        _ when line.StartsWith("go", StringComparison.Ordinal) =>
        [
            "info string thinking",
            "info depth 1 seldepth 2 multipv 1 score cp 30 nodes 10 nps 1000 tbhits 0 time 1 pv e7e5",
            "info depth 2 seldepth 3 multipv 1 score cp -25 lowerbound wdl 100 800 100 nodes 40 nps 2000 time 2 pv e7e5 g1f3",
            "info depth 2 multipv 2 score mate -3 nodes 40 pv c7c5",
            "bestmove e7e5 ponder g1f3",
        ],
        _ => [],
    };

    [Fact]
    public void Handshake_ReadsIdentityAndOptions_AndValidatesSetOption()
    {
        using var fake = new FakeEngine(Standard);
        Assert.Equal("Fake 1.0", fake.Client.Identity.Name);
        Assert.Equal("Tests", fake.Client.Identity.Author);
        Assert.True(fake.Client.Supports("hash"));
        fake.Client.SetOption("Hash", "64");
        Assert.Equal(UciFailure.Rejected, Assert.Throws<UciEngineException>(() => fake.Client.SetOption("Hash", "5000")).Failure);
        Assert.Equal(UciFailure.Rejected, Assert.Throws<UciEngineException>(() => fake.Client.SetOption("Mode", "c")).Failure);
        Assert.Equal(UciFailure.Rejected, Assert.Throws<UciEngineException>(() => fake.Client.SetOption("Nope", "1")).Failure);
        Assert.Equal([new KeyValuePair<string, string>("Hash", "64")], fake.Client.OptionsSent);
    }

    [Fact]
    public void Search_DecodesOneTypedModel_FromBlacksView()
    {
        using var fake = new FakeEngine(Standard);
        fake.Client.NewGame();
        var position = ChessPosition.From(null, ["e2e4"]);
        Assert.False(position.WhiteToMove);
        var analysis = fake.Client.Search(position, new UciLimits(Nodes: 40), TimeSpan.FromSeconds(5), "fresh");

        Assert.Equal("e7e5", analysis.BestMove.Move?.Text);
        Assert.Equal("g1f3", analysis.BestMove.Ponder?.Text);
        Assert.Equal(["thinking"], analysis.Strings);
        var best = analysis.Lines[0];
        Assert.Equal(-25, best.Score!.Cp);
        Assert.Equal(25, best.Score.WhiteCp);
        Assert.Equal(ScoreBound.Lower, best.Score.Bound);
        Assert.Equal(new EngineWdl(100, 800, 100), best.Wdl);
        Assert.Equal(["e7e5", "g1f3"], best.Pv!.Select(m => m.Text));
        Assert.Equal(3, best.SelDepth);
        Assert.Equal(-3, analysis.Lines[1].Score!.Mate);
        Assert.Equal(3, analysis.Lines[1].Score!.WhiteMate);
        Assert.Equal("go nodes 40", analysis.Receipt.Go);
        Assert.Equal("fresh", analysis.Receipt.StateMode);
        Assert.Equal(40, analysis.Receipt.Nodes);
        Assert.Equal("Fake 1.0", analysis.Receipt.Engine.Name);
        Assert.Null(analysis.Position.ContentId); // notation stays notation; no identity is minted from it
        lock (fake.Received)
        {
            Assert.Contains("setoption name Clear Hash", fake.Received);
            Assert.Contains("position startpos moves e2e4", fake.Received);
        }
    }

    [Fact]
    public void NoMove_IsDecodedAsNone()
    {
        using var fake = new FakeEngine(line => line.StartsWith("go", StringComparison.Ordinal)
            ? ["info depth 0 score mate 0", "bestmove (none)"] : Standard(line));
        var analysis = fake.Client.Search(ChessPosition.Start, new UciLimits(Depth: 1), TimeSpan.FromSeconds(5), "warm");
        Assert.Null(analysis.BestMove.Move);
        Assert.Equal(-20_000, analysis.Final!.Score!.SideToMoveCentipawns);
    }

    [Fact]
    public void SilentEngine_TimesOut_AndIsBroken()
    {
        using var fake = new FakeEngine(line => line.StartsWith("go", StringComparison.Ordinal) || line == "stop" ? [] : Standard(line));
        var error = Assert.Throws<UciEngineException>(() =>
            fake.Client.Search(ChessPosition.Start, new UciLimits(Nodes: 1), TimeSpan.FromMilliseconds(200), "fresh"));
        Assert.Equal(UciFailure.Timeout, error.Failure);
        Assert.True(fake.Client.Broken);
        lock (fake.Received) Assert.Contains("stop", fake.Received);
    }

    [Fact]
    public void EngineThatExitsMidSearch_IsUnavailable()
    {
        using var fake = new FakeEngine(line => line.StartsWith("go", StringComparison.Ordinal) ? null : Standard(line));
        var error = Assert.Throws<UciEngineException>(() =>
            fake.Client.Search(ChessPosition.Start, new UciLimits(Nodes: 1), TimeSpan.FromSeconds(5), "fresh"));
        Assert.Equal(UciFailure.Unavailable, error.Failure);
        Assert.True(fake.Client.Broken);
    }

    [Fact]
    public void HandshakeWithoutName_IsAProtocolFailure()
    {
        var error = Assert.Throws<UciEngineException>(() =>
            new FakeEngine(line => line == "uci" ? ["uciok"] : Standard(line)));
        Assert.Equal(UciFailure.Protocol, error.Failure);
    }

    [Theory]
    [InlineData("info depth 7 seldepth 9 multipv 2 score cp 12 wdl 300 500 200 nodes 99 nps 5 tbhits 1 time 3 hashfull 4 pv e2e4 e7e5")]
    [InlineData("info depth 3 score mate -2 upperbound nodes 5 pv a2a3")]
    [InlineData("info string hello world")]
    public void InfoLines_RoundTrip(string line)
        => Assert.Equal(line, EngineInfo.Decode(line, whiteToMove: true)!.Encode());

    [Fact]
    public void GoLimits_DecodeAndEncode()
    {
        var limits = UciLimits.Decode("go wtime 1000 btime 2000 winc 10 binc 20 movestogo 5 depth 3 nodes 9 movetime 7".Split(' '));
        Assert.Equal("go wtime 1000 btime 2000 winc 10 binc 20 movestogo 5 depth 3 nodes 9 movetime 7", limits.Encode());
        Assert.Null(UciLimits.Decode("go depth bad nodes -1".Split(' ')).Depth);
        Assert.Equal("go wtime 700 btime 1700 winc 0 binc 0", new UciClock(1000, 2000, 0, 0, OverheadMs: 300).ToLimits().Encode());
        Assert.Equal("position fen 8/8/8/8/8/8/8/K6k b - - 0 1 moves h1g1",
            ChessPosition.From("8/8/8/8/8/8/8/K6k b - - 0 1", ["h1g1"]).Encode());
        Assert.Throws<FormatException>(() => ChessPosition.From(null, ["e2e9"]));
    }

    [Fact]
    public void Pool_BoundsProcesses_AndDiscardsBroken()
    {
        int created = 0;
        var broken = new HashSet<object>();
        using var pool = new EnginePool<object>(() => { created++; return new object(); }, broken.Contains, capacity: 1);
        var first = pool.Rent();
        Assert.Null(pool.TryRent(TimeSpan.FromMilliseconds(20)));
        broken.Add(first);
        pool.Return(first);
        var second = pool.TryRent(TimeSpan.FromMilliseconds(20));
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal(2, created);
        pool.Return(second!);
    }

    // Real binaries. Each is skipped when the engine is not installed on this host.

    [SkippableFact]
    public void Stockfish_RealBinary_AnalysesThroughTheClient()
    {
        var spec = ChessEngineCatalog.Resolve("stockfish", "testimony");
        Skip.IfNot(spec.Available, spec.Missing);
        using var session = EngineSession.Open(spec);
        var analysis = session.Analyse(ChessPosition.Start, new UciLimits(Nodes: 20_000), multiPv: 2);
        Assert.NotNull(analysis.BestMove.Move);
        Assert.Equal(2, analysis.Lines.Count);
        Assert.StartsWith("Stockfish", analysis.Receipt.Engine.Name);
        Assert.Equal(64, analysis.Receipt.Engine.ExeSha256.Length);
        Assert.Contains(analysis.Receipt.Options, o => o.Key == "Threads" && o.Value == "1");
    }

    [SkippableFact]
    public void Lc0_RealBinary_OnTheGpu_WithASmallNodeBudget()
    {
        string? exe = Laplace.Engine.Core.LaplaceInstall.TryReadConfig("LAPLACE_LC0", "chess-lab.env");
        Skip.If(exe is null || !File.Exists(exe), "Lc0 is not installed (LAPLACE_LC0)");
        string net = Environment.GetEnvironmentVariable("LAPLACE_LC0_TEST_NET")
            ?? @"D:\Data\Ingest\Games\Chess\lc0\networks\768x15x24h-t82-swa-7464000.pb.gz";
        Skip.IfNot(File.Exists(net), "the T82 network is not installed (LAPLACE_LC0_TEST_NET)");
        var spec = ChessEngineCatalog.Resolve("lc0", "testimony");
        using var session = EngineSession.Open(spec, new Dictionary<string, string> { ["network"] = net, ["backend"] = "cuda-fp16" });
        var analysis = session.Analyse(ChessPosition.From(null, ["e2e4"]), new UciLimits(Nodes: 400));
        Assert.NotNull(analysis.BestMove.Move);
        Assert.StartsWith("Lc0", analysis.Receipt.Engine.Name);
        Assert.Contains(analysis.Receipt.Options, o => o.Key == "Backend" && o.Value == "cuda-fp16");
        Assert.Contains(analysis.Receipt.Options, o => o.Key == "WeightsFile" && o.Value == net);
        Assert.NotNull(analysis.Final?.Wdl);
    }

    [SkippableFact]
    public void LaplaceUci_RealBinary_AsTheLaplaceEngine()
    {
        var spec = ChessEngineCatalog.Resolve("laplace");
        Skip.IfNot(spec.Available, spec.Missing);
        // classical control: no database needed
        using var session = EngineSession.Open(spec, new Dictionary<string, string> { ["substrate"] = "off" });
        var analysis = session.Analyse(ChessPosition.Start, new UciLimits(Depth: 3));
        Assert.NotNull(analysis.BestMove.Move);
        Assert.Equal("Laplace", analysis.Receipt.Engine.Name);
        // Laplace advertises no MultiPV: the receipt shows exactly what was sent.
        Assert.Equal([new KeyValuePair<string, string>("Substrate", "off")], analysis.Receipt.Options);
    }

    [SkippableFact]
    public void LichessEngine_PlaysOnTheClock_ThroughLaplaceUci()
    {
        Skip.If(ChessLabPaths.LaplaceUciExecutable() is null || !ChessEngineCatalog.Resolve("stockfish").Available,
            "laplace-uci or Stockfish is not installed");
        var engine = UciLichessEngine.Create("stockfish", new Dictionary<string, string> { ["threads"] = "1" }, overheadMs: 200);
        using var provider = engine.Open();
        Assert.StartsWith("Stockfish", provider.Identity.Via?.Name);
        var decision = provider.Choose(ChessPosition.From(null, ["e2e4"]), new UciClock(5_000, 5_000, 100, 100), CancellationToken.None);
        Assert.Equal("go wtime 4800 btime 4800 winc 100 binc 100", decision.Analysis.Receipt.Go);
        Assert.Equal(200, decision.Analysis.Receipt.OverheadMs);
        Assert.Equal("warm", decision.Analysis.Receipt.StateMode);
        Assert.Matches("^[a-h][1-8][a-h][1-8]$", decision.Move.Text);
    }

    [SkippableFact]
    public void LaplaceUci_ProxiesStockfish_AndReportsTheEngineBehindIt()
    {
        var laplace = ChessEngineCatalog.Resolve("laplace");
        Skip.IfNot(laplace.Available && ChessEngineCatalog.Resolve("stockfish").Available, "laplace-uci or Stockfish is not installed");
        using var process = UciProcess.Start(laplace.Start! with { Arguments = ["uci", "--engine", "stockfish", "--profile", "testimony"] });
        process.IsReady();
        Assert.StartsWith("Stockfish", process.Identity.Name);
        Assert.EndsWith("laplace-uci" + (OperatingSystem.IsWindows() ? ".exe" : ""), process.Identity.ExePath);
        Assert.StartsWith("Stockfish", process.Identity.Via?.Name);
        Assert.NotEqual(process.Identity.ExeSha256, process.Identity.Via!.ExeSha256);
        var analysis = process.Search(ChessPosition.Start, new UciLimits(Nodes: 5_000), TimeSpan.FromSeconds(60), "warm");
        Assert.NotNull(analysis.BestMove.Move);
    }
}
