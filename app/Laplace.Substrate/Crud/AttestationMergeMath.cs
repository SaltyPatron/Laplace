using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

/// <summary>
/// Overflow-safe attestation fold arithmetic shared by compose-time intent
/// folding, consensus accumulation, and the apply-batch merge lane.
/// </summary>
internal static class AttestationMergeMath
{
    private static readonly long PgEpochTicks =
        new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    public static long RowScoreTotal(in AttestationRow row) =>
        row.SumScoreFp1e9 ?? SafeScoreTimesCount(row.ScoreFp1e9, row.ObservationCount);

    public static long SafeScoreTimesCount(long scoreFp1e9, long observationCount)
    {
        if (observationCount == 0) return 0;
        if (observationCount == 1) return scoreFp1e9;
        if (scoreFp1e9 > long.MaxValue / observationCount
            || scoreFp1e9 < long.MinValue / observationCount)
        {
            throw new OverflowException(
                $"attestation score×count overflow: score_fp={scoreFp1e9} × observation_count={observationCount}");
        }

        return checked(scoreFp1e9 * observationCount);
    }

    public static long SafeAddScores(long left, long right) => checked(left + right);

    public static long SafeAddGames(long left, long right) => checked(left + right);

    /// <summary>
    /// Classify net outcome from summed score_fp totals vs observation count,
    /// matching the draw threshold used in <see cref="SubstrateChangeBuilder"/>.
    /// </summary>
    public static AttestationOutcome ClassifyOutcome(long games, long sumScoreFp1e9)
    {
        if (games <= 0) return AttestationOutcome.Draw;

        long drawPerGame = Glicko2.ScoreDraw;
        if (games <= long.MaxValue / drawPerGame)
        {
            long drawTotal = checked(games * drawPerGame);
            if (sumScoreFp1e9 > drawTotal) return AttestationOutcome.Confirm;
            if (sumScoreFp1e9 < drawTotal) return AttestationOutcome.Refute;
            return AttestationOutcome.Draw;
        }

        long avg = sumScoreFp1e9 / games;
        return avg > drawPerGame ? AttestationOutcome.Confirm
             : avg < drawPerGame ? AttestationOutcome.Refute
             : AttestationOutcome.Draw;
    }

    /// <summary>PG COPY timestamptz wire value (µs since 2000-01-01) → UTC DateTime.</summary>
    public static DateTime TimestampFromPgMicros(long pgMicrosSince2000)
    {
        if (pgMicrosSince2000 <= long.MaxValue / 10)
            return new DateTime(checked(pgMicrosSince2000 * 10 + PgEpochTicks), DateTimeKind.Utc);

        return new DateTime(
            checked((long)((decimal)pgMicrosSince2000 * 10m + PgEpochTicks)),
            DateTimeKind.Utc);
    }

    /// <summary>Unix µs → UTC DateTime for consensus fold UPDATE arrays.</summary>
    public static DateTime TimestampFromUnixMicros(long unixMicros, long pgEpochDeltaUs) =>
        TimestampFromPgMicros(unixMicros - pgEpochDeltaUs);
}
