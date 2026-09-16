using System.Reflection;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

public sealed class LaplaceInstallRepoRootTests
{
    [Fact]
    public void RepoRootResolvesSourceInsteadOfRelocatedBuildOutput()
    {
        var stamped = typeof(LaplaceInstall).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "LaplaceRepoRoot").Value;
        Assert.False(string.IsNullOrWhiteSpace(stamped));
        Assert.True(LaplaceInstall.TryRepoRoot(out var root));
        Assert.Equal(Path.GetFullPath(stamped!), root);
        Assert.True(File.Exists(Path.Combine(root, "app", "Laplace.slnx")));
        Assert.True(File.Exists(Path.Combine(root, "engine", "CMakeLists.txt")));
        Assert.True(Directory.Exists(Path.Combine(root, "test-data", "syzygy")));
    }
}
