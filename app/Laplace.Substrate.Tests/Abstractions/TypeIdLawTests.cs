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



    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase);

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

    [SkippableFact]
    public void CliProgram_HasNoRecursiveGenerateCte()
    {
        var repoRoot = FindRepoRoot();
        var program = Path.Combine(repoRoot, "app", "Laplace.Cli", "Program.cs");
        Skip.IfNot(File.Exists(program), "Cli/Program.cs not present (packaged test run)");

        var text = File.ReadAllText(program);
        Assert.DoesNotContain("WITH RECURSIVE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("GenerateSql", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Provider and shared ingest code emit only Content, Projection, Set, Range and
    /// ParseStructure physicalities; the other types are reserved. A shape whose vertex order
    /// carries no sequence meaning must use Set, Range or ParseStructure rather than Content,
    /// so the partial indexes over text trajectories (WHERE type = 1:
    /// physicalities_constituents_gin, physicalityanchor_traj_first_id_btree,
    /// physicalities_traj_probe) and the continuations read over them see only sequences.
    /// </summary>
    [Fact]
    public void PhysicalityType_ProductionEmitters_DoNotUseReservedTypes()
    {
        var repoRoot = FindRepoRoot();
        var decomposerDir = Path.Combine(repoRoot, "app");
        var allowed = new HashSet<string>
        {
            nameof(PhysicalityType.Content),
            nameof(PhysicalityType.Projection),
            nameof(PhysicalityType.Set),
            nameof(PhysicalityType.Range),
            nameof(PhysicalityType.ParseStructure),
        };

        foreach (var file in Directory.EnumerateFiles(decomposerDir, "*.cs", SearchOption.AllDirectories))
        {
            // Provider code plus the shared ingest abstractions in Laplace.Substrate/Abstractions.
            var isDecomposer = file.Contains("Laplace.Decomposers", StringComparison.OrdinalIgnoreCase);
            var isAbstractions = file.Contains(
                $"Laplace.Substrate{Path.DirectorySeparatorChar}Abstractions", StringComparison.OrdinalIgnoreCase);
            if (!isDecomposer && !isAbstractions) continue;
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
    public void EntityTypeRegistry_IsContentAddressed(string name)
    {
        // An entity type's id is the content id of its label: the same entity the text is.
        var expected = Laplace.Decomposers.Abstractions.ContentTierSpine.ResolveRoot(name);
        Assert.NotNull(expected);
        Assert.Equal(expected!.Value, EntityTypeRegistry.Id(name));
    }

    [Fact]
    public void CliProgram_CallsExtensionWalkText()
    {
        var repoRoot = FindRepoRoot();
        var cliDir = Path.Combine(repoRoot, "app", "Laplace.Cli");
        var reads = Path.Combine(repoRoot, "app", "Laplace.Substrate", "Crud", "Npgsql",
            "NpgsqlSubstrateReads.cs");

        var sb = new System.Text.StringBuilder();
        foreach (var f in Directory.EnumerateFiles(cliDir, "*.cs", SearchOption.AllDirectories))
            sb.Append(File.ReadAllText(f));
        var text = sb.ToString();
        // CLI and HTTP consume the canonical forward-turn rows via the native catalog.
        Assert.Contains("WalkTextAsync", text, StringComparison.Ordinal);
        Assert.Contains("SqlCatalog.Get(\"conversation.forward_turn\")", File.ReadAllText(reads), StringComparison.Ordinal);
        Assert.Contains("converse.forward_turn($1,$2,$3,$4,$5,$6)",
            File.ReadAllText(Path.Combine(repoRoot,"engine","core","src","sql_catalog.def")), StringComparison.Ordinal);
        Assert.DoesNotContain("generation.generate(", text, StringComparison.Ordinal);
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
        // Build outputs live outside the repo (Directory.Build.props), so the root is
        // read from the assembly metadata the props file stamps.
        var stamped = typeof(TypeIdLawTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LaplaceRepoRoot")?.Value;
        if (stamped is not null
            && Directory.Exists(Path.Combine(stamped, "app"))
            && Directory.Exists(Path.Combine(stamped, "engine")))
            return stamped;

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
