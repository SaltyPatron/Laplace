using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class AttestationMergeMathTests
{
    [Fact]
    public void ClassifyOutcome_LargeGames_UsesAverageWithoutOverflow()
    {
        long games = 20_000_000_000L;
        long sum = games * Glicko2.ScoreWin;
        Assert.Equal(AttestationOutcome.Confirm, AttestationMergeMath.ClassifyOutcome(games, sum));
    }

    [Fact]
    public void TimestampFromPgMicros_MatchesWorkingSetApplyConvention()
    {
        long unixUs = IntentStage.PgEpochUnixUs + 9_000_000;
        long pgUs = unixUs - IntentStage.PgEpochUnixUs;
        var expected = new DateTime(
            (unixUs - IntentStage.PgEpochUnixUs) * 10
            + new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
            DateTimeKind.Utc);
        Assert.Equal(expected, AttestationMergeMath.TimestampFromPgMicros(pgUs));
    }
}
