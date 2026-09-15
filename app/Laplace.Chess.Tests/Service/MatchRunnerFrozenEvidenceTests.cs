using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class MatchRunnerFrozenEvidenceTests
{
    [Fact]
    public async Task LiveMatch_DoesNotCompleteAnyGameUntilMeasurementCloses()
    {
        if (TestDb.ConnString is not { } cs) return; // integration: explicit disposable DB only

        await using var host = await ChessLiveGameHost.CreateAsync(
            defaultLearnContext: "chess/test/frozen-match", connString: cs);

        const int games = 4;
        int progressCalls = 0;
        var progress = new InlineProgress<(int Done, int AWins, int Draws, int BWins)>(_ =>
        {
            Interlocked.Increment(ref progressCalls);
            // Progress is emitted from inside the measured Parallel.For. Crossing the
            // CompleteGameAsync boundary here would let a finished sibling alter the
            // substrate read by a later sibling in the same benchmark generation.
            Assert.Equal(0, host.GamesCompleted);
        });

        var result = MatchRunner.Play(
            () => MatchRunner.RandomChooser,
            () => MatchRunner.RandomChooser,
            games: games,
            maxPlies: 2,
            seed: 41,
            concurrency: 2,
            openingPlies: 0,
            progress: progress,
            liveHost: host,
            liveLearnContext: "chess/test/frozen-match",
            aPlayerName: "Frozen-A",
            bPlayerName: "Frozen-B",
            externalIdPrefix: "laplace-test/frozen-match");

        Assert.Equal(games, result.Games);
        Assert.True(progressCalls > 0);
        Assert.Equal(games, host.GamesCompleted);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
