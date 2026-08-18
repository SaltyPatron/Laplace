using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class MultiFileSchedulerTests
{
    [Fact]
    public void FullCorpus_SchedulesLargestFilesFirst_WithDeterministicTies()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"laplace-schedule-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string small = Write(dir, "z-small.bin", 10);
            string tieB = Write(dir, "b-tie.bin", 50);
            string large = Write(dir, "a-large.bin", 100);
            string tieA = Write(dir, "a-tie.bin", 50);
            var declared = new[]
            {
                (Path: small, Label: "small"),
                (Path: tieB, Label: "tie-b"),
                (Path: large, Label: "large"),
                (Path: tieA, Label: "tie-a"),
            };

            var scheduled = MultiFileScheduler.Schedule(declared, maxTotalUnits: 0);

            Assert.Equal(
                new[] { "large", "tie-a", "tie-b", "small" },
                scheduled.Select(static f => f.Label));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CappedRun_PreservesDeclaredInputPrefix()
    {
        var declared = new[]
        {
            (Path: "third", Label: "third"),
            (Path: "first", Label: "first"),
            (Path: "second", Label: "second"),
        };

        Assert.Same(declared, MultiFileScheduler.Schedule(declared, maxTotalUnits: 1));
    }

    private static string Write(string dir, string name, int bytes)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }
}
