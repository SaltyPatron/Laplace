namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Standardized file-discovery helpers for decomposer ingest paths.
/// Consolidates the directory-scanning patterns in FrameNet, PropBank, VerbNet, WordNet, etc.
/// </summary>
public static class DecomposerFileDiscovery
{
    /// <summary>
    /// Enumerate files matching <paramref name="pattern"/> under <paramref name="root"/>, trying
    /// optional <paramref name="fallbackSubdirs"/> in order (each relative to root) before falling
    /// back to root itself. Returns files from the first directory that exists and has any matches,
    /// sorted by path.
    /// </summary>
    public static IEnumerable<string> Enumerate(
        string root,
        string pattern,
        SearchOption search = SearchOption.TopDirectoryOnly,
        params string[] fallbackSubdirs)
    {
        var candidates = fallbackSubdirs.Length > 0
            ? fallbackSubdirs.Select(s => Path.Combine(root, s)).Append(root)
            : new[] { root }.AsEnumerable();

        foreach (var dir in candidates)
        {
            if (!Directory.Exists(dir)) continue;
            var files = Directory.EnumerateFiles(dir, pattern, search)
                                 .OrderBy(p => p, StringComparer.Ordinal);
            if (search == SearchOption.AllDirectories || files.Any())
                return files;
        }
        return Enumerable.Empty<string>();
    }

    /// <summary>
    /// Resolve the first subdirectory path (relative to <paramref name="root"/>) that exists and
    /// contains at least one file matching <paramref name="pattern"/>. Returns
    /// <paramref name="root"/> itself if no subdirectory qualifies.
    /// </summary>
    public static string ResolveSubdir(string root, string pattern, params string[] subdirs)
    {
        foreach (var sub in subdirs)
        {
            var dir = Path.Combine(root, sub);
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, pattern).Any())
                return dir;
        }
        return root;
    }
}
