using System.Text.Json;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessPlayRecoveryTests
{
    private static ChessPlayOperation Game(params string[] moves) => ChessPlayOperation.Start(Guid.NewGuid(),
        new OperationPrincipal("workspace", "authenticated-player", "browser", "microsoft"), true) with
    { Moves = moves };

    [Fact]
    public void ReloadRetainsRepetitionHistoryAndAuthenticatedOperation()
    {
        var original = Game("g1f3", "g8f6", "f3g1", "f6g8", "g1f3", "g8f6", "f3g1", "f6g8") with
        { Analysis = new() { [1] = new(ScoreCpSideToMove: 13, Depth: 6, Nodes: 720, Pv: ["g1f3", "d7d5"]) } };
        var reloaded = JsonSerializer.Deserialize<ChessPlayOperation>(JsonSerializer.Serialize(original))!;
        var session = reloaded.Restore();
        Assert.Equal(original.Requester, session.Requester);
        Assert.Equal(original.Moves, session.Moves);
        Assert.Equal(9, session.State!.RepetitionHistory.Count);
        Assert.NotNull(new ChessModality().Terminal(session.State));
        Assert.Equal(720, session.Operation!.Analysis[1].Nodes);
        Assert.Equal("Laplace", session.BlackPlayerName);
        Assert.Equal(original.StartedUtc.ToString("yyyy.MM.dd"), session.Metadata!.Date);
    }

    [Fact]
    public void ReloadRetainsCustomStartAndParticipants()
    {
        const string fen = "4k3/8/8/8/8/8/8/4K2R w - - 0 1";
        var game = Game("h1h2") with
        {
            Context = new("declared-context", null, "Recorded White", null, "Recorded Black",
                new(Event: "Recorded Event", StartFen: fen, TimeControl: "60+1")),
        };
        var session = game.Restore();
        Assert.Equal("declared-context", session.LearnContext);
        Assert.Equal("Recorded White", session.WhitePlayerName);
        Assert.Null(session.WhitePlayerId);
        Assert.Equal("60+1", session.Metadata!.TimeControl);
        Assert.Equal(2, game.ReplayStates().Count());
    }

    [Fact]
    public void RecoveryRejectsCorruptionAndNeverReopensCompletedOrUncertainRecording()
    {
        Assert.Throws<InvalidDataException>(() => Game("e2e5").Restore());
        Assert.Throws<InvalidDataException>(() => (Game("e2e4") with { Fen = ChessModality.StartFen }).Restore());
        foreach (var state in new[] { "completed", "recording", "failed" })
            Assert.Throws<InvalidOperationException>(() => (Game() with { State = state }).Restore());
    }

    [Theory]
    [InlineData("recording")]
    [InlineData("failed")]
    public void RecordingRecoveryRetainsResultAuthorityAndAnalysisWithoutReopeningPlay(string state)
    {
        var game = Game("e2e4", "e7e5") with
        {
            State = state, Recording = "in_progress", Result = "1/2-1/2", Adjudicated = true,
            Analysis = new() { [1] = new(ScoreCpSideToMove: 7, Depth: 3, Nodes: 84, Pv: ["e2e4"]) },
        };
        var retained = JsonSerializer.Deserialize<ChessPlayOperation>(JsonSerializer.Serialize(game))!;
        Assert.True(retained.CanRetryRecording);
        Assert.Throws<InvalidOperationException>(() => retained.Restore());
        var restored = retained.Restore(resumeRecording: true);
        Assert.Equal(game.Requester, restored.Requester);
        Assert.Equal(game.Moves, restored.Moves);
        Assert.Equal(game.Result, restored.Operation!.Result);
        Assert.True(restored.Operation.Adjudicated);
        Assert.Equal(84, restored.Operation.Analysis[1].Nodes);
        Assert.Equal(state, restored.Operation.State);
    }

    [Fact]
    public void RecordingRecoveryRequiresAnUnambiguousRetainedResult()
    {
        var game = Game("e2e4") with { State = "recording", Result = "1-0", Adjudicated = false };
        foreach (var invalid in new[]
        {
            game with { Adjudicated = null }, game with { Result = null }, game with { Result = "*" },
            game with { RecordToSubstrate = false }, game with { State = "completed" },
        })
        {
            Assert.False(invalid.CanRetryRecording);
            Assert.Throws<InvalidOperationException>(() => invalid.Restore(resumeRecording: true));
        }
    }
}
