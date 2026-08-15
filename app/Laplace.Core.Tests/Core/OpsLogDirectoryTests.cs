using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Core.Tests;

/// <summary>
/// The CSV sink creates its directory owned by whoever ran the binary. Inside a checkout
/// that is a directory the checkout's owner may not be able to unlink, and actions/checkout
/// deletes the workspace before every job — so a log directory under a working tree fails
/// every subsequent CI run on that runner, before any job step executes.
///
/// Both branches are asserted because the host's own location decides which one runs:
/// app/Directory.Build.props:17 redirects BaseOutputPath out of the tree whenever
/// LAPLACE_BUILD_ROOT is set, which is the Windows default (D:\Data\Laplace) and available
/// on any platform. In-tree hosts must resolve outside the tree; out-of-tree hosts keep
/// $InstallRoot/logs.
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

            var dir = LaplaceInstall.OpsLogDirectory;
            Assert.True(Path.IsPathRooted(dir), $"must be absolute, was '{dir}'");

            if (IsUnderWorkingTree(AppContext.BaseDirectory))
                Assert.False(IsUnderWorkingTree(dir), $"resolved inside the working tree: '{dir}'");
            else
                Assert.Equal(Path.Combine(LaplaceInstall.InstallRoot, "logs"), dir);
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
