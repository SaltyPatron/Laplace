using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class RepositoryCognitionOwnershipTests
{
    [Fact]
    public void LaplaceBuild_DoesNotDelegateCognitionToAnotherRepository()
    {
        var repoRoot = FindRepoRoot();
        var cmakePath = Path.Combine(repoRoot, "extension", "laplace_substrate", "CMakeLists.txt");
        var scaffoldPath = Path.Combine(repoRoot, "extension", "laplace_substrate", "sql", "functions", "converse", "chat_scaffold.sql.in");

        Assert.False(File.Exists(Path.Combine(repoRoot, "scripts", "build-refactor-engine.sh")),
            "Laplace/main must not clone/build another repository as its cognition implementation.");
        Assert.False(File.Exists(Path.Combine(repoRoot, "extension", "laplace_substrate", "src", "refactor_cognition.c")),
            "Laplace/main must not carry a transport adapter that delegates cognition ownership away from this repository.");
        Assert.False(File.Exists(Path.Combine(repoRoot, "engine", "core", "src", "sql_catalog_refactor.def")),
            "Laplace/main must not carry a second cross-repository SQL catalog for cognition.");

        var cmake = File.ReadAllText(cmakePath);
        Assert.DoesNotContain("Laplace-Refactor", cmake, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refactor_cognition", cmake, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("liblaplace_engine", cmake, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LAPLACE_REFACTOR_ENGINE_PREFIX", cmake, StringComparison.Ordinal);

        var scaffold = File.ReadAllText(scaffoldPath);
        Assert.DoesNotContain("converse.refactor_cognition", scaffold, StringComparison.Ordinal);
        Assert.DoesNotContain("pg_laplace_refactor_cognition", scaffold, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Laplace.sln"))
                || (Directory.Exists(Path.Combine(dir.FullName, "extension"))
                    && Directory.Exists(Path.Combine(dir.FullName, "engine"))
                    && Directory.Exists(Path.Combine(dir.FullName, "app"))))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Laplace repository root.");
    }
}
