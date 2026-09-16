using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessPositionPlayingObservationTests
{
    [Theory]
    [InlineData("parsed")]
    [InlineData("shared-replay")]
    [InlineData("witnessed")]
    [InlineData("live-trajectory")]
    public void CompleteGamesShareContentAndKeepDistinctReplayStableObservationIdentities(string path)
    {
        CodepointPerfcache.LoadDefault();
        var games = new[] { 1, 2 }.Select(i => File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", $"position-playing-game-{i}.pgn")))
            .Select(pgn => Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(pgn))).ToArray();
        Assert.Equal(2, games.Length);
        Assert.NotEqual(games[0].PlayingId, games[1].PlayingId);
        Assert.Equal(games[0].LineId, games[1].LineId);
        var modality = new ChessModality();
        foreach (var game in games)
        {
            Assert.Equal(154, game.ResolvedMoves.Length);
            var state = modality.Initial();
            foreach (var move in game.ResolvedMoves) state = modality.Apply(state, move);
            Assert.Equal(game.Result.ResultToken, modality.Terminal(state)?.ResultToken);
        }

        var first = Deposit(games[0], path);
        var second = Deposit(games[1], path);
        var replay = Deposit(games[0], path);
        try
        {
            var firstRows = first.Attestations.OrderBy(a => a.SubjectId.ToString()).ToArray();
            var secondRows = second.Attestations.OrderBy(a => a.SubjectId.ToString()).ToArray();
            Assert.NotEmpty(firstRows);
            Assert.Equal(firstRows.Select(a => a.SubjectId), secondRows.Select(a => a.SubjectId));
            Assert.All(firstRows, a => Assert.Equal(games[0].PlayingId, a.ContextId));
            Assert.All(secondRows, a => Assert.Equal(games[1].PlayingId, a.ContextId));
            Assert.Empty(firstRows.Select(a => a.Id).Intersect(secondRows.Select(a => a.Id)));
            Assert.Equal(firstRows.Select(a => (a.TypeId, a.ObjectId, a.SourceId, a.ObservationCount, a.SumScoreFp1e9)),
                secondRows.Select(a => (a.TypeId, a.ObjectId, a.SourceId, a.ObservationCount, a.SumScoreFp1e9)));
            Assert.Equal(first.Metadata.IntentId, replay.Metadata.IntentId);
            Assert.Equal(first.Attestations.Select(a => a with { LastObservedAtUnixUs = 0 }),
                replay.Attestations.Select(a => a with { LastObservedAtUnixUs = 0 }));
        }
        finally
        {
            foreach (var change in new[] { first, second, replay })
                foreach (var stage in change.IntentStages) stage.Dispose();
        }
    }

    private static SubstrateChange Deposit(ChessGameRecord game, string path)
    {
        var builder = new SubstrateChangeBuilder(ChessPositionOutcomes.SourceId,
            "test/position-playing/" + game.PlayingId);
        switch (path)
        {
            case "parsed": ChessPositionOutcomes.DepositFromParsed(builder, game); break;
            case "shared-replay": ChessPositionOutcomes.DepositFromParsed(builder, game,
                ChessPgnDecomposer.MaterializeParsedReplay(game)); break;
            case "witnessed": ChessPositionOutcomes.Deposit(builder, ChessAnalyze.WitnessedFromParsed(game)); break;
            case "live-trajectory": ChessPositionOutcomes.DepositTrajectory(builder,
                ChessPgnDecomposer.MaterializeParsedReplay(game).Boards.Select(board => board.ToFen()).ToArray(),
                game.Result, game.PlayingId); break;
            default: throw new ArgumentOutOfRangeException(nameof(path));
        }
        return builder.SetInputUnitsConsumed(1).Build();
    }
}
