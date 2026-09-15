using System.Text.Json;
using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessExperimentEvidenceTests
{
    internal const string Receipt = """
        {
          "formatVersion": 1,
          "experimentId": "test-id",
          "pgnEvent": "chess-lab/cutechess/test-id",
          "requestedOptions": { "stockfishThreads": 4, "stockfishHashMb": 256 },
          "artifacts": { "Stockfish": { "sha256": "exact-engine-identity" } },
          "games": [{ "index": 1, "result": "1-0" }],
          "ingested": null
        }
        """;

    [Fact]
    public void ReceiptSurvivesIngestionStatusUpdatesWithIdenticalRecordedContent()
    {
        var before = ChessExperimentEvidence.Parse(Receipt);
        var after = ChessExperimentEvidence.Parse(Receipt.Replace("\"ingested\": null", "\"ingested\": true"));

        Assert.Equal(before.ReceiptJson, after.ReceiptJson);
        using var document = JsonDocument.Parse(before.ReceiptJson);
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("ingested", out _));
        Assert.Equal(4, root.GetProperty("requestedOptions").GetProperty("stockfishThreads").GetInt32());
        Assert.Equal("exact-engine-identity", root.GetProperty("artifacts").GetProperty("Stockfish").GetProperty("sha256").GetString());
        Assert.Equal("1-0", root.GetProperty("games")[0].GetProperty("result").GetString());
    }

    [Fact]
    public void ReceiptAcceptsItsOriginalEventAndRejectsAnUnrelatedPlaying()
    {
        var evidence = ChessExperimentEvidence.Parse(Receipt);
        evidence.ValidateGame("[Event \"chess-lab/cutechess/test-id\"]\n[Result \"1-0\"]\n\n1. e4 1-0");
        Assert.Throws<InvalidDataException>(() => evidence.ValidateGame(
            "[Event \"chess-lab/cutechess/another-job\"]\n\n1. e4 1-0"));
        Assert.Throws<InvalidDataException>(() => evidence.ValidateGame("1. e4 1-0"));
    }

    [Theory]
    [InlineData("\"formatVersion\": 1", "\"formatVersion\": 2")]
    [InlineData("\"formatVersion\": 1", "\"formatVersion\": \"1\"")]
    [InlineData("\"experimentId\": \"test-id\"", "\"experimentId\": \"\"")]
    [InlineData("\"pgnEvent\": \"chess-lab/cutechess/test-id\"", "\"pgnEvent\": \"T\"")]
    [InlineData("\"requestedOptions\"", "\"missingOptions\"")]
    [InlineData("\"artifacts\"", "\"missingArtifacts\"")]
    public void MalformedReceiptCannotBeAttachedAsExperimentEvidence(string from, string to)
        => Assert.Throws<InvalidDataException>(() => ChessExperimentEvidence.Parse(Receipt.Replace(from, to)));
}
