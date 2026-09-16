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
    [Theory]
    [InlineData("malformed")]
    [InlineData("missing-games")]
    [InlineData("invalid-state")]
    public async Task RetainedValidationFailurePersistsReceiptWithoutInventingRecording(string mutation)
    {
        using var fixture = new RetainedJob();
        string experiment = mutation switch
        {
            "malformed" => "{invalid",
            "missing-games" => "{}",
            _ => fixture.Experiment.Replace("Completed", "NotAState", StringComparison.Ordinal),
        };
        await File.WriteAllTextAsync(fixture.ExperimentPath, experiment);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Service.IngestArtifactAsync(fixture.Id));

        using var receipt = fixture.ReadOnlyReceipt();
        AssertFailure(receipt.RootElement, error.Message);
        Assert.Equal(JsonValueKind.Null, receipt.RootElement.GetProperty("recording").ValueKind);
        var inputs = receipt.RootElement.GetProperty("inputs");
        Assert.Equal(fixture.PgnPath, inputs.GetProperty("pgnPath").GetString());
        Assert.Equal(fixture.ExperimentPath, inputs.GetProperty("experimentPath").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(experiment))),
            inputs.GetProperty("experimentTextSha256").GetString());
        Assert.Equal(0, fixture.HostRequests);
        Assert.Equal(experiment, await File.ReadAllTextAsync(fixture.ExperimentPath));
        Assert.Equal(fixture.Pgn, await File.ReadAllTextAsync(fixture.PgnPath));
        Assert.Equal(0, fixture.Slot.ActiveIngests);
        Assert.Equal(1, fixture.Slot.IngestGate.CurrentCount);
    }

    [Fact]
    public async Task FailureAfterValidReceiptPreservesTheStartedRecordingAndInputIdentity()
    {
        using var fixture = new RetainedJob();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.IngestArtifactAsync(fixture.Id));

        using var receipt = fixture.ReadOnlyReceipt();
        AssertFailure(receipt.RootElement, error.Message);
        var recording = receipt.RootElement.GetProperty("recording");
        Assert.Equal("failed", recording.GetProperty("status").GetString());
        Assert.Equal(error.Message, recording.GetProperty("error").GetString());
        Assert.Equal(1, recording.GetProperty("requestedGames").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Pgn))),
            recording.GetProperty("pgn").GetProperty("sha256").GetString());
        Assert.Equal(1, fixture.HostRequests);
        Assert.Equal(0, fixture.Slot.ActiveIngests);
        Assert.Equal(1, fixture.Slot.IngestGate.CurrentCount);
    }

    private static void AssertFailure(JsonElement receipt, string error)
    {
        Assert.Equal("laplace.chess-retained-ingestion/v1", receipt.GetProperty("schema").GetString());
        Assert.Equal("failed", receipt.GetProperty("status").GetString());
        Assert.Equal("failed", receipt.GetProperty("disposition").GetString());
        Assert.Equal(error, receipt.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, receipt.GetProperty("newlyRecordedGames").ValueKind);
        Assert.Equal(JsonValueKind.Null, receipt.GetProperty("alreadyPresentGames").ValueKind);
        Assert.False(receipt.GetProperty("noOpReplayVerified").GetBoolean());
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

        internal RetainedJob()
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
            Service = new ChessLabService(_ =>
            {
                HostRequests++;
                return Task.FromException<ChessLiveGameHost>(new InvalidOperationException("host unavailable"));
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
