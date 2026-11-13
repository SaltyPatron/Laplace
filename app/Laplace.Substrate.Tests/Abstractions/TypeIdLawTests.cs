using System.Text.RegularExpressions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

// Repository-file helpers shared by tests that read fixtures or sources from the checkout.
public static class TypeIdLawTests
{
    internal static string FindRepoRootPublic() => FindRepoRoot();

    /// <summary>
    /// Reads a C# type's full source: <c>Name.cs</c> plus every partial-class file
    /// <c>Name.*.cs</c> beside it, in ordinal order. Source gates assert on the
    /// type, not on whichever file a modularization split happened to leave a member in.
    /// </summary>
    internal static string ReadTypeSource(params string[] mainFileParts)
    {
        var main = Path.Combine([FindRepoRoot(), .. mainFileParts]);
        var dir = Path.GetDirectoryName(main)!;
        var stem = Path.GetFileNameWithoutExtension(main);
        var parts = new List<string> { File.ReadAllText(main) };
        parts.AddRange(Directory.GetFiles(dir, stem + ".*.cs")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(File.ReadAllText));
        return string.Join("\n", parts);
    }

    /// <summary>
    /// Reads a C module's full source: <c>name.c</c> plus the translation units and
    /// internal header it was split into (<c>name_*.c</c>, <c>name_*.h</c>) beside it.
    /// </summary>
    internal static string ReadCModuleSource(params string[] mainFileParts)
    {
        var main = Path.Combine([FindRepoRoot(), .. mainFileParts]);
        var dir = Path.GetDirectoryName(main)!;
        var stem = Path.GetFileNameWithoutExtension(main);
        var parts = new List<string> { File.ReadAllText(main) };
        parts.AddRange(Directory.GetFiles(dir, stem + "_*.c")
            .Concat(Directory.GetFiles(dir, stem + "_*.h"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(File.ReadAllText));
        return string.Join("\n", parts);
    }

    internal static string ReadSqlCatalog(string? repoRoot = null)
    {
        var root = repoRoot ?? FindRepoRoot();
        var defFile = Path.Combine(root, "engine", "core", "src", "sql_catalog.def");
        var defDir = Path.Combine(root, "engine", "core", "src", "sql_catalog");
        var parts = new List<string> { File.ReadAllText(defFile) };
        if (Directory.Exists(defDir))
            parts.AddRange(Directory.GetFiles(defDir, "*.def").OrderBy(f => f).Select(File.ReadAllText));
        return string.Join("\n", parts);
    }

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
