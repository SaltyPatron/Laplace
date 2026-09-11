using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Chess.Tests.Service;

public sealed class ChessLabRetentionTests
{
    [Fact]
    public void RetentionKeepsNewestTerminalJobsAndNeverSelectsRunningJobs()
    {
        var origin = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        var jobs = new List<ChessLabJob>();
        for (int i = 0; i < ChessLabService.MaxRetainedTerminalJobs + 5; i++)
        {
            var created = origin.AddMinutes(i);
            jobs.Add(new ChessLabJob(
                Guid.NewGuid().ToString("N"),
                ChessLabJobKind.Tactics,
                ChessLabJobState.Completed,
                new Dictionary<string, string>(),
                new ChessLabJobSummary(),
                new Dictionary<string, string>(),
                created,
                created.AddSeconds(30)));
        }

        var running = new ChessLabJob(
            Guid.NewGuid().ToString("N"),
            ChessLabJobKind.Cutechess,
            ChessLabJobState.Running,
            new Dictionary<string, string>(),
            new ChessLabJobSummary(),
            new Dictionary<string, string>(),
            origin.AddHours(-1));
        jobs.Add(running);

        var removed = ChessLabService.SelectTerminalJobsForRemoval(jobs);

        Assert.Equal(5, removed.Count);
        Assert.DoesNotContain(running.Id, removed);
        var expected = jobs
            .Where(job => job.State == ChessLabJobState.Completed)
            .OrderBy(job => job.FinishedAt)
            .Take(5)
            .Select(job => job.Id)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected, removed.ToHashSet(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef", false)]
    [InlineData("../0123456789abcdef0123456789abcdef", false)]
    [InlineData("not-a-job", false)]
    public void WorkspaceCleanupAcceptsOnlyCanonicalServerJobIds(string value, bool expected)
    {
        Assert.Equal(expected, ChessLabService.IsCanonicalJobId(value));
    }

    [Fact]
    public void OrphanWorkspaceAgeIsFinite()
    {
        Assert.True(ChessLabService.OrphanWorkspaceMinimumAge > TimeSpan.Zero);
        Assert.True(ChessLabService.OrphanWorkspaceMinimumAge <= TimeSpan.FromDays(1));
    }
}
