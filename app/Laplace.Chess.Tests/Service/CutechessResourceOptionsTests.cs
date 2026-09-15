using System.Text.Json;
using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class CutechessResourceOptionsTests
{
    [Fact]
    public void DefaultsLeaveStockfishResourcesAtItsAdvertisedDefaults()
    {
        var options = new CutechessOptions().WithStockfishConfiguration(new Dictionary<string, string>
        {
            ["stockfishThreads"] = "", ["stockfishHashMb"] = " ",
            ["stockfishNumaPolicy"] = "", ["stockfishSyzygyPath"] = ""
        });
        var args = CutechessRunner.BuildArguments(options, "laplace", "stockfish");

        foreach (var name in new[] { "Threads", "Hash", "NumaPolicy", "SyzygyPath" })
            Assert.DoesNotContain(args, arg => arg.StartsWith($"option.{name}=", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitResourcesApplyOnlyToStockfishAndKeepPathsAsSingleArguments()
    {
        var options = new CutechessOptions { Concurrency = 3 }.WithStockfishConfiguration(new Dictionary<string, string>
        {
            ["stockfishThreads"] = "8", ["stockfishHashMb"] = "512",
            ["stockfishNumaPolicy"] = "system", ["stockfishSyzygyPath"] = "/vault/Tables A:/vault/Tables B"
        });
        var args = CutechessRunner.BuildArguments(options, "laplace", "stockfish").ToList();
        int stockfishStart = args.IndexOf("name=Stockfish");
        int sharedStart = args.IndexOf("-each");

        foreach (var arg in new[]
        {
            "option.Threads=8", "option.Hash=512", "option.NumaPolicy=system",
            "option.SyzygyPath=/vault/Tables A:/vault/Tables B"
        })
        {
            Assert.Single(args, item => item == arg);
            Assert.InRange(args.IndexOf(arg), stockfishStart + 1, sharedStart - 1);
        }
        Assert.Equal("3", args[args.IndexOf("-concurrency") + 1]);
        Assert.Contains("option.Substrate=substrate", args.Take(stockfishStart));
    }

    [Theory]
    [InlineData("stockfishThreads", "0")]
    [InlineData("stockfishThreads", "-1")]
    [InlineData("stockfishThreads", "2147483647")]
    [InlineData("stockfishThreads", "1.5")]
    [InlineData("stockfishHashMb", "0")]
    [InlineData("stockfishHashMb", "-16")]
    [InlineData("stockfishHashMb", "33554433")]
    [InlineData("stockfishHashMb", "99999999999999999")]
    [InlineData("stockfishNumaPolicy", "unknown")]
    [InlineData("stockfishSyzygyPath", "/vault/tables\nquit")]
    public void InvalidResourceConfigurationIsRejectedBeforeBuildingACommand(string key, string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new CutechessOptions().WithStockfishConfiguration(
            new Dictionary<string, string> { [key] = value }));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("system")]
    [InlineData("hardware")]
    [InlineData("none")]
    public void NamedNumaPoliciesArePreserved(string policy)
    {
        var args = CutechessRunner.BuildArguments(new CutechessOptions { StockfishNumaPolicy = policy }, "l", "s");
        Assert.Contains("option.NumaPolicy=" + policy, args);
    }

    [Fact]
    public void DirectOptionsCannotBypassNumericValidation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CutechessRunner.BuildArguments(
            new CutechessOptions { StockfishThreads = 0 }, "l", "s"));
        Assert.Throws<ArgumentOutOfRangeException>(() => CutechessRunner.BuildArguments(
            new CutechessOptions { StockfishHashMb = 0 }, "l", "s"));
    }

    [Fact]
    public async Task ExperimentReceiptRetainsRequestedResourcesActualArgvIdentityAndObservedResults()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cutechess-receipt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string laplace = Path.Combine(directory, "laplace-uci");
            string stockfish = Path.Combine(directory, "stockfish");
            string cutechess = Path.Combine(directory, "cutechess-cli");
            await File.WriteAllTextAsync(laplace, "Laplace test binary identity");
            await File.WriteAllTextAsync(laplace + ".deps.json", "{\"binding\":\"test\"}");
            await File.WriteAllTextAsync(laplace + ".runtimeconfig.json", "{\"runtime\":\"test\"}");
            await File.WriteAllTextAsync(stockfish, "Stockfish test binary identity");
            await File.WriteAllTextAsync(cutechess, "Cutechess test binary identity");
            var config = new Dictionary<string, string> { ["stockfishThreads"] = "4", ["stockfishHashMb"] = "256" };
            var options = new CutechessOptions { Event = "chess-lab/cutechess/test-id", PairOpenings = false }
                .WithStockfishConfiguration(config);
            var receipt = new CutechessExperimentReceipt("test-id", options, config);
            await receipt.ObserveAsync(new ChessLabCommandEvent(cutechess,
                CutechessRunner.BuildArguments(options, laplace, stockfish), directory), CancellationToken.None);
            await receipt.ObserveAsync(new ChessLabTerminalEvent(ChessLabStream.Uci,
                "id name Stockfish 19", "Stockfish", ChessLabDirection.Recv), CancellationToken.None);
            await receipt.ObserveAsync(new ChessLabTerminalEvent(ChessLabStream.Uci,
                "option name Threads type spin default 1 min 1 max 1024", "Stockfish", ChessLabDirection.Recv), CancellationToken.None);
            await receipt.ObserveAsync(new ChessLabTerminalEvent(ChessLabStream.Uci,
                "setoption name Threads value 4", "Stockfish", ChessLabDirection.Send), CancellationToken.None);
            await receipt.ObserveAsync(new ChessLabGameEvent(1, "Laplace", "Stockfish", "1-0"), CancellationToken.None);
            await receipt.ObserveAsync(new ChessLabMetricEvent("wins", 1), CancellationToken.None);
            await receipt.ObserveAsync(new ChessLabDoneEvent(ChessLabJobState.Completed), CancellationToken.None);
            Assert.True(await receipt.VerifyArtifactsAsync(CancellationToken.None));
            string path = Path.Combine(directory, "experiment.json");
            await receipt.WriteAsync(path);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var root = document.RootElement;
            Assert.Equal("test-id", root.GetProperty("experimentId").GetString());
            Assert.Equal(options.Event, root.GetProperty("pgnEvent").GetString());
            Assert.Equal(4, root.GetProperty("requestedOptions").GetProperty("stockfishThreads").GetInt32());
            Assert.Equal(256, root.GetProperty("requestedOptions").GetProperty("stockfishHashMb").GetInt32());
            var artifact = root.GetProperty("artifacts").GetProperty("Stockfish");
            Assert.Equal(64, artifact.GetProperty("sha256").GetString()!.Length);
            Assert.Equal(stockfish, artifact.GetProperty("path").GetString());
            Assert.Equal(64, root.GetProperty("artifacts").GetProperty("Laplace/laplace-uci.runtimeconfig.json")
                .GetProperty("sha256").GetString()!.Length);
            Assert.Equal(64, root.GetProperty("artifacts").GetProperty("Laplace/laplace-uci.deps.json")
                .GetProperty("sha256").GetString()!.Length);
            Assert.True(root.GetProperty("artifactIdentitiesUnchanged").GetBoolean());
            Assert.Equal(artifact.GetProperty("sha256").GetString(), root.GetProperty("artifactsAfterMatch")
                .GetProperty("Stockfish").GetProperty("sha256").GetString());
            Assert.Contains("option.Threads=4", root.GetProperty("command").GetProperty("arguments")
                .EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(3, root.GetProperty("uciConfiguration").GetProperty("Stockfish").GetArrayLength());
            Assert.Equal("1-0", root.GetProperty("games")[0].GetProperty("result").GetString());
            Assert.Equal("Completed", root.GetProperty("matchState").GetString());
            Assert.NotEqual(JsonValueKind.Null, root.GetProperty("finishedAt").ValueKind);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedOrMissingEngineCannotKeepAnUnqualifiedOriginalIdentity(bool remove)
    {
        string directory = Path.Combine(Path.GetTempPath(), "cutechess-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string engine = Path.Combine(directory, "stockfish");
            await File.WriteAllTextAsync(engine, "before the match");
            var receipt = new CutechessExperimentReceipt("id", new CutechessOptions(), new Dictionary<string, string>());
            await receipt.ObserveAsync(new ChessLabCommandEvent(engine,
                ["-engine", "name=Stockfish", "cmd=" + engine], directory), CancellationToken.None);
            var before = receipt.Artifacts["Stockfish"];
            if (remove) File.Delete(engine);
            else await File.WriteAllTextAsync(engine, "replacement during the match");

            Assert.False(await receipt.VerifyArtifactsAsync(CancellationToken.None));
            Assert.False(receipt.ArtifactIdentitiesUnchanged);
            Assert.Equal(before, receipt.Artifacts["Stockfish"]);
            Assert.NotEqual(before.Sha256, receipt.ArtifactsAfterMatch["Stockfish"].Sha256);
            if (remove) Assert.Equal("not found", receipt.ArtifactsAfterMatch["Stockfish"].Error);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
