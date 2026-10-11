using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

public sealed class LinuxMemoryLimitTests
{
    private const long Physical = 128L << 30;
    private static long Resolve(string membership, string mount, Dictionary<string, string> files)
        => MemoryTopology.BoundLinuxMemory(Physical, membership.Split('\n'), mount.Split('\n'),
            path => files.GetValueOrDefault(path));

    [Fact]
    public void V2HonorsTheSmallestAncestorOfTheActualProcess()
    {
        var files = new Dictionary<string, string>
        {
            ["/cgroup/memory.max"] = "max",
            ["/cgroup/system.slice/memory.max"] = (2L << 30).ToString(),
            ["/cgroup/system.slice/laplace.service/memory.max"] = (8L << 30).ToString()
        };
        Assert.Equal(2L << 30, Resolve("0::/system.slice/laplace.service", "1 0 0:1 / /cgroup rw - cgroup2 cgroup rw", files));
    }

    [Fact]
    public void V1UsesTheMemoryControllerAndDecodesTheMountedSubtree()
    {
        var files = new Dictionary<string, string>
        {
            ["/memory mount/memory.limit_in_bytes"] = (4L << 30).ToString(),
            ["/memory mount/job/memory.limit_in_bytes"] = (16L << 30).ToString(),
            ["/memory mount/job/memory.use_hierarchy"] = "1",
            ["/memory.limit_in_bytes"] = "1"
        };
        Assert.Equal(4L << 30, Resolve("7:cpu:/other\n8:memory:/tenant/job",
            @"1 0 0:1 /tenant /memory\040mount rw - cgroup memory rw,memory", files));
    }

    [Fact]
    public void V1WithoutHierarchyDoesNotChargeAnUnrelatedParentLimit()
    {
        Assert.Equal(4L << 30, Resolve("8:memory:/job", "1 0 0:1 / /memory rw - cgroup memory rw,memory",
            new() { ["/memory/job/memory.limit_in_bytes"] = (4L << 30).ToString(),
                    ["/memory/job/memory.use_hierarchy"] = "0", ["/memory/memory.limit_in_bytes"] = "1" }));
    }

    [Theory]
    [InlineData("max")]
    [InlineData("9223372036854771712")]
    [InlineData("invalid")]
    public void UnboundedOrUnavailableLimitsDoNotInflatePhysicalMemory(string value)
    {
        Assert.Equal(Physical, Resolve("0::/", "1 0 0:1 / /cgroup rw - cgroup2 cgroup rw",
            new() { ["/cgroup/memory.max"] = value }));
    }

    [Fact]
    public void NamespaceTraversalCannotReadOutsideTheCgroupMount()
    {
        var accessed = new List<string>();
        Assert.Equal(Physical, MemoryTopology.BoundLinuxMemory(Physical, ["0::/../../outside"],
            ["1 0 0:1 / /cgroup rw - cgroup2 cgroup rw"], path => { accessed.Add(path); return "1"; }));
        Assert.Empty(accessed);
    }
}
