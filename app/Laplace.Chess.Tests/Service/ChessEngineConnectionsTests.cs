using System.Text.Json;
using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessEngineConnectionsTests
{
    [Theory]
    [InlineData("fastchess")]
    [InlineData("cutechess")]
    public void RemoteEngineKeepsSeparateCommandArgumentsAndWorkerAssets(string conductor)
    {
        var connection = new ChessEngineConnection("mono-staging", "Staging", "laplace-mono", "staging", "world-a",
            "staging.example.com", "C:/Installed tools/connector.exe", "C:/Configured paths/client.json");
        var options = new CutechessOptions
        {
            Conductor = conductor, Rounds = 2, PgnOut = "games.pgn", PairOpenings = false,
            LaplaceConnection = connection,
            StockfishConnection = connection with { Id = "stockfish-worker", Implementation = "stockfish" },
        }.WithInstalledSyzygy(new Dictionary<string, string>(), () => throw new Exception("Must not read coordinator assets."));
        var args = CutechessRunner.BuildArguments(options, "unselected-laplace", "unselected-stockfish");
        Assert.Equal(2, args.Count(a => a == "cmd=" + connection.Executable));
        Assert.DoesNotContain(args, a => a.Contains("unselected-") || a.StartsWith("option.SyzygyPath="));
        if (conductor == "fastchess")
            Assert.Contains("args=connect --configuration \"C:/Configured paths/client.json\" --profile mono-staging", args);
        else
        {
            Assert.Contains("arg=C:/Configured paths/client.json", args);
            Assert.Contains("arg=mono-staging", args);
        }
    }

    [Fact]
    public void CatalogueExcludesLocalProcessAndDoesNotNeedTokenReadAccess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chess-connection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "connector");
            File.WriteAllText(executable, "");
            var configuration = Path.Combine(directory, "client.json");
            File.WriteAllText(configuration, JsonSerializer.Serialize(new { version = 1, profiles = new
            {
                local = new { transport = "process" },
                remote = new { transport = "websocket", implementation = "laplace-mono", label = "Worker", environment = "staging",
                    knowledge = "world-a", endpoint = "wss://worker.example.com/uci/mono", tokenFile = "unreadable-private-file" },
            } }));
            var entry = Assert.Single(ChessEngineConnections.Read(configuration, executable));
            Assert.Equal("remote", entry.Id);
            Assert.Equal("worker.example.com", entry.Host);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("--profile other")]
    public void CallerCannotInventAWorkerProfile(string name)
    {
        Assert.Throws<ArgumentException>(() => ChessEngineConnections.Select(new CutechessOptions(),
            new Dictionary<string, string> { ["laplaceProfile"] = name }));
    }
}
