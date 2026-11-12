using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessLabIngestionReceiptTests
{
    [Fact]
    public async Task ARetainedMatchIsStatedByTheWitnessLaplaceAndReceipted()
    {
        using var engine = new RecordingEngine();
        using var fixture = new RetainedJob(_ => Task.FromResult(engine.Host()));

        var result = await fixture.Service.IngestArtifactAsync(fixture.Id);

        Assert.NotNull(result);
        Assert.Equal(1, result.Parsed);
        Assert.Equal("recorded", result.Disposition);
        var read = Assert.Single(engine.Reads);
        Assert.Equal(ChessGameRecorder.Witness, read.Witness);
        Assert.Equal(fixture.PgnPath, read.Path);
        using var receipt = fixture.ReadOnlyReceipt();
        var root = receipt.RootElement;
        Assert.Equal("laplace.chess-retained-ingestion/v2", root.GetProperty("schema").GetString());
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal("recorded", root.GetProperty("disposition").GetString());
        Assert.Equal(ChessGameRecorder.Witness, root.GetProperty("witness").GetString());
        Assert.Equal(1, root.GetProperty("games").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
        // The retained match is read, never rewritten.
        Assert.Equal(fixture.Experiment, await File.ReadAllTextAsync(fixture.ExperimentPath));
        Assert.Equal(fixture.Pgn, await File.ReadAllTextAsync(fixture.PgnPath));
    }

    [Fact]
    public async Task AFailedReadIsReceiptedAsFailedWithItsInputsAndNeverAsRecorded()
    {
        using var fixture = new RetainedJob();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.IngestArtifactAsync(fixture.Id));

        using var receipt = fixture.ReadOnlyReceipt();
        var root = receipt.RootElement;
        Assert.Equal("laplace.chess-retained-ingestion/v2", root.GetProperty("schema").GetString());
        Assert.Equal("failed", root.GetProperty("status").GetString());
        Assert.Equal("failed", root.GetProperty("disposition").GetString());
        Assert.Equal(error.Message, root.GetProperty("error").GetString());
        Assert.Equal(1, root.GetProperty("games").GetInt32());
        var inputs = root.GetProperty("inputs");
        Assert.Equal(fixture.PgnPath, inputs.GetProperty("pgnPath").GetString());
        Assert.Equal(fixture.ExperimentPath, inputs.GetProperty("experimentPath").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Experiment))),
            inputs.GetProperty("experimentTextSha256").GetString());
        Assert.Equal(1, fixture.HostRequests);
        Assert.Equal(0, fixture.Slot.ActiveIngests);
        Assert.Equal(1, fixture.Slot.IngestGate.CurrentCount);
    }

    private sealed class RetainedJob : IDisposable
    {
        internal readonly string Pgn;
        internal readonly string Id = Guid.NewGuid().ToString("N");
        private readonly string _directory;
        internal readonly string PgnPath;
        internal readonly string ExperimentPath;
        internal readonly string Experiment;
        internal readonly ChessLabService Service;
        internal readonly ChessLabService.JobSlot Slot;
        internal int HostRequests;

        internal RetainedJob(Func<CancellationToken, Task<ChessLiveGameHost>>? host = null)
        {
            _directory = Path.Combine(Path.GetFullPath(ChessLabPaths.LabDir), Id);
            Directory.CreateDirectory(_directory);
            PgnPath = Path.Combine(_directory, "games.pgn");
            ExperimentPath = Path.Combine(_directory, "experiment.json");
            Pgn = $"[Event \"chess-lab/cutechess/{Id}\"]\n[White \"Laplace\"]\n[Black \"Stockfish\"]\n[Result \"1-0\"]\n\n"
                + "1. e4 e5 2. Qh5 Nc6 3. Bc4 Nf6 4. Qxf7# 1-0\n";
            Experiment = JsonSerializer.Serialize(new
            {
                formatVersion = 1, experimentId = Id, pgnEvent = "chess-lab/cutechess/" + Id,
                requestedOptions = new { }, artifacts = new { },
                matchState = "Completed", artifactIdentitiesUnchanged = true,
                command = new { arguments = new[] { "-each", "depth=4" } },
                games = new[] { new { index = 1, white = "Laplace", black = "Stockfish", result = "1-0 (White mates)" } },
            });
            File.WriteAllText(PgnPath, Pgn);
            File.WriteAllText(ExperimentPath, Experiment);
            Service = new ChessLabService(ct =>
            {
                HostRequests++;
                return host?.Invoke(ct)
                    ?? Task.FromException<ChessLiveGameHost>(new InvalidOperationException("host unavailable"));
            });
            var job = new ChessLabJob(Id, ChessLabJobKind.Cutechess, ChessLabJobState.Completed,
                new Dictionary<string, string>(), new ChessLabJobSummary(),
                new Dictionary<string, string> { ["games.pgn"] = PgnPath, ["experiment.json"] = ExperimentPath },
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            Slot = new(job, Channel.CreateUnbounded<ChessLabEvent>());
            // Seed only the service's process-local retained-job inventory. The test
            // invokes the actual HTTP service operation, validation and receipt writer.
            var jobs = (ConcurrentDictionary<string, ChessLabService.JobSlot>)typeof(ChessLabService)
                .GetField("_jobs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Service)!;
            Assert.True(jobs.TryAdd(Id, Slot));
        }

        internal JsonDocument ReadOnlyReceipt()
        {
            var artifact = Assert.Single(Service.GetJob(Id)!.Artifacts,
                item => item.Key.StartsWith("ingest-", StringComparison.Ordinal));
            Assert.Equal(Path.Combine(_directory, artifact.Key), artifact.Value);
            Assert.False(File.Exists(artifact.Value + ".pending"));
            Assert.Equal(artifact.Value, Assert.Single(Directory.GetFiles(_directory, "ingest-*.json")));
            return JsonDocument.Parse(File.ReadAllText(artifact.Value));
        }

        public void Dispose()
        {
            Slot.IngestGate.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
