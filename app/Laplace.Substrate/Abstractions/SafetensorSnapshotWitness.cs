namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Witness unit for a safetensors model directory: config + tokenizer + weight blobs.
/// Unlike GGUF, safetensors are not self-contained — the directory is the unit.
/// Hugging Face hub caches nest the real bundle under <c>models--*/snapshots/&lt;rev&gt;/</c>;
/// <see cref="ResolveCompleteDir"/> walks that layout the same way
/// <c>BenchCommands.EnumerateHubModels</c> already does, so ingest and bench agree.
/// </summary>
public static class SafetensorSnapshotWitness
{
    public const string ConfigFile = "config.json";
    public const string TokenizerFile = "tokenizer.json";

    public sealed record ValidationResult(bool Ok, string? Error);

    public static ValidationResult Validate(string snapshotDir)
    {
        if (string.IsNullOrWhiteSpace(snapshotDir) || !Directory.Exists(snapshotDir))
            return new(false, "snapshot directory not found");

        if (!File.Exists(Path.Combine(snapshotDir, ConfigFile)))
            return new(false,
                $"missing {ConfigFile} — safetensors are not self-contained (unlike GGUF); "
                + "architecture recipe lives beside the weight blobs");

        if (!File.Exists(Path.Combine(snapshotDir, TokenizerFile)))
            return new(false,
                $"missing {TokenizerFile} — vocab/merges are not inside .safetensors");

        if (Directory.GetFiles(snapshotDir, "*.safetensors").Length == 0)
            return new(false, "no *.safetensors weight files in snapshot directory");

        return new(true, null);
    }

    public static bool IsComplete(string snapshotDir) => Validate(snapshotDir).Ok;

    /// <summary>
    /// Resolve a path that may be a complete snapshot, an HF family dir
    /// (<c>models--org--name</c>), or a hub root containing those families, to a directory
    /// that passes <see cref="IsComplete"/>. Newest snapshot by mtime wins inside a family.
    /// Returns <c>null</c> when nothing under <paramref name="path"/> is a complete bundle.
    /// </summary>
    public static string? ResolveCompleteDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return null;

        if (IsComplete(path))
            return path;

        var nested = NewestCompleteSnapshot(Path.Combine(path, "snapshots"));
        if (nested is not null)
            return nested;

        foreach (var fam in Directory.GetDirectories(path, "models--*")
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var snap = NewestCompleteSnapshot(Path.Combine(fam, "snapshots"));
            if (snap is not null)
                return snap;
        }

        foreach (var d in Directory.GetDirectories(path).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(d).StartsWith("models--", StringComparison.Ordinal))
                continue;
            if (IsComplete(d))
                return d;
        }

        return null;
    }

    private static string? NewestCompleteSnapshot(string snapsDir)
    {
        if (!Directory.Exists(snapsDir))
            return null;
        return Directory.GetDirectories(snapsDir)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault(IsComplete);
    }
}
