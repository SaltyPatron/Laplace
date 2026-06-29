using System.Text.Json;
using global::Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Persists dropped physicalities index DDL across separate ingest processes when
/// <c>LAPLACE_DEFER_PHYS_INDEX_REBUILD=1</c> (foundation seed chain).
/// </summary>
public static class DeferredPhysIndexRebuild
{
    private static string DefsPath =>
        Path.Combine(
            Environment.GetEnvironmentVariable("LAPLACE_ROOT")
                ?? Directory.GetCurrentDirectory(),
            "build-win", "deferred-phys-index-defs.json");

    public static void Register(IReadOnlyList<string> indexDefs)
    {
        if (indexDefs.Count == 0) return;
        var dir = Path.GetDirectoryName(DefsPath)!;
        Directory.CreateDirectory(dir);
        var list = new List<string>();
        if (File.Exists(DefsPath))
        {
            try
            {
                list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(DefsPath)) ?? [];
            }
            catch { /* overwrite corrupt file */ }
        }
        list.AddRange(indexDefs);
        File.WriteAllText(DefsPath, JsonSerializer.Serialize(list));
    }

    public static bool HasPending => File.Exists(DefsPath);

    public static async Task RebuildAsync(NpgsqlDataSource ds, CancellationToken ct = default)
    {
        if (!File.Exists(DefsPath)) return;
        List<string> defs;
        try
        {
            defs = JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(DefsPath, ct)) ?? [];
        }
        finally
        {
            try { File.Delete(DefsPath); } catch { /* best effort */ }
        }
        if (defs.Count == 0) return;
        await SecondaryIndexPolicy.RebuildIndexesAsync(ds, defs, ct);
    }
}
