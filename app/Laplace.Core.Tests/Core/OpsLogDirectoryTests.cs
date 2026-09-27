using Laplace.Engine.Core;
using Laplace.Ops;
using Xunit;

namespace Laplace.Core.Tests;

/// <summary>
/// LaplaceInstall.OpsLogDirectory never resolves inside a working tree, since the CSV sink
/// creates it owned by whichever user ran the binary. In-tree hosts resolve outside the
/// tree; out-of-tree hosts (build output redirected by LAPLACE_BUILD_ROOT) keep
/// $InstallRoot/logs. Both branches are asserted because the host location picks one.
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

    [Fact]
    public void ShareInstallDirectory_restores_group_write()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "laplace-share-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var logs = Directory.CreateDirectory(Path.Combine(root, "logs"));
            logs.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            LaplaceLogging.ShareInstallDirectory(logs.FullName, root);
            var mode = new DirectoryInfo(logs.FullName).UnixFileMode;
            Assert.True(mode.HasFlag(UnixFileMode.GroupWrite));
            Assert.True(mode.HasFlag(UnixFileMode.SetGroup));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
