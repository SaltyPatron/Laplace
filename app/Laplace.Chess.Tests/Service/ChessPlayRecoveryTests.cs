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
}
