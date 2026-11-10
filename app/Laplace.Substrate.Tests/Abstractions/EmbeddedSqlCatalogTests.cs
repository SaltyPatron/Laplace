using System.Reflection;
using System.Text.RegularExpressions;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests.Abstractions;

/// <summary>
/// A managed-only build (no laplace_core) reads the catalog from embedded resources. The root
/// sql_catalog.def only #includes its parts, so every part's statements must reach the fallback.
/// </summary>
public sealed class EmbeddedSqlCatalogTests
{
    private static IReadOnlyDictionary<string, NativeSqlQuery> Embedded() =>
        (IReadOnlyDictionary<string, NativeSqlQuery>)typeof(SqlCatalog)
            .GetMethod("LoadEmbeddedCatalog", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

    [Fact]
    public void EmbeddedFallbackCarriesEveryCatalogPart()
    {
        var embedded = Embedded();
        var root = new DirectoryInfo(FindRepoFile("engine/core/src/sql_catalog"));
        var declared = new Regex("SQL_QUERY\\(\\s*\"(?<name>[^\"]+)\"");
        int total = 0;
        foreach (var part in root.GetFiles("*.def"))
        {
            foreach (Match m in declared.Matches(File.ReadAllText(part.FullName)))
            {
                total++;
                Assert.True(embedded.ContainsKey(m.Groups["name"].Value),
                    $"{part.Name}: {m.Groups["name"].Value} is missing from the embedded catalog");
            }
        }
        Assert.True(total > 0);
        Assert.Equal(total, embedded.Count);
        Assert.True(embedded.ContainsKey("owner.lock_platform_roles"));
    }

    private static string FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(candidate)) return candidate;
        }
        // Builds may run outside the checkout; fall back to the repository root of this source file.
        var source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "..", relative));
        return Directory.Exists(source) ? source : throw new DirectoryNotFoundException(relative);
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
