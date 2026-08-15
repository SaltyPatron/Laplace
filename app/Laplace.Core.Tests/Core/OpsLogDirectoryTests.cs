using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Core.Tests;

/// <summary>
/// The CSV sink creates its directory owned by whoever ran the binary. Inside a checkout
/// that is a directory the checkout's owner may not be able to unlink, and actions/checkout
/// deletes the workspace before every job — so a log directory under a working tree fails
/// every subsequent CI run on that runner, before any job step executes. The default is
/// therefore required to resolve outside the tree; the test host runs from
/// app/Laplace.Core.Tests/bin/..., i.e. exactly the in-tree case.
/// </summary>
public sealed class OpsLogDirectoryTests
{
    private const string Var = "LAPLACE_OPS_LOG_DIR";

    /// <summary>The same predicate LaplaceInstall uses: a directory holding both app/ and engine/.</summary>
    private static bool IsUnderWorkingTree(string path)
    {
        var dir = Path.GetFullPath(path);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "app")) && Directory.Exists(Path.Combine(dir, "engine")))
                return true;
            dir = Directory.GetParent(dir)?.FullName;
        }

        return false;
    }

    [Fact]
    public void Default_ResolvesOutsideTheWorkingTree()
    {
        var saved = Environment.GetEnvironmentVariable(Var);
        try
        {
            Environment.SetEnvironmentVariable(Var, null);

            Assert.True(IsUnderWorkingTree(AppContext.BaseDirectory),
                "precondition: the test host must run from inside the working tree");

            var dir = LaplaceInstall.OpsLogDirectory;

            Assert.True(Path.IsPathRooted(dir), $"must be absolute, was '{dir}'");
            Assert.False(IsUnderWorkingTree(dir), $"resolved inside the working tree: '{dir}'");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Var, saved);
        }
    }

    [Fact]
    public void Environment_Wins()
    {
        var saved = Environment.GetEnvironmentVariable(Var);
        try
        {
            var want = Path.Combine(Path.GetTempPath(), "laplace-ops-log-dir-test");
            Environment.SetEnvironmentVariable(Var, want);

            Assert.Equal(Path.GetFullPath(want), LaplaceInstall.OpsLogDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Var, saved);
        }
    }
}
