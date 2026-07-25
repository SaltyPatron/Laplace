using Laplace.Decomposers.Code;
using Xunit;

namespace Laplace.Decomposers.Tests.Code;

/// <summary>
/// Pins GH #594: a directory holding multiple independent git repos must be
/// rejected rather than silently flattened into one repo-root identity.
/// </summary>
public sealed class RepoDecomposerNestedRepoTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "repo-nested-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SingleRepo_RootOwnGitOnly_DoesNotThrow()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Directory.CreateDirectory(Path.Combine(root, "src"));
            RepoDecomposer.ThrowIfNestedRepos(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void NoGitAnywhere_DoesNotThrow()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            RepoDecomposer.ThrowIfNestedRepos(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void NestedSiblingRepos_Throws()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "repo-a", ".git"));
            Directory.CreateDirectory(Path.Combine(root, "repo-b", ".git"));
            var ex = Assert.Throws<InvalidOperationException>(() => RepoDecomposer.ThrowIfNestedRepos(root));
            Assert.Contains("nested git repositor", ex.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RootGitPlusOneNestedRepo_Throws()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Directory.CreateDirectory(Path.Combine(root, "vendor", "some-lib", ".git"));
            Assert.Throws<InvalidOperationException>(() => RepoDecomposer.ThrowIfNestedRepos(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
