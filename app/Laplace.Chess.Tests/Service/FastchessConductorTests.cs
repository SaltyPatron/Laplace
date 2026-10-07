using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class FastchessConductorTests
{
    private static readonly CutechessOptions Match = new()
    {
        Conductor = "fastchess",
        Rounds = 4,
        Depth = 6,
        PgnOut = Path.Combine(Path.GetTempPath(), "fc-test", "games.pgn"),
        OpeningsFile = Path.Combine(Path.GetTempPath(), "fc-test", "openings.epd"),
        Event = "chess-lab/cutechess/abc",
        StockfishSyzygyPath = "/tb/3-4-5",
        Affinity = "16-31",
        Concurrency = 2,
    };

    [Fact]
    public void FastchessReceivesTheSameMatchAsCutechess()
    {
        var args = CutechessRunner.BuildArguments(Match, "uci", "sf").ToList();
        Assert.Equal(["-engine", "name=Laplace", "cmd=uci", "proto=uci", "option.Substrate=substrate"], args.Take(5));
        Assert.Contains("option.SyzygyPath=/tb/3-4-5", args);
        Assert.Contains("depth=6", args);
        Assert.Equal("2", args[args.IndexOf("-games") + 1]);
        Assert.Equal("2", args[args.IndexOf("-rounds") + 1]);                 // 4 games as 2 colour-swapped pairs
        Assert.Equal("16-31", args[args.IndexOf("-use-affinity") + 1]);
        Assert.Equal($"file={Match.PgnOut}", args[args.IndexOf("-pgnout") + 1]);
        Assert.Equal("format=cutechess", args[args.IndexOf("-output") + 1]);  // the one parser reads both conductors
        Assert.Contains("engine=true", args);
        Assert.DoesNotContain("-debug", args);
        Assert.DoesNotContain("-draw", args);                                // no score-based adjudication
        Assert.DoesNotContain("-resign", args);
    }

    [Theory]
    [InlineData("cutechess", "0,2,4")]
    [InlineData("fastchess", "0;2")]
    [InlineData("stockfish", null)]
    public void InvalidConductorOrAffinityIsRefused(string conductor, string? affinity)
        => Assert.ThrowsAny<ArgumentException>(() =>
            new CutechessOptions { Conductor = conductor, Affinity = affinity }.ValidateStockfishConfiguration());

    [Fact]
    public void EngineLogLinesBecomeCutechessTrafficWithOneInstancePerProcess()
    {
        var instances = new Dictionary<(int Thread, string Engine), int>();
        Assert.Equal("0 >Laplace(0): uci",
            CutechessRunner.FastchessTrafficAsCutechess("[Engine] [18:47:06.894761] <  2>  Laplace <--- uci", instances));
        Assert.Equal("0 <Laplace(0): id name Laplace",
            CutechessRunner.FastchessTrafficAsCutechess("[Engine] [18:47:06.901219] <  2>  Laplace ---> id name Laplace", instances));
        Assert.Equal("0 <Stockfish(1): id name Stockfish 19",
            CutechessRunner.FastchessTrafficAsCutechess("[Engine] [18:47:07.256999] <  2>  Stockfish ---> id name Stockfish 19", instances));
        Assert.Equal("0 >Laplace(2): ucinewgame",
            CutechessRunner.FastchessTrafficAsCutechess("[Engine] [18:47:08.000000] < 13>  Laplace <--- ucinewgame", instances));
        Assert.Null(CutechessRunner.FastchessTrafficAsCutechess("[INFO  ] [18:47:06.807305] <   > fastchess --- Starting tournament...", instances));
    }

    [Fact]
    public void TranslatedTrafficVerifiesEngineIdentity()
    {
        var instances = new Dictionary<(int Thread, string Engine), int>();
        var lines = new[]
        {
            "[Engine] [t] <  1>  Laplace ---> id name Laplace",
            "[Engine] [t] <  1>  Stockfish ---> id name Stockfish 19",
        }.Select(line => CutechessRunner.FastchessTrafficAsCutechess(line, instances)!)
         .Concat(["Started game 1 of 2 (Laplace vs Stockfish)", "Started game 2 of 2 (Stockfish vs Laplace)",
                  "Score of Laplace vs Stockfish: 1 - 1 - 0"]);
        var parser = new CutechessRunner.TranscriptParser(2, requireEngineIdentity: true, requirePairedSchedule: true);
        foreach (var line in lines) _ = parser.Line(ChessLabStream.Stdout, line).ToList();
        Assert.Equal(ChessLabJobState.Completed, parser.Complete(0).FinalState);
    }

    [Fact]
    public void InstalledSyzygyIsGivenUnlessTheJobNamesAPathOrNone()
    {
        var empty = new Dictionary<string, string>();
        Assert.Equal("/installed", new CutechessOptions().WithInstalledSyzygy(empty, () => "/installed").StockfishSyzygyPath);
        Assert.Null(new CutechessOptions().WithInstalledSyzygy(empty, () => null).StockfishSyzygyPath);

        var named = new Dictionary<string, string> { ["stockfishSyzygyPath"] = "/mine" };
        Assert.Equal("/mine", new CutechessOptions().WithStockfishConfiguration(named)
            .WithInstalledSyzygy(named, () => "/installed").StockfishSyzygyPath);

        var none = new Dictionary<string, string> { ["stockfishSyzygyPath"] = "none" };
        Assert.Null(new CutechessOptions().WithStockfishConfiguration(none)
            .WithInstalledSyzygy(none, () => "/installed").StockfishSyzygyPath);
    }

    [Fact]
    public async Task ReceiptNamesTheConductorAndOnlyTheOpeningsAsTheSuite()
    {
        string directory = Path.Combine(Path.GetTempPath(), "fastchess-receipt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string fastchess = Path.Combine(directory, "fastchess");
            string openings = Path.Combine(directory, "openings.epd");
            await File.WriteAllTextAsync(fastchess, "fastchess test binary");
            await File.WriteAllTextAsync(openings, "8/8/8/8/8/8/8/K6k w - -\n");
            var options = Match with { PgnOut = Path.Combine(directory, "games.pgn"), OpeningsFile = openings };
            var receipt = new CutechessExperimentReceipt("abc", options, new Dictionary<string, string>());
            await receipt.ObserveAsync(new ChessLabCommandEvent(fastchess,
                CutechessRunner.BuildArguments(options, "uci", "sf"), directory), CancellationToken.None);
            Assert.True(receipt.Artifacts.ContainsKey("fastchess"));
            Assert.Equal(Path.GetFullPath(openings), receipt.Artifacts["openingSuite"].Path);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
