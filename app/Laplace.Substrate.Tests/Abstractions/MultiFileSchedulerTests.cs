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

    [Fact]
    public void InitialWave_GivesSpareComposeLaneToDominantFile()
    {
        long[] costs = [32, 8, 6, 6, 5, 5, 4, 4, 4, 3];

        int[] widths = MultiFileScheduler.PlanInitialSegments(
            costs, fileWorkers: 10, composeWorkers: 11);

        Assert.Equal(11, widths.Sum());
        Assert.Equal(2, widths[0]);
        Assert.All(widths.Skip(1), width => Assert.Equal(1, width));
    }

    [Fact]
    public void FewFiles_UseTheWholeComposePoolWithoutInventingFiles()
    {
        int[] widths = MultiFileScheduler.PlanInitialSegments(
            [100, 50], fileWorkers: 10, composeWorkers: 6);

        Assert.Equal(2, widths.Length);
        Assert.Equal(6, widths.Sum());
        Assert.True(widths[0] >= widths[1]);
    }

    private static string Write(string dir, string name, int bytes)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }
}
