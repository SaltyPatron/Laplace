using System.Text.RegularExpressions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public class TypeIdLawTests
{
    private static readonly Regex ForbiddenMint = new(
        @"Hash128\.OfCanonical\s*\(\s*""substrate/type/",
        RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "EntityTypeRegistry.cs",
        "RelationTypeRegistry.cs",
        "BootstrapIntentBuilder.cs",
        "ByteAtoms.cs",
    };

    [Fact]
    public void DecomposerSources_DoNotMintTypeIdsOutsideRegistries()
    {
        var repoRoot = FindRepoRoot();
        var appDir = Path.Combine(repoRoot, "app");
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (AllowedFiles.Contains(name)) continue;
            if (name.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
                continue;

            var text = File.ReadAllText(file);
            if (!ForbiddenMint.IsMatch(text)) continue;

            var rel = Path.GetRelativePath(repoRoot, file);
            violations.Add(rel);
        }

        Assert.True(violations.Count == 0,
            "Hash128.OfCanonical(\"substrate/type/...\") outside allowed registries:\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void CliProgram_HasNoRecursiveGenerateCte()
    {
        var repoRoot = FindRepoRoot();
        var program = Path.Combine(repoRoot, "app", "Laplace.Cli", "Program.cs");
        if (!File.Exists(program)) return;

        var text = File.ReadAllText(program);
        Assert.DoesNotContain("WITH RECURSIVE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("GenerateSql", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalityType_ProductionEmitters_UseContentOrProjectionOnly()
    {
        var repoRoot = FindRepoRoot();
        var decomposerDir = Path.Combine(repoRoot, "app");
        var allowed = new HashSet<string>
        {
            nameof(PhysicalityType.Content),
            nameof(PhysicalityType.Projection),
        };

        foreach (var file in Directory.EnumerateFiles(decomposerDir, "*.cs", SearchOption.AllDirectories))
        {
            if (!file.Contains("Laplace.Decomposers.", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;

            var text = File.ReadAllText(file);
            foreach (PhysicalityType pt in Enum.GetValues<PhysicalityType>())
            {
                if (allowed.Contains(pt.ToString())) continue;
                var pattern = $"PhysicalityType.{pt}";
                if (text.Contains(pattern, StringComparison.Ordinal))
                {
                    Assert.Fail($"Reserved PhysicalityType.{pt} referenced in production decomposer: {file}");
                }
            }
        }
    }

    [Theory]
    [InlineData("Language")]
    [InlineData("WordNet_Synset")]
    [InlineData("FrameNet_Frame")]
    public void EntityTypeRegistry_MatchesCanonicalPath(string name)
    {
        var expected = Hash128.OfCanonical($"substrate/type/{name}/v1");
        Assert.Equal(expected, EntityTypeRegistry.Id(name));
    }

    [Fact]
    public void CliProgram_CallsExtensionWalkText()
    {
        var repoRoot = FindRepoRoot();
        var program = Path.Combine(repoRoot, "app", "Laplace.Cli", "Program.cs");
        var text = File.ReadAllText(program);
        // Generation was renamed walk_* (module 26): the CLI delegates to the
        // extension's laplace.walk_text wrapper over the engine walk_continuations,
        // and must never inline a bare laplace.generate(.
        Assert.Contains("laplace.walk_text", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("laplace.generate(", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeDynamics_EigenmapsUsesAvx2WhenTargetIsaAvx2()
    {
        var repoRoot = FindRepoRoot();
        var eigenmaps = Path.Combine(repoRoot, "engine", "dynamics", "src", "eigenmaps.cpp");
        var gccToolchain = Path.Combine(repoRoot, "cmake", "toolchains", "gcc-deterministic.cmake");
        Assert.True(File.Exists(eigenmaps));
        Assert.True(File.Exists(gccToolchain));

        var cpp = File.ReadAllText(eigenmaps);
        Assert.Contains("__AVX2__", cpp, StringComparison.Ordinal);

        var cmake = File.ReadAllText(gccToolchain);
        Assert.Contains("LAPLACE_TARGET_ISA", cmake, StringComparison.Ordinal);
        Assert.Contains("AVX2", cmake, StringComparison.Ordinal);
    }

    internal static string FindRepoRootPublic() => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "app"))
                && Directory.Exists(Path.Combine(dir, "engine")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Repository root not found");
    }
}
